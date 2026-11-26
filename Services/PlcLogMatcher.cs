using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace SoulLink.Services
{
    /// <summary>按 BGI 日志每行时间戳匹配；固定行数不再冒充“最近 N 秒”。</summary>
    public static class PlcLogMatcher
    {
        private static readonly ConcurrentDictionary<string, Regex> PatternCache = new(StringComparer.Ordinal);
        private static readonly Regex Prefix = new(@"^\[(?<date>\d{4}-\d{2}-\d{2}[ T])?(?<time>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]\s*(?:\[(?<level>[A-Za-z]{3,5})\])?", RegexOptions.Compiled);
        public static bool Contains(string text, string pattern, bool regex, string level, int seconds, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            Regex? rx = null;
            try
            {
                if (regex)
                {
                    rx = PatternCache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(300)));
                    if (PatternCache.Count > 128) PatternCache.Clear();
                }
            }
            catch { return false; }
            return Scan(text, level, seconds, now, line => rx?.IsMatch(line) == true || (!regex && line.Contains(pattern, StringComparison.OrdinalIgnoreCase)));
        }
        public static bool ValidPattern(string pattern, bool regex)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            if (!regex) return true;
            try { _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(300)); return true; } catch { return false; }
        }

        /// <summary>
        /// 对**一行**文本判断是否命中（不查时间戳）。
        /// 供事件化触点逐行消费使用；时间窗电平查询继续用 <see cref="Contains"/>。
        /// </summary>
        public static bool MatchText(string line, string pattern, bool regex, string level)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            Regex? rx = null;
            try
            {
                if (regex)
                {
                    rx = PatternCache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(300)));
                    if (PatternCache.Count > 128) PatternCache.Clear();
                }
            }
            catch { return false; }
            var m = Prefix.Match(line);
            if (m.Success && level.Length > 0 && !string.Equals(m.Groups["level"].Value, level, StringComparison.OrdinalIgnoreCase))
                return false;
            return rx?.IsMatch(line) == true || (!regex && line.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }

        public static bool HasError(string text, int seconds, DateTime now)
            => Scan(text, "", seconds, now, line => line.Contains("[ERR]", StringComparison.OrdinalIgnoreCase) || line.Contains("执行失败", StringComparison.Ordinal) || line.Contains("未成功完成", StringComparison.Ordinal) || line.Contains("发生异常", StringComparison.Ordinal));
        private static bool Scan(string text, string level, int seconds, DateTime now, Func<string,bool> match)
        {
            if (string.IsNullOrEmpty(text)) return false;
            DateTime? lastAt = null;
            string lastLevel = "";
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim(); if (line.Length == 0) continue;
                var m = Prefix.Match(line);
                if (m.Success)
                {
                    // 带时间戳的行：更新基线；续行沿用上一个带时间戳行的时刻与级别。
                    if (TryTimestamp(m, now, out var parsed)) lastAt = parsed;
                    if (m.Groups["level"].Value.Length > 0) lastLevel = m.Groups["level"].Value;
                }
                // ★ 2026-11-26 修复：续行（无时间戳）不再被直接跳过，而是沿用
                //   上一行时间戳参与时间窗匹配。BGI 日志大量关键输出是续行
                //   （如「循环测试」这类脚本打点），旧逻辑把整行 continue 掉，
                //   导致「日志包含」永远匹配不到续行内容。
                if (lastAt == null) continue;   // 尚未遇到任何带时间戳的行，无法定位
                var at = lastAt.Value;
                if (at > now.AddSeconds(2) || at < now.AddSeconds(-Math.Max(1, seconds))) continue;
                if (level.Length > 0 && !string.Equals(lastLevel, level, StringComparison.OrdinalIgnoreCase)) continue;
                if (match(line)) return true;
            }
            return false;
        }
        private static bool TryTimestamp(Match m, DateTime now, out DateTime at)
        {
            at = default;
            string date = m.Groups["date"].Value.Trim(), time = m.Groups["time"].Value;
            string text = date.Length > 0 ? date + " " + time : now.ToString("yyyy-MM-dd") + " " + time;
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out at)) return false;
            if (date.Length == 0 && at > now.AddSeconds(2)) at = at.AddDays(-1); // 跨零点只有时分秒
            return true;
        }
    }
}