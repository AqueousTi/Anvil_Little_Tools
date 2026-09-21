# Little Tools 跨平台助手与 Linux 套件宿主

Windows 与 Ubuntu 共用的轻量 AI 助手。使用 .NET 10 和 Avalonia 12，不依赖旧 WPF 翻译界面。

- **Windows**：助手作为 `LittleTools.exe` 托管的子进程运行，组件开关、翻译、问答和截图翻译都在同一个托盘菜单中。
- **Linux**：同一份源码直接作为**单进程套件宿主**运行，自带托盘菜单、模块开关、开机自启、全局快捷键和桌面通知，不依赖 Windows 宿主。

## 已实现

- `Shift + Backspace` 打开翻译、`Ctrl + Backspace` 打开问答、`Ctrl + Alt + X` 截图翻译、`Ctrl + Alt + Q` 显示或隐藏股票观察；单实例命令 `--toggle` 可供 Linux 桌面快捷键调用（再次触发会隐藏窗口）。
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
每日待办 ☑                    股票观察 ☑
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
- Linux 的托盘菜单通过 DBus 导出给面板，而 Avalonia 的 DBus 菜单实现只转发点击、**不会**代填 `NativeMenuItem.IsChecked`（Win32/macOS 后端会），所以开关的勾选状态由程序自己在点击处理里翻转；否则每次点击读到的都是上一次写入的值，模块只能开、不能关。翻转会让 Avalonia 的导出器发出整份 `LayoutUpdated`，宿主据此重建菜单项，因此重新打开菜单时勾号是正确的。
- **已知限制：点开关后菜单会自动关闭，应用侧无法干预**（用户实机反馈过）。本机（GNOME Shell 46 + `ubuntu-appindicators@ubuntu.com`，:1）实测：
  - 点**灰色不可用**的「余量监控」→ 菜单保持打开（没有触发激活）；
  - 点任一**可用**项（含「开机自启」这种只写文件 + 发通知、不涉及任何窗口的操作）→ 菜单关闭；
  - 用 `dbus-monitor` 监视 `com.canonical.dbusmenu`：整次点击过程中**应用没有发出任何信号**（既无 `LayoutUpdated` 也无 `ItemsPropertiesUpdated`），菜单照样关闭。

  依据是宿主实现：`dbusMenu.js` 的 `MenuItemFactory.createItem()` 对普通项一律创建 `PopupMenu.PopupMenuItem`（勾号由 `_updateOrnament()` + `setOrnament(CHECK)` 绘制），扩展里**没有**使用 `PopupSwitchMenuItem`；gnome-shell 中只有 switch 类型项在激活后不关闭菜单，而 `_onActivate()` 只把 `clicked` 转给应用，关闭由 shell 的弹出菜单在项被激活时完成。Avalonia 也没有可用的钩子（`NativeMenu.Opening` / `Closed` 在 Linux 的 DBus 导出器里从不触发，`DBusMenuExporter.HandleEvent` 只处理 `clicked`；宿主其实会发 `Event(id,"opened"/"closed")`，Avalonia 丢弃）。作为对照，Windows 的 `ToolStripMenuItem.CheckOnClick` 原生就是保持打开，所以这是宿主差异而非移植缺陷。**要点开关不关菜单只能自绘弹窗菜单**（成本较高，属产品决策）。

  复现与验证脚本（gitignored）：`.tools/menu-probe.sh`、`.tools/trayctl.sh`（直接调 `com.canonical.dbusmenu`），截图与数据在 `.tools/out/`。

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

`Ctrl + Alt + X` 在部分发行版会被常驻程序占用（例如本机实测被 QQ 占用）。程序会准确报告冲突的快捷键，可在 `--diagnose` 输出中查看 `hotkeyConflicts`。`Ctrl + Alt + Q` 是股票观察胶囊自己的快捷键（对应 Windows `StockWindow` 里的 `RegisterHotKey`），同样经 `XGrabKey` 注册。

### 命令行

