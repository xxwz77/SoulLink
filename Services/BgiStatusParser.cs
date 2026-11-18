using System;
using System.Text.RegularExpressions;

namespace SoulLink.Services
{
    /// <summary>
    /// 从 BGI 日志里解析"运行状态"。
    ///
    /// 实测（2026-10-27 ~ 11-16 共 21 份日志）覆盖到以下**多种格式**，
    /// 必须全部兼容，不能只认一种：
    ///
    ///  ① 联机锄地（有轮次）
    ///     [第 2/3 轮 思] "当前进度：开始第 1/78 条线路: "00-B001璃月沉玉谷南陵传奇.json"，
    ///                    本线路预计用时 0时2分11秒，本轮预计剩余 2时3分40秒
    ///
    ///  ② 每日采集（无轮次，用"任务 + 路线 + CD"）
    ///     当前进度：任务「每日采集」，执行路线「03-霜仙花-料洛列夫茨基剧院-5个」；
    ///             路线共 1440 条，CD中 481 条；任务累计执行时长：1时13分03秒
    ///
    ///  ③ 纯数字进度（无轮次、无任务名）
    ///     当前进度：纳塔-安饶之野东(进度: 48/94)
    ///     当前进度：15个
    ///
    ///  ④ 一条龙配置（任务级）
    ///     启用一条龙配置：灭绝提瓦特
    ///
    ///  ⑤ 配置组（拾取诊断里带出来）
    ///     本体模板拾取诊断 v2：... 配置组="CD采集-全采集"
    ///     总文件夹="精英混合一条龙"（旧格式）
    ///
    /// 所有值都取"最近一次出现"，缺失就留空，界面显示 —，不猜。
    /// </summary>
    public sealed class BgiStatus
    {
        // ── 任务与进度 ─────────────────────────────────────────────────

        /// <summary>任务名，例如 "每日采集" / "24小时狗粮"。</summary>
        public string TaskName { get; set; } = "";
        /// <summary>轮次，例如 "2/3"（只有联机锄地这类任务才有）。</summary>
        public string Round { get; set; } = "";
        /// <summary>多开时的角色名，例如 "思"。</summary>
        public string RoleName { get; set; } = "";
        /// <summary>线路进度，例如 "1/78"。</summary>
        public string RouteProgress { get; set; } = "";
        /// <summary>当前路线名，例如 "00-B001璃月沉玉谷南陵传奇"。</summary>
        public string RouteFile { get; set; } = "";
        /// <summary>路线总数，例如 "1440"（每日采集用）。</summary>
        public string RouteTotal { get; set; } = "";
        /// <summary>CD 中数量，例如 "481"。</summary>
        public string RouteCd { get; set; } = "";
        /// <summary>已累计执行时长，例如 "1时13分03秒"。</summary>
        public string Elapsed { get; set; } = "";
        /// <summary>本线路预计用时。</summary>
        public string RouteEta { get; set; } = "";
        /// <summary>本轮预计剩余。</summary>
        public string RoundRemain { get; set; } = "";
        /// <summary>纯数字进度时的区域名，例如 "纳塔-安饶之野东"。</summary>
        public string AreaName { get; set; } = "";
        /// <summary>极简计数进度，例如 "9"（对应日志"当前进度：9个"）。</summary>
        public string PickCount { get; set; } = "";

        /// <summary>
        /// 当前**配置组**累计拾取到的物品统计（名称 → 次数）。
        ///
        /// ★ 从"本路线"改为"本配置组"：用户看挂机收获时关心的是
        ///   这一轮跑下来总共捡了什么，而不是某条 1 分钟小路线捡了什么
        ///   （绝大多数路线只有 0~1 件，按路线统计几乎永远是空的）。
        ///   只有当配置组真正切换时才清空。
        /// </summary>
        public System.Collections.Generic.Dictionary<string, int> RoutePicks { get; }
            = new System.Collections.Generic.Dictionary<string, int>();

        /// <summary>拾取统计对应的配置组名（变了才清空重新累计）。</summary>
        public string PicksRoute { get; set; } = "";

        /// <summary>
        /// 今天每个配置组各捡了什么（配置组名 → 物品 → 次数）。
        /// 用于"点击展开整天"。
        /// </summary>
        public System.Collections.Generic.Dictionary<string,
            System.Collections.Generic.Dictionary<string, int>> DailyByGroup { get; }
            = new System.Collections.Generic.Dictionary<string,
                System.Collections.Generic.Dictionary<string, int>>();

