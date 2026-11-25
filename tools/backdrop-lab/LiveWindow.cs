using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BackdropLab
{
    /// <summary>实时预览窗口：左侧显示背景动画，右侧切方案 / 拨时间轴。</summary>
    internal sealed class LiveWindow : Window
    {
        private readonly List<BackdropBase> _designs = new() { new DesignA(), new DesignB(), new DesignC() };
        private static readonly string[] Titles =
        {
            "A  童话星轨  ·  极光绸带 + 星海 + 爱心",
            "B  星云之瞳  ·  能量核呼吸 + 轨道光弧",
            "C  樱色脉冲  ·  落樱 + 天河光带 + 地平线"
        };

        private readonly BackdropCanvas _canvas = new();
        private readonly StackPanel _tabs = new();
        private readonly Slider _time = new() { Minimum = 0, Maximum = 20, Value = 0, Width = 260 };
        private readonly TextBlock _timeLabel = new() { Foreground = Brushes.White, FontSize = 12 };
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _paused;
        private double _frozenAt;

        public LiveWindow()
        {
            Title = "SoulLink 背景动画预览";
            Width = 1500; Height = 940;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x07, 0x14));

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

            _canvas.Design = _designs[0];
            Grid.SetColumn(_canvas, 0);
            root.Children.Add(_canvas);

            var side = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x0B, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 255, 255, 255)),
                BorderThickness = new Thickness(1, 0, 0, 0),
                Padding = new Thickness(18, 20, 18, 20)
            };
            var sp = new StackPanel();
            sp.Children.Add(Cap("方案"));
            _tabs.Orientation = Orientation.Vertical;
            for (int i = 0; i < _designs.Count; i++)
            {
                int cap = i;
                var b = MakeTab(Titles[i]);
                b.Click += (_, __) => Select(cap);
                _tabs.Children.Add(b);
            }
            sp.Children.Add(_tabs);

            sp.Children.Add(Cap("时间轴（秒）"));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(_time);
            row.Children.Add(_timeLabel);
            _time.ValueChanged += (_, __) =>
            {
                _timeLabel.Text = _time.Value.ToString("0.0") + "s";
                if (_paused) { _frozenAt = _time.Value; _canvas.Time = _frozenAt; _canvas.InvalidateVisual(); }
            };
            sp.Children.Add(row);

            var pause = MakeTab("⏸  暂停 / 继续");
            pause.Click += (_, __) =>
            {
                _paused = !_paused;
                if (_paused) _frozenAt = _time.Value;
                else _clock.Restart();
            };
            sp.Children.Add(pause);

            var note = new TextBlock
            {
                Text = "半透明方框 = 主窗口导航条与内容卡片的大致位置，\n用来判断背景会不会盖住正文。",
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xC8, 0xD2, 0xF0)),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 18, 0, 0)
            };
            sp.Children.Add(note);

            side.Child = sp;
            Grid.SetColumn(side, 1);
            root.Children.Add(side);

            Content = root;

            _timer.Tick += (_, __) =>
            {
                if (_paused) return;
                double t = _clock.Elapsed.TotalSeconds;
                _canvas.Time = t;
                _canvas.InvalidateVisual();
                _time.Value = t % 20;
            };
            _timer.Start();
            Select(0);
        }

        private void Select(int index)
        {
            _canvas.Design = _designs[index];
            _canvas.InvalidateVisual();
            for (int i = 0; i < _tabs.Children.Count && i < _designs.Count; i++)
                if (_tabs.Children[i] is Button b)
                    b.Background = new SolidColorBrush(i == index
                        ? Color.FromArgb(0x38, 0xB0, 0x8C, 0xFF)
                        : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        }

        private static TextBlock Cap(string text) => new()
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xBE, 0xC8, 0xE8)),
            FontSize = 11.5,
            Margin = new Thickness(2, 6, 0, 8)
        };

        private static Button MakeTab(string text)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xF0, 0xF4, 0xFF)),
                Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 9, 11, 9),
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand
            };
            return b;
        }
    }

    internal sealed class BackdropCanvas : FrameworkElement
    {
        public BackdropBase? Design { get; set; }
        public double Time { get; set; }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            if (Design == null) return;
            var c = new Ctx(dc, w, h, Time, true);
            Design.Render(c);
            c.DrawLayoutMask();
        }
    }
}
