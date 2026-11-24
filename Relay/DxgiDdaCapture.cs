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
        ///
        /// ★★ 2026-11-24（T31）：100ms 是**按常数**定的，与 fps 脱钩 ⇒ 产生「时好时坏」。★★
        ///
        ///   【症状】captureMs 在 5~15 与 100 之间跳，用户感知为“释放时好时坏”。
        ///
        ///   【机理】fps=60 ⇒ 主循环节拍 16ms。一次静止抓屏要等满 100ms = **6 个节拍**，
        ///     这段时间 worker 完全阻塞在 AcquireNextFrame 里：
        ///       · urgent 恰好在循环顶部到达 → 15ms（快）；
        ///       · urgent 落在一次 100ms 抓屏中间 → 剩余等待 + 15ms ≈ **115ms**（慢）。
        ///     → 同一个操作，体感差 8 倍，这就是“时好时坏”。
        ///
        ///   【但注释 L48-51 说的“短轮询会丢帧”也是真的】—— 两者必须同时成立：
        ///     · 若超时远小于帧间隔（如 5ms @60fps）：多数轮次拿不到帧，
        ///       这些轮次就被上层当作“静止”跳过 → **帧率实际减半**；
        ///     · 若超时等于/略大于帧间隔：每个节拍都有机会捕到该拍的新帧，不丢；
        ///       且拿到帧时是**立即返回**（≈0.1ms），不会真的等满。
        ///
        ///   【所以取“帧间隔本身”，而不是取极小值】
        ///     · fps=60 → 16ms：一个节拍内必有机会拿到该拍新帧；静止时最多白等 16ms；
        ///     · fps=30 → 33ms（clamp 到 20）：宁可比间隔略短，也不让静止时白等过久；
        ///     · clamp 下限 8ms：低于此值在 60fps 下会开始丢帧（半个节拍）；
        ///     · clamp 上限 20ms：高 fps 时也要给 DDA 留出“这一拍真的有帧”的窗口。
        ///
        ///   【效果】静止抓屏 100ms → 16ms（快 6 倍）；captureMs 分布从「5/15/100」变成「5~20」；
        ///     最重要的是 **urgent 的最坏等待从 ~115ms 降到 ~24ms**（fps=60：16 在途 + 8 urgent）。
        /// </summary>
        private const int AcquireTimeoutMarginMs = 2;   // 调度抖动/计时器精度余量
        private const int AcquireTimeoutMinMs = 8;       // 绝对下限（防退化成忙等）
        private const int AcquireTimeoutMaxMs = 120;     // 上限：容纳极低 fps（10fps→102ms）；正常 30~120fps 时取值 11~36ms

        /// <summary>主循环 fps（由 SessionRecorder 在参数变化时下发）。仅用于**冷启动**时的初始估计。</summary>
        private int _fps = 60;

        // ═══════════════════════════════════════════════════════════════════
        //  ★★ 2026-11-24（T34）：超时改按**实测帧间隔**自适应，不再按配置 fps ★★
        //
        //  【为什么必须改】T31/T33 反复出问题的深层原因：**用「期望值」而不是「实测值」定超时**。
        //    配置 StreamFps=60 ⇒ 算出 19ms；但若服务端实际只能产 40fps，
        //    真实帧间隔 = 25ms → 19ms < 25ms → **仍然每帧必超时 → 又回到复用旧帧**。
        //  用户原话：「不应该根据帧率自动调节吗？不然不同帧率又卡了？」—— 问得对。
        //
        //  【办法】用最近若干帧的**实测间隔**做指数平滑（EMA），超时 = EMA × 1.2 + 2ms：
        //    配置 60 实际 60 → EMA≈16.7 → 超时 19ms
        //    配置 60 实际 40 → EMA≈25   → 超时 32ms   ← 自动放宽，不会必超时
        //    配置 60 实际 30 → EMA≈33   → 超时 42ms
        //  不管实际帧率怎么变，超时总比它大一点。
        //
        //  【两个必须处理的边界】
        //   ① 冷启动：还没成功拿过帧 → EMA=0，用配置 fps 兜底（同 T33 逻辑）
        //   ② 静止污染：长时间无新帧时间隔会是几十~几百 ms；若进 EMA，
        //      超时会被拉到 120ms+，**松手又变慢**。
        //      → 只在「拿到新帧 **且** 间隔 < 500ms」时更新 EMA。
        // ═══════════════════════════════════════════════════════════════════
        private double _emaFrameIntervalMs;      // 0 = 尚未测到，用配置 fps 兜底
        private long _lastNewFrameAtMs;          // Environment.TickCount64：上次**成功拿到新帧**的时刻
        private const double EmaAlpha = 0.2;             // 平滑系数（约等价最近 5 帧）
        private const double TimeoutSafetyFactor = 1.2;  // 超时 = 实测间隔 × 1.2
        private const long MaxEmaSampleMs = 500;         // 超过此间隔不采信（静止/卡顿污染）

        /// <summary>
        /// ★ T34：在**成功拿到新帧**后调用，用实测间隔更新 EMA。
        ///   ★ 必须只在「FrameSequence 真的增长」之后调用 —— 复用旧帧**不算**拿到新帧。
        /// </summary>
        private void NoteNewFrame()
        {
            long now = Environment.TickCount64;
            if (_lastNewFrameAtMs > 0)
            {
                long delta = now - _lastNewFrameAtMs;
                // 过滤静止/卡顿造成的大间隔（否则 EMA 被拉大 → 超时飙到 120ms → 松手变慢）
                if (delta > 0 && delta < MaxEmaSampleMs)
                {
                    double sample = delta;
                    _emaFrameIntervalMs = _emaFrameIntervalMs <= 0
                        ? sample                                            // 第一个样本直接采用
                        : _emaFrameIntervalMs * (1 - EmaAlpha) + sample * EmaAlpha;
                }
            }
            _lastNewFrameAtMs = now;
        }

        /// <summary>
        /// 动态抓屏超时：**必须略大于帧间隔**，否则每帧都会差一点点超时。
        ///
        /// ★ T34：基准从「配置 fps」改为「实测 EMA 帧间隔」；配置 fps 只在冷启动兜底。
        ///
        /// ★★ 2026-11-24（T33 紧急修复）：上一版用了 `1000 / _fps` 的**整数除法**，★★
        ///   结果 fps=60 时得到 16ms，而真实帧间隔是 **16.67ms** —— 超时比间隔小 0.67ms，
        ///   `AcquireNextFrame(16)` 每次都在新帧到达前就超时返回，**永远拿不到新帧**，
        ///   只能复用 _lastBgra。表现为：
        ///     · 日志 captureMs **恒等于 acqMs**（16），偶尔 4~7（那是真拿到了）；
        ///     · 每帧都复用上一帧 ⇒ 画面延迟逐帧累积（用户实测「3 帧 → 9 帧」，91~136ms）；
        ///     · 动态画面看起来「不动」或严重滞后。
        ///
        ///   【修法】向上取整 + 余量：
        ///     fps=60 → Ceiling(16.67)=17，+2 = **19ms** > 16.67 ✓
        ///     fps=30 → Ceiling(33.33)=34，+2 = **36ms** > 33.33 ✓（故上限须放宽到 40）
        ///     fps=120 → Ceiling(8.33)=9，+2 = **11ms** > 8.33 ✓
        ///   +2ms 余量吸收调度抖动与计时器精度（即使已 timeBeginPeriod(1)，仍非精确）。
        ///
        ///   【为什么不能用整数除法】1000/60=16 少了 0.67ms；看似微不足道，但 DDA 是
        ///   「等到新帧到达才返回」，差这 0.67ms 就是「每次都差一步」→ 100% 超时。
        /// </summary>
        private uint CurrentAcquireTimeoutMs()
        {
            // ★ T34：优先用**实测**帧间隔；还没测到（冷启动）才退回配置 fps。
            //   实测值来自 NoteNewFrame() 的 EMA —— 这样「配置 60 实际 40」时
            //   超时会是 25×1.2+2 = 32ms，而不是死按 19ms 每帧必超时。
            double basisMs = _emaFrameIntervalMs > 0
                ? _emaFrameIntervalMs * TimeoutSafetyFactor   // 实测间隔 × 1.2
                : 1000.0 / Math.Max(1, _fps);                 // 冷启动兜底（无余量，靠下面 +2）
            // 向上取整 —— 绝不能用整数除法（T33 的教训：1000/60=16 少了 0.67ms 就 100% 超时）
            int frameIntervalCeil = (int)Math.Ceiling(basisMs);
            int ms = Math.Clamp(frameIntervalCeil + AcquireTimeoutMarginMs,
                                AcquireTimeoutMinMs, AcquireTimeoutMaxMs);
            // ★ urgent 不再减半！
            //   原因：减半后（19/2≈9ms）**小于 60fps 的帧间隔 16.67ms**，
            //   urgent 补帧同样会拿不到新帧 —— 那等于把刚刚修好的 bug 又引回来一次。
            //   urgent 的真正收益来自「跳过 WaitUntilNextSlot 节拍 + 第 4 处 RequestKeyFrame」，
            //   不依赖缩短抓屏超时。保持与常规拍相同的超时，行为最可预测。
            LastAcquireTimeoutMs = (uint)ms;   // 埋点：供日志验证
            return (uint)ms;
        }

        /// <summary>
        /// ★ T33 安全网：当前超时是否**严格大于**帧间隔。
        ///
        ///   为什么需要它：超时 <= 帧间隔 会让 AcquireNextFrame **每帧必超时** →
        ///   永远复用旧帧 → 延迟逐帧累积。这个 bug 在日志里的唯一特征是
        ///   「captureMs 恒等于 acqMs」，但**很难一眼看出**。
        ///   这里显式暴露一个判据，供 SessionRecorder 在启动/参数变化时断言并打日志，
        ///   让「配置不自洽」立刻可见，而不是靠用户感知到延迟变差才发现。
        /// </summary>
        internal bool IsAcquireTimeoutSane()
        {
            double realInterval = 1000.0 / Math.Max(1, _fps);
            return CurrentAcquireTimeoutMs() > realInterval;
        }

        /// <summary>由 SessionRecorder 下发当前 fps（参数变化时调用）。</summary>
        internal void SetTargetFps(int fps) { if (fps > 0) _fps = fps; }

        /// <summary>
        /// 本拍实际会用的抓屏超时（ms）——**仅供日志埋点**，便于部署后验证
        /// 「超时是否真的跟随 fps」以及 captureMs 分布是否从「5/15/100」变成「5~20」。
        /// </summary>
        internal uint LastAcquireTimeoutMs { get; private set; }

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIOutputDuplication? _duplication;
        private int _width, _height;
        private int _originX, _originY;   // ★ P1-A2：抓屏区域左上角在虚拟桌面的物理坐标（网页坐标反算用）
        private uint _captureAdapterVendorId;   // ★ P0-H：抓屏所在 GPU 的 VendorId（供编码器路由与零拷贝门控）
        private string _captureAdapterName = "";
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

        /// <summary>★ P0-H：抓屏所在 GPU 的 VendorId（0x10DE=NVIDIA；0=未建立）。供编码器路由与零拷贝门控。</summary>
        public uint CaptureAdapterVendorId { get { lock (_gate) return _captureAdapterVendorId; } }

        /// <summary>★ P0-H：抓屏所在 GPU 名称（日志用）。</summary>
        public string CaptureAdapterName { get { lock (_gate) return _captureAdapterName; } }

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

            // ★ P0-G/P0-H：前提2 —— 通用自适应抓屏适配器选择：优先「有输出的 NVIDIA」（与 NVENC
            //   同 GPU → 零拷贝可用）；否则第一个有输出的硬件适配器（Intel/AMD/虚拟显示）。
            //   跨 GPU 时零拷贝由上层禁用（物理限制），抓屏本身不受影响。
            var candidates = GpuAdapter.PickCaptureCandidates();
            try
            {
                string failReason = "0 个可枚举输出";
                foreach (var candidate in candidates)
                {
                    if (!GpuAdapter.TryCreateDevice(candidate, out var dev, out var ctx, out _) || dev == null || ctx == null)
                        continue;
                    _device = dev; _context = ctx;
                    using (var a = dev.QueryInterface<IDXGIDevice>().GetAdapter())
                    {
                        if (TryEstablishDuplication(a, out failReason))
                        {
                            // ★ P0-H：记录抓屏 GPU（VendorId/名称）供编码器路由与零拷贝门控
                            try
                            {
                                using var a1 = a.QueryInterface<IDXGIAdapter1>();
                                var d1 = a1.Description1;
                                _captureAdapterVendorId = d1.VendorId;
                                _captureAdapterName = d1.Description.Trim();
                            }
                            catch { }
                            RelayAgent.LogQuiet("[capture] DXGI-DDA 抓屏源=" + _captureAdapterName
                                + "(" + GpuAdapter.VendorHex(_captureAdapterVendorId) + ")"
                                + (_captureAdapterVendorId == GpuAdapter.NvidiaVendorId ? " 与 NVENC 同 GPU" : ""));
                            return;
                        }
                    }
                    // 该候选建立 duplication 失败 → 释放设备，试下一个候选
                    _context?.Dispose(); _device?.Dispose();
                    _context = null; _device = null;
                }
                throw new InvalidOperationException("DDA 建立失败：" + failReason + "（无可用抓屏适配器，降级 WGC/BitBlt）");
            }
            finally
            {
                foreach (var c in candidates) c.Dispose();
            }
        }

        /// <summary>在指定适配器上枚举输出并建立 DuplicateOutput（前提3，Sunshine display_base.cpp L427）。</summary>
        private bool TryEstablishDuplication(IDXGIAdapter adapter, out string failReason)
        {
            failReason = "0 个可枚举输出";
            if (_device == null) return false;
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
                            var dup = output1.DuplicateOutput(_device);
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
                            return true;
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
            return false;
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

        /// <summary>
        /// ★ 2026-11-24（T29）：本拍是否由「输入触发」驱动。
        ///
        ///   【要解决的问题】松手后补帧要白等 200ms：
        ///     ① TryCaptureBgraTexture 先 AcquireNextFrame 等满 100ms 超时 → false；
        ///     ② 回退 CPU CaptureBgra 又等满 100ms 超时 → 复用 _lastBgra。
        ///   合计 200ms 才把一帧编出来，用户感知就是「卡一下才出来」。
        ///
        ///   【为什么这里能省】既然已经知道「这一拍是用户操作结束、桌面大概率静止」，
        ///     就没有必要再赌 100ms 等新帧 —— 直接用上一帧编一次即可。
        ///     动态画面（真在变）时该标志为 false，仍走完整 100ms，零拷贝性能不受影响。
        ///
        ///   【线程模型】由 SessionRecorder 的 worker 线程在抓屏前置位、抓屏后复位，
        ///     两者是同一线程顺序执行，不存在竞态。
        /// </summary>
        internal bool InputTriggeredFrame { get; set; }

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
                    // ★ 2026-11-24（T29）：输入触发的补帧不为静止画面白等 100ms。
                    //   置 InputTriggeredFrame（由 SessionRecorder 在 inputTrigger 时置位）：
                    //     · 超时降到 0 —— 只有「此刻真的有新帧」才会拿到，拿不到立即走复用；
                    //     · 省掉本轮 100ms 等待，松手补帧从 ~200ms 压到 ~100ms 以内（零拷贝那 100ms 也一并省）。
                    //   ★ 动态画面（正常拖动）时该标志为 false，行为与原来完全一致（仍等 100ms）。
                    // ★ T31：动态超时（跟随帧间隔，urgent 再减半）——见 CurrentAcquireTimeoutMs。
                    //   替代原来的「urgent?15:100」硬编码：那会让静止抓屏阻塞 100ms=6个节拍，
                    //   使 urgent 的最坏等待达到 ~115ms。
                    uint acquireTimeout = CurrentAcquireTimeoutMs();
                    Result hr = _duplication.AcquireNextFrame(acquireTimeout, out OutduplFrameInfo frameInfo, out IDXGIResource resource);
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
                                // ★ T34：真的拿到新帧了 → 用实测间隔更新 EMA（超时自适应的数据源）
                                NoteNewFrame();
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
                    // ★ T31：零拷贝与 CPU 路径**共用同一个动态超时**，避免两条路径行为不一致。
                    //   （任务特别要求检查这一点：原实现两处各自写死，改一处漏一处就会出现
                    //     “CPU 路径快了但零拷贝还慢 100ms”的隐蔽不一致。）
                    uint zeroTimeout = CurrentAcquireTimeoutMs();
                    Result hr = _duplication.AcquireNextFrame(zeroTimeout, out OutduplFrameInfo frameInfo, out IDXGIResource resource);
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
                        // ★ P0-F：跨设备共享前 Flush，保证拷贝结果对编码设备可见
                        _context.Flush();

                        // 拿共享句柄
                        using var dxgiRes = _sharedBgraTexture.QueryInterface<IDXGIResource>();
                        sharedHandle = dxgiRes.SharedHandle;
                        if (sharedHandle == IntPtr.Zero) return false;   // Shared 句柄拿不到 → 回退 CPU

                        // ★ 与 CaptureBgra 一致：桌面真实出帧序号递增，供静止判定用。
                        //   （零拷贝分支消费了本帧，序号必须同步增长，否则 fall through 到 CPU 路径时静止判定错乱。）
                        _frameSequence += Math.Max(1L, (long)frameInfo.AccumulatedFrames);
                        // ★ T34：零拷贝成功拿到新帧 → 同样更新 EMA
                        NoteNewFrame();

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
