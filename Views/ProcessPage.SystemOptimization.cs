using System;
using SoulLink.Controls;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoulLink.Services;

namespace SoulLink.Views
{
    public partial class ProcessPage
    {
        // ══════════ 系统优化（可关服务 + Defender 排除）══════════

        private bool _serviceRefreshBusy;
        private bool _serviceRefreshPending;
        private bool _defenderRefreshBusy;
        private bool _defenderRefreshPending;

        private async void RefreshServiceStates()
        {
            _serviceRefreshPending = true;
            if (_serviceRefreshBusy) return;
            _serviceRefreshBusy = true;
            ServiceStatus.Text = "正在读取服务状态…";
            try
            {
                while (_serviceRefreshPending)
                {
                    _serviceRefreshPending = false;
                    var services = await Task.Run(SystemOptimizer.QueryServices);
                    if (_serviceRefreshPending) continue;
                    ServiceList.Children.Clear();
                    foreach (var s in services)
                        ServiceList.Children.Add(BuildServiceRow(s));
                    ServiceStatus.Text = "";
                }
            }
            catch (Exception ex) { ServiceStatus.Text = "读取服务状态失败：" + ex.Message; }
            finally { _serviceRefreshBusy = false; }
        }

        private async void RefreshDefenderExclusions()
        {
            _defenderRefreshPending = true;
            if (_defenderRefreshBusy) return;
            _defenderRefreshBusy = true;
            DefenderStatus.Text = "正在读取目录排除…";
            try
            {
                while (_defenderRefreshPending)
                {
                    _defenderRefreshPending = false;
                    var (ok, msg, paths) = await Task.Run(SystemOptimizer.GetDefenderExclusions);
                    if (_defenderRefreshPending) continue;
                    DefenderExclusionList.Children.Clear();
                    if (!ok)
                    {
                        DefenderStatus.Text = msg;
                    }
                    else
                    {
                        DefenderStatus.Text = "当前共 " + paths.Count + " 个目录排除项。";
                        foreach (var p in paths)
                            DefenderExclusionList.Children.Add(BuildDefenderRow(p));
                    }
                }
            }
            catch (Exception ex) { DefenderStatus.Text = "读取目录排除失败：" + ex.Message; }
            finally { _defenderRefreshBusy = false; }
        }

        /// <summary>服务行：中文名 + 状态 + 停止/恢复按钮。</summary>
        private UIElement BuildServiceRow(SystemOptimizer.ServiceState s)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock
            {
                Text = s.DisplayName,
                FontSize = 11.5,
                Foreground = UiBrush.Get("TextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var state = new TextBlock
            {
                Text = s.StateText + " · " + s.Reason,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = s.Running
                    ? UiBrush.Get("MintBrush")
                    : UiBrush.Get("TextMuted"),
                ToolTip = s.Reason
            };
            text.Children.Add(title);
            text.Children.Add(state);
            System.Windows.Controls.Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var btn = new Button
            {
                Content = s.Running ? "停止" : "恢复",
                FontSize = 10.5,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                Style = s.Running ? (Style)FindResource("GhostButton") : (Style)FindResource("PrimaryButton"),
                Tag = s,
                Visibility = s.CanStop || s.CanRestore ? Visibility.Visible : Visibility.Collapsed,
                IsEnabled = SystemLauncher.IsElevated,
                ToolTip = s.Reason
            };
            btn.Click += ServiceToggle_Click;
            System.Windows.Controls.Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);

            // 提示：需要管理员才可操作
            var adminNote = new TextBlock
            {
                Text = SystemLauncher.IsElevated ? "" : "需管理员",
                FontSize = 9.5,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiBrush.Get("AmberBrush")
            };
            System.Windows.Controls.Grid.SetColumn(adminNote, 2);
            grid.Children.Add(adminNote);

            return grid;
        }

