using System;
using System.Windows;
using SoulLink.Services;

namespace SoulLink.Views
{
    public partial class ChangePasswordWindow : Window
    {
        private readonly string _user;
        public string? Password { get; private set; }
        public bool NeverExpire => NeverExpireCheck.IsChecked == true;
        public ChangePasswordWindow(string user, bool neverExpire)
        {
            InitializeComponent(); _user = user;
            TitleText.Text = "修改密码 · " + user;
            NeverExpireCheck.IsChecked = neverExpire;
            Loaded += (_,__) => PwdBox.Focus();
        }
        private void Input_Changed(object sender, RoutedEventArgs e)
        {
            string password = PwdBox.Password ?? "";
            string error = password.Length == 0 ? "" : LocalUserService.ValidatePassword(password, _user) ?? "";
            if (error.Length == 0 && Pwd2Box.Password.Length > 0 && password != Pwd2Box.Password) error = "两次输入的密码不一致";
            ErrorText.Text = error;
            ConfirmButton.IsEnabled = password.Length > 0 && password == Pwd2Box.Password && error.Length == 0;
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (PwdBox.Password.Length == 0 || PwdBox.Password != Pwd2Box.Password) return;
            string error = LocalUserService.ValidatePassword(PwdBox.Password, _user) ?? "";
            if (error.Length > 0) { ErrorText.Text = error; return; }
            Password = PwdBox.Password; DialogResult = true;
        }
    }
}