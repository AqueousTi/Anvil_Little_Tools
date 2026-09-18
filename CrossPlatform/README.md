# Little Tools 跨平台助手与 Linux 套件宿主

Windows 与 Ubuntu 共用的轻量 AI 助手。使用 .NET 10 和 Avalonia 12，不依赖旧 WPF 翻译界面。

- **Windows**：助手作为 `LittleTools.exe` 托管的子进程运行，组件开关、翻译、问答和截图翻译都在同一个托盘菜单中。
- **Linux**：同一份源码直接作为**单进程套件宿主**运行，自带托盘菜单、模块开关、开机自启、全局快捷键和桌面通知，不依赖 Windows 宿主。

## 已实现

- `Shift + Backspace` 打开翻译、`Ctrl + Backspace` 打开问答、`Ctrl + Alt + X` 截图翻译；单实例命令 `--toggle` 可供 Linux 桌面快捷键调用（再次触发会隐藏窗口）。
- 开机自启和 `--background` 只启动后台服务，不显示翻译窗口；Windows 快捷键注册被占用时使用键盘钩子备用处理。
- 翻译、快问快答、截图翻译三种模式。
- 翻译采用固定胶囊输入区，译文从下方以带间隙的圆角卡片展开；快问的时钟与齿轮按钮在右侧堆叠，历史和设置向右展开。点击窗口外或按 Esc 隐藏，截图框选及设置对话框不会触发误隐藏。
- 文本和截图采用百度翻译 API，展示接口返回的全部译文；通用接口不保证返回词典式多释义。
- 问答支持 GLM-5.3-Flash 与 DeepSeek V4.1 Flash 切换。
- 快问输入区的“截图”可框选并预览图片，支持移除、补充问题或直接发送分析。同一窗口内可继续针对图片追问；隐藏或关闭窗口会清理图片内存，历史只保存文字，重新打开后需重新添加图片。
- 快速/深入模式和联网自动/开启/关闭。
- SSE 流式回答、代码块复制、来源链接、会话历史。
- Windows 区域框选；Linux 支持 `gnome-screenshot`、`spectacle` 或 `grim + slurp`。
- Windows API Key 使用当前用户 DPAPI 加密；Linux 使用 GNOME Keyring（`secret-tool`）或环境变量。
- 原始截图不单独落盘；译图临时保存在缓存目录的 `translated-screenshots` 中，隐藏或关闭窗口时删除，不加入问答历史。启动时清理异常退出及旧版残留译图。Linux 捕获临时文件在读取后删除。
- 截图固定翻译为简体中文，代码和命令保留原文；未译出或请求失败会明确标记，不显示为成功完成。

## Linux 套件宿主

### 托盘菜单

菜单结构与 Windows 宿主持平，未移植完成的模块保持可见但置灰，避免让人误以为功能丢失：

```text
翻译 / 问答 / 截图翻译
────────────────────
余量监控（置灰，待移植）      AI 翻译与快问 ☑
每日待办（置灰，待移植）      股票观察（置灰，待移植）
────────────────────
开机自启 ☑
────────────────────
打开工具目录
────────────────────
退出
```

- 模块开关写入 `${XDG_CONFIG_HOME:-~/.config}/little-tools/manager.json`，字段名与 Windows 宿主的 `manager.json` **完全一致**，因此可以直接把 Windows 的配置拷过来用。
- Windows 上助手只读该文件、不写入，避免与 WPF 宿主的开关状态互相覆盖。
- 托盘需要 StatusNotifier 宿主（GNOME 需 `ubuntu-appindicators` 扩展，Ubuntu 默认自带）。托盘创建失败不影响程序启动，窗口仍可通过快捷键、桌面项或 `--toggle` 打开。

### 开机自启

等价于 Windows 的计划任务，使用 XDG autostart，并同样延迟 30 秒启动：

```bash
LittleTools.Assistant --autostart-enable    # 开启
LittleTools.Assistant --autostart-disable   # 关闭
LittleTools.Assistant --autostart-status    # 查询（enabled/disabled，退出码 0/1）
```

