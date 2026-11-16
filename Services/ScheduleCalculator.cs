using System;
using System.Collections.Generic;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 定时计划的时间计算。
    ///
    /// ★ 核心语义（用户明确要求）：
    ///   "每次执行后延后 N 秒" 是**在基准时刻上递增**，不是固定不变：
    ///     基准 4:00:00，延后 60 秒
    ///       第 1 次 → 4:00:00
    ///       第 2 次 → 4:01:00
    ///       第 3 次 → 4:02:00
    ///     延后 86400 秒（1 天）时：
    ///       第 2 次 → 4:00 + 24h = 第 3 天 4:00
    ///
    ///   公式：第 n 次时刻 = 基准时刻 + (n-1) × PostRunDelaySeconds
    ///   其中 RunCount 记录已执行次数。
    ///
    /// ★ 所有计算精确到**秒**。
    /// </summary>
    public static class ScheduleCalculator
    {
        /// <summary>
        /// 计算"从现在起"下一个应执行的本地时间。
        /// 返回 null 表示不会再执行（例如一次性任务已经过期）。
        /// </summary>
        public static DateTime? NextRun(ScheduleSpec spec, DateTime? now = null)
        {
            // 暂停中：不产生任何执行（界面会显示"已暂停"）
            if (spec == null || !spec.Enabled || spec.Paused) return null;
            var n = now ?? DateTime.Now;

            try
            {
                switch (spec.Kind)
                {
                    case ScheduleKind.Once:
                        return spec.OnceAt > n ? spec.OnceAt : (DateTime?)null;

                    case ScheduleKind.Interval:
                    {
                        int iv = Math.Max(1, spec.IntervalSeconds);
                        var baseAt = spec.LastRunAt ?? n;
                        var next = baseAt.AddSeconds(iv + Math.Max(0, spec.PostRunDelaySeconds));
                        // 上次执行很久以前 → 从现在算，不补跑
                        return next <= n ? n.AddSeconds(iv) : next;
                    }

                    case ScheduleKind.Daily:
                        return NextByStep(n, BaseTimeFor(spec, n), TimeSpan.FromDays(1), spec);

                    case ScheduleKind.Weekly:
                    {
                        if (spec.WeekDays.Count == 0) return null;
                        // 在接下来 14 天里找最近的匹配日
                        var t = new TimeSpan(spec.Hour, spec.Minute, spec.Second);
                        for (int d = 0; d <= 14; d++)
                        {
                            var day = n.Date.AddDays(d);
                            int dow = (int)day.DayOfWeek;   // 0=周日
                            if (!spec.WeekDays.Contains(dow)) continue;

                            var candidate = day + t;
                            // 加上"递增偏移"
                            candidate = ApplyStepOffset(candidate, spec, day);
                            if (candidate > n) return candidate;
                        }
                        return null;
                    }

                    case ScheduleKind.Monthly:
                        return NextMonthly(n, spec);

                    case ScheduleKind.Yearly:
                        return NextYearly(n, spec);

                    default:
                        return null;
                }
            }
            catch { return null; }
        }

        /// <summary>相对 n 的基准日（用于 Daily，把"今天/明天 该时刻"算出来）。</summary>
        private static DateTime BaseTimeFor(ScheduleSpec spec, DateTime n)
            => n.Date.AddHours(spec.Hour).AddMinutes(spec.Minute).AddSeconds(spec.Second);

        /// <summary>按"步长为 1 天"推进，同时叠加 PostRunDelay 的递增偏移。</summary>
        private static DateTime NextByStep(DateTime n, DateTime baseTime, TimeSpan step, ScheduleSpec spec)
        {
            var candidate = baseTime;
            if (candidate <= n) candidate = candidate.Add(step);

            // 递增偏移：第 n 次 = 基准 + (RunCount) × 延时
            candidate = ApplyStepOffset(candidate, spec, candidate.Date);
            if (candidate > n) return candidate;

            // 偏移后可能又落到过去，继续推进
            int guard = 0;
            while (candidate <= n && guard++ < 400)
            {
                candidate = candidate.Add(step);
                candidate = ApplyStepOffset(candidate, spec, candidate.Date);
            }
            return candidate;
        }

        /// <summary>
        /// 把"递增偏移"叠到候选时刻上。
        /// 偏移量 = RunCount × PostRunDelaySeconds（RunCount 是已执行次数）。
        /// </summary>
        private static DateTime ApplyStepOffset(DateTime candidate, ScheduleSpec spec, DateTime asOfDate)
        {
            int delay = Math.Max(0, spec.PostRunDelaySeconds);
            if (delay == 0) return candidate;

            int runs = Math.Max(0, spec.RunCount);
            return candidate.AddSeconds((long)runs * delay);
        }

        private static DateTime? NextMonthly(DateTime n, ScheduleSpec spec)
        {
            int dom = Math.Clamp(spec.DayOfMonth, 1, 31);
            for (int m = 0; m <= 24; m++)
            {
                var monthStart = new DateTime(n.Year, n.Month, 1).AddMonths(m);
                int dim = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
                if (dom > dim) continue;                     // 该月没有这一天，跳过

                var candidate = monthStart.AddDays(dom - 1)
                    .AddHours(spec.Hour).AddMinutes(spec.Minute).AddSeconds(spec.Second);
                candidate = ApplyStepOffset(candidate, spec, candidate.Date);
                if (candidate > n) return candidate;
            }
            return null;
        }

        private static DateTime? NextYearly(DateTime n, ScheduleSpec spec)
        {
            int mon = Math.Clamp(spec.Month, 1, 12);
            int dom = Math.Clamp(spec.DayOfMonth, 1, 31);
            for (int y = 0; y <= 8; y++)
            {
                int year = n.Year + y;
                int dim = DateTime.DaysInMonth(year, mon);
                if (dom > dim) continue;

                var candidate = new DateTime(year, mon, dom)
                    .AddHours(spec.Hour).AddMinutes(spec.Minute).AddSeconds(spec.Second);
                candidate = ApplyStepOffset(candidate, spec, candidate.Date);
                if (candidate > n) return candidate;
            }
            return null;
        }

        /// <summary>
        /// 执行完成后调用：累计次数、记录时间，并给出下次时刻。
        /// </summary>
        public static DateTime? MarkRunCompleted(ScheduleSpec spec, DateTime? at = null)
        {
            if (spec == null) return null;
            var n = at ?? DateTime.Now;
            spec.RunCount = Math.Max(0, spec.RunCount) + 1;
            spec.LastRunAt = n;
            return NextRun(spec, n);
        }

        /// <summary>重置进度（用户改了基准时刻时调用）。</summary>
        public static void ResetProgress(ScheduleSpec spec)
        {
            if (spec == null) return;
            spec.RunCount = 0;
            spec.LastRunAt = null;
        }
    }
}
