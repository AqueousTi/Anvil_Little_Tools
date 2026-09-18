# Little Tools AI Assistant

Windows 与 Ubuntu 共用的轻量 AI 助手。新模块使用 .NET 10 和 Avalonia 12，不依赖旧 WPF 翻译界面。

Windows 下已与余量监控、每日待办和股票观察统一启动、统一托盘。双击整套工具的 `Start.cmd` 或助手程序即可启动整套工具；组件开关、翻译、问答和截图翻译都在同一个托盘菜单中。Windows ZIP 同时携带宿主，Linux 包仍为独立 AI 助手。

## 已实现

- `Shift + Backspace` 打开翻译、`Ctrl + Backspace` 打开问答、`Ctrl + Alt + X` 截图翻译；单实例命令 `--toggle` 可供 Linux 桌面快捷键调用。
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

## 构建

需要 .NET 10 SDK。仓库内本地 SDK 位于 `.tools/dotnet` 时，Windows 构建脚本会自动使用它。

```powershell
powershell -ExecutionPolicy Bypass -File .\CrossPlatform\build.ps1
```

同时生成 Windows x64 和 Linux x64 自包含目录：

```powershell
powershell -ExecutionPolicy Bypass -File .\CrossPlatform\publish.ps1
```

生成 Windows ZIP 和 Ubuntu/Linux tar.gz 交付包：

```powershell
powershell -ExecutionPolicy Bypass -File .\CrossPlatform\package.ps1
```

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

## Linux 快捷键

Wayland 不允许普通应用任意注册系统级快捷键。安装后在 GNOME “Settings → Keyboard → View and Customize Shortcuts → Custom Shortcuts” 中添加：

```text
名称：Little Tools AI
命令：/安装路径/LittleTools.Assistant --toggle
快捷键：Shift + Backspace
```

## 当前边界

- Linux 区域截图依赖桌面提供的截图程序；正式发布前需要在目标 Ubuntu 的 Wayland 会话实测。
- GLM-5.3-Flash 与 DeepSeek V4.1 Flash 是较新的模型，服务端请求字段需以实际 API 账户返回为准；界面会显示完整 HTTP 错误便于适配。
- 当前 Windows 托盘宿主已经直接启动本助手；旧 WPF 百度翻译不再注册快捷键。
