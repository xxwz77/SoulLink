using System;
using System.Text.RegularExpressions;

namespace SoulLink.Services
{
    internal sealed record BgiActivityEvidence(string Text, DateTime At, string Outcome = "");

    internal readonly record struct BgiLiveActivity(
        BgiActivityEvidence? Action, BgiActivityEvidence? Character,
        BgiActivityEvidence? Skill, BgiActivityEvidence? Food, BgiActivityEvidence? Map)
    {
        internal BgiLiveActivity Filter(DateTime now)
        {
            var character = KnownCharacter(Character, now);
            var map = Map != null && Map.At <= now ? Map : null;
            var action = Fresh(Action, now, 10);
            if (action == null && map != null)
                action = WithActor(map, character?.Text);
            else if (action == null && character?.Outcome == "成功" && Fresh(character, now, 10) != null)
                action = new("切换角色 " + character.Text, character.At, character.Outcome);
            return new(action, character, Fresh(Skill, now, 20), Fresh(Food, now, 20), map);
        }

        internal static BgiActivityEvidence? KnownCharacter(BgiActivityEvidence? evidence, DateTime now)
            => evidence != null && evidence.At <= now ? evidence : null;

        internal static BgiActivityEvidence WithActor(BgiActivityEvidence evidence, string? actor)
            => string.IsNullOrEmpty(actor) ? evidence : evidence with { Text = actor + " · " + evidence.Text };

        private static BgiActivityEvidence? Fresh(BgiActivityEvidence? evidence, DateTime now, int seconds)
            => evidence != null && evidence.At <= now && now - evidence.At <= TimeSpan.FromSeconds(seconds) ? evidence : null;
    }

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
    public sealed partial class BgiStatus
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

        /// <summary>
        /// 最近一次判定为"停止"的时间；MinValue = 从未停止。
        /// ★ 日志出现 <see cref="Stopped"/> 锚点即置位，界面据此显示"已停止"
        ///   而不是继续挂着上一个配置组名。
        /// </summary>
        public DateTime StoppedAt { get; set; } = DateTime.MinValue;

        /// <summary>是否处于停止状态（用于界面判断是否还展示运行中信息）。</summary>
        public bool Stopped => StoppedAt != DateTime.MinValue;

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
        [GeneratedRegex(@"\[第\s*(?<round>\d+\s*/\s*\d+)\s*轮\s*(?<role>[^\]]*)\].*?开始第\s*(?<cur>\d+)\s*/\s*(?<total>\d+)\s*条线路.*?""(?<file>[^""]+)""", RegexOptions.Compiled)]
        private static partial Regex RxRound();

        [GeneratedRegex(@"本线路预计用时\s*(?<v>[0-9\u65f6\u5206\u79d2]+)", RegexOptions.Compiled)]
        private static partial Regex RxEta();
        [GeneratedRegex(@"本轮预计剩余\s*(?<v>[0-9\u65f6\u5206\u79d2]+)", RegexOptions.Compiled)]
        private static partial Regex RxRemain();

        // ② 每日采集：任务「每日采集」，执行路线「03-霜仙花-xx」；路线共 1440 条，CD中 481 条；任务累计执行时长：1时13分03秒
        [GeneratedRegex(@"任务「(?<task>[^」]+)」\s*[,，]\s*执行路线「(?<route>[^」]+)」(?:.*?路线共\s*(?<total>\d+)\s*条)?(?:.*?CD中\s*(?<cd>\d+)\s*条)?(?:.*?任务累计执行时长\s*[:：]\s*(?<elapsed>[0-9\u65f6\u5206\u79d2]+))?", RegexOptions.Compiled)]
        private static partial Regex RxTaskRoute();

        // ③ 纯数字进度：当前进度：纳塔-安饶之野东(进度: 48/94)
        [GeneratedRegex(@"当前进度\s*[:：]\s*(?<area>[^\s(（]+)\s*[(（]\s*进度\s*[:：]\s*(?<cur>\d+)\s*/\s*(?<total>\d+)\s*[)）]", RegexOptions.Compiled)]
        private static partial Regex RxAreaProgress();

        // ③b 极简计数进度：当前进度：9个 / 当前进度：19个（实测 20261027 等文件尾部就是这种）
        [GeneratedRegex(@"当前进度\s*[:：]\s*(?<n>\d+)\s*个\s*$", RegexOptions.Compiled)]
        private static partial Regex RxCountProgress();

        // ③c 地图追踪任务（实测最高频，占启动行 1 万+ 次）：
        //   → 开始执行地图追踪任务: "A06-欧蒂克莱尔左侧-8个.json"
        //   注意：这条**不带**"当前进度：任务「X」，执行路线「Y」"的成对行，
        //   所以原来完全解析不到，界面显示"等待日志中的状态"（用户实测）。
        [GeneratedRegex(@"开始执行地图追踪任务\s*[:：]\s*""(?<route>[^""]+)""|开始执行地图追踪任务\s*[:：]\s*(?<route2>[^\r\n""]+)", RegexOptions.Compiled)]
        private static partial Regex RxMapTrace();

