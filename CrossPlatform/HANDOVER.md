# Little Tools Linux 移植：交接文档（HANDOVER）

给**下一位接手的人**，也给**下一次会话**。目标：只读这一份就能继续干活，不必重新考古。

先读什么：

| 想了解 | 读 |
| --- | --- |
| 怎么安装/升级/用/常见问题（面向使用者） | `CrossPlatform/docs/USER-GUIDE.md` |
| 技术实现、每个模块的细节、**与 Windows 的全部有意差异** | `CrossPlatform/README.md` |
| 移植验证脚本怎么用、每个 harness 证明什么 | `CrossPlatform/tools/README.md` |
| 最初的移植计划、源码审计、可复用/必须替换清单 | 仓库根 `LINUX_PORTING.md` |
| Windows 侧原始实现（只读，不要改） | `TodoNotes/ StockMonitor/ AIUsageMonitor/ TranslateApp/ LittleTools/ Common/` |

---

## 1. 当前状态

### 1.1 分支与提交

- 分支：**`linux-port`**（`main` 的 tip 是 `c195fb8`；`linux-port` = `main` + 移植提交）。**不要动 `main`，不要 push。**
- 领先 `main`：`git log --oneline main..linux-port | wc -l`。写成本文时（2026-10-14）是 **95** 个提交（本文档所在的提交另算）。
- 工作区纪律（每一批都要遵守）：
  - 只 `git add` 你真正改的具体文件，**绝不** `git add -A` / `git commit -a`（仓库里有构建产物、`appsettings.json`、用户数据，误提交代价很大）；
  - **不改 Windows 源码**：`TodoNotes/`、`StockMonitor/`、`AIUsageMonitor/`、`TranslateApp/`、`LittleTools/`、`LittleToolsStartup/`、`Common/`、`packaging/`、`tests/`；
  - 构建 **0 警告**、测试全过才提交；分小批提交，提交信息写清「改了什么、怎么验证的」；
  - 安装/卸载类验证一律用**临时 `HOME`**，绝不碰用户正在运行的 `~/.local/share/little-tools`、`~/.config/little-tools`、`~/.config/autostart/little-tools.desktop`。

### 1.2 模块完成度

四个模块与宿主都已原生完成，不再有「置灰的菜单项」：

| 范围 | 状态 | 主要文件 | 验证入口（离线优先） |
| --- | --- | --- | --- |
| 套件宿主 | ✅ | `SuiteLauncher.cs`、`SingleInstanceCoordinator.cs`、`BuildIdentity.cs`、`Platform/*` | `--diagnose`、`tools/takeover-verify.sh`（25 条）、`tools/trayctl.sh`、`SuiteTests`/`SuitePlatformTests` |
| AI 助手（翻译 / 快问 / 截图翻译） | ✅ | `MainWindow.*`、`ScreenshotPreviewWindow.cs`、`Services/*` | `--layout-smoke`、`--preview-smoke`、`--chat-image-smoke`、`--translation-smoke`、`--render-test`、`CredentialTests` |
| 每日待办 | ✅ | `Todo/*` | `--todo-smoke`（渲染待办各界面到 PNG，2026-10-14 复核为 20 张）、`TodoCoreTests`、`tools/todoctl.sh` |
| 股票观察 | ✅ | `Stock/*` | `--stock-smoke`（13 张 PNG，两次运行字节一致）、`--stock-live`、`StockCoreTests`/`StockDataTests`、`tools/race-ui-test.sh`、`tools/cache-ui-test.sh` |
| AI 余量监控 | ✅ | `Monitor/*` | `--monitor-smoke`（16 张 PNG）、`--monitor-live`、`MonitorCoreTests`/`MonitorCredentialTests`/`MonitorDataTests`、`tools/monitor-verify.sh`（31 条） |
| 发行（阶段五） | ✅ | `build-linux.sh`、`package-linux.sh`、`install-linux.sh`、`uninstall-linux.sh` | `tools/release-verify.sh`（40 条）、`tools/deb-verify.sh`（29 条） |

数据互通：四个模块的持久化文件格式都与 Windows 完全一致（PascalCase、`\/Date(ms)\/`、裸 `NaN`），
首次运行会从 `~/.local/share/LittleTools/...` 自动导入已有 Windows 数据；`manager.json` 的字段名也一致。

