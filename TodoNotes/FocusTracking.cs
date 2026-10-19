using System;
using System.Collections.Generic;
using System.Globalization;

namespace LittleTools.DailyTodo
{
    internal sealed class FocusInterval
    {
        public string StartedAtUtc;
        public string EndedAtUtc;
    }

    internal static class FocusTracking
    {
        private static string Stamp(DateTime value) { return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }

        private static bool Read(string value, out DateTime utc)
        {
            DateTime parsed;
            bool valid = DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed);
            utc = valid ? parsed.ToUniversalTime() : DateTime.MinValue;
            return valid;
        }

        // Old countdowns only stored their deadline; infer their first active interval.
        public static bool Normalize(FocusTimerData timer)
        {
            if (timer == null || timer.DurationMinutes <= 0 || timer.DurationMinutes > 100 ||
                string.IsNullOrEmpty(timer.ItemId)) return false;
            DateTime end;
            if (!Read(timer.EndsAtUtc, out end)) return false;
            if (timer.Intervals == null) timer.Intervals = new List<FocusInterval>();
            DateTime start;
            if (!Read(timer.SegmentStartedAtUtc, out start))
                timer.SegmentStartedAtUtc = Stamp(end.AddMinutes(-timer.DurationMinutes));
            return true;
        }

        public static double Remaining(FocusTimerData timer, DateTime nowUtc)
        {
            if (timer == null) return 0;
            if (timer.Paused) return Math.Max(0, timer.PausedRemainingSeconds);
            DateTime end;
            return Read(timer.EndsAtUtc, out end) ? Math.Max(0, (end - nowUtc).TotalSeconds) : 0;
        }

        public static FocusTimerData Start(DailyTodoItem item, int minutes, DateTime nowUtc)
        {
            minutes = Math.Max(1, Math.Min(100, minutes));
            return new FocusTimerData
            {
                ItemId = item.Id, ItemText = item.Text, DurationMinutes = minutes,
                SegmentStartedAtUtc = Stamp(nowUtc), EndsAtUtc = Stamp(nowUtc.AddMinutes(minutes))
            };
        }

        private static void CloseInterval(FocusTimerData timer, DateTime nowUtc)
        {
            if (timer.Paused) return;
            DateTime start, deadline;
            if (!Read(timer.SegmentStartedAtUtc, out start) || !Read(timer.EndsAtUtc, out deadline)) return;
            DateTime end = nowUtc < deadline ? nowUtc : deadline;
            if (end > start) timer.Intervals.Add(new FocusInterval { StartedAtUtc = Stamp(start), EndedAtUtc = Stamp(end) });
        }

        public static void TogglePause(FocusTimerData timer, DateTime nowUtc)
        {
            if (!Normalize(timer)) return;
            if (timer.Paused)
            {
                timer.SegmentStartedAtUtc = Stamp(nowUtc);
                timer.EndsAtUtc = Stamp(nowUtc.AddSeconds(timer.PausedRemainingSeconds));
                timer.Paused = false;
            }
            else
            {
                timer.PausedRemainingSeconds = Remaining(timer, nowUtc);
                CloseInterval(timer, nowUtc);
                timer.Paused = true;
            }
        }

        // Settle only once, and only at completion or an explicit end. Pauses remain pending.
        public static double Finish(DailyTodoData data, DateTime nowUtc, TimeZoneInfo zone)
        {
            FocusTimerData timer = data.FocusTimer;
            data.FocusTimer = null;
            if (!Normalize(timer)) return 0;
            CloseInterval(timer, nowUtc);
            double total = 0;
            foreach (FocusInterval interval in timer.Intervals)
            {
                DateTime start, end;
                if (interval == null || !Read(interval.StartedAtUtc, out start) || !Read(interval.EndedAtUtc, out end) || end <= start) continue;
                total += (end - start).TotalSeconds;
                while (start < end)
                {
                    DateTime localDate = TimeZoneInfo.ConvertTimeFromUtc(start, zone).Date;
                    DateTime midnight = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate.AddDays(1), DateTimeKind.Unspecified), zone);
                    DateTime sliceEnd = end < midnight ? end : midnight;
                    string key = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    TodoDay day = data.Days.Find(delegate(TodoDay value) { return value.Date == key; });
                    if (day == null) { day = new TodoDay { Date = key }; data.Days.Add(day); }
                    day.FocusSeconds += (sliceEnd - start).TotalSeconds;
                    start = sliceEnd;
                }
            }
            DailyTodoItem item = data.BacklogItems.Find(delegate(DailyTodoItem value) { return value.Id == timer.ItemId; });
            foreach (TodoDay day in data.Days)
            {
                if (item != null) break;
                item = day.Items.Find(delegate(DailyTodoItem value) { return value.Id == timer.ItemId; });
            }
            if (item != null) item.FocusSeconds += total;
            return total;
        }

        public static string Describe(double seconds)
        {
            if (seconds <= 0) return "0 分钟";
            int minutes = (int)Math.Floor(seconds / 60);
            if (minutes == 0) return "不足 1 分钟";
            if (minutes < 60) return minutes + " 分钟";
            return (minutes / 60) + " 小时" + (minutes % 60 == 0 ? "" : " " + (minutes % 60) + " 分钟");
        }
    }
}
