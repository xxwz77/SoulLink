using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MsClock
{
    /// <summary>
    /// 毫秒级高刷时钟：HH:mm:ss.fff 大字号等宽数字 + 真实渲染帧率 + 每帧跳变色块 + 渲染序号。
    /// 用于 A2(session4) 桌面，浏览器画面与 RDP 画面同框截图测延迟。
    /// 窗口：无边框、置顶；ESC 退出（首选）、Alt+F4 关闭、--fullscreen 全屏、--bench <path> 自测。
    /// </summary>
    public partial class App : Application
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var app = new App();
            var w = new ClockWindow(
                fullscreen: Array.Exists(args, a => string.Equals(a, "--fullscreen", StringComparison.OrdinalIgnoreCase)),
                bench: BenchPath(args),
                holdMs: HoldMs(args));
            return app.Run(w);
        }

        private static string BenchPath(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "--bench", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        /// <summary>★ 决定性实验参数（2026-11-24）：--hold &lt;ms&gt; —— 渲染照常（色块/序号/fps 每帧变），
        /// 但时间数字每 hold 毫秒才刷新。用于分离「桌面变化帧率」与「时间内容更新频率」：
        /// hold=17（高频）对照 hold=500（低频，模拟保活节拍）测差分。</summary>
        private static int HoldMs(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "--hold", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], out int v))
                    return Math.Clamp(v, 0, 5000);
            return 0;   // 0 = 每帧刷新（原行为）
        }
    }

    public sealed class ClockWindow : Window
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uMilliseconds);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uMilliseconds);

        private readonly TextBlock _time;
        private readonly TextBlock _fps;
        private readonly TextBlock _seq;
        private readonly Border _swatch;

        private int _renderCount;      // CompositionTarget.Rendering 回调次数 = 真实渲染帧数
        private long _swatchSeq;       // 渲染序号（与真实上屏帧同步）
        private double _fpsWindowStart; // 秒
        private int _fpsWindowCount;
        private bool _fpsPrimed;

        // --bench 自测统计
        private readonly string _bench;
        private readonly Stopwatch _benchClock = Stopwatch.StartNew();
        private readonly List<double> _frameIntervalsMs = new List<double>();
        private double _lastRenderSec;

        // ★ --hold <ms>：时间数字刷新间隔（0=每帧）。渲染回调（色块/序号/fps）不受影响。
        private readonly int _holdMs;
        private long _lastTimeUpdateMs;   // Environment.TickCount64

        private static readonly Color[] SwatchColors =
        {
            Color.FromRgb(0xFF, 0x00, 0x00),
            Color.FromRgb(0x00, 0xFF, 0x00),
            Color.FromRgb(0x00, 0x00, 0xFF),
            Color.FromRgb(0xFF, 0xFF, 0x00),
            Color.FromRgb(0x00, 0xFF, 0xFF),
            Color.FromRgb(0xFF, 0x00, 0xFF),
        };

        public ClockWindow(bool fullscreen, string bench, int holdMs)
        {
            _bench = bench;
            _holdMs = holdMs;
            Title = "ms-clock";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            Background = Brushes.Black;
            ShowInTaskbar = false;

            var root = new Grid { Background = Brushes.Black };

            _time = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 96,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Text = "00:00:00.000",
            };
            _time.HorizontalAlignment = HorizontalAlignment.Center;
            _time.VerticalAlignment = VerticalAlignment.Center;

            _fps = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 24,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xFF, 0x7F)),
                Text = "fps: -- (render)",
            };
            _fps.HorizontalAlignment = HorizontalAlignment.Left;
            _fps.VerticalAlignment = VerticalAlignment.Top;
            _fps.Margin = new Thickness(16, 8, 0, 0);

            _swatch = new Border
            {
                Width = 64,
                Height = 64,
                Background = new SolidColorBrush(SwatchColors[0]),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 96, 0),
                CornerRadius = new CornerRadius(6),
            };

            _seq = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 24,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                Text = "#0",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 24, 16, 0),
            };

            root.Children.Add(_time);
            root.Children.Add(_fps);
            root.Children.Add(_swatch);
            root.Children.Add(_seq);
            Content = root;

            if (fullscreen)
            {
                WindowState = WindowState.Maximized;
                WindowStyle = WindowStyle.None;
                Left = 0; Top = 0;
                Width = SystemParameters.PrimaryScreenWidth;
                Height = SystemParameters.PrimaryScreenHeight;
            }
            else
            {
                Width = 900;
                Height = 320;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            PreviewKeyDown += OnPreviewKeyDown;
            Closed += OnClosed;

            // ★ 修正 fps 口径（2026-11-24）：
            // 旧实现用 DispatcherTimer(1ms) 计数，计的是「定时器回调次数」，不是真实上屏帧数；
            // 且默认系统定时器粒度 15.6ms，导致 .fff 毫秒位实际只按 ~15.6ms 桶跳变。
            // 新实现：CompositionTarget.Rendering 每次真实渲染帧触发（≈ 显示器刷新率），
            // 色块/序号与真实上屏帧同步；timeBeginPeriod(1) 把系统定时器粒度提到 ~1ms。
            timeBeginPeriod(1);
            CompositionTarget.Rendering += OnRendering;

            Loaded += (s, e) =>
            {
                Activate();
                Focus();
            };
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();   // ESC 退出（首选）
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.System && e.SystemKey == Key.F4)
            {
                e.Handled = true;
                Close();   // Alt+F4 兜底
                return;
            }
            base.OnKeyDown(e);
        }

        private void OnRendering(object sender, EventArgs e)
        {
            _renderCount++;

            var now = DateTime.Now;
            // ★ --hold：时间值低频刷新（渲染照常）。hold=0 时保持原每帧刷新行为。
            if (_holdMs <= 0 || (Environment.TickCount64 - _lastTimeUpdateMs) >= _holdMs)
            {
                _lastTimeUpdateMs = Environment.TickCount64;
                _time.Text = now.ToString("HH:mm:ss.fff");
            }

            double sec = _benchClock.Elapsed.TotalSeconds;
            if (!_fpsPrimed)
            {
                _fpsPrimed = true;
                _fpsWindowStart = sec;
                _fpsWindowCount = 0;
                _lastRenderSec = sec;
            }

            // 真实渲染帧率（500ms 滑动窗）
            _fpsWindowCount++;
            double dtFps = sec - _fpsWindowStart;
            if (dtFps >= 0.5)
            {
                double fps = _fpsWindowCount / dtFps;
                _fps.Text = "fps: " + fps.ToString("0.0", CultureInfo.InvariantCulture) + " (render)";
                _fpsWindowCount = 0;
                _fpsWindowStart = sec;
            }

            // 帧间隔统计（bench）
            if (_bench != null)
            {
                double dt = sec - _lastRenderSec;
                _lastRenderSec = sec;
                if (dt > 0) _frameIntervalsMs.Add(dt * 1000.0);
            }

            // 色块 + 渲染序号（与真实上屏帧同步）
            _swatchSeq++;
            _swatch.Background = new SolidColorBrush(SwatchColors[_swatchSeq % SwatchColors.Length]);
            _seq.Text = "#" + _swatchSeq.ToString();

            // bench 自测：12 秒后自动落盘并退出
            if (_bench != null && sec >= 12.0)
            {
                Close();
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            timeEndPeriod(1);
            if (_bench != null) WriteBench();
        }

        private void WriteBench()
        {
            // 毫秒粒度验证：Thread.Sleep(1) 实测实际睡眠时长。
            // Windows 默认系统定时器粒度 15.6ms；timeBeginPeriod(1) 后应降到 ~1~2ms。
            // 这是「时钟 .fff 毫秒位更新粒度」最直接的可观测证据。
            var sleepMs = new List<double>();
            for (int i = 0; i < 100; i++)
            {
                var sw = Stopwatch.StartNew();
                Thread.Sleep(1);
                sw.Stop();
                sleepMs.Add(sw.Elapsed.TotalMilliseconds);
            }
            sleepMs.Sort();

            var sb = new StringBuilder();
            sb.AppendLine("renderFps=" + (_renderCount / Math.Max(0.001, _benchClock.Elapsed.TotalSeconds)).ToString("0.0", CultureInfo.InvariantCulture));
            if (_frameIntervalsMs.Count > 0)
            {
                _frameIntervalsMs.Sort();
                int n = _frameIntervalsMs.Count;
                double p50 = _frameIntervalsMs[n / 2];
                double p95 = _frameIntervalsMs[(int)(n * 0.95)];
                double max = _frameIntervalsMs[n - 1];
                sb.AppendLine("renderIntervalMs p50=" + p50.ToString("0.00", CultureInfo.InvariantCulture)
                    + " p95=" + p95.ToString("0.00", CultureInfo.InvariantCulture)
                    + " max=" + max.ToString("0.00", CultureInfo.InvariantCulture));
            }
            sb.AppendLine("sleep1msGranularityMs median=" + sleepMs[sleepMs.Count / 2].ToString("0.00", CultureInfo.InvariantCulture)
                + " min=" + sleepMs[0].ToString("0.00", CultureInfo.InvariantCulture)
                + " max=" + sleepMs[sleepMs.Count - 1].ToString("0.00", CultureInfo.InvariantCulture));
            sb.AppendLine("renderCount=" + _renderCount + " swatchSeq=" + _swatchSeq);
            try { File.WriteAllText(_bench, sb.ToString(), Encoding.UTF8); } catch { }
        }
    }
}
