# Little Tools Linux 版使用说明

适用版本 **1.0.0**（`linux-port` 分支）。这份文档面向**使用**：安装、升级、卸载、快捷键、数据位置、
API Key 配置与常见问题。

- 技术实现、模块细节、与 Windows 源码的逐条差异：`CrossPlatform/README.md`
  （发行包内的 `DEVELOPER-README.md`）
- 交给下一位接手者/下一次会话的内容：`CrossPlatform/HANDOVER.md`

> 一句话：这是一个**单进程套件宿主**，把 AI 助手（翻译 / 快问 / 截图翻译）、每日待办、股票观察、
> AI 余量监控放在同一个 Linux 原生程序里，用一个托盘菜单开关各模块，快捷键和开机自启都在程序内实现。

---

## 1. 系统要求

| 项目 | 要求 |
| --- | --- |
| 发行版 | Ubuntu 24.04 实测通过；其它现代桌面发行版预期可用（Wayland 会话**尚未实测**） |
| 运行时 | **不需要**安装 .NET：发布物是 `linux-x64` 自包含单文件 |
| 依赖库 | `libX11`、`libfontconfig`、`libfreetype`、`libdbus`、`libgtk-3`、`libglib` 等（`.deb` 已声明，tar 包依赖桌面环境自带的这些库） |
| 托盘 | 需要 StatusNotifier 宿主；GNOME 下 `ubuntu-appindicators@ubuntu.com` 扩展（Ubuntu 默认自带） |
| 可选工具 | `libsecret-tools`（保存到系统钥匙串）、`notify-send`（桌面通知）、`canberra-gtk-play`（专注倒计时提示音）、`maim`（非 GNOME 桌面上的无闪截图兜底） |

程序启动后始终驻留托盘；关闭助手窗口只是隐藏，托盘里的「退出」才结束整套工具。

## 2. 安装

### 2.1 tar.gz（按用户安装，不需要 root，推荐）

```bash
tar -xzf LittleTools-linux-x64-1.0.0-YYYYMMDD.tar.gz
cd LittleTools-linux-x64-1.0.0-YYYYMMDD
./install.sh              # 安装
./install.sh --autostart  # 安装并开启开机自启（延迟 30 秒）
```

安装位置：

| 内容 | 位置 |
| --- | --- |
| 程序 | `${XDG_DATA_HOME:-~/.local/share}/little-tools/app/` |
| 命令入口 | `${XDG_DATA_HOME:-~/.local/share}/little-tools/bin/little-tools`（软链） |
| 版本号 | `${XDG_DATA_HOME:-~/.local/share}/little-tools/VERSION` |
| 桌面项 | `${XDG_DATA_HOME:-~/.local/share}/applications/little-tools.desktop` |
| 自启动 | `${XDG_CONFIG_HOME:-~/.config}/autostart/little-tools.desktop` |

安装完成后从应用菜单启动，或运行：

```bash
~/.local/share/little-tools/bin/little-tools
```

### 2.2 .deb（系统级安装，需要 root）

```bash
sudo dpkg -i little-tools_1.0.0_amd64.deb
# 缺少依赖时：
sudo apt-get -f install
```

| 内容 | 位置 |
| --- | --- |
| 程序 | `/opt/little-tools/` |
| 命令入口 | `/usr/bin/little-tools`（软链） |
| 桌面项 / 图标 | `/usr/share/applications/little-tools.desktop`、`/usr/share/pixmaps/little-tools.png` |
| 文档 | `/usr/share/doc/little-tools/`（本说明、开发者 README、BUILD-INFO.txt） |

`.deb` **只放程序**，用户数据仍然在各自用户的 `~/.config/little-tools` 与 `~/.local/share/little-tools`。
两种安装方式可以并存（程序路径不同），但请只运行其中一个：跑两个实例会互相抢全局快捷键。

开机自启对不同安装方式都是**按用户**开启：

```bash
little-tools --autostart-enable     # 开启（延迟 30 秒）
little-tools --autostart-disable    # 关闭
little-tools --autostart-status     # 查询
```

## 3. 升级

**tar.gz（推荐路径）**

```bash
tar -xzf LittleTools-linux-x64-1.0.0-新日期.tar.gz
cd LittleTools-linux-x64-1.0.0-新日期
./install.sh --autostart    # 参数与首次安装相同
```

安装脚本在替换前会：

1. 通过程序自己的命令管道让**正在运行的旧实例干净退出**（不使用 `pkill` 之类的模式匹配）；
2. 把程序目录里遗留的 `appsettings.json`（旧版百度凭据）复制到
   `${XDG_CONFIG_HOME:-~/.config}/little-tools/translate/` 并给出提示；