### 1.3 「照 Windows 逐行对齐」与「有意偏离」的分界

**逐行对齐**的做法是：读 Windows 源码**原文**（不是摘要），按行号对照行为，再决定 Avalonia 怎么写。
例子：待办的一系列 `Line-by-line pass:` 提交（`a292b45` `d2d1d3a` `1594f6c` `c56b0cf` `cc8fcf2` `4293f81`
`04a11b1` `6de5060` `e50dc45` `77cc67c` `78b11f7` 等）；股票沿用 Windows 的字段下标（3/4/30/32/61/77/78）
与 JP 配色；余量监控的错误文案与 Windows **逐字一致**。

**有意偏离**（共 20 条，全文与理由在 `CrossPlatform/README.md`「有意偏离 Windows 的实现」，
这里只给索引；**不要在这里重复展开**）：

| # | 一句话 | # | 一句话 |
| --- | --- | --- | --- |
| 1 | K 线/分时的腾讯备用源 + 传输层重试 | 11 | 译图点击放大查看（Windows 没有的新功能） |
| 2 | 托盘开关的勾选状态由程序自行翻转 | 12 | Codex 查询门槛改为「找得到 app-server」 |
| 3 | 股票胶囊常驻置顶 | 13 | 手动 Key 存钥匙串/`credentials.json`（不是 DPAPI） |
| 4 | 股票窗口隐藏再显示时重新下发窗口标志 | 14 | 余量监控小窗排版适配（右对齐、对话框下限） |
| 5 | 毛玻璃面板 0x9C / 悬浮 0xB6（可读性） | 15 | 没有移植鼠标穿透 |
| 6 | 走势按 `代码\|周期\|区间` 缓存，刷新/失败不清空 | 16 | 明细窗口「退出」= 关闭该模块 |
| 7 | 估值请求 8 秒预算，状态行随图表更新 | 17 | 贴边收起的两处 X11 修正 |
| 8 | K 线横轴可读（跨年 `yy-MM-dd`、周/月 `yyyy-MM`） | 18 | 余量监控凭据回退链 + `--diagnose` 来源可见 |
| 9 | 助手窗口隐藏再显示时重新下发窗口标志 | 19 | 单实例的版本接管（`BuildIdentity`） |
| 10 | 设置对话框 `ShowInTaskbar="False"` | 20 | 注销/关机时的关闭请求要真正退出 |

不在这 20 条里的 Linux 实现细节（例如截图抓图链的顺序、`SelectionWindow` 自绘选区）属于「同一用户可见行为的不同实现」，
README 里另有专门章节说明，不算偏离。

---

## 2. 怎么构建 / 测试 / 验证

### 2.1 环境

```bash
cd /home/forgever/Projects/Linux_Tools
source CrossPlatform/tools/env.sh        # .tools/env.sh 是兼容软链；把 SDK/NuGet/CLI home 都指到 .tools/
export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config" \
       XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data" \
       XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache" \
       DISPLAY=:1
```

`.tools/` 全部 gitignore：SDK（`.tools/dotnet`）、NuGet 缓存、隔离 XDG、渲染产物、探针二进制。
探针二进制要先编译（见 `tools/README.md`）：

```bash
gcc -O2 -o .tools/x11tool     CrossPlatform/tools/x11tool.c     -lX11 -lXtst
gcc -O2 -o .tools/cursorprobe CrossPlatform/tools/cursorprobe.c -lX11 -lXfixes
gcc -O2 -o .tools/xraisetool  CrossPlatform/tools/xraisetool.c  -lX11
gcc -O2 -o .tools/backdrop    CrossPlatform/tools/backdrop.c    -lX11
gcc -O2 -o .tools/wmdelete    CrossPlatform/tools/wmdelete.c    -lX11
```

### 2.2 构建、测试、发行

```bash
./CrossPlatform/build-linux.sh                 # restore + build + 协议测试
./CrossPlatform/build-linux.sh --publish       # 额外产出 CrossPlatform/artifacts/linux-x64
./CrossPlatform/package-linux.sh               # LittleTools-linux-x64-<版本>-YYYYMMDD.tar.gz（打印 sha256）
./CrossPlatform/package-linux.sh --deb         # 额外 little-tools_<版本>_<架构>.deb
./CrossPlatform/package-linux.sh --version     # 只打印版本号
```

