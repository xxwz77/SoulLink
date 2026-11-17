using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Interop;

namespace SoulLink.Views
{
    /// <summary>
    /// 覆盖 RDP 画面的独立 HWND 过场。WPF 浮层会被 WindowsFormsHost 空域挡住，不能用于这里。
    /// </summary>
    public partial class LogoffTransitionWindow : Window
    {
        private Storyboard? _spinner;
        private FrameworkElement? _viewport;
        private Func<bool>? _belongsToCurrentView;
        private readonly System.Windows.Threading.DispatcherTimer _visibilityTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
        private bool _closing;
        private bool _completed;
        private bool _belongsToCurrentViewValue = true;
        private DateTime _hiddenAt = DateTime.MinValue;

        public LogoffTransitionWindow(FrameworkElement viewport, string caption, Func<bool> belongsToCurrentView)
        {
            _belongsToCurrentView = belongsToCurrentView;
            InitializeComponent();
            _viewport = viewport;
            Owner = Window.GetWindow(viewport);
            DetailText.Text = caption + "  ·  正在安全关闭会话与画面";
            Opacity = 0;
            // 用独立窗口对齐视口物理像素，确保盖住整个 ActiveX / WindowsFormsHost。
            var topLeft = viewport.PointToScreen(new Point(0, 0));
            var bottomRight = viewport.PointToScreen(new Point(viewport.ActualWidth, viewport.ActualHeight));
            var source = PresentationSource.FromVisual(viewport);
            double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
            Left = topLeft.X * dpiX;
            Top = topLeft.Y * dpiY;
            Width = Math.Max(1, (bottomRight.X - topLeft.X) * dpiX);
            Height = Math.Max(1, (bottomRight.Y - topLeft.Y) * dpiY);
        }

        public async Task FadeInAsync()
        {
            Show();
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) Services.WindowCorners.ApplyRounded(handle, true);
            BringNavigationToFront();
            _spinner = (Storyboard)Resources["SpinnerStoryboard"];
            _spinner?.Begin(this, true);
            if (Owner != null)
            {
                Owner.LocationChanged += FollowOwner;
                Owner.SizeChanged += FollowOwner;
                Owner.StateChanged += FollowOwner;
                Owner.IsVisibleChanged += (_, __) => FollowOwner(Owner, EventArgs.Empty);
            }
            _viewport.IsVisibleChanged += ViewportVisibilityChanged;
            _visibilityTimer.Tick += (_, __) =>
            {
                    if (_closing || _completed || _viewport == null || Owner == null) return;
                if (!_belongsToCurrentView())
                {
                    _belongsToCurrentViewValue = false;
                    _hiddenAt = DateTime.Now;
                    Hide();
                }
                else if (!_closing)
                {
                    _belongsToCurrentViewValue = true;
                    FollowOwner(this, EventArgs.Empty);
                }
            };
            _visibilityTimer.Start();
            await FadeToAsync(1, 180);
        }

        private void FollowOwner(object? sender, EventArgs e)
        {
            if (_closing || _completed || _viewport == null || Owner == null) return;
            if (_belongsToCurrentView != null && !_belongsToCurrentView()) { Hide(); return; }
            if (!_belongsToCurrentViewValue && (DateTime.Now - _hiddenAt).TotalMilliseconds > 1000) { Close(); return; }
            if (!Owner.IsVisible || Owner.WindowState == WindowState.Minimized || !_viewport.IsVisible || !_viewport.IsLoaded)
            { Hide(); return; }
            var a = _viewport.PointToScreen(new Point(0, 0));
            var b = _viewport.PointToScreen(new Point(_viewport.ActualWidth, _viewport.ActualHeight));
            var source = PresentationSource.FromVisual(_viewport);
            double dx = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
            double dy = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
            Left = a.X * dx; Top = a.Y * dy;
            Width = (b.X - a.X) * dx; Height = (b.Y - a.Y) * dy;
            if (!IsVisible && !_closing && !_completed && _belongsToCurrentViewValue) Show();
            BringNavigationToFront();
        }

        private void BringNavigationToFront()
        {
            if (Owner is MainWindow main) main.BringNavigationToFront();
        }

        private void ViewportVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => FollowOwner(sender, EventArgs.Empty);

        public void SetCompleted(string caption)
        {
            _completed = true;
            if (!_belongsToCurrentViewValue) { Close(); return; }
            StatusText.Text = "注销完成";
            DetailText.Text = caption + "  ·  会话已关闭";
        }

        public async Task FadeOutAndCloseAsync()
        {
            if (_closing) return;
            _closing = true;
            _completed = true;
            _belongsToCurrentViewValue = false;
            _visibilityTimer.Stop();
            await FadeToAsync(0, 220);
            _spinner?.Stop(this);
            Close();
        }

        private Task FadeToAsync(double value, int milliseconds)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            animation.Completed += (_, __) => done.TrySetResult(true);
            BeginAnimation(OpacityProperty, animation);
            return done.Task;
        }
    }
}