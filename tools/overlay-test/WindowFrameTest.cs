using System;
using System.Windows;
using System.Windows.Controls;
using SoulLink.Views;
namespace SoulLink.Services
{
    public static class ConfigStore
    {
        private static readonly UiState State = new UiState();
        public static UiState LoadState() => State;
        public static void SaveState(UiState state) { }
        public class UiState { public System.Collections.Generic.List<RdpWindowLayoutState> RdpWindows { get; set; } = new(); }
    }
}
internal static class WindowFrameTest
{
    [STAThread] private static int Main()
    {
        try { Run(); return 0; } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool ok,string text) { Console.WriteLine((ok?"PASS ":"FAIL ")+text); if(!ok) throw new Exception(text); }
    private static void Run()
    {
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var canvas=new Canvas();
        var owner=new Window { Width=1000,Height=700,Content=canvas,Left=100,Top=80,WindowStartupLocation=WindowStartupLocation.Manual };
        owner.Loaded += async (_,__) =>
        {
            try
            {
                owner.Activate(); await System.Threading.Tasks.Task.Delay(120);
                var frame=new RdpWindowFrame("A2","A2",new Border(),canvas);
                frame.SetBounds(new Rect(40,40,600,400)); canvas.Children.Add(frame);
                Check(frame.Bounds.Width==600 && frame.Bounds.Height==400,"initial internal window geometry");
                frame.ToggleMaximize(); await System.Threading.Tasks.Task.Delay(80);
                Check(frame.IsWorkspaceMaximized && frame.Width==canvas.ActualWidth,"workspace maximize");
                frame.ToggleMaximize(); Check(!frame.IsWorkspaceMaximized && frame.Width==600,"restore previous window size");
                owner.Width=740; owner.Height=540; await System.Threading.Tasks.Task.Delay(150);
                frame.SetBounds(new Rect(600,450,650,500)); frame.ClampToWorkspace();
                Console.WriteLine($"TEST bounds={frame.Bounds} actual={frame.ActualWidth}x{frame.ActualHeight} canvas={canvas.ActualWidth}x{canvas.ActualHeight} max={frame.IsWorkspaceMaximized}");
                Check(frame.Bounds.Right<=canvas.ActualWidth+0.5 && frame.Bounds.Bottom<=canvas.ActualHeight+0.5,"window cannot escape workspace");
                var state=new RdpWindowLayoutState { AccountId="A2",X=frame.Bounds.X,Y=frame.Bounds.Y,Width=frame.Width,Height=frame.Height,ZOrder=4 };
                RdpWindowLayoutStore.Save(state); var restored=RdpWindowLayoutStore.Get("A2");
                Check(restored!=null && restored.AccountId=="A2" && restored.ZOrder==4,"per-account layout persisted");
                Console.WriteLine("ALL INTERNAL WINDOW TESTS PASSED"); app.Shutdown(0);
            }
            catch(Exception ex){Console.Error.WriteLine(ex);app.Shutdown(2);} finally { owner.Close(); }
        };
        owner.Show(); app.Run();
    }
}