using System.Linq;
using System.Windows;
using SoulLink.Views;

namespace SoulLink.Services
{
    public static class Dialogs
    {
        public static bool Confirm(string message, string title = "SoulLink", bool danger = false)
        {
            var dialog = new AppDialog(title, message, true, danger)
            {
                Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                        ?? Application.Current?.MainWindow
            };
            return dialog.ShowDialog() == true;
        }

        public static void Info(string message, string title = "SoulLink")
        {
            var dialog = new AppDialog(title, message)
            {
                Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                        ?? Application.Current?.MainWindow
            };
            dialog.ShowDialog();
        }
    }
}