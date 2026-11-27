using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Services;

namespace SoulLink.Relay
{
    /// <summary>
    /// 会话内共享视频管线：抓屏 → BGRA → NV12 → H.264 → NAL 环形缓冲。
    /// 所有观众只订阅编码结果，不重复抓屏或编码；JPEG 仅保留给一次性截图。
    /// </summary>
    internal static class SessionRecorder
    {
        private sealed class VideoFrame
        {
            public byte[] Nal = Array.Empty<byte>();
            public DateTime At;
            public bool KeyFrame;
        }

        private sealed class Viewer
        {
            public readonly object Sync = new();
            public readonly ViewerFrameQueue<(byte[] Nal, DateTime At)> Pending;
            public Viewer(bool hasInitialIdr) => Pending = new(ViewerQueueDepthValue, hasInitialIdr);
            public readonly SemaphoreSlim FrameReady = new(0, 1);
            public bool Closed;

            public void Signal() { if (FrameReady.CurrentCount == 0) FrameReady.Release(); }
        }

        private static IScreenCapture Capture = CreateCapture();
        private static readonly object Gate = new();
        private static readonly List<VideoFrame> Ring = new();

        // ★ P0-E：1ms 系统定时器精度——WaitOne(ms) 在默认 15.6ms 粒度下每次节拍超调 0~15ms，
        //   30fps 节拍会被拖到 ~25fps；timeBeginPeriod(1) 后粒度 ~1ms。Windows 全局引用计数，
        //   worker 起止成对调用。
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uMilliseconds);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uMilliseconds);
        private static readonly Dictionary<int, Viewer> Viewers = new();
        private static Thread? _worker;
        private static volatile bool _stopRequested;
        private static int _fps = 10;
        private static int _quality = 75;
        private static int _keepSeconds = 30;
        private static int _sessionId;
        private static string _status = "idle";
        private static string _lastError = "";
        private static long _totalFrames;
        private static int _width, _height;
        private static int _nextViewerId;
        private static bool _recordRequested;
        private static volatile bool _remoteControlEnabled = Environment.GetEnvironmentVariable("SOULLINK_REMOTE_CONTROL") == "1";
        public static bool RemoteControlEnabled => _remoteControlEnabled;
        private static long _captureMs, _convertMs, _encodeMs;
        private static long _zeroMs;   // ★ P0-F：零拷贝单帧总耗时（抓取→着色器转换→编码，可观测）
        private static long _zeroConvertMs;   // ★ P0-F：零拷贝纯 GPU 转换耗时（不含等帧）
        private static int _configuredBitrate, _negotiatedBitrate;
        private static readonly AutoResetEvent LifecycleChanged = new(false);
        private static bool _forceKeyFrame;
        private static Stopwatch? _probeClock;
        private static bool _captureProbeComplete;
        // ★ P0-A（2026-11-24，D01/D04）：探测完成只表示「当前后端已选定」，不再永久锁死。
        private static bool _probeSeeded;                  // 本轮探测是否已垫过 BitBlt 首帧
        private static string _captureFallbackReason = ""; // 最近一次降级原因（进 StatusText，D24 可观测）
        private static DateTime _captureFallbackAtUtc;     // 最近一次降级时刻
        private static long _nextGpuRetryAt;               // 下次重试 GPU 后端的时刻（Stopwatch ticks；仅 worker 读写）
        private static volatile bool _captureRetryRequested;   // 外部请求立即重试 GPU 后端（RequestCaptureRetry）
        private static int _backendDeadStreak;             // 后端连续不健康拍数（D04 判死）
        private static long _captureSwapGeneration;        // 后端实例替换代数（Loop 检测后重置静止基准）
        private const int GpuRetrySeconds = 10;            // BitBlt 兜底期间重试 GPU 后端的周期
        private const long DdaFirstFrameWindowMs = 1500;   // DDA 已建立但静止桌面无首帧的等待窗口
        private const long DdaEstablishWindowMs = 2000;    // DDA 建立失败时的探测窗口
        private const long WgcFirstFrameWindowMs = 2500;   // WGC 已 running 但静止桌面无首帧的等待窗口
        private const int DdaDeadFailureCount = 3;         // D04：DDA 连续 Start 失败 ≥3 判死
        private const int BackendDeadStreakLimit = 5;      // D04：后端连续不健康拍数上限
        private static DateTime _lastCaptureAt, _lastEncodeAt, _lastNalAt;
        private static long _loopSequence, _skippedStaticFrames, _noNalFrames;
        // ★ T33：上次已告警过的 fps（避免每拍刷屏；仅在 fps 变化时重新检查）
        private static int _lastSaneWarnAt = -1;
        // ★ P0-D（2026-11-24）：有观众时静止桌面保活——按 500ms（2fps，对齐 Moonlight 500ms ping）
        //   重编码重复帧维持 /stream 帧流，防前端看门狗「长时间无新帧 → 断流重连」。
        private static long _keepaliveNextAt;         // 下次保活编码时刻（Stopwatch ticks；仅 worker 读写）
        private static long _keepaliveFrames;         // 已发出的保活帧计数（可观测指标）
        private static long _viewerQueueDrops;        // ★ P0 队列优化：丢旧保新累计丢帧数（可观测）
        private static int _viewerQueueDepthMax;      // ★ P0 队列优化：入队瞬间观众队列最大深度（可观测）
        private static long _queueAgeMs;              // ★ P0 队列优化：最近一次出队帧龄（服务端队列等待）
        private static long _queueAgeMaxMs;           // ★ P0 队列优化：出队帧龄历史最大
        // ★ P0（2026-11-24）：保活节拍可调（SOULLINK_KEEPALIVE_MS，1~2000，默认 250=4fps）。
        //   原 500（2fps）：静止画面「兜底出帧」最坏延迟 500ms——对秒表类低频跳变内容，
        //   当 DDA 事件驱动帧未覆盖时（真静止）用户要等保活帧才看到更新。降到 250 减半最坏等待。
        //   带宽代价：静止 P 帧实测 ~7.2KB/帧 → 4fps ≈ 230kbps（原 2fps 115kbps，+115kbps）。
        private static readonly int KeepaliveIntervalMs = ReadKeepaliveMs();
        private static int ReadKeepaliveMs()
        {
            string? raw = Environment.GetEnvironmentVariable("SOULLINK_KEEPALIVE_MS");
            return int.TryParse(raw, out int v) ? Math.Clamp(v, 1, 2000) : 250;
        }
        private const int ZeroCopyFailDisableThreshold = 3;   // P0-E：零拷贝连续失败 ≥3 禁用 GPU 直通（防吞帧）
        /// <summary>每个观众的积压上限。超过上限后，下一段数据从完整 IDR 开始。</summary>
        private const int ViewerQueueDepth = 2;
        private static readonly int ViewerQueueDepthValue = ReadQueueDepth();

        private static int ReadQueueDepth()
        {
            string? raw = Environment.GetEnvironmentVariable("SOULLINK_VIEWER_QUEUE");
            return int.TryParse(raw, out int v) ? Math.Clamp(v, 1, 8) : ViewerQueueDepth;
        }
        private static int _captureMode = int.TryParse(Environment.GetEnvironmentVariable("SOULLINK_CAPTURE_MODE"), out int initialMode) ? Math.Clamp(initialMode, 0, 3) : 0;
        private static int _captureModeVersion;
        private static volatile bool _captureSwitchRequested;
        // ★ P0-G：NVENC 探测失败后本进程内不再重试（真机曾因 NVENC 每建必败 + 编码器按需 IDR 重建
        //   每秒重试 2-3 次，日志刷爆）。仅显式热切抓屏后端时重置。
        private static bool _nvencUnavailable;
        private static int _videoParameterVersion;
        private static VideoParameters _videoParameters = VideoParameters.ReadEnvironment();

        private sealed record VideoParameters(int Bitrate, int Preset, int Vbv, int Stream, int Record)
        {
            public static VideoParameters ReadEnvironment() => new(
                ReadInt("SOULLINK_VIDEO_BITRATE", 500, 500000, 8000),
                ReadInt("SOULLINK_NVENC_PRESET", 1, 7, 3),
                ReadInt("SOULLINK_VBV_INCREASE_PERCENT", 0, 200, 0),
                ReadInt("SOULLINK_STREAM_FPS", 1, 120, 30),
                ReadInt("SOULLINK_REC_FPS", 1, 30, 5));
            public string Describe() => "bitrate=" + Bitrate + "kbps preset=P" + Preset + " vbv=+" + Vbv + "% stream=" + Stream + "fps record=" + Record + "fps";
        }
        private static int ReadInt(string key, int min, int max, int fallback)
            => int.TryParse(Environment.GetEnvironmentVariable(key), out int value) ? Math.Clamp(value, min, max) : fallback;

        private static int StreamFps
        {
            get
            {
                lock (Gate) return _videoParameters.Stream;
            }
        }
        private static int RecordFps { get { lock (Gate) return _videoParameters.Record; } }
        private static int StreamBitrate { get { lock (Gate) return _videoParameters.Bitrate; } }

        /// <summary>接收运行时编码参数；不改变录像/直播生命周期，不重建 agent 或 viewer。</summary>
        public static string CaptureBackend { get { lock (Gate) return Capture.Name; } }

        /// <summary>★ P0-A(D24)：最近一次抓屏降级原因（"" = 当前在首选后端）。供 /api/info 扩展使用。</summary>
        public static string CaptureFallbackReason { get { lock (Gate) return _captureFallbackReason; } }

        /// <summary>★ P0-A(D24)：最近一次抓屏降级时刻（UTC；从未降级时为 default）。</summary>
        public static DateTime CaptureFallbackAtUtc { get { lock (Gate) return _captureFallbackAtUtc; } }

        /// <summary>
        /// ★ P0-A(D01)：外部触发（输入活动/用户操作后）立即重试 GPU 后端——置立即重试标志并唤醒
        /// worker，worker 下一拍即尝试（仅 auto 模式、当前非 DDA 后端时生效）。输入链路尚未接线时
        /// 由 10 s 周期重试兜底；接线后调用本方法可实现「一有输入就爬回 DDA/WGC」。
        /// </summary>
        public static void RequestCaptureRetry()
        {
            _captureRetryRequested = true;
            LifecycleChanged.Set();
        }

        /// <summary>
        /// ★ P1-A2（输入坐标换算）：暴露真实抓屏几何——尺寸 = 编码帧尺寸（网页 norm 的基准，
        /// 与 canvas 一致）；原点 = 抓屏区域左上角在虚拟桌面的物理像素坐标（多屏安全）。
        /// 尚未出过帧（编码器未建）时返回 false。
        /// </summary>
        public static bool TryGetCaptureGeometry(out int width, out int height, out int originX, out int originY)
        {
            lock (Gate)
            {
                width = _width; height = _height;
                switch (Capture)
                {
                    case DxgiDdaCapture dda: originX = dda.CaptureOriginX; originY = dda.CaptureOriginY; break;
#if WGC
                    case WgcCapture wgc: originX = wgc.CaptureOriginX; originY = wgc.CaptureOriginY; break;
#endif
                    case BitBltCapture b: originX = b.CaptureOriginX; originY = b.CaptureOriginY; break;
                    default: originX = 0; originY = 0; break;
                }
                return width > 0 && height > 0;
            }
        }

        /// <summary>消息线程只记录热切版本；worker 在帧边界释放/创建 Capture，viewer 不变。</summary>
        public static void UpdateCaptureMode(int mode)
        {
            mode = Math.Clamp(mode, 0, 3);
            lock (Gate)
            {
                if (_captureMode == mode) return;
                int old = _captureMode;
                _captureMode = mode; _captureModeVersion++; _captureSwitchRequested = true;
                RelayAgent.LogQuiet("[capture] 请求热切后端 mode=" + old + " → " + mode + "；等待视频线程切换");
                RecomputeLifecycle();
            }
        }

        public static void UpdateVideoParameters(int bitrateKbps, int preset, int vbvPercent, int streamFps, int recordFps)
        {
            var value = new VideoParameters(
                Math.Clamp(bitrateKbps, 500, 500000), Math.Clamp(preset, 1, 7),
                Math.Clamp(vbvPercent, 0, 200), Math.Clamp(streamFps, 1, 120), Math.Clamp(recordFps, 1, 30));
            lock (Gate)
            {
                if (_videoParameters == value) return;
                _videoParameters = value;
                _videoParameterVersion++;
                // 空闲时只保存参数；下次有观众/录像才用新值创建编码器。
                RecomputeLifecycle();
            }
        }

        private static IScreenCapture CreateCapture()
        {
            return CreateCapture(_captureMode);
        }

        private static IScreenCapture CreateCapture(int mode)
        {
            switch (mode)
            {
                case 3:
                    RelayAgent.LogQuiet("[capture] 显式 BitBlt 后端");
                    return new BitBltCapture();
                case 1:
                    return new DxgiDdaCapture();
                case 2:   // 显式 WGC（跳过 DDA 探测）
#if WGC
                    return new WgcCapture();
#else
                    return new BitBltCapture();
#endif
                default:  // 0 = auto：DDA → WGC → BitBlt
                    return new DxgiDdaCapture();
            }
        }

        /// <summary>自动抓屏探测只在视频 worker 运行，成功帧保留给正常编码，失败静默降级。
        /// 降级链：DXGI DDA → WGC → BitBlt。
        /// ★ P0-A（2026-11-24，D01/D04）：探测完成只表示「当前后端已选定」，不再永久锁死：
        ///   1) 「无新帧」≠「后端不可用」——DDA/WGC 已建立（IsActive）时给足首帧窗口
        ///      （静止桌面 WAIT_TIMEOUT / 无 FrameArrived 是正常语义），窗口耗尽才降级；
        ///      窗口内先用一次性 BitBlt 帧垫首帧，观众不黑屏干等；
        ///   2) BitBlt 仅是临时兜底：每 10 s 重试一次 GPU 后端，桌面一动立即爬回 DDA/WGC；
        ///      WGC 上也会周期重试 DDA（故障恢复后爬回首选后端）；
        ///   3) 运行期 GPU 后端死掉（D04：连续 Start 失败 / 持续不健康）→ 重新进入降级链；
        ///   4) 每次降级记录 fallbackReason/fallbackAt，进 StatusText（可观测）。</summary>
        private static byte[]? CaptureBgraWithProbe(out int width, out int height)
        {
            width = 0; height = 0;
            if (_captureMode != 0) return Capture.CaptureBgra(out width, out height);   // 显式模式：不探测、不自动降级

            if (_captureProbeComplete)
            {
                CheckCaptureHealth();   // ★ P0-A(D04)：运行期健康检查，必要时重进降级链
                if (!_captureProbeComplete)
                    return CaptureBgraWithProbe(out width, out height);
                // ★ P0-A(D01)：BitBlt/WGC 上周期性重试更优 GPU 后端；成功即热切并返回其首帧，
                //   失败则继续用当前后端出本拍，GpuRetrySeconds 后再试。RequestCaptureRetry 可立即触发。
#if WGC
                bool onFallbackBackend = Capture is BitBltCapture || Capture is WgcCapture;
#else
                bool onFallbackBackend = Capture is BitBltCapture;
#endif
                bool retryRequested = _captureRetryRequested;
                _captureRetryRequested = false;
                if (onFallbackBackend && (retryRequested || Stopwatch.GetTimestamp() >= _nextGpuRetryAt))
                {
                    byte[]? adopted = TryAdoptDdaBackend(out width, out height);
                    if (adopted != null) return adopted;
#if WGC
                    if (Capture is BitBltCapture)
                    {
                        adopted = TryAdoptWgcBackend(out width, out height);
                        if (adopted != null) return adopted;
                    }
#endif
                    _nextGpuRetryAt = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (double)GpuRetrySeconds);
                }
                return Capture.CaptureBgra(out width, out height);
            }

            // ── 起播探测：DDA → WGC → BitBlt ──
            // ★ DDA 探测：DxgiDdaCapture 构造时即尝试 DuplicateOutput，失败抛异常
            //   → 降级 WGC；WGC 无首帧 → 降级 BitBlt。
            if (Capture is DxgiDdaCapture dda)
            {
                if (_probeClock == null) { _probeClock = Stopwatch.StartNew(); _probeSeeded = false; }
                byte[]? frame = null;
                string reason = "";
                try { frame = dda.CaptureBgra(out width, out height); }
                catch (Exception ex) { width = height = 0; reason = ex.GetType().Name + "：" + ex.Message; }
                if (frame != null && frame.Length > 0 && dda.IsActive)
                {
                    RelayAgent.LogQuiet("[capture] 采用 DXGI-DDA（出帧 " + width + "x" + height + "）");
                    _captureProbeComplete = true;
                    _probeClock = null;
                    lock (Gate) { _captureFallbackReason = ""; _captureFallbackAtUtc = default; }
                    return frame;
                }
                if (reason.Length == 0) reason = dda.LastError.Length > 0 ? dda.LastError : "DDA 未出帧";
                // ★ P0-A：DDA 尚未出首帧（静止桌面 WAIT_TIMEOUT / 建立中）≠ 后端不可用：给足窗口继续等，
                //   窗口内先用一次性 BitBlt 垫首帧（观众不黑屏干等），桌面一动立即由 DDA 出帧。
                //   仅当「连续 Start 失败达阈值」或「窗口耗尽」才降级 WGC，且之后仍周期性重试回 DDA。
                bool ddaWindowExpired = dda.IsActive
                    ? _probeClock.ElapsedMilliseconds >= DdaFirstFrameWindowMs
                    : _probeClock.ElapsedMilliseconds >= DdaEstablishWindowMs;
                if (!ddaWindowExpired && dda.ConsecutiveFailures < DdaDeadFailureCount)
                {
                    if (!_probeSeeded)
                    {
                        _probeSeeded = true;
                        var seeder = new BitBltCapture();
                        byte[]? seed = seeder.CaptureBgra(out width, out height);
                        seeder.Dispose();
                        if (seed != null)
                        {
                            RelayAgent.LogQuiet("[capture] DXGI-DDA 首帧前 BitBlt 垫帧，继续等 DDA 首帧");
                            return seed;
                        }
                    }
                    Thread.Sleep(10);
                    return null;
                }
                RelayAgent.LogQuiet("[capture] DXGI-DDA 降级：" + reason);
                dda.Dispose();
                _probeClock = null;
                _captureSwapGeneration++;
                lock (Gate) { _captureFallbackReason = SanitizePipe(reason); _captureFallbackAtUtc = DateTime.UtcNow; }
#if WGC
                Capture = new WgcCapture();
                return CaptureBgraWithProbe(out width, out height);
#else
                FallbackToBitBlt(reason);
                return Capture.CaptureBgra(out width, out height);
#endif
            }
