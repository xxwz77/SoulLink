using System.Windows;
using System.Windows.Input;

namespace SoulLink.Views
{
    /// <summary>应用统一的深色玻璃确认/信息框，不再暴露系统 MessageBox。</summary>
    public partial class AppDialog : Window
    {
        public AppDialog(string title, string message, bool confirmation = false, bool danger = false)
        {
            InitializeComponent();
            Title = title;
            TitleText.Text = title;
            MessageText.Text = message;
            CancelButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
            if (danger)
            {
                OkButton.Style = (Style)FindResource("DangerButton");
                OkButton.Content = "移除";
                IconText.Text = "\uE74D";
                IconText.Foreground = (System.Windows.Media.Brush)FindResource("RoseBrush");
            }
            Loaded += (_, __) => (confirmation ? CancelButton : OkButton).Focus();
        }

        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }
        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}