using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views;

public partial class OverviewPage : UserControl
{
    private sealed record AccountSummary(string Caption, string Session, string Bgi, bool BgiRunning, string Group,
        string Ending = "—", string DogfoodExp = "—", string DogfoodMora = "—", string MoraIncome = "—")
    {
        public string AccessibleName => $"{Caption}，会话{Session}，BetterGI {Bgi}，当前配置组 {Group}，"
            + $"今日 A/B 线 {Ending}，狗粮经验汇总 {DogfoodExp}，狗粮摩拉汇总 {DogfoodMora}，今日摩拉总收益 {MoraIncome}";
    }

    private sealed class AccountRow(string id) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public AccountSummary Summary { get; private set; } = new("", "检测中", "—", false, "—");
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(AccountSummary summary)
        {
            if (Summary == summary) return;
            Summary = summary;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
    }

    private sealed record QuickAction(ActionItem Model, string Name, string Icon)
    {
        public string AccessibleName => $"执行快捷动作，{Name}";
    }

    private readonly record struct LogTailKey(string? AccountId, string? File, long Seq, int LineCount, bool? Bound, string? Error);

    private IDisposable? _refresh;
    private DispatcherOperation? _logScroll;
    private string? _selectedAccountId;
    private LogTailKey? _renderedLog;
    private int _logRefreshQueued;
    private bool _syncingAccounts;
    private GridLength _visibleLogHeight = new(1, GridUnitType.Star);
    private bool _logHidden;
    private bool _ready;

    public OverviewPage()
    {
        InitializeComponent();
        var state = ConfigStore.LoadState();
        RestoreSplitterPosition(LogRow, AccountsRow, state.OverviewLogHeightFraction);
        RestoreSplitterPosition(ActionsRow, AutomationRow, state.OverviewActionsHeightFraction);
        _ready = true;
        Unloaded += (_, _) => OnPageDeactivated();
    }

    internal void OnPageActivated()
    {
        bool activating = _refresh == null;
        if (_refresh == null)
        {
            bool refreshAll = false;
            _refresh = TickHub.Register("Overview", 1, () =>
            {
                if (!IsVisible) return;
                if (refreshAll) RefreshOverview();
                else RefreshFlows(App.Config);
                refreshAll = !refreshAll;
            });
            BgiLogCenter.Changed += OnLogsChanged;
            MiyousheService.LedgerRefreshed += OnMoraLedgerRefreshed;
        }
        RefreshOverview(forceMora: activating);
        if (_logScroll == null) QueueLogPosition(LogText.VerticalOffset);
    }

    internal void OnPageDeactivated()
    {
        _refresh?.Dispose();
        _refresh = null;
        BgiLogCenter.Changed -= OnLogsChanged;
        MiyousheService.LedgerRefreshed -= OnMoraLedgerRefreshed;
        _logScroll?.Abort();
        _logScroll = null;
    }