#if WGC
            if (Capture is WgcCapture wgc)
            {
                if (_probeClock == null) { _probeClock = Stopwatch.StartNew(); _probeSeeded = false; }
                byte[]? frame = null;
                string reason = "";
                try { frame = wgc.CaptureBgra(out width, out height); }
                catch (Exception ex) { width = height = 0; reason = ex.GetType().Name; }
                if (frame != null && frame.Length > 0 && wgc.IsActive && wgc.FrameSequence > 0)
                {
                    RelayAgent.LogQuiet("[capture] 自动探测采用 WGC（实际出帧 sequence=" + wgc.FrameSequence + "）");
                    _captureProbeComplete = true;
                    _probeClock = null;
                    _nextGpuRetryAt = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (double)GpuRetrySeconds);
                    return frame;
                }
                if (reason.Length == 0) reason = !wgc.IsActive ? "WGC 未进入 running 状态"
                    : wgc.FrameSequence == 0 ? "WGC 无真实首帧" : "WGC 帧读回失败";
                // ★ P0-A：WGC 已 running 但 FrameSequence==0 = 静止桌面无 FrameArrived，给足首帧窗口
                //   （窗口内 BitBlt 垫首帧）；未 running 给建立窗口。窗口耗尽才降级 BitBlt（临时兜底，
                //   之后每 10 s 自动重试回 DDA/WGC，绝不再永久锁死）。
                bool wgcWindowExpired = wgc.IsActive
                    ? _probeClock.ElapsedMilliseconds >= WgcFirstFrameWindowMs
                    : _probeClock.ElapsedMilliseconds >= DdaEstablishWindowMs;
                if (!wgcWindowExpired)
                {
                    if (!_probeSeeded)
                    {
                        _probeSeeded = true;
                        var seeder = new BitBltCapture();
                        byte[]? seed = seeder.CaptureBgra(out width, out height);
                        seeder.Dispose();
                        if (seed != null) return seed;
                    }
                    Thread.Sleep(10);
                    return null;
                }
                FallbackToBitBlt(reason);
                byte[]? fallback = Capture.CaptureBgra(out width, out height);
                if (fallback != null) RelayAgent.LogQuiet("[capture] BitBlt 降级后已抓到首帧");
                return fallback;
            }
#endif
            if (Capture is BitBltCapture) FallbackToBitBlt("探测链末端 BitBlt");
            return Capture.CaptureBgra(out width, out height);
        }

        /// <summary>★ P0-A(D01)：BitBlt/WGC 兜底期间尝试重新接管 DDA。成功（拿到首帧）即热切 Capture 并返回该帧；
        /// 失败销毁候选并返回 null（保持现后端）。静止桌面拿不到新帧 → 接管失败，等下一周期。</summary>
        private static byte[]? TryAdoptDdaBackend(out int width, out int height)
        {
            width = 0; height = 0;
            var candidate = new DxgiDdaCapture();
            try
            {
                byte[]? frame = candidate.CaptureBgra(out width, out height);
                if (frame != null && frame.Length > 0 && candidate.IsActive)
                {
                    Capture.Dispose();
                    Capture = candidate;
                    _captureSwapGeneration++;
                    lock (Gate) { _captureFallbackReason = ""; _captureFallbackAtUtc = default; }
                    RelayAgent.LogQuiet("[capture] 周期重试成功，回到 DXGI-DDA（" + width + "x" + height + "）");
                    return frame;
                }
            }
            catch (Exception ex)
            {
                RelayAgent.LogQuiet("[capture] 周期重试 DDA 失败：" + ex.GetType().Name + "：" + ex.Message);
            }
            candidate.Dispose();
            return null;
        }

#if WGC
        /// <summary>★ P0-A(D01)：BitBlt 兜底期间尝试重新接管 WGC（DDA 失败后的第二选择）。</summary>
        private static byte[]? TryAdoptWgcBackend(out int width, out int height)
        {
            width = 0; height = 0;
            var candidate = new WgcCapture();
            try
            {
                byte[]? frame = candidate.CaptureBgra(out width, out height);
                if (frame != null && frame.Length > 0 && candidate.IsActive && candidate.FrameSequence > 0)
                {
                    Capture.Dispose();
                    Capture = candidate;
                    _captureSwapGeneration++;
                    RelayAgent.LogQuiet("[capture] 周期重试成功，回到 WGC（" + width + "x" + height + "）");
                    return frame;
                }
            }
            catch (Exception ex)
            {
                RelayAgent.LogQuiet("[capture] 周期重试 WGC 失败：" + ex.GetType().Name + "：" + ex.Message);
            }
            candidate.Dispose();
            return null;
        }
