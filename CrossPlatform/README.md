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
- 截图翻译的结果图可点击放大查看（手形光标 + 独立预览窗口，滚轮/按钮缩放、拖动平移、Esc/失焦关闭）。Windows 没有这个功能，见「有意偏离 Windows 的实现」第 11 条。
- AI 余量监控：316×92 置顶 HUD 显示 Codex 5 小时/周余量、DeepSeek 余额与今日估算、GLM 钱包余额与 Coding Plan 余量；单击展开 380×505 明细（今日/本周/本月与 DS/GLM 切换的自绘消费趋势图），可配置三个供应商与 API Key，数据文件与 Windows 双向兼容。

## Linux 套件宿主

### 托盘菜单

菜单结构与 Windows 宿主持平；Windows 宿主提供的模块现在都在 Linux 原生可用：

```text
翻译 / 问答 / 截图翻译
────────────────────
余量监控 ☑                    AI 翻译与快问 ☑
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
  - 点已置灰的模块项（当时是「余额监控」，现已全部可用）→ 菜单保持打开（没有触发激活）；
  - 点任一**可用**项（含「开机自启」这种只写文件 + 发通知、不涉及任何窗口的操作）→ 菜单关闭；
  - 用 `dbus-monitor` 监视 `com.canonical.dbusmenu`：整次点击过程中**应用没有发出任何信号**（既无 `LayoutUpdated` 也无 `ItemsPropertiesUpdated`），菜单照样关闭。

  依据是宿主实现：`dbusMenu.js` 的 `MenuItemFactory.createItem()` 对普通项一律创建 `PopupMenu.PopupMenuItem`（勾号由 `_updateOrnament()` + `setOrnament(CHECK)` 绘制），扩展里**没有**使用 `PopupSwitchMenuItem`；gnome-shell 中只有 switch 类型项在激活后不关闭菜单，而 `_onActivate()` 只把 `clicked` 转给应用，关闭由 shell 的弹出菜单在项被激活时完成。Avalonia 也没有可用的钩子（`NativeMenu.Opening` / `Closed` 在 Linux 的 DBus 导出器里从不触发，`DBusMenuExporter.HandleEvent` 只处理 `clicked`；宿主其实会发 `Event(id,"opened"/"closed")`，Avalonia 丢弃）。作为对照，Windows 的 `ToolStripMenuItem.CheckOnClick` 原生就是保持打开，所以这是宿主差异而非移植缺陷。**要点开关不关菜单只能自绘弹窗菜单**（成本较高，属产品决策）。

  复现与验证脚本：`CrossPlatform/tools/menu-probe.sh`、`CrossPlatform/tools/trayctl.sh`（直接调 `com.canonical.dbusmenu`），截图与数据在 `.tools/out/`（见 `CrossPlatform/tools/README.md`）。

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
--monitor        显示 AI 余量监控 HUD（并打开该模块开关）
--toggle         已显示则隐藏，否则显示翻译窗口
--background     仅驻留后台（托盘 + 快捷键），不显示窗口
--exit           退出正在运行的实例
--managed        由外部宿主托管时使用：不创建托盘
--diagnose FILE  写入平台集成状态快照（会话、托盘、快捷键、自启动、路径、外部工具），随后退出
--preview-smoke DIR 用本地生成的译图驱动截图翻译的「点击放大查看」并断言（手形光标、缩放/平移、Esc/×/失焦关闭、不进任务栏），随后退出
--preview-hold   与 --preview-smoke 连用：种好译图后按**真实窗口**保持不退出，供 XTest/光标探针做实机点击验证
--todo-smoke DIR 渲染每日待办的各界面到 PNG（供无头验证），随后退出
--stock-smoke DIR 用录制的行情样例渲染股票观察各界面与图表到 PNG，随后退出
--stock-live     与 --stock-smoke 连用：额外走一次真实行情联网并渲染，失败即报错
--monitor-smoke DIR 用录制的供应商响应渲染余量监控的 HUD/明细/设置到 PNG，随后退出
--monitor-live   与 --monitor-smoke 连用：额外走一次真实 Codex/DeepSeek/GLM 并渲染，失败即报错
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
| 密钥（钥匙串不可用时） | 同上的 `translate/credentials.json`（0600） | 不使用（走 DPAPI，存在 `assistant-settings.json`） |
| 数据 | `${XDG_DATA_HOME:-~/.local/share}/little-tools/` | `%LOCALAPPDATA%\LittleTools\Assistant\` |
| 余量监控数据 | 同上的 `monitor/`（`providers.json`、`snapshot.json`、`usage-history.json`、`glm-usage-history.json`） | `%LOCALAPPDATA%\LittleTools\AIUsageMonitor\` |
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

离线验证使用 `--stock-smoke DIR`：它回放 `LittleTools.Assistant/Stock/Fixtures` 里录制的真实响应（每个数据源一份），渲染胶囊与明细到 PNG，并用像素断言守护图表几何、红涨绿跌配色、溢价描边、**X 轴标签是格式化后的日期/时间且落在自然月界**（见「有意偏离 Windows 的实现」第 8 条）以及空数据占位；加 `--stock-live` 会额外真实联网，连不上就明确报错而不是拿样例冒充成功。

冒烟是**自洽、可重复**的（连续跑结果必须一致，这是它的契约）：

- **状态自持**：每次运行前重建 `DIR/data` 并显式写入它要测试的设置（选中 510300、日K、1 年、两张自选），不调用 `StockStore.Load`，因此**上一次运行留下的 `settings.json`、`LITTLETOOLS_STOCK_DATA` 以及 XDG 下的导入候选都影响不了它**；窗口的写盘只落到这个临时目录。
- **不依赖网络**：失败分支由注入的拒绝处理器触发（对 K 线与分时请求返回 404，东财与腾讯备用源都拒），因此「刷新失败要显示暂无走势数据而不是假数据」这一条与样例是否存在、网络是否可用都无关；实时取数只在 `--stock-live` 里出现。
- **不等固定时间**：每个断言都轮询它真正需要的条件（行情文案、图表 `LastRender` 的蜡烛数、`暂无走势数据` 占位、明细窗口 `IsVisible`、状态行不再是「正在查询…」），带超时与失败时的现场信息；图表刷新会因后台刷新抢版本号而提前返回，所以驱动会重试直到图表真的对得上。
- **产物集合固定**：一次成功的运行固定产出 13 张 PNG（脚本与 CI 可以直接比对数量），并且两次运行的同名渲染**字节一致**。

回归验证脚本：`CrossPlatform/tools/verify-stock-smoke.sh polluted|clean [轮数]` —— 故意把状态污染成 `600519/Monthly/5y` 后连跑，用于证明它不再受状态影响。

`--stock-live` 的**窗口步骤**需要桌面空闲：明细窗口失焦且指针不在其上时会自行关闭，同一桌面有别的窗口抢焦点时它会在刷新中途消失（刷新就画不上）。这种情况下 live 会打印一条 `WARNING: the detail window kept closing …` 并跳过窗口步骤，**数据部分（行情/K线/分时/估值 + 降级诊断）仍然照常校验**，不会伪装成产品失败。真实 UI 的验证请看 `CrossPlatform/tools/race-ui-test.sh`（切换/连点）与 `CrossPlatform/tools/cache-ui-test.sh`（缓存与立即出图）。

助手侧 `--layout-smoke` 的「点击别处隐藏窗口」检查也不再依赖固定延时：它会等待焦点真的交出去/收回来（带重试），若桌面始终不把焦点交给它，会明确写出「另一个窗口占着焦点（是否还有另一个 Little Tools 实例在跑）」以及当时的 `IsVisible`/`IsActive`，而不是报成产品回归。

## AI 余量监控

托盘菜单里的「余量监控」开关控制这个模块（`manager.json` 的 `MonitorEnabled`）。打开后出现 316×92 的置顶 HUD：默认位置是工作区右上角内缩 18 像素，三行分别是 CODEX（5h/周余量）、DEEPSEEK（余额 + 今日估算）和 GLM（余额/今日估算/5h/周/MCP 月余量），底部是最后更新时间；未启用的供应商整行折叠隐藏而不是留占位行。单击展开 380×505 的明细窗口，再次单击或点击别处收回；按住 HUD 或明细标题可整组拖动。

与 Windows 一致的行为（`AIUsageMonitor/Program.cs`）：

- 刷新节奏：进入后 3.5 秒做首次刷新（等桌面稳定），之后每 2 分钟一次；明细窗口的「刷新」按钮立即刷新。
- Codex 通过官方 `codex app-server --stdio` 的 `account/rateLimits/read` 读取额度（初始化握手、15 秒超时、用完即杀子进程），**从不读取或复制 `auth.json`**；未检测到 app-server 时保留上次数据并显示「Codex 未运行 · 显示上次数据」。
- DeepSeek `GET https://api.deepseek.com/user/balance`（Bearer）；GLM 先用裸 Key 请求 `quota/limit`，鉴权失败再重试 `Bearer`，账户报告 `query-customer-account-report` 失败时回退到 `paas/v4/balance`；两者彼此独立，一个失败不影响另一个。
- 错误文案与 Windows 逐字一致（「未配置 API Key」「API Key 无效」「需要 Codex 登录」「连接超时」等）；失败时保留上次数据而不清零。
- 余量百分比按「剩余 = 100 - 已用」换算，≤25% 转琥珀、≤10% 转红；余额 ≤0 转红、<10 转琥珀。
- 今日/本周/本月消费趋势：Codex 之外的 DS/GLM 两条曲线按余额下降量累计，优先使用 GLM 账户报告的累计消费（不受充值影响），并复用区间边界前最后一条历史快照作为基线；首日不足一天时明确标注「监控后」。
- 明细窗口的 DS/GLM 与 日/周/月 切换按钮配色、选中态、坐标轴标签格式（今日 `HH:mm`、周/月 `M/d`）与 Windows 相同。
- 供应商设置对话框（430×470 起）与 Windows 同序：Codex 开关、DeepSeek 开关 + 环境变量/手动 Key、GLM 开关 + 环境变量/手动 Key；「从环境变量读取」勾选时输入框禁用；已保存的 Key 不回显，留空即保持原 Key。
- 持久化文件与 Windows **同格式**（PascalCase、`JavaScriptSerializer` 的 `\/Date(ms)\/` 时间戳、裸 `NaN` 字面量），双向可读：`providers.json`、`snapshot.json`、`usage-history.json`、`glm-usage-history.json`。历史保留 35 天、每分钟最多采样一次。

