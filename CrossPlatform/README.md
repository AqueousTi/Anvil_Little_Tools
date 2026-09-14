# Little Tools AI Assistant

Windows 与 Ubuntu 共用的轻量 AI 助手。新模块使用 .NET 10 和 Avalonia 12，不依赖旧 WPF 翻译界面。

## 已实现

- `Shift + Backspace` 在 Windows 全局唤醒；单实例命令 `--toggle` 可供 Linux 桌面快捷键调用。
- 翻译、快问快答、截图翻译三种模式。
- GLM-5.3-Flash 与 DeepSeek V4.1 Flash 切换。
- 快速/深入模式和联网自动/开启/关闭。
- SSE 流式回答、代码块复制、来源链接、会话历史。
- Windows 区域框选；Linux 支持 `gnome-screenshot`、`spectacle` 或 `grim + slurp`。
- Windows API Key 使用当前用户 DPAPI 加密；Linux 使用 GNOME Keyring（`secret-tool`）或环境变量。
- 截图仅驻留内存，不保存到历史或磁盘（Linux 临时截图在读取后删除）。

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
