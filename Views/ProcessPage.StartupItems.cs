using System.Windows;

namespace SoulLink.Views
{
    public partial class ProcessPage
    {
        private void InventorySection_Changed(object sender, RoutedEventArgs e)
        {
            if (DefenderScroll == null) return;
            bool showProcesses = ProcessTab.IsChecked == true;
            PageScroll.Visibility = showProcesses ? Visibility.Visible : Visibility.Collapsed;
            SubText.Visibility = PageScroll.Visibility;
            StartupView.Visibility = StartupTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            SystemScroll.Visibility = SystemTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            DefenderScroll.Visibility = DefenderTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Activate();
        }
    }
}