#endif

        /// <summary>★ P0-A(D01)：降级到 BitBlt 临时兜底。探测完成标志只表示「已选定当前后端」，
        /// 不再永久锁死：_nextGpuRetryAt 保证之后周期性重试 GPU 后端。</summary>
        private static void FallbackToBitBlt(string reason)
        {
            string clean = SanitizePipe(reason);
            if (clean.Length == 0) clean = "未知原因";
            RelayAgent.LogQuiet("[capture] 降级 BitBlt 临时兜底：" + clean + "（每 " + GpuRetrySeconds + "s 自动重试 GPU 后端）");
            Capture.Dispose();
            Capture = new BitBltCapture();
            _captureProbeComplete = true;
            _probeClock = null;
            _backendDeadStreak = 0;
            _captureSwapGeneration++;
            _nextGpuRetryAt = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (double)GpuRetrySeconds);
            lock (Gate) { _captureFallbackReason = clean; _captureFallbackAtUtc = DateTime.UtcNow; }
        }

        /// <summary>
        /// ★ P0-A(D04)：运行期健康检查——GPU 后端死掉后重新进入降级链（不再「每帧重试 Start +
        /// 恒返回旧帧」直到永远）。仅 auto 模式（显式模式已在入口处跳过）。
        /// </summary>
        private static void CheckCaptureHealth()
        {
            if (Capture is DxgiDdaCapture dda)
            {
                bool healthy = dda.IsActive && dda.LastError.Length == 0;
                if (healthy) { _backendDeadStreak = 0; return; }
                // 判死：连续 Start 失败达阈值（含指数退避），或持续不健康达 N 拍
                bool dead = dda.ConsecutiveFailures >= DdaDeadFailureCount
                    || ++_backendDeadStreak >= BackendDeadStreakLimit;
                if (!dead) return;
                string reason = dda.LastError.Length > 0 ? dda.LastError : "DDA 停止出帧";
                RelayAgent.LogQuiet("[capture] DDA 运行期失败，重新进入降级链：" + reason);
                dda.Dispose();
                _probeClock = null;
                _backendDeadStreak = 0;
                lock (Gate) { _captureFallbackReason = SanitizePipe(reason); _captureFallbackAtUtc = DateTime.UtcNow; }
#if WGC
                _captureProbeComplete = false;
                _captureSwapGeneration++;
                Capture = new WgcCapture();
#else
                Capture = new BitBltCapture();
                _captureProbeComplete = true;
                _captureSwapGeneration++;
                _nextGpuRetryAt = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (double)GpuRetrySeconds);
#endif
                return;
            }
#if WGC
            if (Capture is WgcCapture wgc)
            {
                if (wgc.IsActive) { _backendDeadStreak = 0; return; }
                // WGC 无失败计数器（Start 失败静默重试），以连续拍数判死
                if (++_backendDeadStreak < BackendDeadStreakLimit) return;
                RelayAgent.LogQuiet("[capture] WGC 运行期失败（连续 " + BackendDeadStreakLimit + " 拍未 running），重新进入降级链");
                _probeClock = null;
                _backendDeadStreak = 0;
                FallbackToBitBlt("WGC 运行期失败（连续 " + BackendDeadStreakLimit + " 拍未 running）");
            }
