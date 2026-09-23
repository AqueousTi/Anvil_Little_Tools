# CrossPlatform/tools —— 移植验证脚本

这里放的是**「怎么证明这个 Windows→Linux 移植是对的」**的那批脚本：在隔离的 XDG/TMPDIR 里起真实窗口、用 `xprop`/`xwininfo` 查窗口标志、XTest 合成点击、读光标图像、对截图做像素比对。它们原先散落在 gitignore 的 `.tools/` 里，随时可能随会话丢失；现在纳入仓库，验证方法可复现、可被 review。

> 只搬了**脚本与源码**（`*.sh`、`*.c`、`*.py`）。SDK、NuGet 缓存、隔离的 XDG、渲染产物、编译出来的探针二进制**仍然全部落在 `.tools/`**（那里继续 gitignore），一个二进制都没进仓库。

## `.tools/` 与这个目录的关系

| 位置 | 内容 | 是否入库 |
| --- | --- | --- |
| `CrossPlatform/tools/` | 本目录：`*.sh`、`*.c`、`*.py` 脚本与源码 | ✅ 入库 |
| `.tools/dotnet` | .NET SDK（`DOTNET_ROOT`） | ❌ gitignore |
| `.tools/home`、`.tools/nuget` | `DOTNET_CLI_HOME`、`NUGET_PACKAGES` | ❌ gitignore |
| `.tools/xdg/{config,data,cache}` | 隔离的 XDG 根，跑工作区构建时用 | ❌ gitignore |
| `.tools/tmp/*`、`.tools/stkverify/*` | 隔离的 `TMPDIR`（CoreFX 命名管道/命名互斥体都在 `TMPDIR` 下，靠它避免和已安装实例抢单例） | ❌ gitignore |
| `.tools/out/*` | 截图、日志、对比度数据等渲染产物 | ❌ gitignore |
| `.tools/x11tool`、`.tools/cursorprobe`、`.tools/xraisetool`、`.tools/backdrop` | 由本目录 `*.c` 编译出的探针二进制 | ❌ gitignore |
| `.tools/dotnet-install.sh` | 上游官方 .NET 安装器（63K），**刻意不入库** | ❌ gitignore |

### 准备 `.tools/`

```bash
# 1) .NET 10 SDK 放到 .tools/dotnet，可执行文件为 .tools/dotnet/dotnet
mkdir -p .tools/dotnet
#    官方安装器脚本未入库，来源是 https://dot.net/v1/dotnet-install.sh
#    需要时可重新下载：
curl -fsSL https://dot.net/v1/dotnet-install.sh -o .tools/dotnet-install.sh
bash .tools/dotnet-install.sh --channel 10.0 --install-dir .tools/dotnet

# 2) 用仓库里的 env.sh 把 SDK/NuGet/CLI home 全部指到 .tools/
cd <repo-root>
source CrossPlatform/tools/env.sh

# 3) 编译探针二进制（输出到 .tools/，不要提交）
gcc -O2 -o .tools/x11tool     CrossPlatform/tools/x11tool.c     -lX11 -lXtst
gcc -O2 -o .tools/cursorprobe CrossPlatform/tools/cursorprobe.c -lX11 -lXfixes
gcc -O2 -o .tools/xraisetool  CrossPlatform/tools/xraisetool.c  -lX11
gcc -O2 -o .tools/backdrop    CrossPlatform/tools/backdrop.c    -lX11

# 4) 构建要验证的工作区产物
dotnet build CrossPlatform/LittleTools.CrossPlatform.slnx -c Release
```

大多数脚本自己会 `source CrossPlatform/tools/env.sh` 并把 XDG/TMPDIR 指到 `.tools/`；手动跑时用的就是这一段：

```bash
cd /path/to/Linux_Tools
source CrossPlatform/tools/env.sh
export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config" \
       XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data" \
       XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache" \
       DISPLAY=:1
```

### 运行前提

