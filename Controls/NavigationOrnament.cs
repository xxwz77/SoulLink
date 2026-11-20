using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SoulLink.Controls
{
    /// <summary>
    /// SoulLink 左侧菜单专用的纯矢量装饰层。它只负责外观，不接收输入、
    /// 不参与导航逻辑，也不修改任何全局主题资源。
    /// </summary>
    public sealed class NavigationOrnament : FrameworkElement
    {
        private enum GlyphKind { Launch, Console, DataCenter, Update, RouteUpdate, Remote, Lobby, Process, Cli }

        private static readonly Dictionary<string, GlyphKind> Glyphs = new(StringComparer.Ordinal)
        {
            ["launch"] = GlyphKind.Launch,
            ["console"] = GlyphKind.Console,
            ["datacenter"] = GlyphKind.DataCenter,
            ["update"] = GlyphKind.Update,
            ["routeupdate"] = GlyphKind.RouteUpdate,
            ["remote"] = GlyphKind.Remote,
            ["sessions"] = GlyphKind.Lobby,
            ["procs"] = GlyphKind.Process,
            ["cli"] = GlyphKind.Cli,
        };

        public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
            nameof(PageKey), typeof(string), typeof(NavigationOrnament),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
            nameof(IsSelected), typeof(bool), typeof(NavigationOrnament),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.Register(
            nameof(IsHovered), typeof(bool), typeof(NavigationOrnament),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SelectionOnlyProperty = DependencyProperty.Register(
            nameof(SelectionOnly), typeof(bool), typeof(NavigationOrnament),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool SelectionOnly
        {
            get => (bool)GetValue(SelectionOnlyProperty);
            set => SetValue(SelectionOnlyProperty, value);
        }

        public static readonly DependencyProperty IsLightProperty = DependencyProperty.Register(
            nameof(IsLight), typeof(bool), typeof(NavigationOrnament),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(
            nameof(MutedBrush), typeof(Brush), typeof(NavigationOrnament),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
            nameof(TextBrush), typeof(Brush), typeof(NavigationOrnament),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CyanBrushProperty = DependencyProperty.Register(
            nameof(CyanBrush), typeof(Brush), typeof(NavigationOrnament),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty VioletBrushProperty = DependencyProperty.Register(
            nameof(VioletBrush), typeof(Brush), typeof(NavigationOrnament),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

        public string PageKey
        {
            get => (string)GetValue(PageKeyProperty);
            set => SetValue(PageKeyProperty, value);
        }

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        public bool IsHovered
        {
            get => (bool)GetValue(IsHoveredProperty);
            set => SetValue(IsHoveredProperty, value);
        }

        public bool IsLight
        {
            get => (bool)GetValue(IsLightProperty);
            set => SetValue(IsLightProperty, value);
        }

        public Brush MutedBrush
        {
            get => (Brush)GetValue(MutedBrushProperty);
            set => SetValue(MutedBrushProperty, value);
        }

        public Brush TextBrush
        {
            get => (Brush)GetValue(TextBrushProperty);
            set => SetValue(TextBrushProperty, value);
        }

        public Brush CyanBrush
        {
            get => (Brush)GetValue(CyanBrushProperty);
            set => SetValue(CyanBrushProperty, value);
        }

        public Brush VioletBrush
        {
            get => (Brush)GetValue(VioletBrushProperty);
            set => SetValue(VioletBrushProperty, value);
        }

        public NavigationOrnament()
        {
            IsHitTestVisible = false;
            Focusable = false;
            SnapsToDevicePixels = true;
            // 仅由既有侧栏设置/覆盖；控件本身不定义或修改主题。
            IsLight = TryFindResource("TextPrimary") is SolidColorBrush primary
                && (0.299 * primary.Color.R + 0.587 * primary.Color.G + 0.114 * primary.Color.B) > 160;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth, h = ActualHeight;
            if (w < 2 || h < 2) return;

            bool on = IsSelected;
            double rowHeight = Math.Min(34, Math.Max(26, h - 4));
            double top = (h - rowHeight) / 2;
            double left = w <= 64 ? 3.5 : 5.5;
            var rowRect = new Rect(left, top, Math.Max(1, w - left - 5.5), rowHeight);
            var radius = new CornerRadius(10);

            if (SelectionOnly)
            {
                if (!on && !IsHovered) return;
                if (!on)
                {
                    byte alpha = (byte)(IsLight ? 24 : 16);
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(alpha,
                        IsLight ? (byte)123 : (byte)255, IsLight ? (byte)91 : (byte)255, IsLight ? (byte)175 : (byte)255)),
                        null, rowRect, 9, 9);
                    return;
                }
                Brush background = new LinearGradientBrush(
                    Color.FromArgb(64, 124, 92, 255), Color.FromArgb(64, 79, 227, 255), new Point(0, 0), new Point(1, 0));
                if (IsLight)
                    background = new LinearGradientBrush(
                        Color.FromArgb(58, 157, 122, 204), Color.FromArgb(58, 79, 179, 204), new Point(0, 0), new Point(1, 0));
                var border = new Pen(new SolidColorBrush(Color.FromArgb(
                    (byte)(IsLight ? 77 : 85), IsLight ? (byte)151 : (byte)133,
                    IsLight ? (byte)116 : (byte)212, IsLight ? (byte)184 : (byte)236)), .8);
                dc.DrawRoundedRectangle(background, border, rowRect, 10, 10);
                if (w > 120)
                    dc.DrawEllipse(CyanBrush, null, new Point(w - 22, h / 2), 2.1, 2.1);
                return;
            }

            if (!Glyphs.TryGetValue(PageKey ?? string.Empty, out var glyph)) return;

            if (on)
            {
                dc.DrawRoundedRectangle(CyanBrush, null, new Rect(left, top + 8, 2.5, rowHeight - 16), 1.2, 1.2);
            }

            Brush iconBrush = on
                ? new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop(IsLight ? Color.FromRgb(105, 65, 159) : Color.FromRgb(202, 175, 255), 0),
                        new GradientStop(IsLight ? Color.FromRgb(0, 122, 158) : Color.FromRgb(117, 236, 255), 1),
                    }
                }
                : IsHovered ? TextBrush : MutedBrush;

            // The 24x24 glyph is centered in the existing 42px hit target. The text,
            // click handler, tooltip and row dimensions in MainWindow.xaml stay intact.
            double iconSize = 22;
            dc.PushTransform(new TranslateTransform(21 - iconSize / 2, h / 2 - iconSize / 2));
            dc.PushTransform(new ScaleTransform(iconSize / 24, iconSize / 24));
            DrawGlyph(dc, glyph, iconBrush);
            dc.Pop();
            dc.Pop();
        }

        private static void DrawGlyph(DrawingContext dc, GlyphKind glyph, Brush brush)
        {
            var pen = new Pen(brush, 1.7)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            switch (glyph)
            {
                case GlyphKind.Launch:
                    Stroke(dc, pen, P(10.1, 14.1), P(7, 16.2), P(7.9, 12.9));
                    Stroke(dc, pen, P(14.1, 10.1), P(16.3, 7), P(13, 7.9));
                    Shape(dc, brush, pen, "M8.2 16 C6.7 15.8 6.3 14.7 7 13.5 L9 10.4 C11.6 5.5 15.1 2.7 20.8 3.2 C21.3 8.9 18.5 12.4 13.6 15 L10.5 17 C9.3 17.7 8.2 17.3 8.2 16 Z");
                    Curve(dc, pen, P(6.7, 17.4), P(5.2, 18.2), P(4.5, 20), P(4, 21));
                    Curve(dc, pen, P(4, 21), P(5.2, 20.6), P(6.8, 20.1), P(7.8, 18.7));
                    Dot(dc, brush, 15.7, 8.1, 1.65);
                    break;
                case GlyphKind.Console:
                    Stroke(dc, pen, P(3.5, 8.5), P(20.5, 8.5));
                    Stroke(dc, pen, P(3.5, 15.5), P(20.5, 15.5));
                    Stroke(dc, pen, P(8, 5.5), P(8, 11.5));
                    Stroke(dc, pen, P(16, 12.5), P(16, 18.5));
                    break;
                case GlyphKind.DataCenter:
                    Shape(dc, brush, pen, "M4 6.2 C4 4.9 7.6 4 12 4 C16.4 4 20 4.9 20 6.2 C20 7.5 16.4 8.4 12 8.4 C7.6 8.4 4 7.5 4 6.2 Z");
                    Curve(dc, pen, P(4, 6.2), P(4, 6.2), P(4, 8.8), P(4, 12));
                    Curve(dc, pen, P(4, 12), P(4, 13.3), P(7.6, 14.2), P(12, 14.2));
                    Curve(dc, pen, P(12, 14.2), P(16.4, 14.2), P(20, 13.3), P(20, 12));
                    Stroke(dc, pen, P(20, 12), P(20, 6.2));
                    Stroke(dc, pen, P(4, 12), P(4, 17.8));
                    Curve(dc, pen, P(4, 17.8), P(4, 19.1), P(7.6, 20), P(12, 20));
                    Curve(dc, pen, P(12, 20), P(16.4, 20), P(20, 19.1), P(20, 17.8));
                    Stroke(dc, pen, P(20, 17.8), P(20, 12));
                    Dot(dc, brush, 7, 11.9, 1.05);
                    Dot(dc, brush, 7, 17.8, 1.05);
                    break;
                case GlyphKind.Update:
                    Curve(dc, pen, P(20, 11.6), P(20, 11.6), P(20, 15.8), P(20, 17.5));
                    Curve(dc, pen, P(20, 17.5), P(20, 18.9), P(16.4, 20), P(12, 20));
                    Curve(dc, pen, P(12, 20), P(7.6, 20), P(4, 18.9), P(4, 17.5));
                    Stroke(dc, pen, P(4, 17.5), P(4, 11.6));
                    Stroke(dc, pen, P(12, 3.5), P(12, 15));
                    Stroke(dc, pen, P(8.2, 11.3), P(12, 15.1), P(15.8, 11.3));
                    break;
                case GlyphKind.RouteUpdate:
                    Stroke(dc, pen, P(6.8, 18.2), P(10.8, 18.2));
                    Curve(dc, pen, P(10.8, 18.2), P(16.8, 18.2), P(17, 8.1), P(12.1, 8.1));
                    Stroke(dc, pen, P(12.1, 8.1), P(6.2, 8.1));
                    Stroke(dc, pen, P(9.1, 5.2), P(6.2, 8.1), P(9.1, 11));
                    Dot(dc, brush, 5.2, 18.2, 2);
                    Dot(dc, brush, 18.4, 5.7, 2);
                    break;
                case GlyphKind.Remote:
                    Shape(dc, brush, pen, "M3.5 5 H20.5 V16 H3.5 Z");
                    Stroke(dc, pen, P(8.5, 20), P(15.5, 20));
                    Stroke(dc, pen, P(12, 16), P(12, 20));
                    Stroke(dc, pen, P(9, 11.7), P(10.8, 13.5), P(9, 15.3));
                    Stroke(dc, pen, P(12.8, 15.3), P(15.5, 15.3));
                    break;
                case GlyphKind.Lobby:
                    Stroke(dc, pen, P(3.5, 13.5), P(3.5, 18.5), P(20.5, 18.5), P(20.5, 13.5));
                    Stroke(dc, pen, P(12, 18.5), P(12, 21));
                    Arc(dc, pen, P(5.5, 10), 6.5, true);
                    Arc(dc, pen, P(8.5, 10), 3.5, true);
                    Dot(dc, brush, 12, 12, 1.65);
                    break;
                case GlyphKind.Process:
                    Stroke(dc, pen, P(8, 3.5), P(4, 3.5), P(4, 20), P(20, 20), P(20, 16));
                    Stroke(dc, pen, P(8, 8), P(12, 8));
                    Stroke(dc, pen, P(8, 12), P(12, 12));
                    Stroke(dc, pen, P(8, 16), P(11, 16));
                    Shape(dc, brush, pen, "M14 4 H20 V10 H14 Z");
                    Stroke(dc, pen, P(16, 6.3), P(18, 8), P(16, 9.7));
                    break;
                case GlyphKind.Cli:
                    Stroke(dc, pen, P(5, 8), P(9, 12), P(5, 16));
                    Stroke(dc, pen, P(12, 16.5), P(19, 16.5));
                    break;
            }
        }

        private static Point P(double x, double y) => new(x, y);

        private static void Stroke(DrawingContext dc, Pen pen, params Point[] points)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                ctx.PolyLineTo(points, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        private static void Curve(DrawingContext dc, Pen pen, Point start, Point a, Point b, Point end)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.BezierTo(a, b, end, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        private static void Shape(DrawingContext dc, Brush brush, Pen pen, string data)
        {
            // Closed outlines stay hollow: this is a line-icon set, not filled badges.
            dc.DrawGeometry(null, pen, Geometry.Parse(data));
        }

        private static void Arc(DrawingContext dc, Pen pen, Point start, double radius, bool upper)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(P(start.X + 2 * radius, start.Y), new Size(radius, radius), 0, false,
                    upper ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        private static void Dot(DrawingContext dc, Brush brush, double x, double y, double radius)
        {
            dc.DrawEllipse(brush, null, P(x, y), radius, radius);
        }
    }
}