        // ③d 独立 JS 脚本任务：→ 开始执行JS脚本: "xxx.js"
        [GeneratedRegex(@"开始执行JS脚本\s*[:：]\s*""(?<name>[^""]+)""", RegexOptions.Compiled)]
        private static partial Regex RxJsScript();

        // ③e 独立任务：→ 开始执行独立任务: "xxx"
        [GeneratedRegex(@"开始执行独立任务\s*[:：]\s*""(?<name>[^""]+)""", RegexOptions.Compiled)]
        private static partial Regex RxStandalone();

        // ③f 一条龙任务启动：锄地一条龙任务启动 [配置组: "X"]
        [GeneratedRegex(@"一条龙任务启动\s*\[配置组\s*[:：]\s*""(?<g>[^""]+)""\]", RegexOptions.Compiled)]
        private static partial Regex RxOneDragonStart2();

        // ★ 当前动作：BGI 的很多任务只输出动作，不输出"路线/进度"。
        //   例如：开始挖矿 / 动作：下落攻击 / 成功切换角色:"莉奈娅"
        //   原来这类行全部识别不到 → 界面一直显示"等待日志中的状态信息"
        //   （用户实测 A1 明明在挖矿，面板却是空的）。
        [GeneratedRegex(@"^s*(?<act>开始[^，。
]{1,20}|动作[:：]s*(?<a2>[^
]{1,20}?)(?:s*结束)?|成功切换角色s*[:：]s*""?(?<role>[^""
]{1,16}))s*$", RegexOptions.Compiled)]
        private static partial Regex RxCurrentAction();

        // 简单的"动作:"行（单独匹配便于取到动作名）
        [GeneratedRegex(@"动作[:：]\s*(?<name>[^\r\n]{1,24})", RegexOptions.Compiled)]
        private static partial Regex RxActionLine();

        [GeneratedRegex(@"成功切换角色\s*[:：]\s*(?:[""“](?<r>[^""“”\r\n]{1,24})[""”]|(?<r>[\p{L}·]{1,16}))", RegexOptions.Compiled)]
        private static partial Regex RxCharacterSwitch();

        [GeneratedRegex(@"(?:[""“](?<actor>[^""“”\r\n]{1,24})[""”]|(?<actor>[\p{L}·]{1,16}))\s+当前出战\s*$", RegexOptions.Compiled)]
        private static partial Regex RxActiveCharacter();

        [GeneratedRegex(@"开始(?:挖矿|采集|钓鱼|战斗|拾取)", RegexOptions.Compiled)]
        private static partial Regex RxGameplayStart();

        [GeneratedRegex(@"(?:[""“](?<actor>[^""“”\r\n]{1,24})[""”]|(?<actor>[\p{L}·]{1,16}))\s*(?<skill>(?:点按|长按)元素战技)\s*[，,]\s*cd\s*[:：]", RegexOptions.Compiled)]
        private static partial Regex RxSkillInput();

        [GeneratedRegex(@"(?:[""“](?<actor>[^""“”\r\n]{1,24})[""”]\s*)?(?<skill>(?:释放|施放)(?:元素)?(?:战技|爆发))\s*[:：]\s*[""“]?(?<result>成功|失败|未成功|冷却中|超时|未释放|未施放)(?=[""”\s，。,；;]|$)", RegexOptions.Compiled)]
        private static partial Regex RxSkillOutcome();

        [GeneratedRegex(@"(?<!未)(?<!没有)(?:识别到|检测到)战斗结束|自动战斗线程结束|(?:^|\s)战斗(?:已)?结束\s*(?:$|[，,:：])", RegexOptions.Compiled)]
        private static partial Regex RxCombatEnd();

        [GeneratedRegex(@"当前处于联机状态，且(?<guest>在别人世界中|当前账号是房主)，联机人数(?<players>[2-4])人", RegexOptions.Compiled)]
        private static partial Regex RxCoopPartyShape();

        [GeneratedRegex(@"识别到的队伍角色\s*[:：]\s*(?:""(?<names>[^""]*)""|(?<names>[^\r\n]+))", RegexOptions.Compiled)]
        private static partial Regex RxRecognizedRoster();

        [GeneratedRegex(@"当前出战角色未正确识别，通过出战标识推测角色编号为(?<slot>[1-4])", RegexOptions.Compiled)]
        private static partial Regex RxRosterActiveSlot();

        [GeneratedRegex(@"通过内部黑色像素识别出战编号为(?<slot>[1-4])，存在黑色数字的角色编号有\d+个，总角色数量(?<count>[1-4])", RegexOptions.Compiled)]
        private static partial Regex RxObservedActiveSlot();

        [GeneratedRegex(@"切换角色失败，最后一次尝试，当前角色编号\s*[:：]\s*(?<slot>-?[0-4])，期望角色编号", RegexOptions.Compiled)]
        private static partial Regex RxFailedSwitchCurrentSlot();

        // ④ 一条龙配置
        [GeneratedRegex(@"启用一条龙配置\s*[:：]\s*(?<v>.+?)\s*$", RegexOptions.Compiled)]
        private static partial Regex RxOneDragon();