3. 把新文件先复制到旁边的暂存目录，**再整体改名替换**（替换失败会恢复旧版本）。

它**不会**读取、移动或删除 `~/.config/little-tools`、`~/.local/share/little-tools` 中的任何用户数据，
也不会碰系统钥匙串。升级完成后重新启动即可。

**.deb**

```bash
sudo dpkg -i little-tools_1.0.0_amd64.deb
little-tools --exit     # 让升级前就在运行的实例退出
little-tools            # 启动新版本
```

`dpkg` 替换文件时，正在运行的进程仍然跑在旧文件上，所以升级后必须退出并重启才算换到新版本。

**怎么确认现在跑的是哪个版本**

```bash
little-tools --version                 # 例：1.0.0+bf1a95c5...+2227bb774dbb
little-tools --diagnose /tmp/lt.json   # buildVersion / peerVersion / tookOverFrom
```

`--version` 的输出是「版本号 + 构建标识」：既包含编译时的 git 提交号，也包含随包发布的那份文件的指纹，
所以同一个版本号下改了代码重编也能区分。界面里的 `--diagnose` 还能看到接管记录。

## 4. 模块与快捷键

| 功能 | 快捷键 | 说明 |
| --- | --- | --- |
| 文本翻译 | `Shift + Backspace` | 固定胶囊输入区，译文以卡片展开 |
| 快问快答 | `Ctrl + Backspace` | GLM-5.3-Flash / DeepSeek，支持联网与历史 |
| 截图翻译 | `Ctrl + Alt + X` | 自绘选区浮层 + 外部抓图工具，译文固定为简体中文 |
| 股票观察 | `Ctrl + Alt + Q` | 显示 / 隐藏股票胶囊（也可用托盘开关） |
| 每日待办 | 托盘「每日待办」开关 | 316×92 胶囊，单击展开 |
| AI 余量监控 | 托盘「余量监控」开关 | 316×92 HUD，单击展开明细 |
| 显示 / 隐藏助手窗口 | `little-tools --toggle` | Wayland 下用桌面自定义快捷键绑定它 |

界面约定：

- `Esc` 隐藏当前窗口；点击窗口外也会隐藏；助手窗口的**图钉**按钮可让它在失焦后继续显示。
- 截图翻译的结果图可以**点击放大**：手形光标 + 独立预览窗口，滚轮 / `＋` `－` 缩放，放大后拖动为平移，
  未放大时拖动为移动窗口，`Esc`、`×`、失焦都关闭。这是 Linux 侧新增的功能（Windows 没有）。
- 托盘菜单在 GNOME 上是「点一项就关」（宿主行为，见常见问题）。

常用命令行（完整列表见 `CrossPlatform/README.md` 的「命令行」一节）：

```text
--translate / --chat / --screenshot / --todo / --stock / --monitor
--toggle        已显示则隐藏，否则显示翻译窗口
--background    只驻留托盘与快捷键，不显示窗口
--exit          让正在运行的实例退出
--version       打印版本与构建标识
--diagnose FILE 写一份平台集成状态快照（会话、托盘、快捷键、自启动、路径、外部工具、凭据来源、抓图后端 `screenshot`）
--todo-smoke DIR / --stock-smoke DIR / --monitor-smoke DIR / --layout-smoke DIR / --preview-smoke DIR
                渲染 / 断言类冒烟（开发与验收用，见开发者 README）
```

## 5. 数据与配置位置

| 用途 | 位置 |
| --- | --- |
| 配置（助手设置、模块开关 `manager.json`） | `${XDG_CONFIG_HOME:-~/.config}/little-tools/` |
| 凭据文件（钥匙串不可用时） | 同上 `translate/credentials.json`（0600） |
| 数据（待办、股票、余量监控） | `${XDG_DATA_HOME:-~/.local/share}/little-tools/` |
| 缓存（译图临时文件等） | `${XDG_CACHE_HOME:-~/.cache}/little-tools/` |
| 自启动 | `${XDG_CONFIG_HOME:-~/.config}/autostart/little-tools.desktop` |
| 本机密钥 | GNOME Keyring（`secret-tool`，服务名 `little-tools-assistant`） |

**卸载不会删除**上面这些用户数据（唯一例外是 `uninstall.sh --purge`，见第 7 节）。
每个模块的详细文件清单（`todo/data.json`、`stock/settings.json`、`monitor/providers.json` 等）
以及与 Windows 的互通方式见开发者 README 对应章节。

