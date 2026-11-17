using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 新建用户对话框：一次完成「建 Windows 账户 + 入组 + 生成 SoulLink 连接配置」。
    ///
    /// ★ 昵称与用户名严格区分（用户明确要求）：
    ///   昵称 = SoulLink 里的显示名，可以是中文；
    ///   用户名 = 真实 Windows 账户名，必须是合法的本机账户名。
    /// </summary>
    public partial class NewUserWindow : Window
    {
        /// <summary>创建成功的账户（失败为 null）。</summary>
        public LocalUserInfo? Result { get; private set; }

        /// <summary>同时生成的 SoulLink 连接配置（未勾选则为 null）。</summary>
        public Account? SavedAccount { get; private set; }

        public NewUserWindow()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                NextHostText.Text = "下一个可用地址：" + AccountProvisioner.NextLoopbackHost(App.Config.Accounts);
                UserBox.Focus();
            };
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // ── 实时校验 ─────────────────────────────────────────────────────

        private void Field_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Validate();

        private void Field_PasswordChanged(object sender, RoutedEventArgs e) => Validate();

        private bool Validate()
        {
            string user = UserBox.Text?.Trim() ?? "";
            string pwd = PwdBox.Password ?? "";
            string pwd2 = Pwd2Box.Password ?? "";

            string? err = null;

            if (user.Length > 0)
                err = LocalUserService.ValidateUserName(user);
            if (err == null && pwd.Length > 0)
                err = LocalUserService.ValidatePassword(pwd, user);
            if (err == null && pwd2.Length > 0 && pwd != pwd2)
                err = "两次输入的密码不一致";

            if (err != null)
            {
                ValidBox.Visibility = Visibility.Visible;
                ValidText.Text = err;
                if (BtnCreate != null) BtnCreate.IsEnabled = false;
                return false;
            }

            ValidBox.Visibility = Visibility.Collapsed;
            bool ready = user.Length > 0 && pwd.Length > 0 && pwd == pwd2;
            if (BtnCreate != null) BtnCreate.IsEnabled = ready;
            return ready;
        }

        // ── 创建 ─────────────────────────────────────────────────────────

        private async void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            if (!Validate()) return;

            string user = UserBox.Text.Trim();
            string pwd = PwdBox.Password;
            string nick = NickBox.Text?.Trim() ?? "";

            BtnCreate.IsEnabled = false;
            BtnCreate.Content = "创建中…";

            try
            {
                var spec = new LocalUserService.CreateSpec
                {
                    UserName = user,
                    Password = pwd,
                    AddToAdministrators = ChkAdmin.IsChecked == true,
                    AddToRemoteDesktopUsers = ChkRemote.IsChecked == true,
                    Hide = ChkHide.IsChecked == true,
                    PasswordNeverExpires = ChkNeverExpire.IsChecked == true
                };

                var r = await LocalUserService.CreateAsync(spec);

                if (!r.Ok)
                {
                    ValidBox.Visibility = Visibility.Visible;
                    ValidText.Text = r.Output;
                    return;
                }

                Result = new LocalUserInfo
                {
                    Name = user,
                    IsAdmin = spec.AddToAdministrators,
                    IsRemoteUser = spec.AddToRemoteDesktopUsers,
                    Hidden = spec.Hide,
                    PasswordNeverExpires = spec.PasswordNeverExpires
                };

                // 生成 SoulLink 连接配置
                if (ChkCreateConfig.IsChecked == true)
                {
                    var acc = AccountProvisioner.Create(user, pwd, nick, App.Config.Accounts);
                    AccountProvisioner.Persist(acc);
                    SavedAccount = acc;
                }

                DialogResult = true;
            }
            catch (Exception ex)
            {
                ValidBox.Visibility = Visibility.Visible;
                ValidText.Text = "创建失败：" + ex.Message;
            }
            finally
            {
                BtnCreate.Content = "创建";
                BtnCreate.IsEnabled = true;
            }
        }
    }
}
