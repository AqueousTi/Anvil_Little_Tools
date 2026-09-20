using System.Text;
using LittleTools.Assistant.Todo;

/// <summary>
/// Covers the ported todo rules: recurring generation, completion ordering,
/// suppression, backlog handling, import de-duplication, focus maths and the
/// Windows compatible serialization.
/// </summary>
internal static class TodoCoreTests
{
    private sealed class FixedClock : ITodoClock
    {
        public DateTime Now { get; set; } = new(2026, 8, 29, 9, 0, 0, DateTimeKind.Local);
        public DateTime Today => Now.Date;
        public DateTime UtcNow => Now.ToUniversalTime();
    }

    private sealed class SequentialIds : ITodoIdGenerator
    {
        private int _next;
        public string NewId() => "id" + (++_next).ToString("D4");
    }

    public static void Run(Action<string, bool> check)
    {
        Recurring(check);
        Completion(check);
        SuppressionAndBacklog(check);
        Import(check);
        Focus(check);
        Dial(check);
        Storage(check);
        Layout(check);
        SubItemCards(check);
        CompactMessages(check);
        Sound(check);
    }

    private static void Recurring(Action<string, bool> check)
    {
        var clock = new FixedClock();
        var ids = new SequentialIds();
        var data = new DailyTodoData();
        var date = new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Local);
        check("test date is a Saturday", date.DayOfWeek == DayOfWeek.Saturday);

        data.RecurringRules.Add(new RecurringTodoRule { Id = "daily", Text = "晨会", Frequency = "Daily", CreatedDate = "2026-01-05" });
        data.RecurringRules.Add(new RecurringTodoRule { Id = "weekly", Text = "周报", Frequency = "Weekly", ScheduleValue = 6, CreatedDate = "2026-01-05" });
        data.RecurringRules.Add(new RecurringTodoRule { Id = "monthly", Text = "月结", Frequency = "Monthly", ScheduleValue = 29, CreatedDate = "2026-01-05" });
        data.RecurringRules.Add(new RecurringTodoRule { Id = "other", Text = "不匹配", Frequency = "Monthly", ScheduleValue = 15, CreatedDate = "2026-01-05" });

        check("recurring generates every match", RecurringTodoEngine.Ensure(data, date, clock, ids));
        var day = TodoLogic.FindDay(data, date, false)!;
        check("recurring creates three items", day.Items.Count == 3);
        check("recurring keeps created order", day.Items.Select(item => item.Text).SequenceEqual(new[] { "晨会", "周报", "月结" }));
        check("recurring is idempotent", !RecurringTodoEngine.Ensure(data, date, clock, ids) && day.Items.Count == 3);

        // Disabled rules and future creation dates never generate.
        data.RecurringRules.Add(new RecurringTodoRule { Id = "off", Text = "停用", Frequency = "Daily", Enabled = false, CreatedDate = "2026-01-05" });
        data.RecurringRules.Add(new RecurringTodoRule { Id = "future", Text = "未来", Frequency = "Daily", CreatedDate = "2026-09-01" });
        check("disabled and future rules stay out", !RecurringTodoEngine.Ensure(data, date, clock, ids) && day.Items.Count == 3);

