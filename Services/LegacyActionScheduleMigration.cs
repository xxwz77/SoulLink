using System;
using System.Globalization;
using System.Linq;
using SoulLink.Models;

namespace SoulLink.Services;

internal static class LegacyActionScheduleMigration
{
    internal static bool Apply(AppConfig config, DateTime now)
    {
        bool changed = false;
        foreach (var action in config.Actions)
        {
            if (action?.LegacySchedule is not { Enabled: true } schedule || string.IsNullOrWhiteSpace(action.Id) ||
                string.IsNullOrWhiteSpace(action.AccountId) || string.IsNullOrWhiteSpace(action.ExePath) || !Enum.IsDefined(action.Kind) ||
                !config.Accounts.Any(account => account?.Id == action.AccountId && !string.IsNullOrWhiteSpace(account.UserName)) ||
                config.Actions.Count(item => item?.Id == action.Id) != 1)
                continue;

            string flowId = "legacy-action-schedule-" + action.Id;
            if (!config.Automations.Any(flow => flow?.Id == flowId))
            {
                var flow = BuildFlow(action, schedule, flowId, now);
                if (flow == null || FlowValidator.Validate(flow, config).HasError) continue;
                config.Automations.Add(flow);
            }

            action.LegacySchedule = null;
            changed = true;
        }
        return changed;
    }

    private static AutomationFlow? BuildFlow(ActionItem action, ScheduleSpec schedule, string flowId, DateTime now)
    {
        var trigger = new FlowNode { Kind = FlowNodeKind.Condition, Type = FlowNodeType.TrigSchedule, X = 60, Y = 45 };
        // The old scheduler attempted an occurrence once, including on failure.
        trigger.Set("latch", "false");
        var output = new FlowNode { Kind = FlowNodeKind.Action, Type = FlowNodeType.ActRunAction, X = 60, Y = 175 };
        output.Set("actionId", action.Id);
        var flow = new AutomationFlow
        {
            Id = flowId,
            Name = action.Name + " · 定时执行",
            AccountId = action.AccountId,
            CreatedAt = now,
            Enabled = true,
            Paused = schedule.Paused,
            PauseReason = schedule.PauseReason ?? "",
            EngineVersion = 2,
            LastRunAt = schedule.LastRunAt,
            RunCount = Math.Max(0, schedule.RunCount),
            Nodes = [trigger, output],
            Edges = [new FlowEdge { From = trigger.Id, To = output.Id }]
        };

        if (schedule.Kind == ScheduleKind.Interval)
        {
            int interval = Math.Max(1, schedule.IntervalSeconds);
            int seconds = (int)Math.Min(int.MaxValue, (long)interval + Math.Max(0, schedule.PostRunDelaySeconds));
            trigger.Type = FlowNodeType.TrigInterval;
            trigger.Set("intervalSeconds", Number(seconds));
            trigger.Set("catchUpSeconds", "5");
            trigger.Set("restartSeconds", Number(interval));
            var last = schedule.LastRunAt;
            // An overdue interval started over in the previous scheduler.
            flow.ContactPulseAt[trigger.Id] = last == null ? now
                : (now - last.Value).TotalSeconds >= seconds ? now.AddSeconds(interval - seconds) : last.Value;
            return flow;
        }

        string recurrence = schedule.Kind switch
        {
            ScheduleKind.Daily => "daily",
            ScheduleKind.Weekly => "weekly",
            ScheduleKind.Monthly => "monthly",
            ScheduleKind.Yearly => "yearly",
            ScheduleKind.Once => "once",
            _ => ""
        };
        if (recurrence.Length == 0) return null;
        trigger.Set("recurrence", recurrence);
        trigger.Set("catchUpSeconds", "5");
        if (schedule.Kind == ScheduleKind.Once)
        {
            if (schedule.OnceAt <= now || schedule.LastRunAt.HasValue || schedule.RunCount > 0) return null;
            DateTime at = schedule.OnceAt;
            long remainder = at.Ticks % TimeSpan.TicksPerSecond;
            if (remainder > 0 && at.Ticks <= DateTime.MaxValue.Ticks - TimeSpan.TicksPerSecond)
                at = at.AddTicks(TimeSpan.TicksPerSecond - remainder);
            trigger.Set("startDate", at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            trigger.Set("time", at.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            return flow;
        }

        trigger.Set("time", FormattableString.Invariant($"{schedule.Hour:00}:{schedule.Minute:00}:{schedule.Second:00}"));
        trigger.Set("weekDays", string.Join(",", (schedule.WeekDays ?? []).Where(day => day is >= 0 and <= 6).Distinct().Order()));
        trigger.Set("dayOfMonth", Number(Math.Clamp(schedule.DayOfMonth, 1, 31)));
        trigger.Set("month", Number(Math.Clamp(schedule.Month, 1, 12)));
        int step = Math.Max(0, schedule.PostRunDelaySeconds);
        trigger.Set("stepSeconds", Number(step));
        if (flow.RunCount > 0)
        {
            flow.ScheduleFireCount[trigger.Id] = flow.RunCount;
            long previousOffset = (long)(flow.RunCount - 1) * step;
            if (schedule.LastRunAt is { } previous && previous <= DateTime.MaxValue.AddSeconds(-2) &&
                previousOffset <= previous.Ticks / TimeSpan.TicksPerSecond)
                flow.ScheduleLastTriggeredDay[trigger.Id] = previous.AddSeconds(2 - previousOffset).Date;
        }
        return flow;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
