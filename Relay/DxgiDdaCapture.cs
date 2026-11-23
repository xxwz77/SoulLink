using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SoulLink.Relay
{
    /// <summary>
    /// DXGI Desktop Duplication（DDA）抓屏后端。
    ///
    /// ★ 为什么 DDA 优先级最高（2026-11-23，DXGI-DDA-RDP-20261123.md 实测）：
    ///   · 唯一能抓「独占全屏」的 API（WGC 抓独占全屏可能黑屏）；
    ///   · 直接拿合成帧，**无撕裂**；
    ///   · GPU 桌面复制，单帧 0.13ms（对比 BitBlt 17~31ms、WGC 2~8ms）。
    ///   · RDP 会话内实测可用（旧「死路」结论被推翻：根因是显存耗尽 E_OUTOFMEMORY，
    ///     非 RDP 架构限制，见 DXGI-DDA-RDP-20261123.md）。
    ///
    /// ★ 三个前提（对齐 Sunshine display_base.cpp / misc.cpp）：
    ///   1. syncThreadDesktop（OpenInputDesktop + SetThreadDesktop）——旧探针跑在不可见
    ///      窗口站导致失败，这个步骤是必须的；
    ///   2. D3D11 用 DriverType.Hardware（本机 Unknown 需枚举 adapter，Hardware 即可）；
    ///   3. Optimus hook 仅 Intel+NVIDIA 混合本需要，单 N 卡跳过。
    ///
    /// ★ 边界处理：
    ///   · 脏矩形/静止不产帧：AcquireNextFrame 超时（DXGI_ERROR_WAIT_TIMEOUT）是正常的，
    ///     此时返回 null（调用方跳过，或按上一帧处理）；
    ///   · 独占全屏切换：AcquireNextFrame 返回 ACCESS_LOST 时重建 duplication；
    ///   · DPI：用模式列表（Description.ModeDescription）取物理尺寸，不用 GetSystemMetrics；
    ///   · 显存失败：DuplicateOutput 失败/设备创建失败 → 抛异常，由 SessionRecorder 降级链接住；
    ///   · ★ P0-A(D04)：建立失败指数退避重试（500→1000→2000→5000ms），连续失败计数
    ///     由 SessionRecorder 判死并重新进入降级链（不再每帧重建设备）。
    /// </summary>
    internal sealed class DxgiDdaCapture : IScreenCapture
    {
        public string Name => "DXGI-DDA";

        [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
        [DllImport("user32.dll")] static extern bool SetThreadDesktop(IntPtr hDesktop);
        [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr hDesktop);
        const uint DF_ALLOWOTHERACCOUNTHOOK = 0x0001;
        const uint GENERIC_ALL = 0x10000000;
        const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        /// <summary>
        /// ★ P0-E（2026-11-24）：AcquireNextFrame 阻塞等帧超时（Sunshine display_base.cpp 组首
        ///   200ms 大超时同款语义）。帧一到立即返回（≈0.1ms GPU 拷贝），彻底消除「16ms 短轮询
        ///   每拍只有 ~50% 概率捕到帧 → 帧率减半、captureMs 恒 ≥13ms」的问题；
        ///   静止桌面等满超时返回 WAIT_TIMEOUT（复用 _lastBgra），由上层 2fps 保活/静止跳过接管。
        /// </summary>
        private const int AcquireTimeoutMs = 100;

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIOutputDuplication? _duplication;
        private int _width, _height;
        private int _originX, _originY;   // ★ P1-A2：抓屏区域左上角在虚拟桌面的物理坐标（网页坐标反算用）
        private bool _active;
        private bool _disposed;
        private readonly object _gate = new();
        /// <summary>最近一次成功抓到的 BGRA 帧（静止画面 AcquireNextFrame 超时时复用）。</summary>
        private byte[]? _lastBgra;
        private int _lastWidth, _lastHeight;
        /// <summary>桌面真实出帧累计序号：AcquireNextFrame 返回新帧（FRAME_INFO.AccumulatedFrames≥1）时递增，
        /// 超时复用旧帧不递增。供 SessionRecorder 替代全帧 SequenceEqual 判静止。</summary>
        private long _frameSequence;
        /// <summary>零拷贝：私有 BGRA 纹理（Shared flag，尺寸=桌面），每帧 CopyResource 从 DDA 临时纹理拷来，供跨设备共享。</summary>
        private ID3D11Texture2D? _sharedBgraTexture;
        private int _sharedWidth, _sharedHeight;

        // ★ P0-A(D04)：建立失败计数 + 指数退避（500→1000→2000→5000ms），
        //   不再「每帧重建设备」；连续失败由 SessionRecorder 判死降级。
        private long _consecutiveFailures;
        private long _lastStartAttemptAt;   // Environment.TickCount64：上次尝试 Start 的时刻
        private long _startBackoffMs;

        /// <summary>连续 Start 失败次数（D04 判死信号；成功建立清零）。</summary>
        public long ConsecutiveFailures { get { lock (_gate) return _consecutiveFailures; } }

        /// <summary>是否已成功建立 duplication（供 SessionRecorder 探测降级用）。</summary>
        public bool IsActive => _active;

        /// <summary>★ P1-A2：抓屏区域左上角的虚拟桌面物理 X（网页 0~1 坐标反算基准）。</summary>
        public int CaptureOriginX { get { lock (_gate) return _originX; } }

        /// <summary>★ P1-A2：抓屏区域左上角的虚拟桌面物理 Y。</summary>
        public int CaptureOriginY { get { lock (_gate) return _originY; } }

        /// <summary>桌面真实出帧累计序号（对齐 WgcCapture.FrameSequence 语义）。</summary>
        public long FrameSequence { get { lock (_gate) return _frameSequence; } }

        /// <summary>最近一次降级/失败原因（供日志）。</summary>
        public string LastError { get; private set; } = "";

        private void EnsureRunning()
        {
            if (_active) return;
            long now = Environment.TickCount64;
            lock (_gate)
            {
                if (_active) return;
                // ★ P0-A(D04)：指数退避——失败后至少等 _startBackoffMs 再重试，
                //   避免每帧重建设备；判死与降级交给 SessionRecorder 读 ConsecutiveFailures。
                if (now - _lastStartAttemptAt < _startBackoffMs) return;
                _lastStartAttemptAt = now;
            }
            try
            {
                Start();   // 成功时内部清零失败计数与退避
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _consecutiveFailures++;
                    _startBackoffMs = _startBackoffMs == 0 ? 500 : Math.Min(5000, _startBackoffMs * 2);
                    LastError = ex.GetType().Name + "：" + ex.Message;
                }
            }
        }

        private void Start()
        {
            if (_active) return;

            // 前提1：syncThreadDesktop（Sunshine misc.cpp L247-264）
            SyncThreadDesktop();

            // 前提2：硬件 D3D11 设备（DriverType.Hardware，单 N 卡无需 Optimus hook）
            var levels = new[]
            {
                FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0
            };
            var hr = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
                out var device, out _, out var context);
            if (!hr.Success || device == null)
                throw new InvalidOperationException("D3D11 硬件设备失败 0x" + hr.Code.ToString("X8")
                    + "（显存不足时可能发生，降级到 WGC/BitBlt）");
            _device = device;
            _context = context;

            // 前提3：DuplicateOutput（枚举输出，Sunshine display_base.cpp L427）
            using var dxgiDev = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDev.GetAdapter();

            string failReason = "0 个可枚举输出";
            for (int oi = 0; ; oi++)
            {
                Result ohr = adapter.EnumOutputs((uint)oi, out IDXGIOutput output);
                if (!ohr.Success || output == null) break;
                using (output)
                {
                    try
                    {
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        try
                        {
                            var dup = output1.DuplicateOutput(device);
                            _duplication = dup;
                            var dd = dup.Description;
                            // 用模式列表取物理尺寸（DPI 缩放安全）
                            _width = (int)dd.ModeDescription.Width;
                            _height = (int)dd.ModeDescription.Height;
                            // ★ P1-A2：保留该输出在虚拟桌面的物理原点（网页坐标反算用）
                            var od = output.Description;
                            _originX = od.DesktopCoordinates.Left;
                            _originY = od.DesktopCoordinates.Top;
                            if (_width <= 0 || _height <= 0)
                            {
                                // 回退到 DesktopCoordinates
                                _width = od.DesktopCoordinates.Right - od.DesktopCoordinates.Left;
                                _height = od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top;
                            }
                            _active = true;
                            LastError = "";
                            // ★ P0-A(D04)：成功建立即清零失败计数与退避
                            _consecutiveFailures = 0;
                            _startBackoffMs = 0;
                            RelayAgent.LogQuiet("[capture] DXGI-DDA 建立成功：" + _width + "x" + _height);
                            return;
                        }
                        catch (SharpGenException ex)
                        {
                            failReason = "DuplicateOutput 0x" + ex.ResultCode.Code.ToString("X8");
                        }
                    }
                    catch (Exception ex)
                    {
                        failReason = ex.GetType().Name + "：" + ex.Message;
                    }
                }
            }
            throw new InvalidOperationException("DDA 建立失败：" + failReason);
        }

        /// <summary>Sunshine 的 syncThreadDesktop：把线程绑到输入桌面。</summary>
        private static void SyncThreadDesktop()
        {
            IntPtr hDesk = OpenInputDesktop(DF_ALLOWOTHERACCOUNTHOOK, false, GENERIC_ALL);
            if (hDesk == IntPtr.Zero) return;   // 失败不阻塞（某些环境可能仍可用）
            try
            {
                SetThreadDesktop(hDesk);
            }
            catch { }
            finally { CloseDesktop(hDesk); }
        }

        /// <summary>抓一帧原始 BGRA。静止画面（AcquireNextFrame 超时）复用上一帧。</summary>
        public byte[]? CaptureBgra(out int width, out int height)
        {
            width = 0; height = 0;
            if (_disposed) return null;
            EnsureRunning();
            if (!_active || _duplication == null || _device == null || _context == null)
            {
                width = _lastWidth; height = _lastHeight;
                return _lastBgra;   // 降级后返回上一帧，让调用方继续
            }

            lock (_gate)
            {
                try
                {
                    Result hr = _duplication.AcquireNextFrame(AcquireTimeoutMs, out OutduplFrameInfo frameInfo, out IDXGIResource resource);
                    if (hr.Success && resource != null)
                    {
                        try
                        {
                            // IDXGIResource → ID3D11Texture2D
                            using var tex = resource.QueryInterface<ID3D11Texture2D>();
                            var desc = tex.Description;
                            int w = (int)desc.Width, h = (int)desc.Height;
                            var bgra = ReadBackTexture(tex, w, h);
                            if (bgra != null)
                            {
                                _lastBgra = bgra;
                                _lastWidth = w; _lastHeight = h;
                                // ★ A1：以 AcquireNextFrame 返回的 DXGI_OUTDUPL_FRAME_INFO 为「桌面真的更新过」的
                                //   权威判据——hr.Success 即 DDA 已出真实新帧，frameInfo.AccumulatedFrames 是
                                //   自上次处理以来累积的新帧数（≥1）、frameInfo.LastPresentTime 为新的
                                //   QueryPerformanceCounter 时间戳。这里显式累加 AccumulatedFrames，
                                //   供 SessionRecorder 用「序号是否增长」替代全帧 SequenceEqual（该比对在
                                //   RDP 慢合成下基准帧陈旧，操作画面被误判静止，staticSkipped 暴涨）。
                                _frameSequence += Math.Max(1L, (long)frameInfo.AccumulatedFrames);
                                width = w; height = h;
                                return bgra;
                            }
                            width = _lastWidth; height = _lastHeight;
                            return _lastBgra;
                        }
                        finally
                        {
                            resource.Dispose();
                            _duplication.ReleaseFrame();
                        }
                    }
                    else if (hr.Code == DXGI_ERROR_WAIT_TIMEOUT)
                    {
                        // 静止画面：复用上一帧
                        width = _lastWidth; height = _lastHeight;
                        return _lastBgra;
                    }
                    else if (hr.Code == DXGI_ERROR_ACCESS_LOST)
                    {
                        // 独占全屏切换/模式变化：重建 duplication
                        LastError = "ACCESS_LOST（模式切换），重建 duplication";
                        RelayAgent.LogQuiet("[capture] DXGI-DDA ACCESS_LOST，重建");
                        Rebuild();
                        width = _lastWidth; height = _lastHeight;
                        return _lastBgra;
                    }
                    else
                    {
                        LastError = "AcquireNextFrame 0x" + hr.Code.ToString("X8");
                        width = _lastWidth; height = _lastHeight;
                        return _lastBgra;
                    }
                }
                catch (SharpGenException ex) when (ex.ResultCode.Code == DXGI_ERROR_ACCESS_LOST)
                {
                    LastError = "ACCESS_LOST（异常），重建 duplication";
                    RelayAgent.LogQuiet("[capture] DXGI-DDA ACCESS_LOST 异常，重建");
                    Rebuild();
                    width = _lastWidth; height = _lastHeight;
                    return _lastBgra;
                }
                catch (Exception ex)
                {
                    LastError = ex.GetType().Name + "：" + ex.Message;
                    width = _lastWidth; height = _lastHeight;
                    return _lastBgra;
                }
            }
        }

        /// <summary>
        /// 零拷贝（阶段B）：AcquireNextFrame 拿到 GPU 纹理 → CopyResource 到私有 Shared 纹理 → 返回共享句柄，
        /// 不读回 CPU。DDA 的 AcquireNextFrame 纹理是临时的（ReleaseFrame 后失效），所以必须
        /// 在 ReleaseFrame 前拷贝到私有 Shared 纹理。静止画面（WAIT_TIMEOUT）返回 false，调用方回退 CPU。
        /// </summary>
        public bool TryCaptureBgraTexture(out IntPtr sharedHandle, out int width, out int height)
        {
            sharedHandle = IntPtr.Zero; width = 0; height = 0;
            if (_disposed) return false;
            EnsureRunning();
            if (!_active || _duplication == null || _device == null || _context == null) return false;

            lock (_gate)
            {
                try
                {
                    Result hr = _duplication.AcquireNextFrame(AcquireTimeoutMs, out OutduplFrameInfo frameInfo, out IDXGIResource resource);
                    if (!hr.Success || resource == null) return false;   // 静止（WAIT_TIMEOUT）或失败 → 回退 CPU

                    try
                    {
                        using var tex = resource.QueryInterface<ID3D11Texture2D>();
                        var desc = tex.Description;
                        int w = (int)desc.Width, h = (int)desc.Height;
                        if (w <= 0 || h <= 0) return false;

                        // 惰性创建/重建私有 Shared 纹理
                        if (_sharedBgraTexture == null || _sharedWidth != w || _sharedHeight != h)
                        {
                            _sharedBgraTexture?.Dispose();
                            _sharedBgraTexture = _device.CreateTexture2D(new Texture2DDescription
                            {
                                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                                Format = Format.B8G8R8A8_UNorm,
                                SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Default,
                                BindFlags = BindFlags.None,
                                CPUAccessFlags = CpuAccessFlags.None,
                                MiscFlags = ResourceOptionFlags.Shared
                            });
                            _sharedWidth = w; _sharedHeight = h;
                        }

                        // DDA 临时纹理 → 私有 Shared 纹理（显存内拷贝，ReleaseFrame 前完成）
                        _context.CopyResource(_sharedBgraTexture, tex);

                        // 拿共享句柄
                        using var dxgiRes = _sharedBgraTexture.QueryInterface<IDXGIResource>();
                        sharedHandle = dxgiRes.SharedHandle;
                        if (sharedHandle == IntPtr.Zero) return false;   // Shared 句柄拿不到 → 回退 CPU

                        // ★ 与 CaptureBgra 一致：桌面真实出帧序号递增，供静止判定用。
                        //   （零拷贝分支消费了本帧，序号必须同步增长，否则 fall through 到 CPU 路径时静止判定错乱。）
                        _frameSequence += Math.Max(1L, (long)frameInfo.AccumulatedFrames);

                        width = w; height = h;
                        return true;
                    }
                    finally
                    {
                        resource.Dispose();
                        _duplication.ReleaseFrame();
                    }
                }
                catch { return false; }   // 任何异常 → 回退 CPU，不抛
            }
        }

        /// <summary>重建 duplication（独占全屏切换后）。失败则标记 inactive，由调用方降级。</summary>
        private void Rebuild()
        {
            try
            {
                _duplication?.Dispose();
                _duplication = null;
                _active = false;
                Start();
            }
            catch (Exception ex)
            {
                LastError = "重建失败：" + ex.Message;
                _consecutiveFailures++;   // ★ P0-A(D04)：重建失败计入连续失败，供 SessionRecorder 判死降级
                RelayAgent.LogQuiet("[capture] DXGI-DDA 重建失败：" + ex.Message);
                _active = false;
                _duplication?.Dispose();
                _duplication = null;
            }
        }

        /// <summary>GPU 纹理 → CPU BGRA（staging + CopyResource + Map，与 WgcCapture 同款）。</summary>
        private byte[]? ReadBackTexture(ID3D11Texture2D srcTex, int width, int height)
        {
            try
            {
                if (_device == null || _context == null) return null;
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

        /// <summary>DDA 抓的是 BGRA，JPEG 编码与 BitBlt 同款（供截图协议用）。</summary>
        public byte[]? CaptureJpeg(int quality, out int width, out int height)
        {
            var bgra = CaptureBgra(out width, out height);
            if (bgra == null) return null;
            try
            {
                using var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try { Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length); }
                finally { bmp.UnlockBits(bd); }
                using var ms = new MemoryStream();
                using var ep = new System.Drawing.Imaging.EncoderParameters(1);
                ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                bmp.Save(ms, JpegCodec(), ep);
                return ms.ToArray();
            }
            catch { return null; }
        }

        private static System.Drawing.Imaging.ImageCodecInfo? _jpeg;
        private static System.Drawing.Imaging.ImageCodecInfo JpegCodec()
        {
            if (_jpeg != null) return _jpeg;
            foreach (var c in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid) { _jpeg = c; break; }
            return _jpeg ?? throw new InvalidOperationException("找不到 JPEG 编码器");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _duplication?.Dispose(); } catch { }
            try { _sharedBgraTexture?.Dispose(); } catch { }
            try { _context?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            _duplication = null; _sharedBgraTexture = null; _context = null; _device = null;
            _active = false;
        }
    }
}
