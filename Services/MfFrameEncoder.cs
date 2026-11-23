using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.MediaFoundation;
using SharpGen.Runtime;

namespace SoulLink.Services
{
    /// <summary>
    /// 逐帧 H.264 编码器（Annex-B NAL 流），供 /stream 直播用。
    ///
    /// ★ 与 MfH264Encoder（SinkWriter → MP4 文件，给录像保存用）**分工不同**：
    ///   本类用 IMFTransform 直接逐帧出 NAL，不写容器，能低延迟推流。
    ///
    /// ★ 为什么用 Vortice.MediaFoundation 而不是手写 IMFTransform vtable：
    ///   Mp4Writer 手写 MF COM 接口翻车三轮（槽位错位 → AccessViolation），
    ///   Vortice 提供完整 IMFTransform/IMFSample/IMFMediaType 绑定，实测逐帧 NAL 跑通。
    ///
    /// ★ 第一版：软件 H.264 MFT（同步）。硬件 MFT（NVENC）是异步的
    ///   （MF_E_TRANSFORM_ASYNC_LOCKED），需事件驱动，属后续优化。
    ///
    /// ★ 输入：NV12（由调用方从 BGRA 转）。输出：每次 Encode 产出若干 NAL 字节
    ///   （含 SPS/PPS 在首帧）。NAL 是长度前缀（MF 默认），调用方按需转 Annex-B。
    /// </summary>
    public sealed class MfFrameEncoder : IDisposable, IFrameH264Encoder
    {
        private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

        private static bool _mfStarted;
        private static readonly object MfGate = new();

