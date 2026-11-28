using System;
using System.Collections.Generic;
using System.Linq;

namespace SoulLink.Models;

internal static class FlowName
{
    internal static bool UsesSuggestion(AutomationFlow flow, Account owner) =>
        flow.AutoName ?? (string.IsNullOrWhiteSpace(flow.Name) || flow.Name is "新的自动化" or "未命名流程" ||
                          flow.Name == owner.UserName + " 自动化");

    internal static string Suggest(AutomationFlow flow, AppConfig config)
    {
        if (flow.Nodes.Count == 0) return "新的自动化";
        var parts = new List<string>();
        var contacts = flow.Nodes.Where(n => n.IsContact).ToList();
        var trigger = contacts.FirstOrDefault(n => n.Type == FlowNodeType.TrigSchedule) ?? contacts.FirstOrDefault();
        var period = "";
        if (trigger?.Type == FlowNodeType.TrigSchedule)
        {
            var time = PlcSchedule.ClockText(trigger).Trim();
            if (time.Length == 8 && time.EndsWith(":00", StringComparison.Ordinal)) time = time[..5];
            parts.Add("定时 " + time);
            period = PlcSchedule.Recurrence(trigger) switch
            {
                "weekly" => "每周" + string.Join("、", trigger.Get("weekDays").Split(',')
                    .Select(s => int.TryParse(s, out int day) && day is >= 0 and <= 6 ? "日一二三四五六"[day].ToString() : "")),
                "monthly" => $"每月{trigger.GetInt("dayOfMonth", 1)}日",
                "yearly" => $"每年{trigger.GetInt("month", 1)}月{trigger.GetInt("dayOfMonth", 1)}日",
                "intervalDays" => $"每隔{trigger.GetInt("intervalDays", 1)}天",
                "once" => "仅一次",
                _ => "每日一次"
            };
        }
        else if (trigger != null) parts.Add(trigger.Type == FlowNodeType.TrigManual ? "手动" : trigger.Title);
        var actions = flow.Nodes.Where(n => n.Kind == FlowNodeKind.Action).ToList();
        foreach (var action in actions.Take(2))
        {
            var detail = action.Type switch
            {
                FlowNodeType.ActStartGroups => action.Get("groups"),
                FlowNodeType.ActStartOneDragon => action.Get("name"),
                FlowNodeType.ActRunAction => config.Actions.FirstOrDefault(a => a.Id == action.Get("actionId"))?.Name,
                _ => null
            };
            var label = action.Type == FlowNodeType.ActStartGroups ? "启动配置组" : action.Title;
            parts.Add(string.IsNullOrWhiteSpace(detail) ? label : label + "：" + detail.Trim());
        }
        if (actions.Count > 2) parts.Add($"等{actions.Count}个动作");
        if (period.Length > 0) parts.Add(period);
        else if (flow.HasEnd) parts.Add("每日一次");
        else if (trigger?.Type != FlowNodeType.TrigManual) parts.Add("循环触发");
        return string.Join(" · ", parts);
    }
}
