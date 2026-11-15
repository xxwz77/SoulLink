using System.Windows;
using System.Windows.Input;

namespace SoulLink.Views
{
    /// <summary>与深色玻璃主题一致的注销确认框，默认操作始终是取消。</summary>
    public partial class LogoffConfirmWindow : Window
    {
        public LogoffConfirmWindow(string caption, string userName, int sessionId)
        {
            InitializeComponent();
            AccountText.Text = caption + "  ·  " + userName;
            SessionText.Text = "会话 #" + sessionId + " 中的游戏、脚本及其他程序都会退出。";
            Loaded += (_, __) => CancelButton.Focus();
        }

        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}