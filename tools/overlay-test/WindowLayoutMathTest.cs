using System;
using SoulLink.Views;
using System.Collections.Generic;
namespace SoulLink.Services
{
    public static class ConfigStore
    {
        private static readonly UiState State = new UiState();
        public static UiState LoadState() => State;
        public static void SaveState(UiState state) { }
        public class UiState { public List<RdpWindowLayoutState> RdpWindows { get; set; } = new(); }
    }
}
internal static class WindowLayoutMathTest
{
    private static void Check(bool ok, string text) { Console.WriteLine((ok?"PASS ":"FAIL ")+text); if(!ok) throw new Exception(text); }
    public static int Main()
    {
        var a=RdpActualSizeMath.Calculate(1920,1080,1.5,600,400,1400,800,10,58);
        Check(a.windowWidth==1290 && a.windowHeight==778, "1:1 uses physical pixels converted at 150% DPI");
        Check(!a.scrollX && !a.scrollY, "window expands before scrollbars appear");
        var b=RdpActualSizeMath.Calculate(2560,1440,1.0,600,400,1400,800,10,58);
        Check(b.windowWidth==1400 && b.windowHeight==800, "window clamps to workspace");
        Check(b.scrollX && b.scrollY, "scrollbars appear only after workspace limit");
        var s=new RdpWindowLayoutState { AccountId="A2",X=1800,Y=1000,Width=900,Height=700 };
        s.ClampToWorkspace(1200,700);
        Check(s.X+s.Width<=1200 && s.Y+s.Height<=700, "saved window clamps fully into workspace");
        Check(s.Width>=320 && s.Height>=220, "restored window respects minimum size");
        Console.WriteLine("ALL WINDOW LAYOUT MATH TESTS PASSED"); return 0;
    }
}