版本号只有一个来源：`CrossPlatform/LittleTools.Assistant/LittleTools.Assistant.csproj` 的 `<Version>`
（当前 `1.0.0`）。打包脚本优先用 `strings` 从**已发布的二进制**里读 SDK 内嵌的 `<版本>+<git 提交>`，
读不到才回退 csproj；它**不会执行**发布物（不带参数运行助手会打开翻译窗口并把命令转交给正在运行的实例，
打包步骤不该在用户桌面上弹窗）。

测试就是一个自定义 harness（不是 xunit），期望输出结尾是 `All protocol tests passed.`：

```bash
dotnet run --project CrossPlatform/LittleTools.Assistant.Tests/LittleTools.Assistant.Tests.csproj -c Release --no-build
```

### 2.3 五个 smoke、`--diagnose`、`--version`

五个渲染/布局冒烟都**不依赖网络**（回放录制样例），都往 `DIR` 写 PNG，失败会写 `DIR.error.txt` 并以非 0 退出：

```bash
DLL=CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll
dotnet $DLL --todo-smoke     .tools/out/todo-smoke       # 20 张 PNG：胶囊/展开/抽屉/对话框/专注态…
dotnet $DLL --stock-smoke    .tools/out/stock-smoke      # 13 张 PNG，且两次运行同名文件字节一致
dotnet $DLL --monitor-smoke  .tools/out/monitor-smoke    # 16 张 PNG：HUD/明细/趋势图/设置
dotnet $DLL --layout-smoke   .tools/out/layout-smoke     # 助手各布局 + 真实焦点的「点别处隐藏/图钉」
dotnet $DLL --preview-smoke  .tools/out/preview-smoke    # 译图点击放大：光标、缩放、平移、关闭、不进任务栏
```

加 `--stock-live` / `--monitor-live` 会各多走一次真实联网；连不上或 Key 被拒会明确报错，不会拿样例冒充成功。
股票 live 的窗口步骤在桌面不空闲时会打印 `WARNING: the detail window kept closing …` 并跳过窗口部分
（数据部分照常校验）——这是环境问题，不是产品失败。

排查桌面集成的第一手段（装完/跑起来任何版本都先跑它）：

```bash
little-tools --version                 # 1.0.0+<git 提交>+<文件指纹>
little-tools --diagnose /tmp/lt.json   # session/tray/hotkeys/hotkeyConflicts/autostart/buildVersion/peerVersion/
                                       # tookOverFrom/configDirectory/dataDirectory/tools/credentials/monitorCredentials
python3 -m json.tool /tmp/lt.json | head -40
```

### 2.4 发行物验收（阶段五新增，可重复）

```bash
CrossPlatform/tools/release-verify.sh --package LittleTools-linux-x64-1.0.0-20261014.tar.gz
CrossPlatform/tools/deb-verify.sh     --package little-tools_1.0.0_amd64.deb
```

两者都只写自己的 `OUTDIR`（默认 `.tools/out/release-verify`、`.tools/out/deb-verify`）与短路径 `/tmp/lt-*-verify-*`：
真实 `HOME` 不参与，真实 dpkg 数据库只被查询。2026-10-14 的结果：**40/40** 与 **29/29**，均 exit 0。
细节见 `tools/README.md`「发行包校验」。

### 2.5 `CrossPlatform/tools/` 的真机 harness 与环境前提

45 个脚本/源码（清单与用途见 `tools/README.md`，`.c` 探针源码也在里面，二进制不入库）。共同前提与坑：

- **X11 `:1` 的真实桌面**，不是 Wayland、不是 Xvfb；驱动期间别在该屏幕上做别的事（最上层窗口会被截图）。
- **隔离 XDG**：`XDG_*` 指到 `.tools/xdg/*`，避免碰到本机安装的配置。
- **隔离 `TMPDIR` 且必须是短路径**：套件的命令管道/命名互斥体在 `$TMPDIR` 下，AF_UNIX 路径上限约 108 字符。
  工作区路径本身就有 51 字符，放进工作区会让**每一条**管道静默失效（症状：`todoctl.sh start` 打印 `no window`
  且日志为空、测试里 IPC 断言全红）。所以用 `/tmp/lt-<name>` 这种短路径（`monitorctl.sh`、
  `takeover-verify.sh`、`release-verify.sh`、`deb-verify.sh` 都已这么做）。