#endif
        }

        /// <summary>距下一次 GPU 后端重试的毫秒数（仅当前处于兜底/降级后端时有意义，DDA 上恒 0）。</summary>
        private static long GpuRetryCountdown()
        {
            if (Capture is DxgiDdaCapture) return 0;
            long remain = _nextGpuRetryAt - Stopwatch.GetTimestamp();
            return remain > 0 ? remain * 1000 / Stopwatch.Frequency : 0;
        }

        /// <summary>
        /// <summary>★ P0-H：当前抓屏后端所在 GPU（VendorId + 名称）。BitBlt 是 CPU GDI，无 GPU 概念。</summary>
        private static (uint Vendor, string Name) CaptureGpuInfo()
        {
            switch (Capture)
            {
                case DxgiDdaCapture dda: return (dda.CaptureAdapterVendorId, dda.CaptureAdapterName);
#if WGC
                case WgcCapture wgc: return (wgc.CaptureAdapterVendorId, wgc.CaptureAdapterName);
#endif
                default: return (0, "BitBlt(CPU)");
            }
        }

        /// 视频编码器降级链：NVENC SDK 直通 → MF 硬编（内部再落软编）。
        /// ★ 惰性：NVENC 的 DLL 加载 / D3D11 设备 / OpenEncodeSessionEx 都发生在
        ///   TryInitializeCandidate 里（后台视频线程），不进启动路径、不阻塞 UI。
        /// ★ 静默降级：每个候选都实际 TryInitialize，失败即 Dispose 换下一级；
        ///   只有全部失败才抛（调用方进入 error 态），NVENC 绝不是单点故障。
        /// 开关 SOULLINK_ENCODER_MODE：
        ///   0/缺省 = auto（NVENC SDK → MF）
        ///   1 = 强制 NVENC SDK（失败即抛，用于诊断）
        ///   2 = 强制 MF（跳过 NVENC SDK）
        /// </summary>
        private static IFrameH264Encoder TryInitializeCandidate(IFrameH264Encoder candidate, int ew, int eh, int fps, VideoParameters parameters, out string failReason)
        {
            failReason = "";
            try
            {
                if (candidate.TryInitialize(ew, eh, fps, parameters.Bitrate, parameters.Preset, parameters.Vbv)) return candidate;
                failReason = candidate.LastError;
            }
            catch (Exception ex) { failReason = ex.GetType().Name + "：" + ex.Message; }
            try { candidate.Dispose(); } catch { }
            return null!;
        }

        private static IFrameH264Encoder CreateEncoder(int ew, int eh, int fps, VideoParameters parameters)
        {
            string raw = Environment.GetEnvironmentVariable("SOULLINK_ENCODER_MODE") ?? "";
            int mode = int.TryParse(raw, out int m) ? m : 0;

            // ★ P0-H：打印一次 GPU 拓扑（用户排障全靠这行）
            GpuAdapter.LogTopologyOnce();

            // ★ P0-G/P0-H：NVENC 只在机器确实有 NVIDIA GPU 时尝试（Intel/AMD/核显一律走 MF，
            //   vendor 中立 MFT 枚举天然覆盖 QSV/AMF）；探测失败一次即标记，本进程内不再重试。
            if (mode != 2 && !_nvencUnavailable && GpuAdapter.HasNvidiaAdapter())
            {
                RelayAgent.LogQuiet("[video] 尝试 NVENC SDK 直通编码器");
                var nvenc = TryInitializeCandidate(new NvencSdkEncoder(), ew, eh, fps, parameters, out string nvFail);
                if (nvenc != null) return nvenc;
                // ★ P0-G：失败一次即标记，本进程内不再重试——避免「编码器按需 IDR 重建 → 每次都
                //   重试 NVENC → 每秒 2-3 次」的无限循环。显式热切抓屏后端时才重置。
                _nvencUnavailable = true;
                RelayAgent.LogQuiet("[video] NVENC SDK 不可用，降级 MF（本进程内不再重试 NVENC）：" + nvFail);
                if (mode == 1)
                    throw new InvalidOperationException("SOULLINK_ENCODER_MODE=1 强制 NVENC，但 NVENC 不可用：" + nvFail);
            }

            var mf = TryInitializeCandidate(new MfFrameEncoder(), ew, eh, fps, parameters, out string mfFail);
            if (mf != null) return mf;
            throw new InvalidOperationException("H.264 编码器初始化失败：" + mfFail);
        }

        public static string CaptureRoot
        {
            get
            {
                string? env = Environment.GetEnvironmentVariable("SOULLINK_CAPTURE_DIR");
                if (!string.IsNullOrWhiteSpace(env)) return env!;
                string exeDir;
                try { exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "."; }
                catch { exeDir = "."; }
                string preferred = Path.Combine(exeDir, "capture");
                if (CanCreate(preferred)) return preferred;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SoulLinkShare", "capture");
            }
        }

        private static bool CanCreate(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>★ T34：当前**配置**的目标帧率（供 /api/info 与页面区分显示）。</summary>
        internal static int CurrentFps => _fps;

        /// <summary>
        /// ★ T34：最近 Ring 内按真实帧时间跨度算出的**实测**帧率。
        ///   与 CurrentFps 的区别：CurrentFps 是「期望值」，这个是「实际产出了多少」。
        ///   用户看到的浏览器 `fps: 66.0 (render)` 是**渲染**帧率（可高于接收帧率），
        ///   两者不是一回事 —— 页面必须分开显示才不误导。
        /// </summary>
        internal static double MeasuredFps
        {
            get
            {
                lock (Gate)
                {
                    if (Ring.Count < 2) return 0;
                    double sec = (Ring[^1].At - Ring[0].At).TotalSeconds;
                    return sec > 0.001 ? (Ring.Count - 1) / sec : 0;
                }
            }
        }

        public static string StatusText()
        {
            lock (Gate)
            {
                long bytes = 0;
                foreach (var f in Ring) bytes += f.Nal.Length;
                double actualFps = Ring.Count >= 2 ? (Ring.Count - 1) / Math.Max(0.001, (Ring[^1].At - Ring[0].At).TotalSeconds) : 0;
                return (_worker?.IsAlive == true ? "running" : "stopped") + "|fps=" + _fps + "|quality=" + _quality
                    + "|frames=" + Ring.Count + "|bytes=" + bytes + "|total=" + _totalFrames
                    + "|size=" + _width + "x" + _height + "|session=" + _sessionId
                    + "|capture=" + Capture.Name + "|codec=H264|viewers=" + Viewers.Count
                    + "|pipelineRunning=" + (_worker?.IsAlive == true ? 1 : 0)
                    + "|recording=" + (_recordRequested ? 1 : 0)
                    + "|currentFps=" + _fps
                    + "|actualFps=" + actualFps.ToString("0.0") + "|liveFps=" + StreamFps + "|recordFps=" + RecordFps
                    + "|captureMs=" + _captureMs + "|convertMs=" + _convertMs + "|encodeMs=" + _encodeMs
                    + "|loop=" + _loopSequence + "|lastCaptureAt=" + _lastCaptureAt.ToString("HH:mm:ss.fff")
                    + "|lastEncodeAt=" + _lastEncodeAt.ToString("HH:mm:ss.fff") + "|lastNalAt=" + _lastNalAt.ToString("HH:mm:ss.fff")
                    + "|staticSkipped=" + _skippedStaticFrames + "|noNal=" + _noNalFrames
                    + "|keepalive=" + _keepaliveFrames + "|zeroMs=" + _zeroMs + "|zeroConvMs=" + _zeroConvertMs
                    + "|queueDrops=" + _viewerQueueDrops + "|queueDepthMax=" + _viewerQueueDepthMax
                + "|inputTrigger=" + _inputTriggerCount + "|keepaliveMs=" + KeepaliveIntervalMs
                    + "|queueAge=" + _queueAgeMs + "|queueAgeMax=" + _queueAgeMaxMs
                    + "|fallbackReason=" + _captureFallbackReason
                    + "|fallbackAtUtc=" + (_captureFallbackAtUtc == default ? "" : _captureFallbackAtUtc.ToString("yyyy-MM-dd HH:mm:ss"))
                    + "|gpuRetryInMs=" + GpuRetryCountdown()
                    + "|bitrateKbps=" + _configuredBitrate + "|mftBitrateKbps=" + _negotiatedBitrate
                    + (_lastError.Length > 0 ? "|error=" + _lastError : "");
            }
        }

        public static bool Start(int fps, int quality, int keepSeconds, int sessionId, out string message)
        {
            lock (Gate)
            {
                _recordRequested = true;
                RecomputeLifecycle();
                return StartCore(fps, quality, keepSeconds, sessionId, out message);
            }
        }

        private static bool StartCore(int fps, int quality, int keepSeconds, int sessionId, out string message)
        {
            lock (Gate)
            {
                _fps = Math.Clamp(fps, 1, 120);
                _quality = Math.Clamp(quality, 30, 95);
                _keepSeconds = Math.Clamp(keepSeconds, 5, 600);
                _sessionId = sessionId;
                if (_worker != null && _worker.IsAlive)
                {
                    message = "共享 H.264 管线已在运行，" + _fps + "fps，观众=" + Viewers.Count;
                    return true;
                }
                Capture = CreateCapture();
                _captureSwitchRequested = false;
                _stopRequested = false;
                _status = "recording";
                _worker = new Thread(Loop) { IsBackground = true, Name = "SoulLinkVideo" };
                _worker.Start();
                message = "开始共享 H.264 采集编码 " + _fps + "fps，保留 " + _keepSeconds + " 秒（实时链路无 JPEG）";
                return true;
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                _recordRequested = false;
                TrimFrames(DateTime.Now, _fps, _keepSeconds);
                if (Ring.Capacity > 300) Ring.TrimExcess();
                RecomputeLifecycle();
            }
        }

        // Stream viewers have their own bounded queues. Only recording needs a clip-length history.
        private static void TrimFrames(DateTime now, int fps, int keepSeconds)
        {
            int seconds = _recordRequested ? keepSeconds : 1;
            int limit = _recordRequested ? Math.Max(300, fps * seconds * 2) : fps + 1;
            var cutoff = now.AddSeconds(-seconds);
            int remove = Math.Max(0, Ring.Count - limit);
            while (remove < Ring.Count && Ring[remove].At < cutoff) remove++;
            if (remove > 0) Ring.RemoveRange(0, remove);
        }

        private static void RecomputeLifecycle()
        {
            // 管线只要还有录像或观众就继续；只有两者都没有才停止。
            _stopRequested = !_recordRequested && Viewers.Count == 0;
            LifecycleChanged.Set();
        }

        public static void SetRemoteControl(bool enabled)
        {
            lock (Gate)
            {
                _remoteControlEnabled = enabled;
                if (!enabled)
                {
                    foreach (var viewer in Viewers.Values) CloseViewer(viewer);
                    Viewers.Clear();
                }
                RecomputeLifecycle();
            }
        }

        private static void CloseViewer(Viewer viewer)
        {
            lock (viewer.Sync) { viewer.Closed = true; viewer.Signal(); }
        }

        public static void Shutdown(int waitMs = 1500)
        {
            lock (Gate) { _recordRequested = false; SetRemoteControl(false); }
            try { var t = _worker; if (t != null && t.IsAlive) t.Join(waitMs); } catch { }
        }

        private static void Loop()
        {
            IFrameH264Encoder? encoder = null;
            GpuShaderConverter? gpuConverter = null;   // ★ P0-F：PS 着色器零拷贝转换器（与 encoder 同生命周期，取代 VideoProcessor 方案）
            int encodedWidth = 0, encodedHeight = 0;
            long frameIndex = 0;
            byte[]? previousBgra = null;
            byte[]? nv12Buffer = null;
            long previousFrameSeq = -1;   // DDA 后端上一次的 FrameSequence（-1=尚未取得）
            long previousZeroSeq = -1;    // GPU 后端（WGC/DDA）上一次的 FrameSequence（零拷贝分支静止判定用）
            bool zeroCopyPsnrDone = false;   // 零拷贝首帧 PSNR 诊断只跑一次
            int zeroCopyFailStreak = 0;      // ★ P0-E：零拷贝转换/编码连续失败计数（达阈值禁用，防吞帧）
            long nextAt = Stopwatch.GetTimestamp();   // ★ A2：下一次允许抓屏/编码的时间戳（fps 上限门槛）
            int appliedVersion = -1;
            VideoParameters? applied = null;
            int appliedCaptureVersion = _captureModeVersion;
            long appliedCaptureGeneration = -1;   // ★ P0-A：后端实例替换代数（探测/重试/降级/D04 后重置静止基准）
            bool stoppedNormally = false;
            try
            {
                timeBeginPeriod(1);   // ★ P0-E：定时器精度 1ms，消除节拍超调
                while (!_stopRequested)
                {
                    if (_captureSwitchRequested)
                    {
                        int version = _captureModeVersion;
                        _captureSwitchRequested = false;
                        if (version != appliedCaptureVersion)
                        {
                            RelayAgent.LogQuiet("[capture] worker 热切后端 → mode=" + _captureMode);
                            Capture.Dispose(); _probeClock = null; _captureProbeComplete = false;
                            _captureSwapGeneration++;
                            _nvencUnavailable = false;   // ★ P0-G：显式热切后端时重置 NVENC 可用性标记，允许重试
                            lock (Gate) { _captureFallbackReason = ""; _captureFallbackAtUtc = default; }
                            Capture = CreateCapture();
                            encoder?.Dispose(); encoder = null;
                            gpuConverter?.Dispose(); gpuConverter = null;
                            frameIndex = 0; previousBgra = null; previousFrameSeq = -1;
                            appliedCaptureVersion = version;
                        }
                    }
                    // ★ P0-A：后端实例被替换（探测/重试/降级/D04/热切）→ 重置静止判定基准，
                    //   避免旧帧字节比对 / 旧 FrameSequence 把换后端后的第一帧误判为静止。
                    long captureGeneration = _captureSwapGeneration;
                    if (captureGeneration != appliedCaptureGeneration)
                    {
                        previousBgra = null; previousFrameSeq = -1; previousZeroSeq = -1;
                        appliedCaptureGeneration = captureGeneration;
                    }
                    VideoParameters parameters;
                    int parameterVersion;
                    lock (Gate) { parameters = _videoParameters; parameterVersion = _videoParameterVersion; }
                    if (parameterVersion != appliedVersion && encoder != null)
                    {
                        string oldDescription = applied?.Describe() ?? "未记录";
                        RelayAgent.LogQuiet("[video] 参数热更新：" + oldDescription + " → " + parameters.Describe());
                        // 仅 worker 替换编码器；保留观众 HTTP 连接、字典和各自 Pending 队列。
                        encoder.Dispose(); encoder = null;
                        gpuConverter?.Dispose(); gpuConverter = null;
                        frameIndex = 0; previousBgra = null;
                        // ★ 热切后强制按需 IDR：与 432 行静止判定解耦。即使画面完全静止，
                        //   也必须让重建后的编码器首帧必出 SPS/PPS+IDR，否则前端 10s 看门狗
                        //   在「热切后静止」场景等不到新关键帧而断流（W8 根因）。
                        lock (Gate) { _forceKeyFrame = true; }
                        // 保留 Pending：旧帧之后由新 IDR 续接，viewer 不重建、不断开。
                    }
                    int fps, keepSec;
                    bool hasViewers;
                    lock (Gate)
                    {
                        hasViewers = Viewers.Count > 0;
                        fps = hasViewers ? StreamFps : RecordFps;
                        keepSec = _keepSeconds;
                        _fps = fps;
                    }
                    // ★ A2：抓屏前节流——帧到达即编码 + fps 上限，消除「先抓后等」的陈旧帧。
                    //   到拍（距上次编码 ≥ 1/fps）才抓屏，抓到即编码；未到拍则挂起等到拍，
                    //   期间被生命周期事件（stop/热切/参数更新/IDR 请求）唤醒则回顶重新判断。
                    //   forceKeyFrame（按需 IDR）必须立即编码，绕过节流。
                    //   ★ P0 输入触发（2026-11-24）：输入活动同样绕过节拍立即抓帧——
                    //     注入后的光标/界面变化是用户「预期马上看到」的内容，不能等下一拍。
                    //     只消费一次性标志；真实帧仍走 fps 节流（不破坏节拍器累计语义）。
                    bool inputTrigger = _inputActivityPending;
                    _inputActivityPending = false;
                    // ★ T29：只有 urgent（up/click/releaseall）才允许抓屏走短超时，
                    //   move 触发的普通 inputTrigger 仍走完整 100ms —— 否则拖动会退化成复用旧帧。
                    bool urgentInput = ConsumeUrgentInput();
                    bool forceKeyFrame = ConsumeKeyFrameRequest();
                    if (!forceKeyFrame && !inputTrigger && !WaitUntilNextSlot(ref nextAt))
                    {
                        continue;
                    }
                    // ★★ 零拷贝分支（GPU 零拷贝阶段B）：WGC/DDA 纹理直通 NVENC，不读回 CPU、不做 CPU 转换。
                    //   ★ 触发条件：converter 已建 + GPU 后端（WGC/DDA，接口 TryCaptureBgraTexture 支持）+ NVENC SDK。
                    //   ★ 核心语义（修复空转 bug）：只有「成功产出 NAL」才 continue；
                    //     TryCaptureBgraTexture 返回 false（静止无帧 / 共享句柄拿不到 / BitBlt 不支持）
                    //     或 Convert/Encode 失败 → 一律 fall through 到下方 CPU 路径，绝不空转死循环。
                    // ★★ 2026-11-24（T29/T31）：把「本拍是 urgent 补帧」与「当前 fps」告诉抓屏层 ★★
                    //
                    //   T29：urgent 补帧不要再为静止桌面白等（原 100ms + 100ms = 200ms）。
                    //   T31：抓屏超时改为**跟随帧间隔**（fps=60 → 16ms，clamp [8,20]），
                    //        根治「一次静止抓屏阻塞 100ms = 6 个节拍 → urgent 最坏等 115ms」
                    //        导致的「时好时坏」。
                    //   ★ 只影响抓屏超时；静态省 CPU / 保活 250ms / 零拷贝门控 均不变。
                    if (Capture is DxgiDdaCapture ddaForTrigger)
                    {
                        ddaForTrigger.SetTargetFps(fps);
                        ddaForTrigger.InputTriggeredFrame = urgentInput;
                        // ★★ T33 安全网：每拍（廉价，几次浮点比较）确认超时 > 帧间隔。
                        //   若不自洽，AcquireNextFrame 会**每帧必超时 → 只能复用旧帧 →
                        //   画面延迟逐帧累积**（用户实测 3 帧恶化到 9 帧）。
                        //   这个 bug 曾因整数除法 `1000/_fps` 静默引入（fps=60 得 16ms
                        //   而真实间隔 16.67ms），只在日志里表现为「captureMs 恒等于 acqMs」，
                        //   极难一眼看出。这里改成**一旦不自洽就立刻打日志**。
                        if (!ddaForTrigger.IsAcquireTimeoutSane() && _lastSaneWarnAt != fps)
                        {
                            _lastSaneWarnAt = fps;
                            RelayAgent.LogQuiet("[video] 警告：抓屏超时 <= 帧间隔（fps=" + fps
                                + ", acqMs=" + ddaForTrigger.LastAcquireTimeoutMs
                                + "）→ 会每帧必超时、只能复用旧帧，画面延迟会逐帧累积");
                        }
                    }

                    if (gpuConverter != null && encoder is NvencSdkEncoder nvencZero)
                    {
                        bool zeroHandled = false;   // 本拍是否已由零拷贝成功产出 NAL
                        var zeroClock = Stopwatch.StartNew();   // ★ P0-F：零拷贝单帧耗时埋点
                        try
                        {
                            if (Capture.TryCaptureBgraTexture(out IntPtr sharedHandle, out int zw, out int zh)
                                && sharedHandle != IntPtr.Zero && zw > 0 && zh > 0)
                            {
                                // 静止判定：后端 FrameSequence 变化才编码（DDA 的 WAIT_TIMEOUT 已在此前返回 false，
                                //   WGC 靠 FrameSequence 判定静止；不读回 CPU 字节比对）
                                long curSeq = Capture.FrameSequence;
                                bool zChanged = previousZeroSeq < 0 || curSeq != previousZeroSeq;
                                // ★★ 2026-11-24（T25）：inputTrigger 必须能强制产出一帧 ★★
                                //   原来只有 forceKeyFrame || zChanged，而 DDA 的 FrameSequence
                                //   **在只有光标移动时不递增**（光标属合成层，不是桌面内容）。
                                //   → 拖动时流畅是因为**图标真的在移动**；
                                //   → 松手那一下图标已就位、桌面静止 → zChanged=false → **一帧都不出**，
                                //     前端只能等保温节拍（250ms），用户看到的就是「没松开」。
                                //   inputTrigger 是一次性标志（:771-772 已消费），
                                //   所以这里放行**不会**让静止画面持续编码。
                                if (forceKeyFrame || zChanged || inputTrigger)
                                {
                                    // 按需 IDR：NVENC 精确按需（不重建，下一帧 FORCEIDR）
                                    if (forceKeyFrame) nvencZero.RequestKeyFrame();
                                    var convClock = Stopwatch.StartNew();   // ★ P0-F：纯 GPU 转换耗时
                                    if (gpuConverter.ConvertFromSharedHandle(sharedHandle, zw, zh, out var nv12Tex) && nv12Tex != null)
                                    {
                                        convClock.Stop();
                                        _zeroConvertMs = convClock.ElapsedMilliseconds;
                                        byte[]? zNal = nvencZero.EncodeD3D11Texture(nv12Tex, frameIndex * 1_000_000L / fps);
                                        if (zNal != null && zNal.Length > 0)
                                        {
                                            nv12Buffer = null;
                                            zeroCopyFailStreak = 0;   // ★ P0-E：成功清零连续失败计数
                                            frameIndex++; _noNalFrames = 0; _lastNalAt = DateTime.UtcNow;
                                            var zFrame = new VideoFrame { Nal = zNal, At = DateTime.Now, KeyFrame = IsKeyFrame(zNal) };
                                            lock (Gate)
                                            {
                                                Ring.Add(zFrame);
                                                _totalFrames++;
                                                _captureMs = 0; _convertMs = 0; _encodeMs = 0;   // 零拷贝：CPU 段为 0
                                                _zeroMs = zeroClock.ElapsedMilliseconds;        // ★ P0-F：零拷贝整段耗时
                                                _status = "recording"; _lastError = "";
                                                if (_totalFrames % 30 == 0)
                                                    RelayAgent.LogQuiet("[video] 零拷贝管线 total=" + _totalFrames + " zeroMs=" + _zeroMs
                                                        + " convMs=" + _zeroConvertMs + " viewers=" + Viewers.Count + " loop=" + _loopSequence
                                                        + " queueDrops=" + _viewerQueueDrops + " queueDepthMax=" + _viewerQueueDepthMax);
                                                TrimFrames(zFrame.At, fps, keepSec);
                                                PublishToViewers(zFrame);
                                            }
                                            // ★ 零拷贝首帧 PSNR 诊断（只跑一次）：GPU NV12 vs CPU 路径 NV12 同帧对比
                                            if (!zeroCopyPsnrDone)
                                            {
                                                zeroCopyPsnrDone = true;
                                                try
                                                {
                                                    int zew = zw & ~1, zeh = zh & ~1;
                                                    var gpuNv12 = gpuConverter.ReadBackNv12(nv12Tex, zew, zeh);
                                                    // ★ P0-F：读回同一帧的 BGRA（转换器内部副本）做同帧对比——
                                                    //   动态桌面下若再抓一拍 CPU 帧，两帧内容不同会拉低 PSNR。
                                                    var cpuBgra = gpuConverter.ReadBackBgra(zew, zeh);
                                                    if (gpuNv12 != null && cpuBgra != null)
                                                    {
                                                        var cpuNv12 = BgraToNv12(cpuBgra, zew, zeh, zew * 4);
                                                        double psnr = GpuColorConverter.ComputePsnrY(gpuNv12, cpuNv12, zew, zeh);
                                                        RelayAgent.LogQuiet("[video] 零拷贝色差 PSNR(Y) = " + (psnr >= 99 ? "∞(完全相同)" : psnr.ToString("0.0") + " dB") + " 尺寸=" + zew + "x" + zeh + "（≥40dB 达标）");
                                                    }
                                                }
                                                catch (Exception ex) { RelayAgent.LogQuiet("[video] 零拷贝 PSNR 诊断异常：" + ex.Message); }
                                            }
                                            previousZeroSeq = curSeq;
                                            zeroHandled = true;
                                        }
                                        else
                                        {
                                            // ★ P0-E：本拍纹理已被消费但编码无输出 → 不回退 CPU
                                            //   （回退只会把陈旧 _lastBgra 当新帧编码成重复帧）；
                                            //   对齐 CPU 静止判定基准，防止下一拍把陈旧帧当新帧。
                                            zeroHandled = true;
                                            previousFrameSeq = curSeq;
                                            zeroCopyFailStreak++;
                                        }
                                    }
                                    else
                                    {
                                        // ★ P0-E：转换失败（RDP 内 VideoProcessor 不可用等）→ 同样不回退 CPU：
                                        //   每次失败若回退 CPU 都会把刚被零拷贝消费掉的一帧变成「陈旧帧重编码」，
                                        //   动态场景直接砍半帧率。
                                        zeroHandled = true;
                                        previousFrameSeq = curSeq;
                                        zeroCopyFailStreak++;
                                    }
                                }
                                if (curSeq != previousZeroSeq) previousZeroSeq = curSeq;
                            }
                        }
                        catch (Exception ex)
                        {
                            // 零拷贝任何异常 → 禁用 GPU 直通回退 CPU，绝不进入崩溃路径；
                            // 本拍不回退 CPU（纹理可能已被消费，回退会编码陈旧帧）。
                            RelayAgent.LogQuiet("[video] 零拷贝失败，降级 CPU：" + ex.Message);
                            gpuConverter?.Dispose(); gpuConverter = null;
                            zeroHandled = true;
                        }
                        // ★ P0-E：连续失败达阈值 → 禁用 GPU 直通（当前 RDP 内跨设备共享转换不可用，
                        //   每次失败都吞掉一帧真实画面，直接把帧率砍半）。D03/D14 修好转换器后，
                        //   编码器重建会重新创建 converter 自动重试。
                        if (zeroCopyFailStreak >= ZeroCopyFailDisableThreshold && gpuConverter != null)
                        {
                            RelayAgent.LogQuiet("[video] 零拷贝连续失败 " + zeroCopyFailStreak + " 次，禁用 GPU 直通回退纯 CPU 路径（防吞帧）");
                            gpuConverter?.Dispose(); gpuConverter = null;
                        }
                        if (zeroHandled)
                        {
                            AdvanceSlot(ref nextAt, fps);
                            continue;   // 零拷贝成功产出 NAL / 已消费本拍纹理，本拍结束
                        }
                        // zeroHandled=false：静止 / 无帧 / 共享句柄拿不到 → 落到下方 CPU 路径兜底
                    }

                    var frameStart = Stopwatch.StartNew();
                    var bgra = CaptureBgraWithProbe(out int width, out int height);
                    // ★ T29：抓完立刻复位，避免影响后续拍（下一拍若不是输入触发就该等满超时）
                    if (Capture is DxgiDdaCapture ddaReset) ddaReset.InputTriggeredFrame = false;
                    _loopSequence++; _lastCaptureAt = DateTime.UtcNow;
                    long captureMs = frameStart.ElapsedMilliseconds;
                    if (bgra == null || width <= 0 || height <= 0)
                    {
                        lock (Gate) { _status = "waiting"; if (_probeClock == null) _lastError = "抓屏没有返回原始帧"; }
                        // ★ P0-A：探测窗口/静止桌面期间抓屏为 null 时，按需 IDR 不能被吞掉——
                        //   重新置位 _forceKeyFrame，下一拍继续尝试强制出帧（新观众首帧不挂死）。
                        if (forceKeyFrame) lock (Gate) { _forceKeyFrame = true; }
                        // ★★ 2026-11-24（T25 第③条）：inputTrigger 同样不能被吞掉 ★★
                        //   抓屏这一拍没返回图（DDA WAIT_TIMEOUT / 探测窗口期），
                        //   若就此 continue，这次输入活动就白丢了 —— 用户点一下、松一下，
                        //   恰好撞上抓屏空转那一拍，画面就永远不刷新。
                        //   与上面 forceKeyFrame 同法：**重新置位**，下一拍继续强制出帧。
                        if (inputTrigger) _inputActivityPending = true;
                        IdleWait();
                        continue;
                    }
                    if (forceKeyFrame && encoder != null)
                    {
                        if (encoder is NvencSdkEncoder)
                        {
                            // NVENC 精确按需 IDR：不重建，下一帧 FORCEIDR
                            encoder.RequestKeyFrame();
                        }
                        else
                        {
                            // MF 无精确 IDR：Dispose 重建，首帧必 IDR
                            encoder.Dispose();
                            encoder = null;
                            frameIndex = 0;
                        }
                    }
                    // ★ A1：DDA 后端以 AcquireNextFrame 返回的 FRAME_INFO（AccumulatedFrames 增长）为
                    //   「桌面真的更新过」的权威判据，替代全帧 SequenceEqual。RDP 慢合成下 AcquireNextFrame
                    //   超时复用旧 _lastBgra，字节比对的基准帧陈旧，操作画面被误判静止 → staticSkipped 暴涨。
                    //   DDA 出帧序号增长 = 桌面确实更新 → 绝不跳过（操作画面绝不误判静止）。
                    bool desktopChanged;
                    long currentFrameSeq = -1;
                    if (Capture is DxgiDdaCapture ddaBackend)
                    {
                        currentFrameSeq = ddaBackend.FrameSequence;
                        desktopChanged = previousFrameSeq < 0 || currentFrameSeq != previousFrameSeq;
                    }
                    else
                    {
                        // 其它后端（BitBlt / WGC）无 DDA 的 FRAME_INFO，沿用全帧字节比较。
                        desktopChanged = previousBgra == null || !previousBgra.AsSpan().SequenceEqual(bgra);
                    }
                    int ew = width & ~1, eh = height & ~1;
                    // ★ P0-D（2026-11-24）帧 pacing 与保活策略：
                    //   1) 真实帧 = 桌面更新帧，到达即编码（A2 的 fps 上限节拍门只限速不积压）；
                    //   2) 静止 + 有观众 = 每 KeepaliveIntervalMs 重编码一帧重复帧（2fps），
                    //      维持 /stream 帧流；真机实测静止桌面 25s 仅 3 帧 → 前端看门狗断流重连；
                    //   3) 静止 + 无观众（仅录制）= 完全跳过（录像不掺重复帧，只收真实帧）；
                    //   4) 新观众 = forceKeyFrame 绕过静止判定立即出 IDR（既有逻辑）。
                    bool keepaliveDue = hasViewers && encoder != null
                        && Stopwatch.GetTimestamp() >= _keepaliveNextAt;
                    bool keepaliveFrame = !forceKeyFrame && !desktopChanged && keepaliveDue;
                    // ★★ 2026-11-24（T25）：inputTrigger 放行 ★★
                    //   这是「松手不出帧」的**第二道闸门**，也是最隐蔽的一道：
                    //   inputTrigger 在 :774 已经让它「进循环」，但这里又按 desktopChanged 判了一次，
                    //   松手瞬间桌面静止 → 直接 continue → **inputTrigger 被白白消费掉**。
                    //   → 表现：点了没反应、松手不落位、要等 250ms 保温帧。
                    //   inputTrigger 是一次性标志，放行一次即止，**不会**破坏静止省 CPU 语义。
                    if (!forceKeyFrame && !desktopChanged && !keepaliveDue && !inputTrigger)
                    {
                        _skippedStaticFrames++;
                        IdleWait();
                        continue;
                    }
                    // 到拍或按需 IDR：提交 previous 供下一轮静止判定（latest-wins）。
                    if (Capture is DxgiDdaCapture) previousFrameSeq = currentFrameSeq;
                    previousBgra = bgra;
                    if (forceKeyFrame)
                        RelayAgent.LogQuiet("[video] 新观众按需 IDR：即使画面静止也强制编码，不再被去重跳过");

                    if (encoder == null || encodedWidth != ew || encodedHeight != eh)
                    {
                        previousBgra = null;
                        encoder?.Dispose();
                        gpuConverter?.Dispose(); gpuConverter = null;
                        encoder = CreateEncoder(ew, eh, fps, parameters);
                        applied = parameters; appliedVersion = parameterVersion;
                        encodedWidth = ew; encodedHeight = eh;
                        // ★ P0-F 零拷贝伴生：编码器是 NVENC SDK 且抓屏源同为 NVIDIA（同 GPU）时创建 PS 着色器
                        //   转换器（复用其 device，渲染进编码器注册的 NV12 纹理，绕开 RDP 内不可用的
                        //   VideoProcessor）。★ P0-H：跨 GPU（Intel/AMD 抓屏 + NVENC 编码）共享必失败，
                        //   直接 CPU 路径（EncodeNv12 上传），不浪费帧去试。
                        var (captureVendor, captureName) = CaptureGpuInfo();
                        if (encoder is NvencSdkEncoder nvencEnc && nvencEnc.Device != null
                            && nvencEnc.Context != null && nvencEnc.Nv12InputTexture != null
                            && captureVendor == GpuAdapter.NvidiaVendorId)
                        {
                            try
                            {
                                var conv = new GpuShaderConverter(nvencEnc.Device, nvencEnc.Context, nvencEnc.Nv12InputTexture);
                                if (conv.TryInitialize(ew, eh))
                                {
                                    gpuConverter = conv;
                                    zeroCopyFailStreak = 0;
                                    RelayAgent.LogQuiet("[video] 零拷贝转换器=PS着色器两pass（BGRA→NV12，无 VideoProcessor）");
                                }
                                else { RelayAgent.LogQuiet("[video] GPU 着色器转换器不可用，零拷贝降级 CPU：" + conv.LastError); conv.Dispose(); }
                            }
                            catch (Exception ex) { RelayAgent.LogQuiet("[video] GPU 着色器转换器创建失败，降级 CPU：" + ex.Message); }
                        }
                        RelayAgent.LogQuiet("[video] 编码器=" + encoder.Name + " 配置码率=" + encoder.ConfiguredBitrateKbps
                            + "kbps MFT读回=" + encoder.NegotiatedBitrateKbps + "kbps fps=" + fps + " 尺寸=" + ew + "x" + eh);
                        // ★ P0-H 可观测性：抓屏源/编码器/同 GPU/零拷贝路由一览（用户排障全靠这行）
                        RelayAgent.LogQuiet("[gpu] 抓屏源=" + (captureName.Length > 0 ? captureName : "未知")
                            + "(" + GpuAdapter.VendorHex(captureVendor) + "); 编码器=" + encoder.Name
                            + "; 同GPU=" + (captureVendor == GpuAdapter.NvidiaVendorId && encoder is NvencSdkEncoder ? "true" : "false")
                            + "; 零拷贝=" + (gpuConverter != null ? "可用" : "禁用"));
                        lock (Gate) { _width = ew; _height = eh; _configuredBitrate = encoder.ConfiguredBitrateKbps; _negotiatedBitrate = encoder.NegotiatedBitrateKbps; }
                    }

                    var convertStart = Stopwatch.StartNew();
                    nv12Buffer = BgraToNv12(bgra, ew, eh, width * 4, nv12Buffer);
                    long convertMs = convertStart.ElapsedMilliseconds;
                    var encodeStart = Stopwatch.StartNew();
                    byte[]? nal = encoder.EncodeNv12(nv12Buffer, frameIndex * 1_000_000L / fps);
                    long encodeMs = encodeStart.ElapsedMilliseconds;
                    _lastEncodeAt = DateTime.UtcNow;
                    if (nal != null && nal.Length > 0)
                    {
                        frameIndex++; _noNalFrames = 0; _lastNalAt = DateTime.UtcNow;
                        var frame = new VideoFrame { Nal = nal, At = DateTime.Now, KeyFrame = IsKeyFrame(nal) };
                        lock (Gate)
                        {
                            Ring.Add(frame);
                            _totalFrames++;
                            if (keepaliveFrame) _keepaliveFrames++;   // ★ P0-D：保活帧计数（验证指标）
                            _captureMs = captureMs; _convertMs = convertMs; _encodeMs = encodeMs;
                            _status = "recording"; _lastError = "";
                            if (_totalFrames % 30 == 0)
                                RelayAgent.LogQuiet("[video] 共享管线 total=" + _totalFrames + " viewers=" + Viewers.Count
                                    + " captureMs=" + captureMs + " convertMs=" + convertMs + " encodeMs=" + encodeMs
                    + ((Capture is DxgiDdaCapture lc) ? (" acqMs=" + lc.LastAcquireTimeoutMs) : "")
                                    + " loop=" + _loopSequence + " lastCapture=" + _lastCaptureAt.ToString("HH:mm:ss.fff")
                                    + " lastEncode=" + _lastEncodeAt.ToString("HH:mm:ss.fff") + " lastNal=" + _lastNalAt.ToString("HH:mm:ss.fff")
                                    + " staticSkipped=" + _skippedStaticFrames + " keepalive=" + _keepaliveFrames
                                    + " queueDrops=" + _viewerQueueDrops + " queueDepthMax=" + _viewerQueueDepthMax
                                    + " queueAge=" + _queueAgeMs + " queueAgeMax=" + _queueAgeMaxMs);
                            TrimFrames(frame.At, fps, keepSec);
                            PublishToViewers(frame);
                        }
                    }
                    if (nal == null || nal.Length == 0)
                    {
                        _noNalFrames++;
                        // ★ P0 死锁修复：MF 编码器需连续多帧才吐首个 IDR（AgentWebServer.cs:413 注释自证），
                        //   但 _forceKeyFrame 是「一次性」标志，首帧喂给 MF 后即归零；静止画面第二帧起命中
                        //   :461 的 !forceKeyFrame && !desktopChanged 直接 continue，不再喂帧 → MF 永远凑不齐
                        //   输入帧 → Ring 永不产帧 → 首帧 25s 超时 503。
                        //   修复：本帧若因 forceKeyFrame（按需 IDR）进入编码却仍未产出 NAL，重新置位 
                        //   _forceKeyFrame，让下一轮继续强制喂帧，直到 MF 吐出首个 IDR（受 nextAt fps 节流约束不超速）。
                        if (forceKeyFrame) { lock (Gate) { _forceKeyFrame = true; } }
                    }
                    // ★ P0-D：本拍保活节拍到点（无论编码是否产出 NAL）→ 推进下一次保活时刻。
                    //   真实帧拍不推进（桌面活动期间保活不需要；静止恢复后立即补上）。
                    if (keepaliveDue)
                        _keepaliveNextAt = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (double)KeepaliveIntervalMs / 1000);
                    // ★ P0-F（2026-11-24）：改用累积式节拍器 AdvanceSlot —— 旧写法
                    //   「now + 1/fps」把本拍工作耗时（capture+convert+encode）叠加在等待之上，
                    //   实际周期 = 工作耗时 + 1/fps。1920x1080 实测工作 24ms + 33ms = 57ms/帧
                    //   → 仅 17fps（真机实测 15fps），配置 30fps 名不副实。
                    //   累积式让工作耗时包含在周期内，稳定跑到目标 fps。
                    AdvanceSlot(ref nextAt, fps);
                    if (_loopSequence % 150 == 0)
                        RelayAgent.LogQuiet("[video] 活性 loop=" + _loopSequence + " total=" + _totalFrames
                            + " lastCapture=" + _lastCaptureAt.ToString("HH:mm:ss.fff") + " lastEncode=" + _lastEncodeAt.ToString("HH:mm:ss.fff")
                            + " lastNal=" + _lastNalAt.ToString("HH:mm:ss.fff") + " staticSkipped=" + _skippedStaticFrames + " noNal=" + _noNalFrames);
                }
                stoppedNormally = true;
            }
            catch (Exception ex)
            {
                lock (Gate) { _status = "error"; _lastError = ex.GetType().Name + "：" + ex.Message; }
                RelayAgent.LogQuiet("[video] 共享采集编码失败：" + ex);
            }
            finally
            {
                timeEndPeriod(1);   // ★ P0-E：与 timeBeginPeriod 配对
                try { encoder?.Dispose(); } catch { }
                try { gpuConverter?.Dispose(); } catch { }
                lock (Gate)
                {
                    try { Capture.Dispose(); } catch { }
                    _probeClock = null;
                    _captureProbeComplete = false;
                    _width = _height = 0;
                    Ring.Clear(); Ring.TrimExcess(); _status = "idle"; _worker = null;
                    // A subscriber can arrive after the old worker observes the stop flag.
                    if (stoppedNormally && (_recordRequested || Viewers.Count > 0))
                        StartCore(_fps, _quality, _keepSeconds, _sessionId, out _);
                }
            }
        }

        private static long _lastViewerRecoveryMs;

        // Both capture pipelines call this with Gate held. Only the slow viewer waits for resync.
        private static void PublishToViewers(VideoFrame frame)
        {
            bool needsRecovery = false;
            foreach (var viewer in Viewers.Values)
            {
                lock (viewer.Sync)
                {
                    if (viewer.Closed) continue;
                    bool queued = viewer.Pending.Offer((frame.Nal, frame.At), frame.KeyFrame, out int drops, out bool request);
                    _viewerQueueDrops += drops;
                    needsRecovery |= request;
                    if (queued) viewer.Signal();
                    _viewerQueueDepthMax = Math.Max(_viewerQueueDepthMax, viewer.Pending.Count);
                }
            }
            long now = Environment.TickCount64;
            if (needsRecovery && (_lastViewerRecoveryMs == 0 || now - _lastViewerRecoveryMs >= 1000))
            {
                _lastViewerRecoveryMs = now;
                _forceKeyFrame = true;
            }
        }

        /// <summary>订阅共享 NAL。返回失败时调用方必须给出错误，不允许退回 JPEG 轮询。</summary>
        public static bool TrySubscribe(out int viewerId, out byte[]? initial, out string error)
        {
            viewerId = 0; initial = null; error = "";
            lock (Gate)
            {
                if (!_remoteControlEnabled) { error = "网页远控已关闭"; return false; }
                if (_status == "error")
                {
                    // 编码/抓屏失败可重试：清错误并允许 StartCore 新建 worker，不永久拒绝后续观众。
                    RelayAgent.LogQuiet("[video] 订阅遇 error，清理状态并尝试重启视频管线：" + _lastError);
                    _lastError = ""; _status = "idle";
                }
                viewerId = ++_nextViewerId;
                // An older cached IDR cannot be followed by a live P-frame: the intervening references are missing.
                if (Ring.Count > 0 && Ring[^1].KeyFrame) initial = Ring[^1].Nal;
                Viewers[viewerId] = new Viewer(initial != null);
                _forceKeyFrame = true;
                RecomputeLifecycle();
                using var process = Process.GetCurrentProcess();
                if (StartCore(StreamFps, _quality, _keepSeconds, process.SessionId, out error)) return true;
                Unsubscribe(viewerId);
                viewerId = 0;
                return false;
            }
        }

        public static void Unsubscribe(int viewerId)
        {
            lock (Gate)
            {
                if (Viewers.Remove(viewerId, out var viewer)) CloseViewer(viewer);
                RecomputeLifecycle();
            }
        }

        public static bool HasViewer(int viewerId) { lock (Gate) return Viewers.ContainsKey(viewerId); }

        // ── 按需 IDR 的进程内节流（2026-11-24，T26 第④处修复）──────────────
        //
        //  【为什么必须有】
        //    RequestKeyFrame() 原本只置位 _forceKeyFrame，**没有任何节流**。
        //    HTTP 的 POST /api/idr 有 300ms 限流（AgentWebServer.cs:73 IdrMinIntervalMs），
        //    但**输入路径直接调用会绕过那道 HTTP 限流**。
        //    拖动结束/连点时 urgent 动作可能 10Hz 以上，若每次都强制 IDR：
        //      · IDR 体积是 P 帧的数倍 → 带宽暴涨；
        //      · NVENC 每帧重建 GOP → 编码耗时飙升 → 反而更卡
        //      · Moonlight 的教训（代码里原话）：「狂请求会拥塞崩溃」。
        //
        //  【阈值为什么取 200ms】
        //    · 必须 > 客户端 requestIdr 限流(500ms)? 不必 —— 那是**客户端**发的，与本进程内路径互不相干；
        //    · 必须 > 服务端 HTTP 限流(300ms)? 也不必 —— 两条路径独立计数，取小一点更稳；
        //    · 取 200ms 的理由：urgent 动作（松手/点击）的真实发生频率是人手级别（≤5Hz，即 ≥200ms 间隔），
        //      200ms 恰好覆盖「人手正常操作」而不丢，同时把机械重复（脚本连点、拖拽尾部的抖动）压掉；
        //    · 与保活 250ms 同量级，不会引入比现有节拍更密的 IDR。
        //    → 稳态下最多 5 次 IDR/秒，且只在真有 urgent 输入时；静止时一次都不发。
        private const long KeyFrameMinIntervalMs = 200;
        private static long _lastKeyFrameReqMs;

        /// <summary>
        /// ★ P1 加固：供首帧等待方（AgentWebServer.WriteStreamAsync）主动再触发一次按需 IDR。
        /// 置位 _forceKeyFrame 并唤醒 worker，让 worker 即使画面静止也强制编码下一帧（双保险）。
        ///
        /// ★ 2026-11-24（T26）：加**进程内 200ms 节流**（见上方 KeyFrameMinIntervalMs 的说明）。
        ///   需要无条件置位的老调用方（HTTP /api/idr 已自带 300ms 限流）请用 RequestKeyFrameUnthrottled。
        /// </summary>
        /// <returns>true = 本次确实置位了；false = 被节流跳过。</returns>
        public static bool RequestKeyFrame()
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastKeyFrameReqMs);
            if (now - last < KeyFrameMinIntervalMs) return false;
            Interlocked.Exchange(ref _lastKeyFrameReqMs, now);
            lock (Gate) { _forceKeyFrame = true; }
            LifecycleChanged.Set();
            return true;
        }

        /// <summary>
        /// 无条件请求关键帧（不做进程内节流）。
        /// 仅供**已经有自己限流**的调用方使用：目前是 HTTP POST /api/idr（300ms 限流）。
        /// </summary>
        internal static void RequestKeyFrameUnthrottled()
        {
            lock (Gate) { _forceKeyFrame = true; }
            LifecycleChanged.Set();
        }

        // ── ★ P0（2026-11-24）：输入触发出帧（方案 B）────────────────────────────
        // 输入（move/click/keydown…）是「用户预期画面马上变化」的最强先验：注入后桌面
        // 几乎必然产生光标/界面变化。让 worker 绕过 WaitUntilNextSlot 节拍立即抓帧编码，
        // 把「输入→画面反馈」从「≤下一拍(1/fps)+等帧超时」压到「立即」。
        // 限流：距上次触发 >50ms 才置位（move 高频事件不成风暴）；开关：SOULLINK_INPUT_TRIGGER=0 关闭。
        private static long _lastInputTriggerMs;      // Environment.TickCount64：上次输入触发时刻
        private static long _inputTriggerCount;       // 可观测：累计触发次数
        private static bool _inputTriggerArmed = ReadInputTriggerEnabled();
        private static bool ReadInputTriggerEnabled()
        {
            // 默认开启；显式 =0 关闭（现场排障/回退用）
            string? raw = Environment.GetEnvironmentVariable("SOULLINK_INPUT_TRIGGER");
            return !(raw == "0");
        }
        private static volatile bool _inputActivityPending;   // worker 侧消费的一次性标志

        /// <summary>输入通道成功注入后调用：立即安排一次抓帧编码（限流 50ms，绕过 fps 节拍）。</summary>
        /// <param name="urgent">
        /// true = 跳过 50ms 限流，无条件触发一次抓帧。
        ///
        /// ★ 2026-11-24（t24 P0-3）：**松手 up / click / releaseall 必须传 true**。
        ///   拖动时 pointermove 每 8ms 一条、每条都刷新限流时间戳，
        ///   松手那一帧极可能落进 50ms 窗口被丢掉 —— 远端松开了但**画面不刷新**，
        ///   用户看到的就是「判定我没松开、要等很久才落位」。
        ///   这类动作是用户唯一能直接感知的操作终点，多编一帧的代价远小于「看起来卡住」。
        /// </param>
        /// <summary>
        /// ★ 2026-11-24（T29）：本拍是否由 **urgent** 动作（up/click/releaseall）触发。
        ///
        ///   【为什么必须与 inputTrigger 分开】**
        ///     move 也会调 NotifyInputActivity（50ms 限流 ≈ 20Hz），所以拖动期间
        ///     「每一拍都是 inputTrigger」。如果抓屏层按 inputTrigger 走短超时/跳过零拷贝，
        ///     **拖动会退化成「复用旧帧」** —— 正好破坏「拖动必须流畅」这条唯一做对的地方。
        ///
        ///   【urgent 才是「桌面即将静止」的信号】
        ///     松手/点击是操作终点，之后桌面大概率静下来 → 值得用短超时立刻收一帧；
        ///     而 move 期间桌面一直在变，100ms 内必拿到新帧，**根本不需要特殊处理**。
        /// </summary>
        private static volatile bool _urgentInputPending;

        /// <summary>worker 侧消费：本拍是否来自 urgent 动作（消费后立即清）。</summary>
        private static bool ConsumeUrgentInput()
        {
            if (!_urgentInputPending) return false;
            _urgentInputPending = false;
            return true;
        }

        public static void NotifyInputActivity(bool urgent = false)
        {
            if (!_inputTriggerArmed) return;
            long now = Environment.TickCount64;
            // 无锁快路径读 + Interlocked 竞态无害（多线程同时通过限流只多触发一拍，worker 有 fps 节流兜底）
            // urgent：跳过限流（仍刷新时间戳，避免紧接着的 move 再触发一次）
            if (!urgent && now - Interlocked.Read(ref _lastInputTriggerMs) < 50) return;
            Interlocked.Exchange(ref _lastInputTriggerMs, now);
            Interlocked.Increment(ref _inputTriggerCount);
            // ★ urgent 单独记一个标志：只有它才允许抓屏层走「短超时/跳过零拷贝」
            if (urgent) _urgentInputPending = true;
            _inputActivityPending = true;
            LifecycleChanged.Set();   // 唤醒 worker（可能在 WaitUntilNextSlot/IdleWait 里）
        }

        private static bool ConsumeKeyFrameRequest()
        {
            // ★ A2：不再无条件 LifecycleChanged.Set()——原实现每轮提前置位 AutoResetEvent，
            //   使 Loop 末尾 SleepUntil 的 WaitOne 立即返回，定时节拍形同虚设、编码速率不受
            //   fps 上限约束。唤醒已由 TrySubscribe/RecomputeLifecycle→Set 覆盖。
            lock (Gate)
            {
                bool value = _forceKeyFrame;
                _forceKeyFrame = false;
                return value;
            }
        }

        /// <summary>
        /// ★ P0-F（2026-11-24）：累积式帧节拍器——保证**平均**稳定跑到目标 fps。
        ///
        /// ★ 为什么不能用「now + 1/fps」（旧写法，真机实测 30fps 配置只跑出 15fps）：
        ///   旧写法在「本拍工作全部做完之后」才设置下一次时刻，
        ///   所以真实周期 = 工作耗时 + 1/fps。
        ///   1920x1080 实测：captureMs≈16 + convertMs≈5 + encodeMs≈3 = 24ms 工作，
        ///   加 33.3ms 等待 = 57ms/帧 → 17.5fps（实测 15fps，因为还有零碎开销）。
        ///   配置写 30fps，用户看到的是 15fps，与 Sunshine 的 120fps 差距巨大。
        ///
        /// ★ 累积式做法：nextAt += 周期，让工作耗时**包含在**周期内。
        ///   工作 24ms < 周期 33.3ms → 每拍只等 9.3ms → 稳定 30fps。
        ///   工作超过周期时（如 4K / 高负载）nextAt 落后于 now：
        ///   此时按「上一拍 + 周期」累加会无限追不上，故做**追帧补偿**：
        ///   若落后超过一个周期，直接把 nextAt 对齐到 now（不补发积压帧，
        ///   实时链路要的是最新画面，不是补播旧帧）。
        /// </summary>
        private static void AdvanceSlot(ref long nextAt, int fps)
        {
            if (fps <= 0) fps = 1;
            long period = (long)(Stopwatch.Frequency / (double)fps);
            long now = Stopwatch.GetTimestamp();
            long next = nextAt + period;
            // 落后超过一个周期（本拍工作超时 / 长时间挂起）→ 重新对齐到 now，
            // 否则会连续多拍疯狂追帧，把 CPU 打满且画面不连贯。
            if (next < now - period) next = now;
            nextAt = next;
        }

        /// <summary>
        /// ★ A2：抓屏前节流——等到「允许抓屏/编码」的绝对时刻 nextAt（fps 上限门槛）。
        /// nextAt 由 AdvanceSlot() 累积推进，本方法只等待、不推进：
        /// 到拍立即 true；未到拍挂起，期间被生命周期事件（stop/热切/参数更新/IDR，
        /// 均经 RecomputeLifecycle→LifecycleChanged.Set）唤醒返回 false，调用方回顶重新判断。
        /// </summary>
        private static bool WaitUntilNextSlot(ref long nextAt)
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= nextAt) return true;
            int wait = (int)Math.Ceiling((nextAt - now) * 1000d / Stopwatch.Frequency);
            if (LifecycleChanged.WaitOne(wait)) return false;   // 被唤醒 → 回顶重新判断
            return true;   // 到拍
        }

        /// <summary>
        /// ★ A2：空闲兜底——无新帧（抓屏 null）或静止跳过时挂起，可被生命周期事件
        ///   （stop/热切/参数更新/IDR 请求）立即唤醒；短超时兜底后回循环顶重试抓屏，
        ///   绝不退化成 while(true) 空转（BitBlt/WGC 抓屏不阻塞，必须靠这里兜底）。
        /// </summary>
        private static void IdleWait()
        {
            LifecycleChanged.WaitOne(5);
        }

        /// <summary>取一个已编码帧；没有新帧时返回 null，不做抓屏或编码。</summary>
        public static byte[]? ReadViewerFrame(int viewerId)
        {
            Viewer? viewer;
            lock (Gate) { if (!Viewers.TryGetValue(viewerId, out viewer)) return null; }
            byte[]? nal;
            int ageMs;
            lock (viewer.Sync)
            {
                if (viewer.Closed || viewer.Pending.Count == 0) return null;
                var (n, at) = viewer.Pending.Dequeue();
                ageMs = (int)(DateTime.Now - at).TotalMilliseconds;   // ★ P0 队列观测：帧龄 = 服务端队列等待
                nal = n;
            }
            // 锁序：Gate 在 viewer.Sync 之外取（入队侧是 Gate→viewer.Sync，避免 ABBA 死锁）
            lock (Gate)
            {
                _queueAgeMs = ageMs;
                if (ageMs > _queueAgeMaxMs) _queueAgeMaxMs = ageMs;
            }
            return nal;
        }

        /// <summary>
        /// ★ P0（事件驱动等帧，2026-11-24）：阻塞等待该观众队列有新帧入队（或超时兜底返回）。
        /// 替代 WriteStreamAsync 主循环的 Task.Delay(10) 轮询：真阻塞（内核等待，零 CPU 空转），
        /// 帧入队即醒，消除每帧平均 +5ms（最坏 +10ms）的轮询延迟税。
        /// 信号只是「可能有帧」提示，队列仍是唯一真源——调用方醒来后必须再 ReadViewerFrame 取帧。
        /// timeoutMs 兜底用于 worker 异常停止/观众移除等边界（正常有观众时 500ms 保活帧必醒，不命中超时）。
        /// </summary>
        public static Task<bool> WaitForFrameAsync(int viewerId, int timeoutMs)
        {
            lock (Gate)
                return Viewers.TryGetValue(viewerId, out var viewer) ? viewer.FrameReady.WaitAsync(timeoutMs) : Task.FromResult(false);
        }

        public static int FrameCount { get { lock (Gate) return Ring.Count; } }
        public static string? LastError { get { lock (Gate) return _lastError; } }
        public static string StreamReadiness
        {
            get { lock (Gate) return "状态=" + _status + " 帧=" + Ring.Count + " 采集=" + Capture.Name
                    + (_captureFallbackReason.Length > 0 ? " 降级原因=" + _captureFallbackReason : "")
                    + " 错误=" + _lastError; }
        }

        /// <summary>一次性 JPEG 截图，仅供静态 shot/frame 协议使用；绝不进入实时 Ring。</summary>
        public static byte[]? CaptureJpegOnce(int quality)
            => CaptureStill(quality, out _, out _);

        private static byte[]? CaptureStill(int quality, out int width, out int height)
        {
            using var capture = CreateCapture();
            int clamped = Math.Clamp(quality, 30, 95);
            var jpeg = capture.CaptureJpeg(clamped, out width, out height);
            if (jpeg != null || capture is BitBltCapture) return jpeg;
            using var fallback = new BitBltCapture();
            return fallback.CaptureJpeg(clamped, out width, out height);
        }

        public static string? SaveRecording(string userTag, string trigger,
            double beforeSeconds, double afterSeconds, int sessionId,
            out int frames, out string message)
        {
            frames = 0; message = "";
            try
            {
                lock (Gate)
                {
                    if (!_recordRequested)
                    {
                        message = "录像开关已关闭；直播仍可继续，本次不写入 MP4";
                        return null;
                    }
                }
                if (afterSeconds > 0.5) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(afterSeconds, 60)));
                List<VideoFrame> copy;
                int width, height, fps;
                lock (Gate) { copy = new List<VideoFrame>(Ring); width = _width; height = _height; fps = _fps; }
                DateTime cutoff = DateTime.Now.AddSeconds(-Math.Max(0, beforeSeconds));
                int first = 0;
                for (int i = 0; i < copy.Count; i++) if (copy[i].At >= cutoff) { first = i; break; }
                for (int i = first; i >= 0; i--) if (copy[i].KeyFrame) { first = i; break; }
                var selected = copy.GetRange(first, copy.Count - first);
                if (selected.Count == 0 || !selected[0].KeyFrame) { message = "缓冲中没有可封装的关键帧"; return null; }

                string folder = DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + (Sanitize(trigger).Length > 0 ? "-" + Sanitize(trigger) : "");
                string dir = Path.Combine(CaptureRoot, "录制回放", string.IsNullOrWhiteSpace(userTag) ? "会话" + sessionId : Sanitize(userTag), folder);
                Directory.CreateDirectory(dir);
                string rawPath = Path.Combine(dir, "replay.h264");
                using (var output = File.Create(rawPath)) foreach (var frame in selected) output.Write(frame.Nal);
                frames = selected.Count;
                string mp4 = Path.Combine(dir, "replay.mp4");
                var times = selected.ConvertAll(f => f.At.Ticks);
                if (Mp4Writer.TryWriteH264(selected.ConvertAll(f => f.Nal), width, height, fps, mp4, out string encodeMessage, times))
                {
                    File.Delete(rawPath);
                    message = "已保存 " + frames + " 帧 → replay.mp4（" + new FileInfo(mp4).Length / 1024 + " KB，无 JPEG 重编码）";
                }
                else
                {
                    try { if (File.Exists(mp4)) File.Delete(mp4); } catch { }
                    File.WriteAllText(Path.Combine(dir, "encode-error.txt"), "MP4 封装失败，已保留 H.264 原始流。\n" + encodeMessage);
                    message = "已保留 " + frames + " 帧 H.264；MP4 封装失败：" + encodeMessage;
                }
                WriteMeta(dir, sessionId, frames, trigger, beforeSeconds, afterSeconds);
                return dir;
            }
            catch (Exception ex) { message = "保存失败：" + ex.Message; return null; }
        }

        private static bool IsKeyFrame(byte[] data)
        {
            foreach (var (offset, length) in Nals(data))
                if (length > 0 && (data[offset] & 0x1f) == 5) return true;
            return false;
        }

        private static IEnumerable<(int Offset, int Length)> Nals(byte[] data)
        {
            int start = -1;
            for (int i = 0; i + 3 < data.Length; i++)
            {
                int prefix = data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 ? 3
                    : data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1 ? 4 : 0;
                if (prefix == 0) continue;
                if (start >= 0) yield return (start, i - start);
                i += prefix - 1; start = i + 1;
            }
            if (start >= 0 && start < data.Length) yield return (start, data.Length - start);
        }

        private static byte[] BgraToNv12(byte[] bgra, int w, int h, int stride, byte[]? buffer = null)
        {
            int size = w * h * 3 / 2;
            var nv12 = buffer?.Length == size ? buffer : new byte[size];
            // ★ P1 并行化：跨 GPU 场景（Intel/AMD 抓屏 + NVENC 编码）零拷贝被路由禁用时，
            //   CPU 转换是必经之路；原单线程标量 1080p ≈7.8ms，按行并行后多核分摊（8 核实测 ~1.5ms）。
            //   实现与 BgraToNv12Scalar 逐字节一致（每行公式逐字复制），并行只发生在行级：
            //   Y 平面按行分区；UV 平面按「偶数行对」分区（每对内部写 2 行共享，无跨分区写冲突）。
            //   小图（≤640x480 像素量级）不值得开线程，直接走标量。
            if (w * h >= 640 * 480 && Environment.ProcessorCount > 1)
            {
                try { BgraToNv12Parallel(bgra, w, h, stride, nv12); return nv12; }
                catch { /* 线程池异常 → 静默降级标量 */ }
            }
            BgraToNv12Scalar(bgra, w, h, stride, nv12);
            return nv12;
        }

        /// <summary>★ P1 行级并行版：与 BgraToNv12Scalar 逐字节等价（行内公式完全相同）。</summary>
        private static void BgraToNv12Parallel(byte[] bgra, int w, int h, int stride, byte[] nv12)
        {
            int ySize = w * h;

            // Y 平面：每行独立（dst 区间 [y*w, y*w+w) 互不重叠）
            var yOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) };
            Parallel.For(0, h, yOptions, y =>
            {
                int src = y * stride, dst = y * w;
                for (int x = 0; x < w; x++, src += 4, dst++)
                {
                    int b = bgra[src], g = bgra[src + 1], r = bgra[src + 2];
                    nv12[dst] = (byte)((66 * r + 129 * g + 25 * b + 128) >> 8);
                }
            });

            // UV 平面：每对行（y, y+1）独立，写 nv12[uvRow, uvRow + w) 互不重叠
            int uvRows = h / 2;
            Parallel.For(0, uvRows, yOptions, yr =>
            {
                int y = yr * 2;
                int top = y * stride, bottom = top + stride, uv = ySize + yr * w;
                for (int x = 0; x < w; x += 2, top += 8, bottom += 8, uv += 2)
                {
                    int b = bgra[top] + bgra[top + 4] + bgra[bottom] + bgra[bottom + 4];
                    int g = bgra[top + 1] + bgra[top + 5] + bgra[bottom + 1] + bgra[bottom + 5];
                    int r = bgra[top + 2] + bgra[top + 6] + bgra[bottom + 2] + bgra[bottom + 6];
                    r >>= 2; g >>= 2; b >>= 2;
                    nv12[uv] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    nv12[uv + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            });
        }

        /// <summary>原标量实现（保留作为 SIMD 降级路径与等价性基准）。</summary>
        private static void BgraToNv12Scalar(byte[] bgra, int w, int h, int stride, byte[] nv12)
        {
            int ySize = w * h;
            // 保持单线程：多会话同时运行时，不为每帧创建 Parallel 任务/争用线程池。
            // 每行复用递增索引，减少像素坐标换算和重复数组访问。
            for (int y = 0; y < h; y++)
            {
                int src = y * stride, dst = y * w;
                for (int x = 0; x < w; x++, src += 4, dst++)
                {
                    int b = bgra[src], g = bgra[src + 1], r = bgra[src + 2];
                    nv12[dst] = (byte)((66 * r + 129 * g + 25 * b + 128) >> 8);
                }
            }
            for (int y = 0; y < h; y += 2)
            {
                int top = y * stride, bottom = top + stride, uv = ySize + y / 2 * w;
                for (int x = 0; x < w; x += 2, top += 8, bottom += 8, uv += 2)
                {
                    int b = bgra[top] + bgra[top + 4] + bgra[bottom] + bgra[bottom + 4];
                    int g = bgra[top + 1] + bgra[top + 5] + bgra[bottom + 1] + bgra[bottom + 5];
                    int r = bgra[top + 2] + bgra[top + 6] + bgra[bottom + 2] + bgra[bottom + 6];
                    r >>= 2; g >>= 2; b >>= 2;
                    nv12[uv] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    nv12[uv + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }
        }

        private static void WriteMeta(string dir, int sessionId, int frames, string trigger, double before, double after)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "meta.json"), "{\"sessionId\":" + sessionId + ",\"frames\":" + frames
                    + ",\"fps\":" + _fps + ",\"size\":\"" + _width + "x" + _height + "\",\"codec\":\"H264\""
                    + ",\"trigger\":\"" + JsonEscape(trigger) + "\",\"beforeSeconds\":" + before.ToString("0.#")
                    + ",\"afterSeconds\":" + after.ToString("0.#") + ",\"savedAt\":\"" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "\"}");
            }
            catch { }
        }

        private static string JsonEscape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string SanitizePipe(string s) => (s ?? "").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s.Trim()) sb.Append(c < 32 || "\\/:*?\"<>|".IndexOf(c) >= 0 ? '_' : c);
            string r = sb.ToString().Trim();
            return r.Length > 60 ? r.Substring(0, 60) : r;
        }

        /// <summary>一次性静态截图；这是 JPEG 在整个类的唯一合法用途。</summary>
        public static bool CaptureOnce(string userTag, string trigger, int quality, int sessionId, out string path, out string message)
        {
            path = ""; message = "";
            try
            {
                var jpeg = CaptureStill(quality, out int width, out int height);
                if (jpeg == null) { message = "抓屏失败"; return false; }
                string folder = DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + (Sanitize(trigger).Length > 0 ? "-" + Sanitize(trigger) : "");
                string dir = Path.Combine(CaptureRoot, "录制回放", string.IsNullOrWhiteSpace(userTag) ? "会话" + sessionId : Sanitize(userTag), folder);
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "shot.jpg");
                File.WriteAllBytes(path, jpeg);
                File.WriteAllText(Path.Combine(dir, "meta.json"), "{\"sessionId\":" + sessionId + ",\"kind\":\"screenshot\",\"size\":\"" + width + "x" + height + "\"}");
                return true;
            }
            catch (Exception ex) { message = ex.Message; return false; }
        }
    }
}