    private void OnLogsChanged()
    {
        if (Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _logRefreshQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _logRefreshQueued, 0);
            if (_refresh != null && IsVisible) RefreshSelectedData(FindSelectedAccount());
        }));
    }

    private static Account[] GetOverviewAccounts(AppConfig config)
        => AccountScope.Accounts(config)
            .Select((account, index) => (Account: account, Index: index))
            .OrderBy(entry => entry.Account.SortOrder <= 0 ? entry.Index + 1 : entry.Account.SortOrder)
            .Select(entry => entry.Account)
            .ToArray();

    private void RefreshOverview(bool forceMora = false)
    {
        var config = App.Config;
        var accounts = GetOverviewAccounts(config);
        var now = DateTime.Now;
        var gameDay = DogfoodRecordReader.CurrentBusinessDay(now);
        UpdateAccountRows(accounts, now.Date, gameDay);
        _ = RefreshMoraTotalsAsync(forceMora);
        var selected = accounts.FirstOrDefault(account => account.Id == _selectedAccountId);

        RefreshQuickActions(config, selected);
        RefreshFlows(config);
        RefreshSelectedData(selected);
    }

    private void RefreshQuickActions(AppConfig config, Account? selected)
    {
        var actions = selected is { Id.Length: > 0 } account
            ? config.Actions.Where(action => action?.AccountId == account.Id)
                .Select(action => new QuickAction(action, action.Name, action.Kind switch
                {
                    ActionKind.Close => "\uE71A",
                    ActionKind.Restart => "\uE72C",
                    _ => "\uE768"
                })).ToArray()
            : Array.Empty<QuickAction>();
        bool hasAccount = selected is { Id.Length: > 0 };
        ShowRows(QuickActionsList, actions);
        NoActions.Text = hasAccount ? "当前账号暂无快捷动作" : "请选择一个账号";
        NoActions.Visibility = actions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewActionButton.IsEnabled = hasAccount;
        ManageActionsButton.IsEnabled = hasAccount;
    }

    private void UpdateAccountRows(Account[] accounts, DateTime calendarDay, DateTime gameDay)
    {
        // Keep containers and keyboard focus when only live status changes.
        var previous = AccountsList.ItemsSource as AccountRow[];
        var rows = previous != null && previous.Select(row => row.Id).SequenceEqual(accounts.Select(account => account.Id))
            ? previous : accounts.Select(account => new AccountRow(account.Id)).ToArray();
        for (int index = 0; index < accounts.Length; index++)
        {
            var account = accounts[index];
            var state = AccountStateCenter.Get(account.Id);
            var log = state?.BgiRunning == true ? BgiLogCenter.Snapshot(account.Id) : null;
            var ending = state?.DogfoodRecords.FirstOrDefault(record => record.BusinessDay == gameDay)?.Route;
            var dogfood = state?.CollectedDogfood;
            var mora = _moraTotalsDay == gameDay ? _moraTotals.GetValueOrDefault(MoraAccount(account)) : null;
            rows[index].Update(SummarizeAccount(account, state, log) with
            {
                Ending = ending switch
                {
                    DogfoodEndingRoute.EndingA => "A · 联机",
                    DogfoodEndingRoute.EndingB => "B · 自跑",
                    _ => "—"
                },
                DogfoodExp = dogfood?.Day == calendarDay && dogfood.Exp > 0 ? dogfood.Exp.ToString("N0") : "—",
                DogfoodMora = dogfood?.Day == calendarDay && dogfood.Mora > 0 ? dogfood.Mora.ToString("N0") : "—",
                MoraIncome = mora == null ? "暂无数据" : mora.Amount.ToString("N0") + (mora.IsPartial ? "（部分）" : "")
            });
        }

        var chosen = rows.FirstOrDefault(row => row.Id == _selectedAccountId)
            ?? rows.FirstOrDefault(row => row.Summary.BgiRunning)
            ?? rows.FirstOrDefault();
        _selectedAccountId = chosen?.Id;
        _syncingAccounts = true;
        try
        {
            if (!ReferenceEquals(previous, rows)) AccountsList.ItemsSource = rows;
            if (AccountsList.SelectedItem != chosen) AccountsList.SelectedItem = chosen;
        }
        finally { _syncingAccounts = false; }

        NoAccounts.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }


    private static AccountSummary SummarizeAccount(Account account, AccountState? state, BgiLogSnapshot? log)
    {
        bool running = state?.BgiRunning == true;
        string session = state == null ? "检测中"
            : !state.HasSession ? "未登录"
            : state.SessionState.Equals("Disc", StringComparison.OrdinalIgnoreCase) ||
                state.SessionState.Equals("Disconnected", StringComparison.OrdinalIgnoreCase) ? "已断开" : "已登录";
        string? group = string.IsNullOrWhiteSpace(log?.CurrentGroup) ? state?.CurrentGroup : log.CurrentGroup;
        return new(account.Caption, session, running ? "运行中" : state?.HasSession == true ? "未运行" : "—",
            running, running && !string.IsNullOrWhiteSpace(group) ? group : "—");
    }

    private void RefreshSelectedData(Account? account)
    {
        var state = account == null ? null : AccountStateCenter.Get(account.Id);
        var log = account == null ? null : BgiLogCenter.Snapshot(account.Id);

        OpenSelectedData.IsEnabled = account != null;
        LogAccount.Text = account?.Caption ?? "—";
        if (!_logHidden) RefreshLog(account, state, log);
    }


    private static string DescribeAccount(Account account)
        => account.Caption == account.UserName ? account.Caption : $"{account.Caption} · {account.UserName}";

    private static void ShowRows<T>(ItemsControl control, T[] rows)
    {
        if (control.ItemsSource is T[] previous && previous.SequenceEqual(rows)) return;
        control.ItemsSource = rows;
    }

    private void RefreshLog(Account? account, AccountState? state, BgiLogSnapshot? log)
    {
        var key = new LogTailKey(account?.Id, log?.LogFile, log?.LastSeq ?? 0, log?.Tail.Count ?? 0,
            log?.Bound ?? state?.Bound, log?.Error);
        var previous = _renderedLog;
        if (previous == key) return;
        _renderedLog = key;

        bool sameFile = previous?.AccountId == key.AccountId && previous?.File == key.File;
        double offset = sameFile ? LogText.VerticalOffset : 0;
        LogText.Text = log is { Tail.Count: > 0 }
            ? string.Join(Environment.NewLine, log.Tail.TakeLast(90).Select(line =>
                line.At == DateTime.MinValue ? line.Text : $"{line.At:HH:mm:ss}    {line.Text}"))
            : account == null ? "没有可查看的账号。"
            : !string.IsNullOrWhiteSpace(log?.Error) ? "读取 BetterGI 日志失败：" + log.Error
            : key.Bound == false ? "尚未绑定 BetterGI 路径。可在设置中绑定后查看日志。"
            : key.Bound == true ? "等待 BetterGI 写入运行日志。" : "正在检查 BetterGI 日志。";
        QueueLogPosition(offset);
    }

    private void QueueLogPosition(double offset)
    {
        _logScroll?.Abort();
        _logScroll = null;
        if (_logHidden) return;
        _logScroll = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _logScroll = null;
            if (_logHidden || !IsVisible) return;
            if (FollowLog.IsChecked == true) LogText.ScrollToEnd();
            else LogText.ScrollToVerticalOffset(offset);
        }));
    }

    private MainWindow? Host => Window.GetWindow(this) as MainWindow;

    private Account? FindSelectedAccount()
        => App.Config.Accounts.FirstOrDefault(account => account != null && account.Id == _selectedAccountId && AccountScope.IsAllowed(account));

    private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingAccounts || e.Source != AccountsList || AccountsList.SelectedItem is not AccountRow row) return;
        _selectedAccountId = row.Id;
        RefreshOverview();
    }

    private void AccountsList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(AccountsList, source) is not ListBoxItem item) return;
        item.IsSelected = true;
        e.Handled = true;
    }


    private void ManageAccounts_Click(object sender, RoutedEventArgs e) => Host?.NavigateTo("console");

    private void OpenAccountData_Click(object sender, RoutedEventArgs e)
    {
        if (FindSelectedAccount() is { } account) Host?.NavigateToAccountData(account.Id);
        else RefreshOverview();
    }

    private void ManageActions_Click(object sender, RoutedEventArgs e)
    {
        if (FindSelectedAccount() is { Id.Length: > 0 } account) Host?.NavigateToAccountActions(account.Id);
    }

    private void NewAction_Click(object sender, RoutedEventArgs e)
    {
        if (FindSelectedAccount() is { Id.Length: > 0 } account) Host?.CreateOverviewAction(account);
        RefreshOverview();
    }

    private async void RunAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ActionItem action } button || Host is not { } host) return;
        if (FindSelectedAccount() is not { Id.Length: > 0 } account || action.AccountId != account.Id ||
            !App.Config.Actions.Contains(action)) return;
        button.IsEnabled = false;
        try
        {
            var result = await host.RunOverviewActionAsync(action);
            if (!result.Ok) ThemedMessage.Info(host, "执行失败", result.Message);
            if (IsVisible) RefreshOverview();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void FollowLog_Checked(object sender, RoutedEventArgs e)
    {
        if (_ready) QueueLogPosition(LogText.VerticalOffset);
    }

    private static void RestoreSplitterPosition(RowDefinition row, RowDefinition otherRow, double? fraction)
    {
        if (fraction is not (> 0 and < 1)) return;
        row.Height = new GridLength(fraction.Value, GridUnitType.Star);
        otherRow.Height = new GridLength(1 - fraction.Value, GridUnitType.Star);
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!e.Canceled) SaveSplitterPosition(sender);
    }

    private void Splitter_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down) SaveSplitterPosition(sender);
    }

    private void SaveSplitterPosition(object splitter)
    {
        bool log = splitter == LogSplitter;
        var row = log ? LogRow : ActionsRow;
        var otherRow = log ? AccountsRow : AutomationRow;
        if (!row.Height.IsStar || !otherRow.Height.IsStar) return;
        double fraction = row.Height.Value / (row.Height.Value + otherRow.Height.Value);
        if (fraction is not (> 0 and < 1)) return;

        var state = ConfigStore.LoadState();
        double? savedFraction = log ? state.OverviewLogHeightFraction : state.OverviewActionsHeightFraction;
        if (savedFraction == fraction) return;
        if (log) state.OverviewLogHeightFraction = fraction;
        else state.OverviewActionsHeightFraction = fraction;
        ConfigStore.SaveState(state);
    }

    private void HideLog_Click(object sender, RoutedEventArgs e)
    {
        if (!_logHidden) _visibleLogHeight = LogRow.Height;
        _logHidden = !_logHidden;
        LogContent.Visibility = LogSplitter.Visibility = _logHidden ? Visibility.Collapsed : Visibility.Visible;
        LogRow.MinHeight = _logHidden ? 0 : 80;
        LogRow.Height = _logHidden ? GridLength.Auto : _visibleLogHeight;
        HideLogButton.Content = _logHidden ? "显示" : "隐藏";
        if (!_logHidden)
        {
            RefreshSelectedData(FindSelectedAccount());
            QueueLogPosition(LogText.VerticalOffset);
        }
    }

}
