using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views;

public partial class OverviewPage
{
    private sealed record FlowRow(string Name, string Owner, AutomationStatusPresentation Status, string Executions,
        string ToggleText, bool CanEdit, AutomationFlow? Flow, AutomationProfile? Profile = null)
    {
        public string AccessibleName => $"{Name}，{Owner}，{Status.Text}，{Executions}";
        public string EditHint => CanEdit ? "打开流程编辑器" : "";
        public string EditTooltip => CanEdit ? $"编辑 {Name}" : AccessibleName;
        public bool CanToggle => CanEdit && Flow != null;
        public bool CanClearToday => CanEdit && Status.CanClearToday;
        public string ToggleName => $"{ToggleText}自动运行：{Name}，{Owner}";
    }

    private static bool CanManageFlowOwner(Account account, AppConfig config)
        => AccountScope.IsAllowed(account, config) || AccountScope.IsPrimaryUser(account.UserName, config);

    private static Account[] GetFlowOwners(AppConfig config)
        => config.Accounts.Where(account => account != null)
            .Select((account, index) => (Account: account, Order: account.SortOrder > 0 ? account.SortOrder : index + 1))
            .OrderByDescending(entry => AccountScope.IsPrimaryUser(entry.Account.UserName, config))
            .ThenBy(entry => entry.Order)
            .Select(entry => entry.Account).ToArray();

    private static string FlowOwnerLabel(Account account, AppConfig config)
        => AccountScope.IsPrimaryUser(account.UserName, config) ? "主账号" : DescribeAccount(account);

    private void RefreshFlows(AppConfig config)
    {
        var now = DateTime.Now;
        var owners = GetFlowOwners(config);
        var flowsByOwner = config.Automations.ToLookup(flow => flow.AccountId);
        var ownerIds = owners.Select(owner => owner.Id).ToHashSet();
        var flows = owners.DistinctBy(owner => owner.Id)
            .SelectMany(owner => flowsByOwner[owner.Id].Select(flow => ToFlowRow(flow, owner)))
            .Concat(config.Automations.Where(flow => !ownerIds.Contains(flow.AccountId)).Select(flow => ToFlowRow(flow, null)));
        var profiles = config.AutomationProfiles.Select(profile => new FlowRow(
            string.IsNullOrWhiteSpace(profile.Name) ? "未命名配置组" : profile.Name,
            "旧版一条龙", profile.Enabled ? new("已定时", "DesktopAccent") : new("手动", "TextMuted"),
            $"已记录 {profile.RecordedRunCount} 次", "", CanEdit: true, Flow: null, Profile: profile));

        var rows = flows.Concat(profiles).ToArray();
        ShowRows(FlowsList, rows);
        NoFlows.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewFlowButton.IsEnabled = owners.Any(owner => CanManageFlowOwner(owner, config));

        FlowRow ToFlowRow(AutomationFlow flow, Account? owner)
        {
            var (ok, fail) = flow.TodayCounts(now);
            string executions = $"完成 {flow.RunCount} 轮";
            if (ok > 0 || fail > 0) executions += $" · 今日成功 {ok}／失败 {fail}";
            return new(flow.DisplayName, owner == null ? "账号已移除" : FlowOwnerLabel(owner, config),
                AutomationStatusPresentation.ForFlow(flow, now), executions,
                !flow.Enabled ? "启用" : flow.Paused ? "恢复" : "暂停", owner != null && CanManageFlowOwner(owner, config), flow);
        }
    }

    private void ManageFlows_Click(object sender, RoutedEventArgs e)
        => Host?.NavigateTo(App.Config.Automations.Count == 0 && App.Config.AutomationProfiles.Count > 0 ? "automation" : "launch");

    private void NewFlow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var config = App.Config;
        var owners = GetFlowOwners(config).Where(owner => CanManageFlowOwner(owner, config)).ToArray();
        if (owners.Length == 1)
        {
            CreateFlow(owners[0]);
            return;
        }
        if (owners.Length == 0) return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var owner in owners)
        {
            var item = new MenuItem { Header = FlowOwnerLabel(owner, config) };
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                CreateFlow(owner);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void CreateFlow(Account owner)
    {
        var config = App.Config;
        if (!config.Accounts.Contains(owner) || !CanManageFlowOwner(owner, config)) return;
        Host?.CreateOverviewFlow(owner);
        RefreshFlows(config);
    }

    private static Account? GetFlowOwner(AutomationFlow flow, AppConfig config)
        => config.Automations.Contains(flow)
            ? config.Accounts.FirstOrDefault(account => account?.Id == flow.AccountId && CanManageFlowOwner(account, config)) : null;

    private void Flow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FlowRow row } || Host is not { } host) return;
        var config = App.Config;
        if (row.Profile is { } profile && config.AutomationProfiles.Contains(profile))
            host.NavigateToOverviewProfile(profile.Id);
        else if (row.Flow is { } flow && GetFlowOwner(flow, config) is { } owner)
        {
            host.EditOverviewFlow(flow, owner);
            RefreshFlows(config);
        }
    }

    private void ToggleFlow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FlowRow { Flow: { } flow } }) return;
        var config = App.Config;
        if (GetFlowOwner(flow, config) == null) return;
        flow.ToggleAutomaticRun();
        ConfigStore.Save(config);
        RefreshFlows(config);
    }

    private void ClearFlowToday_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FlowRow { Flow: { } flow } }) return;
        var config = App.Config;
        if (GetFlowOwner(flow, config) == null) return;
        AutomationStatusPresentation.ClearToday(config, flow, DateTime.Now);
        RefreshFlows(config);
    }
}
