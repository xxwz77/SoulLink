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
            // ★ 3.1.11：按钮被挡住时点了，就把原因写到提示框里。
            BtnCreate.BlockedFeedback = hint => SetHint(hint, flash: true);
            Loaded += (_, __) =>
            {
                NextHostText.Text = "下一个可用地址：" + AccountProvisioner.NextLoopbackHost(App.Config.Accounts);
                // ★ 必须在这里先校验一次：以前只在字段变动时才跑，
                //   窗口刚打开时按钮其实是"可点"的，点下去 Validate() 返回 false
                //   然后 BtnCreate_Click 直接 return —— 用户看到的就是"点了没反应"。
                Validate();
                UserBox.Focus();
            };
        }

        /// <summary>把一句提示写进校验框；flash=true 时闪一下，让用户注意到。</summary>
        private void SetHint(string text, bool flash = false)
        {
            ValidBox.Visibility = Visibility.Visible;
            ValidText.Text = text;
            if (!flash) return;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(650))
            {
                AutoReverse = true,
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase()
            };
            ValidBox.BeginAnimation(OpacityProperty, fade);
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

            bool ready = user.Length > 0 && pwd.Length > 0 && pwd == pwd2 && err == null;

            // ★ 3.1.11：提示框常驻，不再"没错误就整块消失"——
            //   空表单时告诉用户还差什么，比让他对着灰按钮猜要好。
            SetHint(err ?? (ready ? "" : MissingHint(user, pwd, pwd2)));

            BtnCreate.IsBlocked = !ready;
            BtnCreate.BlockedHint = err ?? MissingHint(user, pwd, pwd2);
            return ready;
        }

        /// <summary>列出还差哪几项（用户点不可用按钮时看到的就是这句）。</summary>
        private static string MissingHint(string user, string pwd, string pwd2)
        {
            var missing = new System.Collections.Generic.List<string>();
            if (user.Length == 0) missing.Add("Windows 用户名");
            if (pwd.Length == 0) missing.Add("密码");
            else if (pwd2.Length == 0) missing.Add("再次输入密码");
            return missing.Count == 0 ? "表单还没填完" : "还差：" + string.Join("、", missing);
        }

        // ── 创建 ─────────────────────────────────────────────────────────

        private async void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            if (!Validate()) return;

            string user = UserBox.Text.Trim();
            string pwd = PwdBox.Password;
            string nick = NickBox.Text?.Trim() ?? "";

            // ★ 3.1.11：创建过程用 IsBlocked 挡重复点击，不用 IsEnabled=false。
            BtnCreate.IsBlocked = true;
            BtnCreate.BlockedHint = "正在创建，请稍候…";
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
                Validate();     // 用表单真实状态决定是否仍然 IsBlocked
            }
        }
    }
}