        private IMFTransform? _transform;
        private int _width, _height, _fps;
        private long _frameDuration;
        private long _frameIndex;
        private int _outBufferSize;
        private IMFSample? _outputSample;
        private Vortice.MediaFoundation.IMFMediaBuffer? _outputBuffer;
        private bool _initialized, _disposed;
        private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
        private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B4);
        private const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);

        private bool _hardware;
        private bool _hardwareInputAccepted;
        private long _hardwareNoOutput;
        private IMFMediaEventGenerator? _events;
        private MftEventCallback? _eventCallback;
        private readonly object _eventGate = new();
        private bool _eventPending, _needInput, _haveOutput;
        private int _eventError;
        private readonly AutoResetEvent _eventReady = new(false);
        private long _hardwareStartedAt;

        private sealed class MftEventCallback : CallbackBase, IMFAsyncCallback
        {
            private readonly MfFrameEncoder _owner;
            public MftEventCallback(MfFrameEncoder owner) => _owner = owner;
            public Result GetParameters(out AsyncCallbackFlags flags, out int queue) { flags = 0; queue = 0; return Result.Ok; }
            public Result Invoke(IMFAsyncResult asyncResult)
            {
                try
                {
                    using var ev = _owner._events?.EndGetEvent(asyncResult);
                    lock (_owner._eventGate)
                    {
                        _owner._eventPending = false;
                        if (ev != null)
                        {
                            if (ev.EventType == MediaEventTypes.TransformNeedInput) _owner._needInput = true;
                            else if (ev.EventType == MediaEventTypes.TransformHaveOutput) _owner._haveOutput = true;
                        }
                    }
                    return Result.Ok;
                }
                catch (Exception ex)
                {
                    lock (_owner._eventGate) { _owner._eventPending = false; _owner._eventError = Marshal.GetHRForException(ex); }
                    return Result.Fail;
                }
                finally { try { _owner._eventReady.Set(); } catch (ObjectDisposedException) { } }
            }
        }

        public string Name => _hardware ? "MF-Frame-H264-HW" : "MF-Frame-H264-SW";
        public int ConfiguredBitrateKbps { get; private set; }
        public int NegotiatedBitrateKbps { get; private set; }
        public string HardwareStatus => _hardware ? "input=" + _hardwareInputAccepted + " noOutput=" + _hardwareNoOutput : "software";

        /// <summary>最近一次初始化/编码失败原因；供 HTTP 500 和 Agent 日志诊断。</summary>
        public string LastError { get; private set; } = "";

        private static void EnsureStarted()
        {
            lock (MfGate)
            {
                if (_mfStarted) return;
                MediaFactory.MFStartup(false);
                _mfStarted = true;
            }
        }

        private static ulong Pack(uint hi, uint lo) => ((ulong)hi << 32) | lo;

        public bool TryInitialize(int width, int height, int fps, int bitrateKbps)
            => TryInitializeCore(width, height, fps, bitrateKbps, allowHardware: true);

        private bool TryInitializeCore(int width, int height, int fps, int bitrateKbps, bool allowHardware)
        {
            if (_initialized) return true;
            LastError = "";
            if (width <= 0 || height <= 0) { LastError = "帧尺寸无效"; return false; }
            if ((width & 1) != 0) width--;
            if ((height & 1) != 0) height--;
            if (width <= 0 || height <= 0) return false;
            bool attemptedHardware = false;

            try
            {
                EnsureStarted();
                _width = width; _height = height; _fps = Math.Max(1, fps);
                ConfiguredBitrateKbps = Math.Max(500, bitrateKbps);
                _frameDuration = 10_000_000L / _fps;

                var inInfo = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_NV12 };
                var outInfo = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };

                using var hwColl = MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, 4, inInfo, outInfo);
                using var swColl = MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, 3, inInfo, outInfo);
                if (!allowHardware) hwColl.Dispose();

                // 硬编优先；硬件不可用时自动落到软件 MFT，不依赖额外的 NVENC SDK。
                if (allowHardware) foreach (var act in hwColl)
                {
                    try
                    {
                        _transform = act.ActivateObject<IMFTransform>();
                        _transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true);
                        _hardware = true;
                        break;
                    }
                    catch { _transform?.Dispose(); _transform = null; _hardware = false; }
                }
                if (_transform == null)
                    foreach (var act in swColl) { _transform = act.ActivateObject<IMFTransform>(); _hardware = false; break; }
                if (_transform == null) { LastError = "系统没有可用的 H.264 MFT"; return false; }

                using var outMediaType = MediaFactory.MFCreateMediaType();
                outMediaType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                outMediaType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
                outMediaType.Set(MF_MT_AVG_BITRATE, (uint)(Math.Max(500, bitrateKbps) * 1000));
                outMediaType.Set(MF_MT_INTERLACE_MODE, 2u);
                outMediaType.Set(MF_MT_FRAME_SIZE, Pack((uint)width, (uint)height));
                outMediaType.Set(MF_MT_FRAME_RATE, Pack((uint)_fps, 1));
                outMediaType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                // 无厂商参数的通用 MFT 路径：短 GOP 保证新观众及时重同步。
                outMediaType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, 60);
                _transform.SetOutputType(0, outMediaType, 0);
                try
                {
                    using var currentType = _transform.GetOutputCurrentType(0);
                    NegotiatedBitrateKbps = (int)(currentType.GetUInt32(MF_MT_AVG_BITRATE) / 1000);
                }
                catch (Exception ex) { LastError = "码率读回失败：" + ex.Message; }

                using var inMediaType = MediaFactory.MFCreateMediaType();
                inMediaType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                inMediaType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
                inMediaType.Set(MF_MT_INTERLACE_MODE, 2u);
                inMediaType.Set(MF_MT_FRAME_SIZE, Pack((uint)width, (uint)height));
                inMediaType.Set(MF_MT_FRAME_RATE, Pack((uint)_fps, 1));
                inMediaType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                _transform.SetInputType(0, inMediaType, 0);
                if (_hardware)
                {
                    _events = _transform.QueryInterface<IMFMediaEventGenerator>();
                    _eventCallback = new MftEventCallback(this);
                    _hardwareStartedAt = Environment.TickCount64;
                    BeginGetEvent();
                }
                _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

                var outInfo2 = _transform.GetOutputStreamInfo(0);
                // MFT 可能报告一个过小的建议值（本机 1920x1080 软编曾因此每帧 MF_E_BUFFERTOOSMALL）。
                // 给关键帧留足够空间，不要用 NV12 输入大小作为 H.264 输出的上限。
                _outBufferSize = Math.Max(outInfo2.Size, width * height * 2);
                _outputSample = MediaFactory.MFCreateSample();
                _outputBuffer = MediaFactory.MFCreateMemoryBuffer(_outBufferSize);

                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + "：" + ex.Message;
                AsyncLog.Append(Path.Combine(Path.GetTempPath(), "soullink-mf-encoder.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  TryInitialize 失败: " + ex + Environment.NewLine);
                ReleaseTransform();
                if (attemptedHardware && allowHardware)
                {
                    _hardware = false;
                    if (TryInitializeCore(width, height, fps, bitrateKbps, allowHardware: false))
                    {
                        LastError = "硬编初始化失败，已降级软编：" + ex.Message;
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// 编码一帧 NV12。产出的是**长度前缀 NAL**（MF 默认），
        /// 调用方通过 <see cref="ToAnnexB"/> 转成 Annex-B 起始码格式。
        /// 返回 null 表示本帧无输出（流水线延迟，正常）。
        /// </summary>
        public byte[]? EncodeNv12(byte[] nv12, long timestampUs)
        {
            if (!_initialized || _disposed || _transform == null) return null;
            bool softwareOnly = !_hardware;
            if (nv12 == null || nv12.Length == 0) return null;

            try
            {
                // Vortice 的 IMFTransform/IMFSample/IMFMediaBuffer 都是 SharpGen ComObject，
                // 实现 IDisposable，Dispose 会 Release 自己持有的 COM 引用。
                // AddBuffer 只让 sample 增加自己的 buf 引用，不转移调用者的引用，故二者都释放。
                using var sample = MediaFactory.MFCreateSample();
                using var buf = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
                bool locked = false;
                try
                {
                    buf.Lock(out IntPtr pData, out _, out _);
                    locked = true;
                    Marshal.Copy(nv12, 0, pData, nv12.Length);
                }
                finally { if (locked) buf.Unlock(); }
                buf.CurrentLength = nv12.Length;
                sample.AddBuffer(buf);
                sample.SampleTime = _frameIndex * _frameDuration;
                sample.SampleDuration = _frameDuration;
                _frameIndex++;

                if (_hardware)
                {
                    byte[]? hardwareNal = EncodeHardware(sample);
                    if (hardwareNal != null) { _hardwareNoOutput = 0; return hardwareNal; }
                    _hardwareNoOutput++;
                    if (Environment.TickCount64 - _hardwareStartedAt >= 3000 || _hardwareNoOutput >= 30)
                    {
                        if (FallbackToSoftware("硬编首帧超时或连续30帧无输出"))
                            return EncodeSoftware(nv12);
                        throw new InvalidOperationException("硬编失败且软编重建失败：" + LastError);
                    }
                    return null;
                }
                return EncodeSoftware(nv12);
            }
            catch (Exception ex) when (!softwareOnly)
            {
                if (FallbackToSoftware(ex)) return EncodeSoftware(nv12);
                throw new InvalidOperationException("硬编失败且软编重建失败：" + LastError, ex);
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + "：" + ex.Message;
                AsyncLog.Append(Path.Combine(Path.GetTempPath(), "soullink-mf-encoder.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  Encode 失败: " + ex + Environment.NewLine);
                throw;
            }
        }

        private byte[]? EncodeSoftware(byte[] nv12)
        {
            _frameIndex--;
            using var sample = MediaFactory.MFCreateSample();
            using var buf = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
            bool locked = false;
            try
            {
                buf.Lock(out IntPtr pData, out _, out _);
                locked = true;
                Marshal.Copy(nv12, 0, pData, nv12.Length);
            }
            finally { if (locked) buf.Unlock(); }
            buf.CurrentLength = nv12.Length;
            sample.AddBuffer(buf);
            sample.SampleTime = _frameIndex * _frameDuration;
            sample.SampleDuration = _frameDuration;
            _frameIndex++;
            _transform!.ProcessInput(0, sample, 0);
            if (_outputSample == null || _outputBuffer == null) throw new InvalidOperationException("输出缓冲未初始化");
                // 同一编码器单线程使用：每帧复用缓冲，不把 2*w*h 的 MF 缓冲无限留在旧样本上。
                // NAL 已复制成独立 byte[] 返回；下一次写入前才清空/解挂旧缓冲。
                _outputSample.RemoveAllBuffers();
                _outputBuffer.CurrentLength = 0;
                _outputSample.AddBuffer(_outputBuffer);
                var outBuf = new OutputDataBuffer { StreamID = 0, Sample = _outputSample };
                try
                {
                    var status = new ProcessOutputStatus();
                    byte[]? result = null;
                    while (true)
                    {
                        var r = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out status);
                        if (!r.Success) break;
                        IMFSample? got = outBuf.Sample;
                        outBuf.Sample = null;
                        if (got == null) break;
                        try { result = ExtractNals(got); }
                        finally
                        {
                            // MFT 可能替换 Sample：必须释放实际返回的对象，包括其缓冲引用。
                            got.RemoveAllBuffers();
                            if (!ReferenceEquals(got, _outputSample))
                            {
                                // 若 MFT 换了 sample，旧字段不再代表本帧返回对象；替换前释放旧引用。
                                _outputSample.Dispose();
                                _outputSample = got;
                            }
                        }
                    }
                    return result;
                }
                finally { outBuf.Sample = null; }
        }

        private byte[]? EncodeHardware(IMFSample input)
        {
            // 一个视频线程串行调用：只在 NeedInput 事件到达后喂入，在 HaveOutput 后取帧。
            // BeginGetEvent 始终只挂一个，回调里绝不重入 ProcessInput/ProcessOutput。
            WaitForHardwareEvent(needInput: false, haveOutput: true, timeoutMs: 8);
            byte[]? result = null;
            if (_haveOutput) result = EncodeHardwareOutput();
            WaitForHardwareEvent(needInput: true, haveOutput: false, timeoutMs: 8);
            if (_needInput)
            {
                try
                {
                    _transform!.ProcessInput(0, input, 0);
                    _hardwareInputAccepted = true;
                    lock (_eventGate) _needInput = false;
                }
                catch (Exception ex) when (Marshal.GetHRForException(ex) == MF_E_NOTACCEPTING)
                {
                    _frameIndex--;
                    _hardwareInputAccepted = false;
                }
            }
            else _frameIndex--;
            return result;
        }

        private void ReleaseTransform()
        {
            try { _transform?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
            try { _transform?.Dispose(); } catch { }
            try { _events?.Dispose(); } catch { }
            try { _eventCallback?.Dispose(); } catch { }
            try { _outputSample?.RemoveAllBuffers(); } catch { }
            try { _outputSample?.Dispose(); } catch { }
            try { _outputBuffer?.Dispose(); } catch { }
            _transform = null; _events = null; _eventCallback = null;
            _outputSample = null; _outputBuffer = null;
            lock (_eventGate)
            {
                _eventPending = _needInput = _haveOutput = false;
                _eventError = 0;
            }
            while (_eventReady.WaitOne(0)) { }
            _initialized = false;
        }

        private bool FallbackToSoftware(Exception failure)
        {
            if (!_hardware) return false;
            LastError = "硬编已降级软编：" + failure.GetType().Name + " " + failure.Message;
            AsyncLog.Append(Path.Combine(Path.GetTempPath(), "soullink-mf-encoder.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + "  硬编运行时降级: " + failure + Environment.NewLine);
            ReleaseTransform();
            _hardware = false;
            _hardwareNoOutput = 0;
            _frameIndex = 0;
            return TryInitializeCore(_width, _height, _fps, ConfiguredBitrateKbps, allowHardware: false);
        }

        private bool FallbackToSoftware(string reason) => FallbackToSoftware(new InvalidOperationException(reason));

        private void BeginGetEvent()
        {
            if (_events == null || _eventCallback == null) throw new InvalidOperationException("硬件编码器不支持异步事件");
            lock (_eventGate)
            {
                if (_disposed || _eventPending) return;
                _eventPending = true;
            }
            try { _events.BeginGetEvent(_eventCallback, null!); }
            catch
            {
                lock (_eventGate) _eventPending = false;
                throw;
            }
        }

        private bool WaitForHardwareEvent(bool needInput, bool haveOutput, int timeoutMs)
        {
            BeginGetEvent();
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                lock (_eventGate)
                {
                    if (_disposed) return false;
                    if (_eventError != 0) Marshal.ThrowExceptionForHR(_eventError);
                    if ((needInput && _needInput) || (haveOutput && _haveOutput)) return true;

                }
                int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
                if (remaining <= 0) return false;
                _eventReady.WaitOne(Math.Min(remaining, 50));
            }
        }

        private byte[]? EncodeHardwareOutput()
        {
            if (_outputSample == null || _outputBuffer == null || _transform == null) throw new InvalidOperationException("输出缓冲未初始化");
            byte[]? result = null;
            for (int guard = 0; guard < 64 && _haveOutput; guard++)
            {
                lock (_eventGate) _haveOutput = false;
                _outputSample.RemoveAllBuffers();
                _outputBuffer.CurrentLength = 0;
                _outputSample.AddBuffer(_outputBuffer);
                var outBuf = new OutputDataBuffer { StreamID = 0, Sample = _outputSample };
                try
                {
                    var status = new ProcessOutputStatus();
                    var hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out status);
                    if (!hr.Success)
                    {
                        if (hr.Code == E_UNEXPECTED || hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT || hr.Code == MF_E_NO_MORE_TYPES) break;
                        Marshal.ThrowExceptionForHR(hr.Code);
                    }
                    var got = outBuf.Sample;
                    outBuf.Sample = null;
                    if (got == null) break;
                    try { result = ExtractNals(got); }
                    finally
                    {
                        got.RemoveAllBuffers();
                        if (!ReferenceEquals(got, _outputSample))
                        {
                            _outputSample.Dispose();
                            _outputSample = got;
                        }
                    }
                }
                finally { outBuf.Sample = null; }
            }
            return result;
        }

        private static byte[]? ExtractNals(IMFSample sample)
        {
            using var buf = sample.ConvertToContiguousBuffer();
            if (buf == null) return null;
            int len = buf.CurrentLength;
            if (len <= 0) return null;
            var bytes = new byte[len];
            bool locked = false;
            try
            {
                buf.Lock(out IntPtr pData, out _, out _);
                locked = true;
                Marshal.Copy(pData, bytes, 0, len);
            }
            finally { if (locked) buf.Unlock(); }
            return bytes;
        }

        /// <summary>
        /// 把 MF 的**长度前缀 NAL** 转成 Annex-B（00 00 00 01 起始码）。
        ///
        /// ★ 注意：本机软件 H.264 MFT 实测**直接输出 Annex-B**（首字节 00 00 00 01），
        ///   所以 /stream 生产路径**不调用此方法**，直接写原始 NAL。
        ///   此方法保留给「某些 MFT 输出长度前缀」的兼容场景（需按实际输出格式判断）。
        /// </summary>
        public static byte[] ToAnnexB(byte[] lengthPrefixed)
        {
            using var ms = new MemoryStream(lengthPrefixed.Length + 32);
            int i = 0;
            var startCode = new byte[] { 0, 0, 0, 1 };
            while (i + 4 <= lengthPrefixed.Length)
            {
                int len = (lengthPrefixed[i] << 24) | (lengthPrefixed[i + 1] << 16) | (lengthPrefixed[i + 2] << 8) | lengthPrefixed[i + 3];
                i += 4;
                if (len <= 0 || i + len > lengthPrefixed.Length) break;
                ms.Write(startCode, 0, 4);
                ms.Write(lengthPrefixed, i, len);
                i += len;
            }
            return ms.ToArray();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _transform?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            }
            catch { }
            _eventReady.Set();
            try { _transform?.Dispose(); } catch { }
            try { _events?.Dispose(); } catch { }
            _events = null; _eventCallback = null;
            try { _outputSample?.RemoveAllBuffers(); } catch { }
            try { _outputSample?.Dispose(); } catch { }
            try { _outputBuffer?.Dispose(); } catch { }
            _outputSample = null; _outputBuffer = null;
            _eventReady.Dispose();
            _transform = null;
        }
    }
}
