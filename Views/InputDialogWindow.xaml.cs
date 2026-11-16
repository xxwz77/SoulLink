using System.Windows;
using System.Windows.Input;

namespace SoulLink.Views
{
    /// <summary>简单的文本输入对话框（用于添加小号名、房间名等）。</summary>
    public partial class InputDialogWindow : Window
    {
        public string ResultText { get; private set; } = "";

        public InputDialogWindow(string title, string hint, string initial = "")
        {
            InitializeComponent();
            TitleText.Text = title;
            HintText.Text = hint;
            HintText.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
            Input.Text = initial;
            Loaded += (_, __) => { Input.Focus(); Input.SelectAll(); };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            ResultText = Input.Text ?? "";
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { Ok_Click(sender!, e); e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        }
    }
}