        /// <summary>本轮的配置组名（任务启动时记录）。</summary>
        public string RoundGroup { get; set; } = "";

        /// <summary>
        /// 本轮是否在跑。任务结束 → false。
        /// 用来判断"本轮拾取"是否还有意义。
        /// </summary>
        public bool RoundRunning { get; set; }

        /// <summary>全天分组拾取的展示文字（多行）。</summary>
        public string DailyByGroupDisplay
        {
            get
            {
                if (DailyByGroup.Count == 0) return "";
                var sb = new System.Text.StringBuilder();
                foreach (var g in DailyByGroup)
                {
                    int total = 0;
                    var items = new System.Collections.Generic.List<(string name, int n)>();
                    foreach (var kv in g.Value)
                    {
                        items.Add((kv.Key, kv.Value));
                        total += kv.Value;
                    }
                    if (total == 0) continue;

                    items.Sort((a, b) => b.n.CompareTo(a.n));
                    var parts = new System.Collections.Generic.List<string>();
                    int shown = 0;
                    foreach (var it in items)
                    {
                        if (shown++ >= 5) { parts.Add("…"); break; }
                        parts.Add(it.n > 1 ? (it.name + "×" + it.n) : it.name);
                    }
                    sb.AppendLine(g.Key + "（" + total + " 件）");
                    sb.AppendLine("  " + string.Join(" · ", parts));
                }
                return sb.ToString().TrimEnd();
            }
        }

        /// <summary>配置组的物品汇总文字，例如 "污秽的面具×3 · 史莱姆清×2"。</summary>
        public string PicksDisplay
        {
            get
            {
                if (RoutePicks.Count == 0) return "";

                // 按次数从多到少排序，最值钱的/最多的排前面
                var items = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>();
                foreach (var kv in RoutePicks) items.Add(kv);
                items.Sort((a, b) => b.Value.CompareTo(a.Value));

                var list = new System.Collections.Generic.List<string>();
                foreach (var kv in items)
                    list.Add(kv.Value > 1 ? (kv.Key + "×" + kv.Value) : kv.Key);
                // 最多显示 12 种（原来 8 种，用户反馈显示不全）
                if (list.Count > 12)
                {
                    int more = list.Count - 12;
                    list = list.GetRange(0, 12);
                    list.Add("…等 " + (6 + more) + " 种");
                }
                return string.Join(" · ", list);
            }
        }

        /// <summary>本路线拾取总件数。</summary>
        public int PickTotal
        {
            get
            {
                int n = 0;
                foreach (var kv in RoutePicks) n += kv.Value;
                return n;
            }
        }

        // ── 每日报错统计 ───────────────────────────────────────────────

        /// <summary>当天 ERR 级别错误数（脚本异常、任务失败等）。</summary>
        public int ErrorCount { get; set; }
        /// <summary>当天警告数（WRN：识别失败、坐标丢失等，通常是可自动恢复的小问题）。</summary>
        public int WarnCount { get; set; }
        /// <summary>当天任务级失败次数（执行失败 / 未成功完成 / 一条龙失败）。</summary>
        public int FailCount { get; set; }

        /// <summary>报错摘要文字，例如 "错误 18 · 失败 3 · 警告 1810"。</summary>
        public string ErrorDisplay
        {
            get
            {
                if (ErrorCount == 0 && FailCount == 0 && WarnCount == 0) return "";
                var parts = new System.Collections.Generic.List<string>();
                if (ErrorCount > 0) parts.Add("错误 " + ErrorCount);
                if (FailCount > 0) parts.Add("失败 " + FailCount);
                if (WarnCount > 0) parts.Add("警告 " + WarnCount);
                return string.Join(" · ", parts);
            }
        }

        /// <summary>报错严重程度：0=无 1=仅警告 2=有错误 3=有失败。</summary>
        public int ErrorLevel
        {
            get
            {
                if (FailCount > 0) return 3;
                if (ErrorCount > 0) return 2;
                if (WarnCount > 0) return 1;
                return 0;
            }
        }

        // ── 配置与队伍 ─────────────────────────────────────────────────