对应文件是 `${XDG_CONFIG_HOME:-~/.config}/autostart/little-tools.desktop`，内容是
`/bin/sh -c "sleep 30; exec '<安装路径>/LittleTools.Assistant' --background"`。
在任意桌面环境下都生效；路径包含引号等特殊字符时自动退化为直接 Exec 加 `X-GNOME-Autostart-Delay`。

### 全局快捷键

| 会话 | 行为 |
| --- | --- |
| X11 | 通过 `XGrabKey` 原生注册，与 Windows `RegisterHotKey` 等价，并同时抢占 CapsLock/NumLock 的各种组合 |
| Wayland | 协议不允许客户端抢占全局按键，改为使用桌面自带的自定义快捷键 |

Wayland（或快捷键被占用）时，启动后会发送一条桌面通知说明情况。GNOME 下配置方式：
**设置 → 键盘 → 查看及自定义快捷键 → 自定义快捷键**，命令填 `<安装路径>/bin/little-tools --toggle`。

`Ctrl + Alt + X` 在部分发行版会被常驻程序占用（例如本机实测被 QQ 占用）。程序会准确报告冲突的快捷键，可在 `--diagnose` 输出中查看 `hotkeyConflicts`。

### 命令行

```text
--translate      显示翻译窗口（默认）
--chat           显示问答窗口
--screenshot     进入截图翻译
--toggle         已显示则隐藏，否则显示翻译窗口
--background     仅驻留后台（托盘 + 快捷键），不显示窗口
--exit           退出正在运行的实例
--managed        由外部宿主托管时使用：不创建托盘
--diagnose FILE  写入平台集成状态快照（会话、托盘、快捷键、自启动、路径、外部工具），随后退出
```

`--diagnose` 是排查桌面集成问题的首选手段，输出示例：

```json
{
  "session": "X11",
  "trayCreated": true,
  "hotkeysRegistered": true,
  "hotkeyChords": ["Shift+Backspace", "Ctrl+Backspace"],
  "hotkeyConflicts": ["Ctrl+Alt+X"],
  "autostartEnabled": false,
  "configDirectory": "/home/user/.config/little-tools"
}
```

### 数据与配置路径