```text
--translate      显示翻译窗口（默认）
--chat           显示问答窗口
--screenshot     进入截图翻译
--todo           打开每日待办并展开到今日
--stock          显示股票观察胶囊（并打开该模块开关）
--toggle         已显示则隐藏，否则显示翻译窗口
--background     仅驻留后台（托盘 + 快捷键），不显示窗口
--exit           退出正在运行的实例
--managed        由外部宿主托管时使用：不创建托盘
--diagnose FILE  写入平台集成状态快照（会话、托盘、快捷键、自启动、路径、外部工具），随后退出
--todo-smoke DIR 渲染每日待办的各界面到 PNG（供无头验证），随后退出
--stock-smoke DIR 用录制的行情样例渲染股票观察各界面与图表到 PNG，随后退出
--stock-live     与 --stock-smoke 连用：额外走一次真实行情联网并渲染，失败即报错
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

## 每日待办

托盘菜单里的「每日待办」开关控制这个模块。打开后右下角出现 316×92 的当前事项胶囊，点一下展开成 430×530 的卡片堆叠界面；关闭开关会隐藏并停掉该模块（数据不丢）。

与 Windows 一致的行为：每日独立列表、完成事项自动沉底并记住原位置（取消完成可回到原位）、拖动排序决定优先级、周期性任务（每天/每周某天/每月某日）、昨日未完成事项可逐项加入今日或堆积或忽略、堆积事项支持多选批量移入今日与删除撤销、0–100 分钟专注倒计时（5 分钟吸附、按墙钟计时、重启后继续、结束时通知）。托盘开关状态与其他模块一起写在 `manager.json`。

数据位置与迁移：

- Linux：`${XDG_DATA_HOME:-~/.local/share}/little-tools/todo/data.json`，另存一份 `data.backup.json`
- 文件格式与 Windows 的 `%LOCALAPPDATA%\LittleTools\DailyTodo\data.json` 完全一致（PascalCase 字段名 + `\/Date(ms)\/` 时间），两边可以互相拷贝
- 首次运行时如果目标文件不存在，会自动从 `~/.local/share/LittleTools/DailyTodo/data.json` 导入一份并弹通知说明；也可以用环境变量 `LITTLETOOLS_TODO_DATA` 指定要导入的文件

```bash
# 从 Windows 机器拷来的数据放到默认位置即可自动导入
cp data.json ~/.local/share/LittleTools/DailyTodo/data.json
```

## 股票观察

托盘菜单里的「股票观察」开关控制这个模块。打开后出现 316×92 的半透明小框，显示当前标的的现价、涨跌幅和参考溢价（普通股票显示滚动 PE）；单击展开 470×650 的明细窗口，再次单击或点击窗口外收回。明细窗口包含自选股标签页、六位代码查询、分时/日K/周K/月K 与 1月/1年/3年/5年切换、自绘图表、估值卡片（滚动 PE 与历史分位）以及加入/移出监控、溢价提醒开关和置顶按钮。`Ctrl + Alt + Q` 随时显示或隐藏。

与 Windows 一致的行为：

- 涨跌配色遵循沪深习惯：上涨红色 `#EF585B`、下跌绿色 `#7CEDAE`、平盘白色；K 线收盘不低于开盘为红，否则灰 `#9CA1AB`；分时与超过 260 根的长序列改画折线 `#EE5C5E`。
- 参考溢价从阈值上方跌至 2% 以下时提醒一次，重新升回阈值以上后才会再次触发；溢价低于 2% 时白字芯加渐变描边，达到 2% 后灰色普通字。
- 510300 用中证指数官方历史 PE，513500（以及名称含“标普500”的 ETF）用 multpl.com 的标普 500 月度 PE，普通股票用东方财富 PE-TTM 历史，其他 ETF 显示“暂未匹配跟踪指数”。
- 行情来源与 Windows 完全相同：腾讯 `qt.gtimg.cn`（GB18030）、东方财富 `push2` / `push2his` / `datacenter-web`、中证指数 `csindex.com.cn`、标普 PE `multpl.com`；腾讯行情里的字段下标（3/4/30/32/61/77/78）也一一对应。
- 设备刷新定时器每 60 秒刷新一次自选股，切换代码或周期立即刷新。
- K 线与分时在东方财富请求失败时会改用腾讯的备用接口，每次 GET 在传输层失败时最多重试两次（400 ms / 1200 ms）；这两点是与 Windows 的**有意差异**，见「有意偏离 Windows 的实现」。

与 Windows 不同的行为：

