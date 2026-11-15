using System.Windows;
using System.Windows.Input;

namespace SoulLink.Views
{
    public partial class PasswordWindow : Window
    {
        public string Password { get; private set; } = "";

        public PasswordWindow(string userName)
        {
            InitializeComponent();
            SubText.Text = "用于以 " + userName + " 身份进入其桌面会话";
            Loaded += (_, __) => PwdBox.Focus();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
        }

        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void Show_Changed(object sender, RoutedEventArgs e)
        {
            if (ShowCheck.IsChecked == true)
            {
                PlainBox.Text = PwdBox.Password;
                PlainBox.Visibility = Visibility.Visible;
                PwdBox.Visibility = Visibility.Collapsed;
                PlainBox.Focus();
            }
            else
            {
                PwdBox.Password = PlainBox.Text;
                PlainBox.Visibility = Visibility.Collapsed;
                PwdBox.Visibility = Visibility.Visible;
                PwdBox.Focus();
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Password = ShowCheck.IsChecked == true ? PlainBox.Text : PwdBox.Password;
            if (string.IsNullOrEmpty(Password))
            {
                SubText.Text = "⚠ 密码不能为空";
                return;
            }
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
