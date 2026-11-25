using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// 用 MediaElement（系统自带 Media Foundation 解码）逐点定位抽帧。
/// 不依赖 ffmpeg、不联网。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string input = args.Length > 0 ? args[0] : "";
        string outDir = args.Length > 1 ? args[1] : "frames";
        if (!File.Exists(input)) { Console.WriteLine("input not found"); return 1; }
        Directory.CreateDirectory(outDir);
        int want = args.Length > 2 && int.TryParse(args[2], out var n) ? n : 8;

        var app = new Application();
        int code = 0;
        app.Startup += (_, _) =>
        {
            code = Run(input, outDir, want);
            app.Shutdown();
        };
        app.Run();
        return code;
    }

    private static int Run(string input, string outDir, int want)
    {
        var me = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            Volume = 0,
            IsMuted = true,
            Width = 1280,
            Height = 720
        };

        var host = new Window
        {
            Width = 1300, Height = 780,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = new Grid { Children = { me } }
        };
        host.Show();

        bool opened = false;
        me.MediaOpened += (_, _) => opened = true;
        me.MediaFailed += (_, e) =>
        {
            Console.WriteLine("media failed: " + e.ErrorException?.Message);
        };
        me.Source = new Uri(input, UriKind.Absolute);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // MediaElement 需要窗口真正可见才会开始解码，且要持续泵消息
        while (!opened && sw.Elapsed.TotalSeconds < 20)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(50),
                System.Windows.Threading.DispatcherPriority.Background,
                (_, __) => frame.Continue = false,
                System.Windows.Threading.Dispatcher.CurrentDispatcher);
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        if (!opened) { Console.WriteLine("open timeout"); host.Close(); return 2; }

        double dur = me.NaturalDuration.HasTimeSpan ? me.NaturalDuration.TimeSpan.TotalSeconds : 0;
        int w = (int)me.NaturalVideoWidth, h = (int)me.NaturalVideoHeight;
        Console.WriteLine($"DURATION={dur:F2}s  SIZE={w}x{h}");
        if (dur <= 0 || w <= 0) { host.Close(); return 3; }

        int saved = 0;
        for (int i = 0; i < want; i++)
        {
            double t = dur * (i + 0.5) / want;
            me.Position = TimeSpan.FromSeconds(t);

            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (wait.Elapsed.TotalMilliseconds < 700)
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.Background);

            try
            {
                // MediaElement 是 Visual，直接用 VisualBrush 抓当前帧
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(me), null, new Rect(0, 0, w, h));
                var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                string path = Path.Combine(outDir, $"t{i:00}_{t:F1}s.png");
                using var fs = File.Create(path);
                enc.Save(fs);
                saved++;
                Console.WriteLine("saved " + path);
            }
            catch (Exception ex) { Console.WriteLine("frame err: " + ex.Message); }
        }

        host.Close();
        Console.WriteLine("TOTAL=" + saved);
        return 0;
    }
}