- **胶囊常驻置顶**，与待办胶囊一致（待办是硬编码置顶）；明细窗口仍然跟随「置顶」按钮与 `settings.json` 里的 `Topmost`（Windows 的默认值是 `false`，`StockMonitor/StockData.cs` L27）。这是有意偏离，见下文。
- 两个股票窗口都不出现在任务栏：`ShowInTaskbar = false` 与 Windows 一致，但 X11 下每次隐藏再显示（`Ctrl + Alt + Q`）都要重新下发一次 `_NET_WM_STATE_SKIP_TASKBAR`，否则 mutter 会把胶囊列进任务栏。

数据位置与迁移：

- Linux：`${XDG_DATA_HOME:-~/.local/share}/little-tools/stock/settings.json`
- 文件格式与 Windows 的 `%LOCALAPPDATA%\LittleTools\StockMonitor\settings.json` 完全一致（PascalCase，含 `JavaScriptSerializer` 写出的裸 `NaN` 字面量），两边可以互相拷贝
- 首次运行时如果目标文件不存在，会自动从 `~/.local/share/LittleTools/StockMonitor/settings.json` 导入一份并弹通知说明；也可以用环境变量 `LITTLETOOLS_STOCK_DATA` 指定要导入的文件

```bash
# 从 Windows 机器拷来的设置放到默认位置即可自动导入
cp settings.json ~/.local/share/LittleTools/StockMonitor/settings.json
```

离线验证使用 `--stock-smoke DIR`：它回放 `LittleTools.Assistant/Stock/Fixtures` 里录制的真实响应（每个数据源一份），渲染胶囊与明细到 PNG，并用像素断言守护图表几何、红涨绿跌配色、溢价描边、**X 轴标签是格式化后的日期/时间**以及空数据占位；加 `--stock-live` 会额外真实联网，连不上就明确报错而不是拿样例冒充成功。

冒烟是**自洽、可重复**的（连续跑结果必须一致，这是它的契约）：

- **状态自持**：每次运行前重建 `DIR/data` 并显式写入它要测试的设置（选中 510300、日K、1 年、两张自选），不调用 `StockStore.Load`，因此**上一次运行留下的 `settings.json`、`LITTLETOOLS_STOCK_DATA` 以及 XDG 下的导入候选都影响不了它**；窗口的写盘只落到这个临时目录。
- **不依赖网络**：失败分支由注入的拒绝处理器触发（对 K 线与分时请求返回 404，东财与腾讯备用源都拒），因此「刷新失败要显示暂无走势数据而不是假数据」这一条与样例是否存在、网络是否可用都无关；实时取数只在 `--stock-live` 里出现。
- **不等固定时间**：每个断言都轮询它真正需要的条件（行情文案、图表 `LastRender` 的蜡烛数、`暂无走势数据` 占位、明细窗口 `IsVisible`、状态行不再是「正在查询…」），带超时与失败时的现场信息；图表刷新会因后台刷新抢版本号而提前返回，所以驱动会重试直到图表真的对得上。
- **产物集合固定**：一次成功的运行固定产出 13 张 PNG（脚本与 CI 可以直接比对数量），并且两次运行的同名渲染**字节一致**。

回归验证脚本（gitignored）：`.tools/verify-stock-smoke.sh polluted|clean [轮数]` —— 故意把状态污染成 `600519/Monthly/5y` 后连跑，用于证明它不再受状态影响。

助手侧 `--layout-smoke` 的「点击别处隐藏窗口」检查也不再依赖固定延时：它会等待焦点真的交出去/收回来（带重试），若桌面始终不把焦点交给它，会明确写出「另一个窗口占着焦点（是否还有另一个 Little Tools 实例在跑）」以及当时的 `IsVisible`/`IsActive`，而不是报成产品回归。

## 有意偏离 Windows 的实现

以下几处与 Windows 源码不一致，都是有意为之，改动范围都尽量小：

