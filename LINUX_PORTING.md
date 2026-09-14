# Little Tools：Ubuntu / Linux 移植交接说明

## 当前结论与进度

原有四模块界面不能直接编译成原生 Linux 安装包。它们基于 Windows 专用的 .NET Framework 4.7.1、WPF 和 Windows Forms，并直接调用 `user32.dll`、Windows 注册表、DPAPI 与 Windows 计划任务。

新的 AI 助手已经在 `CrossPlatform/` 中按本说明启动移植，使用 .NET 10 与 Avalonia 12，同一份源码可发布为 Windows x64 和 Linux x64。它目前包含翻译、快问快答、GLM/DeepSeek 切换、联网查询、聊天历史及截图翻译。Windows 发布物已完成启动和真实 API 验证；Linux 发布物已生成，但截图和全局快捷键仍须在目标 Ubuntu Wayland/X11 环境实测。待办、股票和用量监控仍使用旧 Windows 实现。

不要尝试只把旧模块的构建脚本改成 `dotnet publish -r linux-x64`；WPF/WinForms 的桌面界面和 Win32 调用仍然无法在 Linux 原生运行。新功能应继续加入 `CrossPlatform/`，并逐步抽离可复用业务逻辑。

如果只想临时使用，可先在 Ubuntu 中用 Wine 运行现有 Windows 便携版，但托盘、全局快捷键、开机启动、透明窗口和 Codex Desktop 检测可能不稳定。这只能作为过渡方案，不是原生 Linux 安装包。

## 推荐移植路线

建议使用现代 .NET（当前 Ubuntu 可用的受支持版本）和 Avalonia 重建跨平台界面，同时把网络请求、数据模型、统计计算与本地持久化从界面代码中拆出。保留 C# 能最大限度复用现有业务代码；一次完成后也能重新生成 Windows、Linux x64 和 Linux ARM64 版本。

建议的新结构：

```text
LittleTools.sln
src/
  LittleTools.Core/            # 数据模型、HTTP 客户端、统计、原子写入
  LittleTools.Desktop/         # Avalonia 桌面宿主、托盘和模块生命周期
  LittleTools.Platform/        # 平台接口
  LittleTools.Platform.Linux/  # XDG、Secret Service、自启动、快捷键实现
tests/
  LittleTools.Core.Tests/
```

第一阶段应优先移植“每日待办”和各模块的数据/网络层，再处理窗口特效、贴边隐藏和全局快捷键。这样能尽早得到可运行版本，也便于给核心逻辑补测试。

## 可复用与必须替换的部分

| 范围 | 现有位置 | Linux 处理建议 |
| --- | --- | --- |
| 原子文件写入 | `Common/AtomicFile.cs` | 基本可复用，但不要依赖 Windows 文件替换语义 |
| 股票数据和 HTTP 请求 | `StockMonitor/StockData.cs` | 大部分可迁移到 Core；为外部接口补超时、取消和测试 |
| AI 供应商请求与统计 | `AIUsageMonitor/Program.cs` | 从 UI 中抽出客户端、DTO、历史采样和统计代码 |
| 百度翻译请求 | `TranslateApp/Program.cs` | 从 UI 中抽出签名、请求和响应解析代码 |
| 待办模型与规则 | `TodoNotes/Program.cs` | 从 WPF 控件构建代码中抽出，保留日期、排序、周期任务逻辑 |
| WPF / WinForms UI | 各模块 `Program.cs`、`StockWindow.cs` | 用 Avalonia 控件、样式和窗口生命周期重写 |
| 托盘图标 | `System.Windows.Forms.NotifyIcon` | 使用跨平台托盘 API；同时保留“无托盘环境”的主窗口入口 |
| 全局快捷键 | `RegisterHotKey` / `user32.dll` | 做成可选平台服务；Wayland 下需桌面门户或桌面环境支持，失败时提供普通快捷入口 |
| 鼠标穿透/窗口样式 | `GetWindowLong`、`SetWindowLong` | 作为平台增强功能；先保证普通置顶窗口可用 |
| 密钥加密 | Windows DPAPI | 优先使用 Secret Service/libsecret；无密钥环时只允许环境变量或明确提示用户 |
| 开机启动 | 注册表、计划任务 | 使用 XDG autostart `.desktop`；必要时提供 `systemd --user` 方案 |
| 本地路径 | `%LOCALAPPDATA%` | 配置用 `$XDG_CONFIG_HOME`，数据用 `$XDG_DATA_HOME`，未设置时回退到用户目录标准路径 |
| Codex 检测 | Windows 进程名与本机路径 | 不启动 Codex Desktop；检测 Linux 进程/CLI，再调用可用的 `codex app-server` 接口 |