- **X11 显示**：脚本都打在真实 X 屏幕上（默认 `DISPLAY=:1`），不是 Wayland，也不是无头 Xvfb。`:1` 上最上层窗口会被截图，所以驱动期间别在该屏幕上做别的事。
- **外部命令**：`xwininfo`、`xprop`、`gnome-screenshot`、`gdbus`、`python3` + `Pillow`、`gcc` + `libx11-dev`/`libxtst-dev`/`libxfixes-dev`。
- **托盘菜单类**（`trayctl.sh`、`menu-*.sh`）额外需要 GNOME Shell + AppIndicator 宿主与会话 D-Bus，换桌面环境会直接失效。
- **固定坐标**：菜单类和明细窗口类脚本写死了录制时的屏幕布局（2560×1440、GNOME 顶栏、托盘图标在 `2237,16`、菜单裁剪区 `2100,40,2400,560`、助手上窗口内的偏移等）。换了分辨率/面板布局就得重新标定，不是通用断言。
- **会改源码的扫描脚本**：`glass-sweep.sh`、`alpha-sweep.sh` 会临时改写 `GlassSurface.cs` / `MainWindow.axaml` 并重建，结束再恢复。别和并行的 agent 同时跑。
- **单例管道会撞车**：套件的「第二实例交付」命令管道建在 `$TMPDIR` 下。机器上已经跑着一个套件（例如已安装版）时：
  - `stkverify.sh`、`aictl.sh` 自己把 `TMPDIR` 指到 `.tools/` 下的隔离目录，直接跑就行；
  - `todoctl.sh`、`stockctl.sh`、`capture-ui.sh` **只隔离 XDG、不隔离 `TMPDIR`**，需要自己先 `export TMPDIR="$WORKSPACE_ROOT/.tools/tmp/<name>"` 并 `mkdir -p`。否则新进程会把 `--todo`/`--stock` 转交给已在运行的实例后静默退出，表现为 `todoctl.sh start` 打印 `no window` 且 `.tools/out/todo-app.log` 是空的——不是移植回归。

## 日常通用工具

| 文件 | 作用 | 典型用法 |
| --- | --- | --- |
| `env.sh` | 定义 `WORKSPACE_ROOT`、`DOTNET_ROOT`、`DOTNET_CLI_HOME`、`NUGET_PACKAGES`，关掉 Avalonia/.NET 遥测；其余脚本都 source 它 | `source CrossPlatform/tools/env.sh` |
| `todoctl.sh` | 用工作区 Release 构建起/停每日待办窗口，并给出窗口 id、pid、几何 | `todoctl.sh start` / `stop` / `pid` / `geom` |
| `stockctl.sh` | 起/停带股票模块的套件（默认 fixture 回放，`--live` 走真实行情），给出胶囊/明细窗口 id、几何、map state | `stockctl.sh start [--live]` / `stop` / `capsule` / `details` / `geom <id>` / `state <id>` |
| `aictl.sh` | 在隔离 `TMPDIR`（命令管道 + 命名互斥体）+ 隔离 XDG 下跑一个助手实例，可查窗口 id、`_NET_WM_STATE`、Map State | `aictl.sh start [--chat]` / `send ...` / `id` / `state` / `map` / `stop` |
| `stkverify.sh` | 用隔离 XDG + `TMPDIR` 跑整套（不与已安装实例抢单例管道）；可写 `manager.json`/股票 `settings.json`、列套件窗口 | `stkverify.sh start --background` / `stop` / `pid` / `env` / `manager <json>` / `stock <json>` / `windows` |
| `winlist.sh` | 列出套件所有顶层窗口：id、pid、几何、Map State、`_NET_WM_STATE`、`WM_CLASS` | `winlist.sh [pid]` |
| `shotwin.sh` | 按 pid + 标题子串找到窗口并裁剪截图（第 4 参数 `full` 则存整屏） | `shotwin.sh <pid> <标题子串> <out.png> [full]` |
| `shot-todo.sh` | 截一张 Daily Todo 窗口的图（从整屏按标题几何裁剪） | `shot-todo.sh <out.png>` |
| `capture-ui.sh` | 起助手、延时后截图（`--full` 存整屏） | `capture-ui.sh <out.png> [--delay 秒] [--full] -- <app 参数...>` |
| `trayctl.sh` | 直接走 D-Bus 像面板那样驱动 StatusNotifierItem / `com.canonical.dbusmenu`：查服务名、菜单路径、布局、条目，或按标签点条目 | `trayctl.sh items <pid>` / `click <pid> <标签>` / `service` / `menu` / `layout` |
| `pipe.sh` | 用单例命令管道（`$TMPDIR/CoreFxPipe_LittleTools.Assistant.Command.v1`）给运行中的实例发一条命令 | `pipe.sh .tools/stkverify/tmp ToggleStock` |
| `apps-locator.sh` | 把 GNOME「显示应用程序」网格里的图标反查到磁盘安装位置（.desktop → `Exec=` → dpkg/snap/flatpak）。与移植无关的通用排查工具 | `apps-locator.sh [-v] [关键字]` |
| `fix-steam.sh` | 修 Ubuntu 24.04 上 Steam 起不来（AppArmor 限 user namespace + NVIDIA 内核模块没编出来）。环境修复脚本，与移植无关 | `sudo bash fix-steam.sh` |