1. **K 线与分时的备用数据源 + 传输层重试**（`Stock/StockFallbackSource.cs`）。东方财富的 `push2his` / `push2` 在部分网络（含本机实测的网络）里 TLS 握手正常、请求发出后直接被服务端断开，没有任何 HTTP 响应；Windows 源码在同样的网络里也取不到数据，明细窗口只会显示「暂无走势数据」。Linux 端口**在东方财富请求失败之后**改用腾讯 `web.ifzq.gtimg.cn` 的 `fqkline`（日/周/月，前复权，字段顺序与东方财富一致：日期、开、收、高、低、量）与 `minute`（分时）。东方财富始终是第一顺位；两个源都失败时抛出的仍是东方财富的异常，界面继续走 Windows 的「暂无走势数据」分支，解析失败也不会编造数据。此外每次 GET 在**传输层**失败时最多重试两次（400 ms、1200 ms），带 HTTP 状态码的失败与已取消的请求不重试，避免对正在限流的主机加压。东方财富一旦可用就会自动回到原路径。

2. **托盘开关的勾选状态**。见上文「托盘菜单」：Avalonia 的 Linux DBus 菜单实现不会代填 `IsChecked`，因此由程序在点击时自行翻转。这是让行为**回到** Windows 语义的修正，不是风格偏离。

3. **股票胶囊常驻置顶**（`Stock/StockWindow.cs`）。Windows 的胶囊跟随 `StockSettings.Topmost`，默认 `false`（`StockMonitor/StockData.cs` L27），因此默认会被别的窗口盖住；用户明确要求股票胶囊像待办胶囊一样始终置顶（待办是硬编码 `Topmost = true`，`Todo/TodoWindow.cs` L104）。Linux 端口把胶囊硬编码为置顶，明细窗口仍然跟随「置顶」按钮与 `settings.json` 的 `Topmost`，该字段的默认值与写盘格式不变，Windows 的设置文件照旧互通。

4. **隐藏再显示时重新下发窗口标志**（`Stock/StockWindow.cs:ReapplyWindowFlags`）。mutter 在窗口取消映射时会删除 `_NET_WM_STATE`，而 Avalonia 只在 `Topmost` / `ShowInTaskbar` **发生变化**时才推送给后端，因此 `Ctrl + Alt + Q` 收起再弹出的胶囊会同时丢掉 `_NET_WM_STATE_SKIP_TASKBAR`（任务栏出现 littletools 图标）和 `_NET_WM_STATE_ABOVE`（不再置顶）。每次显示后重新下发一次即可，Windows 没有这个问题。

