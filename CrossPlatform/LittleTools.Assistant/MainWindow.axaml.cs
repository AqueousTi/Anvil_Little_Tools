using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Styling;
using LittleTools.Assistant.Platform;
using LittleTools.Assistant.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LittleTools.Assistant;

public sealed partial class MainWindow : Window
{
    private const double CapsuleHeight = 80;
    private static readonly string[] ChatHints =
    [
        "问点什么？", "今天想学点什么？", "有什么好奇的？", "来，聊个新想法", "遇到难题了？说来听听",
        "想弄懂哪个小知识？", "脑袋里的问号，交给我吧", "今天又发现了什么？", "有个想法想一起琢磨？",
        "随便问问，也许有惊喜", "从一个小问题开始吧", "想把什么讲明白？", "有什么想不通的？",
        "来探索一个新知识点", "今天的好奇心放这里", "想学个新技能吗？", "这段代码哪里卡住了？",
        "碰到报错了？一起看看", "截张图，一起研究一下", "哪个命令让你困惑了？", "想听个简单的解释？",
        "复杂的问题，慢慢拆开聊", "需要一个小例子吗？", "把灵光一闪记在这里", "有个“为什么”想问？",
        "想试试另一种思路？", "今天想搞懂什么原理？", "来点学习的小灵感", "一句话也能开始探索",
        "你的下一个问题是什么？", "想了解点不一样的？", "问题不分大小，尽管问", "好奇一下，又不会怎样", "一起把问号变成感叹号"
    ];
    private int _lastChatHint = -1;

    private string NextChatHint()
    {
        var index = Random.Shared.Next(ChatHints.Length - 1);
        if (index >= _lastChatHint && _lastChatHint >= 0) index++;
        _lastChatHint = index;
        return ChatHints[index];
    }
    private readonly SettingsStore _settingsStore;
    private readonly ConversationStore _conversationStore;
    private readonly IScreenshotService _screenshotService;
    private Conversation _conversation = new();
    private AssistantMode _mode = AssistantMode.Translate;
    private CancellationTokenSource? _requestCancellation;
    private byte[]? _pendingImage;
    private Bitmap? _chatAttachmentBitmap;
    private bool _settingsDialogOpen;
    private string? _lastWindowShape;
    private int _activationVersion;
    private bool _loaded;
    private bool _expanded;
    private bool _captureInProgress;
    private readonly List<Bitmap> _translationBitmaps = [];
    private ContextMenu? _translationRouteMenu;

    public MainWindow() : this(new SettingsStore(), new ConversationStore(), ScreenshotServiceFactory.Create()) { }

