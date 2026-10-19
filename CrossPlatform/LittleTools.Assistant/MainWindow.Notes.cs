using Avalonia.Controls;
using LittleTools.Assistant.Services;
using System.Text;

namespace LittleTools.Assistant;

public sealed partial class MainWindow
{
    private NoteWindow? _noteWindow;

    private void UpdateNoteButton()
    {
        var button = Find<Button>("GenerateNoteButton");
        button.IsVisible = _mode == AssistantMode.Chat;
        button.IsEnabled = _requestCancellation is null && !_captureInProgress && ConversationNotes.CanGenerate(_conversation);
        var discussing = _mode == AssistantMode.Chat && _conversation.NoteDiscussionActive;
        button.Content = discussing ? "重拟方案" : "生成笔记";
        Find<Button>("ConfirmNoteButton").IsVisible = discussing;
        Find<Button>("ConfirmNoteButton").IsEnabled = _requestCancellation is null && _conversation.NotePlanReady && _conversation.PendingNotePlan is not null;
        Find<Button>("CancelNoteButton").IsVisible = discussing;
        Find<Button>("CancelNoteButton").IsEnabled = _requestCancellation is null;
        Find<Button>("CaptureButton").IsEnabled = _requestCancellation is null && !discussing;
        if (discussing) Find<TextBlock>("ComposerPlaceholder").Text = "调整笔记标题、层级或内容；确认后生成";
    }

    private async Task OpenNoteAsync(IAssistantProvider? provider = null)
    {
        if (_requestCancellation is not null || _captureInProgress || !ConversationNotes.CanGenerate(_conversation)) return;
        _noteWindow?.Close();
        if (!_conversation.NoteDiscussionActive) _conversation.NoteDiscussionStart = _conversation.Messages.Count;
        _conversation.NoteDiscussionActive = true;
        _conversation.NotePlanReady = false;
        SetChatAttachment(null);
        ExpandForContent();
        UpdateNoteButton();
        await DiscussNoteAsync("请根据本次问答先拟定笔记的章节标题、几级标题、内容范围和格式，与我确认后再生成正文。", provider);
    }

    private async Task DiscussNoteAsync(string text, IAssistantProvider? provider = null)
    {
        if (_requestCancellation is not null || _captureInProgress) return;
        var conversation = _conversation;
        conversation.NotePlanReady = false;
        var user = new ConversationMessage { Role = "user", Content = text, IsNoteDiscussion = true };
        conversation.Messages.Add(user);
        AddMessageBubble(user);
        UpdateNoteButton();
        var settings = _settingsStore.Current;
        var key = _settingsStore.ResolveKey(settings.Provider);
        if (provider is null && string.IsNullOrWhiteSpace(key))
        {
            AddSystemNotice("请先在快问设置中配置当前模型的 API Key。");
            try { await SaveCurrentAsync(); }
            catch (Exception exception) { AddSystemNotice("协商记录保存失败：" + exception.Message); }
            return;
        }
        var request = ConversationNotes.BuildPlanningRequest(conversation, settings);
        var assistant = new ConversationMessage { Role = "assistant", Model = request.Model, Provider = request.Provider, IsNoteDiscussion = true };
        var bubble = AddStreamingBubble(assistant);
        bubble.Text = "正在拟定笔记标题、层级与内容方案…";
        using var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        SetSending(true);
        SetStatus("正在协商笔记方案…");
        try
        {
            var proposal = await ConversationNotes.ProposeAsync(provider ?? AssistantProviderFactory.Create(request.Provider), request,
                key ?? "", cancellation.Token, () =>
                {
                    bubble.Text = "模型响应未完整返回，正在自动重试一次…";
                    SetStatus("正在重新拟定笔记方案…");
                });
            conversation.PendingNotePlan = proposal.Plan;
            conversation.NotePlanReady = true;
            assistant.Content = proposal.Message;
            SetStatus("笔记方案待确认 · 可在聊天框继续调整");
        }
        catch (OperationCanceledException)
        {
            assistant.Content = "笔记方案已停止，请继续发送要求重试，或退出确认。";
            SetStatus("笔记方案已停止");
        }
        catch (Exception exception)
        {
            assistant.Content = "笔记方案拟定失败：" + exception.Message + "\n请继续发送要求重试，或退出确认。";
            SetStatus("笔记方案拟定失败");
        }
        finally
        {
            conversation.Messages.Add(assistant);
            ReplaceStreamingBubble(bubble, assistant);
            _requestCancellation = null;
            SetSending(false);
        }
        try
        {
            await SaveCurrentAsync();
            if (!App.SmokeTest) await RefreshHistoryAsync();
        }
        catch (Exception exception) { AddSystemNotice("笔记协商记录保存失败：" + exception.Message); }
    }

