using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 「发车动作」设置。
    ///
    /// ★ 为**每个账号**单独配置：先选账号，再给它设动作。
    ///   BGI 路径严格按该账号查（不再"随便取一个"）。
    /// </summary>
    public partial class MyDepartWindow : Window
    {
        private readonly List<string> _users = new();
        private bool _ready;

        public MyDepartWindow(string defaultUser)
        {
            InitializeComponent();

            CollectUsers(defaultUser);
            _ready = true;

            if (UserCombo.Items.Count > 0)
                UserCombo.SelectedIndex = 0;

            // 构造里不能 await：窗口加载完再拉服务器数据
            Loaded += async (_, __) =>
            {
                await UpdateOverviewAsync().ConfigureAwait(true);
            };
        }

        /// <summary>
        /// 收集本机所有账号：配置里的 + 系统会话里的。
        /// </summary>
        private void CollectUsers(string defaultUser)
        {
            var names = new List<string>();

            try
            {
                foreach (var a in App.Config.Accounts)
                    if (!string.IsNullOrWhiteSpace(a.UserName))
                        names.Add(a.UserName);
            }
            catch { }

            try
            {
                var sessions = SessionService.QueryAsync().GetAwaiter().GetResult();
                foreach (var s in sessions)
                    if (!string.IsNullOrWhiteSpace(s.UserName))
                        names.Add(s.UserName);
            }
            catch { }

            if (names.Count == 0) names.Add(defaultUser);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            UserCombo.Items.Clear();

            foreach (var n in names)
            {
                if (!seen.Add(n)) continue;
                _users.Add(n);

                string display = n;
                try
                {
                    foreach (var a in App.Config.Accounts)
                        if (string.Equals(a.UserName, n, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(a.DisplayName))
                        { display = n + " · " + a.DisplayName; break; }
                }
                catch { }

                UserCombo.Items.Add(new ComboBoxItem { Content = display, Tag = n });
            }
        }

        private string CurrentUser() =>
            (UserCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        /// <summary>
        /// 查**该账号**的 BGI 路径。
        /// ★ 修复：原来遍历所有路径取第一个，导致显示的是别人账号的 BGI。
        /// </summary>
        private static string BgiPathOf(string user)
        {
            try
            {
                // ★ 统一走解析器：手动指定 > 正在运行 > 记忆
                if (!string.IsNullOrWhiteSpace(user))
                    return BgiPathResolver.GetPath(user);
            }
            catch { }
            return "";
        }

        /// <summary>从指定 BGI 目录读一条龙 / 配置组名。</summary>
        private static List<string> ListBgiConfigs(string user, string sub)
        {
            var list = new List<string>();
            try
            {
                string exe = BgiPathOf(user);
                if (exe.Length == 0 || !File.Exists(exe)) return list;

                string? dir = Path.GetDirectoryName(exe);
                if (dir == null) return list;

                string target = Path.Combine(dir, "User", sub);
                if (!Directory.Exists(target)) return list;

                foreach (var f in Directory.GetFiles(target, "*.json"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (!string.IsNullOrWhiteSpace(n)) list.Add(n);
                }
                list.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return list;
        }

        // ══════════ 账号切换 ══════════

        private async void User_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;

            string user = CurrentUser();

            // BGI 路径
            string path = BgiPathOf(user);
            if (path.Length == 0)
            {
                BgiPathText.Text = "（该账号还没配置 BGI 路径）";
                BgiPathText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9B, 0x9B));
            }
            else if (!File.Exists(path))
            {
                BgiPathText.Text = path + "  ← 文件不存在";
                BgiPathText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9B, 0x9B));
            }
            else
            {
                BgiPathText.Text = path;
                BgiPathText.Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xE8, 0xC8));
            }

            // 一条龙候选：直接放字符串（原生 ComboBox 会正确显示选中项）
            DragonCombo.Items.Clear();
            var dragons = ListBgiConfigs(user, "OneDragon");
            foreach (var n in dragons) DragonCombo.Items.Add(n);

            DragonHint.Text = dragons.Count > 0
                ? "从 " + user + " 的 BGI 目录读取，共 " + dragons.Count + " 个。点右侧箭头选择，也可以直接输入。"
                : "该账号的 BGI 目录下没找到一条龙配置。可以直接输入名称。";

            // 配置组候选：渲染成可点的列表
            GroupList.Children.Clear();
            var groups = ListBgiConfigs(user, "ScriptGroup");

            if (groups.Count == 0)
            {
                GroupList.Children.Add(new TextBlock
                {
                    Text = "（该账号的 BGI 目录下没找到配置组）",
                    FontSize = 10.5,
                    Foreground = (Brush)FindResource("TextMuted"),
                    Margin = new Thickness(8, 8, 8, 8)
                });
            }
            else
            {
                foreach (var g in groups)
                {
                    string name = g;
                    var btn = new Button
                    {
                        Style = (Style)FindResource("GhostButton"),
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(3),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Content = new TextBlock { Text = name, FontSize = 11 }
                    };
                    btn.Click += (_, __) => AppendGroup(name);
                    GroupList.Children.Add(btn);
                }
            }

            GroupsHint.Text = groups.Count > 0
                ? "该账号有 " + groups.Count + " 个配置组。点上面的名字添加，可加多个。"
                : "";

            // 快捷动作（全局通用，不分账号）
            ActionCombo.Items.Clear();
            try
            {
                foreach (var a in App.Config.Actions)
                    ActionCombo.Items.Add(new ComboBoxItem { Content = a.Name, Tag = a.Id });
            }
            catch { }

            // 载入该账号已有的配置
            await LoadForAsync(user);
        }

        private async Task LoadForAsync(string user)
        {
            var dto = await LobbyApi.GetDepartConfigAsync().ConfigureAwait(true);
            var d = new LobbyApi.UserDepartDto
            {
                Mode = dto.Mode, ActionId = dto.ActionId,
                DragonName = dto.DragonName, Groups = dto.Groups, Summary = dto.Summary
            };

            // 模式
            bool found = false;
            foreach (ComboBoxItem it in ModeCombo.Items)
                if ((it.Tag as string) == d.Mode) { it.IsSelected = true; found = true; break; }
            if (!found) ModeCombo.SelectedIndex = 0;

            // 快捷动作
            if (d.ActionId.Length > 0)
                foreach (ComboBoxItem it in ActionCombo.Items)
                    if ((it.Tag as string) == d.ActionId) { it.IsSelected = true; break; }

            // 一条龙：设选中项（原生 ComboBox 会显示）
            DragonCombo.SelectedItem = null;
            if (d.DragonName.Length > 0)
            {
                foreach (var it in DragonCombo.Items)
                    if (string.Equals(it as string, d.DragonName, StringComparison.OrdinalIgnoreCase))
                    { DragonCombo.SelectedItem = it; break; }

                // 列表里没有就临时加一个，保证能看到
                if (DragonCombo.SelectedItem == null)
                {
                    DragonCombo.Items.Add(d.DragonName);
                    DragonCombo.SelectedItem = d.DragonName;
                }
            }

            // 配置组
            GroupsBox.Text = d.Groups;

            UpdatePanels();
        }

        /// <summary>把一个配置组追加到已选文本框（去重）。</summary>
        private void AppendGroup(string name)
        {
            string cur = (GroupsBox.Text ?? "").Trim();
            var parts = cur.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            if (!parts.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                parts.Add(name);
                GroupsBox.Text = string.Join(" ", parts);
            }
        }

        private string CurrentMode() =>
            (ModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        private void Mode_Changed(object sender, SelectionChangedEventArgs e) => UpdatePanels();

        private void UpdatePanels()
        {
            if (ActionPanel == null) return;
            string mode = CurrentMode();
            ActionPanel.Visibility = mode == "action" ? Visibility.Visible : Visibility.Collapsed;
            DragonPanel.Visibility = mode == "dragon" ? Visibility.Visible : Visibility.Collapsed;
            GroupsPanel.Visibility = mode == "groups" ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>概览：列出所有账号已配置的动作。</summary>
        private async Task UpdateOverviewAsync()
        {
            OverviewPanel.Children.Clear();

            foreach (var u in _users)
            {
                var d = await LobbyApi.GetDepartConfigAsync().ConfigureAwait(true);
                bool has = !string.IsNullOrEmpty(d.Mode);
                string text = has ? d.Summary : "未设置";
                if (has)
                {
                    var marks = new List<string> { "停BGI" };   // 固定
                    try
                    {
                        if (App.Config.Settings.DepartCloseGenshin != null
                            && App.Config.Settings.DepartCloseGenshin.TryGetValue(u, out var cg) && cg)
                            marks.Add("关原神");
                    }
                    catch { }
                    text += "  [" + string.Join(" ", marks) + "]";
                }

                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                row.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center,
                    Fill = new SolidColorBrush(has
                        ? Color.FromRgb(0x41, 0xF0, 0xC0)
                        : Color.FromRgb(0x3A, 0x46, 0x66))
                });

                var name = new TextBlock
                {
                    Text = u, FontSize = 10.5, Width = 80,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);

                var val = new TextBlock
                {
                    Text = text, FontSize = 10.5,
                    Foreground = has
                        ? (Brush)FindResource("TextPrimary")
                        : (Brush)FindResource("TextMuted"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(val, 2);
                row.Children.Add(val);

                OverviewPanel.Children.Add(row);
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            string user = CurrentUser();
            if (user.Length == 0) { ShowError("请先选择账号。"); return; }

            string mode = CurrentMode();
            var dto = new LobbyApi.UserDepartDto
            {
                Mode = mode,
                StopBgiFirst = true,   // 固定：一条龙/配置组都必须先停 BGI
                CloseGenshin = CloseGenshinCheck?.IsChecked == true
            };

            if (mode == "action")
            {
                dto.ActionId = (ActionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                if (dto.ActionId.Length == 0) { ShowError("请选择一个快捷动作。"); return; }
                dto.Summary = (ActionCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "";
            }
            else if (mode == "dragon")
            {
                dto.DragonName = (DragonCombo.SelectedItem as string
                                 ?? DragonCombo.Text ?? "").Trim();
                if (dto.DragonName.Length == 0) { ShowError("请填写一条龙名称。"); return; }
                if (BgiPathOf(user).Length == 0)
                { ShowError("账号「" + user + "」还没配置 BGI 路径，无法启动一条龙。"); return; }
                dto.Summary = "一条龙：" + dto.DragonName;
            }
            else if (mode == "groups")
            {
                dto.Groups = (GroupsBox.Text ?? "").Trim();
                if (dto.Groups.Length == 0) { ShowError("请填写配置组名。"); return; }
                if (BgiPathOf(user).Length == 0)
                { ShowError("账号「" + user + "」还没配置 BGI 路径，无法启动配置组。"); return; }
                dto.Summary = "配置组：" + dto.Groups;
            }
            else
            {
                dto.Summary = "不执行";
            }

            await LobbyApi.SetDepartConfigAsync(new LobbyApi.DepartConfig
            {
                Mode = dto.Mode, ActionId = dto.ActionId,
                DragonName = dto.DragonName, Groups = dto.Groups, Summary = dto.Summary
            }).ConfigureAwait(true);

            ErrorText.Visibility = Visibility.Collapsed;
            await UpdateOverviewAsync().ConfigureAwait(true);

            MessageBox.Show("已保存「" + user + "」的发车动作：\n\n" + dto.Summary
                + "\n\n可以继续切换账号设置其他人的。",
                "SoulLink", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowError(string msg)
        {
            ErrorText.Text = msg;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
