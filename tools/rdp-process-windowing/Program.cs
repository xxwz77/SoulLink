using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace SoulLink.WindowingTest
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--child")
            {
                return RunChild(args[1]);
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new MainWindow();
            window.Show();
            app.Run();
            return 0;
        }

        private static int RunChild(string id)
        {
            int number = int.Parse(id.TrimStart('M'));
            var colors = new[]
            {
                Color.FromRgb(32,80,120), Color.FromRgb(52,100,82), Color.FromRgb(102,65,110),
                Color.FromRgb(100,75,40), Color.FromRgb(35,90,100), Color.FromRgb(90,55,65),
                Color.FromRgb(45,70,110), Color.FromRgb(75,85,45)
            };
            var app = new Application { ShutdownMode = ShutdownMode.OnLastWindowClose };
            var child = new ChildApplication(id, "账号 " + number + " · 模拟", colors[(number-1)%colors.Length]);
            _ = child.RunAsync();
            app.Run();
            return 0;
        }
    }
}
