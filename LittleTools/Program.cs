using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using LittleTools.Common;

namespace LittleTools.Manager
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string command = args.Length == 0 ? "--translate" : args[0].ToLowerInvariant();
            bool created;
            using (var mutex = new Mutex(true, "LittleTools.Manager.SingleInstance", out created))
            {
                if (!created) return CommandPipe.Send(CommandPipe.ManagerName, command, 5000) > 0 ? 0 : 2;
                if (command == "--exit") return 0;
                Forms.Application.EnableVisualStyles();
                Forms.Application.SetCompatibleTextRenderingDefault(false);
                var app = new WpfApplication { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                using (var host = new ManagerHost(app))
                using (var server = new CommandPipe(CommandPipe.ManagerName,
                    delegate(string request) { app.Dispatcher.BeginInvoke(new Action(delegate { host.HandleCommand(request); })); }))
                {
                    app.Dispatcher.BeginInvoke(new Action(delegate { host.HandleCommand(command); }));
                    app.Run();
                }
            }
            return 0;
        }
    }

    internal sealed class ManagerSettings
    {
        public bool MonitorEnabled = true;
        public bool TranslateEnabled = true;
        public bool TodoNotesEnabled = true;
        public bool StockEnabled = true;
        public bool EdgeHideMonitor = true;
        public bool EdgeHideTranslate = true;
        public bool EdgeHideTodo = true;
        public bool EdgeHideStock = true;
    }

    internal sealed class ManagerHost : IDisposable
    {
        private readonly WpfApplication app;
        private readonly string suiteRoot;
        private readonly string settingsPath;
        private readonly ManagerSettings settings;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ContextMenuStrip trayMenu;
        private readonly Forms.ToolStripMenuItem monitorItem;
        private readonly Forms.ToolStripMenuItem translateItem;
        private readonly Forms.ToolStripMenuItem todoNotesItem;
        private readonly Forms.ToolStripMenuItem stockItem;
        private readonly Forms.ToolStripMenuItem autoStartItem;
        private readonly Forms.ToolStripMenuItem edgeAllItem;
        private readonly Forms.ToolStripMenuItem edgeMonitorItem;
        private readonly Forms.ToolStripMenuItem edgeTranslateItem;
        private readonly Forms.ToolStripMenuItem edgeTodoItem;
        private readonly Forms.ToolStripMenuItem edgeStockItem;
        private readonly Forms.Timer menuActionTimer;
        private readonly Forms.Timer menuOutsideClickTimer;
        private readonly DispatcherTimer startupTimer;
        private readonly DispatcherTimer assistantWatchdog;
        private readonly Mutex monitorMutex;
        private readonly Mutex translateMutex;
        private readonly Mutex todoMutex;
        private readonly Mutex stockMutex;
        private readonly bool ownsMonitorMutex;
        private readonly bool ownsTranslateMutex;
        private readonly bool ownsTodoMutex;
        private readonly bool ownsStockMutex;
        private AIUsageMonitor.MonitorController monitor;
        private Process assistantProcess;
        private LittleTools.DailyTodo.DailyTodoWindow todoWindow;
        private LittleTools.StockMonitor.StockController stockController;
        private int startupStage;
        private bool changing;
        private bool menuActionInProgress;
        private Forms.MouseButtons lastMouseButtons;
        private bool disposed;

        public ManagerHost(WpfApplication app)
        {
            this.app = app;
            string managerDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            suiteRoot = Directory.GetParent(Directory.GetParent(managerDir).FullName).FullName;
            settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "manager.json");
            settings = LoadSettings();
            NormalizeAutoStartEntry();

            bool monitorOwned; bool translateOwned; bool todoOwned; bool stockOwned;
            monitorMutex = new Mutex(true, "LittleTools.AIUsageMonitor.SingleInstance", out monitorOwned);
            translateMutex = new Mutex(true, "LittleTools.TranslateApp.SingleInstance", out translateOwned);
            todoMutex = new Mutex(true, "LittleTools.DailyTodo.SingleInstance", out todoOwned);
            stockMutex = new Mutex(true, "LittleTools.StockMonitor.SingleInstance", out stockOwned);
            ownsMonitorMutex = monitorOwned;
            ownsTranslateMutex = translateOwned;
            ownsTodoMutex = todoOwned;
            ownsStockMutex = stockOwned;

            trayMenu = new Forms.ContextMenuStrip();
            Forms.ContextMenuStrip menu = trayMenu;
            menuActionTimer = new Forms.Timer { Interval = 400 };
            menuActionTimer.Tick += delegate
            {
                menuActionTimer.Stop();
                menuActionInProgress = false;
            };
            menu.Items.Add(new Forms.ToolStripMenuItem("LITTLE TOOLS · 工具箱") { Enabled = false });
            menu.Items.Add(new Forms.ToolStripSeparator());
            var openTranslation = new Forms.ToolStripMenuItem("翻译");
            openTranslation.Click += delegate { HandleCommand("--translate"); };
            menu.Items.Add(openTranslation);
            var openChat = new Forms.ToolStripMenuItem("问答");
            openChat.Click += delegate { HandleCommand("--chat"); };
            menu.Items.Add(openChat);
            var openScreenshot = new Forms.ToolStripMenuItem("截图翻译");
            openScreenshot.Click += delegate { HandleCommand("--screenshot"); };
            menu.Items.Add(openScreenshot);
            menu.Items.Add(new Forms.ToolStripSeparator());

            monitorItem = new Forms.ToolStripMenuItem("余量监控") { CheckOnClick = true, Checked = settings.MonitorEnabled };
            monitorItem.CheckedChanged += delegate { if (!changing) SetMonitorEnabled(monitorItem.Checked); };
            menu.Items.Add(monitorItem);

            translateItem = new Forms.ToolStripMenuItem("AI 翻译与快问") { CheckOnClick = true, Checked = settings.TranslateEnabled };
            translateItem.CheckedChanged += delegate { if (!changing) SetTranslateEnabled(translateItem.Checked); };
            menu.Items.Add(translateItem);

            todoNotesItem = new Forms.ToolStripMenuItem("每日待办") { CheckOnClick = true, Checked = settings.TodoNotesEnabled };
            todoNotesItem.CheckedChanged += delegate { if (!changing) SetTodoEnabled(todoNotesItem.Checked); };
            menu.Items.Add(todoNotesItem);

            stockItem = new Forms.ToolStripMenuItem("股票观察") { CheckOnClick = true, Checked = settings.StockEnabled };
            stockItem.CheckedChanged += delegate { if (!changing) SetStockEnabled(stockItem.Checked); };
            menu.Items.Add(stockItem);

            var edgeMenu = new Forms.ToolStripMenuItem("贴边自动收起");
            edgeAllItem = new Forms.ToolStripMenuItem("全部组件") { CheckOnClick = true };
            edgeMonitorItem = new Forms.ToolStripMenuItem("余量监控") { CheckOnClick = true, Checked = settings.EdgeHideMonitor };
            edgeTranslateItem = new Forms.ToolStripMenuItem("AI 助手（独立窗口）") { Enabled = false, Checked = settings.EdgeHideTranslate };
            edgeTodoItem = new Forms.ToolStripMenuItem("每日待办") { CheckOnClick = true, Checked = settings.EdgeHideTodo };
            edgeStockItem = new Forms.ToolStripMenuItem("股票观察") { CheckOnClick = true, Checked = settings.EdgeHideStock };
            edgeAllItem.Checked = settings.EdgeHideMonitor && settings.EdgeHideTranslate && settings.EdgeHideTodo && settings.EdgeHideStock;
            edgeAllItem.CheckedChanged += delegate { if (!changing) SetAllEdgeHide(edgeAllItem.Checked); };
            edgeMonitorItem.CheckedChanged += delegate { if (!changing) SetEdgeHide("monitor", edgeMonitorItem.Checked); };
            edgeTranslateItem.CheckedChanged += delegate { if (!changing) SetEdgeHide("translate", edgeTranslateItem.Checked); };
            edgeTodoItem.CheckedChanged += delegate { if (!changing) SetEdgeHide("todo", edgeTodoItem.Checked); };
            edgeStockItem.CheckedChanged += delegate { if (!changing) SetEdgeHide("stock", edgeStockItem.Checked); };
            edgeMenu.DropDownItems.Add(edgeAllItem);
            edgeMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
            edgeMenu.DropDownItems.Add(edgeMonitorItem);
            edgeMenu.DropDownItems.Add(edgeTranslateItem);
            edgeMenu.DropDownItems.Add(edgeTodoItem);
            edgeMenu.DropDownItems.Add(edgeStockItem);
            menu.Items.Add(edgeMenu);
            menu.Items.Add(new Forms.ToolStripSeparator());

            autoStartItem = new Forms.ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = IsAutoStartEnabled() };
            autoStartItem.CheckedChanged += delegate { SetAutoStart(autoStartItem.Checked); };
            menu.Items.Add(autoStartItem);

            var folder = new Forms.ToolStripMenuItem("打开工具目录");
            folder.Click += delegate { OpenSuiteFolder(); };
            menu.Items.Add(folder);
            menu.Items.Add(new Forms.ToolStripSeparator());

            var exit = new Forms.ToolStripMenuItem("退出 Little Tools");
            exit.Click += delegate { app.Dispatcher.BeginInvoke(new Action(ExitSuite)); };
            menu.Items.Add(exit);

            menu.MouseDown += delegate { PreserveMenuDuringAction(); };
            menu.KeyDown += delegate { PreserveMenuDuringAction(); };
            menu.ItemClicked += delegate { PreserveMenuDuringAction(); };
            edgeMenu.DropDown.MouseDown += delegate { PreserveMenuDuringAction(); };
            edgeMenu.DropDown.KeyDown += delegate { PreserveMenuDuringAction(); };
            edgeMenu.DropDown.ItemClicked += delegate { PreserveMenuDuringAction(); };
            menuOutsideClickTimer = new Forms.Timer { Interval = 30 };
            menuOutsideClickTimer.Tick += delegate
            {
                if (!menu.Visible) { menuOutsideClickTimer.Stop(); return; }
                Forms.MouseButtons currentButtons = Forms.Control.MouseButtons;
                Forms.MouseButtons newlyPressed = (Forms.MouseButtons)((int)currentButtons & ~(int)lastMouseButtons);
                lastMouseButtons = currentButtons;
                if (newlyPressed == Forms.MouseButtons.None) return;

                Point cursor = Forms.Control.MousePosition;
                bool insideMenu = menu.Bounds.Contains(cursor)
                    || (edgeMenu.DropDown.Visible && edgeMenu.DropDown.Bounds.Contains(cursor));
                if (insideMenu) return;

                menuActionTimer.Stop();
                menuActionInProgress = false;
                menu.Close(Forms.ToolStripDropDownCloseReason.AppClicked);
            };
            menu.Opened += delegate
            {
                lastMouseButtons = Forms.Control.MouseButtons;
                menuOutsideClickTimer.Start();
            };
            menu.Closed += delegate
            {
                menuOutsideClickTimer.Stop();
                menuActionTimer.Stop();
                menuActionInProgress = false;
            };
            menu.Closing += delegate(object sender, Forms.ToolStripDropDownClosingEventArgs args)
            {
                if (menuActionInProgress) args.Cancel = true;
            };
            edgeMenu.DropDown.Closing += delegate(object sender, Forms.ToolStripDropDownClosingEventArgs args)
            {
                if (menuActionInProgress) args.Cancel = true;
            };

            tray = new Forms.NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Forms.Application.ExecutablePath) ?? SystemIcons.Application,
                Text = "Little Tools · 工具箱",
                Visible = true,
                ContextMenuStrip = menu
            };
            tray.DoubleClick += delegate { app.Dispatcher.BeginInvoke(new Action(delegate { HandleCommand("--translate"); })); };

            if (settings.TranslateEnabled) StartTranslate();
            assistantWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            assistantWatchdog.Tick += delegate
            {
                if (!disposed && settings.TranslateEnabled && (assistantProcess == null || assistantProcess.HasExited))
                    StartTranslate();
            };
            assistantWatchdog.Start();
            startupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            startupTimer.Tick += delegate { StartNextModule(); };
            startupTimer.Start();
        }

        private void PreserveMenuDuringAction()
        {
            menuActionInProgress = true;
            menuActionTimer.Stop();
            menuActionTimer.Start();
        }

        private void StartNextModule()
        {
            if (startupStage == 0)
            {
                if (settings.TodoNotesEnabled) StartTodo();
                startupStage = 1;
                startupTimer.Interval = TimeSpan.FromMilliseconds(700);
                return;
            }
            if (startupStage == 1)
            {
                if (settings.StockEnabled) StartStock();
                startupStage = 2;
                startupTimer.Interval = TimeSpan.FromMilliseconds(850);
                return;
            }
            startupTimer.Stop();
            if (settings.MonitorEnabled) StartMonitor();
            startupStage = 3;
        }

        private void StartMonitor()
        {
            if (monitor != null || !ownsMonitorMutex) return;
            try
            {
                AIUsageMonitor.MonitorController created = new AIUsageMonitor.MonitorController(app.Dispatcher, false);
                created.Window.Closed += delegate
                {
                    if (!ReferenceEquals(monitor, created)) return;
                    created.Dispose();
                    monitor = null;
                };
                monitor = created;
                created.Window.SetEdgeHideEnabled(settings.EdgeHideMonitor);
                created.Window.Show();
            }
            catch (Exception exception)
            {
                monitor = null;
                ShowModuleError("余量监控", exception);
            }
        }

        private void StopMonitor()
        {
            if (monitor == null) return;
            var window = monitor.Window;
            monitor.Dispose();
            monitor = null;
            try { window.Close(); } catch { }
        }

        public void HandleCommand(string command)
        {
            if (disposed) return;
            if (command == "--exit") { ExitSuite(); return; }
            if (command == "--background") return;
            string assistantCommand;
            if (command == "--chat") assistantCommand = "ShowChat";
            else if (command == "--screenshot") assistantCommand = "Screenshot";
            else if (command == "--translate" || command == "--toggle") assistantCommand = "ShowTranslation";
            else return;

            if (!settings.TranslateEnabled)
            {
                settings.TranslateEnabled = true;
                changing = true; translateItem.Checked = true; changing = false;
                SaveSettings();
            }
            StartTranslate(assistantCommand);
        }

        private void StartTranslate() { StartTranslate("Background"); }

        private void StartTranslate(string command)
        {
            try
            {
                if (assistantProcess != null && !assistantProcess.HasExited)
                {
                    if (command != "Background" && CommandPipe.Send(CommandPipe.AssistantName, "managed:" + command, 5000) == 0)
                        throw new IOException("AI 助手暂时没有响应，请稍后重试。");
                    return;
                }
                if (assistantProcess != null) { assistantProcess.Dispose(); assistantProcess = null; }
                // Adopt an already running assistant instead of repeatedly spawning
                // short-lived clients that the watchdog would mistake for crashes.
                int existingId = CommandPipe.Send(CommandPipe.AssistantName, "managed:" + command, 200);
                if (existingId > 0)
                {
                    assistantProcess = Process.GetProcessById(existingId);
                    return;
                }
                string executable = FindAssistantExecutable();
                if (string.IsNullOrEmpty(executable))
                {
                    throw new FileNotFoundException("找不到 LittleTools.Assistant.exe，请重新生成或安装包含 AI 助手的版本。");
                }
                assistantProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "--managed " + (command == "ShowChat" ? "--chat" : command == "Screenshot" ? "--screenshot" : command == "ShowTranslation" ? "--translate" : "--background"),
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception exception)
            {
                ShowModuleError("AI 翻译与快问", exception);
            }
        }

        private void StopTranslate()
        {
            Process runningAssistant = assistantProcess;
            CommandPipe.Send(CommandPipe.AssistantName, "Exit", 1000);
            if (runningAssistant != null && !runningAssistant.HasExited)
            {
                try { runningAssistant.WaitForExit(3000); } catch { }
            }
            if (runningAssistant != null) runningAssistant.Dispose();
            assistantProcess = null;
        }

        private string FindAssistantExecutable()
        {
            string[] candidates =
            {
                Path.Combine(suiteRoot, "LittleTools.Assistant.exe"),
                Path.Combine(suiteRoot, "Assistant", "LittleTools.Assistant.exe"),
                Path.Combine(suiteRoot, "CrossPlatform", "artifacts", "win-x64", "LittleTools.Assistant.exe")
            };
            foreach (string candidate in candidates)
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        private void StartTodo()
        {
            if (todoWindow != null || !ownsTodoMutex) return;
            try
            {
                var created = new LittleTools.DailyTodo.DailyTodoWindow();
                created.FocusFinished += ShowFocusFinished;
                created.Closed += delegate
                {
                    created.FocusFinished -= ShowFocusFinished;
                    if (ReferenceEquals(todoWindow, created)) todoWindow = null;
                };
                todoWindow = created;
                created.SetEdgeHideEnabled(settings.EdgeHideTodo);
                created.Show();
            }
            catch (Exception exception)
            {
                todoWindow = null;
                ShowModuleError("每日待办", exception);
            }
        }

        private void StopTodo()
        {
            if (todoWindow == null) return;
            todoWindow.FocusFinished -= ShowFocusFinished;
            try { todoWindow.Close(); } catch { }
            todoWindow = null;
        }

        private void ShowFocusFinished(string itemText)
        {
            string message = string.IsNullOrWhiteSpace(itemText)
                ? "本轮专注已完成。"
                : "“" + itemText + "”专注时间结束。";
            tray.ShowBalloonTip(6000, "专注结束", message, Forms.ToolTipIcon.Info);
        }

        private void StartStock()
        {
            if (stockController != null || !ownsStockMutex) return;
            try
            {
                var created = new LittleTools.StockMonitor.StockController(app, false);
                created.AlertRaised += ShowStockAlert;
                stockController = created;
                created.Window.SetEdgeHideEnabled(settings.EdgeHideStock);
                created.Window.Show();
            }
            catch (Exception exception)
            {
                stockController = null;
                ShowModuleError("股票观察", exception);
            }
        }

        private void StopStock()
        {
            if (stockController == null) return;
            stockController.AlertRaised -= ShowStockAlert;
            stockController.Dispose();
            stockController = null;
        }

        private void ShowStockAlert(string title, string message)
        {
            tray.ShowBalloonTip(6000, title, message, Forms.ToolTipIcon.Info);
        }

        private void SetMonitorEnabled(bool enabled)
        {
            settings.MonitorEnabled = enabled;
            if (enabled) StartMonitor(); else StopMonitor();
            SaveSettings();
        }

        private void SetTranslateEnabled(bool enabled)
        {
            settings.TranslateEnabled = enabled;
            if (enabled) StartTranslate(); else StopTranslate();
            SaveSettings();
        }

        private void SetTodoEnabled(bool enabled)
        {
            settings.TodoNotesEnabled = enabled;
            if (enabled) StartTodo(); else StopTodo();
            SaveSettings();
        }

        private void SetStockEnabled(bool enabled)
        {
            settings.StockEnabled = enabled;
            if (enabled) StartStock(); else StopStock();
            SaveSettings();
        }

        private void SetAllEdgeHide(bool enabled)
        {
            changing = true;
            edgeMonitorItem.Checked = enabled;
            edgeTranslateItem.Checked = enabled;
            edgeTodoItem.Checked = enabled;
            edgeStockItem.Checked = enabled;
            changing = false;
            settings.EdgeHideMonitor = settings.EdgeHideTranslate = settings.EdgeHideTodo = settings.EdgeHideStock = enabled;
            ApplyEdgeHideSettings();
            SaveSettings();
        }

        private void SetEdgeHide(string module, bool enabled)
        {
            if (module == "monitor") settings.EdgeHideMonitor = enabled;
            else if (module == "translate") settings.EdgeHideTranslate = enabled;
            else if (module == "todo") settings.EdgeHideTodo = enabled;
            else if (module == "stock") settings.EdgeHideStock = enabled;
            changing = true;
            edgeAllItem.Checked = settings.EdgeHideMonitor && settings.EdgeHideTranslate && settings.EdgeHideTodo && settings.EdgeHideStock;
            changing = false;
            ApplyEdgeHideSettings();
            SaveSettings();
        }

        private void ApplyEdgeHideSettings()
        {
            if (monitor != null) monitor.Window.SetEdgeHideEnabled(settings.EdgeHideMonitor);
            if (todoWindow != null) todoWindow.SetEdgeHideEnabled(settings.EdgeHideTodo);
            if (stockController != null) stockController.Window.SetEdgeHideEnabled(settings.EdgeHideStock);
        }

        private void ToggleMonitor()
        {
            if (monitor == null)
            {
                changing = true; monitorItem.Checked = true; changing = false;
                SetMonitorEnabled(true);
                return;
            }
            if (monitor.Window.IsVisible) monitor.Window.Hide();
            else { monitor.Window.Show(); monitor.Window.Activate(); }
        }

        private void ShowModuleError(string module, Exception exception)
        {
            tray.ShowBalloonTip(4000, "Little Tools", module + "启动失败：" + exception.Message, Forms.ToolTipIcon.Error);
        }

        private ManagerSettings LoadSettings()
        {
            try
            {
                if (File.Exists(settingsPath))
                    return new JavaScriptSerializer().Deserialize<ManagerSettings>(File.ReadAllText(settingsPath, Encoding.UTF8)) ?? new ManagerSettings();
            }
            catch { }
            return new ManagerSettings();
        }

        private void SaveSettings()
        {
            try
            {
                AtomicFile.WriteUtf8(settingsPath, new JavaScriptSerializer().Serialize(settings));
            }
            catch { }
        }

        private static string RunValueName { get { return "Little Tools"; } }
        private static string StartupLauncherPath
        {
            get
            {
                string managerDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string root = Directory.GetParent(Directory.GetParent(managerDir).FullName).FullName;
                return Path.Combine(root, "LittleToolsStartup", "bin", "LittleToolsStartup.exe");
            }
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    return key != null && (key.GetValue(RunValueName) != null || key.GetValue("LittleTools") != null);
            }
            catch { return false; }
        }

        private static void NormalizeAutoStartEntry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    object legacy = key.GetValue("LittleTools");
                    if (key.GetValue(RunValueName) == null && legacy != null) key.SetValue(RunValueName, legacy);
                    if (key.GetValue(RunValueName) != null && File.Exists(StartupLauncherPath))
                        key.SetValue(RunValueName, "\"" + StartupLauncherPath + "\"");
                    key.DeleteValue("LittleTools", false);
                    key.DeleteValue("LittleToolsTranslate", false);
                }
            }
            catch { }
        }

        private static void SetAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (enabled) key.SetValue(RunValueName, "\"" + StartupLauncherPath + "\"");
                    else key.DeleteValue(RunValueName, false);
                    key.DeleteValue("LittleTools", false);
                    key.DeleteValue("LittleToolsTranslate", false);
                }
            }
            catch { }
        }

        private void OpenSuiteFolder()
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + suiteRoot + "\"") { UseShellExecute = true }); }
            catch { }
        }

        private void ExitSuite()
        {
            Dispose();
            app.Shutdown();
        }

        private static void ReleaseModuleMutex(Mutex mutex, bool owned)
        {
            if (owned) { try { mutex.ReleaseMutex(); } catch { } }
            try { mutex.Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            menuOutsideClickTimer.Stop();
            menuActionTimer.Stop();
            startupTimer.Stop();
            assistantWatchdog.Stop();
            StopMonitor();
            StopTranslate();
            StopTodo();
            StopStock();
            tray.Visible = false;
            tray.Dispose();
            trayMenu.Dispose();
            ReleaseModuleMutex(monitorMutex, ownsMonitorMutex);
            ReleaseModuleMutex(translateMutex, ownsTranslateMutex);
            ReleaseModuleMutex(todoMutex, ownsTodoMutex);
            ReleaseModuleMutex(stockMutex, ownsStockMutex);
        }
    }
}
