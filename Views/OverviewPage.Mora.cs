using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views;

public partial class OverviewPage
{
    private sealed record MoraTarget(string Id, string Name, string Caption, string Role, string Region);

    private Dictionary<(string Account, string Role), DailyMoraLedger.Composition> _moraCompositions = new();
    private (string Account, string Role)[] _moraAccounts = [];
    private DateTime _moraQueryDay;
    private DateTime _moraTotalsDay;
    private Task<bool>? _moraQuery;
    private bool _moraReadAgain;
    private bool _moraLastReadSucceeded;
    private long _nextMoraRefresh;
    private Task? _moraDetailsRefresh;
    private string _moraSummaryStatus = "";
    private string _moraDetailsStatus = "";

    private static (string Account, string Role) MoraAccount(Account account)
        => (account.UserName, MiyousheService.GetDefaultRole(account.UserName));

    private static (string Account, string Role)[] MoraAccounts(Account[] accounts)
        => accounts.Where(account => !string.IsNullOrWhiteSpace(account.UserName))
            .Select(MoraAccount).Distinct().ToArray();

    private void OnMoraLedgerRefreshed(string account, string role)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_refresh == null || !IsVisible) return;
            var keys = MoraAccounts(GetOverviewAccounts(App.Config));
            if (keys.Any(key => string.Equals(key.Account, account, StringComparison.OrdinalIgnoreCase) &&
                (key.Role.Length == 0 || key.Role == role)))
                _ = RefreshMoraTotalsAsync(force: true);
        }));
    }

    private Task<bool> RefreshMoraTotalsAsync(bool force = false)
    {
        var keys = MoraAccounts(GetOverviewAccounts(App.Config));
        var day = DogfoodRecordReader.CurrentBusinessDay();
        bool scopeChanged = _moraQueryDay != day || !keys.SequenceEqual(_moraAccounts);
        if (_moraQuery is { } running)
        {
            _moraReadAgain |= force || scopeChanged;
            return running;
        }
        if (!force && !scopeChanged && Environment.TickCount64 < _nextMoraRefresh)
            return Task.FromResult(_moraLastReadSucceeded);

        return _moraQuery = ReadMoraTotalsAsync();
    }

    private async Task<bool> ReadMoraTotalsAsync()
    {
        // Assign the shared task before even a zero-account query can complete.
        await Task.Yield();
        try
        {
            while (true)
            {
                _moraReadAgain = false;
                var keys = MoraAccounts(GetOverviewAccounts(App.Config));
                var day = DogfoodRecordReader.CurrentBusinessDay();
                _moraQueryDay = day;
                _moraAccounts = keys;
                var compositions = keys.Length == 0 ? new Dictionary<(string, string), DailyMoraLedger.Composition>()
                    : await DailyMoraLedger.QueryDiaryCompositionsAsync(keys, day);
                var accounts = GetOverviewAccounts(App.Config);
                var now = DateTime.Now;
                if (_moraReadAgain || DogfoodRecordReader.CurrentBusinessDay(now) != day ||
                    !keys.SequenceEqual(MoraAccounts(accounts))) continue;

                _nextMoraRefresh = Environment.TickCount64 + 60_000;
                _moraLastReadSucceeded = compositions != null;
                if (compositions != null)
                {
                    _moraCompositions = compositions;
                    _moraTotalsDay = day;
                    if (_refresh != null && IsVisible) UpdateAccountRows(accounts, day);
                }
                if (IsMoraCompositionOpen) RenderMoraComposition(readFinished: true);
                return _moraLastReadSucceeded;
            }
        }
        finally { _moraQuery = null; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!RefreshButton.IsEnabled) return;
        RefreshButton.IsEnabled = false;
        if (_moraDetailsRefresh is not { IsCompleted: false }) _moraDetailsStatus = "";
        ShowMoraRefreshStatus("正在刷新会话状态与札记今日获得…");
        var config = App.Config;
        var targets = GetOverviewAccounts(config)
            .Where(account => !string.IsNullOrWhiteSpace(account.UserName))
            .DistinctBy(account => account.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(account => GetMoraTarget(config, account)).ToArray();
        Task<bool> sessionRefresh = RefreshSessionStatesAsync(config);
        try
        {
            bool baselineRead = await RefreshMoraTotalsAsync(force: true);
            var before = _moraCompositions;
            var beforeDay = _moraTotalsDay;
            var results = new List<(MoraTarget Target, MiyousheService.WorkResult Result)>(targets.Length);
            for (int index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                ShowMoraRefreshStatus($"正在读取札记今日获得 · {target.Caption}（{index + 1}/{targets.Length}）");
                results.Add((target, await PullMoraAsync(config, target)));
            }
            bool read = await RefreshMoraTotalsAsync(force: true);
            string status = !ReferenceEquals(App.Config, config) ? "配置已切换"
                : results.Count == 0 ? "没有可刷新的可见账号"
                : string.Join(" · ", results.Select(entry =>
                {
                    var target = entry.Target;
                    var key = (target.Name, target.Role);
                    return target.Caption + "：" + MoraReceipt(
                        MoraTargetIsCurrent(config, target) ? entry.Result : new(false, state: "changed"),
                        read, baselineRead && beforeDay == _moraTotalsDay, before.GetValueOrDefault(key)?.Total,
                        _moraCompositions.GetValueOrDefault(key)?.Total);
                }));
            ShowMoraRefreshStatus(status);
            var detailTargets = results.Where(item => item.Result.summaryAt.HasValue)
                .Select(item => (item.Target, SummaryAt: item.Result.summaryAt!.Value)).ToArray();
            if (detailTargets.Length > 0 && _moraDetailsRefresh is not { IsCompleted: false })
                _moraDetailsRefresh = SyncMoraDetailsAsync(config, detailTargets);
            if (!await sessionRefresh) ShowMoraRefreshStatus(status + " · 会话状态刷新失败");
            if (_refresh != null && IsVisible) RefreshOverview();
        }
        catch (Exception ex)
        {
            LogBus.Warn("概览刷新失败", ex.GetType().Name);
            ShowMoraRefreshStatus("刷新失败，保留已有流水");
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    private static async Task<bool> RefreshSessionStatesAsync(AppConfig config)
    {
        try { await AccountStateCenter.ForceRefreshAsync(config); return true; }
        catch (Exception ex) { LogBus.Warn("概览会话状态刷新失败", ex.GetType().Name); return false; }
    }

    private static MoraTarget GetMoraTarget(AppConfig config, Account account)
        => new(account.Id, account.UserName, account.Caption, MiyousheService.GetDefaultRole(account.UserName),
            MiyousheSignPreferences.RegionFor(config.Settings, account.UserName));

    private static bool MoraTargetIsCurrent(AppConfig config, MoraTarget target)
        => ReferenceEquals(App.Config, config) && AccountScope.Accounts(config).Any(account =>
            account.Id == target.Id && account.UserName == target.Name &&
            MiyousheService.GetDefaultRole(account.UserName) == target.Role &&
            MiyousheSignPreferences.RegionFor(config.Settings, account.UserName) == target.Region);

    private static async Task<MiyousheService.WorkResult> PullMoraAsync(AppConfig config, MoraTarget target)
    {
        if (!MoraTargetIsCurrent(config, target)) return new(false, state: "changed");
        if (target.Region != "cn") return new(false, state: "unsupported");
        try
        {
            return await MiyousheService.RefreshDailyMoraAsync(target.Name, target.Role, default);
        }
        catch (Exception ex)
        {
            LogBus.Warn("概览摩拉拉取失败", ex.GetType().Name);
            return new(false, state: "failed");
        }
    }

    private async Task SyncMoraDetailsAsync(AppConfig config, (MoraTarget Target, DateTime SummaryAt)[] targets)
    {
        _moraDetailsStatus = "正在同步路线摩拉明细…";
        UpdateMoraRefreshStatus();
        var receipts = new List<string>(targets.Length);
        try
        {
            foreach (var (target, summaryAt) in targets)
            {
                if (!MoraTargetIsCurrent(config, target)) continue;
                var result = await MiyousheService.RefreshMoraDetailsAsync(target.Name, target.Role, summaryAt, default);
                if (!MoraTargetIsCurrent(config, target)) continue;
                receipts.Add(target.Caption + (result.ok ? "：路线摩拉明细已同步" : "：路线摩拉明细未同步完成"));
            }
        }
        catch (Exception ex)
        {
            LogBus.Warn("路线摩拉明细同步失败", ex.GetType().Name);
            receipts.Add("路线摩拉明细未同步完成");
        }
        _moraDetailsStatus = string.Join(" · ", receipts);
        UpdateMoraRefreshStatus();
    }

    internal static string MoraReceipt(MiyousheService.WorkResult result, bool read, bool comparable,
        DailyMoraLedger.Total? before, DailyMoraLedger.Total? after)
    {
        if (!result.ok)
            return result.state switch
            {
                "queued" => "米游社正在处理其他任务，请稍后重试",
                "needs_login" => "需要扫码授权或重新登录",
                "default_role" => "请在数据中心选择默认原神角色",
                "skipped" => "米游社账号未绑定原神角色",
                "unsupported" => "该地区暂不支持摩拉拉取",
                "changed" => "账号或默认角色已更改",
                "summary_unavailable" when after is { IsPartial: true } => "札记暂未返回今日获得，仅显示已采集流水",
                "summary_unavailable" when after != null => "札记暂未返回今日获得，保留上次统计",
                "summary_unavailable" => "札记暂未返回今日获得",
                _ => result.retryAfter.HasValue ? "拉取暂停，保留已有流水" : "拉取失败，保留已有流水"
            };
        if (!read) return "已拉取，本地流水读取失败";
        if (after == null) return "已拉取，今日暂无流水";
        if (after.IsPartial) return "札记暂未返回今日获得，仅显示已采集流水";
        if (!comparable) return "已拉取并读取今日摩拉";
        return before == after ? "已拉取，今日摩拉未变化" : "今日摩拉已更新";
    }

    private void ShowMoraRefreshStatus(string status)
    {
        _moraSummaryStatus = status;
        UpdateMoraRefreshStatus();
    }

    private void UpdateMoraRefreshStatus()
    {
        MoraRefreshStatus.Text = _moraDetailsStatus.Length == 0
            ? _moraSummaryStatus : _moraSummaryStatus + " · " + _moraDetailsStatus;
        MoraRefreshStatus.Visibility = Visibility.Visible;
    }
}