数据位置与迁移：

- Linux：`${XDG_DATA_HOME:-~/.local/share}/little-tools/monitor/`
- 首次运行时如果目标文件不存在，会自动从 `~/.local/share/LittleTools/AIUsageMonitor/` 导入（Windows 的 `%LOCALAPPDATA%\LittleTools\AIUsageMonitor` 拷贝到该位置即可），并弹通知说明；也可以用 `LITTLETOOLS_MONITOR_DATA` 指向要导入的目录或其中任一文件。
- 手动输入的 API Key 存在套件的平台密钥环（`secret-tool`，服务名 `little-tools-assistant`、`provider` 为 `glm`/`deepseek`），钥匙串不可用时退化为 0600 的 `translate/credentials.json`；这与 Windows 用 DPAPI 写进 `providers.json` 不同，见「有意偏离 Windows 的实现」第 13 条。

```bash
# 从 Windows 机器拷来的 AIUsageMonitor 目录放到默认位置即可自动导入
cp -r AIUsageMonitor ~/.local/share/LittleTools/
```

离线验证使用 `--monitor-smoke DIR`：它回放 `LittleTools.Assistant/Monitor/Fixtures`（录制的真实响应 + 明确标注的手工样例，见该目录 README），渲染 16 张 PNG（三供应商/单供应商/全关/缺 Key/鉴权失败的 HUD，DS 日周月与 GLM 日周趋势、空图、真实无 Coding Plan 状态的明细，以及设置对话框），并断言：每个供应商行高 48/27/20 与 12 像素页脚下限、折叠后的窗口高度 505/215/205、章节折叠位图、逐字文案、图表点数/绘图区尺寸/最大值/末点位置，以及按色相从像素里数出的曲线颜色（DeepSeek 绿、GLM 紫、空图两者皆无）。它状态自持（临时数据目录 + 显式 providers.json + 注入的历史样本，`XDG_DATA_HOME` 重定向、`LITTLETOOLS_MONITOR_DATA` 清空、定时器关闭），失败分支由拒绝响应或清空 Key 触发，不依赖网络；加 `--monitor-live` 会额外走真实 Codex/DeepSeek/GLM 并渲染，连不上或 Key 被拒就明确报错。

