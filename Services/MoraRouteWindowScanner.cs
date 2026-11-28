using System;
using System.Collections.Generic;
using System.IO;

namespace SoulLink.Services
{
    /// <summary>
    /// BGI 日志路线窗口扫描（t17 归属档二）：从日志正文提取
    /// 「[DEBUG] 进入路线循环，count=N，route="xxx.json"」→「→ 脚本执行结束: "xxx.json"」
    /// 的秒级时间窗，供摩拉记录（分钟精度）做低置信归属。只读文件，无状态。
    /// </summary>
    internal static class MoraRouteWindowScanner
    {
        /// <summary>扫一份日志文件，按文件日期生成 Unix 毫秒路线窗口。</summary>
        internal static List<MoraClassifier.RouteWindow> Scan(string file)
        {
            var result = new List<MoraClassifier.RouteWindow>();
            if (!BgiLogParser.TryFileDate(file, out var day) || !File.Exists(file)) return result;
            long dayEndMs = new DateTimeOffset(day.AddDays(1)).ToUnixTimeMilliseconds();

            var open = new Dictionary<string, (long start, string group)>(StringComparer.OrdinalIgnoreCase);
            string currentGroup = "";

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is string line)
            {
                if (line.Length == 0) continue;

                // 组名粘性：配置组行
                if (line.Contains("配置组", StringComparison.Ordinal) && line.Contains("加载完成", StringComparison.Ordinal))
                {
                    int q1 = line.IndexOf('"');
                    int q2 = q1 >= 0 ? line.IndexOf('"', q1 + 1) : -1;
                    if (q1 >= 0 && q2 > q1) currentGroup = line.Substring(q1 + 1, q2 - q1 - 1);
                }

                var enter = BgiLogParser.RouteLoopEnter.Match(line);
                var anchor = enter.Success ? enter : BgiLogParser.ScriptEndPlain.Match(line);
                if (!anchor.Success || !BgiLogParser.TryWallTime(BgiLogParser.LineTimestamp.Match(line),
                        day, day, out var at) || at.Date != day) continue;

                long lineMs = new DateTimeOffset(at).ToUnixTimeMilliseconds();
                string name = anchor.Groups["name"].Value.Trim();
                if (name.Length == 0) continue;
                if (enter.Success)
                {
                    open.TryAdd(name, (lineMs, currentGroup));
                    continue;
                }

                if (open.Remove(name, out var window) && lineMs > window.start)
                {
                    result.Add(new MoraClassifier.RouteWindow
                    {
                        RouteName = name, GroupName = window.group, StartMs = window.start, EndMs = lineMs,
                    });
                }
            }
            // 未闭合窗口：最多保留 30 分钟，限于日志文件当天。
            foreach (var kv in open)
            {
                result.Add(new MoraClassifier.RouteWindow
                {
                    RouteName = kv.Key, GroupName = kv.Value.group,
                    StartMs = kv.Value.start, EndMs = Math.Min(kv.Value.start + 30 * 60_000, dayEndMs),
                });
            }
            return result;
        }

    }
}
