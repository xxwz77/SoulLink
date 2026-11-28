using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SoulLink.Models
{
    /// <summary>一个扫描周期内某节点的三个状态：自身值、入线电流、出线电流。</summary>
    public sealed class PlcNodeState
    {
        public bool Value;
        public bool Input;
        public bool Output;
        /// <summary>false=数据源未知/错误，不能因 NOT 被视为正常真。</summary>
        public bool Available = true;
    }

    /// <summary>
    /// 无 UI、无 IO 的 PLC 布尔求值器。
    /// 触点：LD=raw，AND=input AND raw，OR=input OR raw；NOT 只反 raw。
    /// OUT/END 的多条入线是独立梯级的 OR 汇入；动作没有上游时绝不自启动。
    /// </summary>
    public static class PlcLogic
    {
        public static Dictionary<string, PlcNodeState> Scan(AutomationFlow flow, Func<FlowNode, bool> read)
        {
            flow.EnsurePlcModel();
            var result = flow.Nodes.ToDictionary(n => n.Id, _ => new PlcNodeState(), StringComparer.OrdinalIgnoreCase);
            foreach (var node in Topological(flow))
            {
                var raw = node.IsContact ? (read(node) != node.Negated) : true;
                result[node.Id] = Evaluate(node, flow.SourcesOf(node.Id).Select(id => result[id]).ToList(), raw);
            }
            return result;
        }

        public static PlcNodeState Evaluate(FlowNode node, IReadOnlyList<PlcNodeState> sources, bool raw)
        {
            var state = new PlcNodeState { Value = raw };
            if (node.IsContact && sources.Count == 0) { state.Input = true; state.Output = raw; return state; }
            bool any = sources.Any(s => s.Output);
            bool all = sources.Count > 0 && sources.All(s => s.Output);
            state.Input = node.IsContact && node.Logic == FlowLogicOp.AND ? all : any;
            state.Output = node.IsContact
                ? node.Logic == FlowLogicOp.OR ? state.Input || raw : state.Input && raw
                : state.Input;
            return state;
        }

        public static List<FlowNode> Topological(AutomationFlow flow)
        {
            var indegree = flow.Nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
            foreach (var e in flow.Edges) if (indegree.ContainsKey(e.To) && indegree.ContainsKey(e.From)) indegree[e.To]++;
            var ready = flow.Nodes.Where(n => indegree[n.Id] == 0).ToList();
            var ordered = new List<FlowNode>();
            while (ready.Count > 0)
            {
                var n = ready[0]; ready.RemoveAt(0); ordered.Add(n);
                foreach (var id in flow.TargetsOf(n.Id))
                    if (indegree.ContainsKey(id) && --indegree[id] == 0) ready.Add(flow.FindNode(id)!);
            }
            // 非法环也返回全部节点，结果保持断开；校验器会阻止运行。
            foreach (var n in flow.Nodes) if (!ordered.Contains(n)) ordered.Add(n);
            return ordered;
        }

        public static int NormalizeBoundaryHour(int hour) => hour == 4 ? 4 : 0;
        public static DateTime BusinessDay(DateTime now, int boundaryHour = 0)
            => NormalizeBoundaryHour(boundaryHour) == 4 && now.Hour < 4 ? now.Date.AddDays(-1) : now.Date;
        public static string DayKey(DateTime now, int boundaryHour = 0)
            => BusinessDay(now, boundaryHour).ToString("yyyy-MM-dd");
        /// <summary>END 节点独立持有日界；没有 END 的流程是循环流程。</summary>
        public static bool HasEnd(AutomationFlow flow)
            => flow.Nodes != null && flow.Nodes.Any(n => n.Kind == FlowNodeKind.Terminate);
        public static int EndBoundaryHour(AutomationFlow flow)
            => flow.Nodes.FirstOrDefault(n => n.Kind == FlowNodeKind.Terminate)?.DayBoundaryHour ?? 0;
        public static bool IsDailyComplete(AutomationFlow flow, DateTime now)
            => HasEnd(flow) && flow.DailyLatchKey == DayKey(now, EndBoundaryHour(flow));
        public static void CompleteToday(AutomationFlow flow, DateTime now)
            => flow.DailyLatchKey = DayKey(now, EndBoundaryHour(flow));
        /// <summary>有 END 才在当日停止扫描；无 END 完成一轮后立即重新获取条件。</summary>
        public static bool IsRunComplete(AutomationFlow flow, DateTime now)
            => IsDailyComplete(flow, now);
        /// <summary>清除 END 今日标记：到日界时自动清除，用户也可以手动清除后当天重跑。</summary>
        public static void ClearDailyLatch(AutomationFlow flow)
            => flow.DailyLatchKey = "";
        /// <summary>把 END 今日标记直接写成当前业务日（今日不运行）。</summary>
        public static void LatchToday(AutomationFlow flow, DateTime now)
            => CompleteToday(flow, now);
        /// <summary>手动切换今日运行状态：true=今日可运行，false=今日不运行。</summary>
        public static void SetTodayRun(AutomationFlow flow, DateTime now, bool enabled)
        {
            if (!HasEnd(flow)) return;
            if (enabled) { ClearDailyLatch(flow); flow.RunComplete = false; }
            else { LatchToday(flow, now); flow.RunComplete = true; }
        }
    }

    /// <summary>本地日历定时。每个基准日期只记录一次，补跑限制在实际到期日期内。</summary>
    public static class PlcSchedule
    {
        private readonly record struct Period(string Kind, TimeSpan Clock, int WeekDays, int Month,
            int DayOfMonth, DateTime StartDate, int IntervalDays, int StepSeconds, int ResetAfter,
            long CatchUpSeconds, bool PreciseCatchUp);

        public readonly record struct Occurrence(DateTime BaseDay, DateTime At, int StepIndex);

        public static string Recurrence(FlowNode node) => node.Get("recurrence", "daily").Trim();

        public static string ClockText(FlowNode node) => node.Get("time",
            $"{node.GetInt("hour", 4):00}:{node.GetInt("minute", 0):00}:{node.GetInt("second", 0):00}");

        public static bool TryReadWeekDays(string text, out int days)
        {
            days = 0;
            foreach (string part in text.Split(',', StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, out int day) || day is < 0 or > 6) return false;
                days |= 1 << day;
            }
            return days != 0;
        }

        public static (string Field, string Message)? Validate(FlowNode node) => Read(node, out _);

        private static (string Field, string Message)? Read(FlowNode node, out Period period)
        {
            period = default;
            string kind = Recurrence(node);
            if (kind is not ("daily" or "weekly" or "monthly" or "yearly" or "intervalDays" or "once"))
                return ("recurrence", "请选择有效的重复周期");
            if (!TryReadClock(ClockText(node), out var clock))
                return ("time", "时刻格式为 HH:mm:ss，也可省略秒");

            int weekDays = 0, month = 1, dayOfMonth = 1, interval = 1;
            var startDate = DateTime.MinValue;
            if (kind == "weekly" && !TryReadWeekDays(node.Get("weekDays"), out weekDays))
                return ("weekDays", "请至少选择一个星期");
            if (kind == "yearly" && !IntField(node, "month", 1, 1, 12, out month))
                return ("month", "月份必须是 1 到 12 的整数");
            if (kind is "monthly" or "yearly")
            {
                int maxDay = kind == "yearly" ? DateTime.DaysInMonth(2000, month) : 31;
                if (!IntField(node, "dayOfMonth", 1, 1, maxDay, out dayOfMonth))
                    return ("dayOfMonth", $"日期必须是 1 到 {maxDay} 的整数");
            }
            if ((kind is "intervalDays" or "once") && !DateTime.TryParseExact(node.Get("startDate").Trim(),
                    "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out startDate))
                return ("startDate", "日期格式为 yyyy-MM-dd");
            if (kind == "intervalDays" && !IntField(node, "intervalDays", 1, 1, int.MaxValue, out interval))
                return ("intervalDays", "间隔天数必须是正整数");

            int step = 0, reset = 0;
            if (kind != "once")
            {
                if (!IntField(node, "stepSeconds", 0, 0, int.MaxValue, out step, optional: true))
                    return ("stepSeconds", "延时秒数必须是非负整数");
                if (!IntField(node, "resetAfter", 0, 0, 999, out reset, optional: true))
                    return ("resetAfter", "复位次数必须是 0 到 999 的整数");
            }
            long catchUpSeconds;
            bool preciseCatchUp = !string.IsNullOrWhiteSpace(node.Get("catchUpSeconds"));
            if (preciseCatchUp)
            {
                if (!IntField(node, "catchUpSeconds", 0, 0, int.MaxValue, out int seconds))
                    return ("catchUpSeconds", "补跑秒数必须是非负整数");
                catchUpSeconds = seconds;
            }
            else
            {
                if (!IntField(node, "catchUpHours", 12, 0, int.MaxValue, out int catchUp, optional: true))
                    return ("catchUpHours", "补跑小时数必须是非负整数");
                catchUpSeconds = (long)catchUp * 3600;
            }
            period = new(kind, clock, weekDays, month, dayOfMonth, startDate, interval, step, reset, catchUpSeconds, preciseCatchUp);
            return null;
        }

        private static bool IntField(FlowNode node, string key, int fallback, int min, int max, out int value,
            bool optional = false)
        {
            string text = node.Get(key, fallback.ToString(CultureInfo.InvariantCulture));
            value = fallback;
            return (optional && string.IsNullOrWhiteSpace(text))
                || (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max);
        }

        private static bool TryReadClock(string text, out TimeSpan clock)
        {
            clock = default;
            var parts = text.Trim().Split(':');
            int seconds = 0;
            if (parts.Length is < 2 or > 3 || !int.TryParse(parts[0], out int hours) || hours is < 0 or > 23
                || !int.TryParse(parts[1], out int minutes) || minutes is < 0 or > 59
                || (parts.Length == 3 && (!int.TryParse(parts[2], out seconds) || seconds is < 0 or > 59))) return false;
            clock = new(hours, minutes, seconds);
            return true;
        }

        internal static PlcSignal Sample(AutomationFlow flow, FlowNode node, DateTime now)
        {
            if (Read(node, out var period) != null) return new(false, false);
            var occurrence = Find(flow, node, period, now, includeFuture: false);
            return new(occurrence.HasValue) { ScheduleDay = occurrence?.BaseDay };
        }

        public static Occurrence? Next(AutomationFlow flow, FlowNode node, DateTime now)
            => Read(node, out var period) == null ? Find(flow, node, period, now, includeFuture: true) : null;

        internal static void MarkTriggered(AutomationFlow flow, FlowNode node, DateTime baseDay)
        {
            flow.ScheduleLastTriggeredDay[node.Id] = baseDay;
            flow.ScheduleCycleDay.Remove(node.Id);
            if (node.GetInt("stepSeconds", 0) <= 0 || Recurrence(node) == "once") return;
            int count = Math.Max(0, flow.ScheduleFireCount.GetValueOrDefault(node.Id));
            int reset = node.GetInt("resetAfter", 0);
            if (reset > 0) count %= reset;
            if (count < int.MaxValue) flow.ScheduleFireCount[node.Id] = count + 1;
        }

        private static Occurrence? Find(AutomationFlow flow, FlowNode node, Period period, DateTime now, bool includeFuture)
        {
            int stepIndex = Math.Max(0, flow.ScheduleFireCount.GetValueOrDefault(node.Id));
            if (period.ResetAfter > 0) stepIndex %= period.ResetAfter;
            long offsetSeconds = (long)stepIndex * period.StepSeconds + (long)period.Clock.TotalSeconds;
            if (offsetSeconds > DateTime.MaxValue.Ticks / TimeSpan.TicksPerSecond) return null;
            long offset = offsetSeconds * TimeSpan.TicksPerSecond;
            long wholeDays = offset / TimeSpan.TicksPerDay * TimeSpan.TicksPerDay;
            var minimum = includeFuture && flow.CreatedAt is { } created && created.Date > now.Date ? created.Date : now.Date;
            var first = new DateTime(Math.Max(0, minimum.Ticks - wholeDays), now.Kind);
            var lastTriggered = LastTriggeredDay(flow, node, period, now);
            // Migrated plans have a short seconds window. A scan just after midnight can still see a 23:59:59 slot.
            if (period.PreciseCatchUp && now.TimeOfDay.TotalSeconds < period.CatchUpSeconds && first > DateTime.MinValue)
            {
                var previous = first.AddDays(-1);
                if ((lastTriggered == null || previous > lastTriggered.Value) && previous.Ticks <= DateTime.MaxValue.Ticks - offset
                    && Matches(period, previous))
                {
                    var target = previous.AddTicks(offset);
                    if (target <= now && (now - target).TotalSeconds <= Math.Min(period.CatchUpSeconds, 86_400)
                        && (flow.CreatedAt is not { } registered || target >= registered)) return new(previous, target, stepIndex);
                }
            }
            if (lastTriggered is { } last && first <= last)
            {
                if (!includeFuture || last.Date == DateTime.MaxValue.Date) return null;
                first = last.Date.AddDays(1);
            }
            DateTime? baseDay = includeFuture ? NextBaseDay(period, first) : Matches(period, first) ? first : null;
            while (baseDay is { } day && day.Ticks <= DateTime.MaxValue.Ticks - offset)
            {
                var at = day.AddTicks(offset);
                if ((flow.CreatedAt is not { } registered || at >= registered)
                    && (at >= now || (at.Date == now.Date && (period.CatchUpSeconds == 0 || (now - at).TotalSeconds <= period.CatchUpSeconds))))
                    return includeFuture || at <= now ? new(day, at, stepIndex) : null;
                if (!includeFuture || day.Date == DateTime.MaxValue.Date) return null;
                baseDay = NextBaseDay(period, day.AddDays(1));
            }
            return null;
        }

        private static DateTime? LastTriggeredDay(AutomationFlow flow, FlowNode node, Period period, DateTime now)
        {
            if (flow.ScheduleLastTriggeredDay.TryGetValue(node.Id, out var last)) return last.Date;
            if (!flow.ScheduleCycleDay.TryGetValue(node.Id, out string? oldDay)
                || !DateTime.TryParseExact(oldDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out last)) return null;
            // Older builds only recorded a business day. Before 04:00 their calendar-day slot was already today's.
            if (flow.DayBoundaryHour == 4 && now.Date > DateTime.MinValue && now.Hour < 4
                && period.Clock.Hours < 4 && last == PlcLogic.BusinessDay(now, 4))
                return now.Date;
            return last;
        }

        private static bool Matches(Period period, DateTime day) => period.Kind switch
        {
            "weekly" => (period.WeekDays & (1 << (int)day.DayOfWeek)) != 0,
            "monthly" => day.Day == period.DayOfMonth,
            "yearly" => day.Month == period.Month && day.Day == period.DayOfMonth,
            "intervalDays" => day >= period.StartDate && (day - period.StartDate).Days % period.IntervalDays == 0,
            "once" => day == period.StartDate,
            _ => true
        };

        private static DateTime? NextBaseDay(Period period, DateTime first)
        {
            if (period.Kind == "once") return period.StartDate >= first ? period.StartDate : null;
            if (period.Kind == "intervalDays")
            {
                if (period.StartDate >= first) return period.StartDate;
                int remainder = (first - period.StartDate).Days % period.IntervalDays;
                int days = remainder == 0 ? 0 : period.IntervalDays - remainder;
                return days <= (DateTime.MaxValue.Date - first).Days ? first.AddDays(days) : null;
            }
            // A valid yearly date recurs at least once in eight years, including Feb 29 across a century.
            for (var day = first; ; day = day.AddDays(1))
            {
                if (Matches(period, day)) return day;
                if (day == DateTime.MaxValue.Date) return null;
            }
        }
    }
}