真机验证脚本：`CrossPlatform/tools/monitorctl.sh`（隔离 XDG + 短路径隔离 `TMPDIR` 起停模块，`windows`/`values` 查窗口与实时 snapshot）与 `CrossPlatform/tools/monitor-verify.sh`（31 条断言：窗口标志与几何、点击展开、趋势图随 DS/GLM 切换换色、刷新按钮、设置对话框往返、拖动贴边/悬停展开/移开再收起、托盘开关开与关）。`MonitorWindow` 还有一个环境变量开关的诊断输出（`LITTLETOOLS_MONITOR_DEBUG=1` 时把 snap/hide/指针事件写进日志），上面两处贴边缺陷就是靠它定位的。

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

6. **按 (代码, 周期, 区间) 缓存走势，刷新期间与失败时都不清空**（`Stock/StockWindow.cs`）。Windows 在刷新失败时清空图表并显示「暂无走势数据」；用户明确要求相反的行为：

   - 切回看过的标的/周期/区间、或关掉明细再打开时，**立刻画出上次的走势**（明文要求：不要"先空白再查询"）。明细窗口失焦即关闭、重开是**新实例**，所以缓存放在拥有者 `StockWindow` 上，键为 `代码|周期|区间`，最多保留 16 条，绘图是同步、无网络等待的；
   - **刷新期间保留当前画面**（状态行显示「正在查询…」），不做清空；
   - **刷新失败时保留该标的最后一次成功的走势**，并在状态行报告失败原因；
   - 只有**从未取到过数据**（该键无缓存、且本次也失败）才显示「暂无走势数据」占位；
   - 为防止把 A 的数据画到 B 上：缓存写入用的是**请求 token 的键**（不是当前选中项），落地时仍走身份校验；当选中项没有缓存而刷新失败时会清空图表，绝不把上一只股票的图当作当前标的。

