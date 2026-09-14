using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using LittleTools.Common;

namespace LittleTools.TranslateApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "LittleTools.TranslateApp.SingleInstance", out created))
            {
                if (!created) return;
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var config = ApiConfig.Load();
                var window = new TranslateWindow(new TranslateService(config), config);
                bool managed = args != null && Array.IndexOf(args, "--managed") >= 0;
                using (var controller = new AppController(app, window, !managed)) app.Run();
            }
        }
    }

    internal sealed class AppController : IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const int HotkeyId = 0x5452;
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        private readonly Application app;
        private readonly TranslateWindow window;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ContextMenuStrip trayMenu;
        private readonly IntPtr handle;
        private readonly HwndSource source;
        private bool disposed;

        public AppController(Application app, TranslateWindow window, bool showTray)
        {
            this.app = app;
            this.window = window;
            app.MainWindow = window;
            handle = new WindowInteropHelper(window).EnsureHandle();
            source = HwndSource.FromHwnd(handle);
            source.AddHook(WndProc);
            bool registered = RegisterHotKey(handle, HotkeyId, 0x0004, 0x08);

            if (showTray)
            {
                trayMenu = BuildTrayMenu();
                tray = new Forms.NotifyIcon
                {
                    Icon = System.Drawing.SystemIcons.Asterisk,
                    Text = "Little Tools · Translate",
                    Visible = true,
                    ContextMenuStrip = trayMenu
                };
                tray.DoubleClick += delegate { app.Dispatcher.BeginInvoke(new Action(Toggle)); };
                if (!registered)
                {
                    tray.BalloonTipTitle = "翻译快捷键不可用";
                    tray.BalloonTipText = "Shift + Backspace 已被占用，可双击托盘图标打开。";
                    tray.ShowBalloonTip(4500);
                }
            }
        }

        private Forms.ContextMenuStrip BuildTrayMenu()
        {
            var menu = new Forms.ContextMenuStrip();
            var show = new Forms.ToolStripMenuItem("显示 / 隐藏");
            show.Click += delegate { app.Dispatcher.BeginInvoke(new Action(Toggle)); };
            menu.Items.Add(show);
            var autoStart = new Forms.ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = IsAutoStartEnabled() };
            autoStart.CheckedChanged += delegate { SetAutoStart(autoStart.Checked); };
            menu.Items.Add(autoStart);
            menu.Items.Add(new Forms.ToolStripSeparator());
            var exit = new Forms.ToolStripMenuItem("退出");
            exit.Click += delegate { app.Dispatcher.BeginInvoke(new Action(Exit)); };
            menu.Items.Add(exit);
            return menu;
        }

        private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                Toggle();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void Toggle()
        {
            if (window.IsVisible) window.HideWindow(); else window.ShowWindow();
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    return key != null && key.GetValue("LittleToolsTranslate") != null;
            }
            catch { return false; }
        }

        private static void SetAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (enabled) key.SetValue("LittleToolsTranslate", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                    else key.DeleteValue("LittleToolsTranslate", false);
                }
            }
            catch { }
        }

        private void Exit() { Dispose(); app.Shutdown(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            UnregisterHotKey(handle, HotkeyId);
            if (source != null) source.RemoveHook(WndProc);
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                trayMenu.Dispose();
            }
        }
    }

    internal sealed class TranslateWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x80;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        private readonly TranslateService translator;
        private readonly ApiConfig config;
        private readonly TextBox input;
        private readonly TextBlock placeholder;
        private readonly TextBox result;
        private readonly Border resultPanel;
        private readonly TextBlock languageText;
        private readonly Border shell;
        private readonly ContextMenu languageMenu;
        private string from = "auto";
        private string to = "zh";
        private bool translating;
        private bool positioned;
        private readonly DispatcherTimer edgeHideTimer;
        private bool edgeHideEnabled = true;
        private int hiddenEdge;
        private double revealedLeft;

        public TranslateWindow(TranslateService translator, ApiConfig config)
        {
            this.translator = translator;
            this.config = config;
            Width = 430;
            SizeToContent = SizeToContent.Height;
            MinHeight = 116;
            MaxHeight = 420;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Title = "Little Tools · Translate";
            LoadLanguagePreference();

            shell = new Border
            {
                CornerRadius = new CornerRadius(15),
                Background = NormalBackground(),
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 13, 16, 13),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 1, Opacity = 0.16, Color = Colors.Black }
            };
            var panel = new StackPanel();
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition());
            languageText = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(124, 237, 174)),
                Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center
            };
            languageText.MouseLeftButtonUp += delegate { OpenLanguageMenu(); };
            header.Children.Add(languageText);
            var hint = new TextBlock
            {
                Text = "SHIFT + BACKSPACE", FontFamily = new FontFamily("Segoe UI"), FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(105, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hint, 1); header.Children.Add(hint);
            panel.Children.Add(header);

            var inputGrid = new Grid();
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition());
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            input = new TextBox
            {
                FontFamily = new FontFamily("Segoe UI"), FontSize = 15, Foreground = Brushes.White,
                CaretBrush = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 5, 5, 5), MinHeight = 36, AcceptsReturn = false
            };
            input.KeyDown += InputKeyDown;
            inputGrid.Children.Add(input);
            placeholder = new TextBlock
            {
                Text = "输入文字，按 Enter 翻译", FontFamily = new FontFamily("Segoe UI"), FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromArgb(105, 255, 255, 255)),
                Margin = new Thickness(2, 5, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top
            };
            inputGrid.Children.Add(placeholder);
            input.TextChanged += delegate { placeholder.Visibility = string.IsNullOrEmpty(input.Text) ? Visibility.Visible : Visibility.Collapsed; };
            var translate = SmallButton("→", 30);
            translate.Click += delegate { BeginTranslate(); };
            Grid.SetColumn(translate, 1); inputGrid.Children.Add(translate);
            panel.Children.Add(inputGrid);

            resultPanel = new Border
            {
                Visibility = Visibility.Collapsed,
                BorderBrush = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
                BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 9, 0, 0),
                Padding = new Thickness(0, 10, 0, 0)
            };
            var resultStack = new StackPanel();
            result = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                MaxHeight = 180, MinHeight = 42, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Foreground = Brushes.White,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0)
            };
            resultStack.Children.Add(result);
            var footer = new Grid { Margin = new Thickness(0, 9, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            var state = new TextBlock
            {
                Text = config.IsConfigured ? "百度翻译已连接" : "尚未配置 API",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center
            };
            footer.Children.Add(state);
            var copy = SmallButton("复制", 52); copy.Click += delegate { CopyResult(); };
            Grid.SetColumn(copy, 1); footer.Children.Add(copy);
            var hide = SmallButton("隐藏", 52); hide.Margin = new Thickness(6, 0, 0, 0); hide.Click += delegate { HideWindow(); };
            Grid.SetColumn(hide, 2); footer.Children.Add(hide);
            resultStack.Children.Add(footer);
            resultPanel.Child = resultStack;
            panel.Children.Add(resultPanel);

            shell.Child = panel;
            Content = shell;
            languageMenu = CreateLanguageMenu();
            UpdateLanguageLabel();
            edgeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
            edgeHideTimer.Tick += delegate { edgeHideTimer.Stop(); if (edgeHideEnabled && hiddenEdge != 0 && !IsMouseOver) HideToEdge(); };
            MouseEnter += delegate { edgeHideTimer.Stop(); shell.Background = HoverBackground(); RevealFromEdge(); };
            MouseLeave += delegate { shell.Background = NormalBackground(); if (edgeHideEnabled && hiddenEdge != 0) edgeHideTimer.Start(); };
            MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                RevealFromEdge();
                try { DragMove(); } catch { }
                SnapOrHideAtEdge();
                args.Handled = true;
            };
            SourceInitialized += delegate { ApplyToolWindowStyle(); };
            Closed += delegate { edgeHideTimer.Stop(); translator.Dispose(); };
        }

        private void InputKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.Enter) { args.Handled = true; BeginTranslate(); }
            else if (args.Key == Key.Escape) { args.Handled = true; HideWindow(); }
        }

        private async void BeginTranslate()
        {
            if (translating) return;
            string text = (input.Text ?? "").Trim();
            if (text.Length == 0) return;
            translating = true;
            resultPanel.Visibility = Visibility.Visible;
            result.Text = "正在翻译…";
            try
            {
                string actualFrom = from;
                string actualTo = to;
                if (from == "auto" && ContainsChinese(text))
                {
                    actualFrom = "zh";
                    actualTo = to == "zh" ? "en" : to;
                }
                result.Text = await translator.TranslateAsync(text, actualFrom, actualTo);
            }
            catch (Exception exception) { result.Text = exception.Message; }
            finally { translating = false; }
        }

        private static bool ContainsChinese(string text)
        {
            foreach (char value in text) if (value >= 0x4E00 && value <= 0x9FFF) return true;
            return false;
        }

        private ContextMenu CreateLanguageMenu()
        {
            var menu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 24, 27, 35)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                PlacementTarget = languageText
            };
            foreach (LanguagePair pair in LanguagePairs.All)
            {
                var item = new MenuItem
                {
                    Header = LanguageName(pair.From) + " → " + LanguageName(pair.To), Tag = pair,
                    Foreground = Brushes.White, Background = Brushes.Transparent, Padding = new Thickness(10, 5, 14, 5)
                };
                item.Click += delegate(object sender, RoutedEventArgs args)
                {
                    var selected = (LanguagePair)((MenuItem)sender).Tag;
                    from = selected.From; to = selected.To;
                    UpdateLanguageLabel(); SaveLanguagePreference();
                };
                menu.Items.Add(item);
            }
            return menu;
        }

        private void OpenLanguageMenu() { languageMenu.PlacementTarget = languageText; languageMenu.IsOpen = true; }
        private void UpdateLanguageLabel() { languageText.Text = LanguageName(from) + "  →  " + LanguageName(to) + "  ▾"; }

        private static string LanguageName(string code)
        {
            switch (code)
            {
                case "auto": return "自动检测"; case "zh": return "中文"; case "en": return "英语";
                case "jp": return "日语"; case "kor": return "韩语"; case "fra": return "法语";
                case "de": return "德语"; case "ru": return "俄语"; default: return code;
            }
        }

        private static string PreferencePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "TranslateApp", "language.txt"); }
        }

        private void LoadLanguagePreference()
        {
            try
            {
                if (!File.Exists(PreferencePath)) return;
                string[] parts = File.ReadAllText(PreferencePath, Encoding.UTF8).Split('|');
                if (parts.Length == 2 && LanguagePairs.Contains(parts[0], parts[1])) { from = parts[0]; to = parts[1]; }
            }
            catch { }
        }

        private void SaveLanguagePreference()
        {
            try
            {
                AtomicFile.WriteUtf8(PreferencePath, from + "|" + to);
            }
            catch { }
        }

        private void CopyResult() { try { if (!string.IsNullOrEmpty(result.Text)) Clipboard.SetText(result.Text); } catch { } }

        public void ShowWindow()
        {
            if (!positioned)
            {
                Rect area = SystemParameters.WorkArea;
                Left = area.Left + (area.Width - Width) / 2;
                Top = area.Top + 82;
                positioned = true;
            }
            Show(); Activate(); input.Focus(); input.SelectAll();
        }

        public void HideWindow() { edgeHideTimer.Stop(); Hide(); }

        private void SnapOrHideAtEdge()
        {
            const double snapDistance = 28;
            Rect work = SystemParameters.WorkArea;
            Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - ActualHeight));
            if (!edgeHideEnabled)
            {
                hiddenEdge = 0;
                Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
            }
            else if (Left <= work.Left + snapDistance)
            {
                hiddenEdge = -1; revealedLeft = work.Left; HideToEdge();
            }
            else if (Left + Width >= work.Right - snapDistance)
            {
                hiddenEdge = 1; revealedLeft = work.Right - Width; HideToEdge();
            }
            else
            {
                hiddenEdge = 0; Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
            }
        }

        private void RevealFromEdge() { if (hiddenEdge != 0) Left = revealedLeft; }
        private void HideToEdge()
        {
            if (!edgeHideEnabled) return;
            Rect work = SystemParameters.WorkArea;
            Left = hiddenEdge < 0 ? work.Left - Width + 9 : work.Right - 9;
        }

        public void SetEdgeHideEnabled(bool enabled)
        {
            edgeHideEnabled = enabled;
            edgeHideTimer.Stop();
            if (!enabled) { RevealFromEdge(); hiddenEdge = 0; }
        }
        private void ApplyToolWindowStyle()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExToolWindow);
        }

        private static Button SmallButton(string text, double width)
        {
            return new Button
            {
                Content = text, Width = width, Height = 27, FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
                Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), BorderThickness = new Thickness(1)
            };
        }

        private static Brush NormalBackground() { return new SolidColorBrush(Color.FromArgb(72, 17, 20, 27)); }
        private static Brush HoverBackground() { return new SolidColorBrush(Color.FromArgb(122, 17, 20, 27)); }
    }

    internal sealed class LanguagePair
    {
        public string From;
        public string To;
        public LanguagePair(string from, string to) { From = from; To = to; }
    }

    internal static class LanguagePairs
    {
        public static readonly LanguagePair[] All =
        {
            new LanguagePair("auto", "zh"), new LanguagePair("zh", "en"), new LanguagePair("zh", "jp"),
            new LanguagePair("zh", "kor"), new LanguagePair("en", "zh"), new LanguagePair("jp", "zh"),
            new LanguagePair("kor", "zh"), new LanguagePair("en", "jp"), new LanguagePair("en", "kor"),
            new LanguagePair("fra", "zh"), new LanguagePair("de", "zh"), new LanguagePair("ru", "zh")
        };
        public static bool Contains(string from, string to)
        {
            foreach (LanguagePair pair in All) if (pair.From == from && pair.To == to) return true;
            return false;
        }
    }

    internal sealed class ApiConfig
    {
        public string AppId;
        public string SecretKey;
        public string SourcePath;
        public bool IsConfigured { get { return !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(SecretKey); } }

        public static ApiConfig Load()
        {
            string appId = Environment.GetEnvironmentVariable("BAIDU_TRANSLATE_APP_ID");
            string secret = Environment.GetEnvironmentVariable("BAIDU_TRANSLATE_SECRET_KEY");
            if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(secret))
                return new ApiConfig { AppId = appId.Trim(), SecretKey = secret.Trim(), SourcePath = "环境变量" };

            string executableDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var candidates = new List<string>();
            string customPath = Environment.GetEnvironmentVariable("TRANSLATE_APP_CONFIG");
            if (!string.IsNullOrWhiteSpace(customPath)) candidates.Add(customPath);
            candidates.Add(Path.Combine(executableDir, "appsettings.json"));
            DirectoryInfo parent = Directory.GetParent(executableDir);
            if (parent != null) candidates.Add(Path.Combine(parent.FullName, "appsettings.json"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "TranslateApp", "appsettings.json"));
            string driveRoot = Path.GetPathRoot(executableDir);
            if (!string.IsNullOrEmpty(driveRoot)) candidates.Add(Path.Combine(driveRoot, "TranslateApp", "appsettings.json"));
            foreach (string path in candidates)
            {
                ApiConfig loaded = TryLoad(path);
                if (loaded != null && loaded.IsConfigured) return loaded;
            }
            return new ApiConfig { SourcePath = Path.Combine(executableDir, "appsettings.json") };
        }

        private static ApiConfig TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                if (root == null) return null;
                object appId; object secret;
                root.TryGetValue("AppId", out appId); root.TryGetValue("SecretKey", out secret);
                return new ApiConfig
                {
                    AppId = appId == null ? null : Convert.ToString(appId),
                    SecretKey = secret == null ? null : Convert.ToString(secret), SourcePath = path
                };
            }
            catch { return null; }
        }
    }

    internal sealed class TranslateService : IDisposable
    {
        private readonly ApiConfig config;
        private readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private bool disposed;
        public TranslateService(ApiConfig config)
        {
            this.config = config;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public async Task<string> TranslateAsync(string text, string from, string to)
        {
            if (!config.IsConfigured)
                throw new InvalidOperationException("尚未配置百度翻译 API。请在程序目录创建 appsettings.json。\n参考 appsettings.example.json");
            string salt = DateTime.UtcNow.Ticks.ToString();
            string sign = Md5(config.AppId + text + salt + config.SecretKey);
            var fields = new Dictionary<string, string>
            {
                { "q", text }, { "from", from }, { "to", to }, { "appid", config.AppId }, { "salt", salt }, { "sign", sign }
            };
            using (var content = new FormUrlEncodedContent(fields))
            using (HttpResponseMessage response = await client.PostAsync("https://fanyi-api.baidu.com/api/trans/vip/translate", content))
            {
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("翻译服务连接失败：HTTP " + (int)response.StatusCode);
                Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>;
                if (root == null) throw new InvalidOperationException("翻译服务返回了无法识别的数据");
                object errorCode;
                if (root.TryGetValue("error_code", out errorCode)) throw new InvalidOperationException(FriendlyApiError(Convert.ToString(errorCode)));
                object rawResults;
                if (!root.TryGetValue("trans_result", out rawResults)) throw new InvalidOperationException("翻译服务没有返回结果");
                object[] results = rawResults as object[];
                if (results == null || results.Length == 0) throw new InvalidOperationException("翻译结果为空");
                var lines = new List<string>();
                foreach (object raw in results)
                {
                    var item = raw as Dictionary<string, object>; object translated;
                    if (item != null && item.TryGetValue("dst", out translated) && translated != null) lines.Add(Convert.ToString(translated));
                }
                if (lines.Count == 0) throw new InvalidOperationException("翻译结果为空");
                return string.Join(Environment.NewLine, lines.ToArray());
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            client.Dispose();
        }

        private static string Md5(string value)
        {
            using (MD5 algorithm = MD5.Create())
            {
                byte[] hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value));
                var output = new StringBuilder(hash.Length * 2);
                foreach (byte item in hash) output.Append(item.ToString("x2"));
                return output.ToString();
            }
        }

        private static string FriendlyApiError(string code)
        {
            switch (code)
            {
                case "52001": return "翻译请求超时，请重试"; case "52002": return "百度翻译系统错误，请稍后重试";
                case "52003": return "百度翻译 AppId 无效"; case "54001": return "百度翻译密钥或签名无效";
                case "54003": return "翻译请求过于频繁，请稍后重试"; case "54004": return "百度翻译账户余额不足";
                case "58001": return "当前翻译方向暂不支持"; default: return "百度翻译错误：" + code;
            }
        }
    }
}