- **`XMODIFIERS=@im=none`**：本工作区的真机验证一直带上它（避免输入法接管按键/输入框焦点）。**未单独复现**不带它时的差异，按约定照带。
- **钥匙串不在 XDG 里**：GNOME Keyring 属于登录会话，隔离 `HOME`/`XDG_*` **不能**隔离它。渲染类冒烟用
  `LITTLETOOLS_DISABLE_KEYRING=1` 关掉真实 Key（冒烟自己在 `finally` 里恢复）；私有 D-Bus 总线上没有
  `org.freedesktop.secrets` 时 `secret-tool` 会**永久阻塞**，`session-logout-verify.sh` 因此必须带上这个开关。
- **托盘/菜单类脚本**（`trayctl.sh`、`menu-*.sh`）需要 GNOME Shell + AppIndicator 宿主与会话总线，换桌面环境直接失效。
- **写死坐标**：菜单/明细窗口类脚本用录制时的屏幕布局（2560×1440、托盘图标在 `2237,16` 等），换分辨率要重新标定。
- **会改源码的扫描脚本**：`glass-sweep.sh`、`alpha-sweep.sh` 会临时改 `GlassSurface.cs` 并重建，
  结束时恢复**运行前的档位**（不要再写死默认值）；别和并行的 agent 同时跑。
- **单例会撞车**：机器上已有套件在跑时，`todoctl.sh`/`stockctl.sh`/`capture-ui.sh` 只隔离 XDG 不隔离 `TMPDIR`，
  需要自己 `export TMPDIR=/tmp/lt-<name>`；`stkverify.sh`、`aictl.sh`、`monitorctl.sh` 已经自己隔离。

---

## 3. 已知问题与未完成

### 3.1 注销/关机（**未证实**，优先级最高）

- 现象（用户真机报告）：点 GNOME 的关机/注销按钮没反应；结束套件进程后恢复正常。
- 已实现的对策：关闭请求分两类——`org.gnome.SessionManager.SessionIsActive == false`（会话正在结束）时
  真正退出，否则只隐藏（Windows 行为）。见 `Platform/GnomeSession.cs`、`Platform/SessionClose.cs`、README 第 20 条。
- **仍未验证的是**：真实注销/关机流程**是否真的**会给非 XSMP 窗口发 `WM_DELETE_WINDOW`。
  gnome-session 46/48 源码里找不到这条路径（只有 XSMP 客户端会被通知），所以有可能根本收不到请求。
  离线证据只到「关闭请求 + 会话状态」这条判定链（`session-logout-verify.sh`，26 条断言，用私有总线上的假
  `org.gnome.SessionManager` 驱动，不需要真的登出）。
- 下次遇到时的现场取证（**先取证再强杀**，强杀会毁掉证据）：

  ```bash
  pgrep -a -f LittleTools
  DISPLAY=:1 xwininfo -root -tree | grep -i little          # 窗口还在不在、是不是未映射
  DISPLAY=:1 xprop -id <窗口id> _NET_WM_STATE WM_PROTOCOLS  # 有没有 WM_DELETE_WINDOW
  journalctl --user -b --no-pager | tail -80
  journalctl -b --no-pager | grep -iE "gnome-session|logind|XSMP" | tail -80
  gdbus monitor --session --dest org.gnome.SessionManager    # 注销期间有没有信号
  little-tools --diagnose /tmp/logout.json                   # session / sessionType
  ```

- 复现/回归：`CrossPlatform/tools/session-logout-verify.sh`（`wmdelete.c` 发关闭请求 + 假会话管理器）。
- 如果确认桌面**根本不发**关闭请求，那么正确的方向是在 `gnome-session` 的会话结束信号上做，
  或改用 XSMP —— 但那会引入更大的行为面，需要先有现场证据再决定。

### 3.2 Wayland（**未实测**）

