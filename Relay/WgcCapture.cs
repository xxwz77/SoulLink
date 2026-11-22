using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SoulLink.Relay
{
#if WGC
    /// <summary>
    /// Windows.Graphics.Capture（WGC）抓屏后端。
    ///
    /// ★ 只在版本化 TFM（net11.0-windows10.0.26100.0，csproj 定义 WGC 符号）下有实现；
    ///   无版本 TFM 下本文件被 #if WGC 排除（不碰 WinRT，给 tools 引用用）。
    ///
    /// ★ 设计（严格按用户约束）：
    ///   · CreateFreeThreaded（不用 Create）：移除 DispatcherQueue 依赖，帧在内部工作线程回调。
    ///   · 帧池容量 2 + 到达即取（TryGetNextFrame）+ 专用泵线程。
    ///   · FrameArrived 回调里**不做 ReadBack**：只排干帧池、保存最新纹理引用（AddRef）、置标志。
    ///   · 版本检测后应用 MinUpdateInterval / IsBorderRequired / IsCursorCaptureEnabled。
    ///   · CreateForWindow 优先，CreateForMonitor 与 BitBlt 作为降级；运行时失败也降级。
    ///   · 生命周期：引用计数 = 观众数 + (录制?1:0)，归零后 30 秒宽限再停。
    ///
    /// ★ 读回（截图路径）：GPU 纹理不能 IDXGISurface::Map（E_INVALIDARG），必须
    ///   staging 纹理 + ID3D11DeviceContext.CopyResource + Map(D3D11_MAP_READ)。
    ///   用 Vortice（与 media-probe 同版本 3.6.2），避免手写 vtable 槽位错位。
    /// </summary>
    internal sealed class WgcCapture : IScreenCapture, IDisposable
    {
        public string Name => "WGC";

        // ── 生命周期 ───────────────────────────────────────────────────────
        private readonly object _lifeGate = new();
        private int _refCount;
        private CancellationTokenSource? _graceCts;
        private volatile bool _running;
        public bool IsActive => _running;

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice? _winrtDevice;
        private Windows.Graphics.Capture.Direct3D11CaptureFramePool? _pool;
        private Windows.Graphics.Capture.GraphicsCaptureSession? _session;
        private IntPtr _stashedTexture;   // 最新帧纹理引用（AddRef 持有）
        private int _stashedWidth, _stashedHeight;
        private long _frameSeq;
        private readonly object _stashLock = new();

        /// <summary>引用计数 +1（观众连接/录制开始）。</summary>
        public void AddRef()
        {
            lock (_lifeGate)
            {
                _refCount++;
                _graceCts?.Cancel();
                _graceCts = null;
            }
            EnsureRunning();
        }

        /// <summary>引用计数 -1；归零后 30 秒宽限再停。</summary>
        public void Release()
        {
            CancellationTokenSource? cts = null;
            lock (_lifeGate)
            {
                if (_refCount > 0) _refCount--;
                if (_refCount == 0)
                {
                    _graceCts = new CancellationTokenSource();
                    cts = _graceCts;
                }
            }
            if (cts != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(30), cts.Token).ConfigureAwait(false); }
                    catch (TaskCanceledException) { return; }
                    Stop();
                });
            }
        }

        private void EnsureRunning()
        {
            if (_running) return;
            try { Start(); } catch { }
        }

        /// <summary>启动常驻 capture session。</summary>
        private void Start()
        {
            if (_running) return;
            try
            {
                if (!CreateDevice()) return;

                // 原生 ID3D11Device → WinRT IDirect3DDevice
                using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
                int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr winrtPtr);
                if (hr < 0) return;
                _winrtDevice = WinRT.ComWrappersSupport.CreateRcwForComObject<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>(winrtPtr);
                Marshal.Release(winrtPtr); // CreateRcwForComObject 已取得自己的引用

                // CreateForWindow 优先，CreateForMonitor 降级
                var item = TryCreateFromWindow() ?? TryCreateFromMonitor();
                if (item == null) return;

                // 帧池必须与捕获目标的实际尺寸一致；前台窗口小于整屏时用整屏会产生空帧/黑帧。
                int w = item.Size.Width, h = item.Size.Height;
                if (w <= 0 || h <= 0) return;

                _pool = Windows.Graphics.Capture.Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winrtDevice,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    new Windows.Graphics.SizeInt32 { Width = w, Height = h });

                _session = _pool.CreateCaptureSession(item);

                // 版本检测后应用属性
                TrySetIsBorderRequired(_session, false);
                TrySetCursorCaptureEnabled(_session, false);
                TrySetMinUpdateInterval(_session, TimeSpan.Zero);

                // ★ 回调里只排干帧池 + 保存最新纹理引用（AddRef）+ 置标志，不做 ReadBack
                _pool.FrameArrived += OnFrameArrived;

                _session.StartCapture();
                _running = true;
            }
            catch
            {
                Stop();
            }
        }

        /// <summary>
        /// FrameArrived 回调（在帧池内部工作线程）：
        /// 排干帧池只留最新一帧，AddRef 持有纹理，不做 ReadBack。
        /// </summary>
        private void OnFrameArrived(Windows.Graphics.Capture.Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                Windows.Graphics.Capture.Direct3D11CaptureFrame? frame;
                while ((frame = sender.TryGetNextFrame()) != null)
                {
                    using (frame)
                    {
                        var surface = frame.Surface;
                        IntPtr surfNative = ((WinRT.IWinRTObject)surface).NativeObject.ThisPtr;
                        Guid iidAccess = new Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
                        int qi = Marshal.QueryInterface(surfNative, ref iidAccess, out IntPtr pAccess);
                        if (qi < 0 || pAccess == IntPtr.Zero) continue;
                        IntPtr texPtr;
                        int gh;
                        try
                        {
                            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(pAccess);
                            Guid iidTex = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                            gh = access.GetInterface(ref iidTex, out texPtr);
                        }
                        finally { Marshal.Release(pAccess); }
                        if (gh < 0 || texPtr == IntPtr.Zero) continue;

                        int fw = frame.ContentSize.Width, fh = frame.ContentSize.Height;

                        // 锁内只做指针交换（AddRef 新的、Release 旧的），不做 ReadBack
                        lock (_stashLock)
                        {
                            if (_stashedTexture != IntPtr.Zero) Marshal.Release(_stashedTexture);
                            _stashedTexture = texPtr;   // GetInterface 已 AddRef，持有它
                            _stashedWidth = fw;
                            _stashedHeight = fh;
                            _frameSeq++;
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>停止并释放帧池/设备/纹理（16MB 显存还回去）。</summary>
        private void Stop()
        {
            _running = false;
            try { if (_session != null) { try { _session.Dispose(); } catch { } _session = null; } } catch { }
            try { if (_pool != null) { try { _pool.FrameArrived -= OnFrameArrived; } catch { } try { _pool.Dispose(); } catch { } _pool = null; } } catch { }
            lock (_stashLock)
            {
                if (_stashedTexture != IntPtr.Zero) { try { Marshal.Release(_stashedTexture); } catch { } _stashedTexture = IntPtr.Zero; }
            }
            try { _winrtDevice?.Dispose(); } catch { }
            _winrtDevice = null;
            try { _context?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            _context = null; _device = null;
        }

        private Windows.Graphics.Capture.GraphicsCaptureItem? TryCreateFromWindow()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            return Windows.Graphics.Capture.GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId((ulong)hwnd));
        }

        private Windows.Graphics.Capture.GraphicsCaptureItem? TryCreateFromMonitor()
        {
            try
            {
                return Windows.Graphics.Capture.GraphicsCaptureItem.TryCreateFromDisplayId(new Windows.Graphics.DisplayId(0));
            }
            catch { return null; }
        }

        private bool CreateDevice()
        {
            try
            {
                var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
                var hr = Vortice.Direct3D11.D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var device, out _, out var context);
                if (!hr.Success || device == null)
                    hr = Vortice.Direct3D11.D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out _, out context);
                if (!hr.Success || device == null) return false;
                _device = device;
                _context = context;
                return true;
            }
            catch { return false; }
        }

        /// <summary>实时原始帧：常驻时只读回最新 GPU 纹理，绝不 JPEG 中转。</summary>
        public byte[]? CaptureBgra(out int width, out int height)
        {
            // 调用方是唯一的视频线程；_running 只读、Start 只在锁外首次触发。
            if (!_running) AddRef();
            DrainFramePool();
            lock (_stashLock)
            {
                if (_stashedTexture == IntPtr.Zero) { width = 0; height = 0; return null; }
                width = _stashedWidth; height = _stashedHeight;
                return ReadBackTexture(_stashedTexture, width, height);
            }
        }

        private void DrainFramePool()
        {
            var pool = _pool;
            if (pool == null) return;
            try
            {
                Windows.Graphics.Capture.Direct3D11CaptureFrame? frame;
                while ((frame = pool.TryGetNextFrame()) != null)
                {
                    using (frame)
                    {
                        IntPtr surface = ((WinRT.IWinRTObject)frame.Surface).NativeObject.ThisPtr;
                        Guid accessIid = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
                        if (Marshal.QueryInterface(surface, ref accessIid, out IntPtr accessPtr) < 0 || accessPtr == IntPtr.Zero) continue;
                        IntPtr texture = IntPtr.Zero;
                        try
                        {
                            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                            Guid textureIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                            if (access.GetInterface(ref textureIid, out texture) < 0 || texture == IntPtr.Zero) continue;
                        }
                        finally { Marshal.Release(accessPtr); }
                        lock (_stashLock)
                        {
                            if (_stashedTexture != IntPtr.Zero) Marshal.Release(_stashedTexture);
                            _stashedTexture = texture;
                            _stashedWidth = frame.ContentSize.Width;
                            _stashedHeight = frame.ContentSize.Height;
                            _frameSeq++;
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 抓一帧 JPEG（截图路径）。优先取缓存最新帧；没有缓存则临时抓一帧读回。
        /// 常驻模式下，缓存帧由泵线程 FrameArrived 持续更新（不做 ReadBack），
        /// 这里才真正读回一次。
        /// </summary>
        public byte[]? CaptureJpeg(int quality, out int width, out int height)
        {
            width = 0;
            height = 0;
            // 有常驻缓存 → 直接读回最新纹理
            lock (_stashLock)
            {
                if (_stashedTexture != IntPtr.Zero)
                {
                    width = _stashedWidth;
                    height = _stashedHeight;
                    var bgra = ReadBackTexture(_stashedTexture, width, height);
                    return bgra == null ? null : EncodeJpeg(bgra, width, height, quality);
                }
            }
            // 无常驻 → 临时单帧抓取
            return CaptureOnce(quality, out width, out height);
        }

        private byte[]? CaptureOnce(int quality, out int width, out int height)
        {
            width = 0; height = 0;
            ID3D11Device? device = null;
            ID3D11DeviceContext? context = null;
            IntPtr winrtPtr = IntPtr.Zero;
            try
            {
                var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
                var hr = Vortice.Direct3D11.D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device, out _, out context);
                if (!hr.Success || device == null)
                    hr = Vortice.Direct3D11.D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out _, out context);
                if (!hr.Success || device == null) return null;

                // 让实例字段指向临时设备，供 ReadBackTexture 用（用完还原）
                _device = device;
                _context = context;

                using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                int ch = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out winrtPtr);
                if (ch < 0) return null;
                var winrtDev = WinRT.ComWrappersSupport.CreateRcwForComObject<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>(winrtPtr);

                var item = TryCreateFromWindow() ?? TryCreateFromMonitor();
                if (item == null) return null;

                int w = item.Size.Width, h = item.Size.Height;
                if (w <= 0 || h <= 0) return null;

                var pool = Windows.Graphics.Capture.Direct3D11CaptureFramePool.CreateFreeThreaded(
                    winrtDev, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                    new Windows.Graphics.SizeInt32 { Width = w, Height = h });
                var sess = pool.CreateCaptureSession(item);
                TrySetIsBorderRequired(sess, false);
                TrySetCursorCaptureEnabled(sess, false);
                sess.StartCapture();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                Windows.Graphics.Capture.Direct3D11CaptureFrame? frame = null;
                while (sw.ElapsedMilliseconds < 5000)
                {
                    frame = pool.TryGetNextFrame();
                    if (frame != null) break;
                    Thread.Sleep(5);
                }
                if (frame == null) return null;

                using (frame)
                {
                    width = frame.ContentSize.Width;
                    height = frame.ContentSize.Height;
                    IntPtr surfNative = ((WinRT.IWinRTObject)frame.Surface).NativeObject.ThisPtr;
                    Guid iidAccess = new Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
                    int qi = Marshal.QueryInterface(surfNative, ref iidAccess, out IntPtr pAccess);
                    if (qi < 0 || pAccess == IntPtr.Zero) return null;
                    IntPtr texPtr;
                    try
                    {
                        var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(pAccess);
                        Guid iidTex = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                        int gh = access.GetInterface(ref iidTex, out texPtr);
                        if (gh < 0 || texPtr == IntPtr.Zero) return null;
                    }
                    finally { Marshal.Release(pAccess); }

                    try
                    {
                        var bgra = ReadBackTexture(texPtr, width, height);
                        return bgra == null ? null : EncodeJpeg(bgra, width, height, quality);
                    }
                    finally { Marshal.Release(texPtr); }
                }
            }
            catch { return null; }
            finally
            {
                if (winrtPtr != IntPtr.Zero) Marshal.Release(winrtPtr);
                _device = null; _context = null;
                context?.Dispose();
                device?.Dispose();
            }
        }

        /// <summary>GPU 纹理 → CPU BGRA（staging + CopyResource + Map）。</summary>
        private byte[]? ReadBackTexture(IntPtr texturePtr, int width, int height)
        {
            try
            {
                if (_device == null || _context == null) return null;
                var srcTex = new ID3D11Texture2D(texturePtr);
                var desc = new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                };
                var staging = _device.CreateTexture2D(desc);
                try
                {
                    _context.CopyResource(staging, srcTex);
                    var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        int rowPitch = (int)mapped.RowPitch;
                        var tight = new byte[width * 4 * height];
                        if (rowPitch == width * 4)
                        {
                            Marshal.Copy(mapped.DataPointer, tight, 0, tight.Length);
                        }
                        else
                        {
                            for (int y = 0; y < height; y++)
                                Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * rowPitch), tight, y * width * 4, width * 4);
                        }
                        return tight;
                    }
                    finally { _context.Unmap(staging, 0); }
                }
                finally { staging.Dispose(); }
            }
            catch { return null; }
        }

        private byte[]? EncodeJpeg(byte[] bgra, int width, int height, int quality)
        {
            try
            {
                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try { Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length); }
                finally { bmp.UnlockBits(bd); }
                using var ms = new MemoryStream();
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                bmp.Save(ms, JpegCodecStatic(), ep);
                return ms.ToArray();
            }
            catch { return null; }
        }

        private static ImageCodecInfo? _jpegStatic;
        private static ImageCodecInfo JpegCodecStatic()
        {
            if (_jpegStatic != null) return _jpegStatic;
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) { _jpegStatic = c; break; }
            return _jpegStatic ?? throw new InvalidOperationException("找不到 JPEG 编码器");
        }

        // ── 版本检测后的属性设置 ───────────────────────────────────────────
        private static void TrySetIsBorderRequired(Windows.Graphics.Capture.GraphicsCaptureSession s, bool v)
        {
            try { s.IsBorderRequired = v; } catch { }
        }

        private static void TrySetCursorCaptureEnabled(Windows.Graphics.Capture.GraphicsCaptureSession s, bool v)
        {
            try { s.IsCursorCaptureEnabled = v; } catch { }
        }

        private static void TrySetMinUpdateInterval(Windows.Graphics.Capture.GraphicsCaptureSession s, TimeSpan ts)
        {
            try
            {
                var prop = s.GetType().GetProperty("MinUpdateInterval");
                if (prop != null && prop.CanWrite) prop.SetValue(s, ts);
            }
            catch { }
        }

        // ── P/Invoke ────────────────────────────────────────────────────────
        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

        [ComImport, Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess { [PreserveSig] int GetInterface([In] ref Guid iid, [Out] out IntPtr pObject); }

        public void Dispose() => Stop();
    }
#else
    // 无版本 TFM：不引用 WinRT/Vortice。文件为空（类型在版本化 TFM 才有）。
#endif
}
