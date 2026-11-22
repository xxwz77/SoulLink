using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SoulLink.Services
{
    /// <summary>
    /// Media Foundation H.264 编码器（第一步：CPU BGRA 输入，产出可播放的 MP4）。
    ///
    /// ★ 为什么复用 Mp4Writer 的 COM 接口而不是新写：
    ///   Mp4Writer 里的 IMFAttributes / IMFMediaBuffer / IMFSinkWriter 槽位顺序
    ///   已经过三轮踩坑 + 实测 ffprobe 校验通过（h264/Main/yuv420p）。
    ///   同一程序集内直接复用这三个 internal 接口，等于继承那份「已经验证是对的」顺序，
    ///   不重新冒手写 vtable 错位的风险。
    ///
    /// ★ 与 Mp4Writer 的区别：
    ///   Mp4Writer 是「JPEG 文件序列 → 离线 MP4」；本类是「逐帧 BGRA → 流式 MP4」，
    ///   供实时画面/直播用。两者共用同一条 SinkWriter 编码通路，参数一致。
    ///
    /// ★ 第一步参数（用户指定）：
    ///   · 码率 8 Mbps（8000 kbps，随调用方传入）
    ///   · Profile = Main（77，与 Mp4Writer 一致，兼容性最好）
    ///   · B 帧：微软 H.264 编码器默认 0（关），天然低延迟
    ///   · GOP / CODECAPI_AVLowLatencyMode：需 ICodecAPI，属第二步（见类尾「第二步」）
    ///
    /// ★ 输入约定：bgra 是 BGRA 布局、**自下而上**（bottom-up），与 Mp4Writer 的
    ///   LoadAsBgraBottomUp 产出一致；stride 通常 = width*4。
    /// </summary>
    public sealed class MfH264Encoder : IVideoEncoder
    {
        // ── Media Foundation P/Invoke（与 Mp4Writer 同源）────────────────────
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMediaType(out IntPtr ppMFType);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateSample(out IntPtr ppIMFSample);
        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int MFCreateSinkWriterFromURL(
            [MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr pByteStream, IntPtr pAttributes,
            out IMFSinkWriter ppSinkWriter);

        private const int MF_VERSION = 0x00020070;
        private const int MF_E_TRANSFORM_TYPE_NOT_SET = unchecked((int)0xC00D6D60);

        // 属性键（与 Mp4Writer 一致的 GUID）
        private static readonly Guid K_MAJOR     = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid K_SUBTYPE   = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid K_BITRATE   = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid K_INTERLACE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid K_SIZE      = new("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid K_RATE      = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid K_PAR       = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid K_INDEP     = new("c9173739-5e56-461c-b713-46fb995cb95f");
        private static readonly Guid K_PROFILE   = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

        private static readonly Guid V_VIDEO = new("73646976-0000-0010-8000-00AA00389B71");
        private static readonly Guid V_H264  = new("34363248-0000-0010-8000-00AA00389B71");
        private static readonly Guid V_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");

        private static readonly Guid IID_IMFAttributes = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");

        private static bool _mfStarted;
        private static readonly object MfGate = new();

        private string? _tmpPath;
        private IMFSinkWriter? _writer;
        private uint _streamIndex;
        private int _width, _height, _fps;
        private long _frameDuration;   // 100ns
        private long _frameCount;
        private bool _initialized, _completed, _disposed;

        public string Name => "MF-H264";

        private static void EnsureMfStarted()
        {
            lock (MfGate)
            {
                if (_mfStarted) return;
                int hr = MFStartup(MF_VERSION, 0);
                if (hr < 0) throw new InvalidOperationException("MFStartup 失败 0x" + hr.ToString("X8"));
                _mfStarted = true;
            }
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new InvalidOperationException(what + " 失败 hr=0x" + hr.ToString("X8"));
        }

        /// <summary>QI 出 IMFAttributes（复用 Mp4Writer 已验证的接口）。</summary>
        private static IMFAttributes AsAttributes(IntPtr mediaType)
        {
            Guid iid = IID_IMFAttributes;
            Check(Marshal.QueryInterface(mediaType, ref iid, out IntPtr p), "QI(IMFAttributes)");
            try { return (IMFAttributes)Marshal.GetObjectForIUnknown(p); }
            finally { if (p != IntPtr.Zero) Marshal.Release(p); }
        }

        private static void SetPair(IMFAttributes at, Guid key, uint hi, uint lo)
        {
            Guid g = key;
            at.SetUINT64(ref g, ((ulong)hi << 32) | lo);
        }

        public bool TryInitialize(int width, int height, int fps, int bitrateKbps)
        {
            if (_initialized) return true;
            if (width <= 0 || height <= 0) return false;
            // yuv420p 要求偶数尺寸
            if ((width & 1) != 0) width--;
            if ((height & 1) != 0) height--;
            if (width <= 0 || height <= 0) return false;

            try
            {
                EnsureMfStarted();
                _width = width; _height = height; _fps = Math.Max(1, fps);
                _frameDuration = 10_000_000L / _fps;

                _tmpPath = Path.Combine(Path.GetTempPath(), "soullink-h264-" + Guid.NewGuid().ToString("N") + ".mp4");

                IntPtr targetType = IntPtr.Zero, inputType = IntPtr.Zero;
                try
                {
                    // ① 输出类型：H.264，Main Profile，指定码率
                    Check(MFCreateMediaType(out targetType), "MFCreateMediaType(out)");
                    var ot = AsAttributes(targetType);
                    Guid k = K_MAJOR, v = V_VIDEO; ot.SetGUID(ref k, ref v);
                    k = K_SUBTYPE; v = V_H264; ot.SetGUID(ref k, ref v);
                    k = K_BITRATE; ot.SetUINT32(ref k, (uint)(Math.Max(500, bitrateKbps) * 1000));
                    k = K_INTERLACE; ot.SetUINT32(ref k, 2);   // Progressive
                    k = K_PROFILE; ot.SetUINT32(ref k, 77);    // Main Profile
                    SetPair(ot, K_SIZE, (uint)width, (uint)height);
                    SetPair(ot, K_RATE, (uint)_fps, 1);
                    SetPair(ot, K_PAR, 1, 1);

                    // ② SinkWriter（.mp4 容器）
                    Check(MFCreateSinkWriterFromURL(_tmpPath, IntPtr.Zero, IntPtr.Zero, out var writer), "SinkWriter");
                    _writer = writer;
                    Check(writer.AddStream(targetType, out uint streamIndex), "AddStream");
                    _streamIndex = streamIndex;

                    // ③ 输入类型：RGB32（BGRA，自下而上）
                    Check(MFCreateMediaType(out inputType), "MFCreateMediaType(in)");
                    var it = AsAttributes(inputType);
                    k = K_MAJOR; v = V_VIDEO; it.SetGUID(ref k, ref v);
                    k = K_SUBTYPE; v = V_RGB32; it.SetGUID(ref k, ref v);
                    k = K_INTERLACE; it.SetUINT32(ref k, 2);
                    k = K_INDEP; it.SetUINT32(ref k, 1);   // 每帧独立
                    SetPair(it, K_SIZE, (uint)width, (uint)height);
                    SetPair(it, K_RATE, (uint)_fps, 1);
                    SetPair(it, K_PAR, 1, 1);

                    Check(writer.SetInputMediaType(streamIndex, inputType, IntPtr.Zero), "SetInputMediaType");
                    Check(writer.BeginWriting(), "BeginWriting");
                }
                finally
                {
                    if (targetType != IntPtr.Zero) { try { Marshal.Release(targetType); } catch { } }
                    if (inputType != IntPtr.Zero) { try { Marshal.Release(inputType); } catch { } }
                }

                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Log("MfH264Encoder.TryInitialize 失败：" + ex.Message);
                CleanupWriter();
                return false;
            }
        }

        public bool Encode(byte[] bgra, int stride, long timestampUs, out byte[]? nalUnits)
        {
            nalUnits = null;
            if (!_initialized || _completed || _disposed || _writer == null) return false;
            if (bgra == null || bgra.Length == 0) return false;
            if (stride <= 0) stride = _width * 4;

            try
            {
                int bufSize = _width * _height * 4;
                if (bgra.Length < bufSize) return false;

                Check(MFCreateMemoryBuffer((uint)bufSize, out var mb), "MFCreateMemoryBuffer");
                try
                {
                    mb.Lock(out IntPtr dst, out _, out _);
                    try
                    {
                        if (stride == _width * 4)
                        {
                            Marshal.Copy(bgra, 0, dst, bufSize);
                        }
                        else
                        {
                            // 非紧凑 stride：逐行拷贝（调用方给的步进不同，保持通用）
                            for (int y = 0; y < _height; y++)
                                Marshal.Copy(bgra, y * stride, IntPtr.Add(dst, y * _width * 4), _width * 4);
                        }
                    }
                    finally { mb.Unlock(); }
                    mb.SetCurrentLength((uint)bufSize);

                    Check(MFCreateSample(out IntPtr sample), "MFCreateSample");
                    try
                    {
                        SampleVtable.AddBuffer(sample, mb);
                        SampleVtable.SetSampleTime(sample, _frameCount * _frameDuration);
                        SampleVtable.SetSampleDuration(sample, _frameDuration);
                        Check(_writer.WriteSample(_streamIndex, sample), "WriteSample");
                    }
                    finally { Marshal.Release(sample); }
                }
                finally { Marshal.ReleaseComObject(mb); }

                _frameCount++;
                return true;
            }
            catch (Exception ex)
            {
                Log("MfH264Encoder.Encode 失败（第 " + _frameCount + " 帧）：" + ex.Message);
                return false;
            }
        }

        public bool Encode(IntPtr d3dTexture, long timestampUs, out byte[]? nalUnits)
        {
            // 第二步（GPU 纹理零拷贝）预留。第一步只做 CPU 路径。
            nalUnits = null;
            return false;
        }

        public bool RequestKeyFrame()
        {
            // 第一步 SinkWriter 自动管理关键帧，暂不支持手动请求。
            return false;
        }

        public bool Complete(out byte[] containerBytes)
        {
            containerBytes = Array.Empty<byte>();
            if (_completed || _disposed) return containerBytes.Length > 0;
            try
            {
                if (_writer == null || !_initialized)
                {
                    _completed = true;
                    return false;
                }

                Check(_writer.Finalize_(), "Finalize");

                string path = _tmpPath ?? "";
                _completed = true;
                CleanupWriter();

                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                containerBytes = File.ReadAllBytes(path);
                try { File.Delete(path); } catch { }
                return containerBytes.Length > 0;
            }
            catch (Exception ex)
            {
                Log("MfH264Encoder.Complete 失败：" + ex.Message);
                _completed = true;
                CleanupWriter();
                return false;
            }
        }

        private void CleanupWriter()
        {
            try
            {
                if (_writer != null) { Marshal.ReleaseComObject(_writer); }
            }
            catch { }
            _writer = null;
        }

        private static void Log(string msg)
        {
            // 复用现有异步日志，避免编码线程做文件 IO。
            try { AsyncLog.Append(Path.Combine(Path.GetTempPath(), "soullink-relay-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine); }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (!_completed) { try { _writer?.Finalize_(); } catch { } } } catch { }
            CleanupWriter();
            try
            {
                if (!string.IsNullOrEmpty(_tmpPath) && File.Exists(_tmpPath)) File.Delete(_tmpPath);
            }
            catch { }
        }

        // ── IMFSample vtable 直调（槽位与 Mp4Writer.SampleVtable 完全一致）──
        private static class SampleVtable
        {
            private static IntPtr Slot(IntPtr obj, int index)
                => Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int FnSetI64(IntPtr self, long value);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int FnAddBuffer(IntPtr self, IntPtr buffer);

            public static int AddBuffer(IntPtr sample, object buffer)
            {
                IntPtr p = Marshal.GetIUnknownForObject(buffer);
                try
                {
                    var fn = Marshal.GetDelegateForFunctionPointer<FnAddBuffer>(Slot(sample, 42));
                    return fn(sample, p);
                }
                finally { if (p != IntPtr.Zero) Marshal.Release(p); }
            }

            public static int SetSampleTime(IntPtr sample, long t)
                => Marshal.GetDelegateForFunctionPointer<FnSetI64>(Slot(sample, 36))(sample, t);

            public static int SetSampleDuration(IntPtr sample, long d)
                => Marshal.GetDelegateForFunctionPointer<FnSetI64>(Slot(sample, 38))(sample, d);
        }
    }
}
