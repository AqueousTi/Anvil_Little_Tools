using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LittleTools.Assistant.Services;
using System.Runtime.CompilerServices;

namespace LittleTools.Assistant;

public sealed partial class MainWindow
{
    private async Task RunNotesSmokeAsync(string path)
    {
        ShowChat();
        _conversation.Title = "execute / fetchone / fetchall";
        _conversation.Messages.Add(new() { Role = "user", Content = "sqlite3 execute 和 fetchone 有什么区别？" });
        _conversation.Messages.Add(new() { Role = "assistant", Content = "execute 返回 Cursor，fetchone 返回下一行 tuple。" });
        _conversation.Messages.Add(new() { Role = "user", Content = "COUNT 没有记录时呢？" });
        _conversation.Messages.Add(new() { Role = "assistant", Content = "COUNT 返回 (0,)，普通查询无匹配返回 None。" });
        ExpandForContent(); RenderConversation(); UpdateLayout();
        if (!Find<Button>("GenerateNoteButton").IsVisible || !Find<Button>("GenerateNoteButton").IsEnabled)
            throw new InvalidOperationException("Answered chat has no enabled note action.");
        var proposal = """
{"reply":"这是 sqlite3 笔记中的子章节，可以继续调整。","plan":{"title":"SQLite 查询取数","headingLevel":3,"scope":"查询返回值与 COUNT 的追问澄清","format":"速查表、易错点和最小示例","sections":["核心速查","追问澄清","最小示例"]}}
""";
        await OpenNoteAsync(new NoteSmokeProvider(proposal, false));
        if (_requestCancellation is not null || _noteWindow is not null || _conversation.NoteMarkdown is not null
            || !_conversation.NotePlanReady || !Find<Button>("ConfirmNoteButton").IsEnabled || !Find<Button>("ConfirmNoteButton").IsVisible)
            throw new InvalidOperationException("Planning did not stay in chat before confirmation.");
        await SmokeWaiter.PumpAsync();
        UpdateWindowLayout(); UpdateLayout(); ScrollToBottom();
        await SmokeWaiter.PumpAsync();
        UpdateLayout();
        SaveRender(path + ".notes-confirmation.png");
        var interrupted = DiscussNoteAsync("调整标题", new NoteSmokeProvider(proposal, true));
        if (_conversation.NotePlanReady || Find<Button>("ConfirmNoteButton").IsEnabled)
            throw new InvalidOperationException("An unfinished edit can be confirmed.");
        _requestCancellation!.Cancel();
        await interrupted;
        await ConfirmNoteAsync();
        if (_noteWindow is not null) throw new InvalidOperationException("A cancelled plan edit generated stale content.");
        proposal = proposal.Replace("SQLite 查询取数", "execute / fetchone / fetchall");
        await DiscussNoteAsync("标题用 execute / fetchone / fetchall，三级，保留最小示例。", new NoteSmokeProvider(proposal, false));
        var originalDiscussionStart = _conversation.NoteDiscussionStart;
        await OpenNoteAsync(new NoteSmokeProvider(proposal, false));
        if (_conversation.NoteDiscussionStart != originalDiscussionStart
            || !ConversationNotes.BuildPlanningRequest(_conversation, _settingsStore.Current).Messages.Any(message => message.Content == "标题用 execute / fetchone / fetchall，三级，保留最小示例。"))
            throw new InvalidOperationException("Replanning discarded the user's previous edits.");
        var rawNote = "# sqlite3 查询速查\n\n核心结论：execute 返回 Cursor，fetchone 才取下一行。\n\n## 核心速查\n\n| API | 返回值 |\n| --- | --- |\n| execute | Cursor |\n| fetchone | tuple 或 None |\n\n## 追问澄清\n\nCOUNT 无记录时返回 (0,)，不是 None。\n\n```python\nrow = conn.execute(\"SELECT COUNT(*) FROM employees\").fetchone()\ncount = row[0]\n```";
        var messageCount = _conversation.Messages.Count;
        await ConfirmNoteAsync(new NoteSmokeProvider(rawNote, false));
        var window = _noteWindow ?? throw new InvalidOperationException("Confirmed note did not open.");
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            var note = ConversationNotes.FinishMarkdown(rawNote, window.HeadingOptions);
            if (window.Markdown != note || !window.CanExport || _conversation.NoteMarkdown != note || _conversation.Messages.Count != messageCount
                || _conversation.NoteDiscussionActive)
                throw new InvalidOperationException("Note output lost content, export actions or mutated chat.");
            if (!_conversation.NoteMarkdown.StartsWith("### execute / fetchone / fetchall\n", StringComparison.Ordinal)
                || !_conversation.NoteMarkdown.Contains("#### 核心速查", StringComparison.Ordinal)
                || _conversation.NoteHeadingLevel != 3 || _conversation.NoteTitle != "execute / fetchone / fetchall")
                throw new InvalidOperationException("Confirmed headings were not applied or saved.");
            var generation = GenerateNoteAsync(_conversation, window, new NoteSmokeProvider(note, true));
            if (window.CanExport || Find<Button>("GenerateNoteButton").IsEnabled)
                throw new InvalidOperationException("Partial note can be exported or duplicate generation started.");
            _requestCancellation!.Cancel();
            await generation;
            if (window.Markdown != note || !window.CanExport || _conversation.NoteMarkdown != note)
                throw new InvalidOperationException("Cancelling regeneration lost the previous complete note.");
            App.SmokeTest = false;
            window.Activate();
            await Task.Delay(150);
            if (!IsVisible) throw new InvalidOperationException("Opening a note hid the chat owner.");
            App.SmokeTest = true;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            SaveNoteSmokeRender(window, path + ".notes.png");
            SaveRender(path + ".notes-action.png");
        }
        finally { App.SmokeTest = true; window.Close(); _noteWindow = null; }
        _conversation.NoteDiscussionActive = true;
        await ExitNoteDiscussionAsync();
        if (_conversation.NoteDiscussionActive || Find<Button>("ConfirmNoteButton").IsVisible)
            throw new InvalidOperationException("Exiting note discussion did not restore normal chat.");
    }

    private static void SaveNoteSmokeRender(NoteWindow window, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)(window.Bounds.Width * window.RenderScaling), (int)(window.Bounds.Height * window.RenderScaling)), new Vector(96 * window.RenderScaling, 96 * window.RenderScaling));
        bitmap.Render(window);
        using var stream = File.Create(path);
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    private sealed class NoteSmokeProvider(string text, bool wait) : IAssistantProvider
    {
        public ProviderKind Kind => ProviderKind.Glm;
        public async IAsyncEnumerable<StreamUpdate> StreamAsync(AssistantRequest request, string apiKey,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new StreamUpdate { TextDelta = text[..20] };
            if (wait) await Task.Delay(Timeout.Infinite, cancellationToken);
            yield return new StreamUpdate { TextDelta = text[20..] };
        }
    }
}