        /// <summary>一条龙配置名，例如 "灭绝提瓦特"。</summary>
        public string OneDragonConfig { get; set; } = "";
        /// <summary>配置组 / 路线总文件夹，例如 "CD采集-全采集"。</summary>
        public string ConfigGroup { get; set; } = "";
        /// <summary>战斗脚本名，例如 "联机锄地-融合"。</summary>
        public string FightScript { get; set; } = "";
        /// <summary>当前队伍，例如 "联机锄地"。</summary>
        public string Team { get; set; } = "";

        // ── 异常 ───────────────────────────────────────────────────────

        /// <summary>失败的配置组名。</summary>
        public string FailedGroup { get; set; } = "";
        /// <summary>最近的异常文本（已去掉堆栈）。</summary>
        public string LastError { get; set; } = "";
        /// <summary>是否已出现"任务结束"。</summary>
        public bool TaskEnded { get; set; }
        /// <summary>最近一次异常发生后，是否又恢复了正常进度。</summary>
        public bool Recovered { get; set; }

        // ── 正则 ───────────────────────────────────────────────────────

        // ① 联机锄地：[第 2/3 轮 思] "...开始第 1/78 条线路: "xxx.json"，本线路预计用时 ...
        private static readonly Regex RxRound = new Regex(
            @"\[第\s*(?<round>\d+\s*/\s*\d+)\s*轮\s*(?<role>[^\]]*)\]" +
            @".*?开始第\s*(?<cur>\d+)\s*/\s*(?<total>\d+)\s*条线路" +
            @".*?""(?<file>[^""]+)""",
            RegexOptions.Compiled);

        private static readonly Regex RxEta = new Regex(
            @"本线路预计用时\s*(?<v>[0-9\u65f6\u5206\u79d2]+)", RegexOptions.Compiled);
        private static readonly Regex RxRemain = new Regex(
            @"本轮预计剩余\s*(?<v>[0-9\u65f6\u5206\u79d2]+)", RegexOptions.Compiled);

        // ② 每日采集：任务「每日采集」，执行路线「03-霜仙花-xx」；路线共 1440 条，CD中 481 条；任务累计执行时长：1时13分03秒
        private static readonly Regex RxTaskRoute = new Regex(
            @"任务「(?<task>[^」]+)」\s*[,，]\s*执行路线「(?<route>[^」]+)」" +
            @"(?:.*?路线共\s*(?<total>\d+)\s*条)?" +
            @"(?:.*?CD中\s*(?<cd>\d+)\s*条)?" +
            @"(?:.*?任务累计执行时长\s*[:：]\s*(?<elapsed>[0-9\u65f6\u5206\u79d2]+))?",
            RegexOptions.Compiled);

        // ③ 纯数字进度：当前进度：纳塔-安饶之野东(进度: 48/94)
        private static readonly Regex RxAreaProgress = new Regex(
            @"当前进度\s*[:：]\s*(?<area>[^\s(（]+)\s*[(（]\s*进度\s*[:：]\s*(?<cur>\d+)\s*/\s*(?<total>\d+)\s*[)）]",
            RegexOptions.Compiled);

        // ③b 极简计数进度：当前进度：9个 / 当前进度：19个（实测 20261027 等文件尾部就是这种）
        private static readonly Regex RxCountProgress = new Regex(
            @"当前进度\s*[:：]\s*(?<n>\d+)\s*个\s*$", RegexOptions.Compiled);

        // ③c 地图追踪任务（实测最高频，占启动行 1 万+ 次）：
        //   → 开始执行地图追踪任务: "A06-欧蒂克莱尔左侧-8个.json"
        //   注意：这条**不带**"当前进度：任务「X」，执行路线「Y」"的成对行，
        //   所以原来完全解析不到，界面显示"等待日志中的状态"（用户实测）。
        private static readonly Regex RxMapTrace = new Regex(
            @"开始执行地图追踪任务\s*[:：]\s*""(?<route>[^""]+)""" + @"|开始执行地图追踪任务\s*[:：]\s*(?<route2>[^\r\n""]+)",
            RegexOptions.Compiled);

        // ③d 独立 JS 脚本任务：→ 开始执行JS脚本: "xxx.js"
        private static readonly Regex RxJsScript = new Regex(
            @"开始执行JS脚本\s*[:：]\s*""(?<name>[^""]+)""", RegexOptions.Compiled);