        /// <summary>Defender 排除行：路径 + 移除按钮。</summary>
        private UIElement BuildDefenderRow(string path)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = path,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = UiBrush.Get("TextSecondary"),
                ToolTip = path
            };
            System.Windows.Controls.Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var btn = new Button
            {
                Content = "移除",
                FontSize = 10.5,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("GhostButton"),
                Tag = path
            };
            btn.Click += RemoveDefenderExclusion_Click;
            System.Windows.Controls.Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);

            return grid;
        }

        private void RefreshDefenderExclusions_Click(object sender, RoutedEventArgs e)
            => RefreshDefenderExclusions();

        /// <summary>停止 / 恢复一项白名单服务。</summary>
        private async void ServiceToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not SystemOptimizer.ServiceState s) return;
            try
            {
                bool stop = s.Running;
                if (stop)
                {
                    bool ok = ThemedMessage.Confirm(Window.GetWindow(this),
                        "停止服务",
                        "将停止「" + s.DisplayName + "」。\n\n"
                        + s.Reason + "\n\n"
                        + "· 影响这台电脑的所有账号，只修改当前运行状态\n"
                        + "· 需要管理员权限",
                        true);
                    if (!ok) return;
                }

                b.IsEnabled = false;
                var r = stop
                    ? await Services.SystemOptimizer.StopServiceAsync(s.ServiceName).ConfigureAwait(true)
                    : await Services.SystemOptimizer.StartServiceAsync(s.ServiceName).ConfigureAwait(true);

                Services.LogBus.Write(r.ok ? Services.LogLevel.Ok : Services.LogLevel.Error,
                    (stop ? "停止服务" : "恢复服务") + " " + s.DisplayName, r.message);

                if (!r.ok)
                    ThemedMessage.Error(Window.GetWindow(this), stop ? "停止失败" : "恢复失败", r.message);
            }
            catch (Exception ex)
            {
                Services.LogBus.Error("服务操作失败", ex.Message);
            }
            finally
            {
                if (sender is Button bb) bb.IsEnabled = true;
                RefreshServiceStates();
            }
        }

        /// <summary>把 SoulLink 数据目录加入 Defender 排除（二次确认 + 权限提示）。</summary>
        private async void AddDefenderExclusion_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = AppIdentity.DataDir;

                bool ok = ThemedMessage.Confirm(Window.GetWindow(this),
                    "加入 Defender 排除",
                    "将把以下目录加入 Windows Defender 排除：\n\n"
                    + "  " + dir + "\n\n"
                    + "· 只排除该目录，不关闭实时保护\n"
                    + "· 需要管理员权限\n"
                    + "· 此目录的内容将被排除在扫描之外，设置会在重启后保留\n\n"
                    + "确定继续吗？",
                    true);
                if (!ok) return;

                var r = await Task.Run(() => Services.SystemOptimizer.AddDefenderExclusion(dir));
                Services.LogBus.Write(r.ok ? Services.LogLevel.Ok : Services.LogLevel.Error,
                    "Defender 排除", r.message);
                if (!r.ok)
                    ThemedMessage.Error(Window.GetWindow(this), "加入排除失败", r.message);
            }
            catch (Exception ex)
            {
                ThemedMessage.Error(Window.GetWindow(this), "加入排除失败", ex.Message);
            }
            finally
            {
                RefreshDefenderExclusions();
            }
        }

        /// <summary>从 Defender 排除中移除一个目录。</summary>
        private async void RemoveDefenderExclusion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string path) return;
            try
            {
                bool ok = ThemedMessage.Confirm(Window.GetWindow(this),
                    "移除 Defender 排除",
                    "将把以下目录从 Windows Defender 排除中移除：\n\n  " + path + "\n\n需要管理员权限。",
                    true);
                if (!ok) return;

                b.IsEnabled = false;
                var r = await Task.Run(() => Services.SystemOptimizer.RemoveDefenderExclusion(path));
                Services.LogBus.Write(r.ok ? Services.LogLevel.Ok : Services.LogLevel.Error,
                    "移除 Defender 排除", r.message);
                if (!r.ok)
                    ThemedMessage.Error(Window.GetWindow(this), "移除排除失败", r.message);
            }
            catch (Exception ex)
            {
                ThemedMessage.Error(Window.GetWindow(this), "移除排除失败", ex.Message);
            }
            finally
            {
                b.IsEnabled = true;
                RefreshDefenderExclusions();
            }
        }
    }
}
