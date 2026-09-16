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
    private readonly SettingsStore _settingsStore;
    private readonly ConversationStore _conversationStore;
    private readonly IScreenshotService _screenshotService;
    private Conversation _conversation = new();
    private AssistantMode _mode = AssistantMode.Translate;
    private CancellationTokenSource? _requestCancellation;
    private byte[]? _pendingImage;
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
            }
        };
        Closed += (_, _) => ClearTranslationImages();
        Opened += (_, _) => ApplyNativeWindowShape();
        SizeChanged += (_, _) => ApplyNativeWindowShape();
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

    private void WireEvents()
    {
        var shell = Find<Border>("WindowShell");
        shell.PointerEntered += (_, _) => shell.Background = Brush.Parse("#7011141B");
        shell.PointerExited += (_, _) => shell.Background = Brush.Parse("#4811141B");
        PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (args.GetPosition(this).Y <= 12) BeginMoveDrag(args);
        };
        Find<Button>("HistoryToggle").Click += (_, _) =>
        {
            var panel = Find<Border>("HistoryPanel");
            panel.IsVisible = !panel.IsVisible;
            if (panel.IsVisible) ExpandForContent();
            else UpdateWindowLayout();
        };
        Find<Button>("OptionsToggle").Click += (_, _) =>
        {
            var panel = Find<Border>("AdvancedPanel");
            panel.IsVisible = !panel.IsVisible;
            UpdateWindowLayout();
        };
        Find<Button>("HideButton").Click += (_, _) => Hide();
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
            await CaptureAndTranslateAsync();
        };
        Find<Button>("SendButton").Click += async (_, _) => await SendComposerAsync();
        Find<Button>("StopButton").Click += (_, _) => _requestCancellation?.Cancel();
        Find<Button>("SettingsButton").Click += async (_, _) =>
        {
            var dialog = new SettingsWindow(_settingsStore);
            await dialog.ShowDialog(this);
            ApplySettings();
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
            Find<TextBlock>("ComposerPlaceholder").IsVisible = string.IsNullOrEmpty(composer.Text);
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
            AssistantMode.Translate => "输入文字，Enter 翻译 · Alt+Enter 换行",
            AssistantMode.Screenshot => "点击截图，框选需要翻译的区域",
            _ => "输入问题，Enter 发送 · Alt+Enter 换行"
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
        var translating = mode == AssistantMode.Translate;
        var chatting = mode == AssistantMode.Chat;
        Find<Grid>("AssistantLayout").RowDefinitions = new RowDefinitions(chatting ? "Auto,Auto,*,Auto,Auto" : "Auto,Auto,Auto,*,Auto");
        Grid.SetRow(Find<Border>("ComposerBar"), chatting ? 3 : 2);
        Grid.SetRow(Find<Grid>("ConversationArea"), chatting ? 2 : 3);
        Find<Grid>("ConversationArea").Margin = chatting ? new Thickness(0, 0, 0, 8) : new Thickness(0, 6, 0, 0);
        Find<Border>("TitleBar").IsVisible = !translating;
        Find<StackPanel>("ModeActions").IsVisible = false;
        Find<TextBlock>("ComponentTitle").IsVisible = !translating;
        Find<TextBlock>("ComponentTitle").Text = chatting ? "快问" : "截图翻译";
        Find<Button>("HistoryToggle").IsVisible = chatting;
        Find<Button>("OptionsToggle").IsVisible = chatting;
        Find<Button>("CompactTranslationRouteButton").IsVisible = translating;
        Find<StackPanel>("TranslationToolbar").IsVisible = !chatting;
        Find<Button>("CaptureButton").IsVisible = !chatting;
        Find<TextBox>("Composer").MinHeight = chatting ? 29 : 45;
        var send = Find<Button>("SendButton");
        send.Content = chatting ? "发送" : new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 0,5 L 12,5 M 7,0 L 12,5 L 7,10"),
            Width = 14, Height = 12, Stretch = Stretch.Uniform,
            Stroke = Brush.Parse("#A8FFD2"), StrokeThickness = 1.6
        };
        send.Width = chatting ? 42 : 32;
        send.FontSize = 12;
        Find<Border>("WindowShell").Padding = translating ? new Thickness(12, 10) : new Thickness(16, 13);
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
        if (string.IsNullOrWhiteSpace(text) || _requestCancellation is not null) return;
        composer.Text = string.Empty;
        await SendAsync(text, text);
    }

    private async void OnComposerKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter) return;
        args.Handled = true;
        if (args.KeyModifiers.HasFlag(KeyModifiers.Alt))
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
        var user = new ConversationMessage { Role = "user", Content = displayText };
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
            }).ToList()
            : [new ProviderMessage { Role = "user", Content = groundedProviderText }];
        var request = new AssistantRequest
        {
            Provider = settings.Provider,
            Model = model,
            SystemPrompt = PromptProfiles.ForMode(requestMode, TranslationRoutes.Find(settings.TranslationRouteId), providerText),
            Messages = providerMessages,
            ImageBytes = _pendingImage,
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
            _pendingImage = null;
            if (requestMode == AssistantMode.Chat)
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
            if (requestMode == AssistantMode.Chat) await _conversationStore.SaveAsync(_conversation);
            SetStatus("已停止");
        }
        catch (Exception exception)
        {
            assistant.Content = "请求失败：" + exception.Message;
            _conversation.Messages.Add(assistant);
            ReplaceStreamingBubble(streamText, assistant);
            if (requestMode == AssistantMode.Chat) await _conversationStore.SaveAsync(_conversation);
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
            Background = Brush.Parse("#12FFFFFF"),
            BorderBrush = Brush.Parse("#25FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = _mode == AssistantMode.Translate ? new Thickness(6, 4) : new Thickness(13, 10),
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
        if (_conversation.Mode == AssistantMode.Chat && _conversation.Messages.Count > 0)
            await _conversationStore.SaveAsync(_conversation);
    }

    private void SetSending(bool sending)
    {
        Find<Button>("SendButton").IsEnabled = !sending;
        Find<Button>("SendButton").IsVisible = !sending;
        Find<Button>("CaptureButton").IsEnabled = !sending;
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
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        Activate();
        Find<TextBox>("Composer").Focus();
    }

    private void CollapseForWake()
    {
        _expanded = false;
        Find<Border>("AdvancedPanel").IsVisible = false;
        Find<Border>("HistoryPanel").IsVisible = false;
        Find<Grid>("ConversationArea").IsVisible = false;
        UpdateWindowLayout();
    }

    private void ExpandForContent()
    {
        _expanded = true;
        Find<Grid>("ConversationArea").IsVisible = true;
        UpdateWindowLayout();
    }

    private void UpdateWindowLayout()
    {
        var optionsVisible = Find<Border>("AdvancedPanel").IsVisible;
        var historyVisible = Find<Border>("HistoryPanel").IsVisible;
        Find<TextBlock>("StatusText").IsVisible = _expanded || optionsVisible;
        CanResize = _expanded;
        if (_mode != AssistantMode.Chat)
        {
            var screenshot = _conversation.Mode == AssistantMode.Screenshot;
            Width = _expanded && screenshot ? 500 : 430;
            if (_expanded && !screenshot)
            {
                var messages = Find<StackPanel>("MessagesPanel");
                messages.Measure(new Size(Width - 46, double.PositiveInfinity));
                Find<Grid>("ComposerGrid").Measure(new Size(Width - 34, double.PositiveInfinity));
                Height = Math.Clamp(messages.DesiredSize.Height + Find<Grid>("ComposerGrid").DesiredSize.Height + 65, 150, 360);
            }
            else Height = _expanded ? 360 : 104;
            return;
        }
        Width = _expanded ? (historyVisible ? 780 : 680) : 430;
        Height = _expanded ? 540 : (optionsVisible ? 188 : 140);
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
        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        var radius = Math.Max(1, (int)Math.Round(32 * scale));
        var region = CreateRoundRectRgn(0, 0, width, height, radius, radius);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(handle, region, true) == 0) DeleteObject(region);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

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
            await new SettingsWindow(_settingsStore).ShowDialog(this);
            ApplySettings();
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