    private async Task ExitNoteDiscussionAsync()
    {
        if (_requestCancellation is not null) return;
        _conversation.NoteDiscussionActive = false;
        _conversation.NotePlanReady = false;
        Find<TextBlock>("ComposerPlaceholder").Text = NextChatHint();
        UpdateNoteButton();
        AddSystemNotice("已退出笔记确认，可以继续问答。");
        await SaveCurrentAsync();
    }

    private async Task ConfirmNoteAsync(IAssistantProvider? provider = null)
    {
        if (_requestCancellation is not null || _captureInProgress || !_conversation.NoteDiscussionActive) return;
        if (!_conversation.NotePlanReady || _conversation.PendingNotePlan is null)
        {
            AddSystemNotice("请先让模型完整返回当前笔记方案，再确认生成。");
            return;
        }
        _noteWindow?.Close();
        var conversation = _conversation;
        var plan = conversation.PendingNotePlan;
        NoteWindow? window = null;
        window = new NoteWindow(conversation.NoteMarkdown, new NoteHeadingOptions(plan.Title, plan.HeadingLevel, plan.SectionHeadingLevel),
            async () =>
            {
                window!.Close();
                ShowAndFocus();
                _conversation.NoteDiscussionActive = true;
                _conversation.NoteDiscussionStart = _conversation.Messages.Count;
                _conversation.NotePlanReady = false;
                await DiscussNoteAsync("我想调整刚才的笔记方案，请列出当前标题、层级、范围和格式，暂不生成正文。");
            },
            () => _requestCancellation?.Cancel());
        _noteWindow = window;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_noteWindow, window)) return;
            // Only a note generation owned by this window may be cancelled.
            if (_generatingNote) _requestCancellation?.Cancel();
            _noteWindow = null;
        };
        window.Show(this);
        await GenerateNoteAsync(conversation, window, provider);
    }

    private bool _generatingNote;

    private async Task GenerateNoteAsync(Conversation conversation, NoteWindow window, IAssistantProvider? provider = null)
    {
        if (_requestCancellation is not null || _captureInProgress) return;
        var settings = _settingsStore.Current;
        var key = _settingsStore.ResolveKey(settings.Provider);
        if (provider is null && string.IsNullOrWhiteSpace(key))
        {
            window.SetStatus("请先在快问设置中配置当前模型的 API Key。");
            return;
        }
        var heading = window.HeadingOptions;
        var request = ConversationNotes.BuildRequest(conversation, settings, heading, conversation.PendingNotePlan);
        var messageCount = conversation.Messages.Count;
        var previousNote = conversation.NoteMarkdown;
        using var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        _generatingNote = true;
        SetSending(true);
        window.SetBusy(true);
        window.UpdateText("");
        window.SetStatus("正在归纳核心知识点与追问…");
        SetStatus("正在生成知识笔记…");
        var markdown = new StringBuilder();
        try
        {
            await foreach (var update in (provider ?? AssistantProviderFactory.Create(request.Provider)).StreamAsync(request, key ?? "", cancellation.Token))
            {
                markdown.Append(update.TextDelta);
                window.UpdateText(markdown.ToString());
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var note = ConversationNotes.FinishMarkdown(markdown.ToString(), heading);
            conversation.NoteMarkdown = note;
            conversation.NoteMessageCount = messageCount;
            conversation.NoteTitle = heading.Title;
            conversation.NoteHeadingLevel = heading.Level;
            conversation.NoteDiscussionActive = false;
            Find<TextBlock>("ComposerPlaceholder").Text = NextChatHint();
            window.Complete(note);
            if (!App.SmokeTest)
            {
                await _conversationStore.SaveAsync(conversation);
                await RefreshHistoryAsync();
            }
            SetStatus("知识笔记已生成");
        }
        catch (OperationCanceledException)
        {
            window.UpdateText(previousNote ?? "");
            window.SetStatus("已停止生成笔记" + (previousNote is null ? "，可重新生成。" : "，保留上次笔记。"));
            SetStatus("笔记生成已停止");
        }
        catch (Exception exception)
        {
            window.UpdateText(conversation.NoteMarkdown ?? "");
            window.SetStatus("笔记生成或保存失败：" + exception.Message);
            SetStatus("笔记生成或保存失败");
        }
        finally
        {
            _generatingNote = false;
            _requestCancellation = null;
            SetSending(false);
            window.SetBusy(false);
        }
    }
}
