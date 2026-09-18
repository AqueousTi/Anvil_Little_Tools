# Little Tools

包含 AI 余量监控、AI 翻译与快问、每日待办和股票观察。Windows 使用统一启动入口和一个托盘菜单，菜单可直接打开翻译、问答、截图翻译，以及启停全部组件。

一组轻量、透明、置顶的桌面小工具。原有四模块运行于 Windows；新的 AI 助手使用同一份源码支持 Windows 与 Ubuntu/Linux。

- 一键构建：`build-all.ps1`
- 双击启动：`Start.cmd`（或运行 `start-all.ps1`）
- 统一后台入口：`LittleTools\bin\LittleTools.exe`
- Ubuntu / Linux 移植交接：`LINUX_PORTING.md`
- 生成不含二进制与本机配置的源码交接包：`packaging\build-source-package.ps1`
- Windows / Ubuntu 共用 AI 助手：`CrossPlatform\README.md`

Windows 下直接打开 `LittleTools.Assistant.exe` 也会启动同套程序中的统一宿主，重复打开只唤醒已有实例。关闭助手窗口会收回后台；托盘中的“退出 Little Tools”关闭整套工具。各组件沿用已有开关配置，可在托盘重新开启。

`build-all.ps1` 会同时构建宿主、其他组件和最新 Windows AI 助手。Windows 的 Portable 包和 Assistant 包均包含统一宿主；Linux 包目前仍只包含跨平台 AI 助手。

## AI Usage Monitor

监控 Codex 剩余额度、DeepSeek 余额与消费趋势，以及国内 GLM 的账户余额和 Coding Plan 5 小时/周/MCP 月余量；供应商与密钥来源可在运行时设置。

- Codex 额度使用上次成功缓存；只有检测到 Codex Desktop 已运行时才短暂查询，Codex 未运行时不会启动它或创建查询子进程。
- 说明：`AIUsageMonitor\README.md`

## AI Assistant

文本翻译和截图翻译使用百度翻译开放平台，自动兼容旧版 APPID 与密钥；问答使用 GLM 5.3 Flash 或 DeepSeek Flash，支持联网查询。`Shift + Backspace` 打开翻译，`Ctrl + Backspace` 打开问答，`Ctrl + Alt + X` 截图翻译。翻译窗口左下角提供语言选择和截图按钮。

- 说明：`CrossPlatform\README.md`

## Daily Todo

按日期管理待办事项，支持为事项拆分可勾选的子事项，也支持将昨日未完成事项加入今日、堆积或忽略、拖动排序、收起态快速完成、专注倒计时、全局堆积事项界面，以及每天、每周、每月自动生成的周期性任务。

## Stock Monitor

查询沪深股票与 ETF，支持长期监控、IOPV 与参考溢价、可切换 K 线、PE 历史分位及低溢价提醒。全局快捷键为 `Ctrl + Alt + Q`。

- 说明：`StockMonitor\README.md`