## 6. API Key 的三种配置方式

解析优先级从高到低（与 Windows 相同，只是把 DPAPI 换成钥匙串）：

1. **环境变量**（最高优先级，需成对设置）

   ```bash
   export BAIDU_TRANSLATE_APP_ID='your-appid'
   export BAIDU_TRANSLATE_SECRET_KEY='your-secret'
   export ZHIPUAI_API_KEY='your-glm-key'      # 也兼容 ZHIPU_API_KEY / GLM_API_KEY / BIGMODEL_API_KEY
   export DEEPSEEK_API_KEY='your-deepseek-key'
   ```

   桌面菜单和开机自启**不继承**交互 shell 里的临时 `export`；要长期生效请写进 `~/.profile`
   （GNOME 会话会读）。**不要把 Key 写进 `.desktop` 文件。**

2. **系统钥匙串**（推荐）。装 `libsecret-tools` 后在设置界面保存即可：

   ```bash
   sudo apt install libsecret-tools
   secret-tool lookup service little-tools-assistant provider glm   # 读回验证
   ```

3. **配置文件** `${XDG_CONFIG_HOME:-~/.config}/little-tools/translate/credentials.json`（0600）。
   没有 `libsecret-tools` 或钥匙串不可用时，设置界面会把 Key 存到这里；重装和升级都不会删除它。

没有图形设置界面时，以上三种方式任选一种即可让翻译 / 快问 / 余量监控读到 Key。
余量监控的手动 Key 也走同一把钥匙串，且当 `providers.json` 声明的来源取不到时会**回退**到钥匙串
（这是 Linux 侧的增强，见开发者 README「有意偏离 Windows 的实现」第 18 条）。

查当前实际生效的来源（**不会**输出任何密钥文本）：

```bash
little-tools --diagnose /tmp/lt.json
python3 -c "import json;d=json.load(open('/tmp/lt.json'));print(d['credentials'],d['monitorCredentials'])"
```

## 7. 卸载

```bash
./uninstall.sh            # 删除程序，保留用户数据与配置
./uninstall.sh --purge    # 连 ~/.config/little-tools 与 ~/.local/share/little-tools 一起删除
```

`.deb` 方式：

```bash
sudo apt remove little-tools     # 保留用户数据
sudo apt purge little-tools      # 同样保留 ~/.config、~/.local 下的用户数据
```

卸载会先让正在运行的实例退出，并删除程序目录、命令软链、桌面项、自启动项和 `VERSION`。
待办数据、股票设置、余量监控历史与凭据文件都留在原处，便于以后重新安装继续用。

## 8. 常见问题

**Q：Wayland 会话下快捷键不生效？**
Wayland 协议不允许客户端抢占全局按键，程序无法注册 `Shift+Backspace` 等热键，启动时会发一条桌面通知。
请在 GNOME「设置 → 键盘 → 查看及自定义快捷键 → 自定义快捷键」里绑定：

```bash
~/.local/share/little-tools/bin/little-tools --toggle     # .deb 安装则是 /usr/bin/little-tools --toggle
```

Wayland 下程序也不能自己给窗口定位，所以贴边隐藏之类的增强不会生效；窗口本身可以正常使用。
**本项目的 Wayland 会话尚未实机验证**，自测清单见开发者 README。

**Q：截图翻译时屏幕会闪一下白。**
**1.0.0 起默认不闪**：在 GNOME 会话里程序会直接走 GNOME Shell 的 `flash=false` 抓图接口（抓图瞬间临时申请一个 Shell 认可的 D-Bus 名字，拿到图立刻释放），所以正常应该看不到闪光。

如果你仍然看到闪光，说明走的是回退路径，按下面顺序排查：

1. **portal 回退**：没有会话总线或 GNOME Shell 服务时，程序会用官方 portal 抓图；`xdg-desktop-portal-gnome`（到 46 为止，main/48 也一样）在内部把 `flash=true` 写死，**这条路径一定会闪**。检查 `little-tools --diagnose FILE` 的 `screenshot.lastBackend`：是 `portal` 就属于这种情况（`order` 会列出应走的顺序）。
2. **工具链回退**：`lastBackend` 是 `gnome-screenshot` 时说明前两条都不可用，它每次抓图都会放全屏白光。装一个静默工具即可，程序会自动优先使用它：

```bash
sudo apt install maim        # 或 scrot / imagemagick 的 import
```

抓图工具的安装情况、portal 服务是否可用、以及上一轮实际用了哪个后端，都可以用 `little-tools --diagnose FILE` 的 `screenshot` 与 `tools` 字段查看。