        // ③e 独立任务：→ 开始执行独立任务: "xxx"
        private static readonly Regex RxStandalone = new Regex(
            @"开始执行独立任务\s*[:：]\s*""(?<name>[^""]+)""", RegexOptions.Compiled);

        // ③f 一条龙任务启动：锄地一条龙任务启动 [配置组: "X"]
        private static readonly Regex RxOneDragonStart2 = new Regex(
            @"一条龙任务启动\s*\[配置组\s*[:：]\s*""(?<g>[^""]+)""\]", RegexOptions.Compiled);

        // ★ 当前动作：BGI 的很多任务只输出动作，不输出"路线/进度"。
        //   例如：开始挖矿 / 动作：下落攻击 / 成功切换角色:"莉奈娅"
        //   原来这类行全部识别不到 → 界面一直显示"等待日志中的状态信息"
        //   （用户实测 A1 明明在挖矿，面板却是空的）。
        private static readonly Regex RxCurrentAction = new Regex(
            @"^s*(?<act>开始[^，。
]{1,20}|动作[:：]s*(?<a2>[^
]{1,20}?)(?:s*结束)?|成功切换角色s*[:：]s*""?(?<role>[^""
]{1,16}))s*$",
            RegexOptions.Compiled);

        // 简单的"动作:"行（单独匹配便于取到动作名）
        private static readonly Regex RxActionLine = new Regex(
            @"动作[:：]s*(?<name>[^
]{1,24})", RegexOptions.Compiled);

        // ⑥ 拾取物品：交互或拾取："霜仙花"
        private static readonly Regex RxPick = new Regex(
            @"交互或拾取\s*[:：]\s*""(?<item>[^""]+)""", RegexOptions.Compiled);

        // ④ 一条龙配置
        private static readonly Regex RxOneDragon = new Regex(
            @"启用一条龙配置\s*[:：]\s*(?<v>.+?)\s*$", RegexOptions.Compiled);

        // ⑤ 配置组（两种写法都见过）
        private static readonly Regex RxConfigGroup = new Regex(
            @"配置组\s*=\s*""(?<g>[^""]+)""|总文件夹\s*=\s*""(?<g2>[^""]+)""",
            RegexOptions.Compiled);

        private static readonly Regex RxFight = new Regex(
            @"匹配到战斗脚本\s*[:：]\s*""(?<v>[^""]+)""", RegexOptions.Compiled);
        private static readonly Regex RxTeam = new Regex(
            @"当前队伍名称\s*[:：]\s*""(?<v>[^""]+)""", RegexOptions.Compiled);

        // 失败/异常
        private static readonly Regex RxFailGroup = new Regex(
            @"配置组\s*""(?<g>[^""]+)""\s*未成功完成", RegexOptions.Compiled);
        private static readonly Regex RxTaskEnd = new Regex(
            @"→\s*""任务结束""", RegexOptions.Compiled);
        private static readonly Regex RxError = new Regex(
            @"执行脚本时发生异常\s*[:：]\s*""(?<e>[^""]+)""", RegexOptions.Compiled);
        // 一条龙失败：一条龙执行失败 / 一条龙前置动作未成功
        private static readonly Regex RxOneDragonFail = new Regex(
            @"一条龙(?:前置动作未成功|执行失败)", RegexOptions.Compiled);
        // 游戏启动准备失败
        private static readonly Regex RxStartFail = new Regex(
            @"游戏启动准备失败[^""]*""(?<e>[^""]+)""", RegexOptions.Compiled);

        // 一条龙开始/完成（判断"是否在跑"）
        private static readonly Regex RxOneDragonStart = new Regex(
            @"→\s*开始(?:执行)?一条龙|开始执行一条龙", RegexOptions.Compiled);
        private static readonly Regex RxOneDragonDone = new Regex(
            @"一条龙(?:任务)?(?:完成|结束)", RegexOptions.Compiled);

        // ── 解析 ───────────────────────────────────────────────────────

        /// <summary>
        /// 在日志增量中更新状态（后出现的覆盖先出现的）。
        /// </summary>
        public void Update(string logChunk)
        {
            if (string.IsNullOrEmpty(logChunk)) return;
            try
            {
                var lines = logChunk.Split('\n');
                for (int idx = 0; idx < lines.Length; idx++)
                {
                    string line = lines[idx];
                    if (line.Length == 0) continue;

                    // 把「本行 + 下一行」也作为候选：BGI 头行与正文分属两个物理行
                    string merged = (idx + 1 < lines.Length) ? (line + " " + lines[idx + 1]) : line;

                    bool matched = false;

                    // ★ 当前动作：BGI 大量任务是"只报动作不报进度"的
                    //   （挖矿、下落攻击、切换角色…），之前完全没解析，
                    //   于是界面一直停在"等待日志中的状态信息"（用户实测）。
                    var am = RxActionLine.Match(merged);
                    if (am.Success)
                    {
                        string act = am.Groups["name"].Value.Trim();
                        // "下落攻击 结束" 这种收尾行不算当前动作
                        if (act.EndsWith("结束", StringComparison.Ordinal)) act = "";
                        if (act.Length > 0) { CurrentAction = act; matched = true; }
                    }
                    else
                    {
                        // "开始挖矿" 这类独立动作行
                        int iStart = merged.IndexOf("开始", StringComparison.Ordinal);
                        if (iStart >= 0)
                        {
                            int iEnd = merged.IndexOfAny(new[] { '，', '。', '\r' }, iStart);
                            string seg = iEnd > iStart
                                ? merged.Substring(iStart, iEnd - iStart)
                                : merged.Substring(iStart);
                            seg = seg.Trim();
                            if (seg.Length >= 3 && seg.Length <= 20 && !seg.Contains("开始第"))
                            {
                                CurrentAction = seg;
                                matched = true;
                            }
                        }

                        var rm = Regex.Match(merged, @"成功切换角色s*[:：]s*""?(?<r>[^""s，。]{1,16})");
                        if (rm.Success)
                        {
                            CurrentAction = "切换角色 " + rm.Groups["r"].Value;
                            matched = true;
                        }
                    }

                    // ① 联机锄地（带轮次）
                    var m = RxRound.Match(merged);
                    if (m.Success)
                    {
                        Round = m.Groups["round"].Value.Replace(" ", "");
                        RoleName = m.Groups["role"].Value.Trim();
                        RouteProgress = m.Groups["cur"].Value + "/" + m.Groups["total"].Value;

                        // ★ 拾取统计按**配置组**累计，不再按单条路线清空。
                        //   路线大多只捡 0~1 件，按路线统计几乎永远是空的；
                        //   用户真正关心的是"这一轮总共捡了什么"。
                        RouteFile = StripJson(m.Groups["file"].Value);

                        RouteEta = Maybe(RxEta.Match(merged), "v");
                        RoundRemain = Maybe(RxRemain.Match(merged), "v");
                        AreaName = "";
                        Recovered = true;   // 有正常进度 = 已恢复
                        matched = true;
                    }

                    // ② 每日采集（任务 + 路线 + CD）
                    if (!matched)
                    {
                        var t = RxTaskRoute.Match(merged);
                        if (t.Success)
                        {
                            TaskName = t.Groups["task"].Value;
                            RouteFile = t.Groups["route"].Value;
                            RouteTotal = t.Groups["total"].Success ? t.Groups["total"].Value : RouteTotal;
                            RouteCd = t.Groups["cd"].Success ? t.Groups["cd"].Value : RouteCd;
                            Elapsed = t.Groups["elapsed"].Success ? t.Groups["elapsed"].Value : Elapsed;
                            Round = "";          // 这类任务没有轮次
                            RouteProgress = "";
                            Recovered = true;
                            matched = true;
                        }
                    }

                    // ③ 纯数字区域进度
                    if (!matched)
                    {
                        var a = RxAreaProgress.Match(merged);
                        if (a.Success)
                        {
                            AreaName = a.Groups["area"].Value;
                            RouteProgress = a.Groups["cur"].Value + "/" + a.Groups["total"].Value;
                            Recovered = true;
                            matched = true;
                        }
                    }

                    // ③c 地图追踪任务：→ 开始执行地图追踪任务: "xxx.json"
                    //     ★ 这是实测最高频的格式（1 万+ 次），原来完全没解析，
                    //       导致 RouteFile 永远是空 → 界面只能显示"已拾取 N 个"。
                    if (!matched)
                    {
                        var mt = RxMapTrace.Match(line);
                        if (mt.Success)
                        {
                            string route = mt.Groups["route"].Success
                                ? mt.Groups["route"].Value
                                : mt.Groups["route2"].Value;
                            route = route.Trim();
                            if (route.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                route = route.Substring(0, route.Length - 5);
                            RouteFile = route;
                            TaskName = "地图追踪";
                            RouteProgress = "";
                            // 新任务开始 → 本路线拾取数清零
                            PickCount = "";
                            Recovered = true;
                            matched = true;
                        }
                    }

                    // ③d JS 脚本 / 独立任务
                    if (!matched)
                    {
                        var js = RxJsScript.Match(line);
                        if (js.Success)
                        {
                            RouteFile = js.Groups["name"].Value;
                            TaskName = "JS脚本";
                            Recovered = true;
                            matched = true;
                        }
                        else
                        {
                            var sd = RxStandalone.Match(line);
                            if (sd.Success)
                            {
                                RouteFile = sd.Groups["name"].Value;
                                TaskName = "独立任务";
                                Recovered = true;
                                matched = true;
                            }
                        }
                    }

                    // ③e 一条龙任务启动 [配置组: "X"]
                    if (!matched)
                    {
                        var od = RxOneDragonStart2.Match(line);
                        if (od.Success)
                        {
                            OneDragonConfig = od.Groups["g"].Value;
                            Recovered = true;
                            matched = true;
                        }
                    }

                    // ③b 极简计数（只有"当前进度：9个"，没有路线名和总数）
                    if (!matched)
                    {
                        var c = RxCountProgress.Match(line);
                        if (c.Success)
                        {
                            PickCount = c.Groups["n"].Value;
                            Recovered = true;
                            matched = true;
                        }
                    }

                    // ④ 一条龙配置
                    if (!matched)
                    {
                        var od = RxOneDragon.Match(line);
                        if (od.Success)
                        {
                            OneDragonConfig = od.Groups["v"].Value.Trim();
                            matched = true;
                        }
                    }

                    // ⑤ 配置组（拾取诊断 / 旧格式）
                    if (!matched)
                    {
                        var cg = RxConfigGroup.Match(line);
                        if (cg.Success)
                        {
                            string g = cg.Groups["g"].Success ? cg.Groups["g"].Value : cg.Groups["g2"].Value;
                            if (g != "无")
                            {
                                // ★ 配置组真的换了 → 清空拾取统计，重新累计新一轮
                                if (!string.IsNullOrEmpty(g) && g != PicksRoute)
                                {
                                    RoutePicks.Clear();
                                    PicksRoute = g;
                                }
                                ConfigGroup = g;
                            }
                            matched = true;
                        }
                    }

                    if (!matched)
                    {
                        var fs = RxFight.Match(line);
                        if (fs.Success) { FightScript = fs.Groups["v"].Value; matched = true; }
                    }
                    if (!matched)
                    {
                        var tm = RxTeam.Match(line);
                        if (tm.Success) { Team = tm.Groups["v"].Value; matched = true; }
                    }

                    // ⑦ 每日报错计数（按行统计，与状态解析互不影响）
                    if (line.IndexOf("[ERR]", StringComparison.Ordinal) >= 0)
                    {
                        ErrorCount++;
                    }
                    else if (line.IndexOf("[WRN]", StringComparison.Ordinal) >= 0)
                    {
                        WarnCount++;
                    }
                    // 任务级失败：日志正文行（不含级别前缀）
                    if (line.IndexOf("执行失败", StringComparison.Ordinal) >= 0
                        || line.IndexOf("未成功完成", StringComparison.Ordinal) >= 0
                        || line.IndexOf("一条龙前置动作未成功", StringComparison.Ordinal) >= 0)
                    {
                        FailCount++;
                    }

                    // ⑥ 拾取物品（累计到本路线）。高频日志，必须放在最后且不打断 matched。
                    var pk = RxPick.Match(line);
                    if (pk.Success)
                    {
                        string item = pk.Groups["item"].Value.Trim();
                        // 过滤掉明显的无效值
                        if (item.Length > 0 && item != "无有效结果")
                        {
                            if (RoutePicks.TryGetValue(item, out int cnt))
                                RoutePicks[item] = cnt + 1;
                            else
                                RoutePicks[item] = 1;
                        }
                    }

                    // ── 异常（优先级高，单独判定，不受 matched 影响）──

                    var fg = RxFailGroup.Match(line);
                    if (fg.Success)
                    {
                        FailedGroup = fg.Groups["g"].Value;
                        Recovered = false;
                        continue;
                    }

                    var se = RxError.Match(line);
                    if (se.Success)
                    {
                        LastError = Clean(se.Groups["e"].Value);
                        Recovered = false;
                        continue;
                    }

                    var odf = RxOneDragonFail.Match(line);
                    if (odf.Success)
                    {
                        LastError = Clean(line);
                        Recovered = false;
                        continue;
                    }

                    var sf = RxStartFail.Match(line);
                    if (sf.Success)
                    {
                        LastError = Clean(sf.Groups["e"].Value);
                        Recovered = false;
                        continue;
                    }

                    if (RxTaskEnd.IsMatch(line))
                    {
                        TaskEnded = true;
                        continue;
                    }
                    if (RxOneDragonDone.IsMatch(line))
                    {
                        TaskEnded = true;
                        continue;
                    }
                }
            }
            catch { }
        }

        private static string Maybe(Match m, string name)
            => m.Success && m.Groups[name].Success ? m.Groups[name].Value : "";

        private static string StripJson(string s)
            => s.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 5) : s;

        /// <summary>压缩异常文本：去掉路径前缀和多余空白，过长截断。</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            if (s.Length > 90) s = s.Substring(0, 87) + "…";
            return s;
        }

        // ── 展示用 ─────────────────────────────────────────────────────

        /// <summary>
        /// 当前正在做的事（挖矿 / 下落攻击 / 切换角色 等）。
        /// ★ 很多任务只输出动作、不输出路线进度，没有这个字段界面就会一片空白。
        /// </summary>
        public string CurrentAction { get; private set; } = "";

        /// <summary>
        /// 从另一个解析结果里补齐"结构性"字段（配置组 / 任务名 / 一条龙 / 轮次 / 路线）。
        /// 仅在本对象缺这些字段时采用，避免覆盖更新的数据。
        /// 拾取与报错统计不在这里处理（由各自流程维护）。
        /// </summary>
        public void MergeStructuralFrom(BgiStatus other)
        {
            if (other == null) return;

            if (string.IsNullOrEmpty(ConfigGroup) && !string.IsNullOrEmpty(other.ConfigGroup))
                ConfigGroup = other.ConfigGroup;
            if (string.IsNullOrEmpty(OneDragonConfig) && !string.IsNullOrEmpty(other.OneDragonConfig))
                OneDragonConfig = other.OneDragonConfig;
            if (string.IsNullOrEmpty(TaskName) && !string.IsNullOrEmpty(other.TaskName))
                TaskName = other.TaskName;
            if (string.IsNullOrEmpty(FightScript) && !string.IsNullOrEmpty(other.FightScript))
                FightScript = other.FightScript;
            if (string.IsNullOrEmpty(RouteFile) && !string.IsNullOrEmpty(other.RouteFile))
                RouteFile = other.RouteFile;
            if (string.IsNullOrEmpty(Round) && !string.IsNullOrEmpty(other.Round))
                Round = other.Round;
            if (string.IsNullOrEmpty(RoleName) && !string.IsNullOrEmpty(other.RoleName))
                RoleName = other.RoleName;
            if (string.IsNullOrEmpty(AreaName) && !string.IsNullOrEmpty(other.AreaName))
                AreaName = other.AreaName;
            if (string.IsNullOrEmpty(CurrentAction) && !string.IsNullOrEmpty(other.CurrentAction))
                CurrentAction = other.CurrentAction;
        }

        /// <summary>是否已有可用状态。</summary>
        public bool HasData =>
            !string.IsNullOrEmpty(Round) || !string.IsNullOrEmpty(TaskName)
            || !string.IsNullOrEmpty(OneDragonConfig) || !string.IsNullOrEmpty(ConfigGroup)
            || !string.IsNullOrEmpty(FightScript) || !string.IsNullOrEmpty(RouteFile)
            || !string.IsNullOrEmpty(AreaName) || !string.IsNullOrEmpty(PickCount)
            || !string.IsNullOrEmpty(CurrentAction) || HasProblem;

        /// <summary>配置组 / 脚本 / 一条龙配置（优先配置组）。</summary>
        public string GroupDisplay
        {
            get
            {
                if (!string.IsNullOrEmpty(ConfigGroup)) return ConfigGroup;
                if (!string.IsNullOrEmpty(OneDragonConfig)) return OneDragonConfig;
                if (!string.IsNullOrEmpty(FightScript)) return FightScript;
                if (!string.IsNullOrEmpty(TaskName)) return TaskName;
                return "—";
            }
        }

        /// <summary>轮次/任务行：有轮次显示轮次，否则显示任务名。</summary>
        public string RoundDisplay
        {
            get
            {
                if (!string.IsNullOrEmpty(Round))
                {
                    string r = "第 " + Round + " 轮";
                    if (!string.IsNullOrEmpty(RoleName)) r += " · " + RoleName;
                    return r;
                }
                if (!string.IsNullOrEmpty(TaskName)) return TaskName;
                if (!string.IsNullOrEmpty(OneDragonConfig)) return OneDragonConfig;
                return "—";
            }
        }

        /// <summary>
        /// 轮次行：有轮次显示"第 X/Y 轮"，否则显示任务名。
        /// ★ 原来无轮次时返回 "—"，界面据此显示"等待日志中的状态…"，
        ///   而地图追踪这类任务本来就没有轮次 —— 会误导成"没解析到"。
        /// </summary>
        public string SafeRoundDisplay
        {
            get
            {
                string r = RoundDisplay;
                if (r != "—") return r;
                if (!string.IsNullOrWhiteSpace(TaskName)) return TaskName;
                return "";
            }
        }

        /// <summary>路线行：兼容三种进度写法。</summary>
        public string RouteDisplay
        {
            get
            {
                // 有路线名 + 进度（联机锄地）
                if (!string.IsNullOrEmpty(RouteFile) && !string.IsNullOrEmpty(RouteProgress))
                    return RouteFile + "  [" + RouteProgress + "]";

                // 每日采集：路线名 + 共N条 / CD中M条
                if (!string.IsNullOrEmpty(RouteFile))
                {
                    string s = RouteFile;
                    if (!string.IsNullOrEmpty(RouteTotal))
                        s += "  共 " + RouteTotal + " 条";
                    if (!string.IsNullOrEmpty(RouteCd))
                        s += " · CD中 " + RouteCd;
                    return s;
                }

                // 纯数字区域进度
                if (!string.IsNullOrEmpty(AreaName) && !string.IsNullOrEmpty(RouteProgress))
                    return AreaName + "  [" + RouteProgress + "]";
                if (!string.IsNullOrEmpty(RouteProgress))
                    return "[" + RouteProgress + "]";

                // 极简计数（只有"当前进度：9个"）
                if (!string.IsNullOrEmpty(PickCount)) return "已拾取 " + PickCount + " 个";
                if (!string.IsNullOrEmpty(TaskName)) return "任务：" + TaskName + "（已结束或无详细进度）";

                if (!string.IsNullOrEmpty(OneDragonConfig)) return "一条龙：" + OneDragonConfig;
                return "—";
            }
        }

        /// <summary>附加信息行（预计时间 / 累计时长 / 队伍）。</summary>
        public string EtaDisplay
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrEmpty(RouteEta)) parts.Add("本线路 " + RouteEta);
                if (!string.IsNullOrEmpty(RoundRemain)) parts.Add("本轮剩余 " + RoundRemain);
                if (!string.IsNullOrEmpty(Elapsed)) parts.Add("已累计 " + Elapsed);
                if (!string.IsNullOrEmpty(Team)) parts.Add("队伍 " + Team);
                return string.Join(" · ", parts);
            }
        }

        /// <summary>是否处于异常状态（异常后尚未恢复正常进度）。</summary>
        public bool HasProblem =>
            !Recovered && (!string.IsNullOrEmpty(FailedGroup) || TaskEnded
                           || !string.IsNullOrEmpty(LastError));

        /// <summary>异常摘要。</summary>
        public string ProblemText
        {
            get
            {
                if (!HasProblem) return "";
                if (!string.IsNullOrEmpty(FailedGroup))
                    return "⚠ 配置组「" + FailedGroup + "」执行失败";
                if (!string.IsNullOrEmpty(LastError)) return "⚠ " + LastError;
                if (TaskEnded) return "✓ 任务已结束";
                return "";
            }
        }
    }
}