        var later = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Local);
        check("creation date starts generating", RecurringTodoEngine.Ensure(data, later, clock, ids));
        check("future rule generates on its date", TodoLogic.FindDay(data, later, false)!.Items.Any(item => item.Text == "未来"));
    }

    private static void Completion(Action<string, bool> check)
    {
        var data = new DailyTodoData();
        var date = new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Local);
        var day = TodoLogic.FindDay(data, date, true)!;
        var clock = new FixedClock();
        var ids = new SequentialIds();

        var first = TodoLogic.AddItem(day, "一", clock, ids);
        var second = TodoLogic.AddItem(day, "二", clock, ids);
        var third = TodoLogic.AddItem(day, "三", clock, ids);
        check("new items keep open order", day.Items.Select(item => item.Text).SequenceEqual(new[] { "一", "二", "三" }));

        TodoLogic.Complete(day, second, data);
        check("completed item sinks to the end", day.Items.Select(item => item.Text).SequenceEqual(new[] { "一", "三", "二" }));
        check("completed item records its index", second.PreviousOpenIndex == 1 && second.Completed);
        check("completed count", TodoLogic.CountCompleted(day) == 1);

        // A new open item must land before the completed block.
        var fourth = TodoLogic.AddItem(day, "四", clock, ids);
        check("new item lands before completed", day.Items.Select(item => item.Text).SequenceEqual(new[] { "一", "三", "四", "二" }));

        TodoLogic.Uncomplete(day, second);
        check("uncomplete restores the remembered position", day.Items.Select(item => item.Text).SequenceEqual(new[] { "一", "二", "三", "四" }));
        check("uncomplete clears the remembered index", second.PreviousOpenIndex == -1 && !second.Completed);

        // Out of range PreviousOpenIndex falls back to the end of the open block.
        TodoLogic.Complete(day, first, data);
        first.PreviousOpenIndex = 99;
        TodoLogic.Uncomplete(day, first);
        check("out of range restore clamps", day.Items.Select(item => item.Text).SequenceEqual(new[] { "二", "三", "四", "一" }));
    }

    private static void SuppressionAndBacklog(Action<string, bool> check)
    {
        var data = new DailyTodoData();
        var clock = new FixedClock();
        var ids = new SequentialIds();
        var date = clock.Today;

        data.RecurringRules.Add(new RecurringTodoRule { Id = "daily", Text = "晨会", Frequency = "Daily", CreatedDate = "2026-01-05" });
        RecurringTodoEngine.Ensure(data, date, clock, ids);
        var day = TodoLogic.FindDay(data, date, false)!;
        var generated = day.Items[0];

        TodoLogic.Delete(day, generated, data);
        check("deleting a generated item suppresses its rule", day.SuppressedRuleIds.Contains("daily"));
        check("deleted item is gone", day.Items.Count == 0);
        check("suppressed rule does not regenerate", !RecurringTodoEngine.Ensure(data, date, clock, ids) && day.Items.Count == 0);

        TodoLogic.RestoreRecurringRulesForToday(data, ["daily"], date);
        check("re-enabled rule regenerates today", RecurringTodoEngine.Ensure(data, date, clock, ids) && day.Items.Count == 1);

        // Focus timer is cancelled when its item disappears.
        var item = day.Items[0];
        data.FocusTimer = FocusTimerMath.Create(item.Id!, item.Text!, 25, clock);
        TodoLogic.Complete(day, item, data);
        check("completing cancels the running focus", data.FocusTimer is null);

        // Backlog moves.
        var open = TodoLogic.AddItem(day, "报销", clock, ids);
        data.FocusTimer = FocusTimerMath.Create(open.Id!, open.Text!, 25, clock);
        check("move to backlog succeeds", TodoLogic.MoveToBacklog(day, open, data));
        check("move to backlog dates the source day", open.BacklogSourceDate == TodoLogic.DateKey(date));
        check("move to backlog cancels focus", data.FocusTimer is null);
        check("completed items cannot move to backlog", !TodoLogic.MoveToBacklog(day, item, data));

        var backlogItem = data.BacklogItems[0];
        check("backlog tick moves the item to today as done",
            TodoLogic.ToggleBacklogCompleted(data, backlogItem, clock.Today)
            && backlogItem.Completed
            && data.BacklogItems.Count == 0
            && TodoLogic.FindDay(data, clock.Today, false)!.Items.Contains(backlogItem));

        // Bulk delete and undo keeps the original order.
        data.BacklogItems.Clear();
        var a = new DailyTodoItem { Id = "a", Text = "A", CreatedAt = clock.Now };
        var b = new DailyTodoItem { Id = "b", Text = "B", CreatedAt = clock.Now };
        var c = new DailyTodoItem { Id = "c", Text = "C", CreatedAt = clock.Now };
        data.BacklogItems.AddRange([a, b, c]);
        var undo = TodoLogic.DeleteBacklogItems(data, [a, c]);
        check("backlog delete records indices", undo.Count == 2 && undo[0].Index == 0 && undo[1].Index == 2);
        check("backlog delete removes the items", data.BacklogItems.Count == 1 && data.BacklogItems[0] == b);
        TodoLogic.UndoBacklogDelete(data, undo);
        check("backlog undo restores order", data.BacklogItems.SequenceEqual(new[] { a, b, c }));

        // Move several backlog items into today, open ones first.
        data.Days.Clear();
        data.BacklogItems.Clear();
        var x = new DailyTodoItem { Id = "x", Text = "X", CreatedAt = clock.Now };
        var y = new DailyTodoItem { Id = "y", Text = "Y", CreatedAt = clock.Now, Completed = true };
        var z = new DailyTodoItem { Id = "z", Text = "Z", CreatedAt = clock.Now };
        data.BacklogItems.AddRange([x, y, z]);
        check("bulk move reports the count", TodoLogic.MoveBacklogToToday(data, [x, y, z], clock.Today) == 3);
        var today = TodoLogic.FindDay(data, clock.Today, false)!;
        check("open items are inserted first, completed last",
            today.Items.Select(item => item.Text).SequenceEqual(new[] { "X", "Z", "Y" }));
    }

    private static void Import(Action<string, bool> check)
    {
        var data = new DailyTodoData();
        var clock = new FixedClock();
        var ids = new SequentialIds();
        var today = clock.Today;
        var yesterday = TodoLogic.FindDay(data, today.AddDays(-1), true)!;

        var open = new DailyTodoItem { Id = "y1", Text = "未完成", CreatedAt = clock.Now.AddDays(-1) };
        var done = new DailyTodoItem { Id = "y2", Text = "已完成", CreatedAt = clock.Now.AddDays(-1), Completed = true };
        yesterday.Items.AddRange([open, done]);

        var candidates = TodoLogic.ImportCandidates(data, today);
        check("only yesterday's open items are candidates", candidates.Count == 1 && candidates[0] == open);

        check("import copies one item", TodoLogic.ImportItems(data, candidates, today, clock, ids) == 1);
        var day = TodoLogic.FindDay(data, today, false)!;
        check("imported item links back to its source", day.Items[0].SourceId == "y1" && day.Items[0].Id != "y1");
        check("imported item lands before completed", !day.Items[0].Completed);
        check("yesterday's item is kept", yesterday.Items.Contains(open));
        check("import is de-duplicated", TodoLogic.ImportItems(data, candidates, today, clock, ids) == 0 && day.Items.Count == 1);
        check("import candidates exclude imported items", TodoLogic.ImportCandidates(data, today).Count == 0);

        // Importing into the backlog dates the item as yesterday.
        var second = new DailyTodoItem { Id = "y3", Text = "第二个", CreatedAt = clock.Now.AddDays(-1) };
        yesterday.Items.Add(second);
        check("backlog import copies the item", TodoLogic.ImportItemsToBacklog(data, [second], today, clock, ids) == 1);
        check("backlog import keeps the source date", data.BacklogItems[0].BacklogSourceDate == TodoLogic.DateKey(today.AddDays(-1)));
    }

    private static void Focus(Action<string, bool> check)
    {
        var clock = new FixedClock();
        var timer = FocusTimerMath.Create("item", "写周报", 25, clock);
        check("focus stores the full item", timer.ItemId == "item" && timer.ItemText == "写周报" && timer.DurationMinutes == 25);
        check("focus end is a round trip timestamp", FocusTimerMath.TryGetEnd(timer.EndsAtUtc, out var endsAt) && endsAt.Kind == DateTimeKind.Utc);
        check("focus remaining uses the wall clock",
            Math.Abs(FocusTimerMath.RemainingSeconds(timer, clock.UtcNow) - 1500) < 1);
        check("focus fill is normalised to 100 minutes",
            Math.Abs(FocusTimerMath.Fill(timer, clock.UtcNow) - 0.25) < 0.001);
        check("focus is not urgent at 25 minutes", !FocusTimerMath.IsUrgent(timer, clock.UtcNow));
        check("focus is urgent inside the last minute",
            FocusTimerMath.IsUrgent(timer, clock.UtcNow.AddSeconds(1500 - 30)));

        check("focus accepts a live timer", FocusTimerMath.Normalize(timer, clock.UtcNow) is not null);
        check("focus drops an expired timer", FocusTimerMath.Normalize(timer, clock.UtcNow.AddMinutes(26)) is null);
        check("focus drops an out of range duration",
            FocusTimerMath.Normalize(new FocusTimerData { ItemId = "i", DurationMinutes = 101, EndsAtUtc = timer.EndsAtUtc }, clock.UtcNow) is null);
        check("focus drops a missing item id",
            FocusTimerMath.Normalize(new FocusTimerData { ItemId = "", DurationMinutes = 25, EndsAtUtc = timer.EndsAtUtc }, clock.UtcNow) is null);
        check("focus drops an unparsable end",
            FocusTimerMath.Normalize(new FocusTimerData { ItemId = "i", DurationMinutes = 25, EndsAtUtc = "nonsense" }, clock.UtcNow) is null);

        check("focus countdown formatting", FocusTimerMath.FormatRemaining(1500) == "25:00" && FocusTimerMath.FormatRemaining(59) == "00:59");
        check("focus clamps the duration", FocusTimerMath.Create("i", "t", 999, clock).DurationMinutes == 100);
        check("focus min duration is zero", FocusTimerMath.Create("i", "t", 0, clock).DurationMinutes == 0);

        // The compact view prefers the running focus item.
        var data = new DailyTodoData();
        var day = TodoLogic.FindDay(data, clock.Today, true)!;
        var ids = new SequentialIds();
        var first = TodoLogic.AddItem(day, "第一", clock, ids);
        var second = TodoLogic.AddItem(day, "第二", clock, ids);
        check("compact shows the first open item", TodoLogic.CurrentTodayItem(data, clock.Today) == first);
        data.FocusTimer = FocusTimerMath.Create(second.Id!, second.Text!, 25, clock);
        check("compact prefers the running focus item", TodoLogic.CurrentTodayItem(data, clock.Today) == second);
    }

    private static void Dial(Action<string, bool> check)
    {
        const double size = 310;
        var centre = size / 2;
        check("dial dead zone keeps the value",
            FocusTimerMath.AngleToMinutes(centre + 10, centre, size, size, 42) == 42);
        // Straight up is minute 0, straight right is 25, straight down is 50.
        check("dial top is zero", FocusTimerMath.AngleToMinutes(centre, 0, size, size, 50) == 0);
        check("dial right is 25", FocusTimerMath.AngleToMinutes(size, centre, size, size, 50) == 25);
        check("dial bottom is 50", FocusTimerMath.AngleToMinutes(centre, size, size, size, 0) == 50);
        check("dial left is 75", FocusTimerMath.AngleToMinutes(0, centre, size, size, 0) == 75);
        // Near the top the value snaps to the nearest five minute detent.
        var nearTop = FocusTimerMath.AngleToMinutes(centre + 4, 40, size, size, 50);
        check("dial snaps to detents", nearTop % 5 == 0);
        check("dial clamps to range",
            FocusTimerMath.AngleToMinutes(0, 0, size, size, 0) is >= 0 and <= 100);
    }

    private static void Storage(Action<string, bool> check)
    {
        var root = TempDirectory();
        try
        {
            var store = new TodoStore(root);
            var data = new DailyTodoData();
            var clock = new FixedClock();
            var ids = new SequentialIds();
            var day = TodoLogic.FindDay(data, clock.Today, true)!;
            var item = TodoLogic.AddItem(day, "写周报", clock, ids);
            TodoLogic.Complete(day, item, data);
            data.FocusTimer = FocusTimerMath.Create(item.Id!, item.Text!, 25, clock);
            data.BacklogItems.Add(new DailyTodoItem
            {
                Id = "b1", Text = "报销", CreatedAt = clock.Now, BacklogSourceDate = "2026-08-28"
            });
            check("first save succeeds", store.Save(data));
            check("no temp file is left behind", !File.Exists(store.DataPath + ".tmp"));
            check("backup is not created by the first save", !File.Exists(store.BackupPath));

            var reloaded = new TodoStore(root).Load();
            check("round trip keeps the item", reloaded.Days[0].Items[0].Text == "写周报");
            check("round trip keeps completion state", reloaded.Days[0].Items[0].Completed);
            check("round trip keeps the previous index", reloaded.Days[0].Items[0].PreviousOpenIndex == 0);
            check("round trip keeps the focus timer",
                reloaded.FocusTimer is not null
                && reloaded.FocusTimer.DurationMinutes == 25
                && reloaded.FocusTimer.ItemId == item.Id
                && reloaded.FocusTimer.ItemText == "写周报");
            check("round trip keeps backlog metadata",
                reloaded.BacklogItems[0].BacklogSourceDate == "2026-08-28");

            reloaded.Days[0].Items[0].Text = "写月报";
            check("second save succeeds", store.Save(reloaded));
            check("backup holds the previous generation", File.Exists(store.BackupPath));
            check("target holds the newest data", new TodoStore(root).Load().Days[0].Items[0].Text == "写月报");
            var backupText = File.ReadAllText(store.BackupPath);
            check("backup holds the older text", backupText.Contains("写周报", StringComparison.Ordinal));
            // JavaScriptSerializer writes raw UTF-8; escaped CJK would still parse
            // but would no longer look like a Windows produced file.
            check("chinese text is written unescaped", !backupText.Contains("\\u5199", StringComparison.Ordinal));

            // Corrupting the primary file falls back to the backup.
            File.WriteAllText(store.DataPath, "{ not json");
            var recovered = new TodoStore(root);
            var loaded = recovered.Load();
            check("corrupt data falls back to the backup", loaded.Days.Count > 0);
            check("fallback is reported", recovered.LoadWarning is not null);

            // Windows style payload is understood and written back unchanged in shape.
            var windowsJson = """
            {
              "Days": [
                {
                  "Date": "2026-08-29",
                  "Items": [
                    { "Id": "abc", "Text": "写周报", "Completed": false,
                      "CreatedAt": "\/Date(1787012345678)\/",
                      "SourceId": null, "RecurringRuleId": "daily0001",
                      "BacklogSourceDate": null, "PreviousOpenIndex": -1 }
                  ],
                  "ImportedSourceIds": ["src1"],
                  "SuppressedRuleIds": ["daily0001"]
                }
              ],
              "BacklogItems": [],
              "LastImportPromptDate": "2026-08-29",
              "RecurringRules": [
                { "Id": "daily0001", "Text": "晨会", "Frequency": "Daily",
                  "ScheduleValue": 0, "Enabled": true, "CreatedDate": "2026-01-05" }
              ],
              "FocusTimer": null
            }
            """;
            var parsed = TodoJson.Deserialize(windowsJson);
            check("windows json parses", parsed is not null && parsed.Days.Count == 1);
            check("windows json keeps the legacy date", parsed!.Days[0].Items[0].CreatedAt != default);
            check("windows json keeps suppression", parsed.Days[0].SuppressedRuleIds.Contains("daily0001"));
            check("windows json keeps imported ids", parsed.Days[0].ImportedSourceIds.Contains("src1"));
            check("windows json keeps the rule", parsed.RecurringRules[0].ScheduleValue == 0);

            var written = TodoJson.Serialize(parsed);
            check("legacy shape is preserved on write", written.Contains("/Date(", StringComparison.Ordinal));
            check("pascal case field names are preserved", written.Contains("\"PreviousOpenIndex\"", StringComparison.Ordinal));
            check("null fields are still written", written.Contains("\"BacklogSourceDate\": null", StringComparison.Ordinal));
            var again = TodoJson.Deserialize(written);
            check("legacy round trip keeps the wall clock",
                again!.Days[0].Items[0].CreatedAt == parsed.Days[0].Items[0].CreatedAt);

            // Sub items must survive a round trip and stay compatible with Windows.
            var withSubs = TodoJson.Deserialize("""
            {
              "Days": [ { "Date": "2026-08-29", "Items": [
                { "Id": "a1", "Text": "发布", "Completed": false,
                  "CreatedAt": "\/Date(1787012345678)\/",
                  "SubItems": [
                    { "Id": "s1", "Text": "写文档", "Completed": true,
                      "CreatedAt": "\/Date(1787012345679)\/" },
                    { "Id": "s2", "Text": "通知团队", "Completed": false,
                      "CreatedAt": "\/Date(1787012345680)\/" }
                  ] } ], "ImportedSourceIds": [], "SuppressedRuleIds": [] } ],
              "BacklogItems": [], "LastImportPromptDate": null, "RecurringRules": [], "FocusTimer": null
            }
            """);
            var subsOwner = withSubs!.Days[0].Items[0];
            check("windows sub items parse", subsOwner.SubItems.Count == 2);
            check("sub item fields are kept",
                subsOwner.SubItems[0].Text == "写文档" && subsOwner.SubItems[0].Completed);
            check("sub item dates are kept", subsOwner.SubItems[0].CreatedAt != default);
            var subsWritten = TodoJson.Serialize(withSubs);
            check("sub items are written back as SubItems",
                subsWritten.Contains("\"SubItems\"", StringComparison.Ordinal));
            var subsAgain = TodoJson.Deserialize(subsWritten);
            check("sub items survive a round trip", subsAgain!.Days[0].Items[0].SubItems.Count == 2);

            // An item without the field still loads, as older files have none.
            var noSubs = TodoJson.Deserialize("""
            { "Days": [ { "Date": "2026-08-29", "Items": [
                { "Id": "b1", "Text": "旧数据", "Completed": false,
                  "CreatedAt": "\/Date(1787012345678)\/" } ],
              "ImportedSourceIds": [], "SuppressedRuleIds": [] } ],
              "BacklogItems": [], "LastImportPromptDate": null, "RecurringRules": [], "FocusTimer": null }
            """);
            check("items without sub items still load", noSubs!.Days[0].Items[0].SubItems.Count == 0);

            // Completing the owner completes every sub item, and the reverse.
            var subDay = new TodoDay { Date = "2026-08-29" };
            var subOwner = new DailyTodoItem { Id = "o1", Text = "发布", CreatedAt = new DateTime(2026, 8, 29) };
            subOwner.SubItems.Add(new TodoSubItem { Id = "s1", Text = "写文档" });
            subOwner.SubItems.Add(new TodoSubItem { Id = "s2", Text = "通知团队", Completed = true });
            subDay.Items.Add(subOwner);
            var subData = new DailyTodoData();
            subData.Days.Add(subDay);
            TodoLogic.Complete(subDay, subOwner, subData);
            check("completing an item completes its sub items",
                subOwner.SubItems.TrueForAll(subItem => subItem.Completed));
            TodoLogic.Uncomplete(subDay, subOwner);
            check("uncompleting clears every sub item",
                subOwner.SubItems.TrueForAll(subItem => !subItem.Completed));

            var now = new DateTime(2026, 8, 29, 14, 30, 15, DateTimeKind.Local);
            check("local round trip is exact",
                LegacyDateTimeConverter.TryParseLegacy(LegacyDateTimeConverter.ToLegacy(now)) == now);
            check("offset variant parses",
                LegacyDateTimeConverter.TryParseLegacy("/Date(1787012345678+0800)/") is not null);

            // Repair mirrors the Windows loader.
            var broken = new DailyTodoData
            {
                Days = [new TodoDay { Items = [new DailyTodoItem { Text = null }] }],
                BacklogItems = [new DailyTodoItem { Text = null, CreatedAt = new DateTime(2026, 8, 20) }],
                RecurringRules = [new RecurringTodoRule { Text = null }]
            };
            TodoStore.Repair(broken);
            check("repair fills ids", !string.IsNullOrEmpty(broken.Days[0].Items[0].Id));
            check("repair fills text", broken.Days[0].Items[0].Text == "");
            check("repair fills lists", broken.Days[0].ImportedSourceIds.Count == 0 && broken.Days[0].SuppressedRuleIds.Count == 0);
            check("repair derives the backlog source date", broken.BacklogItems[0].BacklogSourceDate == "2026-08-20");
            check("repair fills the rule creation date", !string.IsNullOrEmpty(broken.RecurringRules[0].CreatedDate));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void Layout(Action<string, bool> check)
    {
        check("canvas grows with the card stack", TodoLogic.CardCanvasHeight(1) == 330 && TodoLogic.CardCanvasHeight(7) == 6 * 48 + 72 + 8);
        check("card inset stops after five cards", TodoLogic.CardInset(0) == 0 && TodoLogic.CardInset(4) == 12 && TodoLogic.CardInset(9) == 12);
        check("card alpha fades with depth", TodoLogic.CardAlpha(0) == 148 && TodoLogic.CardAlpha(9) == 108);
        check("drop index clamps to the open block",
            TodoLogic.ComputeDropIndex(-50, 3) == 0
            && TodoLogic.ComputeDropIndex(500, 3) == 2
            && TodoLogic.ComputeDropIndex(48, 3) == 1);
        check("drop index with no open items", TodoLogic.ComputeDropIndex(100, 0) == 0);
    }

    /// <summary>
    /// Card geometry and the compact capsule height while a sub item panel is open,
    /// mirroring Windows CardHeightAt / CardTopAt / CompactHeightFor.
    /// </summary>
    private static void SubItemCards(Action<string, bool> check)
    {
        var clock = new FixedClock();
        var ids = new SequentialIds();
        var day = new TodoDay { Date = "2026-08-29" };
        var owner = new DailyTodoItem { Id = "o1", Text = "发布", CreatedAt = clock.Now };
        owner.SubItems.Add(new TodoSubItem { Id = "s1", Text = "写文档" });
        owner.SubItems.Add(new TodoSubItem { Id = "s2", Text = "通知团队", Completed = true });
        owner.SubItems.Add(new TodoSubItem { Id = "s3", Text = "发布公告" });
        var other = new DailyTodoItem { Id = "o2", Text = "其他", CreatedAt = clock.Now };
        day.Items.Add(owner);
        day.Items.Add(other);

        check("collapsed card keeps the base height",
            TodoLogic.CardHeightAt(owner, null, null) == TodoLogic.CardHeight);
        check("a neighbour does not change the card height",
            TodoLogic.CardHeightAt(owner, "o2", null) == TodoLogic.CardHeight);
        check("expanded cards grow by 30 per sub item",
            TodoLogic.CardHeightAt(owner, "o1", null) == TodoLogic.CardHeight + 3 * 30 + 4 + 24);
        check("the revealed input swaps 24 for 31",
            TodoLogic.CardHeightAt(owner, "o1", "o1") == TodoLogic.CardHeight + 3 * 30 + 4 + 31);
        owner.Completed = true;
        check("completed cards keep only the view rows",
            TodoLogic.CardHeightAt(owner, "o1", "o1") == TodoLogic.CardHeight + 3 * 30 + 4);
        check("a completed card has no add control on screen",
            TodoLogic.CardHeightAt(owner, "o1", "o1") == TodoLogic.CardHeightAt(owner, "o1", null));
        owner.Completed = false;

        check("top of the first card ignores its own height",
            TodoLogic.CardTopAt(0, day.Items, "o1", null) == 0);
        check("cards below an expanded card move down",
            TodoLogic.CardTopAt(1, day.Items, "o1", null)
            == TodoLogic.CardStep + TodoLogic.CardHeightAt(owner, "o1", null) - TodoLogic.CardHeight);
        check("cards below a collapsed card keep the deck step",
            TodoLogic.CardTopAt(1, day.Items, null, null) == TodoLogic.CardStep);
        check("the canvas covers the last expanded card",
            TodoLogic.CardCanvasHeight(day.Items, "o1", null)
            == Math.Max(330, TodoLogic.CardTopAt(1, day.Items, "o1", null)
                             + TodoLogic.CardHeightAt(other, "o1", null) + 8));
        check("the canvas keeps its 330 floor", TodoLogic.CardCanvasHeight([], "o1", null) == 330);
        // A tall enough deck shows the expanded card pushing the canvas past the floor.
        var tall = new TodoDay { Date = "2026-08-29" };
        for (var index = 0; index < 7; index++)
            tall.Items.Add(index == 0 ? owner : new DailyTodoItem { Id = "t" + index, Text = "第" + index });
        check("the canvas grows from the expanded card",
            TodoLogic.CardCanvasHeight(tall.Items, "o1", null)
            > TodoLogic.CardCanvasHeight(tall.Items.Count));
        check("the canvas still grows by the panel height",
            TodoLogic.CardCanvasHeight(tall.Items, "o1", null) - TodoLogic.CardCanvasHeight(tall.Items.Count)
            == TodoLogic.CardHeightAt(owner, "o1", null) - TodoLogic.CardHeight);

        check("drop index measures against real card tops",
            TodoLogic.ResolveDropIndex(0, day.Items, 2, "o1", null) == 0
            && TodoLogic.ResolveDropIndex(TodoLogic.CardTopAt(1, day.Items, "o1", null), day.Items, 2, "o1", null) == 1);
        check("drop index clamps past the last open card",
            TodoLogic.ResolveDropIndex(9999, day.Items, 2, "o1", null) == 1);
        check("drop index with no open items", TodoLogic.ResolveDropIndex(50, day.Items, 0, "o1", null) == 0);

        // Capsule height, matching Windows RenderCompactSubItems / CompactHeightFor.
        check("capsule keeps its base height without sub items",
            TodoLogic.CompactHeightFor(null, true) == TodoLogic.CompactBaseHeight
            && TodoLogic.CompactHeightFor(other, true) == TodoLogic.CompactBaseHeight);
        check("capsule grows by the visible sub item rows",
            TodoLogic.CompactHeightFor(owner, true)
            == TodoLogic.CompactBaseHeight + 3 * TodoLogic.CompactSubItemRowHeight + 3);
        check("hiding the list restores the base height",
            TodoLogic.CompactHeightFor(owner, false) == TodoLogic.CompactBaseHeight);
        var many = new DailyTodoItem { Id = "o3", Text = "多" };
        for (var index = 0; index < 6; index++) many.SubItems.Add(new TodoSubItem { Text = "子" + index });
        check("only four sub item rows are visible",
            TodoLogic.CompactSubItemsHeight(6, true) == 4 * TodoLogic.CompactSubItemRowHeight + 3
            && TodoLogic.CompactHeightFor(many, true)
            == TodoLogic.CompactBaseHeight + 4 * TodoLogic.CompactSubItemRowHeight + 3);

        // Toggling one sub item and normalizing a hand edited file.
        TodoLogic.ToggleSubItem(owner.SubItems[0]);
        check("toggling flips one sub item only",
            owner.SubItems[0].Completed && !owner.SubItems[2].Completed);
        var messy = new DailyTodoItem { Id = "m", Text = "x" };
        messy.SubItems = [null!, new TodoSubItem { Text = null }];
        TodoLogic.NormalizeItem(messy);
        check("normalize drops null sub items and fills fields",
            messy.SubItems.Count == 1 && !string.IsNullOrEmpty(messy.SubItems[0].Id) && messy.SubItems[0].Text == "");

        // Imported copies carry their sub items with fresh identities.
        var data = new DailyTodoData();
        var today = TodoLogic.FindDay(data, clock.Today, true)!;
        var source = new DailyTodoItem { Id = "src", Text = "发布", CreatedAt = clock.Now };
        source.SubItems.Add(new TodoSubItem
        {
            Id = "old", Text = "写文档", Completed = true, CreatedAt = clock.Now
        });
        TodoLogic.ImportItems(data, [source], clock.Today, clock, ids);
        var imported = today.Items[0];
        check("import copies the sub items",
            imported.SubItems.Count == 1 && imported.SubItems[0].Text == "写文档" && imported.SubItems[0].Completed);
        check("import gives the copy a fresh sub item id",
            imported.SubItems[0].Id != "old" && !string.IsNullOrEmpty(imported.SubItems[0].Id));
        check("import leaves the source sub items alone", source.SubItems[0].Id == "old");

        var backlogData = new DailyTodoData();
        TodoLogic.ImportItemsToBacklog(backlogData, [source], clock.Today, clock, ids);
        check("backlog import copies the sub items too",
            backlogData.BacklogItems[0].SubItems.Count == 1
            && backlogData.BacklogItems[0].SubItems[0].Text == "写文档");
    }

    /// <summary>The gentle capsule wording and its five minute rotation.</summary>
    private static void CompactMessages(Action<string, bool> check)
    {
        check("an open item wins the capsule",
            new CompactMessageRotation(new Random(1)).Update(new DailyTodoItem { Text = "写周报" }, 3, 1) == "写周报");

        var empty = TodoCompactMessages.BuildCompactMessages(0, 0);
        check("an empty day gets four gentle lines",
            empty.Count == 4 && empty[0] == "今天想先做点什么？" && empty[3] == "今天也给自己留点从容");
        var emptyWithBacklog = TodoCompactMessages.BuildCompactMessages(0, 3);
        check("an empty day with backlog adds two lines",
            emptyWithBacklog.Count == 6
            && emptyWithBacklog[4] == "堆积区有 3 件，挑一件吗？"
            && emptyWithBacklog[5] == "还有 3 件暂存，今天做一点？");
        var done = TodoCompactMessages.BuildCompactMessages(3, 0);
        check("a finished day celebrates the count",
            done.Count == 5 && done[0] == "今天 3 件都完成了，辛苦啦" && done[4] == "今天做得很好，给自己松口气");
        var doneWithBacklog = TodoCompactMessages.BuildCompactMessages(3, 2);
        check("a finished day with backlog adds two lines",
            doneWithBacklog.Count == 7
            && doneWithBacklog[5] == "堆积区还有 2 件，不着急"
            && doneWithBacklog[6] == "想继续的话，可以再挑一件");

        var rotation = new CompactMessageRotation(new Random(7));
        check("the rotation only starts without an item",
            !rotation.HasPriority && rotation.Update(null, 0, 0).Length > 0 && rotation.Messages.Count == 4);
        check("a message hides the priority flag", !rotation.HasPriority);
        var first = rotation.Messages[rotation.Index];
        check("the first message comes from the empty set", empty.Contains(first));
        var openItem = new DailyTodoItem { Text = "临时" };
        var openText = rotation.Update(openItem, 1, 0);
        check("an open item is shown instead of a message", openText == "临时");
        check("an open item raises the priority flag", rotation.HasPriority);
        check("an open item clears the message list", rotation.Messages.Count == 0 && rotation.Index == -1);

        var cycling = new CompactMessageRotation(new Random(7));
        var started = cycling.Update(null, 0, 0);
        var seen = new List<string> { started };
        for (var index = 0; index < empty.Count - 1; index++) seen.Add(cycling.Rotate(null)!);
        check("every message is reachable", seen.Distinct().Count() == empty.Count);
        check("the rotation wraps around", cycling.Rotate(null) == started);
        check("the rotation pauses while an item is open",
            cycling.Rotate(new DailyTodoItem { Text = "写周报" }) is null);

        var finished = new CompactMessageRotation(new Random(3));
        var text = finished.Update(null, 4, 2);
        check("a finished day uses the completed set", doneWithBacklog.Contains(text));
        var next = finished.Update(null, 5, 2);
        check("changed counts rebuild the set",
            TodoCompactMessages.BuildCompactMessages(5, 2).Contains(next));

        check("the flag defaults to showing sub items", new DailyTodoData().ShowCompactSubItems);
        var hidden = TodoJson.Deserialize("{\"ShowCompactSubItems\": false}");
        check("the hidden choice parses", hidden!.ShowCompactSubItems == false);
        check("the hidden choice is written back",
            TodoJson.Serialize(new DailyTodoData { ShowCompactSubItems = false })
                .Contains("\"ShowCompactSubItems\": false", StringComparison.Ordinal));
    }

    private static void Sound(Action<string, bool> check)
    {
        var click = TodoSoundSynthesizer.ClickWav();
        var chime = TodoSoundSynthesizer.ChimeWav();
        check("click sample is a riff wave",
            Encoding.ASCII.GetString(click, 0, 4) == "RIFF" && Encoding.ASCII.GetString(click, 8, 4) == "WAVE");
        check("click sample header sizes agree",
            BitConverter.ToInt32(click, 4) == click.Length - 8 && BitConverter.ToInt32(click, 40) == click.Length - 44);
        check("chime sample is longer than the click", chime.Length > click.Length);
        check("samples are 16 bit mono 44.1k",
            BitConverter.ToInt16(click, 22) == 1 && BitConverter.ToInt16(click, 34) == 16 && BitConverter.ToInt32(click, 24) == 44100);
    }

    private static string TempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "LittleTools-todo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidOperationException("Unsafe test cleanup path.");
        Directory.Delete(root, true);
    }
}
