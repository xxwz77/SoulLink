using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using SoulLink.Models;
using System.Text.RegularExpressions;

namespace SoulLink.Services
{
    /// <summary>狗粮当天的收尾路线结论。</summary>
    public enum DogfoodEndingRoute
    {
        /// <summary>无法判断（无记录 / 非当日 / 文件缺失）。</summary>
        Unknown = 0,
        /// <summary>收尾额外A —— 走联机，自己不拾取。</summary>
        EndingA = 1,
        /// <summary>收尾额外B —— 自己跑完，需要跳转别的一条龙。</summary>
        EndingB = 2
    }

    /// <summary>一次狗粮记录的读取结果。</summary>
    public sealed class DogfoodRecord
    {
        /// <summary>结论。</summary>
        public DogfoodEndingRoute Route = DogfoodEndingRoute.Unknown;
        /// <summary>记录日期（yyyy/MM/dd）。</summary>
        public string Date = "";
        /// <summary>当日经验。</summary>
        public long Exp;
        /// <summary>当日摩拉（仅展示，不参与判断）。</summary>
        public long Mora;
        /// <summary>记录所属的"业务日"（04:00 分界后的日期）。</summary>
        public DateTime BusinessDay = DateTime.MinValue;
        /// <summary>结论是否有效（读到、且是当个业务日）。</summary>
        public bool IsValid;
        /// <summary>无效时的原因，便于日志/界面说明。</summary>
        public string Reason = "";

        /// <summary>经验是否已达到该路线的参考上限（达到后不必再解析日志）。</summary>
        public bool ExpReachedLimit => IsValid && Exp >= LimitFor(Route);

        /// <summary>按路线返回经验参考上限。</summary>
        public static long LimitFor(DogfoodEndingRoute route)
            => route == DogfoodEndingRoute.EndingA ? 130000L : 50000L;
    }

    /// <summary>
    /// 读取 AAA-Artifacts-Bulk-Supply 的 records 文件，判断**当天**的收尾路线。
    ///
    /// ★ 数据来源（实测确认）：
    ///   &lt;BGI&gt;/User/JsScript/AAA-Artifacts-Bulk-Supply/records/&lt;accountName&gt;.txt
    ///   内容形如：
    ///     日期:2026/11/17，运行收尾路线收尾额外A，狗粮经验142480，摩拉38957
    ///
    /// ★ 为什么用它：
    ///   这行由狗粮脚本在整轮跑完时写入（main.js 第 190 行 writeRecord），
    ///   是脚本自己算出的最终结论，不需要复现算法，也不受日志滚动影响。
    ///   A = 走联机（自己不拾取）；B = 自己跑（应跳转别的一条龙）。
    ///
    /// ★ 当日判定（严格）：
    ///   BGI 以北京时间 04:00 作为一天的分界（凌晨 1 点仍算前一天）。
    ///   这里用同样的分界计算"业务日"，只有记录日期 == 当前业务日才算有效，
    ///   绝不接受昨天的数据 —— 否则会出现"昨天B线，今天误触发跳转"。
    /// </summary>
    public static partial class DogfoodRecordReader
    {
        /// <summary>只解析 日期 + 收尾路线；经验/摩拉是可选的附加信息。</summary>
        [GeneratedRegex(@"日期\s*[:：]\s*(?<date>\d{4}/\d{2}/\d{2})\s*[，,]\s*运行收尾路线\s*(?<route>收尾额外[AB])", RegexOptions.Compiled)]
        private static partial Regex RxDaily();

        /// <summary>经验（可选，用于显示与上限判断）。</summary>
        [GeneratedRegex(@"狗粮经验\s*(?<exp>-?\d+)", RegexOptions.Compiled)]
        private static partial Regex RxExp();
        [GeneratedRegex(@"摩拉\s*(?<mora>-?\d+)", RegexOptions.Compiled)]
        private static partial Regex RxMora();

        /// <summary>狗粮脚本目录名（固定）。</summary>
        public const string ScriptFolder = "AAA-Artifacts-Bulk-Supply";

        /// <summary>BGI 默认日界；流程可显式选 00:00。</summary>
        public const int DayBoundaryHour = 4;

        /// <summary>
        /// 计算当前"业务日"：04:00 之前算前一天。
        /// </summary>
        public static DateTime CurrentBusinessDay(DateTime? now = null, int boundaryHour = DayBoundaryHour)
            => PlcLogic.BusinessDay(now ?? DateTime.Now, boundaryHour);

        /// <summary>从 BetterGI.exe 目录推导 records 文件路径。</summary>
        public static string ResolveRecordPath(string? bgiExePath, string? accountName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(bgiExePath)) return "";
                string dir = Path.GetDirectoryName(bgiExePath) ?? "";
                if (string.IsNullOrWhiteSpace(dir)) return "";