## 一次性 bug 复现 harness

这些是当时为了**证明某个具体 bug 已修 / 定位某个宿主差异**写的，留着是为了能重跑那条证据链。

| 文件 | 复现/证明什么 | 典型用法 |
| --- | --- | --- |
| `ai-flags-test.sh` | 助手窗口反复「显示 → 点别处隐藏 → 再显示」（翻译与问答两个入口）后，仍保持 `_NET_WM_STATE_SKIP_TASKBAR` | `ai-flags-test.sh translate 3` |
| `menu-probe.sh` | 真实 GNOME 托盘菜单在纯黑背景上打开、点某一行，报告菜单点前/点后的状态 | `menu-probe.sh <行 y> <标签>` |
| `menu-test.sh` | 真实托盘菜单里点开关项后菜单是否**仍然打开**（宿主差异：开关项不关菜单只能自绘菜单） | `menu-test.sh <标签>` |
| `menu-verify.sh` | 按行号（0 起：翻译/问答/截图翻译/余量监控/AI翻译与快问/每日待办/股票观察/开机自启/打开工具目录/退出）点条目，报告菜单是否还开着 | `menu-verify.sh <行号> <标签>` |
| `menu-item-test.sh` | 打开菜单、按 Y 坐标点一项，报告菜单是否还开着 | `menu-item-test.sh <标签> <itemY>` |
| `menu-diff.py` | 数菜单区域两张截图的不同像素，用来判断「开/关」而不是靠人眼看 | `menu-diff.py <a.png> <b.png>` |
| `race-ui-test.sh` | 真实明细窗口切标的 + 连点，证明 K 线最终一定属于当前选择（**哈希 y 轴标签列**判定，而不是抢时机截图；刷新期间窗口仍显示上一只票的图） | 先 `stkverify.sh start --stock`，再 `race-ui-test.sh` |
| `race-signature.py` | 哈希 y 轴标签列，作为「现在画的是哪只票」的指纹 | `race-signature.py <shot.png> <winX> <winY>` |
| `chart-pixels.py` | 数 K 线蜡烛/线颜色的像素，区分「图画出来了」与「空图」 | `chart-pixels.py <png> <x> <y> <w> <h> [标签]` |
| `cache-ui-test.sh` | 缓存 K 线的真实 UI 行为：首访联网慢、二次走缓存（计时到 ms）、重新查询时不清空、关窗重开仍有图 | 先 `stkverify.sh start --stock`，再 `cache-ui-test.sh` |
| `verify-stock-smoke.sh` | 反复跑 `--stock-smoke`，并**故意污染状态**（`600519/Monthly/5y`，含遗留 `settings.json` 导入通道），证明结果不受残留状态影响 | `verify-stock-smoke.sh polluted 3` / `clean 3` |
| `measure-capsule.sh` | 白/黑背景各截一次股票胶囊，打印面板底色、最亮字形、WCAG 对比度 | `measure-capsule.sh <标签>` |
| `measure-windows.sh` | 白背景下截待办胶囊与助手窗口并打印面板指标（毛玻璃改版前后对比） | `measure-windows.sh <标签> <pid> [white或black]` |
| `glass-sweep.sh` | 每个 `ShellAlpha:HoverAlpha` 组合重建一次，在纯白/纯黑背景上量胶囊、待办、助手三处面板的对比度（结束恢复 `0xB4`/`0xC8`） | `glass-sweep.sh 9A:B4 B4:C8 C8:D6` |
| `alpha-sweep.sh` | 每个 `ShellAlpha` 重建一次并测胶囊白/黑背景对比度，给报告里的可读性扫描出真实数字（结束恢复 `0xF0`） | `alpha-sweep.sh 3E B4 D8 F0` |
| `panel-metrics.py` | 量一块面板：底色中位数、最亮字形像素、两者 WCAG 对比度、字形边缘是次像素渲染还是灰度 AA | `panel-metrics.py <png> <x> <y> <w> <h> [标签]` |
| `preview-demo.sh` | 截图翻译「点击放大」预览的**真实窗口**驱动（`--preview-hold` 本地种图，不需要百度账号或网络）：记录窗口状态、`cursorprobe` 光标读数、xwd 截图 | `preview-demo.sh [证据目录]` |
| `xwd2png.py` | 把 `xwd -root` 的 XWD 转成 PNG：只用 `xwd` 读帧缓冲，不夺焦点（`gnome-screenshot` 的抓取会让「失焦即关闭」的对话框提前关掉） | `xwd2png.py in.xwd out.png` |
| `credcase.sh` | 在受控启动环境（`real` 全 PATH+会话总线 / `nodbus` 去掉总线 / `nosectool` 无 `secret-tool` / `minimal` `env -i` 式）下开真实设置对话框，比较百度凭据解析路径；XDG 指到 `<outdir>`，但 `$HOME` 保持真实以便命中真实 GNOME keyring | `credcase.sh real /tmp/cred-real` |

