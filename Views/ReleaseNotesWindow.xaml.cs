using System;
using System.Windows;
using System.Windows.Input;

namespace SoulLink.Views
{
    public partial class ReleaseNotesWindow : Window
    {
        private ReleaseNotesWindow()
        {
            InitializeComponent();
            Height = Math.Clamp(SystemParameters.WorkArea.Height - 32, MinHeight, Height);
            HistoryViewer.Document = ReleaseNotesDocument.Render(ReleaseNotesDocument.ReadHistory(), "暂无更新记录。");
            Loaded += (_, _) => HistoryViewer.Focus();
        }

        internal static void ShowFor(Window owner)
        {
            new ReleaseNotesWindow { Owner = owner }.ShowDialog();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
