using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SoulLink.Models;

namespace SoulLink.Views
{
    /// <summary>用户与 RDP 连接设置。既改账号显示名，也改画面参数。</summary>
    public partial class RdpAddressWindow : Window
    {
        private readonly Account _account;

        public RdpAddressWindow(Account account)
        {
            InitializeComponent();
            _account = account;

            TitleText.Text = account.Caption;
            SubText.Text = "用户 " + account.UserName + " 的中控台接入参数";

            NameBox.Text = account.DisplayName ?? "";
            HostBox.Text = account.RdpHost ?? "";
            PortBox.Text = (account.RdpPort > 0 ? account.RdpPort : 3389).ToString();
            UserBox.Text = account.EffectiveRdpUserName;

            int w = account.RdpWidth > 0 ? account.RdpWidth : 1280;
            int h = account.RdpHeight > 0 ? account.RdpHeight : 720;
            WidthBox.Text = w.ToString();
            HeightBox.Text = h.ToString();

            SelectByTag(ScaleCombo, (account.RdpScaleMode >= 0 ? account.RdpScaleMode : 0).ToString());
            SelectByTag(DepthCombo, (account.RdpColorDepth > 0 ? account.RdpColorDepth : 32).ToString());

            AutoDiscCheck.IsChecked = account.RdpAutoDisconnect;
            LowBwCheck.IsChecked = account.RdpLowBandwidth;

            Loaded += (_, __) => NameBox.Focus();
        }

        private static void SelectByTag(ComboBox box, string tag)
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
            {
                if ((item.Tag as string) == tag) { box.SelectedItem = item; return; }
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private static int SelectedTag(ComboBox box, int fallback)
        {
            if (box.SelectedItem is ComboBoxItem it && it.Tag is string s && int.TryParse(s, out int v))
                return v;
            return fallback;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _account.DisplayName = (NameBox.Text ?? "").Trim();
            _account.RdpHost = (HostBox.Text ?? "").Trim();

            if (int.TryParse((PortBox.Text ?? "").Trim(), out int port) && port > 0 && port <= 65535)
                _account.RdpPort = port;
            else _account.RdpPort = 3389;

            string user = (UserBox.Text ?? "").Trim();
            _account.RdpUserName = user.Length == 0 ? _account.UserName : user;

            if (int.TryParse((WidthBox.Text ?? "").Trim(), out int w) && w >= 200 && w <= 8192)
                _account.RdpWidth = w;
            else _account.RdpWidth = 1280;

            if (int.TryParse((HeightBox.Text ?? "").Trim(), out int h) && h >= 200 && h <= 8192)
                _account.RdpHeight = h;
            else _account.RdpHeight = 720;

            _account.RdpScaleMode = SelectedTag(ScaleCombo, 0);
            _account.RdpColorDepth = SelectedTag(DepthCombo, 32);
            _account.RdpAutoDisconnect = AutoDiscCheck.IsChecked == true;
            _account.RdpLowBandwidth = LowBwCheck.IsChecked == true;

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void DragArea_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }
    }
}
