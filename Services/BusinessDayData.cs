using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>同一份原始日志的只读业务日投影；00:00 与 04:00 独立缓存。</summary>
    public sealed class BusinessDayView
    {
        public string DayKey { get; init; } = "";
        public int BoundaryHour { get; init; }
        public long Version;
        public ScriptGroupTracker Groups { get; init; } = new();
        public DailyStats Stats { get; init; } = new();
        public DogfoodRecord Dogfood { get; init; } = new();
        public string RecentLog { get; set; } = "";
    }

    /// <summary>共享采集与派生缓存；投影不读取文件，也不依赖流程数量。</summary>
    public static class BusinessDayData
    {
        private sealed class Entry
        {
            public string File = "";
            public string Text = "";
            public string PreviousText = "";
            public string PreviousFile = "";
            public List<DogfoodRecord> Records = new();
            public readonly Dictionary<string, BusinessDayView> Views = new(StringComparer.OrdinalIgnoreCase);
            public long Version;
        }
        private static readonly object Sync = new();
        private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex Prefix = new(@"^\[(?<date>\d{4}-\d{2}-\d{2}[ T])?(?<time>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]", RegexOptions.Compiled);
        public static long SourceReads { get; private set; }
        public static long ProjectionBuilds { get; private set; }

        public static void Reset(string accountId, string logFile, string text, string previousText = "", string previousFile = "")
        {
            if (string.IsNullOrWhiteSpace(accountId)) return;
            lock (Sync)
            {
                var e = Get(accountId);
                e.File = logFile ?? ""; e.Text = text ?? ""; e.PreviousText = previousText ?? ""; e.PreviousFile = previousFile ?? "";
                e.Views.Clear(); e.Version++; SourceReads++;
            }
        }
        public static void Append(string accountId, string logFile, string chunk)
        {
            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrEmpty(chunk)) return;
            lock (Sync)
            {
                var e = Get(accountId);
                if (!string.Equals(e.File, logFile, StringComparison.OrdinalIgnoreCase)) { e.File = logFile ?? ""; e.Text = ""; e.PreviousText = ""; }
                if (string.Equals(logFile, e.PreviousFile, StringComparison.OrdinalIgnoreCase)) e.PreviousText += chunk;
                else e.Text += chunk;
                e.Version++; SourceReads++;
            }
        }
        public static void SetDogfood(string accountId, string recordFile, List<DogfoodRecord> records)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return;
            lock (Sync)
            {
                var e = Get(accountId); e.Records = records ?? new List<DogfoodRecord>();
                e.Views.Clear(); e.Version++; SourceReads++;
            }
        }
        public static BusinessDayView GetView(string accountId, int boundaryHour, DateTime now)
        {
            boundaryHour = PlcLogic.NormalizeBoundaryHour(boundaryHour);
            string day = PlcLogic.DayKey(now, boundaryHour);
            lock (Sync)
            {
                var e = Get(accountId);
                if (e.Views.TryGetValue(day + "|" + boundaryHour, out var cached) && cached.Version == e.Version) return cached;
                string text = Filter(e.PreviousText + "\n" + e.Text, now, boundaryHour);
                var groups = new ScriptGroupTracker(); groups.Update(text);
                var record = e.Records.Select(r => DogfoodRecordReader.ForDay(r, now, boundaryHour))
                    .FirstOrDefault(r => r.IsValid) ?? DogfoodRecordReader.ForDay(e.Records.FirstOrDefault() ?? new DogfoodRecord(), now, boundaryHour);
                var view = new BusinessDayView
                {
                    DayKey = day, BoundaryHour = boundaryHour, Version = e.Version, Groups = groups,
                    Stats = DailyStatsCache.Calculate(text, DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    Dogfood = record, RecentLog = Tail(text, 400)
                };
                if (e.Views.Count > 8)
                    foreach (var old in e.Views.Where(kv => kv.Key != day + "|" + boundaryHour).Take(e.Views.Count - 8).Select(kv => kv.Key).ToList()) e.Views.Remove(old);
                e.Views[day + "|" + boundaryHour] = view;
                ProjectionBuilds++;
                return view;
            }
        }
        internal static string Filter(string text, DateTime now, int boundaryHour)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string day = PlcLogic.DayKey(now, boundaryHour);
            var sb = new StringBuilder();
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                if (!TryTimestamp(raw, now, out var at)) continue;
                if (at <= now.AddSeconds(2) && PlcLogic.DayKey(at, boundaryHour) == day) sb.AppendLine(raw);
            }
            return sb.ToString();
        }
        internal static bool TryTimestamp(string line, DateTime now, out DateTime at)
        {
            at = default;
            var m = Prefix.Match(line); if (!m.Success) return false;
            string date = m.Groups["date"].Value.Trim();
            string value = (date.Length > 0 ? date + " " : now.ToString("yyyy-MM-dd") + " ") + m.Groups["time"].Value;
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out at)) return false;
            if (date.Length == 0 && at > now.AddSeconds(2)) at = at.AddDays(-1);
            return true;
        }
        private static string Tail(string text, int count)
        {
            if (text.Length == 0) return "";
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - count))) + "\n";
        }
        private static Entry Get(string id)
        {
            if (!Entries.TryGetValue(id, out var e)) { e = new Entry(); Entries[id] = e; }
            return e;
        }
        internal static void ResetForTests() { lock (Sync) { Entries.Clear(); SourceReads = 0; ProjectionBuilds = 0; } }
    }
}