## 探针 C 源码

编译产物放 `.tools/`（gitignore），四项都依赖 `libx11-dev`：

| 文件 | 作用 | 典型用法 |
| --- | --- | --- |
| `x11tool.c` | XTest 合成键鼠：`grab-check` 查热键 grab 是否被别的客户端占了；`send`/`click`/`drag`/`move`/`release` 驱动机器 | `.tools/x11tool grab-check`、`.tools/x11tool click 2237 16` |
| `cursorprobe.c` | 读当前 X 光标图像（名字、尺寸、hotspot），带任意参数时再打印一份 ASCII 位图 | `.tools/cursorprobe ascii` |
| `xraisetool.c` | 按窗口 id 提升并激活窗口（`XRaiseWindow` + EWMH `_NET_ACTIVE_WINDOW`，等价于点任务栏） | `.tools/xraisetool 0x...` |
| `backdrop.c` | 映射一张纯色全屏窗口当已知背景板，便于测量半透明面板；打印 `backdrop <winid>` 后一直活着直到被杀 | `.tools/backdrop black 2560 1440` |

## 约定与注意

- **二进制不进仓库**：`x11tool`/`cursorprobe`/`xraisetool`/`backdrop` 一律编译到 `.tools/`；脚本里引用它们时写的也是 `$WORKSPACE_ROOT/.tools/<name>`。
- **脚本引用脚本**用 `$WORKSPACE_ROOT/CrossPlatform/tools/<name>`（或 `$ROOT/...`），路径基于脚本自身位置推导，不依赖当前工作目录。
- **写死绝对路径/home**：脚本只用 `$WORKSPACE_ROOT`、`$HOME`，没有硬编码的个人家目录；菜单/明细窗口的**像素坐标**是另一回事（见上「固定坐标」）。
- `env.sh` 与各个脚本的 `WORKSPACE_ROOT`/`ROOT` 推导都是「脚本所在目录向上两级 = 仓库根」。这个目录再被移动的话，这几行要跟着改。

## 迁移后的验证记录

脚本从 `.tools/` 搬到这里之后跑过一遍（分支 `linux-port`，`DISPLAY=:1`，隔离 XDG + 隔离 `TMPDIR`）：

| 验证 | 命令 | 结果 |
| --- | --- | --- |
| 方案构建 | `dotnet build CrossPlatform/LittleTools.CrossPlatform.slnx -c Release` | Build succeeded，0 Warning / 0 Error |
| 待办渲染冒烟 | `.tools/dotnet/dotnet …/LittleTools.Assistant.dll --todo-smoke .tools/out/todo-smoke-check` | exit=0，23 张 PNG，无 `todo-smoke-check.error.txt` |
| 真实窗口 + 截图 | `todoctl.sh start` → `shot-todo.sh .tools/out/todo-shot-check.png` → `todoctl.sh stop` | `window=0x3200017 pid=69979`，geom 316×92+2226+1330，产出 430×530 PNG |
| 探针编译 | 见上文四条 `gcc` | 四条全部零报错，二进制落在 `.tools/` |