## Linux 桌面的现实差异

- Ubuntu 的 GNOME、KDE、X11 和 Wayland 对托盘、透明窗口、鼠标穿透、窗口置顶与全局快捷键的支持不完全相同。
- 应把这些能力设计成“检测后启用”的增强功能，不能因某项不支持而导致整个程序无法启动。
- 至少用 Ubuntu 当前 LTS 的默认 Wayland 会话测试；如需兼容 X11，再增加一次 X11 会话验证。
- 透明毛玻璃和贴边唤醒条应放在功能移植完成之后，避免窗口管理器差异拖慢主体迁移。

## 数据和隐私要求

- 不要把真实 API Key、Codex 登录信息或用户数据提交到源码或安装包。
- 保持现有安全边界：Codex 认证交给官方程序；供应商 Key 只发往各自 API；历史记录只保存余额和统计所需字段。
- 为 Windows 数据迁移提供一次性导入功能，不要直接覆盖 Linux 端已有数据。
- 建议的 Linux 路径：
  - 配置：`${XDG_CONFIG_HOME:-~/.config}/little-tools/`
  - 数据：`${XDG_DATA_HOME:-~/.local/share}/little-tools/`
  - 缓存：`${XDG_CACHE_HOME:-~/.cache}/little-tools/`

## 建议交付顺序

1. 建立新的 SDK 风格解决方案和 Core 单元测试，确保 Windows 源码原样保留用于对照。
2. 抽离 Todo、翻译、股票和 AI 用量的模型、存储、网络与统计逻辑。
3. 建立 Avalonia 单进程宿主，先实现普通窗口和模块开关。
4. 实现 Linux 数据目录、Secret Service、托盘、自启动和快捷键适配器。
5. 补齐贴边隐藏、置顶、透明度和通知等桌面增强功能。
6. 在 Ubuntu Wayland 与 X11 实机验证，再生成 `linux-x64` 自包含发布物。
7. 发布顺序建议先提供 `.tar.gz`，验证稳定后再制作 `.deb`；如需要多发行版分发，可再评估 AppImage 或 Flatpak。

原生程序完成后，可使用类似下面的命令生成发布目录（具体项目名和受支持的 .NET 版本由移植工程决定）：

```bash
dotnet test
dotnet publish src/LittleTools.Desktop/LittleTools.Desktop.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o artifacts/linux-x64
```

## 可直接交给 Ubuntu AI 的提示词

```text
请阅读仓库根目录 README.md 和 LINUX_PORTING.md，并先审计所有源码，不要删除或覆盖现有 Windows 实现。

目标：把 Little Tools 移植成 Ubuntu 可原生运行的单进程桌面应用。使用现代受支持的 .NET 和 Avalonia，建立 SDK 风格解决方案；把业务逻辑抽到 LittleTools.Core，把 Linux 专属能力放到平台适配层。优先完成每日待办、HTTP 数据访问、持久化和普通窗口，再实现托盘、XDG 自启动、Secret Service、通知及可选全局快捷键。Wayland 不支持的能力必须优雅降级。

要求：
1. 不提交任何真实密钥或用户数据。
2. 保留现有数据格式，或提供明确、可测试的一次性迁移。
3. 为待办规则、统计计算、序列化和 API 响应解析增加单元测试。
4. 每完成一个模块就运行测试和 Ubuntu 实机烟雾测试。
5. 最终生成 linux-x64 自包含发布目录、tar.gz，以及安装/卸载说明；功能稳定后再制作 deb。
6. 在文档中列出与 Windows 版本尚有差异的功能，尤其是 Wayland 全局快捷键、托盘和窗口特效。

先给出源码审计和分阶段计划，然后从建立解决方案、抽离 Core 与移植每日待办开始实施。
```

## 本源码包说明

源码交接包保留现有 Windows 构建脚本、测试、文档和图标作为行为参考，排除 `bin/`、`obj/`、`.artifacts/`、历史便携包、临时目录与本机 `appsettings.json`。`TranslateApp/appsettings.example.json` 只是空白示例，可以保留。
