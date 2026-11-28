using System;
using System.Collections.Generic;
using System.Linq;
using SoulLink.Models;

namespace SoulLink.Services;

internal static class FlowActionBinding
{
    internal static Account? ResolveTarget(FlowNode node, string flowAccountId, AppConfig config, out string error)
    {
        string user = node.Get("user").Trim();
        string bare = SessionService.BareName(user);
        var targets = config.Accounts.Where(account => account != null &&
            (user.Length == 0 ? account.Id == flowAccountId : bare.Length > 0 && string.Equals(
                SessionService.BareName(account.UserName), bare, StringComparison.OrdinalIgnoreCase)))
            .Take(2).ToArray();

        if (targets.Length == 0 || targets.Length == 1 &&
            (string.IsNullOrWhiteSpace(targets[0].Id) || string.IsNullOrWhiteSpace(targets[0].UserName)))
        {
            error = "没有可用的目标账号，请重新选择。";
            return null;
        }
        if (targets.Length > 1 || config.Accounts.Count(account => account?.Id == targets[0].Id) != 1)
        {
            error = "目标账号有重复记录，请修正账号配置或选择本账号。";
            return null;
        }

        error = "";
        return targets[0];
    }

    internal static IEnumerable<ActionItem> Choices(AppConfig config, Account target)
        => config.Actions.Where(action => action != null && !string.IsNullOrWhiteSpace(action.Id))
            .GroupBy(action => action.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1 && group.First().AccountId == target.Id)
            .Select(group => group.First());

    internal static ActionItem? Resolve(FlowNode node, string flowAccountId, AppConfig config, out string field, out string error)
    {
        field = "user";
        var target = ResolveTarget(node, flowAccountId, config, out error);
        if (target == null) return null;

        field = "actionId";
        return ResolveAction(node.Get(field), target, config, out error);
    }

    internal static ActionItem? ResolveAction(string actionId, Account target, AppConfig config, out string error)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            error = "请选择该目标账号的快捷动作。";
            return null;
        }

        var matches = config.Actions.Where(action => action?.Id == actionId).Take(2).ToArray();
        if (matches.Length == 1 && matches[0].AccountId == target.Id)
        {
            error = "";
            return matches[0];
        }

        error = matches.Length switch
        {
            0 => "原快捷动作已不存在，请重新选择。",
            1 => "原快捷动作不属于当前目标账号，请重新选择。",
            _ => "原快捷动作的标识有重复记录，请重新选择。"
        };
        return null;
    }
}
