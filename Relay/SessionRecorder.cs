using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
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
            public readonly Queue<byte[]> Pending = new();
            public bool Closed;
        }

        private static IScreenCapture Capture = CreateCapture();
        private static readonly object Gate = new();
        private static readonly List<VideoFrame> Ring = new();
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
        private static long _captureMs, _convertMs, _encodeMs;
        private static int _configuredBitrate, _negotiatedBitrate;
        private static readonly AutoResetEvent LifecycleChanged = new(false);
        private static bool _forceKeyFrame;
        private static Stopwatch? _probeClock;
        private static bool _captureProbeComplete;
        private static DateTime _lastCaptureAt, _lastEncodeAt, _lastNalAt;
        private static long _loopSequence, _skippedStaticFrames, _noNalFrames;
        private static int _captureMode = int.TryParse(Environment.GetEnvironmentVariable("SOULLINK_CAPTURE_MODE"), out int initialMode) ? Math.Clamp(initialMode, 0, 3) : 0;
        private static int _captureModeVersion;
        private static volatile bool _captureSwitchRequested;
        private static int _videoParameterVersion;
        private static VideoParameters _videoParameters = VideoParameters.ReadEnvironment();

        private sealed record VideoParameters(int Bitrate, int Preset, int Vbv, int Stream, int Record)
        {
            public static VideoParameters ReadEnvironment() => new(
                ReadInt("SOULLINK_VIDEO_BITRATE", 500, 50000, 8000),
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
                Math.Clamp(bitrateKbps, 500, 50000), Math.Clamp(preset, 1, 7),
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
        /// 降级链：DXGI DDA → WGC → BitBlt。</summary>
        private static byte[]? CaptureBgraWithProbe(out int width, out int height)
        {
            if (_captureProbeComplete) return Capture.CaptureBgra(out width, out height);
            // ★ DDA 探测：DxgiDdaCapture 构造时即尝试 DuplicateOutput，失败抛异常
            //   → 降级 WGC；WGC 300ms 无首帧 → 降级 BitBlt。
            if (Capture is DxgiDdaCapture dda && _captureMode == 0)
            {
                _probeClock ??= Stopwatch.StartNew();
                byte[]? frame = null;
                string reason = "";
                try { frame = dda.CaptureBgra(out width, out height); }
                catch (Exception ex) { width = height = 0; reason = ex.GetType().Name + "：" + ex.Message; }
                if (frame != null && frame.Length > 0 && dda.IsActive)
                {
                    RelayAgent.LogQuiet("[capture] 采用 DXGI-DDA（出帧 " + width + "x" + height + "）");
                    _captureProbeComplete = true;
                    _probeClock = null;
                    return frame;
                }
                if (reason.Length == 0) reason = dda.LastError.Length > 0 ? dda.LastError : "DDA 未出帧";
                if (_probeClock.ElapsedMilliseconds >= 300)
                {
                    RelayAgent.LogQuiet("[capture] DXGI-DDA 降级：" + reason);
                    dda.Dispose();
#if WGC
                    Capture = new WgcCapture();
#else
                    Capture = new BitBltCapture();
#endif
                    _probeClock = null;
                    return CaptureBgraWithProbe(out width, out height);
                }
                Thread.Sleep(10);
                return null;
            }
#if WGC
            if (Capture is WgcCapture wgc && _captureMode != 2)
            {
                _probeClock ??= Stopwatch.StartNew();
                byte[]? frame = null;
                string reason = "";
                try { frame = wgc.CaptureBgra(out width, out height); }
                catch (Exception ex) { width = height = 0; reason = ex.GetType().Name; }
                if (frame != null && frame.Length > 0 && wgc.IsActive && wgc.FrameSequence > 0)
                {
                    RelayAgent.LogQuiet("[capture] 自动探测采用 WGC（实际出帧 sequence=" + wgc.FrameSequence + "）");
                    _captureProbeComplete = true;
                    _probeClock = null;
                    return frame;
                }
                if (reason.Length == 0) reason = !wgc.IsActive ? "WGC 未进入 running 状态"
                    : wgc.FrameSequence == 0 ? "WGC 300ms 内无真实首帧" : "WGC 帧读回失败";
                if (_probeClock.ElapsedMilliseconds >= 300)
                {
                    RelayAgent.LogQuiet("[capture] 自动探测降级 BitBlt：" + reason);
                    wgc.Dispose(); Capture = new BitBltCapture(); _probeClock = null; _captureProbeComplete = true;
                    byte[]? fallback = Capture.CaptureBgra(out width, out height);
                    if (fallback != null) RelayAgent.LogQuiet("[capture] BitBlt 降级后已抓到首帧");
                    return fallback;
                }
                Thread.Sleep(10);
                return null;
            }
#endif
            return Capture.CaptureBgra(out width, out height);
        }

        /// <summary>
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

            if (mode != 2)
            {
                RelayAgent.LogQuiet("[video] 尝试 NVENC SDK 直通编码器");
                var nvenc = TryInitializeCandidate(new NvencSdkEncoder(), ew, eh, fps, parameters, out string nvFail);
                if (nvenc != null) return nvenc;
                RelayAgent.LogQuiet("[video] NVENC SDK 不可用，降级 MF：" + nvFail);
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
                    + "|bitrateKbps=" + _configuredBitrate + "|mftBitrateKbps=" + _negotiatedBitrate
                    + (_lastError.Length > 0 ? "|error=" + _lastError : "");
            }
        }

        public static bool Start(int fps, int quality, int keepSeconds, int sessionId, out string message)
        {
            lock (Gate) { _recordRequested = true; RecomputeLifecycle(); }
            return StartCore(fps, quality, keepSeconds, sessionId, out message);
        }

        private static bool StartCore(int fps, int quality, int keepSeconds, int sessionId, out string message)
        {
            lock (Gate)
            {
                _fps = Math.Clamp(fps, 1, 30);
                _quality = Math.Clamp(quality, 30, 95);
                _keepSeconds = Math.Clamp(keepSeconds, 5, 600);
                _sessionId = sessionId;
                if (_worker != null && _worker.IsAlive)
                {
                    message = "共享 H.264 管线已在运行，" + _fps + "fps，观众=" + Viewers.Count;
                    return true;
                }
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
                RecomputeLifecycle();
            }
        }

        private static void RecomputeLifecycle()
        {
            // 管线只要还有录像或观众就继续；只有两者都没有才停止。
            _stopRequested = !_recordRequested && Viewers.Count == 0;
            LifecycleChanged.Set();
        }

        public static void Shutdown(int waitMs = 1500)
        {
            _stopRequested = true;
            lock (Gate) { foreach (var v in Viewers.Values) lock (v.Sync) v.Closed = true; }
            try { var t = _worker; if (t != null && t.IsAlive) t.Join(waitMs); } catch { }
            try { Capture.Dispose(); } catch { }
        }

        private static void Loop()
        {
            IFrameH264Encoder? encoder = null;
            int encodedWidth = 0, encodedHeight = 0;
            long frameIndex = 0;
            byte[]? previousBgra = null;
            long nextAt = Stopwatch.GetTimestamp();
            int appliedVersion = -1;
            VideoParameters? applied = null;
            int appliedCaptureVersion = _captureModeVersion;
            try
            {
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
                            Capture = CreateCapture();
                            encoder?.Dispose(); encoder = null;
                            frameIndex = 0; previousBgra = null;
                            appliedCaptureVersion = version;
                        }
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
                        frameIndex = 0; previousBgra = null;
                        // 保留 Pending：旧帧之后由新 IDR 续接，viewer 不重建、不断开。
                    }
                    int fps, keepSec;
                    lock (Gate)
                    {
                        fps = Viewers.Count > 0 ? StreamFps : RecordFps;
                        keepSec = _keepSeconds;
                        _fps = fps;
                    }
                    var frameStart = Stopwatch.StartNew();
                    var bgra = CaptureBgraWithProbe(out int width, out int height);
                    _loopSequence++; _lastCaptureAt = DateTime.UtcNow;
                    long captureMs = frameStart.ElapsedMilliseconds;
                    if (bgra == null || width <= 0 || height <= 0)
                    {
                        lock (Gate) { _status = "waiting"; if (_probeClock == null) _lastError = "抓屏没有返回原始帧"; }
                        SleepUntil(ref nextAt, fps);
                        continue;
                    }
                    bool forceKeyFrame = ConsumeKeyFrameRequest();
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
                    int ew = width & ~1, eh = height & ~1;
                    if (!forceKeyFrame && previousBgra != null && previousBgra.AsSpan().SequenceEqual(bgra))
                    {
                        _skippedStaticFrames++;
                        SleepUntil(ref nextAt, fps);
                        continue;
                    }
                    if (forceKeyFrame)
                    {
                        previousBgra = bgra;
                        RelayAgent.LogQuiet("[video] 新观众按需 IDR：即使画面静止也强制编码，不再被去重跳过");
                    }
                    previousBgra = bgra;

                    if (encoder == null || encodedWidth != ew || encodedHeight != eh)
                    {
                        previousBgra = null;
                        encoder?.Dispose();
                        encoder = CreateEncoder(ew, eh, fps, parameters);
                        applied = parameters; appliedVersion = parameterVersion;
                        encodedWidth = ew; encodedHeight = eh;
                        RelayAgent.LogQuiet("[video] 编码器=" + encoder.Name + " 配置码率=" + encoder.ConfiguredBitrateKbps
                            + "kbps MFT读回=" + encoder.NegotiatedBitrateKbps + "kbps fps=" + fps + " 尺寸=" + ew + "x" + eh);
                        lock (Gate) { _width = ew; _height = eh; _configuredBitrate = encoder.ConfiguredBitrateKbps; _negotiatedBitrate = encoder.NegotiatedBitrateKbps; }
                    }

                    var convertStart = Stopwatch.StartNew();
                    byte[] nv12 = BgraToNv12(bgra, ew, eh, width * 4);
                    long convertMs = convertStart.ElapsedMilliseconds;
                    var encodeStart = Stopwatch.StartNew();
                    byte[]? nal = encoder.EncodeNv12(nv12, frameIndex * 1_000_000L / fps);
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
                            _captureMs = captureMs; _convertMs = convertMs; _encodeMs = encodeMs;
                            _status = "recording"; _lastError = "";
                            if (_totalFrames % 30 == 0)
                                RelayAgent.LogQuiet("[video] 共享管线 total=" + _totalFrames + " viewers=" + Viewers.Count
                                    + " captureMs=" + captureMs + " convertMs=" + convertMs + " encodeMs=" + encodeMs
                                    + " loop=" + _loopSequence + " lastCapture=" + _lastCaptureAt.ToString("HH:mm:ss.fff")
                                    + " lastEncode=" + _lastEncodeAt.ToString("HH:mm:ss.fff") + " lastNal=" + _lastNalAt.ToString("HH:mm:ss.fff")
                                    + " staticSkipped=" + _skippedStaticFrames);
                            DateTime cutoff = frame.At.AddSeconds(-keepSec);
                            while (Ring.Count > 0 && (Ring[0].At < cutoff || Ring.Count > Math.Max(300, fps * keepSec * 2))) Ring.RemoveAt(0);
                            foreach (var viewer in Viewers.Values)
                            {
                                lock (viewer.Sync)
                                {
                                    if (viewer.Closed) continue;
                                    if (frame.KeyFrame) viewer.Pending.Clear();
                                    // 队列满时只丢旧非关键帧；绝不能把下一个 IDR 丢掉导致永久花屏。
                                    while (viewer.Pending.Count >= 4 && !IsKeyFrame(viewer.Pending.Peek())) viewer.Pending.Dequeue();
                                    viewer.Pending.Enqueue(frame.Nal);
                                }
                            }
                        }
                    }
                    if (nal == null || nal.Length == 0) _noNalFrames++;
                    SleepUntil(ref nextAt, fps);
                    if (_loopSequence % 150 == 0)
                        RelayAgent.LogQuiet("[video] 活性 loop=" + _loopSequence + " total=" + _totalFrames
                            + " lastCapture=" + _lastCaptureAt.ToString("HH:mm:ss.fff") + " lastEncode=" + _lastEncodeAt.ToString("HH:mm:ss.fff")
                            + " lastNal=" + _lastNalAt.ToString("HH:mm:ss.fff") + " staticSkipped=" + _skippedStaticFrames + " noNal=" + _noNalFrames);
                }
            }
            catch (Exception ex)
            {
                lock (Gate) { _status = "error"; _lastError = ex.GetType().Name + "：" + ex.Message; }
                RelayAgent.LogQuiet("[video] 共享采集编码失败：" + ex);
            }
            finally
            {
                try { encoder?.Dispose(); } catch { }
                // 静态 Capture 跨订阅复用；仅热切/Shutdown 释放，避免二次订阅拿到已 disposed 后端。
                lock (Gate)
                {
                    Ring.Clear(); _status = "idle"; _worker = null;
                    // 停止与最后一位观众退订可能并发：仅在确实无需求时保留停止态。
                    if (_recordRequested || Viewers.Count > 0) _stopRequested = false;
                }
            }
        }

        /// <summary>订阅共享 NAL。返回失败时调用方必须给出错误，不允许退回 JPEG 轮询。</summary>
        public static bool TrySubscribe(out int viewerId, out byte[]? initial, out string error)
        {
            viewerId = 0; initial = null; error = "";
            lock (Gate)
            {
                if (_status == "error")
                {
                    // 编码/抓屏失败可重试：清错误并允许 StartCore 新建 worker，不永久拒绝后续观众。
                    RelayAgent.LogQuiet("[video] 订阅遇 error，清理状态并尝试重启视频管线：" + _lastError);
                    _lastError = ""; _status = "idle";
                }
                viewerId = ++_nextViewerId;
                Viewers[viewerId] = new Viewer();
                // 新 MFT 的首帧必为 IDR；这是无 ICodecAPI 的跨厂商按需关键帧。
                _forceKeyFrame = true;
                RecomputeLifecycle();
                if (Ring.Count > 0)
                {
                    int start = Ring.Count - 1;
                    for (int i = Ring.Count - 1; i >= 0; i--) if (Ring[i].KeyFrame) { start = i; break; }
                    initial = Ring[start].Nal;
                }
            }
            if (!StartCore(StreamFps, _quality, _keepSeconds, Process.GetCurrentProcess().SessionId, out error))
            {
                Unsubscribe(viewerId); viewerId = 0; return false;
            }
            return true;
        }

        public static void Unsubscribe(int viewerId)
        {
            lock (Gate) { if (Viewers.Remove(viewerId, out var viewer)) lock (viewer.Sync) viewer.Closed = true; }
            // 录制未开启且最后一个观众离开时停止共享管线，释放编码器与 MF 缓冲池。
            // 锁外赋值避免在持锁状态调用线程管理。
            lock (Gate) RecomputeLifecycle();
        }

        private static bool ConsumeKeyFrameRequest()
        {
            LifecycleChanged.Set();
            lock (Gate)
            {
                bool value = _forceKeyFrame;
                _forceKeyFrame = false;
                return value;
            }
        }

        private static void SleepUntil(ref long nextAt, int fps)
        {
            nextAt += (long)(Stopwatch.Frequency / (double)fps);
            int wait = (int)Math.Ceiling((nextAt - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency);
            if (wait > 0) LifecycleChanged.WaitOne(wait);
            else if (wait < -1000 / fps) nextAt = Stopwatch.GetTimestamp();
        }

        /// <summary>取一个已编码帧；没有新帧时返回 null，不做抓屏或编码。</summary>
        public static byte[]? ReadViewerFrame(int viewerId)
        {
            Viewer? viewer;
            lock (Gate) { if (!Viewers.TryGetValue(viewerId, out viewer)) return null; }
            lock (viewer.Sync) return viewer.Pending.Count > 0 ? viewer.Pending.Dequeue() : null;
        }

        public static int FrameCount { get { lock (Gate) return Ring.Count; } }
        public static string? LastError { get { lock (Gate) return _lastError; } }
        public static string StreamReadiness
        {
            get { lock (Gate) return "状态=" + _status + " 帧=" + Ring.Count + " 采集=" + Capture.Name + " 错误=" + _lastError; }
        }

        /// <summary>一次性 JPEG 截图，仅供静态 shot/frame 协议使用；绝不进入实时 Ring。</summary>
        public static byte[]? CaptureJpegOnce(int quality)
            => Capture.CaptureJpeg(Math.Clamp(quality, 30, 95), out _, out _);

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

        private static byte[] BgraToNv12(byte[] bgra, int w, int h, int stride)
        {
            int ySize = w * h;
            var nv12 = new byte[ySize + ySize / 2];
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
            return nv12;
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
                var jpeg = Capture.CaptureJpeg(Math.Clamp(quality, 30, 95), out int width, out int height);
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