    internal MainWindow(SettingsStore settingsStore, ConversationStore conversationStore, IScreenshotService screenshotService)
    {
        _settingsStore = settingsStore;
        _conversationStore = conversationStore;
        _screenshotService = screenshotService;
        AvaloniaXamlLoader.Load(this);
        WireEvents();
        ApplySettings();
        if (!App.SmokeTest) TranslationImageCache.Clear();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && !IsVisible && !_captureInProgress)
            {
                if (_mode != AssistantMode.Chat) _requestCancellation?.Cancel();
                ClearTranslationImages();
                ClearChatImages();
            }
        };
        Closed += (_, _) => { ClearTranslationImages(); ClearChatImages(); };
        Opened += (_, _) => ApplyNativeWindowShape();
        SizeChanged += (_, _) => ApplyNativeWindowShape();
        LayoutUpdated += (_, _) => ApplyNativeWindowShape();
        Deactivated += (_, _) =>
        {
            var version = _activationVersion;
            Dispatcher.UIThread.Post(() =>
            {
                if (version == _activationVersion && !App.SmokeTest && IsVisible && !IsActive && !_captureInProgress && !_settingsDialogOpen
                    && _translationRouteMenu?.IsOpen != true && !Find<ComboBox>("ProviderSelector").IsDropDownOpen)
                    Hide();
            }, DispatcherPriority.Background);
        };
        Opened += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await RefreshHistoryAsync();
        };
    }

    public void ShowTranslation()
    {
        if (_requestCancellation is not null || _captureInProgress) { ShowAndFocus(); return; }
        _ = SaveCurrentAsync();
        _conversation = new Conversation { Mode = AssistantMode.Translate };
        _pendingImage = null;
        SetMode(AssistantMode.Translate);
        ConfigureComponentLayout(AssistantMode.Translate);
        Find<StackPanel>("MessagesPanel").Children.Clear();
        CollapseForWake();
        ShowAndFocus();
    }

    public void ShowChat()
    {
        if (_requestCancellation is not null || _captureInProgress) { ShowAndFocus(); return; }
        _ = SaveCurrentAsync();
        _conversation = new Conversation { Mode = AssistantMode.Chat };
        _pendingImage = null;
        SetMode(AssistantMode.Chat);
        ConfigureComponentLayout(AssistantMode.Chat);
        Find<StackPanel>("MessagesPanel").Children.Clear();
        CollapseForWake();
        ShowAndFocus();
    }

    public void ShowNewConversation() => ShowChat();

    internal void SaveRender(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var scale = RenderScaling;
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        using var stream = File.Create(path);
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    internal async Task RunTranslationSmokeAsync(string? screenshotPath, string outputPath)
    {
        ShowTranslation();
        if (screenshotPath is not null) _pendingImage = File.ReadAllBytes(screenshotPath);
        var originalRoute = _settingsStore.Current.TranslationRouteId;
        try
        {
            _settingsStore.Current.TranslationRouteId = "auto-zh";
            await SendTranslationAsync("Please open the terminal.", screenshotPath is null ? "Please open the terminal." : "截图翻译测试");
        }
        finally { _settingsStore.Current.TranslationRouteId = originalRoute; }
        if (!Find<TextBlock>("StatusText").Text!.EndsWith("· 完成", StringComparison.Ordinal))
            throw new InvalidOperationException(_conversation.Messages.LastOrDefault()?.Content ?? "Translation did not complete.");
        var result = _conversation.Messages.Last();
        if (result.Role != "assistant" || !TranslationRoutes.ContainsChinese(result.Content))
            throw new InvalidOperationException("Translation UI did not receive a Chinese answer.");
        if (screenshotPath is not null && (result.ImagePath is null || !File.Exists(result.ImagePath)))
            throw new InvalidOperationException("Screenshot UI did not receive its translated image.");
        UpdateLayout();
        var toolbar = Find<StackPanel>("TranslationToolbar");
        var send = Find<Button>("SendButton");
        var grid = Find<Grid>("ComposerGrid");
        if (!toolbar.IsVisible || !Find<Button>("CaptureButton").IsVisible || toolbar.Bounds.Top < Find<TextBox>("Composer").Bounds.Bottom)
            throw new InvalidOperationException("Translation toolbar is not below the input.");
        if (Math.Abs(send.Bounds.Center.Y - grid.Bounds.Height / 2) > 2)
            throw new InvalidOperationException("Send button is not vertically centered.");
        var composerBottom = grid.TranslatePoint(new Point(0, grid.Bounds.Height), this);
        if (composerBottom is null || composerBottom.Value.Y > Bounds.Height)
            throw new InvalidOperationException("Composer extends outside the translation window.");
        SaveRender(outputPath);
        var resultTop = Find<Grid>("ConversationArea").TranslatePoint(new Point(0, 0), this);
        if (resultTop is null || resultTop.Value.Y < composerBottom.Value.Y)
            throw new InvalidOperationException("Translation result is not below the composer.");
        if (screenshotPath is not null)
        {
            // Cover typing directly after a screenshot opened via its hotkey.
            SetMode(AssistantMode.Screenshot);
            ConfigureComponentLayout(AssistantMode.Screenshot);
            Find<TextBox>("Composer").Text = "Please open the terminal.";
            await SendComposerAsync();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            UpdateLayout();
            var scroll = Find<ScrollViewer>("MessageScroll");
            var messages = Find<StackPanel>("MessagesPanel");
            SaveRender(outputPath + ".after-text.png");
            if (_mode != AssistantMode.Translate || _conversation.Mode != AssistantMode.Translate
                || scroll.Viewport.Height < messages.Bounds.Height - 1 || scroll.Offset.Y > 1
                || !_conversation.Messages.Last().Content.Contains("终端", StringComparison.Ordinal))
                throw new InvalidOperationException($"Screenshot-to-text failed: mode={_mode}; viewport={scroll.Viewport}; result={messages.Bounds}; offset={scroll.Offset}.");
        }
        if (screenshotPath is null && Height >= 250)
            throw new InvalidOperationException("Short translation has excess window height.");
        Hide();
        if (_translationBitmaps.Count != 0)
            throw new InvalidOperationException("Hiding did not release screenshot resources.");
    }

    internal async Task RunLayoutSmokeAsync(string path)
    {
        ShowTranslation(); UpdateLayout();
        var capsule = Find<Border>("ComposerBar");
        var original = capsule.Bounds;
        SaveRender(path + ".capsule.png");
        ExpandForContent();
        AddMessageBubble(new ConversationMessage { Role = "assistant", Content = "这是一段翻译结果。" });
        UpdateWindowLayout(); UpdateLayout();
        if (capsule.Bounds != original || capsule.CornerRadius.TopLeft != capsule.Bounds.Height / 2)
            throw new InvalidOperationException("Capsule changed shape or position when expanded.");
        SaveRender(path + ".translation.png");
        ShowChat(); UpdateLayout();
        var main = Find<Border>("WindowShell").Bounds;
        SaveRender(path + ".chat.png");
        foreach (var name in new[] { "HistoryToggle", "OptionsToggle" })
        {
            Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UpdateLayout();
            var panel = Find<Border>(name == "HistoryToggle" ? "HistoryPanel" : "AdvancedPanel");
            if (!panel.IsVisible || panel.Bounds.X <= main.Right || Find<Border>("WindowShell").Bounds != main)
                throw new InvalidOperationException("Sidebar is not positioned independently to the right.");
            SaveRender(path + "." + name + ".png");
        }
        ShowChat();
        if (!double.IsNaN(Find<ScrollViewer>("MessageScroll").Height))
            throw new InvalidOperationException("Collapsed chat retained an explicit message height.");
        ExpandForContent();
        Find<StackPanel>("MessagesPanel").Children.Clear();
        var streaming = AddStreamingBubble(new ConversationMessage { Role = "assistant" });
        streaming.Text = string.Join('\n', Enumerable.Range(1, 40)
            .Select(index => $"第 {index:00} 行：这是用于验证快问长回答滚动布局的测试文字。"));
        UpdateWindowLayout();
        ScrollToBottom();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        UpdateLayout();
        var layout = Find<Grid>("AssistantLayout");
        var scroll = Find<ScrollViewer>("MessageScroll");
        var shell = Find<Border>("WindowShell");
        var composer = Find<Border>("ComposerBar");
        var rowHeights = layout.RowDefinitions.Select(row => row.ActualHeight).ToArray();
        var composerBottom = composer.TranslatePoint(new Point(0, composer.Bounds.Height), shell)?.Y ?? double.NaN;
        File.WriteAllText(path + ".chat-long.metrics.txt",
            $"Rows=[{string.Join(", ", rowHeights.Select(value => value.ToString("0.###")))}]\n"
            + $"RowSum={rowHeights.Sum():0.###}; GridHeight={layout.Bounds.Height:0.###}\n"
            + $"AssignedHeight={scroll.Height:0.###}; ExtentHeight={scroll.Extent.Height:0.###}; ViewportHeight={scroll.Viewport.Height:0.###}\n"
            + $"ComposerBottom={composerBottom:0.###}; ShellHeight={shell.Bounds.Height:0.###}\n",
            Encoding.UTF8);
        SaveRender(path + ".chat-long.png");
        if (scroll.Viewport.Height >= scroll.Extent.Height)
            throw new InvalidOperationException($"Long chat is not scrollable: viewport={scroll.Viewport.Height:0.###}; extent={scroll.Extent.Height:0.###}.");
        if (double.IsNaN(composerBottom) || composerBottom > shell.Bounds.Height + 0.5)
            throw new InvalidOperationException($"Composer is outside the shell: bottom={composerBottom:0.###}; shell={shell.Bounds.Height:0.###}.");
        if (rowHeights.Sum() > layout.Bounds.Height + 0.5)
            throw new InvalidOperationException($"Chat rows exceed the layout: rows={rowHeights.Sum():0.###}; grid={layout.Bounds.Height:0.###}.");
        ShowChat();
        var inputBox = Find<TextBox>("Composer");
        inputBox.Text = "first";
        inputBox.CaretIndex = 5;
        inputBox.SelectionStart = inputBox.SelectionEnd = 5;
        inputBox.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Shift });
        if (inputBox.Text != "first\n") throw new InvalidOperationException("Shift+Enter did not insert a newline.");
        inputBox.Text = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"Input line {i}"));
        inputBox.CaretIndex = inputBox.Text.Length;
        UpdateWindowLayout(); UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(100);
        UpdateWindowLayout(); UpdateLayout();
        var inputScroll = inputBox.GetVisualDescendants().OfType<ScrollViewer>().First();
        if (inputScroll.Offset.Y <= 0)
            throw new InvalidOperationException("Caret did not scroll into view after typing.");
        inputScroll.ScrollToHome(); UpdateLayout();
        inputScroll.ScrollToEnd(); UpdateLayout();
        if (inputScroll.Extent.Height <= inputScroll.Viewport.Height || inputScroll.Offset.Y <= 0)
            throw new InvalidOperationException("Long input cannot scroll to the last line.");
        var inputBottom = inputBox.TranslatePoint(new Point(0, inputBox.Bounds.Height), Find<Border>("WindowShell"))!.Value.Y;
        if (inputBottom > Find<Border>("WindowShell").Bounds.Height)
            throw new InvalidOperationException($"Long input overflows the collapsed window: bottom={inputBottom}, shell={Find<Border>("WindowShell").Bounds.Height}, desired={inputBox.DesiredSize.Height}, bounds={inputBox.Bounds}, window={Height}.");
        SaveRender(path + ".input-scroll.png");
        inputBox.Text = "";
        var other = new Window { Width = 100, Height = 80, ShowInTaskbar = false };
        try
        {
            App.SmokeTest = false;
            _captureInProgress = true;
            other.Show(); other.Activate();
            // The focus is granted by the window manager and any other window on
            // the display (a second Little Tools instance, a browser the user just
            // clicked) can take it away mid-check, so the transitions are awaited
            // and a desktop that never hands the focus over is reported as such
            // instead of looking like a product regression.
            if (!await SmokeWaiter.WaitAsync(() => !IsActive, TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException(
                    "Layout smoke could not run the click-away check: the window never lost the focus, so the "
                    + "deactivation path was not exercised. Another window on this display is holding the focus "
                    + "(another Little Tools instance running?). state: IsVisible=" + IsVisible + ", IsActive=" + IsActive);
            await SmokeWaiter.PumpAsync();
            if (!IsVisible) throw new InvalidOperationException("Capture lost its owner window.");
            _captureInProgress = false;
            if (!await ActivateForSmokeAsync())
                throw new InvalidOperationException(
                    "Layout smoke could not run the click-away check: the window never regained the focus after the "
                    + "capture step, so the click-away could not be exercised. state: IsVisible=" + IsVisible
                    + ", IsActive=" + IsActive);
            other.Activate();
            if (!await SmokeWaiter.WaitAsync(() => !IsVisible, TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException(
                    "Click-away activation did not hide the window. state: IsVisible=" + IsVisible
                    + ", IsActive=" + IsActive + ", captureInProgress=" + _captureInProgress);
        }
        finally { App.SmokeTest = true; _captureInProgress = false; other.Close(); }
        ShowTranslation();
        RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape });
        if (IsVisible) throw new InvalidOperationException("Escape did not hide the window.");
    }

    /// <summary>
    /// Asks the window manager for the focus, retrying while the desktop may still
    /// be handing it to the window the smoke just activated. Returns false instead
    /// of throwing so the caller can report a diagnosable desktop conflict.
    /// </summary>
    private async Task<bool> ActivateForSmokeAsync()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Activate();
            if (await SmokeWaiter.WaitAsync(() => IsActive, TimeSpan.FromMilliseconds(700))) return true;
        }
        return false;
    }

    public async void ShowForScreenshot()
    {
        if (_requestCancellation is not null || _captureInProgress) { ShowAndFocus(); return; }
        _ = SaveCurrentAsync();
        _conversation = new Conversation { Mode = AssistantMode.Screenshot };
        _pendingImage = null;
        SetMode(AssistantMode.Screenshot);
        ConfigureComponentLayout(AssistantMode.Screenshot);
        Find<StackPanel>("MessagesPanel").Children.Clear();
        ShowAndFocus();
        await CaptureAndTranslateAsync();
    }

    internal async Task RunChatImageSmokeAsync(string screenshotPath, string outputPath)
    {
        var settings = _settingsStore.Current;
        var originalProvider = settings.Provider;
        var originalSearch = settings.Search;
        try
        {
            settings.Search = SearchPolicy.Off;
            foreach (var provider in new[] { ProviderKind.Glm, ProviderKind.DeepSeek })
            {
                if (string.IsNullOrWhiteSpace(_settingsStore.ResolveKey(provider))) continue;
                settings.Provider = provider;
                ShowChat();
                SetChatAttachment(File.ReadAllBytes(screenshotPath));
                UpdateLayout();
                var attachment = Find<Border>("ChatAttachment");
                var bottom = attachment.TranslatePoint(new Point(0, attachment.Bounds.Height), this);
                if (!attachment.IsVisible || bottom is null || bottom.Value.Y > Bounds.Height)
                    throw new InvalidOperationException("Screenshot preview extends outside chat window.");
                SaveRender(outputPath + ".preview.png");
                SetChatAttachment(null);
                if (_pendingImage is not null || _chatAttachmentBitmap is not null) throw new InvalidOperationException("Attachment removal failed.");
                SetChatAttachment(File.ReadAllBytes(screenshotPath));
                Find<TextBox>("Composer").Text = "";
                await SendComposerAsync();
                if (!Find<TextBlock>("StatusText").Text!.Contains("完成", StringComparison.Ordinal))
                    throw new InvalidOperationException(provider + ": " + _conversation.Messages.Last().Content);
                Find<TextBox>("Composer").Text = "刚才图片的第一行英文是什么？只输出图片中的英文原文。";
                await SendComposerAsync();
                if (!_conversation.Messages.Last().Content.Contains("Settings", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(provider + " image follow-up failed: " + _conversation.Messages.Last().Content);
                UpdateLayout();
                SaveRender(outputPath + "." + provider + ".png");
                Hide();
                if (_pendingImage is not null || _chatAttachmentBitmap is not null || _conversation.Messages.Any(message => message.ImageBytes is not null))
                    throw new InvalidOperationException("Hiding retained chat images.");
                File.AppendAllText(outputPath + ".results.txt", provider + " image and follow-up OK\n");
            }
        }
        finally { settings.Provider = originalProvider; settings.Search = originalSearch; }
    }

    private void WireEvents()
    {
        var shell = Find<Border>("WindowShell");
        Find<Button>("HistoryToggle").Content = MakeIcon("M 8,1 A 7,7 0 1 1 7.99,1 M 8,4 L 8,8 L 11,10");
        Find<Button>("OptionsToggle").Content = MakeIcon("M 6,1 L 10,1 L 10.5,3 L 12,4 L 14,3.5 L 16,7 L 14.5,8.5 L 14.5,10 L 16,11.5 L 14,15 L 12,14.5 L 10.5,15.5 L 10,17.5 L 6,17.5 L 5.5,15.5 L 4,14.5 L 2,15 L 0,11.5 L 1.5,10 L 1.5,8.5 L 0,7 L 2,3.5 L 4,4 L 5.5,3 Z M 11,9 A 3,3 0 1 1 5,9 A 3,3 0 1 1 11,9");
        Find<Button>("CaptureButton").Content = MakeIcon("M 1,6 L 1,1 L 6,1 M 10,1 L 15,1 L 15,6 M 15,10 L 15,15 L 10,15 M 6,15 L 1,15 L 1,10", 13);
        foreach (var name in new[] { "WindowShell", "ComposerBar", "ResultCard", "HistoryPanel", "AdvancedPanel", "HistoryToggle", "OptionsToggle" })
        {
            Find<Control>(name).PointerEntered += (_, _) => ApplySurfaceColors();
            Find<Control>(name).PointerExited += (_, _) => ApplySurfaceColors();
        }
        PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (args.GetPosition(this).Y <= 12) BeginMoveDrag(args);
        };
        Find<Button>("HistoryToggle").Click += (_, _) =>
        {
            var panel = Find<Border>("HistoryPanel");
            panel.IsVisible = !panel.IsVisible;
            Find<Border>("AdvancedPanel").IsVisible = false;
            UpdateWindowLayout();
        };
        Find<Button>("OptionsToggle").Click += (_, _) =>
        {
            var panel = Find<Border>("AdvancedPanel");
            panel.IsVisible = !panel.IsVisible;
            Find<Border>("HistoryPanel").IsVisible = false;
            UpdateWindowLayout();
        };
        Find<Border>("TitleBar").PointerPressed += (_, args) =>
        {
            if (args.Source is Visual visual
                && (visual is Button || visual.GetVisualAncestors().OfType<Button>().Any())) return;
            if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Hide();
        };
        Find<Button>("NewConversationButton").Click += (_, _) => ShowNewConversation();
        Find<Button>("TranslateMode").Click += (_, _) => SwitchMode(AssistantMode.Translate);
        Find<Button>("ChatMode").Click += (_, _) => SwitchMode(AssistantMode.Chat);
        _translationRouteMenu = CreateTranslationRouteMenu();
        Find<Button>("TranslationRouteButton").Click += (_, _) =>
            _translationRouteMenu.Open(Find<Button>("TranslationRouteButton"));
        Find<Button>("CompactTranslationRouteButton").Click += (_, _) =>
            _translationRouteMenu.Open(Find<Button>("CompactTranslationRouteButton"));
        Find<Button>("ScreenshotMode").Click += async (_, _) =>
        {
            SwitchMode(AssistantMode.Screenshot);
            await CaptureAndTranslateAsync();
        };
        Find<Button>("CaptureButton").Click += async (_, _) =>
        {
            if (_mode == AssistantMode.Chat) await CaptureForChatAsync();
            else await CaptureAndTranslateAsync();
        };
        Find<Button>("RemoveChatAttachment").Click += (_, _) => SetChatAttachment(null);
        Find<Button>("SendButton").Click += async (_, _) => await SendComposerAsync();
        Find<Button>("StopButton").Click += (_, _) => _requestCancellation?.Cancel();
        Find<Button>("SettingsButton").Click += async (_, _) =>
        {
            await OpenSettingsAsync();
        };
        Find<ComboBox>("ProviderSelector").SelectionChanged += (_, _) =>
        {
            var settings = _settingsStore.Current;
            settings.Provider = Find<ComboBox>("ProviderSelector").SelectedIndex == 1 ? ProviderKind.DeepSeek : ProviderKind.Glm;
            _settingsStore.Save(settings);
            UpdateStatus();
        };
        Find<Button>("ThinkingToggle").Click += (_, _) =>
        {
            var settings = _settingsStore.Current;
            settings.DeepThinking = !settings.DeepThinking;
            _settingsStore.Save(settings);
            ApplySettings();
        };
        Find<Button>("SearchToggle").Click += (_, _) =>
        {
            var settings = _settingsStore.Current;
            settings.Search = settings.Search switch
            {
                SearchPolicy.Auto => SearchPolicy.On,
                SearchPolicy.On => SearchPolicy.Off,
                _ => SearchPolicy.Auto
            };
            _settingsStore.Save(settings);
            ApplySettings();
        };
        var composer = Find<TextBox>("Composer");
        composer.TextChanged += (_, _) =>
        {
            Find<TextBlock>("ComposerPlaceholder").IsVisible = string.IsNullOrEmpty(composer.Text);
            Dispatcher.UIThread.Post(UpdateWindowLayout, DispatcherPriority.Background);
        };
        composer.SizeChanged += (_, _) =>
            Dispatcher.UIThread.Post(UpdateWindowLayout, DispatcherPriority.Background);
        composer.AddHandler(InputElement.KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void ApplySettings()
    {
        var settings = _settingsStore.Current;
        Find<ComboBox>("ProviderSelector").SelectedIndex = settings.Provider == ProviderKind.DeepSeek ? 1 : 0;
        var routeLabel = TranslationRoutes.Find(settings.TranslationRouteId).Label + "  ▾";
        Find<Button>("TranslationRouteButton").Content = routeLabel;
        Find<Button>("CompactTranslationRouteButton").Content = routeLabel;
        Find<Button>("ThinkingToggle").Content = settings.DeepThinking ? "深入" : "快速";
        Find<Button>("SearchToggle").Content = settings.Search switch
        {
            SearchPolicy.On => "联网·开",
            SearchPolicy.Off => "联网·关",
            _ => "联网·自动"
        };
        UpdateStatus();
    }

    private void SetMode(AssistantMode mode)
    {
        _mode = mode;
        _conversation.Mode = mode;
        SetActive("TranslateMode", mode == AssistantMode.Translate);
        SetActive("ChatMode", mode == AssistantMode.Chat);
        SetActive("ScreenshotMode", mode == AssistantMode.Screenshot);
        Find<Button>("TranslationRouteButton").IsVisible = mode == AssistantMode.Translate;
        Find<TextBlock>("ComposerPlaceholder").Text = mode switch
        {
            AssistantMode.Translate => "输入文字，Enter 翻译 · Shift+Enter 换行",
            AssistantMode.Screenshot => "点击截图，框选需要翻译的区域",
            _ => NextChatHint()
        };
        UpdateStatus();
    }

    private void SwitchMode(AssistantMode mode)
    {
        if (_requestCancellation is not null) return;
        _ = SaveCurrentAsync();
        _conversation = new Conversation { Mode = mode };
        _pendingImage = null;
        Find<StackPanel>("MessagesPanel").Children.Clear();
        SetMode(mode);
        ConfigureComponentLayout(mode);
        if (_expanded) RenderWelcome();
    }

    private void ConfigureComponentLayout(AssistantMode mode)
    {
        var translating = mode != AssistantMode.Chat;
        var chatting = mode == AssistantMode.Chat;
        Find<Grid>("AssistantLayout").RowDefinitions = new RowDefinitions(chatting ? "Auto,Auto,*,Auto,Auto" : "Auto,Auto,Auto,*,Auto");
        Grid.SetRow(Find<Border>("ComposerBar"), chatting ? 3 : 2);
        Grid.SetRow(Find<Border>("ResultCard"), chatting ? 2 : 3);
        Find<Border>("ResultCard").Margin = chatting ? new Thickness(0, 0, 0, 8) : new Thickness(0, 8, 0, 0);
        Find<StackPanel>("HeaderActions").IsVisible = chatting;
        var composer = Find<Border>("ComposerBar");
        composer.Height = chatting ? double.NaN : CapsuleHeight;
        composer.CornerRadius = new CornerRadius(chatting ? 8 : CapsuleHeight / 2);
        composer.Padding = chatting ? new Thickness(0) : new Thickness(28, 8);
        composer.BorderBrush = Brush.Parse("#30FFFFFF");
        composer.BorderThickness = new Thickness(chatting ? 0 : 1);
        Find<TextBox>("Composer").MaxHeight = chatting ? 90 : 29;
        Find<Border>("TitleBar").IsVisible = false;
        Find<StackPanel>("ModeActions").IsVisible = false;
        Find<TextBlock>("ComponentTitle").IsVisible = false;
        ToolTip.SetTip(Find<TextBox>("Composer"), "Enter 发送 · Shift+Enter 换行 · Esc 隐藏");
        Find<TextBlock>("ComponentTitle").Text = chatting ? "快问" : "截图翻译";
        Find<Button>("HistoryToggle").IsVisible = chatting;
        Find<Button>("OptionsToggle").IsVisible = chatting;
        Find<Button>("CompactTranslationRouteButton").IsVisible = translating;
        Find<StackPanel>("TranslationToolbar").IsVisible = true;
        Find<Button>("CaptureButton").IsVisible = true;
        ToolTip.SetTip(Find<Button>("CaptureButton"), chatting ? "框选截图，添加到问题" : "框选屏幕并翻译成中文");
        if (!chatting || _pendingImage is null) SetChatAttachment(null);
        Find<TextBox>("Composer").MinHeight = 29;
        var send = Find<Button>("SendButton");
        send.Content = chatting ? "发送" : new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 0,5 L 12,5 M 7,0 L 12,5 L 7,10"),
            Width = 14, Height = 12, Stretch = Stretch.Uniform,
            Stroke = Brush.Parse("#A8FFD2"), StrokeThickness = 1.6
        };
        send.Width = chatting ? 42 : 32;
        send.FontSize = 12;
        Find<Border>("WindowShell").Padding = translating ? new Thickness(0) : new Thickness(16, 10);
        ApplySurfaceColors();
        Find<Border>("WindowShell").BorderThickness = new Thickness(translating ? 0 : 1);
        if (!chatting) Find<Border>("HistoryPanel").IsVisible = false;
    }

    private void SetActive(string name, bool active)
    {
        var button = Find<Button>(name);
        button.Classes.Set("active", active);
    }

    private async Task SendComposerAsync()
    {
        var composer = Find<TextBox>("Composer");
        var text = (composer.Text ?? string.Empty).Trim();
        if (_requestCancellation is not null || _captureInProgress) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (_mode != AssistantMode.Chat || _pendingImage is null) return;
            text = "请分析这张截图，说明关键内容；如果存在报错，请解释原因并给出处理建议。";
        }
        composer.Text = string.Empty;
        await SendAsync(text, text);
    }

    private async void OnComposerKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter) return;
        args.Handled = true;
        if ((args.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Shift)) != 0)
        {
            InsertComposerNewLine();
            return;
        }
        await SendComposerAsync();
    }

    private void InsertComposerNewLine()
    {
        var composer = Find<TextBox>("Composer");
        var text = composer.Text ?? string.Empty;
        var start = Math.Clamp(Math.Min(composer.SelectionStart, composer.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(composer.SelectionStart, composer.SelectionEnd), start, text.Length);
        composer.Text = text.Remove(start, end - start).Insert(start, "\n");
        composer.CaretIndex = start + 1;
        composer.SelectionStart = composer.CaretIndex;
        composer.SelectionEnd = composer.CaretIndex;
    }

    private async Task CaptureAndTranslateAsync()
    {
        if (_requestCancellation is not null || _captureInProgress) return;
        _captureInProgress = true;
        try
        {
            SetStatus("请选择需要翻译的区域…");
            _pendingImage = await _screenshotService.CaptureRegionAsync(this, CancellationToken.None);
            if (_pendingImage is null)
            {
                SetStatus("已取消截图");
                return;
            }
            await SendAsync("请忠实提取并翻译这张截图。", "📷 截图翻译");
        }
        catch (Exception exception)
        {
            ExpandForContent();
            AddSystemNotice("截图失败：" + exception.Message);
            UpdateStatus();
        }
        finally { _captureInProgress = false; }
    }

    private async Task CaptureForChatAsync()
    {
        if (_requestCancellation is not null || _captureInProgress) return;
        _captureInProgress = true;
        try
        {
            var image = await _screenshotService.CaptureRegionAsync(this, CancellationToken.None);
            if (image is not null) SetChatAttachment(image);
        }
        catch (Exception exception) { ExpandForContent(); AddSystemNotice("截图失败：" + exception.Message); }
        finally { _captureInProgress = false; }
    }

    private void SetChatAttachment(byte[]? image)
    {
        if (_mode == AssistantMode.Chat) _pendingImage = image;
        Find<Image>("ChatAttachmentPreview").Source = null;
        _chatAttachmentBitmap?.Dispose();
        _chatAttachmentBitmap = null;
        if (image is not null)
        {
            using var input = new MemoryStream(image, writable: false);
            _chatAttachmentBitmap = new Bitmap(input);
            Find<Image>("ChatAttachmentPreview").Source = _chatAttachmentBitmap;
        }
        Find<Border>("ChatAttachment").IsVisible = image is not null;
        if (_mode == AssistantMode.Chat) UpdateWindowLayout();
    }

    private void ClearChatImages()
    {
        SetChatAttachment(null);
        foreach (var message in _conversation.Messages) message.ImageBytes = null;
    }

    private async Task SendTranslationAsync(string text, string displayText)
    {
        if (_requestCancellation is not null) return;
        var image = _pendingImage;
        _pendingImage = null;
        ClearTranslationImages();
        var requestMode = image is null ? AssistantMode.Translate : AssistantMode.Screenshot;
        SetMode(requestMode);
        ConfigureComponentLayout(requestMode);
        var credentials = _settingsStore.ResolveBaiduCredentials();
        if (credentials is null)
        {
            ExpandForContent();
            AddSystemNotice("未找到百度翻译配置。请在“中英互译”菜单底部打开“翻译设置”，填写 APPID 和密钥。");
            SetStatus("百度翻译 · 尚未配置");
            return;
        }
        _conversation = new Conversation { Mode = requestMode };
        Find<StackPanel>("MessagesPanel").Children.Clear();
        ExpandForContent();
        var user = new ConversationMessage { Role = "user", Content = displayText };
        _conversation.Messages.Add(user);
        // The composer already contains the input; keep the result area concise.
        var answer = new ConversationMessage { Role = "assistant", Model = image is null ? "百度翻译" : "百度图片翻译" };
        var bubble = AddStreamingBubble(answer);
        bubble.Text = image is null ? "正在翻译…" : "正在识别并翻译截图…";
        using var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        SetSending(true);
        try
        {
            var service = new BaiduTranslationService();
            var untranslated = 0;
            if (image is null)
            {
                SetStatus("百度翻译 · 正在翻译…");
                answer.Content = await service.TranslateAsync(text, TranslationRoutes.Find(_settingsStore.Current.TranslationRouteId), credentials, cancellation.Token);
            }
            else
            {
                SetStatus("百度图片翻译 · 正在识别并翻译…");
                var prepared = PreparedTranslationImage.Create(image);
                var blocks = (await service.TranslateImageAsync(prepared, credentials, cancellation.Token)).ToList();
                for (var index = 0; index < blocks.Count; index++)
                {
                    if (!ScreenshotTranslationImage.NeedsChineseTranslation(blocks[index])) continue;
                    SetStatus("百度翻译 · 正在补译未翻译文字…");
                    var translated = await service.TranslateAsync(blocks[index].Source, TranslationRoutes.Find("auto-zh"), credentials, cancellation.Token);
                    blocks[index] = blocks[index] with { Translation = translated };
                    if (ScreenshotTranslationImage.NeedsChineseTranslation(blocks[index]))
                    {
                        untranslated++;
                        blocks[index] = blocks[index] with { Translation = "未译出：" + blocks[index].Source };
                    }
                }
                cancellation.Token.ThrowIfCancellationRequested();
                var result = ScreenshotTranslationImage.Create(image, blocks, _conversation.Id,
                    App.TranslationSmokePath is null ? null : App.TranslationSmokePath + ".translated.png");
                answer.ImagePath = result.ImagePath;
                answer.Content = result.PlainText;
            }
            _conversation.Messages.Add(answer);
            ReplaceStreamingBubble(bubble, answer);
            if (untranslated > 0)
                AddSystemNotice($"有 {untranslated} 处文字未获得中文译文，已在译图中标记；可能是专有名称或识别不清。");
            SetStatus(untranslated > 0 ? "百度图片翻译 · 部分文字未译出" : answer.Model + " · 完成");
        }
        catch (OperationCanceledException)
        {
            answer.Content = cancellation.IsCancellationRequested ? "已停止翻译。" : "百度翻译请求超时，请重试。";
            _conversation.Messages.Add(answer);
            ReplaceStreamingBubble(bubble, answer);
            SetStatus(cancellation.IsCancellationRequested ? "已停止" : "请求超时");
        }
        catch (Exception exception)
        {
            answer.Content = "翻译失败：" + exception.Message;
            _conversation.Messages.Add(answer);
            ReplaceStreamingBubble(bubble, answer);
            SetStatus("百度翻译 · 失败");
        }
        finally
        {
            _requestCancellation = null;
            SetSending(false);
            if (!IsVisible) ClearTranslationImages();
            UpdateWindowLayout();
        }
    }

    private async Task SendAsync(string providerText, string displayText)
    {
        if (_mode != AssistantMode.Chat)
        {
            await SendTranslationAsync(providerText, displayText);
            return;
        }
        var requestMode = _mode;
        if (requestMode != AssistantMode.Chat)
        {
            _conversation = new Conversation { Mode = requestMode };
            Find<StackPanel>("MessagesPanel").Children.Clear();
        }
        var settings = _settingsStore.Current;
        var key = _settingsStore.ResolveKey(settings.Provider);
        if (string.IsNullOrWhiteSpace(key))
        {
            ExpandForContent();
            AddSystemNotice(settings.Provider == ProviderKind.Glm
                ? "未找到 GLM API Key。请打开设置，或配置 ZHIPUAI_API_KEY。"
                : "未找到 DeepSeek API Key。请打开设置，或配置 DEEPSEEK_API_KEY。");
            return;
        }

        if (_conversation.Messages.Count == 0)
            _conversation.Title = MakeTitle(displayText);
        ExpandForContent();
        var image = _pendingImage;
        var user = new ConversationMessage { Role = "user", Content = image is null ? displayText : "[截图]\n" + displayText, ImageBytes = image };
        _conversation.Messages.Add(user);
        AddMessageBubble(user);
        _requestCancellation = new CancellationTokenSource();
        SetSending(true);

        var model = settings.Provider == ProviderKind.Glm ? settings.GlmModel : settings.DeepSeekModel;
        var enableSearch = requestMode == AssistantMode.Chat && SearchDecider.ShouldSearch(settings.Search, providerText);
        var searchSources = new List<WebSource>();
        var groundedProviderText = providerText;
        if (enableSearch)
        {
            var searchKey = _settingsStore.ResolveKey(ProviderKind.Glm);
            if (string.IsNullOrWhiteSpace(searchKey))
            {
                AddSystemNotice("联网查询需要 GLM API Key 来调用 Web Search；本次将使用模型已有知识回答。");
                enableSearch = false;
            }
            else
            {
                try
                {
                    SetStatus("正在检索实时网页资料…");
                    var search = await new WebSearchService().SearchAsync(providerText, searchKey, _requestCancellation.Token);
                    groundedProviderText = search.AddContextTo(providerText);
                    searchSources = search.Sources;
                }
                catch (OperationCanceledException)
                {
                    SetStatus("已停止");
                    _requestCancellation.Dispose();
                    _requestCancellation = null;
                    SetSending(false);
                    return;
                }
                catch (Exception exception)
                {
                    AddSystemNotice("联网检索失败，将使用模型已有知识回答：" + exception.Message);
                    enableSearch = false;
                }
            }
        }
        var providerMessages = requestMode == AssistantMode.Chat
            ? _conversation.Messages.Select(message => new ProviderMessage
            {
                Role = message.Role,
                Content = ReferenceEquals(message, user) ? groundedProviderText : message.Content
                    + (message.ImageBytes is null && message.Content.StartsWith("[截图]", StringComparison.Ordinal)
                        ? "\n（此历史截图已清理，当前无法查看原图。）" : ""),
                ImageBytes = message.ImageBytes
            }).ToList()
            : [new ProviderMessage { Role = "user", Content = groundedProviderText }];
        var request = new AssistantRequest
        {
            Provider = settings.Provider,
            Model = model,
            SystemPrompt = PromptProfiles.ForMode(requestMode, TranslationRoutes.Find(settings.TranslationRouteId), providerText),
            Messages = providerMessages,
            ImageBytes = image,
            EnableSearch = false,
            DeepThinking = settings.DeepThinking && requestMode == AssistantMode.Chat
        };
        var assistant = new ConversationMessage
        {
            Role = "assistant",
            Provider = settings.Provider,
            Model = model
        };
        var streamText = AddStreamingBubble(assistant);
        SetStatus(enableSearch ? $"{model} · 正在联网查询…" : $"{model} · 正在回答…");
        var content = new StringBuilder();
        var sources = new Dictionary<string, WebSource>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < searchSources.Count; index++)
        {
            var source = searchSources[index];
            sources[string.IsNullOrWhiteSpace(source.Url) ? $"title:{source.Title}:{index}" : source.Url] = source;
        }
        try
        {
            var provider = AssistantProviderFactory.Create(settings.Provider);
            await foreach (var update in provider.StreamAsync(request, key, _requestCancellation.Token))
            {
                content.Append(update.TextDelta);
                foreach (var source in update.Sources)
                    if (!string.IsNullOrWhiteSpace(source.Url)) sources[source.Url] = source;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    streamText.Text = content.ToString();
                    ScrollToBottom();
                });
            }
            assistant.Content = content.ToString().Trim();
            assistant.Sources = sources.Values.ToList();
            if (string.IsNullOrWhiteSpace(assistant.Content)) assistant.Content = "模型没有返回文字结果。";
            _conversation.Messages.Add(assistant);
            ReplaceStreamingBubble(streamText, assistant);
            SetChatAttachment(null);
            if (requestMode == AssistantMode.Chat && !App.SmokeTest)
            {
                await _conversationStore.SaveAsync(_conversation);
                await RefreshHistoryAsync();
            }
            SetStatus($"{model} · 完成" + (enableSearch ? " · 已联网" : string.Empty));
        }
        catch (OperationCanceledException)
        {
            assistant.Content = content.Length > 0 ? content.ToString() + "\n\n（已停止）" : "已停止生成。";
            _conversation.Messages.Add(assistant);
            ReplaceStreamingBubble(streamText, assistant);
            if (requestMode == AssistantMode.Chat && !App.SmokeTest) await _conversationStore.SaveAsync(_conversation);
            SetStatus("已停止");
        }
        catch (Exception exception)
        {
            assistant.Content = "请求失败：" + exception.Message;
            _conversation.Messages.Add(assistant);
            ReplaceStreamingBubble(streamText, assistant);
            if (requestMode == AssistantMode.Chat && !App.SmokeTest) await _conversationStore.SaveAsync(_conversation);
            SetStatus("请求失败");
        }
        finally
        {
            _requestCancellation.Dispose();
            _requestCancellation = null;
            SetSending(false);
        }
    }

    private void RenderWelcome()
    {
        if (Find<StackPanel>("MessagesPanel").Children.Count > 0) return;
        AddSystemNotice("翻译与截图每次独立且不记入历史；快问会保存上下文，可从左侧历史继续。");
    }

    private void RenderConversation()
    {
        var panel = Find<StackPanel>("MessagesPanel");
        panel.Children.Clear();
        foreach (var message in _conversation.Messages) AddMessageBubble(message);
        if (_conversation.Messages.Count == 0) RenderWelcome();
        ScrollToBottom();
    }

    private void AddSystemNotice(string text)
    {
        Find<StackPanel>("MessagesPanel").Children.Add(new Border
        {
            Background = Brush.Parse("#12FFFFFF"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#B8FFFFFF") }
        });
        ScrollToBottom();
    }

    private void AddMessageBubble(ConversationMessage message)
    {
        var content = BuildMessageContent(message);
        if (_mode != AssistantMode.Chat)
        {
            Find<StackPanel>("MessagesPanel").Children.Add(content);
            ScrollToBottom();
            return;
        }
        Find<StackPanel>("MessagesPanel").Children.Add(new Border
        {
            Background = Brush.Parse(message.Role == "user" ? "#207CEDAE" : "#12FFFFFF"),
            BorderBrush = Brush.Parse(message.Role == "user" ? "#487CEDAE" : "#25FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(13, 10),
            MaxWidth = message.Role == "user" ? 620 : double.PositiveInfinity,
            HorizontalAlignment = message.Role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Child = content
        });
        ScrollToBottom();
    }

    private SelectableTextBlock AddStreamingBubble(ConversationMessage message)
    {
        var text = new SelectableTextBlock { Text = "…", TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        var border = new Border
        {
            Tag = text,
            Background = _mode == AssistantMode.Chat ? Brush.Parse("#12FFFFFF") : Brushes.Transparent,
            BorderBrush = _mode == AssistantMode.Chat ? Brush.Parse("#25FFFFFF") : Brushes.Transparent,
            BorderThickness = new Thickness(_mode == AssistantMode.Chat ? 1 : 0),
            CornerRadius = new CornerRadius(10),
            Padding = _mode == AssistantMode.Chat ? new Thickness(13, 10) : new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = text
        };
        Find<StackPanel>("MessagesPanel").Children.Add(border);
        ScrollToBottom();
        return text;
    }

    private void ReplaceStreamingBubble(SelectableTextBlock streaming, ConversationMessage message)
    {
        var panel = Find<StackPanel>("MessagesPanel");
        var border = panel.Children.OfType<Border>().FirstOrDefault(item => ReferenceEquals(item.Tag, streaming));
        if (border is null) return;
        border.Tag = null;
        border.Child = BuildMessageContent(message);
        ScrollToBottom();
    }

    private Control BuildMessageContent(ConversationMessage message)
    {
        if (_mode != AssistantMode.Chat && string.IsNullOrWhiteSpace(message.ImagePath))
            return new SelectableTextBlock { Text = message.Content, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        var stack = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(message.ImagePath) && File.Exists(message.ImagePath))
            AddScreenshotResult(stack, message);
        else
            AddMarkdownLikeContent(stack, message.Content);
        if (message.Sources.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "来源",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse("#9CFFD0")
            });
            foreach (var source in message.Sources.Take(8))
            {
                var label = string.IsNullOrWhiteSpace(source.PublishedAt) ? source.Title : $"{source.Title} · {source.PublishedAt}";
                if (string.IsNullOrWhiteSpace(source.Url))
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = label,
                        FontSize = 11,
                        Foreground = Brush.Parse("#A8FFFFFF"),
                        Margin = new Thickness(8, 3)
                    });
                    continue;
                }
                var button = new Button
                {
                    Content = label,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 4),
                    FontSize = 11
                };
                ApplyWidgetTheme(button);
                button.Click += (_, _) => OpenUrl(source.Url);
                stack.Children.Add(button);
            }
        }
        if (message.Role == "assistant" && !string.IsNullOrWhiteSpace(message.Model))
        {
            stack.Children.Add(new TextBlock
            {
                Text = message.Model,
                FontSize = 10,
                Foreground = Brush.Parse("#68FFFFFF"),
                HorizontalAlignment = HorizontalAlignment.Right
            });
        }
        return stack;
    }

    private void AddScreenshotResult(StackPanel target, ConversationMessage message)
    {
        try
        {
            var bitmap = new Bitmap(message.ImagePath!);
            _translationBitmaps.Add(bitmap);
            var image = new Image
            {
                Source = bitmap,
                MaxHeight = 520,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            target.Children.Add(image);
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var open = new Button { Content = "打开图片", FontSize = 10, Padding = new Thickness(7, 3) };
            ApplyWidgetTheme(open);
            open.Click += (_, _) => OpenFile(message.ImagePath!);
            var copy = new Button { Content = "复制译文", FontSize = 10, Padding = new Thickness(7, 3) };
            ApplyWidgetTheme(copy);
            copy.Click += async (_, _) =>
            {
                var clipboard = TopLevel.GetTopLevel(copy)?.Clipboard;
                if (clipboard is not null) await clipboard.SetTextAsync(message.Content);
            };
            actions.Children.Add(open);
            actions.Children.Add(copy);
            target.Children.Add(actions);
        }
        catch
        {
            AddMarkdownLikeContent(target, message.Content);
        }
    }

    private static void AddMarkdownLikeContent(StackPanel target, string content)
    {
        var segments = content.Replace("\r\n", "\n").Split("```", StringSplitOptions.None);
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (string.IsNullOrEmpty(segment)) continue;
            if (index % 2 == 0)
            {
                target.Children.Add(new SelectableTextBlock
                {
                    Text = segment.Trim('\n'),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 21
                });
                continue;
            }
            var firstBreak = segment.IndexOf('\n');
            var code = firstBreak >= 0 ? segment[(firstBreak + 1)..] : segment;
            var box = new TextBox
            {
                Text = code.TrimEnd(),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
                Background = Brush.Parse("#0C0F15"),
                BorderBrush = Brush.Parse("#28FFFFFF"),
                Padding = new Thickness(10),
                MaxHeight = 280
            };
            target.Children.Add(box);
            var copy = new Button { Content = "复制代码", FontSize = 10, Padding = new Thickness(7, 3), HorizontalAlignment = HorizontalAlignment.Right };
            ApplyWidgetTheme(copy);
            copy.Click += async (_, _) =>
            {
                var clipboard = TopLevel.GetTopLevel(box)?.Clipboard;
                if (clipboard is not null) await clipboard.SetTextAsync(code.TrimEnd());
            };
            target.Children.Add(copy);
        }
    }

    private async Task RefreshHistoryAsync()
    {
        var items = Find<StackPanel>("HistoryItems");
        items.Children.Clear();
        foreach (var conversation in await _conversationStore.LoadAsync())
        {
            var button = new Button
            {
                Content = new TextBlock { Text = conversation.DisplayTitle, TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = conversation
            };
            ApplyWidgetTheme(button);
            button.Click += (_, _) =>
            {
                _ = SaveCurrentAsync();
                _conversation = conversation;
                _pendingImage = null;
                SetMode(conversation.Mode);
                ConfigureComponentLayout(AssistantMode.Chat);
                ExpandForContent();
                RenderConversation();
            };
            var delete = new Button
            {
                Content = "×",
                Width = 27,
                MinWidth = 27,
                Padding = new Thickness(0)
            };
            ToolTip.SetTip(delete, "删除记录");
            ApplyWidgetTheme(delete);
            delete.Click += async (_, _) =>
            {
                await _conversationStore.DeleteAsync(conversation.Id);
                if (_conversation.Id == conversation.Id)
                {
                    _conversation = new Conversation { Mode = AssistantMode.Chat };
                    Find<StackPanel>("MessagesPanel").Children.Clear();
                    RenderWelcome();
                }
                await RefreshHistoryAsync();
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
            Grid.SetColumn(delete, 1);
            row.Children.Add(button);
            row.Children.Add(delete);
            items.Children.Add(row);
        }
    }

    private async Task SaveCurrentAsync()
    {
        if (App.SmokeTest) return;
        if (_conversation.Mode == AssistantMode.Chat && _conversation.Messages.Count > 0)
            await _conversationStore.SaveAsync(_conversation);
    }

    private void SetSending(bool sending)
    {
        Find<Button>("SendButton").IsEnabled = !sending;
        Find<Button>("SendButton").IsVisible = !sending;
        Find<Button>("CaptureButton").IsEnabled = !sending;
        Find<Button>("RemoveChatAttachment").IsEnabled = !sending;
        Find<Button>("StopButton").IsVisible = sending;
    }

    private void UpdateStatus()
    {
        if (_mode != AssistantMode.Chat)
        {
            SetStatus(_settingsStore.ResolveBaiduCredentials() is null ? "百度翻译 · 需要配置 APPID 和密钥" : "百度翻译 · 就绪");
            return;
        }
        var settings = _settingsStore.Current;
        var model = settings.Provider == ProviderKind.Glm ? settings.GlmModel : settings.DeepSeekModel;
        var hasKey = !string.IsNullOrWhiteSpace(_settingsStore.ResolveKey(settings.Provider));
        SetStatus($"{model} · {(settings.DeepThinking ? "深入" : "快速")} · {(hasKey ? "就绪" : "需要 API Key")}");
    }

    private void SetStatus(string text) => Find<TextBlock>("StatusText").Text = text;

    private void ShowAndFocus()
    {
        _activationVersion++;
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        Activate();
        if (OperatingSystem.IsWindows())
            SetForegroundWindow(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        Find<TextBox>("Composer").Focus();
    }

    /// <summary>
    /// Desktop shortcut / tray entry point. Hides the window when it is already
    /// showing, otherwise behaves like the translation entry point.
    /// </summary>
    public void ToggleTranslation()
    {
        if (IsVisible && !_captureInProgress && _requestCancellation is null)
        {
            Hide();
            return;
        }
        ShowTranslation();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    private void CollapseForWake()
    {
        _expanded = false;
        Find<Border>("AdvancedPanel").IsVisible = false;
        Find<Border>("HistoryPanel").IsVisible = false;
        Find<Border>("ResultCard").IsVisible = false;
        UpdateWindowLayout();
    }

    private void ExpandForContent()
    {
        _expanded = true;
        Find<Border>("ResultCard").IsVisible = true;
        UpdateWindowLayout();
    }

    private void UpdateWindowLayout()
    {
        var optionsVisible = Find<Border>("AdvancedPanel").IsVisible;
        var historyVisible = Find<Border>("HistoryPanel").IsVisible;
        Find<TextBlock>("StatusText").IsVisible = _expanded || optionsVisible;
        CanResize = false;
        if (_mode != AssistantMode.Chat)
        {
            Find<ScrollViewer>("MessageScroll").Height = double.NaN;
            var screenshot = _conversation.Mode == AssistantMode.Screenshot;
            Find<Border>("WindowShell").Width = 430;
            Find<Border>("WindowShell").Height = double.NaN;
            Find<Border>("WindowShell").VerticalAlignment = VerticalAlignment.Stretch;
            Width = 430;
            if (_expanded && !screenshot)
            {
                var messages = Find<StackPanel>("MessagesPanel");
                messages.Measure(new Size(Width - 46, double.PositiveInfinity));
                Find<Grid>("ComposerGrid").Measure(new Size(Width - 34, double.PositiveInfinity));
                Height = CapsuleHeight + 8 + Math.Clamp(messages.DesiredSize.Height + 48, 65, 280);
            }
            else Height = _expanded ? 360 : CapsuleHeight;
            return;
        }
        var mainWidth = _expanded ? 680 : 430;
        Find<Border>("WindowShell").Width = mainWidth;
        Find<Grid>("AssistantLayout").RowDefinitions = new RowDefinitions(_expanded ? "Auto,Auto,*,Auto,Auto" : "Auto,Auto,Auto,Auto,Auto");
        var input = Find<TextBox>("Composer");
        input.Measure(new Size(mainWidth - 90, double.PositiveInfinity));
        var inputGrowth = Math.Clamp(input.DesiredSize.Height - 29, 0, 61);
        var mainHeight = _expanded ? 510 : 84 + inputGrowth + (Find<Border>("ChatAttachment").IsVisible ? 78 : 0);
        Find<Border>("WindowShell").Height = mainHeight;
        Find<Border>("WindowShell").VerticalAlignment = VerticalAlignment.Top;
        Width = mainWidth + 40 + (optionsVisible || historyVisible ? 238 : 0);
        Height = Math.Max(optionsVisible || historyVisible ? 280 : 0, mainHeight);

        var messageScroll = Find<ScrollViewer>("MessageScroll");
        if (!_expanded)
        {
            messageScroll.Height = double.NaN;
            return;
        }

        // Do not rely on the star row to constrain long chat content. Measure the
        // fixed rows and give the scrollable region the exact remaining height.
        var shell = Find<Border>("WindowShell");
        var title = Find<Border>("TitleBar");
        var composer = Find<Border>("ComposerBar");
        var result = Find<Border>("ResultCard");
        var status = Find<TextBlock>("StatusText");
        var innerWidth = Math.Max(0, mainWidth - shell.Padding.Left - shell.Padding.Right);
        var scale = Math.Max(1, RenderScaling);
        var shellBorderHeight = (Math.Ceiling(shell.BorderThickness.Top * scale)
            + Math.Ceiling(shell.BorderThickness.Bottom * scale)) / scale;
        var innerHeight = Math.Max(0,
            mainHeight - shell.Padding.Top - shell.Padding.Bottom - shellBorderHeight);
        title.Measure(new Size(innerWidth, double.PositiveInfinity));
        composer.Measure(new Size(innerWidth, double.PositiveInfinity));
        status.Measure(new Size(
            Math.Max(0, innerWidth - result.Padding.Left - result.Padding.Right),
            double.PositiveInfinity));
        var statusHeight = status.IsVisible ? status.DesiredSize.Height : 0;
        var resultChromeHeight = result.Margin.Top + result.Margin.Bottom
            + result.Padding.Top + result.Padding.Bottom
            + result.BorderThickness.Top + result.BorderThickness.Bottom;
        messageScroll.Height = Math.Max(120,
            innerHeight - title.DesiredSize.Height - composer.DesiredSize.Height
            - statusHeight - resultChromeHeight);
    }

    private void ClearTranslationImages()
    {
        foreach (var bitmap in _translationBitmaps) bitmap.Dispose();
        _translationBitmaps.Clear();
        if (!App.SmokeTest) TranslationImageCache.Clear();
        if (_conversation.Mode == AssistantMode.Screenshot)
        {
            Find<StackPanel>("MessagesPanel").Children.Clear();
            _conversation.Messages.Clear();
        }
    }

    private void ScrollToBottom() => Dispatcher.UIThread.Post(() =>
    {
        var scroll = Find<ScrollViewer>("MessageScroll");
        if (_mode == AssistantMode.Chat) scroll.ScrollToEnd();
        else scroll.Offset = default;
    }, DispatcherPriority.Background);

    private void ApplyNativeWindowShape()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var scale = RenderScaling;
        var names = _mode == AssistantMode.Chat
            ? new[] { "WindowShell", "HistoryToggle", "OptionsToggle", "HistoryPanel", "AdvancedPanel" }
            : new[] { "ComposerBar", "ResultCard" };
        var shapes = new List<(Rect Bounds, double Radius)>();
        foreach (var name in names)
        {
            var control = Find<Control>(name);
            if (!control.IsVisible || !control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) continue;
            var point = control.TranslatePoint(default, this);
            if (point is not null) shapes.Add((new Rect(point.Value, control.Bounds.Size), name == "ComposerBar" ? CapsuleHeight / 2 : 16));
        }
        if (shapes.Count == 0) return;
        var signature = scale + string.Join(";", shapes);
        if (_lastWindowShape == signature) return;
        _lastWindowShape = signature;
        var region = CreateRoundRectRgn(0, 0, 0, 0, 0, 0);
        foreach (var shape in shapes)
        {
            var box = shape.Bounds;
            var part = CreateRoundRectRgn((int)Math.Round(box.X * scale), (int)Math.Round(box.Y * scale),
                (int)Math.Ceiling(box.Right * scale), (int)Math.Ceiling(box.Bottom * scale),
                (int)Math.Round(shape.Radius * 2 * scale), (int)Math.Round(shape.Radius * 2 * scale));
            CombineRgn(region, region, part, 2);
            DeleteObject(part);
        }
        if (SetWindowRgn(handle, region, true) == 0) DeleteObject(region);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);

    private void ApplySurfaceColors()
    {
        var chat = _mode == AssistantMode.Chat;
        foreach (var name in new[] { "WindowShell", "ComposerBar", "ResultCard", "HistoryPanel", "AdvancedPanel" })
        {
            var border = Find<Border>(name);
            var painted = name == "WindowShell" ? chat : name is "ComposerBar" or "ResultCard" ? !chat : true;
            border.Background = painted ? Surface(border.IsPointerOver) : Brushes.Transparent;
            if (name == "ResultCard") border.BorderThickness = new Thickness(chat ? 0 : 1);
        }
        foreach (var name in new[] { "HistoryToggle", "OptionsToggle" })
        {
            var button = Find<Button>(name);
            button.Background = Surface(button.IsPointerOver);
        }

        // Linux side readability deviation: the Windows surface colours (#4811141B
        // and its #7011141B hover) are 28% opaque, which leaves the white text on
        // a light grey over a bright desktop. The frosted panel comes from the
        // Window resources so the XAML defaults and this runtime path cannot
        // drift apart; only these background values change.
        IBrush Surface(bool hover) =>
            this.FindResource(hover ? "GlassPanelHover" : "GlassPanel") as IBrush
            ?? Brush.Parse(hover ? "#B6171B23" : "#9C171B23");
    }

    private static Control MakeIcon(string data, double size = 16) => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform,
        Stroke = Brush.Parse("#DCE5EF"), StrokeThickness = 1.5
    };

    private async Task OpenSettingsAsync()
    {
        _settingsDialogOpen = true;
        try { await new SettingsWindow(_settingsStore).ShowDialog(this); ApplySettings(); }
        finally { _settingsDialogOpen = false; }
    }

    private ContextMenu CreateTranslationRouteMenu()
    {
        var menu = new ContextMenu
        {
            Background = Brush.Parse("#F0181B23"),
            BorderBrush = Brush.Parse("#3CFFFFFF"),
            BorderThickness = new Thickness(1),
            FontSize = 10.5
        };
        foreach (var route in TranslationRoutes.All)
        {
            var item = new MenuItem { Header = route.Label, Tag = route.Id };
            item.Click += (_, _) =>
            {
                var settings = _settingsStore.Current;
                settings.TranslationRouteId = route.Id;
                _settingsStore.Save(settings);
                Find<Button>("TranslationRouteButton").Content = route.Label + "  ▾";
                Find<Button>("CompactTranslationRouteButton").Content = route.Label + "  ▾";
                UpdateStatus();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var settingsItem = new MenuItem { Header = "翻译设置…" };
        settingsItem.Click += async (_, _) =>
        {
            await OpenSettingsAsync();
        };
        menu.Items.Add(settingsItem);
        return menu;
    }

    private static void ApplyWidgetTheme(Button button)
    {
        if (Application.Current?.TryGetResource("WidgetButtonTheme", null, out var resource) == true
            && resource is ControlTheme theme)
            button.Theme = theme;
    }

    private T Find<T>(string name) where T : Control => this.FindControl<T>(name)
        ?? throw new InvalidOperationException($"Control '{name}' was not found.");

    private static string MakeTitle(string text)
    {
        var value = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return value.Length <= 30 ? value : value[..30] + "…";
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
