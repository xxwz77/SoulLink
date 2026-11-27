using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SoulLink.Services;

namespace SoulLink.Views
{
    public partial class StartupInventoryView : UserControl
    {
        private IReadOnlyList<StartupItemRow> _entries = Array.Empty<StartupItemRow>();
        private bool _activated;
        private bool _scanning;
        private bool _scanPending;
        private bool _reloadAccounts;
        private bool _settingAccounts;
        private bool _changingStartup;
        private bool _isElevated;
        private string _scanNote = "";

        public StartupInventoryView() => InitializeComponent();

        internal void Activate()
        {
            if (_activated) return;
            _activated = true;
            _ = RequestScanAsync(reloadAccounts: true);
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
            => await RequestScanAsync(reloadAccounts: true);

        private async void Account_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded && !_settingAccounts && !_changingStartup) await RequestScanAsync();
        }

        private void Scope_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded) ShowEntries();
        }

        private void Entry_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded) UpdateSelection();
        }

        private async Task RequestScanAsync(bool reloadAccounts = false, string? resultMessage = null)
        {
            _scanPending = true;
            _reloadAccounts |= reloadAccounts;
            if (_scanning) return;
            _scanning = true;
            _entries = Array.Empty<StartupItemRow>();
            _scanNote = "";
            ShowEntries();
            StatusLabel.Text = "正在读取启动项…";
            StatusLabel.ToolTip = null;
            RefreshButton.IsEnabled = false;
            try
            {
                while (_scanPending)
                {
                    _scanPending = false;
                    if (_reloadAccounts)
                    {
                        _reloadAccounts = false;
                        var sid = (AccountCombo.SelectedItem as StartupAccount)?.Sid;
                        var accounts = await Task.Run(StartupInventory.GetAccounts);
                        _settingAccounts = true;
                        try
                        {
                            AccountCombo.ItemsSource = accounts;
                            AccountCombo.SelectedItem = accounts.FirstOrDefault(a => a.Sid == sid)
                                ?? accounts.FirstOrDefault(a => a.IsCurrent)
                                ?? accounts.FirstOrDefault();
                        }
                        finally { _settingAccounts = false; }
                    }

                    var account = AccountCombo.SelectedItem as StartupAccount;
                    var result = await Task.Run(() => StartupInventory.Scan(account, manageableOnly: true));
                    if (_scanPending) continue;
                    _entries = result.Entries.Select(StartupItemRow.Describe).ToArray();
                    _isElevated = result.IsElevated;
                    _scanNote = string.Join(Environment.NewLine, result.Notes);
                }
                StatusLabel.Text = resultMessage ?? (_scanNote.Length > 0 ? "部分来源有读取说明，悬停查看详情。" : "");
                StatusLabel.ToolTip = _scanNote.Length > 0 ? _scanNote : null;
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "读取启动项失败：" + ex.Message;
            }
            finally
            {
                _scanning = false;
                RefreshButton.IsEnabled = !_changingStartup;
                ShowEntries();
            }
        }

        private void ShowEntries()
        {
            var scope = (ScopeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            IReadOnlyList<StartupItemRow> visible = scope switch
            {
                "machine" => _entries.Where(row => row.Entry.IsMachineWide).ToArray(),
                "account" => _entries.Where(row => !row.Entry.IsMachineWide).ToArray(),
                _ => _entries
            };
            EntriesList.ItemsSource = visible;
            CountLabel.Text = _scanning ? "" : visible.Count + " 项";
            EmptyListLabel.Visibility = !_scanning && visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            var row = EntriesList.SelectedItem as StartupItemRow;
            var entry = row?.Entry;
            var account = AccountCombo.SelectedItem as StartupAccount;
            ImpactLabel.Text = row?.Impact ?? "";
            TechnicalDetails.Visibility = entry == null ? Visibility.Collapsed : Visibility.Visible;
            if (entry == null)
            {
                TechnicalDetails.IsExpanded = false;
            }
            else
            {
                OriginalNameLabel.Text = "原始名称    " + entry.Name;
                OriginalNameLabel.ToolTip = entry.Name;
                TriggerLabel.Text = "启动方式    " + StartupItemRow.DescribeTrigger(entry.Source);
                LocationLabel.Text = "位置    " + entry.Location;
                LocationLabel.ToolTip = entry.Location;
                CommandLabel.Text = "命令    " + (entry.Command.Length == 0 ? "未记录" : entry.Command);
                CommandLabel.ToolTip = entry.Command;
            }
            bool migrate = entry?.IsMachineWide == true;
            string actionLabel = migrate ? "改为所选账号自启" : "移除该账号自启";
            string? blocked = entry switch
            {
                _ when _scanning => "正在读取启动项…",
                _ when _changingStartup => "正在修改启动项…",
                null => _entries.Count > 0 ? "选中一项可查看可用操作。" : "",
                _ when account == null => "请先选择一个已登录的账号。",
                { IsMachineWide: true, CanMigrate: false } => entry.MigrationBlockReason,
                { IsMachineWide: false, AccountAction: StartupAccountAction.None } => entry.AccountActionBlockReason,
                _ when !_isElevated && (migrate || !account.IsCurrent) => "请以管理员身份启动 SoulLink 后重试。",
                _ => null
            };
            SelectionHint.Text = blocked ?? (migrate ? "改为仅在所选账号登录时启动。" : "下次登录时生效。");
            StartupActionButton.Content = actionLabel;
            StartupActionButton.Visibility = entry == null ? Visibility.Collapsed : Visibility.Visible;
            StartupActionButton.ToolTip = blocked;
            StartupActionButton.IsEnabled = blocked == null;
        }

        private async void StartupAction_Click(object sender, RoutedEventArgs e)
        {
            if (!StartupActionButton.IsEnabled || EntriesList.SelectedItem is not StartupItemRow row
                || AccountCombo.SelectedItem is not StartupAccount account) return;
            bool migrate = row.Entry.IsMachineWide;
            string action = migrate ? "迁移启动项" : "移除账号启动项";
            string message = migrate
                ? $"将「{row.Title}」改为仅在「{account.Name}」登录时启动？"
                : $"从「{account.Name}」的登录启动列表中移除「{row.Title}」？\n\n" +
                  "下次登录时生效。当前运行的程序会继续运行。";
            if (MessageBox.Show(Window.GetWindow(this), message, action,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                != MessageBoxResult.Yes) return;

            _changingStartup = true;
            AccountCombo.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            UpdateSelection();
            StatusLabel.Text = "正在" + action + "…";
            try
            {
                var result = await Task.Run(() => migrate
                    ? StartupInventory.Migrate(row.Entry, account)
                    : StartupInventory.ChangeAccountStartup(row.Entry, account));
                await RequestScanAsync(resultMessage: result.Message);
            }
            catch (Exception ex)
            {
                StatusLabel.Text = action + "失败：" + ex.Message;
            }
            finally
            {
                _changingStartup = false;
                AccountCombo.IsEnabled = true;
                RefreshButton.IsEnabled = true;
                UpdateSelection();
            }
        }
    }
}
