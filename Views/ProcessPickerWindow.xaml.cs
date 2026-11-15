using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    public partial class ProcessPickerWindow : Window
    {
        private readonly string _user;
        private List<RemoteProcess> _all = new List<RemoteProcess>();
        private List<RemoteProcess> _filtered = new List<RemoteProcess>();

        public RemoteProcess? Selected { get; private set; }

        public ProcessPickerWindow(string user)
        {
            InitializeComponent();
            _user = user;
            SubText.Text = "正在读取 " + user + " 桌面上运行的程序…";
            Loaded += async (_, __) => await LoadAsync();
        }

        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private async Task LoadAsync()
        {
            try
            {
                var sessions = SessionService.FilterUsers(await SessionService.QueryAsync());
                var s = SessionService.Find(sessions, _user);
                if (s == null)
                {
                    SubText.Text = "找不到用户 " + _user + " 的会话，请确认已登录。";
                    StatusText.Text = "";
                    return;
                }

                SubText.Text = _user + " · 会话 #" + s.Id + " 中运行的可见程序";
                _all = await Task.Run(() => ProcessService.List(s.Id));
                ApplyFilter();
            }
            catch (Exception ex)
            {
                SubText.Text = "读取失败：" + ex.Message;
            }
        }

        private void ApplyFilter()
        {
            string q = (SearchBox.Text ?? "").Trim();
            _filtered = string.IsNullOrEmpty(q)
                ? _all
                : _all.Where(p => p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                  || p.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                  || p.ExePath.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            List.ItemsSource = null;
            List.ItemsSource = new System.Windows.Data.ListCollectionView(_filtered);
            StatusText.Text = "共 " + _all.Count + " 个进程" + (q.Length > 0 ? "，匹配 " + _filtered.Count + " 个" : "");
            if (_filtered.Count > 0) List.SelectedItem = _filtered[0];
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void Reload_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (List.SelectedItem is not RemoteProcess p) { StatusText.Text = "请先选一个程序。"; return; }
            Selected = p;
            DialogResult = true;
        }

        private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Ok_Click(sender, new RoutedEventArgs());

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
