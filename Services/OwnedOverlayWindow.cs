using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SoulLink.Services
{
    /// <summary>
    /// A non-topmost, owner-bound HWND overlay for WindowsFormsHost / ActiveX airspace.
    ///
    /// WPF ZIndex cannot cover an HWND child. A normal Popup can cover it, but its
    /// top-level window may remain above unrelated applications. This window instead
    /// uses a real Win32 owner, never activates, and follows the owner's z-order.
    /// Coordinates passed to SetWindowPos are physical pixels, never DIP.
    /// </summary>
    public sealed class OwnedOverlayWindow : Window
    {
        private const int GwlExstyle = -20;
        private const int GwlHwndparent = -8;
        private const int WsExToolwindow = 0x00000080;
        private const int WsExNoactivate = 0x08000000;
        private const uint SwpNozorder = 0x0004;
        private const uint SwpNoactivate = 0x0010;
        private const uint SwpShowwindow = 0x0040;
        private const int SwHide = 0;
        private const int SwShownoactivate = 4;
        private const int WmMouseactivate = 0x0021;
        private static readonly IntPtr MaNoactivate = new IntPtr(3);

        private readonly Window _owner;
        private readonly FrameworkElement _anchor;
        private readonly string _diagnosticName;
        private readonly bool _allowInactiveOwner;
        private bool _requested;
        private bool _closed;
        private double? _widthOverride;
        private double? _heightOverride;

        public OwnedOverlayWindow(Window owner, FrameworkElement anchor, UIElement content, string diagnosticName, bool allowInactiveOwner = false)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
            _diagnosticName = string.IsNullOrWhiteSpace(diagnosticName) ? "OwnedOverlay" : diagnosticName;
            _allowInactiveOwner = allowInactiveOwner;

            WindowStyle = WindowStyle.None;
            // WPF's Owner property can create a native parent relationship for a
            // WS_POPUP window. Real Win32 ownership is applied explicitly below.
            Content = content ?? throw new ArgumentNullException(nameof(content));
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SizeToContent = SizeToContent.Manual;

            SourceInitialized += OnSourceInitialized;
            Loaded += (_, __) => Dispatcher.BeginInvoke(new Action(ReassertNativeOwner));
            _owner.Activated += OnOwnerChanged;
            _owner.Deactivated += OnOwnerDeactivated;
            _owner.LocationChanged += OnOwnerChanged;
            _owner.SizeChanged += OnOwnerChanged;
            _owner.StateChanged += OnOwnerChanged;
            _owner.IsVisibleChanged += OnOwnerVisibilityChanged;
            _owner.Closed += OnOwnerClosed;
            _anchor.SizeChanged += OnAnchorSizeChanged;
        }

        public bool IsRequested => _requested;
        public bool NativeVisible { get; private set; }

        /// <summary>在同一 Owner 的浮层中置顶，不使用全局 Topmost。</summary>
        public void BringToFront()
        {
            if (_closed) return;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0,
                0x0001 | 0x0002 | SwpNoactivate); // HWND_TOP, NOSIZE | NOMOVE
            Topmost = false; // owned, non-topmost window remains below other apps
        }

        /// <summary>Request visibility. It is still suppressed while the owner is inactive.</summary>
        public void ShowOverlay(double? widthDip = null, double? heightDip = null)
        {
            _requested = true;
            _widthOverride = widthDip;
            _heightOverride = heightDip;
            SyncToAnchor();
        }

        public void UpdateOverlaySize(double? widthDip = null, double? heightDip = null)
        {
            _widthOverride = widthDip;
            _heightOverride = heightDip;
            SyncToAnchor();
        }

        /// <summary>Hide without discarding content or the requested state.</summary>
        public void HideForOwnerState()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero && (NativeVisible || IsWindowVisible(handle)))
                    ShowWindow(handle, SwHide);
                NativeVisible = false;
            }
            catch (Exception ex) { Log(ex); }
        }

        /// <summary>Cancel the request and hide immediately.</summary>
        public void HideOverlay()
        {
            _requested = false;
            Trace("HideOverlay", false);
            HideForOwnerState();
        }

        public void SyncToAnchor()
        {
            if (_closed || !_requested) return;
            try
            {
                if (!CanDisplay())
                {
                    // Move/resize can temporarily change owner activation. Keep the
                    // existing bounds until the deferred sync, avoiding a geometry jump.
                    if (IsVisible && _owner.IsVisible && _owner.IsActive
                        && _owner.WindowState != WindowState.Minimized && _anchor.IsVisible)
                        return;
                    HideForOwnerState();
                    return;
                }

                var source = PresentationSource.FromVisual(_anchor);
                var transform = source?.CompositionTarget?.TransformToDevice;
                if (transform == null || transform.Value.M11 <= 0 || transform.Value.M22 <= 0)
                {
                    HideForOwnerState();
                    return;
                }

                double widthDip = _widthOverride ?? _anchor.ActualWidth;
                double heightDip = _heightOverride ?? _anchor.ActualHeight;
                if (widthDip < 1 || heightDip < 1 || _anchor.ActualWidth < 1 || _anchor.ActualHeight < 1)
                {
                    HideForOwnerState();
                    return;
                }

                var topLeft = _anchor.PointToScreen(new Point(0, 0));
                int x = (int)Math.Round(topLeft.X);
                int y = (int)Math.Round(topLeft.Y);
                int width = Math.Max(1, (int)Math.Round(widthDip * transform.Value.M11));
                int height = Math.Max(1, (int)Math.Round(heightDip * transform.Value.M22));
                if (x < -20000 || y < -20000)
                {
                    HideForOwnerState();
                    return;
                }

                // Set initial DIP values for the first layout pass. SetWindowPos below
                // is authoritative and corrects both position and size in device pixels.
                Width = widthDip;
                Height = heightDip;
                if (!IsVisible)
                {
                    Left = x / transform.Value.M11;
                    Top = y / transform.Value.M22;
                    Show();
                    ReassertNativeOwner();
                }

                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero) return;
                SetWindowPos(handle, IntPtr.Zero, x, y, width, height,
                    SwpNozorder | SwpNoactivate | (NativeVisible ? 0 : SwpShowwindow));
                // WPF Window.IsVisible may still be true after SW_HIDE (notably after
                // logoff disposes/recreates the RDP ActiveX). Restore the existing
                // HWND explicitly; do not rely on Window.Show() being called again.
                if (!NativeVisible) ShowWindow(handle, SwShownoactivate);
                NativeVisible = IsWindowVisible(handle);
            }
            catch (Exception ex) { Log(ex); }
        }

        private bool CanDisplay()
        {
            return _owner.IsVisible && (_owner.IsActive || _allowInactiveOwner)
                   && _owner.WindowState != WindowState.Minimized
                   && _anchor.IsVisible && _anchor.IsLoaded;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                var ownerHandle = new WindowInteropHelper(_owner).Handle;
                if (handle == IntPtr.Zero || ownerHandle == IntPtr.Zero)
                    throw new InvalidOperationException("Overlay or owner HWND is unavailable.");
                // WPF's Window.Owner may set a native *parent* rather than the
                // owner of an overlapped window. A child of the WPF host would be
                // clipped to it and would not acquire owned-window z-order semantics.
                // Set GWLP_HWNDPARENT explicitly: for WS_OVERLAPPED this is the owner.
                if (GetWindowLongPtr(handle, GwlHwndparent) != ownerHandle)
                    SetWindowLongPtr(handle, GwlHwndparent, ownerHandle);
                long style = GetWindowLongPtr(handle, GwlExstyle).ToInt64();
                style |= WsExNoactivate | WsExToolwindow;
                SetWindowLongPtr(handle, GwlExstyle, new IntPtr(style));
                if (HwndSource.FromHwnd(handle) is HwndSource source)
                {
                    source.DpiChanged += (_, __) => Dispatcher.BeginInvoke(new Action(SyncToAnchor));
                    source.AddHook(OverlayWndProc);
                }
            }
            catch (Exception ex) { Log(ex); }
        }

        private IntPtr OverlayWndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmMouseactivate)
            {
                handled = true;
                return MaNoactivate;
            }
            return IntPtr.Zero;
        }

        private void ReassertNativeOwner()
        {
            if (_closed) return;
            var handle = new WindowInteropHelper(this).Handle;
            var ownerHandle = new WindowInteropHelper(_owner).Handle;
            if (handle != IntPtr.Zero && ownerHandle != IntPtr.Zero
                && GetWindowLongPtr(handle, GwlHwndparent) != ownerHandle)
                SetWindowLongPtr(handle, GwlHwndparent, ownerHandle);
        }

        private void OnOwnerChanged(object? sender, EventArgs e)
        {
            ReassertNativeOwner();
            // Owner.Deactivated is synchronous; do not defer hiding to a timer.
            if (!_owner.IsActive && !_allowInactiveOwner)
            {
                HideForOwnerState();
                return;
            }
            SyncToAnchor();
            Dispatcher.BeginInvoke(new Action(SyncToAnchor));
        }

        private void OnOwnerDeactivated(object? sender, EventArgs e)
        {
            if (!_allowInactiveOwner) HideForOwnerState();
        }

        private void OnOwnerVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            if (!_owner.IsVisible) HideForOwnerState();
            else SyncToAnchor();
        }

        private void OnAnchorSizeChanged(object sender, SizeChangedEventArgs e) => SyncToAnchor();

        private void OnOwnerClosed(object? sender, EventArgs e)
        {
            _requested = false;
            if (!_closed) Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            _requested = false;
            _owner.Activated -= OnOwnerChanged;
            _owner.Deactivated -= OnOwnerDeactivated;
            _owner.LocationChanged -= OnOwnerChanged;
            _owner.SizeChanged -= OnOwnerChanged;
            _owner.StateChanged -= OnOwnerChanged;
            _owner.IsVisibleChanged -= OnOwnerVisibilityChanged;
            _owner.Closed -= OnOwnerClosed;
            _anchor.SizeChanged -= OnAnchorSizeChanged;
            Content = null;
            base.OnClosed(e);
        }

        private void Trace(string stage, bool stack)
        {
            if (!SoulLink.Controls.RdpConsole.VerboseLog) return;
            try
            {
                string text = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + _diagnosticName + "] " + stage
                    + " requested=" + _requested + " visible=" + IsVisible
                    + " ownerActive=" + _owner.IsActive + " ownerVisible=" + _owner.IsVisible
                    + " anchorVisible=" + _anchor.IsVisible + " anchorLoaded=" + _anchor.IsLoaded
                    + (stack ? "\n" + Environment.StackTrace : "");
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SoulLink-Overlay-Diagnostics");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "overlay-window.log"), text + Environment.NewLine);
            }
            catch { }
        }

        private void Log(Exception ex) => App.AppendErrorLog(_diagnosticName, ex);

        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

        private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
            => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, value);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern IntPtr GetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern IntPtr SetWindowLong32(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);
    }
}