        // ⑤ 配置组（两种写法都见过）
        [GeneratedRegex(@"配置组\s*=\s*""(?<g>[^""]+)""|总文件夹\s*=\s*""(?<g2>[^""]+)""", RegexOptions.Compiled)]
        private static partial Regex RxConfigGroup();

        [GeneratedRegex(@"匹配到战斗脚本\s*[:：]\s*""(?<v>[^""]+)""", RegexOptions.Compiled)]
        private static partial Regex RxFight();
        [GeneratedRegex(@"当前队伍名称\s*[:：]\s*""(?<v>[^""]+)""", RegexOptions.Compiled)]
        private static partial Regex RxTeam();

        // 失败/异常
        [GeneratedRegex(@"配置组\s*""(?<g>[^""]+)""\s*未成功完成", RegexOptions.Compiled)]
        private static partial Regex RxFailGroup();
        [GeneratedRegex(@"→\s*""任务结束""", RegexOptions.Compiled)]
        private static partial Regex RxTaskEnd();
        [GeneratedRegex(@"执行脚本时发生异常\s*[:：]\s*""(?<e>[^""]+)""", RegexOptions.Compiled)]
        private static partial Regex RxError();
        // 一条龙失败：一条龙执行失败 / 一条龙前置动作未成功
        [GeneratedRegex(@"一条龙(?:前置动作未成功|执行失败)", RegexOptions.Compiled)]
        private static partial Regex RxOneDragonFail();
        // 游戏启动准备失败
        [GeneratedRegex(@"游戏启动准备失败[^""]*""(?<e>[^""]+)""", RegexOptions.Compiled)]
        private static partial Regex RxStartFail();

        // 一条龙开始/完成（判断"是否在跑"）
        [GeneratedRegex(@"→\s*开始(?:执行)?一条龙|开始执行一条龙", RegexOptions.Compiled)]
        private static partial Regex RxOneDragonStart();
        [GeneratedRegex(@"一条龙(?:任务)?(?:完成|结束)", RegexOptions.Compiled)]
        private static partial Regex RxOneDragonDone();

        // ── 解析 ───────────────────────────────────────────────────────

        /// <summary>
        /// 在日志增量中更新状态（后出现的覆盖先出现的）。
        /// </summary>
        public void Update(string logChunk)
        {
            if (!string.IsNullOrEmpty(logChunk)) Update(new BgiLogBatch(logChunk));
        }

