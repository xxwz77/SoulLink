using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SoulLink.Services
{
    /// <summary>
    /// BGI 日志路线窗口扫描（t17 归属档二）：从日志正文提取
    /// 「[DEBUG] 进入路线循环，count=N，route="xxx.json"」→「→ 脚本执行结束: "xxx.json"」
    /// 的秒级时间窗，供摩拉记录（分钟精度）做低置信归属。只读文件，无状态。
    /// </summary>
    internal static class MoraRouteWindowScanner
    {
        private static readonly Regex RxTs = new(
            @"^\[(?<time>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]", RegexOptions.Compiled);
        private static readonly Regex RxRouteEnter = new(
            @"进入路线循环\s*[,，]?\s*count\s*=\s*(?<c>\d+)\s*[,，]?\s*route\s*=\s*""(?<name>[^""]+)""",
            RegexOptions.Compiled);
        private static readonly Regex RxRouteEnd = new(
            @"→\s*脚本执行结束\s*[:：]\s*""(?<name>[^""]+)""", RegexOptions.Compiled);

        /// <summary>扫一份日志文件，产出路线窗口列表（本地时区 ms）。</summary>
        internal static List<MoraClassifier.RouteWindow> Scan(string file)
        {
            var result = new List<MoraClassifier.RouteWindow>();
            if (!File.Exists(file)) return result;

            string fileName = Path.GetFileNameWithoutExtension(file);
            if (!TryFileDay(fileName, out long dayMs)) return result;

            long lastRaw = long.MinValue;
            var open = new Dictionary<string, (long start, string group)>(StringComparer.OrdinalIgnoreCase);
            string currentGroup = "";

            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                long lineMs = 0;
                var m = RxTs.Match(line);
                if (m.Success)
                {
                    if (TimeSpan.TryParse(m.Groups["time"].Value, CultureInfo.InvariantCulture, out var ts))
                    {
                        long rawMs = dayMs + (long)ts.TotalMilliseconds;
                        if (lastRaw != long.MinValue && rawMs < lastRaw - 1_000) rawMs += 86_400_000;
                        lastRaw = rawMs;
                        lineMs = rawMs;
                    }
                }

                // 组名粘性：配置组行
                if (line.Contains("配置组", StringComparison.Ordinal) && line.Contains("加载完成", StringComparison.Ordinal))
                {
                    int q1 = line.IndexOf('"');
                    int q2 = q1 >= 0 ? line.IndexOf('"', q1 + 1) : -1;
                    if (q1 >= 0 && q2 > q1) currentGroup = line.Substring(q1 + 1, q2 - q1 - 1);
                }

                var enter = RxRouteEnter.Match(line);
                if (enter.Success && lineMs > 0)
                {
                    string name = enter.Groups["name"].Value.Trim();
                    if (name.Length > 0 && !open.ContainsKey(name))
                        open[name] = (lineMs, currentGroup);
                    continue;
                }

                var end = RxRouteEnd.Match(line);
                if (end.Success && lineMs > 0)
                {
                    string name = end.Groups["name"].Value.Trim();
                    if (name.Length > 0 && open.TryGetValue(name, out var w))
                    {
                        result.Add(new MoraClassifier.RouteWindow
                        {
                            RouteName = name, GroupName = w.group, StartMs = w.start, EndMs = lineMs,
                        });
                        open.Remove(name);
                    }
                }
            }
            // 未闭合窗口：宽容到窗口起点 + 30 分钟
            foreach (var kv in open)
            {
                result.Add(new MoraClassifier.RouteWindow
                {
                    RouteName = kv.Key, GroupName = kv.Value.group,
                    StartMs = kv.Value.start, EndMs = kv.Value.start + 30 * 60_000,
                });
            }
            return result;
        }

        private static bool TryFileDay(string name, out long dayMs)
        {
            dayMs = 0;
            int at = name.LastIndexOf("impact", StringComparison.OrdinalIgnoreCase);
            string digits = at >= 0 && at + 6 <= name.Length ? name.Substring(at + 6) : "";
            digits = new string(digits.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length != 8) return false;
            if (!int.TryParse(digits[..4], out int y)
                || !int.TryParse(digits.Substring(4, 2), out int mo)
                || !int.TryParse(digits.Substring(6, 2), out int d)) return false;
            try
            {
                var day = new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Unspecified);
                if (day.Year < 2000 || day.Year > 2999) return false;
                dayMs = (long)(day - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)).TotalMilliseconds;
                return true;
            }
            catch { return false; }
        }
    }
}
