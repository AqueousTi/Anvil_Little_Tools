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

菜单顶部可直接打开翻译、问答和截图翻译；双击托盘图标打开翻译。

推荐从仓库或解压目录的 `Start.cmd` 启动。直接打开本套程序的 `LittleTools.Assistant.exe` 也会转交给宿主，统一启动已开启的组件。重复启动不会增加托盘或助手实例，退出时一起关闭。

宿主支持 `--translate`、`--chat`、`--screenshot`、`--background` 和 `--exit`。开机启动使用 `--background`，不弹出助手窗口。助手的 `--managed` 参数供宿主内部使用。

模块选择保存在 `%LOCALAPPDATA%\LittleTools\manager.json`。

## 图标

托盘图标采用透明背景、纯白单色的 Windows 原生简笔画风格。源文件和预览位于 `assets` 目录，构建时会自动将 `little-tools.ico` 嵌入程序。
