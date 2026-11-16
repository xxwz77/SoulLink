using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>加入房间：房间码 + 选择要上线的本机账号。</summary>
    public partial class JoinRoomWindow : Window
    {
        public string RoomCode => (CodeBox.Text ?? "").Trim();

        public string UserName =>
            (UserCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        public string DisplayName
        {
            get
            {
                var item = UserCombo.SelectedItem as ComboBoxItem;
                string tag = item?.Tag as string ?? "";
                string content = item?.Content as string ?? tag;

                // Content 形如 "A1 · 思"，取后面的备注做显示名
                int idx = content.IndexOf(" · ", StringComparison.Ordinal);
                return idx > 0 ? content.Substring(idx + 3).Trim() : tag;
            }
        }

        public string RoleName => (RoleBox.Text ?? "").Trim();

        /// <summary>
        /// 加入房间窗口。
        /// lockCode 非空时：房间码预填并只读（自己房间的场景）。
        /// offerRooms 非空时：显示房间选择（大小号房的场景，可分别进大号/小号）。
        /// </summary>
        public JoinRoomWindow(string defaultUser, string lockCode = "",
                              List<(string title, string code)>? offerRooms = null)
        {
            InitializeComponent();
            FillUsers(defaultUser);

            if (!string.IsNullOrEmpty(lockCode))
            {
                CodeBox.Text = lockCode;
                CodeBox.IsReadOnly = true;
                CodeBox.Opacity = 0.7;
            }

            if (offerRooms != null && offerRooms.Count > 0)
            {
                RoomPickerPanel.Visibility = Visibility.Visible;
                foreach (var (title, code) in offerRooms)
                    RoomCombo.Items.Add(new ComboBoxItem { Content = title, Tag = code });
                RoomCombo.SelectedIndex = 0;

                // 选了房间 → 自动填码
                RoomCombo.SelectionChanged += (_, __) =>
                {
                    if (RoomCombo.SelectedItem is ComboBoxItem it)
                        CodeBox.Text = it.Tag as string ?? "";
                };
            }

            Loaded += (_, __) => { if (!CodeBox.IsReadOnly) CodeBox.Focus(); };
        }

        /// <summary>
        /// 填下拉框：列出本机所有账号。
        /// 来源：query user 的会话用户 + 配置里的账号。
        /// </summary>
        private void FillUsers(string defaultUser)
        {
            var names = new List<string>();

            try
            {
                // 配置里配过的账号优先
                foreach (var a in App.Config.Accounts)
                    if (!string.IsNullOrWhiteSpace(a.UserName))
                        names.Add(a.UserName);
            }
            catch { }

            try
            {
                // 再加系统里实际存在的会话用户
                var sessions = SessionService.QueryAsync().GetAwaiter().GetResult();
                foreach (var s in sessions)
                    if (!string.IsNullOrWhiteSpace(s.UserName))
                        names.Add(s.UserName);
            }
            catch { }

            if (names.Count == 0) names.Add(defaultUser);

            // 去重（忽略大小写），保留首次出现的顺序
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uniq = new List<string>();
            foreach (var n in names)
                if (seen.Add(n)) uniq.Add(n);

            UserCombo.Items.Clear();
            ComboBoxItem? select = null;

            foreach (var n in uniq)
            {
                // 带上备注名（若配置里有）
                string display = n;
                try
                {
                    foreach (var a in App.Config.Accounts)
                        if (string.Equals(a.UserName, n, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(a.DisplayName))
                        { display = n + " · " + a.DisplayName; break; }
                }
                catch { }

                var item = new ComboBoxItem { Content = display, Tag = n };
                UserCombo.Items.Add(item);

                if (string.Equals(n, defaultUser, StringComparison.OrdinalIgnoreCase))
                    select = item;
            }

            UserCombo.SelectedItem = select ?? (UserCombo.Items.Count > 0 ? UserCombo.Items[0] : null);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (RoomCode.Length == 0)
            { ShowError("房间码不能为空。"); return; }

            if (UserName.Length == 0)
            { ShowError("请选择要上线的用户。"); return; }

            DialogResult = true;
        }

        private void ShowError(string msg)
        {
            ErrorText.Text = msg;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