7. **估值请求有界，状态行随图表就绪更新**。估值来自另一台主机（`multpl.com` 之类），实测会长时间不响应；它现在有 8 秒预算，而且状态行在**蜡烛落地时**就更新（估值稍后再补），所以不会出现"图已经画好、窗口还写着正在查询…"的情况。

8. **K 线横坐标可读性**（`Stock/StockChartMath.cs` 的 `AxisLabels` / `MiddleLabelIndex`）。Windows 的 `CandleChart.OnRender`（`StockMonitor/StockWindow.cs` L128-L135）只在首/中/尾三根蜡烛上按 `TimeOfDay` 选 `MM-dd` 或 `HH:mm`，中间那根就是"第 N/2 根"，也不带年份。于是日 K 选 1 年时横轴必然是 `09-30 / 03-31 / 09-21` 这种跨年却不显示年份的标签，用户实机反馈"看不懂、像日期往回跳"。Linux 侧按区间自动切换格式（**用户明确要求的偏离**）：

   - 日 K 跨年 → `yy-MM-dd`（实测 `25-09-22` / `26-04-01` / `26-09-22`），不跨年保持 `MM-dd`（实测 1 个月为 `08-05` / `09-01` / `09-22`）；
   - 周 K / 月 K → `yyyy-MM`（实测 5 年月 K 为 `2021-09` / `2024-03` / `2026-09`）；
   - 分时 → `HH:mm`（不变）；
   - 中间刻度改为"离区间中点最近的当月首个交易日"（找不到足够近的自然月初时回退到中点），看起来像正经坐标轴。

   `--stock-smoke` 的轴标签断言同步改成守住这些不变量：标签等于规划结果、点数位置对应正确蜡烛、形状匹配区间（跨年 8 字符 / 不跨年 5 字符 / `yyyy-MM` / `HH:mm`）、中间刻度确实落在月界，并保留"绝不是格式串字面量"（`MM-dd`）这条旧回归。