程序在 Wayland 下会降级：不能抢占全局快捷键（用桌面自定义快捷键绑定 `little-tools --toggle`）、
不能自定位窗口（贴边隐藏不生效）、截图要走 `grim`（**grim 路径没有在真机验证过**）。
`CrossPlatform/README.md` 有 7 步自测清单，第一次上 Wayland 时应逐条走完并记录结果。

### 3.3 截图闪屏

`gnome-screenshot` 每抓一次闪一次（源码写死，无开关）。抓图链已把它排到最后，优先 `maim`/`scrot`/`import`/`grim`/`spectacle`。
本机只装了 `gnome-screenshot` 与 `xwd`（`--diagnose` 的 `tools` 字段可查），所以**本机默认仍会闪**；
`sudo apt install maim` 即可免闪。证据与负对照：`tools/capture-tool-order-verify.sh`（5 条）、
`tools/screenshot-region-verify.sh`（6 条）。

### 3.4 其它未完成/待决策

| 事项 | 状态 | 备注 |
| --- | --- | --- |
| 托盘菜单「点一项就关」 | 宿主行为，**不修** | GNOME AppIndicator 把普通项做成点击即关；要「不关菜单」只能自绘弹窗菜单（产品决策，成本较高）。证据：`tools/menu-test.sh` + `dbus-monitor` 记录，README 托盘菜单节 |
| 次要文字对比度 < 4.5:1 | 待决策 | 0x9C 面板上更低；只改文字 alpha 即可（README 第 5 条列了具体数值），未应用 |
| 子像素抗锯齿彩边 | 待决策 | `TextOptions.SetTextRenderingMode(..., Antialias)` 可消除，属 Avalonia 全局行为，未应用 |
| `Stock/StockWindow.cs` 的 `PointerExited` | 已知同类缺陷，**一行可改** | 与 README 第 17 条里余量监控修掉的那处写法相同（`IsPointerOver` 在事件送达时仍是旧值），股票胶囊「悬停展开后移开」可能不会重新收起 |
| 鼠标穿透 | 未移植 | README 第 15 条：Avalonia 无输入形状 API，且托盘没有按模块的子菜单，宁可不做也不做一个关不掉的开关 |
| GLM 旧余额接口 | 服务端问题 | `paas/v4/balance` 2026-09-23 实测 404，账户报告不可用时余额确实读不到，界面按 Windows 文案显示 |
| `secret-tool` 阻塞主线程 | 既有隐患 | 密钥环服务不可达时会永久阻塞（私有总线上实测）；正常登录会话不发生，`LITTLETOOLS_DISABLE_KEYRING=1` 是绕过开关 |
| `.deb` 的真实 `dpkg -i` | **未在真机执行** | 只在假根（`dpkg --root --force-not-root --force-script-chrootless`）验证；`postinst` 的 `update-desktop-database`/`gtk-update-icon-cache` 在真实系统上的效果、以及会不会与 per-user 安装的桌面项冲突，都还没看过 |
| linux-arm64 | 未做 | 只有 `linux-x64` RID |
| 自动更新 | 未做 | 不是需求；升级靠重跑 `install.sh` 或 `dpkg -i` |

---

## 4. 新会话开场提示词（可直接粘贴）

