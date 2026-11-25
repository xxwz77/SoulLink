using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using SoulLink.Controls;
using SoulLink.Views;

namespace SoulLink
{
    public class App : Application
    {
        public static void AppendErrorLog(string source, Exception? ex) => Console.Error.WriteLine(source + ": " + ex);
    }
}
namespace SoulLink.Controls { public static class RdpConsole { public static bool VerboseLog => false; } }

internal static class SplitLayoutTest
{
    [STAThread] private static int Main()
    {
        try { Run(); return 0; } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool ok, string text)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + text);
        if (!ok) throw new Exception(text);
    }
    private static void Run()
    {
        foreach (var mode in new[] { RdpLayoutMode.Horizontal, RdpLayoutMode.Vertical, RdpLayoutMode.Grid })
        {
            Check(RdpLayoutRules.PageCount(1, mode) == 1, mode + " single page");
            Check(RdpLayoutRules.PageCount(8, mode) == (mode == RdpLayoutMode.Grid ? 2 : 4), mode + " 8-item paging");
        }
        Check(RdpLayoutRules.Dimensions(RdpLayoutMode.Grid, 3) == (2, 2), "3-item grid uses 2x2");
        Check(RdpLayoutRules.Dimensions(RdpLayoutMode.Grid, 4) == (2, 2), "4-item grid uses 2x2");
        Check(RdpLayoutRules.PageCount(99, RdpLayoutMode.Columns) == 1, "column scroll layout never pages");
        Check(RdpLayoutRules.PageCount(99, RdpLayoutMode.Rows) == 1, "row scroll layout never pages");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window? owner = null;
        var tiles = new List<RdpViewportTile>();
        owner = new Window { Width = 1100, Height = 720, Left = 100, Top = 80, WindowStartupLocation = WindowStartupLocation.Manual };
        owner.Loaded += async (_, __) =>
        {
            try
            {
                owner.Activate();
                await Task.Delay(250);
                var grid = new Grid();
                for (int i=0;i<2;i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int i=0;i<2;i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                owner.Content = grid;
                for (int i=0;i<4;i++)
                {
                    var console = new FakeConsole(i);
                    var tile = new RdpViewportTile(i.ToString(), "TEST-" + i, console, owner);
                    Grid.SetColumn(tile,i%2); Grid.SetRow(tile,i/2); grid.Children.Add(tile);
                    tiles.Add(tile);
                }
                await Task.Delay(200);
                foreach(var tile in tiles) tile.Mount();
                await Task.Delay(400);
                Check(tiles.Count == 4, "four independent viewport tiles mounted");
                foreach(var tile in tiles) Check(((FrameworkElement)tile.Console).Parent != null, "tile retains its own console host");
                foreach(var tile in tiles) tile.Lock();
                await Task.Delay(250);
                Check(!tiles[0].IsUnlocked && !tiles[1].IsUnlocked, "viewport shields start locked");
                Console.WriteLine("ALL SPLIT LAYOUT TESTS PASSED");
                app.Shutdown(0);
            }
            catch(Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(2); }
            finally { foreach(var tile in tiles) tile.Dispose(); owner.Close(); }
        };
        owner.Show(); app.Run();
    }

    private sealed class FakeConsole : Grid, IRdpViewport
    {
        public FakeConsole(int index)
        {
            var host = new WindowsFormsHost { Child = new System.Windows.Forms.Panel { BackColor = System.Drawing.Color.FromArgb(30+index*30,60,90) } };
            Children.Add(host);
        }
        public FrameworkElement RootElement => this;
        public FrameworkElement ViewportElement => this;
        public bool IsLooking => false;
        public bool IsConnected => true;
        public void OnShown() { }
        public void RefreshViewport() { }
        public void RequestResize(Version _ = default) { }
        public void OnShieldReleased() { }
        public void ReleasePointerForRearm() { }
        public void SetForegroundActive(bool active) { }
        public void SetActualSizeMode(bool enabled) { }
    }
}
