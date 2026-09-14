# Little Tools Manager

Windows 统一托盘入口。余量监控、每日待办和股票观察仍由 `LittleTools.exe` 承载；“AI 翻译与快问”会启动跨平台 `LittleTools.Assistant.exe` 伴随进程。

右键托盘图标可以：

- 勾选或取消“余量监控”。
- 勾选或取消“AI 翻译与快问”。
- 勾选或取消“每日待办”。
- 勾选或取消“股票观察”。
- 在“贴边自动收起”子菜单中让全部组件或指定组件遵循左右屏幕边缘收起行为。
- 设置统一开机自启。
- 打开工具目录或退出整套工具。

双击托盘图标可快速切换余量监控模块。

模块选择保存在 `%LOCALAPPDATA%\LittleTools\manager.json`。

## 图标

托盘图标采用透明背景、纯白单色的 Windows 原生简笔画风格。源文件和预览位于 `assets` 目录，构建时会自动将 `little-tools.ico` 嵌入程序。