**Q：GNOME 托盘菜单点一下某项就自动关掉了。**
这是 GNOME AppIndicator 宿主的实现行为（宿主把普通菜单项一律做成点击即关的类型），
不是本移植的缺陷：Windows 的托盘菜单本来会保持打开。开关本身是生效的，重新打开菜单就能看到新的勾选状态。
实测依据与复现脚本写在开发者 README 的「托盘菜单」一节。

**Q：托盘图标不出现？**
需要 StatusNotifier 宿主。GNOME 请确认已启用 `ubuntu-appindicators@ubuntu.com` 扩展；
托盘创建失败不会影响程序启动，窗口仍可用快捷键、桌面项或 `--toggle` 打开。

**Q：`Ctrl + Alt + X` 没反应，或提示快捷键冲突。**
部分常驻程序（例如本机实测的 QQ）会占用这个组合。`--diagnose` 的 `hotkeyConflicts` 会列出被占用的项，
其余快捷键不受影响；可以关闭冲突程序，或改用它自己的入口。

**Q：升级后「还是旧界面 / 我改的代码没生效」。**
套件是单实例：新构建启动时会比较构建标识，并让旧实例退出后接管；如果旧实例 6 秒内不退出，
新进程会**明确报错并以退出码 3 停止**，而不是把命令悄悄交给旧实例。手动处理：

```bash
little-tools --exit
little-tools --version      # 确认版本与构建标识
```

**Q：点了 GNOME 的注销 / 关机按钮没反应（1.0.0 已修复，用户实机确认）。**
现象（旧版本）：启动 Little Tools 后，注销或关机流程被卡住，结束套件进程后恢复正常。
1.0.0 已处理：助手窗口的关闭请求分两类——**用户关闭仍然只是隐藏**（与 Windows 一致），
而**会话正在结束时收到的关闭请求会真正退出进程**（判定依据是关闭请求那一刻
`org.gnome.SessionManager.SessionIsActive` 的读取结果）。**用户已在实机上确认：修复前无法关机，修复后关机正常。**

一点客观说明：GNOME 46/48 的 gnome-session / gnome-shell / mutter 源码里找不到
「注销时向非 XSMP 窗口发送 `WM_DELETE_WINDOW`」的路径，所以**触发机制没有在源码层面确认**，
只能确认实机结果；离线证据只覆盖「关闭请求 + 会话状态」这条判定链
（`CrossPlatform/tools/session-logout-verify.sh`，26 条断言，用私有 D-Bus 总线上的假会话管理器驱动，
不需要真的登出）。

如果将来换 GNOME 版本后**再次**遇到，请**在注销前**先收集现场信息，再决定是否强制结束：

```bash
# 1) 关机/注销卡住时，另开一个终端（或 SSH）看进程与窗口状态
pgrep -a -f LittleTools
DISPLAY=:1 xwininfo -root -tree | grep -i little
DISPLAY=:1 xprop -id <窗口id> _NET_WM_STATE WM_PROTOCOLS   # 有没有 WM_DELETE_WINDOW

# 2) 看会话管理器与程序的日志
journalctl --user -b --no-pager | tail -50
journalctl -b --no-pager | grep -iE "gnome-session|systemd-logind|XSMP" | tail -50

# 3) 监视会话总线上的注销相关信号与我们的关闭请求
gdbus monitor --session --dest org.gnome.SessionManager

# 4) 临时处置（不影响数据）
little-tools --exit      # 或按 PID 结束进程
```

`--diagnose` 的 `session` / `sessionType` 可以帮助确认程序自身看到的会话类型。

**Q：没有声音？**
专注倒计时结束提示音使用桌面声音主题（`canberra-gtk-play`）；没有可用播放器时静默降级，不影响计时。

**Q：卸载后配置和数据还在？**
这是故意的：程序与用户数据分离，重装后继续可用。要一并删除请用 `uninstall.sh --purge`。

## 9. 从 Windows 迁移数据

各模块的数据文件格式与 Windows 完全兼容，把 Windows 的数据放到约定位置，首次运行会自动导入并弹通知：

```bash
cp data.json      ~/.local/share/LittleTools/DailyTodo/data.json
cp settings.json  ~/.local/share/LittleTools/StockMonitor/settings.json
cp -r AIUsageMonitor ~/.local/share/LittleTools/
```

也可以用 `LITTLETOOLS_TODO_DATA`、`LITTLETOOLS_STOCK_DATA`、`LITTLETOOLS_MONITOR_DATA`
指定要导入的文件。细节见开发者 README 各模块的「数据位置与迁移」。
