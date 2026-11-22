using System.Windows;
using System.Windows.Controls;

namespace SoulLink.Views
{
    /// <summary>选择发车人数（2/3/4）。</summary>
    public partial class LobbyDepartSizeWindow : Window
    {
        /// <summary>选中的发车人数（2-4）。取消时返回 0。</summary>
        public int SelectedSize { get; private set; }

        public LobbyDepartSizeWindow(string title, string hint, int initial = 4)
        {
            InitializeComponent();
            TitleText.Text = title;
            if (!string.IsNullOrEmpty(hint)) HintText.Text = hint;

            int want = initial >= 2 && initial <= 4 ? initial : 4;
            foreach (var item in SizeCombo.Items)
                if (item is ComboBoxItem cbi && cbi.Tag is string t && t == want.ToString())
                { SizeCombo.SelectedItem = item; break; }
            if (SizeCombo.SelectedItem == null) SizeCombo.SelectedIndex = 2; // 4 人
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (SizeCombo.SelectedItem is ComboBoxItem cbi
                && cbi.Tag is string t && int.TryParse(t, out int n))
            {
                SelectedSize = n;
                DialogResult = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
