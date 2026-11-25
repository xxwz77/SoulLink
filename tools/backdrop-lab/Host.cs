using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BackdropLab
{
    /// <summary>离屏渲染器：不需要开窗口，直接画到 RenderTargetBitmap 再存 PNG / GIF。</summary>
    internal static class Host
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in args.Where(x => x.StartsWith("--")))
            {
                var body = a.Substring(2);
                var eq = body.IndexOf('=');
                if (eq < 0) opts[body] = "1";
                else opts[body.Substring(0, eq)] = body.Substring(eq + 1);
            }

            if (opts.ContainsKey("live"))
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.Startup += (_, __) => new LiveWindow().Show();
                return app.Run();
            }

            if (opts.ContainsKey("petalprobe"))
            {
                PetalProbe.Run(opts.TryGetValue("out", out var pp) ? pp : "petals.png");
                return 0;
            }

            double w = Num(opts, "w", 1240);
            double h = Num(opts, "h", 820);
            string design = opts.TryGetValue("design", out var d) ? d.ToUpperInvariant() : "A";

            BackdropBase bd = design switch
            {
                "B" => new DesignB(),
                "C" => new DesignC(),
                _ => new DesignA(),
            };

            if (opts.TryGetValue("gif", out var gifPath))
            {
                int frames = (int)Num(opts, "frames", 48);
                double fps = Num(opts, "fps", 16);
                var list = new List<BitmapSource>(frames);
                for (int i = 0; i < frames; i++) list.Add(Render(bd, w, h, i / fps));
                GifWriter.Write(gifPath, list, (int)Math.Round(1000.0 / fps));
                Console.WriteLine("gif " + gifPath + " frames=" + frames);
            }
            else
            {
                string outPath = opts.TryGetValue("out", out var op) ? op : "frame.png";
                PngWriter.Write(outPath, Render(bd, w, h, Num(opts, "t", 0)));
                Console.WriteLine("png " + outPath);
            }
            return 0;
        }

        private static double Num(Dictionary<string, string> o, string k, double def)
            => o.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : def;

        private static BitmapSource Render(BackdropBase bd, double w, double h, double t)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var ctx = new Ctx(dc, w, h, t, true);
                bd.Render(ctx);
                ctx.DrawLayoutMask();
            }
            var rtb = new RenderTargetBitmap((int)Math.Round(w), (int)Math.Round(h), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
    }

    internal sealed class Ctx
    {
        public readonly DrawingContext Dc;
        public readonly double W, H, T;
        public readonly bool Dark;

        public Ctx(DrawingContext dc, double w, double h, double t, bool dark)
        { Dc = dc; W = w; H = h; T = t; Dark = dark; }

        /// <summary>画出主窗口的导航条 + 内容卡片占位，方便判断背景在真实界面下的可读性。</summary>
        public void DrawLayoutMask()
        {
            double navW = 78, topH = 74, margin = 20;
            var glass = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));
            glass.Freeze();
            var stroke = new Pen(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), 1);
            stroke.Freeze();
            Dc.DrawRoundedRectangle(glass, stroke, new Rect(margin, topH, navW, H - topH - 16), 14, 14);
            Dc.DrawRoundedRectangle(glass, stroke, new Rect(margin + navW + 16, topH, W - navW - 2 * margin - 16, H - topH - 16), 16, 16);
        }
    }

    internal abstract class BackdropBase
    {
        public abstract void Render(Ctx c);

        protected static void Base(Ctx c, Color top, Color mid, Color bottom, double midOffset = 0.5)
            => c.Dc.DrawRectangle(Fx.VBase(top, mid, bottom, midOffset), null, new Rect(0, 0, c.W, c.H));
    }

    internal static class PngWriter
    {
        public static void Write(string path, BitmapSource bmp)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }
}