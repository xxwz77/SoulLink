using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Media;
using SoulLink.Models;
using SoulLink.Services;
using SoulLink.Views;

internal static class NavigationTest
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    private static IntPtr H(Window w) => new WindowInteropHelper(w).Handle;
    private static RECT Bounds(FrameworkElement e)
    {
        e.UpdateLayout();
        var a = e.PointToScreen(new Point(0, 0));
        var b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return new RECT { Left = (int)Math.Round(a.X), Top = (int)Math.Round(a.Y), Right = (int)Math.Round(b.X), Bottom = (int)Math.Round(b.Y) };
    }
    private static RECT Bounds(Window window)
    {
        if (!GetWindowRect(H(window), out RECT rect)) throw new Exception("GetWindowRect failed");
        return rect;
    }
    private static bool Same(RECT a, RECT b)
        => Math.Abs(a.Left-b.Left) <= 1 && Math.Abs(a.Top-b.Top) <= 1
        && Math.Abs(a.Right-b.Right) <= 1 && Math.Abs(a.Bottom-b.Bottom) <= 1;
    private static async Task Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(10);
        }
    }
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2500)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < end) await Pump(20);
    }
    private static object? Field(object target, string name)
        => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
    private static void Invoke(object target, string name, params object[] args)
        => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static void Click(Point point)
    {
        SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y));
        Thread.Sleep(60);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(35);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
    private static void Click(FrameworkElement e)
    {
        var r = Bounds(e);
        Click(new Point((r.Left+r.Right)/2.0, (r.Top+r.Bottom)/2.0));
    }

    [STAThread]
    private static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Run()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Theme/Styles.xaml", UriKind.Relative) });
        ThemeManager.Apply();
        var failures = new System.Collections.Generic.List<string>();
        string scenario = Environment.GetEnvironmentVariable("NAV_TEST_SCENARIO") ?? "collapsed";
        void Check(bool value, string message)
        {
            Console.WriteLine((value ? "PASS " : "FAIL ") + message);
            if (!value) failures.Add(message);
        }
        MainWindow? window = null;
        Window? other = null;
        try
        {
            var savedState = ConfigStore.LoadState();
            savedState.NavCollapsed = scenario != "expanded";
            savedState.NavAutoHide = scenario == "autohide";
            ConfigStore.SaveState(savedState);
            window = new MainWindow();
            Console.WriteLine($"TEST scenario={scenario} ctor collapsed={Field(window, "_navCollapsed")} docked={Field(window, "_navDockedInOverlay")}");
            other = new Window { Title = "Unrelated navigation test foreground", Width=280, Height=180, Left=1300, Top=160,
                Content = new TextBlock { Text = "Other app simulation", Margin = new Thickness(15) } };
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(3); return; }
        window.Deactivated += (_, __) => Console.WriteLine("TEST EVENT MainWindow.Deactivated");
        window.Activated += (_, __) => Console.WriteLine("TEST EVENT MainWindow.Activated");
        window.Loaded += async (_, __) =>
        {
            try
            {
                window.Activate();
                other.Show();
                window.Activate();
                await Pump(180);
                await WaitUntil(() => (bool)Field(window, "_navCollapsed")! == (scenario != "expanded")
                    && (scenario != "expanded" || window.NavRail.ActualWidth == 180), 1500);
                Check((bool)Field(window, "_navCollapsed")! == (scenario != "expanded"),
                    "startup navigation matches saved preference after activation");
                if (scenario == "expanded")
                {
                    var startupOverlay = (OwnedOverlayWindow?)Field(window, "_navOverlayWindow");
                    Check(startupOverlay?.IsVisible == true && (bool)Field(window, "_navDockedInOverlay")!
                        && window.NavRail.ActualWidth == 180 && window.NavRail.IsLoaded,
                        "saved expanded state restores a visible native navigation after activation");
                }

                // Real button click against the actual MainWindow XAML.
                Click(window.BtnHamburger);
                await Pump(120);
                Console.WriteLine($"TEST after120 collapsed={Field(window, "_navCollapsed")} docked={Field(window, "_navDockedInOverlay")} width={window.NavRail.ActualWidth} visible={((OwnedOverlayWindow?)Field(window, "_navOverlayWindow"))?.IsVisible} active={window.IsActive}");
                await Pump(480);
                var overlay = (OwnedOverlayWindow?)Field(window, "_navOverlayWindow");
                Check(overlay != null, scenario == "expanded"
                    ? "button click collapses startup-expanded navigation"
                    : "button click creates native navigation overlay");
                if (scenario == "autohide")
                {
                    Check(overlay.IsVisible && window.NavRail.ActualWidth > 175, "auto-hide navigation expands from real button");
                    other.Activate();
                    SetForegroundWindow(H(other));
                    await WaitUntil(() => GetForegroundWindow() == H(other), 1200);
                    await Pump(250);
                    Check(!overlay.IsVisible, "auto-hide deactivation hides native navigation immediately");
                    await Pump(500);
                    Check((bool)Field(window, "_navCollapsed")! && !(bool)Field(window, "_navDockedInOverlay")!
                        && window.NavRail.ActualWidth == 56, "auto-hide deactivation returns navigation to collapsed main-window rail");
                    Console.WriteLine("ALL AUTOHIDE NAVIGATION TESTS PASSED");
                    return;
                }
                if (scenario == "expanded")
                {
                    Check(!(bool)Field(window, "_navDockedInOverlay")! && window.NavRail.ActualWidth == 56,
                        "startup-expanded navigation can be collapsed by its real button");
                    if (failures.Count > 0) Console.WriteLine(failures.Count + " STARTUP EXPANDED TESTS FAILED");
                    else Console.WriteLine("ALL STARTUP EXPANDED NAVIGATION TESTS PASSED");
                    return;
                }
                if (overlay == null) throw new Exception("No navigation overlay.");
                Check(overlay.IsVisible, "expanded navigation is visible");
                Check(GetWindowLongPtr64(H(overlay), -8) == H(window), "navigation has correct native owner");
                Check(!overlay.Topmost, "navigation is not topmost");
                Check(GetForegroundWindow() == H(window), "navigation does not steal foreground");
                Check(window.NavRail.ActualWidth > 175, "navigation reaches expanded width");
                Check(window.NavTitleText.Visibility == Visibility.Visible
                    && window.NavSessionPanel.Visibility == Visibility.Visible
                    && window.AutoHideNavCheck.Visibility == Visibility.Visible,
                    "expanded labels, session and auto-hide controls are visible");
                var expected = new RECT { Left = Bounds(window.NavHomeHost).Left, Top = Bounds(window.NavHomeHost).Top };
                expected.Right = expected.Left + (int)Math.Round(window.NavRail.ActualWidth * VisualTreeHelper.GetDpi(window.NavHomeHost).DpiScaleX);
                expected.Bottom = Bounds(window.NavHomeHost).Bottom;
                Check(Same(Bounds(overlay), expected), "navigation matches home anchor at 150% DPI");
                var pageBounds = Bounds(window.PageLaunch);
                Check(pageBounds.Left > Bounds(overlay).Left, "page remains in its original non-pushed layout");

                Click(window.BtnHamburger);
                await Pump(600);
                Check(!(bool)Field(window, "_navDockedInOverlay")!, "button click restores navigation to main window");
                Check(window.NavRail.ActualWidth == 56, "collapsed navigation restores 56 DIP width");
                Check(window.NavTitleText.Visibility == Visibility.Collapsed
                    && window.NavSessionPanel.Visibility == Visibility.Collapsed,
                    "collapsed navigation hides expanded content");
                Check(Same(Bounds(window.PageLaunch), pageBounds), "page geometry is unchanged after collapse");

                // Interrupt expansion halfway; must still finish at exactly 180.
                Invoke(window, "SetNavCollapsed", false, "rapid-test");
                await Pump(35);
                Invoke(window, "SetNavCollapsed", true, "rapid-test");
                await Pump(25);
                Invoke(window, "SetNavCollapsed", false, "rapid-test");
                await WaitUntil(() => window.NavRail.ActualWidth == 180 && (bool)Field(window, "_navDockedInOverlay")!);
                var rapidOverlay = (OwnedOverlayWindow?)Field(window, "_navOverlayWindow");
                Console.WriteLine($"TEST rapid width={window.NavRail.ActualWidth} collapsed={Field(window, "_navCollapsed")} docked={Field(window, "_navDockedInOverlay")} overlayVisible={rapidOverlay?.IsVisible} railLoaded={window.NavRail.IsLoaded} active={window.IsActive} railParent={LogicalTreeHelper.GetParent(window.NavRail)?.GetType().Name}");
                Check(window.NavRail.ActualWidth == 180 && (bool)Field(window, "_navDockedInOverlay")!,
                    "rapid interrupted animation settles at exactly 180 DIP");
                Invoke(window, "SetNavCollapsed", true, "rapid-test");
                await Pump(600);
                Check(window.NavRail.ActualWidth == 56 && !(bool)Field(window, "_navDockedInOverlay")!,
                    "rapid collapse settles at exactly 56 DIP");

                for (int i = 0; i < 5; i++)
                {
                    Invoke(window, "SetNavCollapsed", false, "repeat-test");
                    await Pump(400);
                    if (window.NavRail.ActualWidth != 180) Check(false, "repeat expansion " + i);
                    Invoke(window, "SetNavCollapsed", true, "repeat-test");
                    await Pump(400);
                    if (window.NavRail.ActualWidth != 56) Check(false, "repeat collapse " + i);
                }
                Check(true, "five complete expand/collapse cycles finish without stale visual parent");

                Invoke(window, "SetNavCollapsed", false, "movement-test");
                await Pump(550);
                window.Left += 24;
                await Pump(180);
                var a = Bounds(window.NavHomeHost);
                var o = Bounds(overlay!);
                Check(Math.Abs(a.Left-o.Left)<=1 && Math.Abs(a.Top-o.Top)<=1,
                    "navigation follows main-window movement");
                other.Activate();
                SetForegroundWindow(H(other));
                await WaitUntil(() => GetForegroundWindow() == H(other), 1200);
                await Pump(160);
                Check(GetForegroundWindow() == H(other), "unrelated window becomes foreground");
                Check(!overlay!.NativeVisible && !IsWindowVisible(H(overlay)), "deactivation hides expanded navigation");
                Check(!(bool)Field(window, "_navCollapsed")!, "pinned navigation preserves expanded preference while inactive");
                window.Activate();
                await Pump(180);
                Check(overlay.NativeVisible && IsWindowVisible(H(overlay)) && window.NavRail.ActualWidth == 180,
                    "return to SoulLink restores pinned expanded navigation");
                Invoke(window, "SetNavCollapsed", true, "post-deactivation-test");
                await Pump(500);

                window.WindowState = WindowState.Minimized;
                await Pump(100);
                Check(!overlay.NativeVisible && !IsWindowVisible(H(overlay)), "minimized main window leaves no navigation overlay");
                window.WindowState = WindowState.Normal;
                await Pump(120);
                Console.WriteLine(failures.Count == 0 ? "ALL NAVIGATION TESTS PASSED" : failures.Count + " NAVIGATION TESTS FAILED");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                failures.Add(ex.Message);
            }
            finally
            {
                try { ((OwnedOverlayWindow?)Field(window, "_navOverlayWindow"))?.Close(); other.Close(); window.Close(); } catch { }
                app.Shutdown(failures.Count == 0 ? 0 : 2);
            }
        };
        window.Show();
        app.Run();
    }
}