                string name = string.IsNullOrWhiteSpace(accountName) ? "默认账户" : accountName.Trim();
                return Path.Combine(dir, "User", "JsScript", ScriptFolder, "records", name + ".txt");
            }
            catch { return ""; }
        }

        /// <summary>从配置组 JSON 读出 accountName（项目里可能是任意名字）。</summary>
        public static string ResolveAccountName(string? bgiExePath, string? scriptGroupName = "狗粮批发")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(bgiExePath)) return "";
                string dir = Path.GetDirectoryName(bgiExePath) ?? "";
                string groupFile = Path.Combine(dir, "User", "ScriptGroup",
                    (string.IsNullOrWhiteSpace(scriptGroupName) ? "狗粮批发" : scriptGroupName) + ".json");
                if (!File.Exists(groupFile)) return "";

                string text = File.ReadAllText(groupFile);
                var m = Regex.Match(text, @"""accountName""\s*:\s*""(?<n>[^""]*)""");
                return m.Success ? m.Groups["n"].Value.Trim() : "";
            }
            catch { return ""; }
        }

        /// <summary>解析全部原始记录；是否属于“今日”由调用方的日界决定。</summary>
        public static List<DogfoodRecord> Parse(string recordFile)
        {
            FileReadCount++;
            var records = new List<DogfoodRecord>();
            try
            {
                if (string.IsNullOrWhiteSpace(recordFile) || !File.Exists(recordFile)) return records;

                // 最新记录在文件最前面，只读前 64KB 即可
                string head;
                using (var fs = new FileStream(recordFile, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    int take = (int)Math.Min(fs.Length, 64 * 1024);
                    var buf = new byte[take];
                    int got = fs.Read(buf, 0, take);
                    head = System.Text.Encoding.UTF8.GetString(buf, 0, got);
                }

                var all = RxDaily().Matches(head);
                foreach (Match m0 in all)
                {
                var record = new DogfoodRecord
                {
                    Date = m0.Groups["date"].Value,
                    Route = m0.Groups["route"].Value == "收尾额外A" ? DogfoodEndingRoute.EndingA : DogfoodEndingRoute.EndingB
                };

                // 经验/摩拉：从该条记录所在行取（仅在原文里找，避免跨行串值）
                try
                {
                    int pos = m0.Index;
                    int nl = head.IndexOf('\n', pos);
                    string line = nl > pos ? head.Substring(pos, nl - pos) : head.Substring(pos);
                    var me = RxExp().Match(line);
                    if (me.Success) long.TryParse(me.Groups["exp"].Value, out record.Exp);
                    var mm = RxMora().Match(line);
                    if (mm.Success) long.TryParse(mm.Groups["mora"].Value, out record.Mora);
                }
                catch { }

                if (DateTime.TryParseExact(record.Date, "yyyy/MM/dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var day)) record.BusinessDay = day;
                records.Add(record);
                }
                return records;
            }
            catch { return records; }
        }
        /// <summary>把原始记录投影到指定业务日，不改变原记录。</summary>
        public static DogfoodRecord ForDay(DogfoodRecord source, DateTime now, int boundaryHour)
        {
            var cur = PlcLogic.BusinessDay(now, boundaryHour);
            var r = new DogfoodRecord { Date=source.Date, Route=source.Route, Exp=source.Exp, Mora=source.Mora, BusinessDay=source.BusinessDay };
            if (r.BusinessDay != cur)
            {
                r.Reason = r.BusinessDay == DateTime.MinValue ? "没有可识别的记录" : "最近记录是 " + r.Date + "，不是当前业务日 " + cur.ToString("yyyy/MM/dd");
                r.Route = DogfoodEndingRoute.Unknown;
            }
            else r.IsValid = true;
            return r;
        }
        public static DogfoodRecord Read(string recordFile, DateTime? now = null, int boundaryHour = DayBoundaryHour)
        {
            var time = now ?? DateTime.Now;
            var records = Parse(recordFile);
            var result = records.Select(r => ForDay(r, time, boundaryHour)).FirstOrDefault(r => r.IsValid)
                ?? ForDay(records.FirstOrDefault() ?? new DogfoodRecord(), time, boundaryHour);
            if (records.Count == 0) result.Reason = "找不到记录文件或没有可识别的条目";
            return result;
        }
        public static List<DogfoodRecord> ReadAllForBgi(string? bgiExePath, out string recordFile, string? scriptGroupName = "狗粮批发")
        {
            string account = ResolveAccountName(bgiExePath, scriptGroupName);
            recordFile = ResolveRecordPath(bgiExePath, account);
            return Parse(recordFile);
        }
        internal static long FileReadCount;
        public static DogfoodRecord ReadForBgi(string? bgiExePath, string? scriptGroupName = "狗粮批发", DateTime? now = null, int boundaryHour = DayBoundaryHour)
        {
            var records = ReadAllForBgi(bgiExePath, out _, scriptGroupName);
            var time = now ?? DateTime.Now;
            return records.Select(r => ForDay(r, time, boundaryHour)).FirstOrDefault(r => r.IsValid)
                ?? ForDay(records.FirstOrDefault() ?? new DogfoodRecord(), time, boundaryHour);
        }
    }
}
