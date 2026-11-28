using System.Windows;
using SoulLink.Controls;

namespace SoulLink.Views
{
    /// <summary>
    /// 主题风格的消息框，替代系统 MessageBox（系统自带的是白色老式窗口，和整体风格不符）。
    ///
    /// 用法与 MessageBox 类似：
    ///   ThemedMessage.Info(owner, "标题", "内容");
    ///   ThemedMessage.OkAsync(...)
    /// </summary>
    public partial class ThemedMessageWindow : Window
    {
        public ThemedMessageWindow(string title, string body, bool showCancel = false,
                                   bool isError = false)
        {
            InitializeComponent();

            TitleText.Text = title;
            BodyText.Text = body ?? "";

            if (isError)
            {
                IconText.Text = "\uEA39";
                IconText.Foreground = UiBrush.Get("RoseBrush");
            }

            if (showCancel)
                BtnCancel.Visibility = Visibility.Visible;

            Loaded += (_, __) =>
            {
                // 长文本时窗口不要超出屏幕
                if (Height > SystemParameters.WorkArea.Height - 40)
                    Height = SystemParameters.WorkArea.Height - 40;
            };
        }

        internal MessageBoxResult Choice { get; private set; } = MessageBoxResult.Cancel;

        internal void ShowSaveOptions()
        {
            BtnNo.Visibility = Visibility.Visible;
            BtnCancel.Visibility = Visibility.Visible;
            BtnCancel.IsCancel = true;
            OkLabel.Text = "保存";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Choice = MessageBoxResult.Yes;
            DialogResult = true;
        }

        private void No_Click(object sender, RoutedEventArgs e)
        {
            Choice = MessageBoxResult.No;
            DialogResult = false;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }

    /// <summary>主题消息框的便捷入口。</summary>
    public static class ThemedMessage
    {
        /// <summary>提示（只有一个确定）。</summary>
        public static void Info(Window? owner, string title, string body)
        {
            var w = new ThemedMessageWindow(title, body) { Owner = owner };
            w.ShowDialog();
        }

        /// <summary>错误（红色图标）。</summary>
        public static void Error(Window? owner, string title, string body)
        {
            var w = new ThemedMessageWindow(title, body, false, true) { Owner = owner };
            w.ShowDialog();
        }

        /// <summary>是否确认（返回 true = 用户点了确定）。</summary>
        public static bool Confirm(Window? owner, string title, string body, bool danger = false)
        {
            var w = new ThemedMessageWindow(title, body, true, danger) { Owner = owner };
            return w.ShowDialog() == true;
        }

        /// <summary>
        /// 是/否/取消 三选。
        /// 保存、不保存和取消是独立选择。关闭对话框等同取消。
        /// </summary>
        public static System.Windows.MessageBoxResult YesNoCancel(
            Window? owner, string title, string body)
        {
            var w = new ThemedMessageWindow(title, body) { Owner = owner };
            w.ShowSaveOptions();
            w.ShowDialog();
            return w.Choice;
        }
    }
}