```text
工作区：/home/forgever/Projects/Linux_Tools（分支 linux-port）。项目：把 Windows WPF 小工具套件移植到 Linux
（.NET 10 + Avalonia 12.1.2，单进程套件宿主）。四个功能模块（助手、每日待办、股票观察、AI 余量监控）与宿主、
以及阶段五的发行包（tar.gz + .deb）与文档都已完成并提交。

开始之前先读（按顺序）：
1) CrossPlatform/HANDOVER.md      —— 当前状态、怎么验证、已知问题、关键决策，先读这一份
2) CrossPlatform/docs/USER-GUIDE.md —— 使用者视角（安装/升级/快捷键/API Key/FAQ）
3) CrossPlatform/README.md        —— 技术细节与「有意偏离 Windows 的实现」1~20 条
4) CrossPlatform/tools/README.md  —— 45 个真机验证脚本的用途与前提
需要对照 Windows 行为时，去读 TodoNotes/ StockMonitor/ AIUsageMonitor/ TranslateApp/ 的源码原文，
不要只读摘要或注释。

环境：
  cd /home/forgever/Projects/Linux_Tools
  source CrossPlatform/tools/env.sh
  export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config" XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data" \
         XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache" DISPLAY=:1
  dotnet build CrossPlatform/LittleTools.CrossPlatform.slnx -c Release     # 必须 0 警告
  dotnet run --project CrossPlatform/LittleTools.Assistant.Tests/LittleTools.Assistant.Tests.csproj -c Release --no-build
                                                                          # 必须 "All protocol tests passed."
五个冒烟（离线，失败写 DIR.error.txt）：--todo-smoke / --stock-smoke / --monitor-smoke / --layout-smoke / --preview-smoke；
桌面集成问题先跑 little-tools --diagnose /tmp/lt.json 和 little-tools --version。

纪律（硬性）：
- 不改 Windows 源码（TodoNotes/ StockMonitor/ AIUsageMonitor/ TranslateApp/ LittleTools/ LittleToolsStartup/
  Common/ packaging/ tests/），不动 main，不 push；只 git add 你改的具体文件，绝不 git add -A / commit -a；
- 每批改完保证构建 0 警告、测试全过，再分小批提交，提交信息写清「改了什么、怎么验证的」；
- 安装/卸载/升级类验证一律用临时 HOME（例如 HOME=/tmp/lt-xxx、TMPDIR=/tmp/lt-xxx-tmp 这种短路径），
  绝对不要动用户正在运行的 ~/.local/share/little-tools、~/.config/little-tools、
  ~/.config/autostart/little-tools.desktop；
- 真机 harness 要用隔离 XDG + 短路径 TMPDIR（AF_UNIX 约 108 字符上限，工作区路径太长会让管道静默失效），
  并带上 XMODIFIERS=@im=none；
- 文档不确定的地方写「未验证/存疑」，不要编造。

优先事项：先复现/取证「GNOME 注销/关机被卡住」（HANDOVER 第 3.1 节，需要现场证据，不要为了测试把用户登出），
然后按 HANDOVER 第 3.4 节的待决策项选择。开始前先用一句话复述你打算怎么验证，再动手。
```

---

## 5. 关键决策记录

