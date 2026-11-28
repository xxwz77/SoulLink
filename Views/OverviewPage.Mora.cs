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

    private Dictionary<(string Account, string Role), DailyMoraLedger.Total> _moraTotals = new();
    private (string Account, string Role)[] _moraAccounts = [];
    private DateTime _moraQueryDay;
    private DateTime _moraTotalsDay;
    private Task<bool>? _moraQuery;
    private bool _moraReadAgain;
    private bool _moraLastReadSucceeded;
    private long _nextMoraRefresh;

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
                var totals = keys.Length == 0 ? new Dictionary<(string, string), DailyMoraLedger.Total>()
                    : await DailyMoraLedger.QueryTotalsAsync(keys, day, DogfoodRecordReader.DayBoundaryHour);
                var accounts = GetOverviewAccounts(App.Config);
                var now = DateTime.Now;
                if (_moraReadAgain || DogfoodRecordReader.CurrentBusinessDay(now) != day ||
                    !keys.SequenceEqual(MoraAccounts(accounts))) continue;

                _nextMoraRefresh = Environment.TickCount64 + 60_000;
                _moraLastReadSucceeded = totals != null;
                if (totals != null)
                {
                    _moraTotals = totals;
                    _moraTotalsDay = day;
                    if (_refresh != null && IsVisible) UpdateAccountRows(accounts, now.Date, day);
                }
                return _moraLastReadSucceeded;
            }
        }
        finally { _moraQuery = null; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!RefreshButton.IsEnabled) return;
        RefreshButton.IsEnabled = false;
        ShowMoraRefreshStatus("正在刷新会话状态与今日摩拉…");
        var config = App.Config;
        var targets = GetOverviewAccounts(config)
            .Where(account => !string.IsNullOrWhiteSpace(account.UserName))
            .DistinctBy(account => account.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(account => GetMoraTarget(config, account)).ToArray();
        Task<bool> sessionRefresh = RefreshSessionStatesAsync(config);
        try
        {
            bool baselineRead = await RefreshMoraTotalsAsync(force: true);
            var before = _moraTotals;
            var beforeDay = _moraTotalsDay;
            var results = new List<(MoraTarget Target, MiyousheService.WorkResult Result)>(targets.Length);
            for (int index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                ShowMoraRefreshStatus($"正在刷新今日摩拉 · {target.Caption}（{index + 1}/{targets.Length}）");
                results.Add((target, await PullMoraAsync(config, target)));
            }
            bool read = await RefreshMoraTotalsAsync(force: true);
            bool sessionOk = await sessionRefresh;
            string status = !ReferenceEquals(App.Config, config) ? "配置已切换"
                : results.Count == 0 ? "没有可刷新的可见账号"
                : string.Join(" · ", results.Select(entry =>
                {
                    var target = entry.Target;
                    var key = (target.Name, target.Role);
                    return target.Caption + "：" + MoraReceipt(
                        MoraTargetIsCurrent(config, target) ? entry.Result : new(false, state: "changed"),
                        read, baselineRead && beforeDay == _moraTotalsDay, before.GetValueOrDefault(key),
                        _moraTotals.GetValueOrDefault(key));
                }));
            ShowMoraRefreshStatus(sessionOk ? status : status + " · 会话状态刷新失败");
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
            if (target.Role.Length == 0)
            {
                var login = await MiyousheService.LoginStatusAsync(target.Name, default);
                return new(false, state: login.State == "stored" ? "default_role" : "needs_login");
            }
            return await MiyousheService.RunForAccountAsync(target.Name, sign: false, ledger: true,
                default, expectedLedgerUid: target.Role);
        }
        catch (Exception ex)
        {
            LogBus.Warn("概览摩拉拉取失败", ex.GetType().Name);
            return new(false, state: "failed");
        }
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
                _ => result.retryAfter.HasValue ? "拉取暂停，保留已有流水" : "拉取失败，保留已有流水"
            };
        if (!read) return "已拉取，本地流水读取失败";
        if (after == null) return "已拉取，今日暂无流水";
        if (!comparable) return "已拉取并读取今日摩拉";
        return before == after ? "已拉取，今日摩拉未变化" : "今日摩拉已更新";
    }

    private void ShowMoraRefreshStatus(string status)
    {
        MoraRefreshStatus.Text = status;
        MoraRefreshStatus.Visibility = Visibility.Visible;
    }
}
