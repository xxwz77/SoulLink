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
    internal static class PetalProbe
    {
        public static void Run(string outPath)
        {
            double w = 700, h = 380;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));

                // 1) 纯几何（白色，无描边）
                dc.PushTransform(new TransformGroup
                {
                    Children = { new TranslateTransform(90, 90) }
                });
                dc.PushTransform(new ScaleTransform(70, 70));
                dc.DrawGeometry(Brushes.White, null, Fx.PetalGeometry());
                dc.Pop();
                dc.Pop();

                // 2) Fx.Petal，正常参数
                Fx.Petal(dc, 240, 90, 70, 0, Colors.White, Color.FromRgb(0xFF, 0x9E, 0xC9), 1.0, 1.0);

                // 3) Fx.Petal，旋转 35 度
                Fx.Petal(dc, 400, 90, 70, 35, Colors.White, Color.FromRgb(0xFF, 0x6F, 0xB5), 1.0, 1.0);

                // 4) Fx.Petal，小尺寸（真实落樱尺寸 8~19）
                Fx.Petal(dc, 560, 90, 14, 20, Colors.White, Color.FromRgb(0xFF, 0x9E, 0xC9), 1.0, 1.0);
                Fx.Petal(dc, 620, 90, 14, 20, Fx.C("#FFF4FA"), Fx.C("#FF9EC9"), 0.9, 0.8);

                // 5) 一排尺寸递增
                for (int i = 0; i < 10; i++)
                    Fx.Petal(dc, 80 + i * 60, 230, 6 + i * 2.5, i * 18, Fx.C("#FFF4FA"), Fx.C("#FF9EC9"), 0.9, 0.75);

                // 6) 网格参照
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 0.5);
                pen.Freeze();
                for (int i = 0; i < (int)w; i += 50) dc.DrawLine(pen, new Point(i, 0), new Point(i, h));
                for (int i = 0; i < (int)h; i += 50) dc.DrawLine(pen, new Point(0, i), new Point(w, i));
            }
            var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            PngWriter.Write(outPath, rtb);
            Console.WriteLine("petal probe -> " + outPath);
        }
    }
}