| 决策 | 为什么 | 证据 |
| --- | --- | --- |
| 毛玻璃面板用 **0x9C**、悬浮 **0xB6** | Windows 的 24~28% 不透明在白底上白字只有约 1.7:1（用户反馈「文字发虚」）；0xF0 像黑板、0xB4 仍偏实，用户要求再放宽；悬浮档不照搬 Windows 的 +50，而是保留「透出量」的 74.1% 比例，否则 0xE6 与不透明无异 | `5bc3114` → `fd3dcaf`（0xB4）→ `bfc9d6f`（0x9C/0xB6）；`GlassSurface.cs`；README 第 5 条 |
| 隐藏再显示时**重新下发窗口标志** | mutter 在窗口取消映射时删掉 `_NET_WM_STATE`，而 Avalonia 只在属性**变化**时推送 → 每轮「隐藏→显示」都会丢 `SKIP_TASKBAR`（Dock 冒图标）和 `ABOVE`（不再置顶）；同值赋值是 no-op，必须反向绕行 | `38fd9b2`、`fb59b9c`、`fd528ab`（股票/待办）、`0a4119b`、`fb59b9c`；README 第 4/9 条 |
| 单实例要**版本接管** | 原先「有实例就无条件转交命令」，于是一个旧构建只要还活着，新构建每次启动都被它接走（表现为「改了代码完全没生效」），而且旧构建那个 1870×985 的置顶半透明窗口一旦被映射会盖住桌面一大块 | `bf8e641`；`BuildIdentity.cs` + README 第 19 条；`CrossPlatform/tools/takeover-verify.sh`（25 条，含假 peer 与超时退出码 3） |
| 凭据**回退到钥匙串/credentials.json** | Linux 没有 DPAPI；且从 Windows 拷来的 `providers.json` 写的是 `Source = "Environment"`，变量在 Linux 不存在时，明明钥匙串里有同一对 Key，HUD 却显示「未配置 API Key」 | `f8f8758`（不再依赖 `libsecret-tools` 才能保存）、`90e36f0`（回退链）、`9475f1d`（`--diagnose` 报告来源）、`472583c`（升级救援旧 `appsettings.json`）；README 第 13/18 条 |
| 注销时的关闭请求要**真正退出** | 用户报告点关机/注销没反应，杀掉进程即恢复；但用户点窗口 × 仍必须只是隐藏（Windows 行为）——所以只按「收到关闭请求那一刻」读一次 `SessionIsActive`，不能轮询（切换用户时它也会变 false） | `67124fc`、`24ca6eb`、`bf1a95c`；`session-logout-verify.sh`（26 条）；README 第 20 条。**真实注销仍未证实** |
| 抓图链优先**免闪工具** | `gnome-screenshot` 的闪光在源码里写死（Shell 后端 `flash=TRUE`、X11 后端 `CheeseFlash`），没有开关；但不装任何工具也要能用，所以它是最后回退 | `343934d`（自绘选区浮层取代 `gnome-screenshot -a`）、`054941d`（加入 `maim` 与优先级）、`001a1d5`（记录成因）；`capture-tool-order-verify.sh`（5 条） |
| 股票走势**缓存** + 腾讯备用源 | 用户明确要求「切回看过的标的、关掉明细再打开时立刻出图」；东财 `push2his`/`push2` 在部分网络 TLS 之后被服务端直接断开 | `085040f`（缓存）、`3cb028a`（腾讯备用源）、`70abe61`（主源短预算）；README 第 1/6 条；`race-ui-test.sh`、`cache-ui-test.sh` |
| K 线横轴改用**格式化日期** | Windows 只按 `TimeOfDay` 选 `MM-dd`/`HH:mm`，日 K 选 1 年时跨年却不显示年份，用户反馈「像日期往回跳」 | `6a354e5`、`0b2bc8c`；README 第 8 条；`--stock-smoke` 的轴标签断言 |
| 发行版本号**只在 csproj**、打包时从载荷里读 | 打包脚本不能执行发布物：不带参数运行助手会打开翻译窗口并把命令转交给正在运行的实例，等于在用户桌面上弹窗 | 本轮 `c7822f6`、`a7fd208`；`package-linux.sh` |
| 安装用**暂存目录 + 改名替换**，并救援程序目录里的 `appsettings.json` | 升级要安全：`cp -a` 会留下旧版本文件、失败还会留下半坏目录；而程序目录是升级唯一会被替换的位置，历史用户的百度凭据就放在那里 | 本轮 `7578963`；`release-verify.sh`（40 条，含 0600 凭据救援与字节级比对） |
| `.deb` 用**假 dpkg 根**验证 | 本工作区的纪律是不破坏用户正在运行的安装，所以真机 `dpkg -i` 没做，改用 `dpkg --root --force-not-root --force-script-chrootless` 走完整的安装/升级/卸载脚本链 | 本轮 `3474645`；`deb-verify.sh`（29 条） |

---

## 6. 阶段五这一轮改了什么

- `package-linux.sh`：版本化 tar.gz（`LittleTools-linux-x64-<版本>-<日期>.tar.gz` + `VERSION`/`BUILD-INFO.txt`/用户指南）、
  可选 `.deb`、`--version`。
- `install-linux.sh` / `uninstall-linux.sh`：暂存+改名替换、旧凭据救援、`VERSION` 记录/删除、卸载时停掉运行中的实例。
- `Program.cs` / csproj：`--version` 与显式 `<Version>1.0.0</Version>`。
- `CrossPlatform/docs/USER-GUIDE.md`：面向使用者的发行说明（安装/升级/卸载、快捷键、数据位置、API Key 三种方式、FAQ）。
- `CrossPlatform/HANDOVER.md`：本文档。
- `CrossPlatform/README.md`：补 `--version`、`--layout-smoke`、版本化打包与 `.deb`、升级安全说明、两个新文档的链接；
  修正毛玻璃档位（0xB4→0x9C、0xC8→0xB6）的过期描述与对比度表格的标注。
- `CrossPlatform/tools/`：新增 `release-verify.sh`（40 条）与 `deb-verify.sh`（29 条）；
  `glass-sweep.sh`/`alpha-sweep.sh` 改为恢复运行前的档位。
- 根 `README.md`：修正「股票观察与余量监控尚未移植」的过期说法，补两个新文档的链接。