9. **助手窗口隐藏再显示时重新下发窗口标志**（`MainWindow.LinuxWindowFlags.cs`）。与第 4 条同一根因，但助手主窗口（翻译/快问/截图）比股票胶囊更早暴露：它已经由 `MainWindow.axaml` 声明了 `Topmost="True" ShowInTaskbar="False"`，但 mutter 在窗口取消映射（点别处、Esc、托盘/快捷键切换都会 `Hide()`）时删除 `_NET_WM_STATE`，而 Avalonia 只在属性**发生变化**时推送，所以第一次显示以后每轮"隐藏→再显示"都会丢掉 `_NET_WM_STATE_SKIP_TASKBAR`（Dock/任务栏冒出 "Little Tools AI" 图标）和 `_NET_WM_STATE_ABOVE`（不再置顶）。

   - 实机（GNOME 46 / mutter）对照：首次显示 `SKIP_TASKBAR, ABOVE, FOCUSED`；点别处隐藏后再显示只剩 `FOCUSED`。修复后连续 3 轮"显示→点别处隐藏→显示"（翻译与快问两个入口）每一轮都是 `SKIP_TASKBAR, ABOVE, FOCUSED`。
   - 做法：`MainWindow` 是 `sealed partial`，新增 `MainWindow.LinuxWindowFlags.cs`，用 `OnOpened` 覆写订阅 `IsVisible`（而不是往共用的 `MainWindow.axaml` / `MainWindow.axaml.cs` 里加调用点；这两个文件与上游 main 共用），每次可见后在 `DispatcherPriority.Background` 上调用 `Stock/StockWindow.cs:ReapplyWindowFlags`（同值赋值是 no-op，必须经一次反向绕行，见第 4 条）。**共用文件零改动。**

10. **设置对话框跳过任务栏**（`SettingsWindow.axaml`）。设置窗口有 `WM_TRANSIENT_FOR` 指向跳过任务栏的主窗口，GNOME 于是把它当成该应用唯一"值得列进任务栏"的窗口——打开设置时 Dock 会出现 "Little Tools AI" 图标（实测该窗口 `_NET_WM_STATE` 只有 `FOCUSED`，而主窗口此时仍是 `SKIP_TASKBAR, ABOVE`；关掉对话框图标随即消失）。在 `SettingsWindow.axaml` 上加了**一行** `ShowInTaskbar="False"`（与套件里其它对话框 `TodoDialogs` / `BacklogTabWindow` / `StockDetailsWindow` / `SelectionWindow` 在构造器里做的一致），改后打开设置实测 `SKIP_TASKBAR, FOCUSED`，Dock 无图标。共用文件只动了这一行，属 Linux 侧的有意改动。

