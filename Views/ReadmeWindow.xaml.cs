using System.Windows;
using System.Windows.Input;

namespace SoulLink.Views
{
    public partial class ReadmeWindow : Window
    {
        public ReadmeWindow()
        {
            InitializeComponent();
        }

        public static void ShowFor(Window? owner)
        {
            var w = new ReadmeWindow();
            if (owner != null && owner.IsVisible) w.Owner = owner;
            w.ShowDialog();
        }

        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