        internal void Update(BgiLogBatch batch)
        {
            try
            {
                var lines = batch.Lines;
                var groupEvents = batch.GroupEvents;
                int groupEventIndex = 0;
                for (int idx = 0; idx < lines.Length; idx++)
                {
                    string line = lines[idx].Raw;
                    if (line.Length == 0) continue;

                    if (line[0] == '[' && lines[idx].Timestamp is { Success: true } stamp)
                        ObserveActivityHeader(line.AsSpan(stamp.Length));

                    int lineEnd = lines[idx].Start + line.Length;
                    while (groupEventIndex < groupEvents.Count && groupEvents[groupEventIndex].Position < lineEnd)
                    {
                        var groupEvent = groupEvents[groupEventIndex++];
                        if (!BgiLogParser.ValidGroupName(groupEvent.Name)) continue;
                        if (groupEvent.Kind == BgiGroupEventKind.Start)
                        {
                            BeginActivity();
                            _activityGroup = ConfigGroup = groupEvent.Name;
                        }
                        else
                        {
                            ClearActivity(stopped: true);
                            ClearProgress();
                        }
                    }

                    var mapStart = line.Contains("开始执行地图追踪任务", StringComparison.Ordinal)
                        ? RxMapTrace().Match(line) : Match.Empty;
                    string mapRoute = mapStart.Success
                        ? StripJson((mapStart.Groups["route"].Success ? mapStart.Groups["route"] : mapStart.Groups["route2"]).Value.Trim())
                        : "";
                    if (mapRoute.Length > 0) StartMap(mapRoute, batch, lines[idx].Start);
                    if (_liveMap != null && line.Contains("脚本执行结束", StringComparison.Ordinal))
                        FinishMapIfMatching(line);

                    // 把「本行 + 下一行」也作为候选：BGI 头行与正文分属两个物理行
                    string merged = (idx + 1 < lines.Length) ? (line + " " + lines[idx + 1].Raw) : line;

                    bool matched = false;

                    // ★ 当前动作：BGI 大量任务是"只报动作不报进度"的
                    //   （挖矿、下落攻击、切换角色…），之前完全没解析，
                    //   于是界面一直停在"等待日志中的状态信息"（用户实测）。
                    var am = RxActionLine().Match(merged);
                    Match rm = Match.Empty;
                    if (am.Success)
                    {
                        string act = am.Groups["name"].Value.Trim();
                        // "下落攻击 结束" 这种收尾行不算当前动作
                        if (act.EndsWith("结束", StringComparison.Ordinal)) act = "";
                        if (act.Length > 0) CurrentAction = act;
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
                            }
                        }

                        rm = RxCharacterSwitch().Match(merged);
                        if (rm.Success)
                        {
                            CurrentAction = "切换角色 " + rm.Groups["r"].Value;
                        }
                    }

                    // ① 联机锄地（带轮次）
                    var m = RxRound().Match(merged);
                    if (m.Success)
                    {
                        string round = m.Groups["round"].Value.Replace(" ", "");
                        string role = m.Groups["role"].Value.Trim();
                        string progress = m.Groups["cur"].Value + "/" + m.Groups["total"].Value;
                        string route = StripJson(m.Groups["file"].Value);
                        bool newRound = Round != round;
                        if (newRound || (RouteProgress.Length > 0 && RouteProgress != progress))
                            RouteEta = "";
                        if (newRound) RoundRemain = "";
                        if (newRound || role.Length > 0) RoleName = role;
                        Round = round;
                        RouteProgress = progress;
                        SetRoute(route);
                        ReadEstimates(merged);
                        AreaName = "";
                        Recovered = true;   // 有正常进度 = 已恢复
                        matched = true;
                    }

                    // ② 每日采集（任务 + 路线 + CD）
                    if (!matched)
                    {
                        var t = RxTaskRoute().Match(merged);
                        if (t.Success)
                        {
                            string task = t.Groups["task"].Value;
                            if (TaskName != task)
                            {
                                if (TaskName.Length > 0 && TaskName != "地图追踪") BeginActivity();
                                else ClearProgress();
                            }
                            TaskName = task;
                            SetRoute(t.Groups["route"].Value);
                            if (t.Groups["total"].Success) RouteTotal = t.Groups["total"].Value;
                            if (t.Groups["cd"].Success) RouteCd = t.Groups["cd"].Value;
                            if (t.Groups["elapsed"].Success) Elapsed = t.Groups["elapsed"].Value;
                            Round = RoleName = RoundRemain = RouteProgress = "";
                            Recovered = true;
                            matched = true;
                        }
                    }

                    // ③ 纯数字区域进度
                    if (!matched)
                    {
                        var a = RxAreaProgress().Match(merged);
                        if (a.Success)
                        {
                            AreaName = a.Groups["area"].Value;
                            RouteProgress = a.Groups["cur"].Value + "/" + a.Groups["total"].Value;
                            Recovered = true;
                            matched = true;
                        }
                    }

                    // A map task may be part of an existing script or round.
                    if (!matched && mapRoute.Length > 0)
                    {
                        if (RouteFile != mapRoute)
                        {
                            SetRoute(mapRoute);
                            RouteProgress = PickCount = "";
                        }
                        if (TaskName.Length == 0) TaskName = "地图追踪";
                        Recovered = true;
                        matched = true;
                    }

                    // ③d JS 脚本 / 独立任务
                    if (!matched)
                    {
                        var js = RxJsScript().Match(line);
                        if (js.Success)
                        {
                            StartScript("JS脚本", js.Groups["name"].Value);
                            Recovered = true;
                            matched = true;
                        }
                        else
                        {
                            var sd = RxStandalone().Match(line);
                            if (sd.Success)
                            {
                                StartScript("独立任务", sd.Groups["name"].Value);
                                Recovered = true;
                                matched = true;
                            }
                        }
                    }

                    // ③e 一条龙任务启动 [配置组: "X"]
                    if (!matched)
                    {
                        var od = RxOneDragonStart2().Match(line);
                        if (od.Success)
                        {
                            BeginActivity();
                            OneDragonConfig = od.Groups["g"].Value;
                            _activityGroup = OneDragonConfig;
                            Recovered = true;
                            matched = true;
                        }
                    }

                    // ③b 极简计数（只有"当前进度：9个"，没有路线名和总数）
                    if (!matched)
                    {
                        var c = RxCountProgress().Match(line);
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
                        var od = RxOneDragon().Match(line);
                        if (od.Success)
                        {
                            OneDragonConfig = od.Groups["v"].Value.Trim();
                            matched = true;
                        }
                    }

                    // ⑤ 配置组（拾取诊断 / 旧格式）
                    Match? groupTag = null;
                    if (!matched)
                    {
                        var cg = groupTag = RxConfigGroup().Match(line);
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
                        var fs = RxFight().Match(line);
                        if (fs.Success) { FightScript = fs.Groups["v"].Value; matched = true; }
                    }
                    if (!matched)
                    {
                        var tm = RxTeam().Match(line);
                        if (tm.Success) { Team = tm.Groups["v"].Value; matched = true; }
                    }

                    if (groupTag == null && (line.Contains("配置组", StringComparison.Ordinal)
                        || line.Contains("总文件夹", StringComparison.Ordinal)))
                        groupTag = RxConfigGroup().Match(line);
                    if (groupTag is { Success: true })
                    {
                        string name = (groupTag.Groups["g"].Success ? groupTag.Groups["g"] : groupTag.Groups["g2"]).Value.Trim();
                        if (name != "无" && BgiLogParser.ValidGroupName(name) && _activityGroup != name)
                        {
                            if (_activityGroup.Length > 0)
                            {
                                ClearActivity(_activityStopped);
                                ClearProgress();
                            }
                            _activityGroup = name;
                        }
                    }

                    // A stop may be followed by a new one-dragon or standalone task.
                    if (_activityStopped && (RxOneDragonStart().IsMatch(line)
                        || line.Contains("→ \"任务启动！\"", StringComparison.Ordinal)))
                        BeginActivity();

                    if (RouteFile.Length > 0 && line.Contains("预计", StringComparison.Ordinal))
                        ReadEstimates(line);

                    // ⑦ 停止判定：脚本停了就不再显示"当前配置组/任务/路线"。
                    //   用户要求：日志出现停止即判为停止，不再保留运行状态。
                    //   锚点与 BgiLogCenter.RxStopped 保持一致。
                    if (line.IndexOf("停止快捷键", StringComparison.Ordinal) >= 0
                        && line.IndexOf("按下", StringComparison.Ordinal) >= 0
                        || line.IndexOf("停止当前执行任务", StringComparison.Ordinal) >= 0
                        || line.IndexOf("任务已停止", StringComparison.Ordinal) >= 0
                        || line.IndexOf("→ \"任务结束\"", StringComparison.Ordinal) >= 0
                        || line.IndexOf("取消执行配置组", StringComparison.Ordinal) >= 0
                        || (line.IndexOf("配置组 \"", StringComparison.Ordinal) >= 0
                            && (line.IndexOf("执行结束", StringComparison.Ordinal) >= 0
                                || line.IndexOf("未成功完成", StringComparison.Ordinal) >= 0)))
                    {
                        ClearActivity(stopped: true);
                        StoppedAt = DateTime.Now;
                        ClearProgress();
                    }

                    // ⑦' 每日报错计数（按行统计，与状态解析互不影响）
                    var issues = lines[idx].Issues;
                    if ((issues & BgiLogIssues.Error) != 0) ErrorCount++;
                    if ((issues & BgiLogIssues.Warning) != 0) WarnCount++;
                    if ((issues & BgiLogIssues.Failure) != 0) FailCount++;

                    // ⑥ 拾取物品（累计到本路线）。
                    var pk = lines[idx].Pick;
                    string item = "";
                    if (pk.Success)
                    {
                        item = pk.Groups["item"].Value.Trim();
                        // 过滤掉明显的无效值
                        if (item.Length > 0 && item != "无有效结果")
                        {
                            if (RoutePicks.TryGetValue(item, out int cnt))
                                RoutePicks[item] = cnt + 1;
                            else
                                RoutePicks[item] = 1;
                        }
                    }

                    if (!_activityStopped)
                    {
                        ObserveAvatarContext(batch, lines[idx].Start, line);
                        if (line.Contains("战斗", StringComparison.Ordinal) && RxCombatEnd().IsMatch(line))
                            ClearCombatAction();
                        else
                            TrackActivity(batch, lines[idx].Start, line, am, rm, item);
                    }

                    // ── 异常（优先级高，单独判定，不受 matched 影响）──

                    var fg = RxFailGroup().Match(line);
                    if (fg.Success)
                    {
                        FailedGroup = fg.Groups["g"].Value;
                        Recovered = false;
                        continue;
                    }

                    var se = RxError().Match(line);
                    if (se.Success)
                    {
                        LastError = Clean(se.Groups["e"].Value);
                        Recovered = false;
                        continue;
                    }

                    var odf = RxOneDragonFail().Match(line);
                    if (odf.Success)
                    {
                        LastError = Clean(line);
                        Recovered = false;
                        continue;
                    }

                    var sf = RxStartFail().Match(line);
                    if (sf.Success)
                    {
                        LastError = Clean(sf.Groups["e"].Value);
                        Recovered = false;
                        continue;
                    }

                    if (RxTaskEnd().IsMatch(line))
                    {
                        ClearActivity(stopped: true);
                        ClearProgress();
                        TaskEnded = true;
                        continue;
                    }
                    if (RxOneDragonDone().IsMatch(line))
                    {
                        ClearActivity(stopped: true);
                        ClearProgress();
                        TaskEnded = true;
                        continue;
                    }
                }
            }
            catch { }
        }

        private BgiActivityEvidence? _liveAction, _liveCharacter, _liveSkill, _liveFood, _liveMap;
        private string _activityGroup = "", _activityMapRoute = "", _activityScript = "", _combatActionName = "";
        private bool _activityStopped;

        private readonly record struct PartyObservation(DateTime At, string Scope)
        {
            internal bool IsRecent(DateTime at, string scope, int seconds)
                => At != DateTime.MinValue && Scope == scope && at >= At && at - At <= TimeSpan.FromSeconds(seconds);
        }

        private string _activityScope = "", _primaryActivityProcess = "";
        private string[]? _recognizedRoster;
        private PartyObservation _rosterObservation, _partyLayoutObservation, _rosterSlotObservation;
        private int _worldPartyLayout = -1, _nextRosterSlot;
        private bool _primaryAvatarObserved;
        private bool IsChildActivitySource => _activityScope.StartsWith("ChildSession:", StringComparison.Ordinal);

        internal BgiLiveActivity LiveActivity => new(_liveAction, _liveCharacter, _liveSkill, _liveFood, _liveMap);
        internal BgiLiveActivity GetLiveActivity(DateTime now) => LiveActivity.Filter(now);

        internal void InheritMapActivityFrom(BgiStatus previous)
        {
            if (previous._liveMap == null || previous._activityStopped) return;
            _liveMap = previous._liveMap;
            _activityMapRoute = previous._activityMapRoute;
            _activityScript = previous._activityScript;
            _activityGroup = previous._activityGroup;
            _liveCharacter = previous._liveCharacter;
            _primaryActivityProcess = previous._primaryActivityProcess;
            _primaryAvatarObserved = previous._primaryAvatarObserved;
            _worldPartyLayout = previous._worldPartyLayout;
        }

        private void BeginActivity()
        {
            ClearActivity(stopped: false);
            ClearProgress();
            StoppedAt = DateTime.MinValue;
            TaskEnded = false;
        }

        private void ClearProgress()
        {
            TaskName = Round = RoleName = RouteFile = RouteProgress = RouteEta = RoundRemain = "";
            RouteTotal = RouteCd = Elapsed = AreaName = PickCount = CurrentAction = "";
            RoundRunning = false;
        }

        private void SetRoute(string route)
        {
            if (RouteFile != route) RouteEta = "";
            RouteFile = route;
        }

        private void ReadEstimates(string text)
        {
            string eta = Maybe(RxEta().Match(text), "v");
            if (eta.Length > 0) RouteEta = eta;
            string remain = Maybe(RxRemain().Match(text), "v");
            if (remain.Length > 0) RoundRemain = remain;
        }

        private void StartScript(string type, string name)
        {
            if (_activityStopped || TaskName != type || RouteFile != name) BeginActivity();
            TaskName = type;
            RouteFile = _activityScript = name;
        }

        private void StartMap(string route, BgiLogBatch batch, int lineStart)
        {
            if (_activityStopped) BeginActivity();
            ClearCombatAction();
            _activityMapRoute = route;
            DateTime at = batch.TimestampAt(lineStart);
            _liveMap = at != DateTime.MinValue ? new("正在执行地图追踪", at) : null;
        }

        private void FinishMapIfMatching(string line)
        {
            for (var end = BgiLogParser.ScriptEndPlain.Match(line); end.Success; end = end.NextMatch())
            {
                var name = end.Groups["name"].Value.Trim();
                if (!string.Equals(StripJson(name), _activityMapRoute, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, _activityScript, StringComparison.OrdinalIgnoreCase)) continue;
                ClearCombatAction();
                _activityMapRoute = "";
                _liveMap = null;
                break;
            }
        }

        private void ClearCombatAction()
        {
            _liveAction = null;
            _combatActionName = "";
            if (_liveCharacter?.Outcome == "成功")
                _liveCharacter = _liveCharacter with { Outcome = "" };
        }

        private void ClearActivity(bool stopped)
        {
            if (stopped) InvalidateAvatarContext();
            else ClearCombatAction();
            _liveSkill = _liveFood = _liveMap = null;
            _activityMapRoute = _activityScript = "";
            _activityStopped = stopped;
        }

        private void ObserveActivityHeader(ReadOnlySpan<char> suffix)
        {
            suffix = suffix.TrimStart();
            if (!suffix.StartsWith("[Primary:", StringComparison.Ordinal)
                && !suffix.StartsWith("[ChildSession:", StringComparison.Ordinal))
            {
                _activityScope = "";
                return;
            }

            int close = suffix[..Math.Min(suffix.Length, 80)].IndexOf(']');
            if (close < 0)
            {
                _activityScope = "";
                return;
            }
            var scope = suffix[1..close];
            if (_activityScope.AsSpan().SequenceEqual(scope)) return;
            _activityScope = scope.ToString();

            if (!scope.StartsWith("Primary:", StringComparison.Ordinal)) return;
            int thread = scope.IndexOf(":T", StringComparison.Ordinal);
            var process = thread < 0 ? scope : scope[..thread];
            if (_primaryActivityProcess.AsSpan().SequenceEqual(process)) return;
            if (_primaryActivityProcess.Length > 0)
            {
                InvalidateAvatarContext();
                _liveSkill = _liveFood = _liveMap = null;
                _activityMapRoute = _activityScript = "";
            }
            _primaryActivityProcess = process.ToString();
        }

        private void ObserveAvatarContext(BgiLogBatch batch, int lineStart, string line)
        {
            // Child tasks can interleave with the primary log. Their roster, slot and world
            // observations cannot overwrite the primary session's character or formation.
            if (IsChildActivitySource && (_primaryActivityProcess.Length > 0 || _primaryAvatarObserved)) return;

            if (line.Contains("切换队伍成功", StringComparison.Ordinal)
                || line.Contains("不在对方世界，开始尝试加入", StringComparison.Ordinal)
                || ((line.Contains("成功加入序号", StringComparison.Ordinal)
                    || line.Contains("成功加入对方世界", StringComparison.Ordinal))
                    && !line.Contains("通知", StringComparison.Ordinal)))
            {
                InvalidateAvatarContext();
                return;
            }

            if (line.Contains("传送完成，返回主界面", StringComparison.Ordinal))
            {
                // Same-world teleport may preserve the active character. Combat activity does not survive loading.
                ClearCombatAction();
                _rosterSlotObservation = default;
                return;
            }

            if (line.Contains("当前处于联机状态", StringComparison.Ordinal))
            {
                var shape = RxCoopPartyShape().Match(line);
                int layout = shape.Success
                    ? shape.Groups["players"].ValueSpan[0] - '0' + (shape.Groups["guest"].ValueSpan.StartsWith("在别人") ? 0 : 10)
                    : line.Contains("但是没有其他玩家连入", StringComparison.Ordinal) ? 11 : -1;
                if (layout >= 0)
                {
                    if (layout != _worldPartyLayout && (_worldPartyLayout >= 0 || _liveCharacter != null)) InvalidateAvatarContext();
                    _worldPartyLayout = layout;
                    _partyLayoutObservation = new(batch.TimestampAt(lineStart), _activityScope);
                }
                return;
            }

            if (line.Contains("识别到的队伍角色", StringComparison.Ordinal))
            {
                ObserveRoster(batch.TimestampAt(lineStart), RxRecognizedRoster().Match(line));
                return;
            }

            if (line.Contains("通过出战标识推测角色编号", StringComparison.Ordinal)
                || line.Contains("当前编队中可能只存在一个角色（且角色编号未正确识别）", StringComparison.Ordinal))
            {
                var slot = RxRosterActiveSlot().Match(line);
                bool only = line.Contains("当前编队中可能只存在一个角色（且角色编号未正确识别）", StringComparison.Ordinal);
                if (slot.Success || only)
                {
                    _nextRosterSlot = only ? 1 : slot.Groups["slot"].ValueSpan[0] - '0';
                    _rosterSlotObservation = new(batch.TimestampAt(lineStart), _activityScope);
                }
                return;
            }

            Match active = Match.Empty;
            if (line.Contains("识别出战编号为", StringComparison.Ordinal))
                active = RxObservedActiveSlot().Match(line);
            else if (line.Contains("切换角色失败，最后一次尝试", StringComparison.Ordinal))
                active = RxFailedSwitchCurrentSlot().Match(line);
            if (!active.Success) return;

            var at = batch.TimestampAt(lineStart);
            var roster = _recognizedRoster;
            int slotNumber = int.Parse(active.Groups["slot"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
            if (slotNumber < 1)
            {
                MarkUnknownCharacter();
                return;
            }
            if (roster == null || !_rosterObservation.IsRecent(at, _activityScope, 120)) return;
            var count = active.Groups["count"];
            if (count.Success && count.ValueSpan[0] - '0' != roster.Length) return;
            SetActiveCharacterFromSlot(roster, slotNumber, at);
        }

        private void ObserveRoster(DateTime at, Match match)
        {
            if (at == DateTime.MinValue || !match.Success) return;
            var roster = match.Groups["names"].Value.Split([',', '，'], StringSplitOptions.TrimEntries);
            if (roster.Length is < 1 or > 4) return;
            for (int i = 0; i < roster.Length; i++)
            {
                string name = roster[i];
                int costume = name.IndexOfAny(['(', '（']);
                if (costume > 0) name = name[..costume].TrimEnd();
                roster[i] = IsKnownActorName(name) ? name : "";
            }
            // The recognized order maps slots to names. A complete new roster can disprove an old actor.
            if (_liveCharacter != null && at >= _liveCharacter.At
                && Array.IndexOf(roster, _liveCharacter.Text) < 0 && Array.IndexOf(roster, "") < 0)
                ClearActiveCharacter();

            _recognizedRoster = roster;
            _rosterObservation = new(at, _activityScope);
            if (_rosterSlotObservation.IsRecent(at, _activityScope, 10))
                SetActiveCharacterFromSlot(roster, _nextRosterSlot, at);
            // BetterGI MultiGameStatus limits guests in a 3/4-person world and hosts in a
            // 4-person world to one playable avatar.
            else if (roster.Length == 1 && _worldPartyLayout is 3 or 4 or 14
                && _partyLayoutObservation.IsRecent(at, _activityScope, 10))
                SetActiveCharacterFromSlot(roster, 1, at);
            _rosterSlotObservation = default;
        }

        private static bool IsKnownActorName(string name)
        {
            if (name.Length is < 1 or > 24 || name is "未知角色" or "未知" or "识别失败") return false;
            foreach (char ch in name)
                if (!char.IsLetter(ch) && ch is not ('·' or ' ' or '-' or '\'')) return false;
            return true;
        }

        private void SetActiveCharacterFromSlot(string[] roster, int slot, DateTime at)
        {
            if (slot <= roster.Length && roster[slot - 1].Length > 0)
                SetActiveCharacter(roster[slot - 1], at, switched: false);
            else
                MarkUnknownCharacter();
        }

        private void SetActiveCharacter(string name, DateTime at, bool switched)
        {
            if (!IsKnownActorName(name) || (IsChildActivitySource && _primaryAvatarObserved)) return;
            if (switched || _liveCharacter?.Text != name) ClearCombatAction();
            _liveCharacter = new(name, at, switched ? "成功" : "");
            if (!IsChildActivitySource) _primaryAvatarObserved = true;
        }

        private void MarkUnknownCharacter()
        {
            ClearActiveCharacter();
            if (!IsChildActivitySource) _primaryAvatarObserved = true;
        }

        private void ClearActiveCharacter()
        {
            ClearCombatAction();
            _liveCharacter = null;
        }

        private void InvalidateAvatarContext()
        {
            ClearActiveCharacter();
            _recognizedRoster = null;
            _rosterObservation = _partyLayoutObservation = _rosterSlotObservation = default;
            _worldPartyLayout = -1;
            _primaryAvatarObserved = false;
        }

        private void TrackActivity(BgiLogBatch batch, int lineStart, string line, Match action, Match switchCharacter, string item)
        {
            var actionText = LineCapture(action, "name", line);
            if (actionText.IsEmpty && line.Contains("开始", StringComparison.Ordinal))
                actionText = RxGameplayStart().Match(line).ValueSpan;

            // The legacy status considers a merged line; evidence belongs to the physical line carrying the signal.
            if (switchCharacter.Index >= line.Length || (action.Success && line.Contains("成功切换角色", StringComparison.Ordinal)))
                switchCharacter = RxCharacterSwitch().Match(line);
            var character = LineCapture(switchCharacter, "r", line);
            bool switched = !character.IsEmpty;
            if (!switched && line.Contains("当前出战", StringComparison.Ordinal))
                character = RxActiveCharacter().Match(line).Groups["actor"].ValueSpan;

            Match cast = Match.Empty;
            if (line.Contains("战技", StringComparison.Ordinal) || line.Contains("爆发", StringComparison.Ordinal))
            {
                cast = RxSkillOutcome().Match(line);
                if (!cast.Success) cast = RxSkillInput().Match(line);
            }

            bool usedFood = item == "使用回血料理";
            if (actionText.IsEmpty && character.IsEmpty && !cast.Success && !usedFood) return;

            if (actionText.EndsWith("结束", StringComparison.Ordinal))
            {
                if (actionText[..^2].TrimEnd().SequenceEqual(_combatActionName))
                    ClearCombatAction();
                actionText = default;
            }

            DateTime at = batch.TimestampAt(lineStart);
            if (at == DateTime.MinValue) return;

            if (!character.IsEmpty)
                SetActiveCharacter(character.ToString(), at, switched);

            string? activeActor = IsChildActivitySource && _primaryAvatarObserved
                ? null : BgiLiveActivity.KnownCharacter(_liveCharacter, at)?.Text;
            if (!actionText.IsEmpty)
            {
                _combatActionName = actionText.ToString();
                _liveAction = BgiLiveActivity.WithActor(new(_combatActionName, at), activeActor);
            }
            if (cast.Success)
            {
                string actor = cast.Groups["actor"].Value;
                string skill = cast.Groups["skill"].Value;
                string outcome = cast.Groups["result"].Success ? cast.Groups["result"].Value : "尝试";
                _liveSkill = new(actor.Length == 0 ? skill : actor + " " + skill, at, outcome);
                _combatActionName = skill;
                _liveAction = BgiLiveActivity.WithActor(new(skill, at, outcome), actor.Length > 0 ? actor : activeActor);
            }
            if (usedFood)
                _liveFood = new(item, at);
        }

        private static ReadOnlySpan<char> LineCapture(Match match, string name, string line)
        {
            if (!match.Success || match.Index >= line.Length) return default;
            var capture = match.Groups[name];
            if (!capture.Success || capture.Index >= line.Length) return default;
            return line.AsSpan(capture.Index, Math.Min(capture.Length, line.Length - capture.Index)).Trim();
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

            // ★ 如果对方处于"已停止"状态，只能继承"停在哪"这些静态信息，
            //   绝不能把轮次 / 角色 / 路线 / 进度一并搬过来——
            //   那正是"脚本明明停了，总览还挂着第 2/2 轮 · 阿宴"的根因。
            if (other.Stopped)
            {
                StoppedAt = other.StoppedAt;
                RoundRunning = false;
                if (string.IsNullOrEmpty(ConfigGroup) && !string.IsNullOrEmpty(other.ConfigGroup))
                    ConfigGroup = other.ConfigGroup;
                if (string.IsNullOrEmpty(OneDragonConfig) && !string.IsNullOrEmpty(other.OneDragonConfig))
                    OneDragonConfig = other.OneDragonConfig;
                // Round / RoleName / TaskName / RouteFile / AreaName / CurrentAction 一律不继承。
                return;
            }

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