11. **译图点击放大查看（`ScreenshotPreviewWindow.cs`）——Windows 没有的新增功能**。用户实机反馈"翻译完成后的图片无法放大查看"。**Windows 侧确认没有这个功能**：截图翻译的结果图由共用的 `MainWindow.AddScreenshotResult` 以 `MaxHeight = 520` + `Stretch = Uniform` 渲染，整份助手源码里没有任何点击/滚轮/缩放交互（`origin/main` 上 `git grep -n -iE "zoom|放大|preview|DoubleTapped"` 只命中 `ScreenshotTranslationImage.Create` 里无关的 `sourceCursor` 变量），而 Windows 的 WPF `TranslateApp/Program.cs` 只有文本翻译、根本没有截图翻译（全仓库搜 `截图|Screenshot|SelectionWindow` 只命中助手与宿主菜单）。所以译文小的截图在结果列表里永远被压在 520 DIP 内，文字看不清。这是**用户明确要求的新增需求，Windows 没有**，因此不属于"照 Windows 实现"，实现与验证如下：

    - **交互**：结果图悬停显示手形光标（`StandardCursorType.Hand`，与待办胶囊同一约定）并带「点击放大查看」提示，点击打开独立预览窗口（选弹出窗口而非就地放大：主窗口高度固定 430×360、输入框是固定胶囊，就地放大会把结果区挤到几乎不可用；独立窗口还能复用现成的对话框约定与 `GlassSurface`）。
    - **窗口约定**：无边框、圆角 15、毛玻璃 `GlassSurface.Dialog()`、`Topmost`、`ShowInTaskbar = false`、`WM_TRANSIENT_FOR` 主窗口，参照 `Todo/TodoDialogs.cs:TodoDialogWindow` 与 `Stock/StockDetailsWindow.cs`。实机（GNOME 46 / mutter）`_NET_WM_STATE = SKIP_TASKBAR, ABOVE, FOCUSED`，Dock 无图标。
    - **缩放**：初始为"原尺寸，放不下才按屏幕可用区域等比缩小"（`适应窗口`）；滚轮按**指针位置**锚定缩放、`＋/－` 按钮与 `+`/`-` 键按同一步长（×1.15）缩放（5%~800%）、`100%` 回原尺寸（1 位图像素 = 1 DIP）、`F` 回到适应窗口；放大后可用拖动或方向键平移。
    - **关闭**：Esc、`×`、失焦都关闭。**预览刻意不用 `ShowDialog`**（模态会让主窗口被禁用、点别处不会产生失焦，见 `TodoDialogs.cs:DateChooserWindow` 的注释），改用 `Show(owner)` + `Activate()`。主窗口本来"失焦即隐藏"，所以：预览打开期间用 `_previewWindowOpen` 抑制该隐藏（与设置对话框的 `_settingsDialogOpen` 同一手法）；**失焦**关闭时按既有规则让助手隐藏，**Esc/`×`** 关闭则等焦点稳定后把助手重新激活——否则取消映射预览的那一瞬间 mutter 会把焦点交给别的窗口，助手会跟着消失（实测修复前 Esc 之后主窗口 `IsUnmapped`，修复后 `IsViewable`）。
    - **位图生命周期**：预览自己从 PNG 重新加载一份 `Bitmap` 并在 `Closed` 里释放，不引用 `MainWindow._translationBitmaps`，所以主窗口隐藏时的 `ClearTranslationImages()`（含 `TranslationImageCache.Clear()` 删缓存文件）不会让它显示已释放的位图；连续翻译的每张结果图各自都能放大（`--preview-smoke` 一次种两张图断言）。
    - **共用文件影响**：`MainWindow.axaml.cs` 只动了 3 行（`AddScreenshotResult` 里把 `Image` 交给 `ScreenshotPreview.MakePreviewable(...)`；失焦守卫加一个 `!_previewWindowOpen`）。新代码放 `ScreenshotPreviewWindow.cs`（窗口 + 手形/点击接线）、`MainWindow.ImagePreview.cs`（partial：打开/关闭与守卫标志）、`MainWindow.ScreenshotPreviewSmoke.cs`（partial：离线断言）；`Program.cs` / `App.axaml.cs` 各加一个 `--preview-smoke/--preview-hold` 入口。
    - **验证**：`--preview-smoke DIR` 离线渲染并断言（手形光标、两张图各自可放大、缩放 1→1.323、平移偏移、Esc/`×` 关闭、`ShowInTaskbar=False`）；`CrossPlatform/tools/preview-demo.sh` 用真实窗口 + XTest 实机驱动（`hand2` 光标读数、点击弹出 930×885 预览、`＋`×2 后标签 100%→132.3%、Esc/失焦关闭后主窗口仍 `IsViewable`）。


12. **余量监控的 Codex 查询门槛**（`Monitor/MonitorCodexProvider.cs`）。Windows 只在 ChatGPT 桌面进程运行时才查询（`IsDesktopRunning()`，L593-L608），因为 `codex.exe` 只随桌面应用分发。Linux 上同一份 `codex app-server` 也以独立 CLI 安装，所以判断条件换成「找得到 app-server 可执行文件」（`LITTLETOOLS_CODEX_BIN` 覆盖 → `~/.codex/.sandbox-bin/codex` → `PATH` → 桌面 bundle `/usr/lib/chatgpt/resources/codex` 等），CLI-only 的机器因此能拿到真实数据而不是永远显示「Codex 未运行」；都找不到时仍用 Windows 的原文案。桌面进程检测本身也按 Linux 的路径规则实现（可执行文件名 `ChatGPT`/`codex-launcher`）。

13. **余量监控的手动 Key 存放位置**（`Monitor/MonitorCredentials.cs`、`MonitorProviderSettingsWindow.cs`）。Windows 用当前用户 DPAPI 把手动 Key 加密后写进 `providers.json` 的 `GlmProtectedKey`/`DeepSeekProtectedKey`。Linux 没有 DPAPI，手动 Key 存进套件的平台密钥环（`secret-tool`，退化为 0600 的 `credentials.json`），两个模块共用同一把 Key；`providers.json` 的字段与形状不变（原 DPAPI blob 原样保留），Windows 仍可读该文件。Windows 的 blob 在 Linux 无法解密，设置对话框会明确提示「检测到 Windows 保存的手动 Key，本机无法解密；请重新输入一次」，而不是装作已配置。