| 用途 | Linux | Windows |
| --- | --- | --- |
| 配置 | `${XDG_CONFIG_HOME:-~/.config}/little-tools/` | `%LOCALAPPDATA%\LittleTools\Assistant\` |
| 模块开关 | 同上的 `manager.json` | `%LOCALAPPDATA%\LittleTools\manager.json` |
| 数据 | `${XDG_DATA_HOME:-~/.local/share}/little-tools/` | `%LOCALAPPDATA%\LittleTools\Assistant\` |
| 缓存 | `${XDG_CACHE_HOME:-~/.cache}/little-tools/` | `%LOCALAPPDATA%\LittleTools\Assistant\cache\` |
| 自启动 | `${XDG_CONFIG_HOME:-~/.config}/autostart/little-tools.desktop` | 计划任务 `Little Tools Deferred Start` + HKCU Run |

## 构建

需要 .NET 10 SDK。仓库内本地 SDK 位于 `.tools/dotnet` 时，构建脚本会自动使用它。

```powershell
# Windows
powershell -ExecutionPolicy Bypass -File .\CrossPlatform\build.ps1
powershell -ExecutionPolicy Bypass -File .\CrossPlatform\publish.ps1
powershell -ExecutionPolicy Bypass -File .\CrossPlatform\package.ps1
```

```bash
# Linux
./CrossPlatform/build-linux.sh              # 还原、编译、跑测试
./CrossPlatform/build-linux.sh --publish    # 额外生成 artifacts/linux-x64
./CrossPlatform/package-linux.sh            # 生成 LittleTools-linux-x64-YYYYMMDD.tar.gz
```

## 安装与卸载

```bash
tar -xzf LittleTools-linux-x64-*.tar.gz
cd LittleTools-linux-x64-*
./install.sh --autostart     # --autostart 可选
./uninstall.sh               # 保留用户数据
./uninstall.sh --purge       # 连数据一起删除
```

安装位置：

- 程序：`${XDG_DATA_HOME:-~/.local/share}/little-tools/app`
- 命令：`${XDG_DATA_HOME:-~/.local/share}/little-tools/bin/little-tools`（软链，供快捷键使用）
- 桌面项：`${XDG_DATA_HOME:-~/.local/share}/applications/little-tools.desktop`
- 自启动：`${XDG_CONFIG_HOME:-~/.config}/autostart/little-tools.desktop`

## API Key

在翻译窗口左下角的语言菜单中选择“翻译设置…”，填写百度翻译开放平台的 APPID 和密钥。文本与图片共用这套凭据，但图片翻译必须单独开通。支持旧版 `%LOCALAPPDATA%\LittleTools\TranslateApp\appsettings.json`、仓库 `TranslateApp/appsettings.json` 和 `TRANSLATE_APP_CONFIG` 指定的配置。

也支持 `BAIDU_TRANSLATE_APP_ID`、`BAIDU_TRANSLATE_SECRET_KEY` 环境变量（需成对设置）。新填写的密钥在 Windows 使用 DPAPI 保存，Linux 使用系统密钥环。图片接口为开放平台签名接口，不使用百度智能云的 API Key/Secret Key。

Windows 可以在应用设置中安全保存，也会自动兼容现有 AI Usage Monitor 的 DPAPI 配置。Ubuntu 安装 `libsecret-tools` 后，也可以直接在设置中保存到系统密钥环：

```bash
sudo apt install libsecret-tools
```

也可以使用环境变量：

```bash
export ZHIPUAI_API_KEY='your-key'
export DEEPSEEK_API_KEY='your-key'
```

Linux 桌面快捷方式无法继承 shell 的临时 `export`，因此桌面使用推荐保存到系统密钥环。不要把 Key 写入 `.desktop` 文件。

## Wayland 自测清单

Wayland 下无法由程序设置窗口位置、抢占全局快捷键或做鼠标穿透，这些都按“检测后降级”处理。在 Wayland 会话登录后请依次确认：

1. `./LittleTools.Assistant --diagnose /tmp/lt.json` 中 `session` 为 `Wayland`、`trayCreated` 为 `true`。
2. 托盘图标出现，菜单项与 Windows 一致，可切换“AI 翻译与快问”“开机自启”。
3. 桌面通知能弹出（Wayland 下会提示需要自行配置快捷键）。
4. 在 GNOME 自定义快捷键里绑定 `.../bin/little-tools --toggle`，确认能显示与再次隐藏窗口。
5. 截图翻译可框选：Wayland 下应走 `gnome-screenshot`；若失败，安装 `grim` 与 `slurp` 后重试。
6. 窗口为置顶半透明且无边框；若合成器不支持透明，背景会退化为不透明而不是崩溃。
7. 密钥环：在设置中填写 GLM/DeepSeek Key，确认能用 `secret-tool lookup service little-tools-assistant provider glm` 读回。

## 当前边界

- 本阶段只移植了套件宿主能力（托盘、开关、自启动、快捷键、通知、打包）。每日待办、股票观察和 AI 余量监控尚未在 Linux 提供，托盘菜单中对应项置灰。
- Linux 区域截图依赖桌面提供的截图程序；正式发布前需要在目标 Ubuntu 的 Wayland 会话实测。
- Wayland 不支持程序自定位窗口，因此贴边隐藏等桌面增强需要单独的“可用则启用”实现，尚未包含在本阶段。
- GLM-5.3-Flash 与 DeepSeek V4.1 Flash 是较新的模型，服务端请求字段需以实际 API 账户返回为准；界面会显示完整 HTTP 错误便于适配。
- 当前 Windows 托盘宿主已经直接启动本助手；旧 WPF 百度翻译不再注册快捷键。
