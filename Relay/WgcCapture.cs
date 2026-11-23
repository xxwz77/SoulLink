using System;
using System.Collections.Concurrent;
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
    /// ★ 设计（2026-11-23 黑闪根治，对齐 BGI GraphicsCaptureV2.cs 检出模型）：
    ///   · A（stash 帧对象而非纹理裸指针）：WGC 帧池 buffer 的检出语义挂在帧对象上，
    ///     帧对象存活期=检出期，DWM 不会复写该 buffer；消费侧检出期内 CopyResource 到
    ///     私有 _gpuTexture 后立即 Dispose 检入，读回只面向私有纹理。
    ///   · B（Recreate 状态机 + 帧池代数 + IsIconic + 泵线程）：ContentSize 变化检测，
    ///     WinRT 对象线程亲和（Win10 跨线程 Recreate 抛 RPC_E_WRONG_THREAD）统一路由泵线程。
    ///   · E（黑帧过滤降级为 warmup 门）：仅会话首 30 帧丢弃全黑过渡帧，稳态放行真实暗内容。
    ///   · 帧池容量 2 + 到达即取（TryGetNextFrame）。
    ///   · CreateForWindow 优先，CreateForMonitor 降级；运行时失败也降级。
    ///   · 生命周期：引用计数 = 观众数 + (录制?1:0)，归零后 30 秒宽限再停。
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
        private long _frameSeq;
        public long FrameSequence { get { lock (_stashLock) return _frameSeq; } }
        private long _lastBlackFrameLog;

        /// <summary>
        /// ★ P1-A2：抓屏区域左上角在虚拟桌面的物理像素坐标（网页 0~1 坐标反算基准）。
        /// 窗口捕获取窗口 rect（实时，窗口拖动后立即正确）；monitor 捕获取主显示器 rcMonitor。
        /// </summary>
        private bool TryGetCaptureOrigin(out int x, out int y)
        {
            x = y = 0;
            if (_hWnd != IntPtr.Zero)
            {
                if (!GetWindowRect(_hWnd, out RECT r)) return false;
                x = r.Left; y = r.Top;
                return true;
            }
            // monitor 降级：DisplayId(0) 即主显示器，取其 rcMonitor 虚拟坐标
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY), ref info)) return false;
            x = info.rcMonitor.Left; y = info.rcMonitor.Top;
            return true;
        }

        /// <summary>★ P1-A2：抓屏区域左上角的虚拟桌面物理 X。</summary>
        public int CaptureOriginX { get { TryGetCaptureOrigin(out int x, out _); return x; } }

        /// <summary>★ P1-A2：抓屏区域左上角的虚拟桌面物理 Y。</summary>
        public int CaptureOriginY { get { TryGetCaptureOrigin(out _, out int y); return y; } }

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice? _winrtDevice;
        private Windows.Graphics.Capture.Direct3D11CaptureFramePool? _pool;
        private Windows.Graphics.Capture.GraphicsCaptureSession? _session;
        private Windows.Graphics.Capture.GraphicsCaptureItem? _captureItem;

        // ★ A：stash 帧对象（而非纹理裸指针）——WGC 帧池 buffer 的检出语义挂在帧对象上，
        //   帧对象存活期内 buffer 不被 DWM 复写。stash 帧对象 = 持有检出期。
        private Windows.Graphics.Capture.Direct3D11CaptureFrame? _stashedFrame;
        private long _stashedGeneration;   // stash 帧所属帧池代数（与 _stashedFrame 成对读写）
        private long _poolGeneration;       // 帧池代数：每次 Recreate 自增
        private readonly object _stashLock = new();

        // ★ A：私有 GPU 纹理（尺寸=池尺寸）。消费侧检出期内 CopyResource 写入，读回只面向它。
        private ID3D11Texture2D? _gpuTexture;
        private int _gpuWidth, _gpuHeight;
        private long _gpuFrameTs;           // _gpuTexture 内容对应的帧时间戳（0=无有效帧）

        // ★ B：帧池真实尺寸（item.Size，非 ContentSize）
        private Windows.Graphics.SizeInt32 _poolSize;
        private IntPtr _hWnd;               // 捕获目标窗口句柄（monitor 降级时为 0，跳过 IsIconic 保护）

        // 消费侧串行化（单飞闸门）：CaptureBgra/CaptureJpeg 与 Stop 销毁 _gpuTexture 互斥
        private readonly object _captureGate = new();

        // ★ B：WGC 泵线程（WinRT 对象线程亲和：Win10 上跨线程 Recreate 抛 RPC_E_WRONG_THREAD）
        private Thread? _wgcPumpThread;
        private BlockingCollection<Action> _wgcActions = new();
        private const int WgcDispatchTimeoutSeconds = 2;

        // ★ E：warmup 门诊断计数
        private long _droppedBlackFrames;

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

        /// <summary>启动常驻 capture session（WinRT 对象全部路由泵线程）。</summary>
        private void Start()
        {
            if (_running) return;
            try
            {
                RunOnWgcThread(StartOnPumpThread);
                _running = true;
            }
            catch
            {
                Stop();
            }
        }

        private void StartOnPumpThread()
        {
            if (!CreateDevice())
                throw new InvalidOperationException("D3D11 设备失败");

            // 原生 ID3D11Device → WinRT IDirect3DDevice
            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr winrtPtr);
            if (hr < 0) throw new InvalidOperationException("CreateDirect3D11DeviceFromDXGIDevice 失败 0x" + hr.ToString("X8"));
            _winrtDevice = WinRT.ComWrappersSupport.CreateRcwForComObject<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>(winrtPtr);
            Marshal.Release(winrtPtr); // CreateRcwForComObject 已取得自己的引用

            // CreateForWindow 优先，CreateForMonitor 降级
            IntPtr hwnd = GetForegroundWindow();
            var item = TryCreateFromWindowId(hwnd);
            if (item == null) { item = TryCreateFromMonitor(); hwnd = IntPtr.Zero; }
            if (item == null) throw new InvalidOperationException("无捕获目标");
            _captureItem = item;
            _hWnd = hwnd;

            // 帧池必须与捕获目标的实际尺寸一致；前台窗口小于整屏时用整屏会产生空帧/黑帧。
            int w = item.Size.Width, h = item.Size.Height;
            if (w <= 0 || h <= 0) throw new InvalidOperationException("捕获目标尺寸无效");
            _poolSize = new Windows.Graphics.SizeInt32 { Width = w, Height = h };

            _pool = Windows.Graphics.Capture.Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _poolSize);

            _session = _pool.CreateCaptureSession(item);

            // 版本检测后应用属性
            TrySetIsBorderRequired(_session, false);
            // ★ P0（2026-11-24，AUDIT-20261124-03 §4.1 C1）：光标由系统合成进视频帧
            //   （Sunshine display_wgc.cpp 同款默认行为）；网页端已删虚拟蓝球，依赖帧内光标。
            //   远端应用全屏隐藏光标（游戏视频）时帧内光标自动消失，是正确行为而非回退。
            if (TrySetCursorCaptureEnabled(_session, true))
                RelayAgent.LogQuiet("[capture] WGC 光标合成（IsCursorCaptureEnabled=true）已生效");
            TrySetMinUpdateInterval(_session, TimeSpan.Zero);

            _pool.FrameArrived += OnFrameArrived;

            _session.StartCapture();
            RelayAgent.LogQuiet("[capture] WGC 建立成功：" + w + "x" + h);
        }

        /// <summary>
        /// ★ A：FrameArrived 回调（帧池内部工作线程）——只做帧对象检出/检入（WinRT 轻量操作），
        /// 不触碰 D3D11 设备上下文。stash 帧对象（非纹理裸指针），被替换的旧帧立即 Dispose 检入。
        /// </summary>
        private void OnFrameArrived(Windows.Graphics.Capture.Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                Windows.Graphics.Capture.Direct3D11CaptureFrame? frame;
                while ((frame = sender.TryGetNextFrame()) != null)
                {
                    // 代数必须在取帧后、入 stash 前读：Recreate 与本回调并发时，
                    // 横跨 Recreate 的旧尺寸帧会盖上旧代数而被消费侧丢弃。
                    var generation = Interlocked.Read(ref _poolGeneration);
                    Windows.Graphics.Capture.Direct3D11CaptureFrame? replaced;
                    lock (_stashLock)
                    {
                        replaced = _stashedFrame;
                        _stashedFrame = frame;
                        _stashedGeneration = generation;
                        _frameSeq++;
                    }
                    replaced?.Dispose();   // 被替换的旧帧检入归还池（buffer 交还 DWM）
                }
            }
            catch { }
        }

        /// <summary>停止并释放帧池/会话/私有纹理（WinRT 销毁路由泵线程）。</summary>
        private void Stop()
        {
            _running = false;
            try { RunOnWgcThread(StopOnPumpThread); } catch { }
            try { _wgcActions.CompleteAdding(); } catch { }

            // 清空 stash（池已销毁，帧对象只剩 COM 释放）
            lock (_stashLock)
            {
                _stashedFrame?.Dispose();
                _stashedFrame = null;
            }

            // 销毁私有纹理（与消费侧读回互斥）
            lock (_captureGate)
            {
                _gpuTexture?.Dispose();
                _gpuTexture = null;
                _gpuWidth = _gpuHeight = 0;
                _gpuFrameTs = 0;
            }

            try { _winrtDevice?.Dispose(); } catch { }
            _winrtDevice = null;
            try { _context?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            _context = null; _device = null;
            _captureItem = null;
        }

        private void StopOnPumpThread()
        {
            try { if (_session != null) { try { _session.Dispose(); } catch { } _session = null; } } catch { }
            try { if (_pool != null) { try { _pool.FrameArrived -= OnFrameArrived; } catch { } try { _pool.Dispose(); } catch { } _pool = null; } } catch { }
            _captureItem = null;
        }

        private static Windows.Graphics.Capture.GraphicsCaptureItem? TryCreateFromWindowId(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            try { return Windows.Graphics.Capture.GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId((ulong)hwnd)); }
            catch { return null; }
        }

        private static Windows.Graphics.Capture.GraphicsCaptureItem? TryCreateFromMonitor()
        {
            try { return Windows.Graphics.Capture.GraphicsCaptureItem.TryCreateFromDisplayId(new Windows.Graphics.DisplayId(0)); }
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

        // ── 泵线程 ─────────────────────────────────────────────────────────
        private void EnsureWgcPumpThread()
        {
            if (_wgcPumpThread != null && _wgcPumpThread.IsAlive && !_wgcActions.IsAddingCompleted) return;
            // 旧集合不 Dispose：旧泵线程可能仍在排干最后的动作，Dispose 会让枚举抛异常击穿泵循环。
            _wgcActions = new BlockingCollection<Action>();
            _wgcPumpThread = new Thread(WgcPumpLoop) { IsBackground = true, Name = "SoulLinkWgcCapture" };
            _wgcPumpThread.Start();
        }

        private void WgcPumpLoop()
        {
            foreach (var action in _wgcActions.GetConsumingEnumerable()) action();
        }

        /// <summary>在 WGC 专属泵线程上同步执行（FFmpeg run_on_wgc_thread 同款），
        /// 绝不退化为调用线程内联（跨线程抛 RPC_E_WRONG_THREAD）。</summary>
        private void RunOnWgcThread(Action action)
        {
            if (Thread.CurrentThread == _wgcPumpThread) { action(); return; }
            EnsureWgcPumpThread();
            var done = new ManualResetEventSlim(false);
            var cancelled = false;
            Exception? error = null;
            _wgcActions.Add(() =>
            {
                try { if (!cancelled) action(); }
                catch (Exception e) { error = e; }
                finally { done.Set(); }
            });
            if (!done.Wait(TimeSpan.FromSeconds(WgcDispatchTimeoutSeconds)))
            {
                cancelled = true;
                throw new TimeoutException("WGC 泵线程执行超时（" + WgcDispatchTimeoutSeconds + "s）");
            }
            if (error != null) throw error;
        }

        // ── 消费侧 ─────────────────────────────────────────────────────────
        /// <summary>实时原始帧：消费侧检出期内拷贝到私有纹理，读回只面向私有纹理。</summary>
        public byte[]? CaptureBgra(out int width, out int height)
            => ConsumeBgra(true, out width, out height);

        /// <summary>
        /// 零拷贝（阶段B）：返回私有 _gpuTexture（B8G8R8A8，已在显存）+ 其跨设备共享句柄，不读回 CPU。
        /// 供 GpuColorConverter 在编码器设备上 OpenSharedResource + VideoProcessorBlt 转 NV12。
        /// 无有效帧时返回 false（sharedHandle=0，width/height=0）。
        /// </summary>
        public bool TryCaptureBgraTexture(out IntPtr sharedHandle, out int width, out int height)
        {
            sharedHandle = IntPtr.Zero; width = 0; height = 0;
            if (!_running) return false;
            lock (_captureGate)
            {
                var gpuTex = _gpuTexture;
                if (gpuTex == null || _gpuFrameTs == 0) return false;
                try
                {
                    using var dxgiRes = gpuTex.QueryInterface<IDXGIResource>();
                    sharedHandle = dxgiRes.SharedHandle;
                    if (sharedHandle == IntPtr.Zero) return false;
                }
                catch { return false; }
                width = _gpuWidth; height = _gpuHeight;
                return true;
            }
        }

        /// <summary>
        /// 抓一帧 JPEG（截图路径）。常驻会话运行时读私有纹理；无常驻则临时单帧抓取。
        /// </summary>
        public byte[]? CaptureJpeg(int quality, out int width, out int height)
        {
            width = 0; height = 0;
            if (_running)
            {
                var bgra = ConsumeBgra(false, out width, out height);
                return bgra == null ? null : EncodeJpeg(bgra, width, height, quality);
            }
            // 无常驻 → 临时单帧抓取
            return CaptureOnce(quality, out width, out height);
        }

        /// <summary>
        /// 取走 stash 帧 → 检出期内 CopyResource 到私有 _gpuTexture → 检入 → 读回私有纹理。
        /// ensureRunning=true 时（视频线程）无会话则启动；false 时（快照）无会话返回 null。
        /// </summary>
        private byte[]? ConsumeBgra(bool ensureRunning, out int width, out int height)
        {
            width = 0; height = 0;
            if (ensureRunning && !_running) AddRef();
            if (!_running) return null;

            lock (_captureGate)
            {
                // 阶段0：取走 stash 帧（到达线程写的最新帧），检出期内拷贝到私有纹理后检入
                Windows.Graphics.Capture.Direct3D11CaptureFrame? frame;
                long generation;
                lock (_stashLock)
                {
                    frame = _stashedFrame;
                    _stashedFrame = null;
                    generation = _stashedGeneration;
                }
                if (frame != null)
                {
                    try { ProcessStashedFrame(frame, generation); }
                    catch { }
                    finally { frame.Dispose(); }   // 检入归还池（渲染已完成，buffer 交还 DWM）
                }

                var device = _device;
                var context = _context;
                var gpuTex = _gpuTexture;
                if (gpuTex == null || device == null || context == null || _gpuFrameTs == 0)
                    return null;   // 尚未收到有效帧

                width = _gpuWidth; height = _gpuHeight;
                var bgra = ReadBackTexture(device, context, gpuTex, width, height);
                if (bgra == null) return null;

                // ★ E：warmup 门——仅会话首 30 帧丢弃全黑过渡帧（DWM 起始黑过渡）；
                //   稳态放行真实暗内容（不误杀加载/暗场），只计数 + 节流日志。
                if (FrameSequence < 30 && IsMostlyBlack(bgra, width, height))
                {
                    _droppedBlackFrames++;
                    long now = Environment.TickCount64;
                    if (now - _lastBlackFrameLog >= 5000)
                    {
                        _lastBlackFrameLog = now;
                        RelayAgent.LogQuiet("[capture] WGC warmup 丢弃黑帧（seq=" + FrameSequence + " dropped=" + _droppedBlackFrames + "）");
                    }
                    width = height = 0;
                    return null;
                }
                return bgra;
            }
        }

        /// <summary>
        /// 消费侧处理 stash 帧：代数守卫 → ContentSize 变化检测/Recreate → 检出期内拷贝到私有纹理。
        /// 必须在帧对象 Dispose（检入）前调用；调用方保证帧在检出期内。
        /// </summary>
        private void ProcessStashedFrame(Windows.Graphics.Capture.Direct3D11CaptureFrame frame, long generation)
        {
            // Recreate 竞态守卫：旧帧池（Recreate 前）的遗留帧直接丢弃，避免旧尺寸帧反向 Recreate
            if (generation != Interlocked.Read(ref _poolGeneration)) return;

            var contentSize = frame.ContentSize;

            // ★ B：ContentSize 变化 → Recreate + 帧池代数自增（FFmpeg gfxcapture 同款）
            if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
            {
                // 最小化窗口的 ContentSize 会缩成图标尺寸，此时不 Recreate：保留旧资源与缓存帧，
                // 窗口还原后尺寸再变化时自然恢复
                if (_hWnd != IntPtr.Zero && IsIconic(_hWnd)) return;

                var pool = _pool;
                var winrtDevice = _winrtDevice;
                if (pool == null || winrtDevice == null) return;

                try
                {
                    // Recreate 必须在帧池创建线程（泵线程）执行；失败异常兜底记录并跳过本帧
                    RunOnWgcThread(() =>
                    {
                        pool.Recreate(winrtDevice, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
                        Interlocked.Increment(ref _poolGeneration);   // 此后到达的帧属新池
                    });
                }
                catch (Exception e)
                {
                    // Recreate 失败必须响亮：作废私有纹理与时间戳，让上层感知（返回空），下帧重试
                    _gpuTexture?.Dispose();
                    _gpuTexture = null;
                    _gpuWidth = _gpuHeight = 0;
                    _gpuFrameTs = 0;
                    RelayAgent.LogQuiet("[capture] WGC Recreate 失败：" + e.GetType().Name + "：" + e.Message);
                    return;
                }

                RelayAgent.LogQuiet("[capture] WGC Recreate：" + _poolSize.Width + "x" + _poolSize.Height
                    + " -> " + contentSize.Width + "x" + contentSize.Height);
                _poolSize = contentSize;
                _gpuTexture?.Dispose();
                _gpuTexture = null;
                _gpuWidth = _gpuHeight = 0;
                _gpuFrameTs = 0;
                // 本帧 surface 仍是 Recreate 前旧尺寸，跳过，待新尺寸帧到达再渲染
                return;
            }

            // 检出期内：帧 Surface → 私有纹理（CopyResource 0.1ms 级；源=池尺寸，目标=池尺寸恒一致）
            using var srcTex = GetSurfaceTexture(frame);
            if (srcTex == null) return;
            EnsureGpuTexture(contentSize.Width, contentSize.Height);
            var gpuTex = _gpuTexture;
            var context = _context;
            if (gpuTex == null || context == null) return;
            context.CopyResource(gpuTex, srcTex);
            _gpuFrameTs = frame.SystemRelativeTime.Ticks;
        }

        /// <summary>私有 GPU 纹理（DEFAULT usage，尺寸=池尺寸），尺寸变化时惰性重建。</summary>
        private void EnsureGpuTexture(int width, int height)
        {
            if (_gpuTexture != null && _gpuWidth == width && _gpuHeight == height) return;
            _gpuTexture?.Dispose();
            _gpuTexture = null;
            var device = _device;
            if (device == null || width <= 0 || height <= 0) return;
            _gpuTexture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.None,
                // ★ 零拷贝：Shared 使本纹理可被同进程其他 D3D11 设备打开（跨设备 CopyResource）。
                //   不影响现有 CopyResource/读回路径。
                MiscFlags = ResourceOptionFlags.Shared
            });
            _gpuWidth = width; _gpuHeight = height;
        }

        /// <summary>帧 Surface → ID3D11Texture2D（AddRef 过，调用方 using 释放）。</summary>
        private static ID3D11Texture2D? GetSurfaceTexture(Windows.Graphics.Capture.Direct3D11CaptureFrame frame)
        {
            try
            {
                var surface = frame.Surface;
                IntPtr surfNative = ((WinRT.IWinRTObject)surface).NativeObject.ThisPtr;
                Guid iidAccess = new Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
                if (Marshal.QueryInterface(surfNative, ref iidAccess, out IntPtr pAccess) < 0 || pAccess == IntPtr.Zero) return null;
                try
                {
                    var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(pAccess);
                    Guid iidTex = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                    if (access.GetInterface(ref iidTex, out IntPtr texPtr) < 0 || texPtr == IntPtr.Zero) return null;
                    // ★ SharpGen ComObject(IntPtr) 构造不 AddRef（只存指针），Dispose 才 Release：
                    //   此处把 GetInterface 给的 +1 引用转移给包装对象，调用方 using 释放时 Release。
                    //   绝不能再 Marshal.Release(texPtr)，否则 using Dispose 二次释放导致引用计数下溢。
                    return new ID3D11Texture2D(texPtr);
                }
                finally { Marshal.Release(pAccess); }
            }
            catch { return null; }
        }

        /// <summary>只检查 WGC 读回帧；自适应步长保证采样少于10万点，不做逐像素全扫。</summary>
        private static bool IsMostlyBlack(byte[] bgra, int width, int height)
        {
            if (width <= 0 || height <= 0) return false;
            int cellsX = (width + 7) / 8, cellsY = (height + 7) / 8;
            int stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(cellsX * (double)cellsY / 90000.0)));
            int samples = 0, black = 0;
            for (int y = 0; y < height; y += 8 * stride)
            for (int x = 0; x < width; x += 8 * stride)
            {
                int p = (y * width + x) * 4;
                samples++;
                if (bgra[p] == 0 && bgra[p + 1] == 0 && bgra[p + 2] == 0) black++;
            }
            return samples > 0 && black * 2 > samples;
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

                using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                int ch = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out winrtPtr);
                if (ch < 0) return null;
                var winrtDev = WinRT.ComWrappersSupport.CreateRcwForComObject<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>(winrtPtr);

                var item = TryCreateFromWindowId(GetForegroundWindow()) ?? TryCreateFromMonitor();
                if (item == null) return null;

                int w = item.Size.Width, h = item.Size.Height;
                if (w <= 0 || h <= 0) return null;

                var pool = Windows.Graphics.Capture.Direct3D11CaptureFramePool.CreateFreeThreaded(
                    winrtDev, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                    new Windows.Graphics.SizeInt32 { Width = w, Height = h });
                var sess = pool.CreateCaptureSession(item);
                TrySetIsBorderRequired(sess, false);
                TrySetCursorCaptureEnabled(sess, true);   // ★ P0：探针路径与主通路一致，光标入帧
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
                    using var srcTex = GetSurfaceTexture(frame);
                    if (srcTex == null) return null;
                    var bgra = ReadBackTexture(device, context, srcTex, width, height);
                    return bgra == null ? null : EncodeJpeg(bgra, width, height, quality);
                }
            }
            catch { return null; }
            finally
            {
                if (winrtPtr != IntPtr.Zero) Marshal.Release(winrtPtr);
                context?.Dispose();
                device?.Dispose();
            }
        }

        /// <summary>GPU 纹理 → CPU BGRA（staging + CopyResource + Map）。</summary>
        private static byte[]? ReadBackTexture(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D srcTex, int width, int height)
        {
            try
            {
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
                var staging = device.CreateTexture2D(desc);
                try
                {
                    context.CopyResource(staging, srcTex);
                    var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
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
                    finally { context.Unmap(staging, 0); }
                }
                finally { staging.Dispose(); }
            }
            catch { return null; }
        }

        private static byte[]? EncodeJpeg(byte[] bgra, int width, int height, int quality)
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

        private static bool TrySetCursorCaptureEnabled(Windows.Graphics.Capture.GraphicsCaptureSession s, bool v)
        {
            try { s.IsCursorCaptureEnabled = v; return true; }
            catch { return false; }
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
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

        [ComImport, Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess { [PreserveSig] int GetInterface([In] ref Guid iid, [Out] out IntPtr pObject); }

        public void Dispose() => Stop();
    }
#else
    // 无版本 TFM：不引用 WinRT/Vortice。文件为空（类型在版本化 TFM 才有）。
#endif
}
