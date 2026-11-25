using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;

public static class Program
{
    [DllImport("user32.dll")] static extern bool EnableWindow(IntPtr h, bool e);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);

    static readonly System.Text.StringBuilder Log = new System.Text.StringBuilder();
    static void W(string s) { Log.AppendLine(s); File.WriteAllText("probe-result.txt", Log.ToString()); }

    [STAThread]
    public static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, __) =>
        {
            var host = new WindowsFormsHost();
            host.Child = new System.Windows.Forms.Panel();
            int borderUp = 0, windowUp = 0;
            var border = new Border { Background = Brushes.Red };
            border.MouseLeftButtonUp += (_, e) => { borderUp++; W("Border MouseUp"); };

            var grid = new Grid();
            grid.Children.Add(host);
            grid.Children.Add(border);

            var win = new Window
            {
                Width = 500, Height = 400, Left = 150, Top = 150,
                Content = grid, WindowStartupLocation = WindowStartupLocation.Manual,
                Topmost = true, ShowInTaskbar = false,
            };
            win.PreviewMouseUp += (_, e) => { windowUp++; W("Window PreviewMouseUp"); };
            win.Show();
            win.UpdateLayout();

            win.Loaded += async (_, __) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                SetForegroundWindow(hwnd);
                win.Activate();
                await System.Threading.Tasks.Task.Delay(500);

                int cx = (int)(win.Left + 200), cy = (int)(win.Top + 200);
                SetCursorPos(cx, cy);
                await System.Threading.Tasks.Task.Delay(300);

                W("=== host enabled=FALSE 时点击 ===");
                EnableWindow(host.Handle, false);
                await System.Threading.Tasks.Task.Delay(300);
                borderUp = 0; windowUp = 0;
                mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
                await System.Threading.Tasks.Task.Delay(60);
                mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
                await System.Threading.Tasks.Task.Delay(600);
                W("Border=" + borderUp + "  Window=" + windowUp);
                W(borderUp > 0 ? "RESULT=PASS 点击能到达WPF" : "RESULT=FAIL 点击到不了WPF");

                win.Close();
                app.Shutdown();
            };
        };
        app.Run();
        return 0;
    }
}
