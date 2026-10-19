using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LittleTools.Assistant.Services;
using System.Text;

namespace LittleTools.Assistant;

internal sealed class NoteWindow : Window
{
    internal string Markdown => _markdown.Text ?? "";
    internal bool CanExport => _copy.IsEnabled && _save.IsEnabled;
    private readonly TextBox _markdown;
    private readonly TextBlock _status;
    private readonly Button _copy;
    private readonly Button _save;
    private readonly Button _regenerate;
    private readonly Button _stop;
    internal NoteHeadingOptions HeadingOptions { get; }
    private bool _complete;

    internal NoteWindow(string? markdown, NoteHeadingOptions heading, Func<Task> regenerate, Action stop)
    {
        Title = "快问 · 知识笔记";
        Width = 720; Height = 600; MinWidth = 440; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = true;
        Background = Brush.Parse("#171C26");
        HeadingOptions = heading.Validate();
        _markdown = new TextBox
        {
            Text = markdown ?? "", IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_markdown, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _status = new TextBlock { Text = "已确认笔记方案 · 正在准备生成", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        _copy = new Button { Content = "复制全文" };
        _save = new Button { Content = "保存 .md" };
        _regenerate = new Button { Content = "返回聊天调整" };
        _stop = new Button { Content = "停止", IsVisible = false };
        _copy.Click += async (_, _) =>
        {
            try
            {
                var clipboard = GetTopLevel(this)?.Clipboard;
                if (clipboard is null) throw new InvalidOperationException("剪贴板不可用");
                await clipboard.SetTextAsync(_markdown.Text ?? "");
                _status.Text = "已复制完整 Markdown（含代码块）";
            }
            catch (Exception exception) { _status.Text = "复制失败：" + exception.Message; }
        };
        _save.Click += async (_, _) => await SaveAsync();
        _regenerate.Click += async (_, _) =>
        {
            try { _ = HeadingOptions; await regenerate(); }
            catch (ArgumentException exception) { _status.Text = exception.Message; }
        };
        _stop.Click += (_, _) => stop();
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in new[] { _copy, _save, _regenerate, _stop }) actions.Children.Add(button);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12, Margin = new Thickness(16) };
        layout.Children.Add(_status);
        Grid.SetRow(_markdown, 1); layout.Children.Add(_markdown);
        Grid.SetRow(actions, 2); layout.Children.Add(actions);
        Content = layout;
        _complete = !string.IsNullOrWhiteSpace(markdown);
        SetBusy(false);
    }

    internal void SetBusy(bool busy)
    {
        _regenerate.IsEnabled = !busy;
        _copy.IsEnabled = _save.IsEnabled = !busy && _complete;
        _stop.IsVisible = busy;
    }

    internal void UpdateText(string markdown) => _markdown.Text = markdown;
    internal void SetStatus(string status) => _status.Text = status;
    internal void Complete(string markdown)
    {
        UpdateText(markdown);
        _complete = true;
        _status.Text = "笔记已生成 · 可复制完整 Markdown 或保存为 .md";
    }

    private async Task SaveAsync()
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存知识笔记（可选择 Obsidian 笔记目录）",
                SuggestedFileName = ConversationNotes.SuggestedFileName(_markdown.Text ?? ""),
                DefaultExtension = "md",
                FileTypeChoices = [new FilePickerFileType("Markdown 笔记") { Patterns = ["*.md"] }],
                ShowOverwritePrompt = true
            });
            if (file is null) return;
            using (file)
            {
                await using var stream = await file.OpenWriteAsync();
                if (stream.CanSeek) stream.SetLength(0);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(_markdown.Text);
            }
            _status.Text = "笔记已保存";
        }
        catch (Exception exception) { _status.Text = "保存失败：" + exception.Message; }
    }
}