5. **毛玻璃面板（可读性修正）**。Windows 的胶囊是 ARGB(62/72, 17, 20, 27)（约 24%~28% 不透明），在白底桌面上合成结果是浅灰 `rgb(188,189,191)`，白字对比度只有约 1.7:1，用户实机反馈「浅色背景下文字发虚」。Linux 侧把**所有**面板换成 `GlassSurface`：半透明的深色（**0xB4 ≈ 71%**，首版曾用 0xF0≈94%，用户反馈「完全黑了」，已回调）+ 极轻微垂直渐变 + 原有 1px 发丝边框，白底实测 6.8:1、黑底 17:1 以上。悬浮档按 Windows 的比例推导为 **0xC8**：Windows 常态 62 → 悬浮 112（`StockMonitor/StockWindow.cs` L963-L964，RGB 不变只提 alpha），即悬浮保留常态「透出量」的 `0.561/0.757 = 74.1%`；0xB4 的透出量 0.294 × 0.741 = 0.218 → alpha = 199 ≈ 0xC8（照搬 +50 会得到 0xE6，与不透明无异）。同时每个窗口都请求 `[AcrylicBlur, Blur, Transparent]`：Avalonia 的 X11 后端只在 KWin 下支持 `Blur`（`Avalonia.X11.TransparencyHelper.IsSupported` 判断 `WmName == "KWin"`，且从不支持 `AcrylicBlur`），所以在 GNOME 上自动回落到 `Transparent`，由半透明面板承担观感；**KDE 下的真模糊路径本机无法验证**。

   实测（纯白 / 纯黑背景，胶囊与助手窗口实测面板色与文字对比度；正文白色文字）：

   | 窗口 | 修改前（Windows 24~28%） | 0xB4（现值） | 0xC8（悬浮档） |
   | --- | --- | --- | --- |
   | 股票胶囊 | `rgb(197,198,200)` · 1.69:1 | `rgb(88,91,96)` · **6.76:1** | `rgb(70,72,78)` · 9.06:1 |
   | 待办胶囊 | `rgb(188,189,191)` · 1.88:1 | `rgb(87,90,95)` · **6.92:1** | `rgb(69,71,77)` · 9.29:1 |
   | 助手窗口 | `rgb(184,185,188)` · 1.96:1 | `rgb(88,91,96)` · **6.82:1** | `rgb(70,72,78)` · 9.14:1 |

   纯黑背景下三种档位都在 17:1 以上；相邻档位 `0x9A`（60%，白底 4.8:1）与 `0xC8`（78%，白底 9.1:1）的截图与数据留在 `.tools/out/glass-sweep.txt`、`sweep-*.png`，便于再微调。

   **次要文字偏低的已知项（未改，待决策）**：0xB4 面板上正文达标，但更低不透明度的次要文字在白底上低于 WCAG AA 正文标准——股票胶囊的名称 `white@150` 3.57:1、底部提示 `white@120` 2.87:1、明细窗口 `Secondary()` `ARGB(145,255,255,255)` 3.44:1、待办 `SecondaryText` `ARGB(150,220,224,232)` 2.90:1、`MutedText` `ARGB(165,165,170,180)` 2.08:1（白底、按面板 `rgb(88,91,96)` 解析式计算，与实测探针 4.17:1 / 3.73:1 吻合）。若要把次要文字也拉到 4.5:1，只需提高这些**文字**的 alpha（面板不动）：`white@150 → @185`、`white@120 → @255`、`ARGB(150,220,224,232) → @230`；`MutedText (165,170,180)` 即使全不透明也只有约 3:1，需要同时提亮颜色（例如 `(200,205,215)`）。这是可选方案，尚未应用。

   覆盖范围：待办胶囊（含 hover）、待办展开外壳、待办各对话框与堆积抽屉；股票胶囊与明细窗口；助手窗口 `MainWindow`。助手窗口那一处**同时改了 `MainWindow.axaml`（与 `main` 共用的文件）和 `MainWindow.axaml.cs` 的 `ApplySurfaceColors()`**（它在运行时用 `#4811141B` 覆盖 XAML），两侧都只动背景值，`ApplySurfaceColors` 改为读取 `GlassPanel` / `GlassPanelHover` 资源，方便将来合并上游时核对。对话框/堆积抽屉保持 Windows 自己的 `0xF4~0xF6` 不透明度（本来就近不透明），只加了同一层渐变与发丝边框。

   残余因素（**未改，待决策**）：Avalonia 的 Skia 文本默认走**子像素抗锯齿**（`Avalonia.Skia.GlyphRunImpl`：`TextRenderingMode.Unspecified` → `SubpixelAntialias`），在合成窗口里会在字形边缘留下彩色条纹（实测边缘通道差最大 136/255，放大可见蓝/琥珀色描边）；设置 `TextOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias)` 后降到 9/255，文字变为中性灰度抗锯齿。这是 Avalonia 全局行为，不是本次改动引入的，暂未应用。

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

- 每日待办与股票观察已原生提供。AI 余量监控尚未在 Linux 提供，托盘菜单中对应项置灰。
- 股票观察的明细界面在 Windows 里是同一个 `StockWindow` 的控件字段；Linux 端口把它拆成独立的 `StockDetailsWindow`（两边本来就是两个顶层窗口），行为一致但控件引用各自持有。
- 每日待办的贴边自动收起在 X11 生效；Wayland 无法自定位窗口，该增强会自动不生效，窗口仍可正常使用。
- 专注倒计时提示音使用桌面声音主题（`canberra-gtk-play`）；没有可用播放器时静默降级，不影响计时。
- Linux 区域截图依赖桌面提供的截图程序；正式发布前需要在目标 Ubuntu 的 Wayland 会话实测。
- Wayland 不支持程序自定位窗口，因此贴边隐藏等桌面增强需要单独的“可用则启用”实现，尚未包含在本阶段。
- GLM-5.3-Flash 与 DeepSeek V4.1 Flash 是较新的模型，服务端请求字段需以实际 API 账户返回为准；界面会显示完整 HTTP 错误便于适配。
- 当前 Windows 托盘宿主已经直接启动本助手；旧 WPF 百度翻译不再注册快捷键。
