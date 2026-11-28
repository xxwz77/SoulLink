using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SoulLink.Models;

internal static class FlowEdits
{
    internal static AutomationFlow DraftOf(AutomationFlow source)
    {
        var draft = new AutomationFlow { Id = source.Id, CreatedAt = source.CreatedAt };
        CopyDefinition(draft, source);
        draft.EnsurePlcModel();
        return draft;
    }

    internal static string DefinitionOf(AutomationFlow flow) => JsonSerializer.Serialize(new
    {
        Name = flow.DisplayName.Trim(), flow.AutoName, flow.AccountId, flow.Enabled,
        Nodes = flow.Nodes.Select(n => new
        {
            n.Id, n.Type, n.Kind, X = Math.Round(n.X, 4), Y = Math.Round(n.Y, 4),
            n.Logic, n.Negated, n.BranchMode, n.DayBoundaryHour,
            Params = CompareParams(n)
        }),
        flow.Edges
    });

    private static IEnumerable<KeyValuePair<string, string>> CompareParams(FlowNode node)
    {
        var parameters = new SortedDictionary<string, string>(node.Params, StringComparer.Ordinal);
        foreach (var field in FlowNodeCatalog.Get(node.Type)?.Params ?? Enumerable.Empty<FlowParamDef>())
            parameters.TryAdd(field.Key, field.Key == "time" && node.Type == FlowNodeType.TrigSchedule
                ? PlcSchedule.ClockText(node) : field.Default);
        if (node.Type == FlowNodeType.TrigSchedule && parameters.TryGetValue("weekDays", out var days)
            && PlcSchedule.TryReadWeekDays(days, out int mask)) parameters["weekDays"] = mask.ToString();
        return parameters;
    }

    internal static bool SameOwner(AutomationFlow draft, AutomationFlow live)
        => string.Equals(draft.AccountId, live.AccountId, StringComparison.OrdinalIgnoreCase);

    internal static bool SameEnd(AutomationFlow draft, AutomationFlow live)
        => draft.EndNode?.Id == live.EndNode?.Id && draft.EndBoundaryHour == live.EndBoundaryHour;

    internal static void ReadRuntime(AutomationFlow draft, AutomationFlow? live, DateTime now, bool? today)
    {
        bool sameOwner = live != null && SameOwner(draft, live);
        draft.Paused = live?.Paused ?? false;
        draft.PauseReason = live?.PauseReason ?? "";
        if (live != null) draft.CreatedAt = sameOwner ? live.CreatedAt : now;
        draft.DailyLatchKey = sameOwner && SameEnd(draft, live!) ? live!.DailyLatchKey : "";
        draft.RunComplete = sameOwner && SameEnd(draft, live!) && live!.RunComplete;
        draft.RunDayKey = sameOwner ? live!.RunDayKey : "";
        draft.ScheduleFireCount = sameOwner ? new(live!.ScheduleFireCount, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        draft.ScheduleCycleDay = sameOwner ? new(live!.ScheduleCycleDay, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        draft.ScheduleLastTriggeredDay = sameOwner ? new(live!.ScheduleLastTriggeredDay, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        draft.ContactPulseAt = sameOwner ? new(live!.ContactPulseAt, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        draft.SatisfiedContacts = sameOwner ? new(live!.SatisfiedContacts, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        if (today.HasValue) PlcLogic.SetTodayRun(draft, now, today.Value);
    }

    internal static AutomationFlow Commit(AutomationFlow draft, AutomationFlow? live, DateTime now, bool? today)
    {
        bool sameOwner = live != null && SameOwner(draft, live);
        var result = sameOwner ? live!.CopyCurrentState() : new AutomationFlow
        {
            Id = draft.Id,
            CreatedAt = live == null ? draft.CreatedAt : now,
            RunCount = live?.RunCount ?? 0,
            Paused = live?.Paused ?? draft.Paused,
            PauseReason = live?.PauseReason ?? draft.PauseReason
        };
        if (sameOwner && !SameEnd(draft, live!))
        {
            PlcLogic.ClearDailyLatch(result);
            result.RunComplete = false;
            result.ToggleTodayRequested = null;
            result.RunResetRequested = false;
        }
        CopyDefinition(result, draft);
        if (sameOwner) KeepCurrentNodes(result, live!);
        if (today.HasValue && result.HasEnd)
        {
            result.ToggleTodayRequested = today;
            result.RunResetRequested = false;
            PlcLogic.SetTodayRun(result, now, today.Value);
        }
        return result;
    }

    private static void CopyDefinition(AutomationFlow target, AutomationFlow source)
    {
        target.Name = source.Name;
        target.AutoName = source.AutoName;
        target.AccountId = source.AccountId;
        target.Enabled = source.Enabled;
        target.EngineVersion = source.EngineVersion;
        target.LifecycleVersion = source.LifecycleVersion;
        target.DayBoundaryHour = source.DayBoundaryHour;
        target.ConditionMode = source.ConditionMode;
        target.ArmTimeoutMinutes = source.ArmTimeoutMinutes;
        target.PersistLatched = source.PersistLatched;
        target.Nodes = (source.Nodes ?? new()).Select(n => new FlowNode
        {
            Id = n.Id, Type = n.Type, Kind = n.Kind, X = n.X, Y = n.Y, Logic = n.Logic, Negated = n.Negated,
            BranchMode = n.BranchMode, DayBoundaryHour = n.DayBoundaryHour, Params = new(n.Params ?? new())
        }).ToList();
        target.Edges = (source.Edges ?? new()).Select(e => new FlowEdge { From = e.From, To = e.To, Branch = e.Branch }).ToList();
    }

    private static void KeepCurrentNodes(AutomationFlow result, AutomationFlow live)
    {
        var current = result.Nodes.ToDictionary(n => n.Id, n => n.Type, StringComparer.OrdinalIgnoreCase);
        var retained = live.Nodes.Where(node => current.TryGetValue(node.Id, out var type) && node.Type == type)
            .Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (retained.Count == live.Nodes.Count) return;

        Dictionary<string, T> Keep<T>(Dictionary<string, T> values)
            => values.Where(item => retained.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        HashSet<string> KeepSet(HashSet<string> values)
            => values.Where(retained.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

        result.RunningActions = Keep(result.RunningActions);
        result.WaitingActions = KeepSet(result.WaitingActions);
        result.PlcPower = Keep(result.PlcPower);
        result.ActionResults = Keep(result.ActionResults);
        result.ContactPulseAt = Keep(result.ContactPulseAt);
        result.ScheduleFireCount = Keep(result.ScheduleFireCount);
        result.ScheduleCycleDay = Keep(result.ScheduleCycleDay);
        result.ScheduleLastTriggeredDay = Keep(result.ScheduleLastTriggeredDay);
        result.DelayUntil = Keep(result.DelayUntil);
        result.SatisfiedContacts = KeepSet(result.SatisfiedContacts);
        result.LogTriggers = Keep(result.LogTriggers);
        result.RuntimeStates = Keep(result.RuntimeStates);
    }
}
