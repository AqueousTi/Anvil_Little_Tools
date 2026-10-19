using LittleTools.Assistant;
using LittleTools.Assistant.Services;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

internal static class NoteResponseTests
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        var conversation = new Conversation();
        var request = ConversationNotes.BuildPlanningRequest(conversation, new AppSettings { Provider = ProviderKind.DeepSeek, DeepThinking = true });
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(DeepSeekProvider.BuildBody(request)));
        check("planning disables reasoning even in deep mode", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString() == "none" && !request.DeepThinking);
        check("planning uses API JSON output", body.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString() == "json_object");
        check("unsupported thinking field removed", !body.RootElement.TryGetProperty("thinking", out _));
        conversation.PendingNotePlan = new NotePlan { Title = "旧章节", HeadingLevel = 3, Scope = "旧范围", Format = "旧格式", Sections = ["旧小节"] };
        conversation.Messages.Add(new() { Role = "user", Content = "只需要 sqlite3 的笔记，可以每个知识点作为一个二级标题处理", IsNoteDiscussion = true });
        var editRequest = ConversationNotes.BuildPlanningRequest(conversation, new AppSettings());
        check("latest edit is the final real user message", editRequest.Messages[^1].Role == "user" && editRequest.Messages[^1].Content == conversation.Messages[^1].Content);
        check("per-knowledge-point level is distinguished from root", ConversationNotes.RequestedHeadingLevels(editRequest.Messages) == (1, 2));
        const string unchanged = """
{"reply":"已改为二级。","plan":{"title":"旧章节","headingLevel":3,"sectionHeadingLevel":4,"scope":"旧范围","format":"简洁列表","sections":["旧小节"]}}
""";
        const string changed = """
{"reply":"已排除测试框架内容，请确认。","plan":{"title":"sqlite3","headingLevel":1,"sectionHeadingLevel":2,"scope":"仅 SQLite 查询，不含 unittest 断言","format":"按知识点分节","sections":["只读连接","查询结果"]}}
""";
        var editedProvider = new ProposalProvider(unchanged, changed);
        var edited = await ConversationNotes.ProposeAsync(editedProvider, editRequest, "test", CancellationToken.None);
        check("repeating the old level is rejected and retried", editedProvider.Calls == 2 && edited.Plan.Title == "sqlite3" && edited.Plan.HeadingLevel == 1 && edited.Plan.SectionHeadingLevel == 2);
        check("confirmed proposal shows actual subheading level", edited.Message.Contains("知识点小节：2 级（##）"));
        var formatted = ConversationNotes.FinishMarkdown("### 旧标题\n#### 只读连接\n#### 查询结果", new("sqlite3", 1, 2));
        check("final note follows second-level knowledge sections", formatted == "# sqlite3\n## 只读连接\n## 查询结果");
        conversation.Messages.Add(new() { Role = "user", Content = "这次改成四级标题", IsNoteDiscussion = true });
        check("later explicit heading edit overrides earlier layout", ConversationNotes.RequestedHeadingLevels(ConversationNotes.BuildPlanningRequest(conversation, new AppSettings()).Messages) == (4, null));

        string Event(object data) => "data: " + JsonSerializer.Serialize(data) + "\n\n";
        string Completed(string text) => Event(new { type = "response.completed", response = new { output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } } } });
        async Task<string> ReadAsync(string sse)
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
            var text = new StringBuilder();
            await foreach (var update in DeepSeekProvider.ReadResponseAsync(stream, CancellationToken.None)) text.Append(update.TextDelta);
            return text.ToString();
        }
        check("final response supplies missing deltas", await ReadAsync(Completed("完整方案")) == "完整方案");
        var delta = Event(new { type = "response.output_text.delta", delta = "完整" });
        check("final response supplies missing suffix", await ReadAsync(delta + Completed("完整方案")) == "完整方案");
        check("final response does not duplicate deltas", await ReadAsync(delta + Completed("完整")) == "完整");
        check("reasoning is never treated as visible output", await ReadAsync(Event(new { type = "response.reasoning_text.delta", delta = "hidden reasoning" }) + Completed("方案")) == "方案");
        async Task RejectAsync(string name, string sse, bool retry)
        {
            var rejected = false;
            try { await ReadAsync(sse); }
            catch (ModelResponseException exception) { rejected = exception.CanRetry == retry; }
            check(name, rejected);
        }
        await RejectAsync("output limit is surfaced", delta + Event(new { type = "response.incomplete", response = new { incomplete_details = new { reason = "max_output_tokens" } } }), true);
        await RejectAsync("content filtering does not auto retry", Event(new { type = "response.incomplete", response = new { incomplete_details = new { reason = "content_filter" } } }), false);
        await RejectAsync("premature stream end is surfaced", delta, true);
        await RejectAsync("empty completed output is surfaced", Completed(""), true);
        await RejectAsync("failed response is surfaced", Event(new { type = "response.failed", response = new { error = new { message = "request failed" } } }), false);
        await RejectAsync("broken SSE JSON has readable error", "data: {\"type\":\"response.output_text.delta\"\n\n", true);

        const string valid = """
{"reply":"请确认。","plan":{"title":"查询返回值","headingLevel":3,"scope":"fetchone 返回值","format":"简短列表","sections":["返回值","易错点"]}}
""";
        foreach (var broken in new[] { "", "{\"plan\":{\"title\":\"未完成" })
        {
            var provider = new ProposalProvider(broken, valid);
            var retries = 0;
            var result = await ConversationNotes.ProposeAsync(provider, request, "test", CancellationToken.None, () => retries++);
            check("empty or truncated proposal retries once: " + broken.Length, result.Plan.Title == "查询返回值" && provider.Calls == 2 && retries == 1);
        }
        var twiceBroken = new ProposalProvider("", "");
        var failed = false;
        try { await ConversationNotes.ProposeAsync(twiceBroken, request, "test", CancellationToken.None); }
        catch (NoteProposalException exception) { failed = exception.Message.Contains("未返回") && twiceBroken.Calls == 2; }
        check("retry is bounded and errors are readable", failed);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledProvider = new ProposalProvider(valid);
        try { await ConversationNotes.ProposeAsync(cancelledProvider, request, "test", cancellation.Token); }
        catch (OperationCanceledException) { check("cancelled planning is not retried", cancelledProvider.Calls <= 1); }
    }

    private sealed class ProposalProvider(params string[] replies) : IAssistantProvider
    {
        internal int Calls { get; private set; }
        public ProviderKind Kind => ProviderKind.DeepSeek;
        public async IAsyncEnumerable<StreamUpdate> StreamAsync(AssistantRequest request, string apiKey,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var index = Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new StreamUpdate { TextDelta = replies[Math.Min(index, replies.Length - 1)] };
        }
    }
}