14. **余量监控小窗的排版适配**（`Monitor/MonitorWindow.cs`、`MonitorLayout.cs`）。Windows 的 316 像素 HUD 用 12 像素圆点列 + 79 像素标签列 + 自适应值列；Linux 字体回退到 Inter 后「DEEPSEEK」需要约 89 像素，标签列因此加宽到 92。另外右侧值改为填满单元格 + `TextAlignment.Right` + 字符省略号 + 完整文本 tooltip：Avalonia 对右对齐 `TextBlock` 报出的 desired width 明显偏小（114 像素的文本报 75、225 像素的报 38），照它排布会把值贴到窗口右缘并从字形中间截断；GLM 行在 316 像素里本来就放不下（任何字体都放不下），省略号与 Windows 的裁剪等价，tooltip 保证整行可读。设置对话框同理：Windows 的 430×470 变成**下限**，用 `SizeToContent.Height` 让按钮行始终在窗口内（Linux 字体让提示段落多折了一行，实测 430×515），否则 保存/取消 有一半在窗外点不到。

15. **没有移植「鼠标穿透」**（Windows L1830-L1837 的 `WS_EX_TRANSPARENT`）。Avalonia 没有输入形状 API，而 Linux 套件托盘没有按模块的子菜单，一旦开启就没有地方关掉；宁可不做也不做一个关不掉的开关。Windows 托盘里的「显示/隐藏」「立即刷新」「退出」在 Linux 由套件托盘开关、明细窗口的 刷新/退出 承担，语义见下一条。

16. **明细窗口的「退出」= 关闭该模块**。Windows 的余量监控是独立进程，「退出」结束它；Linux 套件是单进程宿主，所以「退出」把 `manager.json` 的 `MonitorEnabled` 置为 false 并收起窗口（托盘勾号同步翻转为未选中），进程继续服务其它模块。待办/股票在没有这个按钮的情况下只由托盘开关控制，这里是行为最接近的等价实现。

17. **贴边收起的两个 X11 修正**（`Monitor/MonitorWindow.cs`）。都属于「照 Windows 行为实现、但 X11 的异步性让它必须换个写法」，不是风格偏离：
    - 贴边吸附改到「拖动停止后」（400 ms 无位移的 settle 定时器）执行。X11 的交互式移动由窗口管理器接管，`BeginMoveDrag` 立即返回、最终位置稍后才通过 `PositionChanged` 到达，照 Windows 那样在按下后固定 180 ms 吸附，实测会把中间位置当终点，拖到屏幕边缘也不收起。
    - `PointerExited` 里不再用 `IsPointerOver` 作为启动 550 ms 收起定时器的条件：事件送达时该属性仍是 `true`（旧值），于是「悬停展开后再移开」永远不会重新收起；定时器回调里再判一次即可。**`Stock/StockWindow.cs` 有同一处写法**，股票胶囊的「边缘收起」很可能有同样的潜在问题，本次未动（超出本阶段范围），建议按同样方式改一行。

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

在翻译窗口左下角的语言菜单中选择“翻译设置…”，填写百度翻译开放平台的 APPID 和密钥。文本与图片共用这套凭据，但图片翻译必须单独开通。设置界面会回填已保存的 APPID，并把已保存的密钥显示为“已保存（留空=不修改）”，所以不会因为输入框看起来是空的而误以为凭据丢了。

Linux 严格按下面的顺序解析，前者优先（Windows 顺序相同，只是把钥匙串换成当前用户 DPAPI）：

1. **环境变量**（最高优先级，需成对设置）
   - 百度：`BAIDU_TRANSLATE_APP_ID` + `BAIDU_TRANSLATE_SECRET_KEY`
   - GLM：`ZHIPUAI_API_KEY`（同时兼容 `ZHIPU_API_KEY`、`GLM_API_KEY`、`BIGMODEL_API_KEY`，也可以是设置界面里填的“GLM 环境变量”名）
   - DeepSeek：`DEEPSEEK_API_KEY`
2. **系统钥匙串**（GNOME Keyring，`secret-tool`）
3. **配置文件**`${XDG_CONFIG_HOME:-~/.config}/little-tools/translate/credentials.json`（文件 0600、目录 0700）
4. **旧版 `appsettings.json`**：最后兜底，只读兼容，见下

临时使用：

```bash
export BAIDU_TRANSLATE_APP_ID='your-appid'
export BAIDU_TRANSLATE_SECRET_KEY='your-secret'
export ZHIPUAI_API_KEY='your-key'
export DEEPSEEK_API_KEY='your-key'
```

