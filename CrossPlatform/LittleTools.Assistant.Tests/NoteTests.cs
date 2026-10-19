using LittleTools.Assistant;
using LittleTools.Assistant.Services;
using System.Text.Json;

internal static class NoteTests
{
    internal static async Task RunLiveAsync()
    {
        var store = new SettingsStore();
        var key = store.ResolveKey(store.Current.Provider);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No API key for live note test.");
        var conversation = new Conversation
        {
            Messages = [
                new() { Role = "user", Content = "sqlite3 的 execute/fetchone/fetchall 的区别是什么？" },
                new() { Role = "assistant", Content = "execute 返回 Cursor，fetchone 返回下一行 tuple 或 None，fetchall 返回剩余行 list[tuple]。" },
                new() { Role = "user", Content = "COUNT 没有记录时是 None 吗？一行一列如何取值？" },
                new() { Role = "assistant", Content = "COUNT 返回一行一列，空表为 (0,) 不是 None，用 row[0] 取数值；普通 SELECT 无匹配返回 None。\n```python\nrow = conn.execute(\"SELECT COUNT(*) FROM employees\").fetchone()\ncount = row[0]\n```" }
            ]
        };
        // Exercise a longer programming conversation, rather than only a short API sample.
        for (var index = 0; index < 5; index++)
        {
            conversation.Messages.Add(new() { Role = "user", Content = $"第 {index + 1} 次追问：测试多条件查询与 LIMIT 时，普通 SELECT 无匹配和 COUNT 空表返回值怎么区分？" });
            conversation.Messages.Add(new()
            {
                Role = "assistant",
                Content = "普通 SELECT 无匹配返回 None，COUNT 空表返回 (0,)，用 row[0] 取值。\n```sql\n"
                    + string.Join('\n', Enumerable.Range(1, 32).Select(row => $"-- 测试查询 {row}\nSELECT id, name FROM employees WHERE id > 0 AND name = 'test' LIMIT 1;")) + "\n```"
            });
        }
        conversation.NoteDiscussionActive = true;
        conversation.NoteDiscussionStart = conversation.Messages.Count;
        conversation.Messages.Add(new() { Role = "user", Content = "请先根据问答拟定笔记标题、层级、范围和格式，与我确认后再生成正文。", IsNoteDiscussion = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        async Task<string> CompleteAsync(AssistantRequest request)
        {
            var output = new System.Text.StringBuilder();
            await foreach (var update in AssistantProviderFactory.Create(store.Current.Provider).StreamAsync(request, key, timeout.Token))
                output.Append(update.TextDelta);
            return output.ToString();
        }
        var proposal = ConversationNotes.ParseProposal(await CompleteAsync(ConversationNotes.BuildPlanningRequest(conversation, store.Current)));
        conversation.PendingNotePlan = proposal.Plan;
        conversation.Messages.Add(new() { Role = "assistant", Content = proposal.Message, IsNoteDiscussion = true });
        conversation.Messages.Add(new() { Role = "user", Content = "标题改为 COUNT 与 fetchone 返回值，使用四级标题，只总结 COUNT 空表和 fetchone 返回值，不总结 fetchall，不要代码，不要表格，用简短列表。", IsNoteDiscussion = true });
        proposal = ConversationNotes.ParseProposal(await CompleteAsync(ConversationNotes.BuildPlanningRequest(conversation, store.Current)));
        if (proposal.Plan.Title != "COUNT 与 fetchone 返回值" || proposal.Plan.HeadingLevel != 4)
            throw new InvalidOperationException("Live planning ignored the requested title or heading level.");
        conversation.PendingNotePlan = proposal.Plan;
        var heading = new NoteHeadingOptions(proposal.Plan.Title, proposal.Plan.HeadingLevel);
        var note = ConversationNotes.FinishMarkdown(await CompleteAsync(ConversationNotes.BuildRequest(conversation, store.Current, heading, proposal.Plan)), heading);
        var path = Path.GetFullPath("CrossPlatform/artifacts/notes-live-test.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, note);
        await File.WriteAllTextAsync(Path.GetFullPath("CrossPlatform/artifacts/notes-live-proposal.md"), proposal.Message);
        if (!note.StartsWith(heading.MarkdownHeading + "\n", StringComparison.Ordinal) || !note.Contains("COUNT", StringComparison.OrdinalIgnoreCase) || !note.Contains("fetchone", StringComparison.OrdinalIgnoreCase)
            || note.Contains("```") || note.Contains('|'))
            throw new InvalidOperationException("Live note ignored the confirmed scope or no-code/no-table format.");
        Console.WriteLine("Live note generation passed: " + path);
    }

    internal static void Run(Action<string, bool> check)
    {
        var conversation = new Conversation();
        check("empty chat cannot generate notes", !ConversationNotes.CanGenerate(conversation));
        conversation.Messages.Add(new() { Role = "user", Content = "sqlite3 怎么取查询结果？", ImageBytes = [1, 2, 3] });
        conversation.Messages.Add(new() { Role = "assistant", Content = "请求失败：test" });
        check("failed chat cannot generate notes", !ConversationNotes.CanGenerate(conversation));
        conversation.Messages[1].Content = "fetchone() 返回一行 tuple。\n```python\nrow = cur.fetchone()\n```";
        conversation.Messages[1].Sources.Add(new() { Title = "SQLite", Url = "https://docs.python.org/3/library/sqlite3.html" });
        conversation.Messages.Add(new() { Role = "user", Content = "COUNT 没有记录时是 None 吗？" });
        conversation.Messages.Add(new() { Role = "assistant", Content = "COUNT 返回 (0,)；无匹配普通查询返回 None。" });
        check("answered chat can generate notes", ConversationNotes.CanGenerate(conversation));
        var before = JsonSerializer.Serialize(conversation);
        foreach (var provider in new[] { ProviderKind.Glm, ProviderKind.DeepSeek })
        {
            var request = ConversationNotes.BuildRequest(conversation, new AppSettings { Provider = provider, Search = SearchPolicy.On });
            check(provider + " note keeps question and follow-up", request.Messages[0].Content.Contains("sqlite3 怎么") && request.Messages[0].Content.Contains("COUNT 没有记录"));
            check(provider + " note keeps complete code fences", request.Messages[0].Content.Contains("```python\nrow = cur.fetchone()\n```"));
            check(provider + " note keeps supplied sources", request.Messages[0].Content.Contains("https://docs.python.org/3/library/sqlite3.html"));
            check(provider + " note is text only and has no search", !request.EnableSearch && request.ImageBytes is null && request.Messages.All(message => message.ImageBytes is null));
            using var body = JsonDocument.Parse(JsonSerializer.Serialize(provider == ProviderKind.Glm ? GlmProvider.BuildBody(request) : DeepSeekProvider.BuildBody(request)));
            check(provider + " note has room for complete examples", body.RootElement.GetProperty(provider == ProviderKind.Glm ? "max_tokens" : "max_output_tokens").GetInt32() == 3000);
        }
        check("note request does not mutate conversation", JsonSerializer.Serialize(conversation) == before);
        conversation.NoteMarkdown = "# sqlite3 速查\n\n```python\nrow = cur.fetchone()\n```";
        conversation.NoteMessageCount = conversation.Messages.Count;
        var restored = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(conversation))!;
        check("note roundtrips with chat", restored.NoteMarkdown == conversation.NoteMarkdown && restored.NoteMessageCount == 4);
        check("old chat without note fields still loads", JsonSerializer.Deserialize<Conversation>("{\"Messages\":[]}")!.NoteMarkdown is null);
        check("filename uses heading", ConversationNotes.SuggestedFileName(conversation.NoteMarkdown) == "sqlite3 速查.md");
        check("filename removes invalid characters", ConversationNotes.SuggestedFileName("# a/b:c?d*") == "abcd.md");
        check("filename has fallback", ConversationNotes.SuggestedFileName("# /:*?") == "问答笔记.md");
        check("filename handles reserved Windows names", ConversationNotes.SuggestedFileName("# CON") == "笔记-CON.md");
        check("outer markdown fence is removed", ConversationNotes.FinishMarkdown("```markdown\n" + conversation.NoteMarkdown + "\n```") == conversation.NoteMarkdown);
        check("complete code remains unchanged", ConversationNotes.FinishMarkdown(conversation.NoteMarkdown) == conversation.NoteMarkdown);
        var rejected = false;
        try { ConversationNotes.FinishMarkdown("# 笔记\n```python\nrow = cur.fetchone()"); }
        catch (InvalidOperationException) { rejected = true; }
        check("truncated code cannot be exported", rejected);
        var rawSection = "# 自动标题\n\n结论\n## 核心速查\n### 易错点\n```python\n# 这是一行代码注释\nprint('## 原样保留')\n```\n~~~text\n# 围栏内原样保留\n~~~";
        var section = ConversationNotes.FinishMarkdown(rawSection, new("execute / fetchone / fetchall", 3));
        check("confirmed title replaces model title", section.StartsWith("### execute / fetchone / fetchall\n", StringComparison.Ordinal));
        check("nested headings follow selected level", section.Contains("#### 核心速查\n##### 易错点"));
        check("heading adjustment preserves fenced code", section.Contains("```python\n# 这是一行代码注释\nprint('## 原样保留')\n```") && section.Contains("~~~text\n# 围栏内原样保留\n~~~"));
        check("sixth level falls back to bold subheadings", ConversationNotes.FinishMarkdown(rawSection, new("查询", 6)).Contains("###### 查询\n\n结论\n**核心速查**\n**易错点**"));
        check("model supplied selected headings remain at selected level", ConversationNotes.FinishMarkdown("### 模型标题\n#### 子标题", new("确认标题", 3)) == "### 确认标题\n#### 子标题");
        check("missing heading is added", ConversationNotes.FinishMarkdown("核心结论。", new("查询", 2)) == "## 查询\n\n核心结论。");
        check("section heading provides filename", ConversationNotes.SuggestedFileName(section) == "execute  fetchone  fetchall.md");
        conversation.NoteTitle = "查询取数";
        conversation.NoteHeadingLevel = 4;
        var savedHeading = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(conversation))!;
        check("heading preferences persist", ConversationNotes.HeadingOptionsFor(savedHeading) == new NoteHeadingOptions("查询取数", 4));
        check("legacy note title and level are inferred", ConversationNotes.HeadingOptionsFor(new Conversation { NoteMarkdown = "### 原有章节\n正文" }) == new NoteHeadingOptions("原有章节", 3));
        foreach (var invalidHeading in new[] { new NoteHeadingOptions(" ", 3), new("多行\n标题", 3), new("查询", 7) })
        {
            var invalidRejected = false;
            try { ConversationNotes.BuildRequest(conversation, new AppSettings(), invalidHeading); }
            catch (ArgumentException) { invalidRejected = true; }
            check("invalid title or level rejected: " + invalidHeading, invalidRejected);
        }
        var proposal = ConversationNotes.ParseProposal("""
```json
{"reply":"请确认，或继续调整。","plan":{"title":"COUNT 返回值","headingLevel":4,"scope":"只保留空表返回值的追问","format":"简短列表，不要代码","sections":["结论","易错点"]}}
```
""");
        check("model proposes visible title, level and scope", proposal.Message.Contains("标题：COUNT 返回值") && proposal.Message.Contains("4 级（####）") && proposal.Message.Contains("不要代码"));
        conversation.NoteDiscussionActive = true;
        conversation.NoteDiscussionStart = conversation.Messages.Count;
        conversation.Messages.Add(new() { Role = "user", Content = "edit-only-marker:不要代码", IsNoteDiscussion = true });
        conversation.Messages.Add(new() { Role = "assistant", Content = proposal.Message, IsNoteDiscussion = true });
        conversation.PendingNotePlan = proposal.Plan;
        conversation.NotePlanReady = true;
        var planningRequest = ConversationNotes.BuildPlanningRequest(conversation, new AppSettings());
        var referenceMessage = planningRequest.Messages[0].Content;
        using var planning = JsonDocument.Parse(referenceMessage[(referenceMessage.IndexOf('\n') + 1)..]);
        check("planner separates knowledge and editing context", planning.RootElement.GetProperty("knowledge").GetArrayLength() == 4 && planningRequest.Messages.Count == 3);
        check("editing requirements are sent as real user turns", planningRequest.Messages[1].Role == "user" && planningRequest.Messages[1].Content == "edit-only-marker:不要代码" && planningRequest.Messages[2].Role == "assistant");
        var finalRequest = ConversationNotes.BuildRequest(conversation, new AppSettings(), new(proposal.Plan.Title, proposal.Plan.HeadingLevel), proposal.Plan);
        check("confirmed scope and format enter final request", finalRequest.SystemPrompt.Contains(JsonSerializer.Serialize(proposal.Plan)));
        check("negotiation is excluded from knowledge summary", !finalRequest.Messages[0].Content.Contains("edit-only-marker") && !finalRequest.Messages[0].Content.Contains("建议的笔记方案"));
        var resumed = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(conversation))!;
        check("discussion and pending plan survive history reload", resumed.NoteDiscussionActive && resumed.NotePlanReady && resumed.PendingNotePlan!.HeadingLevel == 4 && resumed.Messages[^1].IsNoteDiscussion);
        check("explicit confirmation recognized", ConversationNotes.IsGenerationConfirmation("确认生成！"));
        check("edits cannot trigger generation", !ConversationNotes.IsGenerationConfirmation("确认，但标题改成 COUNT") && !ConversationNotes.IsGenerationConfirmation("先不要生成"));
        check("planning alone is not knowledge", !ConversationNotes.CanGenerate(new Conversation { Messages = [new() { Role = "user", Content = "计划", IsNoteDiscussion = true }, new() { Role = "assistant", Content = proposal.Message, IsNoteDiscussion = true }] }));
        conversation.Mode = AssistantMode.Translate;
        check("translation cannot generate notes", !ConversationNotes.CanGenerate(conversation));
    }
}
