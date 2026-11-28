using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace SoulLink.Services
{
    [Flags]
    internal enum BgiLogIssues
    {
        None = 0,
        Error = 1,
        Warning = 2,
        Failure = 4
    }

    internal enum BgiGroupEventKind
    {
        Start,
        Finish,
        Fail,
        Cancel
    }

    internal readonly record struct BgiGroupEvent(int Position, BgiGroupEventKind Kind,
        string Name, int ScriptCount, string Result);

    internal static class BgiLogParser
    {
        internal static readonly Regex GroupStart = new(
            @"配置组\s*""(?<g>[^""]+)""\s*加载完成[，,]\s*共\s*(?<n>\d+)\s*个脚本[，,]\s*开始执行", RegexOptions.Compiled);
        internal static readonly Regex GroupFinish = new(
            @"配置组\s*""(?<g>[^""]+)""\s*执行结束", RegexOptions.Compiled);
        internal static readonly Regex GroupFail = new(
            @"配置组\s*""(?<g>[^""]+)""\s*未成功完成[，,]?\s*结果\s*[:：]\s*(?<r>\S*)", RegexOptions.Compiled);
        internal static readonly Regex GroupCancel = new(
            @"取消执行配置组\s*[:：]\s*""(?<g>[^""]+)""", RegexOptions.Compiled);

        internal static readonly Regex Pick = new(
            @"交互或拾取\s*[:：]\s*""(?<item>[^""]+)""", RegexOptions.Compiled);
        internal static readonly Regex Exp = new(
            @"(?<!JS 通知关闭，消息被拦截:\s*)(?:狗粮路线获取狗粮经验\s*[:：]\s*(?<v1>\d+)|[\u4e00-\u9fa5]{1,6}可获得(?<v2>\d+)经验)",
            RegexOptions.Compiled);
        internal static readonly Regex Mora = new(
            @"(?<!JS 通知关闭，消息被拦截:\s*)(?:狗粮路线获取摩拉\s*[:：]\s*(?<v1>\d+)|[\u4e00-\u9fa5]{1,6}获得(?<v2>\d+)摩拉(?!鼬))",
            RegexOptions.Compiled);

        internal static readonly Regex RouteLoopEnter = new(
            @"进入路线循环\s*[,，]?\s*count\s*=\s*(?<c>\d+)\s*[,，]?\s*route\s*=\s*""(?<name>[^""]+)""",
            RegexOptions.Compiled);
        internal static readonly Regex ScriptEndPlain = new(
            @"→\s*脚本执行结束\s*[:：]\s*""(?<name>[^""]+)""", RegexOptions.Compiled);

        private const string Timestamp = @"\[(?<date>\d{4}-\d{2}-\d{2}[ T])?(?<time>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]";
        internal static readonly Regex LineTimestamp = new(
            "^" + Timestamp + @"\s*(?:\[(?<level>[A-Za-z]{3,5})\])?", RegexOptions.Compiled);

        internal static bool TryFileDate(string sourceFile, out DateTime day)
        {
            day = default;
            if (string.IsNullOrWhiteSpace(sourceFile)) return false;
            var name = Path.GetFileNameWithoutExtension(sourceFile.AsSpan());
            int prefix = name.LastIndexOf("impact", StringComparison.OrdinalIgnoreCase);
            if (prefix < 0) return false;
            var date = name[(prefix + "impact".Length)..];
            if (date.Length < 8 || (date.Length > 8 && char.IsDigit(date[8]))) return false;
            return DateTime.TryParseExact(date[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out day) && day.Year is >= 2000 and <= 2999;
        }

        internal static bool TryWallTime(Match stamp, DateTime now, DateTime? fileDay, out DateTime at)
        {
            at = default;
            if (!stamp.Success) return false;
            var date = stamp.Groups["date"];
            var time = stamp.Groups["time"].ValueSpan;
            if (!date.Success)
            {
                if (!TimeSpan.TryParse(time, CultureInfo.InvariantCulture, out var elapsed)
                    || elapsed >= TimeSpan.FromDays(1)) return false;
                at = (fileDay ?? now).Date.Add(elapsed);
                if (!fileDay.HasValue && at > now.AddSeconds(2)) at = at.AddDays(-1);
                return true;
            }

            return DateTime.TryParse(
                string.Concat(date.ValueSpan[..^1], " ", time), CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out at);
        }

        internal static bool ValidGroupName(string name)
            => !string.IsNullOrWhiteSpace(name)
                && !name.Equals("The operation was canceled.", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("OperationCanceledException", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Exception", StringComparison.OrdinalIgnoreCase);

        internal static long RewardValue(Match m)
        {
            if (long.TryParse(m.Groups["v1"].ValueSpan, out var a) && a > 0) return a;
            return long.TryParse(m.Groups["v2"].ValueSpan, out var b) && b > 0 ? b : 0;
        }

        internal static BgiLogIssues FindIssues(ReadOnlySpan<char> line)
        {
            var issues = BgiLogIssues.None;
            if (line.Contains("[ERR]", StringComparison.Ordinal)) issues |= BgiLogIssues.Error;
            else if (line.Contains("[WRN]", StringComparison.Ordinal)) issues |= BgiLogIssues.Warning;
            if (line.Contains("执行失败", StringComparison.Ordinal)
                || line.Contains("未成功完成", StringComparison.Ordinal)
                || line.Contains("一条龙前置动作未成功", StringComparison.Ordinal)) issues |= BgiLogIssues.Failure;
            return issues;
        }
    }

    /// <summary>A bounded decoded chunk, shared by the log consumers for one account and file.</summary>
    internal sealed class BgiLogBatch
    {
        private BgiLogBatchLine[]? _lines;
        private List<BgiGroupEvent>? _groupEvents;
        private MatchCollection? _pickMatches, _expMatches, _moraMatches;
        private readonly DateTime _previousTimestamp;

        internal BgiLogBatch(string text, string sourceFile = "", DateTime previousTimestamp = default)
        {
            Text = text;
            Now = DateTime.Now;
            FileDay = BgiLogParser.TryFileDate(sourceFile, out var fileDay) ? fileDay : null;
            _previousTimestamp = previousTimestamp;
        }

        internal string Text { get; }
        internal DateTime Now { get; }
        internal DateTime? FileDay { get; }
        internal BgiLogBatchLine[] Lines => _lines ??= SplitLines();
        internal MatchCollection PickMatches => _pickMatches ??= BgiLogParser.Pick.Matches(Text);
        internal MatchCollection ExpMatches => _expMatches ??= BgiLogParser.Exp.Matches(Text);
        internal MatchCollection MoraMatches => _moraMatches ??= BgiLogParser.Mora.Matches(Text);
        internal IReadOnlyList<BgiGroupEvent> GroupEvents => _groupEvents ??= ReadGroupEvents();

        internal bool TryWallTime(Match timestamp, out DateTime at)
            => BgiLogParser.TryWallTime(timestamp, Now, FileDay, out at);

        internal DateTime TimestampAt(int position)
        {
            var lines = Lines;
            int lo = 0, hi = lines.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (lines[mid].Start <= position) lo = mid + 1;
                else hi = mid;
            }
            for (int i = lo - 1; i >= 0; i--)
                if (TryWallTime(lines[i].Timestamp, out var at)) return at;
            return _previousTimestamp;
        }

        private BgiLogBatchLine[] SplitLines()
        {
            var rawLines = Text.Split('\n');
            var lines = new BgiLogBatchLine[rawLines.Length];
            int start = 0;
            for (int i = 0; i < rawLines.Length; i++)
            {
                var raw = rawLines[i];
                lines[i] = new BgiLogBatchLine(start, raw);
                start += raw.Length + 1;
            }
            return lines;
        }

        private List<BgiGroupEvent> ReadGroupEvents()
        {
            var events = new List<BgiGroupEvent>();
            AddEvents(BgiLogParser.GroupStart, BgiGroupEventKind.Start);
            AddEvents(BgiLogParser.GroupFinish, BgiGroupEventKind.Finish);
            AddEvents(BgiLogParser.GroupFail, BgiGroupEventKind.Fail);
            AddEvents(BgiLogParser.GroupCancel, BgiGroupEventKind.Cancel);
            events.Sort((a, b) => a.Position.CompareTo(b.Position));
            return events;

            void AddEvents(Regex regex, BgiGroupEventKind kind)
            {
                for (var m = regex.Match(Text); m.Success; m = m.NextMatch())
                {
                    string name = m.Groups["g"].Value.Trim();
                    int count = kind == BgiGroupEventKind.Start && int.TryParse(m.Groups["n"].ValueSpan, out int n) ? n : 0;
                    string result = kind == BgiGroupEventKind.Fail ? m.Groups["r"].Value.Trim() : "";
                    events.Add(new BgiGroupEvent(m.Index, kind, name, count, result));
                }
            }
        }
    }

    internal struct BgiLogBatchLine(int start, string raw)
    {
        private string? _text;
        private Match? _timestamp, _pick;
        private BgiLogIssues? _issues;

        internal int Start { get; } = start;
        internal string Raw { get; } = raw;
        internal string Text => _text ??= Raw.TrimEnd('\r');
        internal Match Timestamp => _timestamp ??= Raw.StartsWith('[') ? BgiLogParser.LineTimestamp.Match(Text) : Match.Empty;
        internal Match Pick => _pick ??= BgiLogParser.Pick.Match(Text);
        internal BgiLogIssues Issues => _issues ??= BgiLogParser.FindIssues(Raw);
    }
}
