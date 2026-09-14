# Little Tools

包含 AI 余量监控、AI 翻译与快问、每日待办和股票观察。Windows 托盘宿主负责统一启停模块；AI 助手作为跨平台伴随进程运行。

一组轻量、透明、置顶的桌面小工具。原有四模块运行于 Windows；新的 AI 助手使用同一份源码支持 Windows 与 Ubuntu/Linux。

- 一键构建：`build-all.ps1`
- 一键启动：`start-all.ps1`
- 统一后台入口：`LittleTools\bin\LittleTools.exe`
- Ubuntu / Linux 移植交接：`LINUX_PORTING.md`
- 生成不含二进制与本机配置的源码交接包：`packaging\build-source-package.ps1`
- Windows / Ubuntu 共用 AI 助手：`CrossPlatform\README.md`

## AI Usage Monitor

监控 Codex 剩余额度、DeepSeek 余额与消费趋势，以及国内 GLM 的账户余额和 Coding Plan 5 小时/周/MCP 月余量；供应商与密钥来源可在运行时设置。

- Codex 额度使用上次成功缓存；只有检测到 Codex Desktop 已运行时才短暂查询，Codex 未运行时不会启动它或创建查询子进程。
- 说明：`AIUsageMonitor\README.md`

## AI Assistant

使用 GLM 5.3 Flash 或 DeepSeek Flash，提供翻译、快问快答、联网查询和截图翻译，快捷键为 `Shift + Backspace`。旧百度翻译界面已从统一宿主中移除。

- 说明：`CrossPlatform\README.md`

## Daily Todo

按日期管理待办事项，支持将昨日未完成事项加入今日、堆积或忽略，并支持拖动排序、收起态快速完成、专注倒计时、全局堆积事项界面，以及每天、每周、每月自动生成的周期性任务。

## Stock Monitor

查询沪深股票与 ETF，支持长期监控、IOPV 与参考溢价、可切换 K 线、PE 历史分位及低溢价提醒。全局快捷键为 `Ctrl + Alt + Q`。

- 说明：`StockMonitor\README.md`
