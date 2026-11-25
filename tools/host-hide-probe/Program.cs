using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Threading;

public static class Program
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    const int SW_HIDE = 0;
    const int SW_SHOWNOACTIVATE = 4;

    [STAThread]
    public static int Main()
    {
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
        int code = 0;

        app.Startup += (_, __) =>
        {
            var child = new System.Windows.Forms.Panel { BackColor = System.Drawing.Color.DarkRed };
            var host = new WindowsFormsHost();
            host.Child = child;

            var root = new Grid();
            root.Children.Add(host);

            var win = new System.Windows.Window
            {
                Title = "HostHideProbe", Width = 500, Height = 360,
                Content = root, WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            win.Show();

            // 等布局与句柄就绪
            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                IntPtr hostHwnd = IntPtr.Zero;
                try { hostHwnd = host.Handle; } catch { }

                Console.WriteLine("host.Handle       = " + hostHwnd);
                Console.WriteLine("control.Handle    = " + child.Handle);

                bool v1 = IsWindowVisible(hostHwnd);
                Console.WriteLine("[1] 初始 host 可见        = " + v1);

                // WPF 折叠：原生窗口应该**不受影响**
                host.Visibility = Visibility.Collapsed;
                win.UpdateLayout();
                System.Threading.Thread.Sleep(200);
                bool v2 = IsWindowVisible(hostHwnd);
                Console.WriteLine("[2] WPF Collapsed 后可见  = " + v2 + "  (期望 True = WPF 管不到它)");

                host.Visibility = Visibility.Visible;
                win.UpdateLayout();
                System.Threading.Thread.Sleep(120);

                // 我们修复用的手段
                ShowWindow(hostHwnd, SW_HIDE);
                System.Threading.Thread.Sleep(200);
                bool v3 = IsWindowVisible(hostHwnd);
                Console.WriteLine("[3] SW_HIDE 后可见        = " + v3 + "  (期望 False)");

                ShowWindow(hostHwnd, SW_SHOWNOACTIVATE);
                System.Threading.Thread.Sleep(200);
                bool v4 = IsWindowVisible(hostHwnd);
                Console.WriteLine("[4] SW_SHOWNOACTIVATE 后  = " + v4 + "  (期望 True)");

                // 焦点是否被抢
                Console.WriteLine("[5] 恢复后窗口 IsActive   = " + win.IsActive);

                bool pass = v1 && v2 && !v3 && v4;
                Console.WriteLine(pass ? "RESULT=PASS" : "RESULT=FAIL");
                code = pass ? 0 : 1;

                win.Close();
            }), DispatcherPriority.Loaded);
        };

        app.Run();
        return code;
    }
}
