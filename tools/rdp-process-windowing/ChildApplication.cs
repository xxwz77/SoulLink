using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SoulLink.WindowingTest
{
    internal sealed class ChildApplication
    {
        private readonly string _id;
        private readonly string _caption;
        private readonly Color _color;
        private readonly Window _window;
        private readonly NamedPipeServerStream _pipe;
        private readonly Dispatcher _dispatcher;
        private bool _closing;
        private readonly SemaphoreSlim _writeLock=new(1,1);

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd,int index,IntPtr value);
        [DllImport("user32.dll", EntryPoint="SetWindowLongW")] private static extern int SetWindowLong32(IntPtr hwnd,int index,int value);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd,int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd,int index,int value);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectNative rect);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd,int command);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }

        public ChildApplication(string id, string caption, Color color)
        {
            _id = id;
            _caption = caption;
            _color = color;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _pipe = JsonPipe.CreateServer("SoulLink.WindowingTest." + id);
            _window = new Window
            {
                Title = caption + " · 独立连接窗口",
                Width = 760, Height = 480, MinWidth = 280, MinHeight = 180,
                Left = 160 + int.Parse(id.TrimStart('M')) * 24,
                Top = 120 + int.Parse(id.TrimStart('M')) * 20,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(color),
                Content = BuildContent()
            };
            _window.SourceInitialized += (_, __) =>
            {
                var hwnd = new WindowInteropHelper(_window).Handle;
                _ = SendAsync(new WindowMessage { Type = "ready", Id = _id, Hwnd = hwnd.ToInt64() });
            };
            _window.LocationChanged += (_, __) => ReportBounds();
            _window.SizeChanged += (_, __) => ReportBounds();
            _window.Closing += (_, e) => { if (!_closing) e.Cancel = true; };
        }

        private UIElement BuildContent()
        {
            var grid = new Grid { Background = new SolidColorBrush(_color) };
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock
            {
                Text = _caption, FontSize = 32, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = "独立子进程 · 固定顶级窗口 · 无 ActiveX", FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromArgb(210,255,255,255)),
                Margin = new Thickness(0,14,0,0), HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = "PID " + Environment.ProcessId + "  |  此窗口从创建到退出不更换宿主",
                FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(180,255,255,255)),
                Margin = new Thickness(0,10,0,0), HorizontalAlignment = HorizontalAlignment.Center
            });
            grid.Children.Add(panel);
            return grid;
        }

        public async Task RunAsync()
        {
            Trace.Write(_id,"window shown");
            _window.Show();
            var connect = _pipe.WaitForConnectionAsync();
            var pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            pump.Tick += (_, __) => { };
            pump.Start();
            await connect; Trace.Write(_id,"controller connected");
            await SendAsync(new WindowMessage { Type = "ready", Id = _id, Hwnd = new WindowInteropHelper(_window).Handle.ToInt64() }); Trace.Write(_id,"ready sent");
            while (!_closing && _pipe.IsConnected)
            {
                var message = await JsonPipe.ReadAsync(_pipe, CancellationToken.None);
                if (message == null) break; Trace.Write(_id,"received "+message.Type);
                await _dispatcher.InvokeAsync(() => Apply(message)); Trace.Write(_id,"applied "+message.Type);
            }
            _dispatcher.Invoke(() => { _closing = true; _window.Close(); });
        }

        private void Apply(WindowMessage message)
        {
            if (_closing) return;
            var hwnd = new WindowInteropHelper(_window).Handle;
            switch (message.Type)
            {
                case "set-owner":
                    var owner = new IntPtr(message.OwnerHwnd);
                    if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd,-8,owner); else SetWindowLong32(hwnd,-8,owner.ToInt32());
                    int ex = GetWindowLong(hwnd,-20);
                    SetWindowLong(hwnd,-20,(ex | 0x80) & ~0x40000); // TOOLWINDOW, not APPWINDOW
                    ReportBounds();
                    break;
                case "set-bounds":
                    _window.WindowStartupLocation = WindowStartupLocation.Manual;
                    var source = HwndSource.FromHwnd(hwnd);
                    double sx = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
                    double sy = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
                    _window.Left = message.X * sx;
                    _window.Top = message.Y * sy;
                    _window.Width = Math.Max(_window.MinWidth, message.Width * sx);
                    _window.Height = Math.Max(_window.MinHeight, message.Height * sy);
                    break;
                case "set-style":
                    _window.WindowStyle = message.Text == "embedded" ? WindowStyle.None : WindowStyle.SingleBorderWindow;
                    _window.ResizeMode = message.Text == "embedded" ? ResizeMode.NoResize : ResizeMode.CanResize;
                    break;
                case "show":
                    if (message.Visible)
                    {
                        if (_window.WindowState == WindowState.Minimized) ShowWindow(hwnd,9);
                        ShowWindow(hwnd,4);
                        if (message.Activate) SetForegroundWindow(hwnd);
                    }
                    else ShowWindow(hwnd,0);
                    break;
                case "report":
                    ReportBounds();
                    break;
                case "close":
                    _closing = true;
                    _window.Close();
                    break;
            }
        }

        private void ReportBounds()
        {
            if (_closing) return;
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
            _ = SendAsync(new WindowMessage
            {
                Type = "bounds", Id = _id, Hwnd = hwnd.ToInt64(),
                X = r.Left, Y = r.Top, Width = r.Right-r.Left, Height = r.Bottom-r.Top, Visible = IsWindowVisible(hwnd)
            });
        }

        private async Task SendAsync(WindowMessage message)
        {
            if(!_pipe.IsConnected)return;
            await _writeLock.WaitAsync();
            try { if (_pipe.IsConnected) await JsonPipe.WriteAsync(_pipe, message, CancellationToken.None); }
            catch { }
            finally{_writeLock.Release();}
        }
    }
}