桌面快捷方式与开机自启不会继承交互 shell 的临时 `export`，所以要让它们生效，请写进 `~/.profile`（GNOME 会话会读），或把密钥保存到钥匙串 / `credentials.json`。**不要把 Key 写进 `.desktop` 文件。**

安装 `libsecret-tools` 后可以直接在设置里保存到系统钥匙串：

```bash
sudo apt install libsecret-tools
```

没装 `libsecret-tools`（或当前会话拿不到钥匙串）时，设置界面依然能保存：密钥会落到 `${XDG_CONFIG_HOME:-~/.config}/little-tools/translate/credentials.json`（0600）。该文件在 XDG 配置目录里，**重装或更新都不会删除**；以前“必须先装 libsecret-tools 才能保存、否则每次都要重填”的行为已经修掉。

**更新不会吃掉凭据**：`reinstall-linux.sh` 会整体替换程序目录，所以它在 `rm -rf` 之前先把程序目录里的 `appsettings.json` 备份到 `${XDG_CONFIG_HOME:-~/.config}/little-tools/translate/appsettings.json` 并打印提示；程序继续读取该位置，在设置界面点一次“保存”即可迁移到钥匙串或 `credentials.json`。程序目录是唯一会被更新删除的位置，不要把凭据只放在那里。重启那一行也在安装后清掉 `XDG_*` 再拉起程序，否则从导出了 `XDG_CONFIG_HOME` 的 shell（例如本仓库的 Linux 测试环境）执行更新时，新实例会去读那个目录、看不到 `assistant-settings.json`，表现成“更新后又要重新填”。

`--diagnose FILE` 的 `credentials` 字段会写明每一项的实际来源，便于一眼定位：

```json
"credentials": {
  "baidu": { "source": "keyring", "appId": "20260623002636616", "appIdStored": true, "secretStored": true },
  "glm": { "source": "keyring", "secretStored": true },
  "deepSeek": { "source": "none", "secretStored": false },
  "keyringAvailable": true,
  "configFile": "/home/user/.config/little-tools/translate/credentials.json",
  "configFileExists": false,
  "legacyAppIdFound": false
}
```

`source` 取值 `env` / `dpapi` / `keyring` / `config` / `legacy` / `none`。`appIdStored` 与 `source` 相互独立：钥匙串临时不可用时 `source` 是 `none`，但 `appIdStored` 仍为 `true`，APPID 也不会在设置界面显示成空。

Windows 可以在应用设置中安全保存（DPAPI），也会自动兼容现有 AI Usage Monitor 的 DPAPI 配置。图片接口为开放平台签名接口，不使用百度智能云的 API Key/Secret Key。

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

- 每日待办、股票观察与 AI 余量监控都已原生提供，Windows 托盘宿主提供的模块在 Linux 都有对应实现。
- AI 余量监控的 Codex 行需要本机有可用的 `codex app-server` 与已登录的 `CODEX_HOME`；GLM 的旧余额回退接口 `paas/v4/balance` 在真机上已返回 404（2026-09-23 实测），因此账户报告不可用时余额确实读不到，界面按 Windows 文案显示「套餐正常 · 账户余额不可用」。
- 余量监控的「鼠标穿透」未移植（见「有意偏离 Windows 的实现」第 15 条）。
- 股票观察的明细界面在 Windows 里是同一个 `StockWindow` 的控件字段；Linux 端口把它拆成独立的 `StockDetailsWindow`（两边本来就是两个顶层窗口），行为一致但控件引用各自持有。
- 每日待办的贴边自动收起在 X11 生效；Wayland 无法自定位窗口，该增强会自动不生效，窗口仍可正常使用。
- 专注倒计时提示音使用桌面声音主题（`canberra-gtk-play`）；没有可用播放器时静默降级，不影响计时。
- Linux 区域截图依赖桌面提供的截图程序；正式发布前需要在目标 Ubuntu 的 Wayland 会话实测。
- Wayland 不支持程序自定位窗口，因此贴边隐藏等桌面增强需要单独的“可用则启用”实现，尚未包含在本阶段。
- GLM-5.3-Flash 与 DeepSeek V4.1 Flash 是较新的模型，服务端请求字段需以实际 API 账户返回为准；界面会显示完整 HTTP 错误便于适配。
- 当前 Windows 托盘宿主已经直接启动本助手；旧 WPF 百度翻译不再注册快捷键。
