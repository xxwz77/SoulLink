using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SoulLink.Services;

namespace SoulLink.Controls { public static class RdpConsole { public static bool VerboseLog => false; } }

namespace SoulLink
{
    public partial class App : System.Windows.Application
    {
        public static void AppendErrorLog(string source, Exception? ex)
            => Console.Error.WriteLine(source + ": " + ex);
    }
}

internal static class OverlayTest
{
    private const int GwlExstyle = -20;
    private const int GwlHwndparent = -8;
    private const int WsExTopmost = 0x00000008;
    private const int WsExNoactivate = 0x08000000;
    private const uint GaParent = 1;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [STAThread]
    private static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Run()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var host = new WindowsFormsHost
        {
            Child = new System.Windows.Forms.Panel
            {
                BackColor = System.Drawing.Color.FromArgb(32, 62, 89)
            }
        };
        var anchor = new Grid { Margin = new Thickness(110, 90, 30, 30) };
        anchor.Children.Add(host);
        var root = new Grid { Background = Brushes.Black };
        root.Children.Add(anchor);
        var owner = new Window
        {
            Title = "Owned overlay isolated test",
            Width = 680, Height = 480,
            Left = 120, Top = 100,
            Content = root,
            Background = Brushes.Black,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        var other = new Window
        {
            Title = "Unrelated test foreground",
            Width = 320, Height = 220,
            Left = 820, Top = 180,
            Content = new TextBlock { Text = "Foreground test", Margin = new Thickness(20) },
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        OwnedOverlayWindow? overlay = null;
        bool firstClick = false;
        Border shield = new()
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
            Child = new TextBlock
            {
                Text = "LOCKED", Foreground = Brushes.White, FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        shield.MouseLeftButtonDown += (_, e) => { Console.WriteLine("TEST shield MouseDown"); e.Handled = true; };
        shield.MouseLeftButtonUp += (_, e) =>
        {
            Console.WriteLine("TEST shield MouseUp");
            e.Handled = true;
            firstClick = true;
            overlay?.HideOverlay();
        };

        var failures = new List<string>();
        void Check(bool condition, string message)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
            if (!condition) failures.Add(message);
        }
        IntPtr H(Window window) => new WindowInteropHelper(window).Handle;
        RECT AnchorRect(FrameworkElement element)
        {
            element.UpdateLayout();
            var p = element.PointToScreen(new Point(0, 0));
            var q = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            return new RECT
            {
                Left = (int)Math.Round(p.X), Top = (int)Math.Round(p.Y),
                Right = (int)Math.Round(q.X), Bottom = (int)Math.Round(q.Y)
            };
        }
        RECT Rect(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out RECT rect)) throw new System.ComponentModel.Win32Exception();
            return rect;
        }
        async Task Pump(int milliseconds)
        {
            var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < end)
            {
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                await Task.Delay(10);
            }
        }
        void ClickAt(IntPtr hwnd, int x, int y)
        {
            Check(SetCursorPos(x, y), "position test cursor");
            Thread.Sleep(80);
            var point = new POINT { X = x, Y = y };
            Console.WriteLine($"TEST click window={WindowFromPoint(point)} expected overlay={hwnd}");
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(35);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        }

        owner.Loaded += async (_, __) =>
        {
            try
            {
                owner.Activate();
                other.Show();
                owner.Activate();
                await Pump(100);
                owner.Focusable = false;
                var before = AnchorRect(anchor);
                overlay = new OwnedOverlayWindow(owner, anchor, shield, "Overlay isolated test");
                overlay.ShowOverlay(anchor.ActualWidth, anchor.ActualHeight);
                await Pump(200);

                IntPtr nativeOwner = GetAncestor(H(overlay), GaParent);
                int style = GetWindowLong(H(overlay), GwlExstyle);
                Check(GetWindowLongPtrRaw(H(overlay), GwlHwndparent) == H(owner), $"native owner is the SoulLink test window (overlay={H(overlay)}, style={GetWindowLong(H(overlay), -16):X8}, ownerField={GetWindowLong(H(overlay), GwlHwndparent)}, ancestor={nativeOwner}, expected={H(owner)}, ownerStyle={GetWindowLong(H(owner), -16):X8})");
                Check((style & WsExTopmost) == 0, "WS_EX_TOPMOST is absent");
                Check((style & WsExNoactivate) != 0, "WS_EX_NOACTIVATE is present");
                Check(GetForegroundWindow() == H(owner), "showing overlay does not steal foreground");
                var actual = Rect(H(overlay));
                Check(actual.Left == before.Left && actual.Top == before.Top
                    && actual.Right == before.Right && actual.Bottom == before.Bottom,
                    $"physical bounds match anchor: overlay={actual.Left},{actual.Top},{actual.Right},{actual.Bottom}; anchor={before.Left},{before.Top},{before.Right},{before.Bottom}");
                var dpi = VisualTreeHelper.GetDpi(anchor);
                Console.WriteLine($"DPI scale: {dpi.DpiScaleX:0.###} x {dpi.DpiScaleY:0.###}");

                other.Activate();
                await Pump(120);
                Check(GetForegroundWindow() == H(other), "unrelated test window is foreground");
                Check(!overlay.NativeVisible && !IsWindowVisible(H(overlay)), "owner deactivation hides overlay synchronously");

                owner.Activate();
                await Pump(120);
                Check(overlay.NativeVisible && IsWindowVisible(H(overlay)), "owner activation restores requested overlay");
                ClickAt(H(overlay), before.Left + 20, before.Top + 20);
                await Pump(100);
                Check(firstClick && !overlay.NativeVisible && !IsWindowVisible(H(overlay)), "complete first click unlocks only after MouseUp");
                Check(GetForegroundWindow() == H(owner), $"clicking nonactivating overlay preserves foreground (foreground={GetForegroundWindow()}, owner={H(owner)}, other={H(other)}, overlay={H(overlay)}, visible={overlay.IsVisible}, click={firstClick})");

                overlay.ShowOverlay(anchor.ActualWidth, anchor.ActualHeight);
                await Pump(80);
                owner.Left += 30;
                await Pump(150);
                var movedAnchor = AnchorRect(anchor);
                var movedOverlay = Rect(H(overlay));
                Check(movedAnchor.Left == movedOverlay.Left && movedAnchor.Top == movedOverlay.Top
                    && movedAnchor.Right == movedOverlay.Right && movedAnchor.Bottom == movedOverlay.Bottom,
                    $"overlay follows owner movement in physical pixels (overlay={movedOverlay.Left},{movedOverlay.Top},{movedOverlay.Right},{movedOverlay.Bottom}; anchor={movedAnchor.Left},{movedAnchor.Top},{movedAnchor.Right},{movedAnchor.Bottom})");

                overlay.HideOverlay();
                var inactive = new OwnedOverlayWindow(owner, anchor, shield, "Inactive owner test", allowInactiveOwner: true);
                other.Activate();
                await Pump(150);
                for (int i = 0; i < 5; i++)
                {
                    inactive.ShowOverlay(anchor.ActualWidth, anchor.ActualHeight);
                    await Pump(100);
                }
                Check(inactive.IsRequested && inactive.NativeVisible && IsWindowVisible(H(inactive)),
                    $"inactive-owner overlay remains visible (requested={inactive.IsRequested}, nativeVisible={inactive.NativeVisible}, win32Visible={IsWindowVisible(H(inactive))}, ownerActive={owner.IsActive})");
                inactive.HideOverlay();
                inactive.Close();

                owner.WindowState = WindowState.Minimized;
                await Pump(100);
                Check(!overlay.NativeVisible && !IsWindowVisible(H(overlay)), "owner minimization hides overlay");
                owner.WindowState = WindowState.Normal;
                await Pump(100);
                overlay.HideOverlay();
                overlay.Close();
                other.Close();
                owner.Close();
                Console.WriteLine(failures.Count == 0 ? "ALL OVERLAY TESTS PASSED" : failures.Count + " TESTS FAILED");
                app.Shutdown(failures.Count == 0 ? 0 : 2);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                try { overlay?.Close(); other.Close(); owner.Close(); } catch { }
                app.Shutdown(3);
            }
        };
        owner.Show();
        app.Run();
    }

    private static IntPtr GetWindowLongPtrRaw(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong(hwnd, index);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
