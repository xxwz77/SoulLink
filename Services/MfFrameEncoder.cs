using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

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
    public sealed class MfFrameEncoder : IDisposable
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
        private bool _initialized, _disposed;

        public string Name => "MF-Frame-H264";

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
        {
            if (_initialized) return true;
            if (width <= 0 || height <= 0) return false;
            if ((width & 1) != 0) width--;
            if ((height & 1) != 0) height--;
            if (width <= 0 || height <= 0) return false;

            try
            {
                EnsureStarted();
                _width = width; _height = height; _fps = Math.Max(1, fps);
                _frameDuration = 10_000_000L / _fps;

                var inInfo = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_NV12 };
                var outInfo = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };

                var hwColl = MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, 4, inInfo, outInfo);
                var swColl = MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, 3, inInfo, outInfo);

                // 第一版用软件 MFT（同步）；硬件 MFT 异步需事件驱动，后续再做。
                foreach (var act in swColl) { _transform = act.ActivateObject<IMFTransform>(); break; }
                if (_transform == null)
                    foreach (var act in hwColl) { _transform = act.ActivateObject<IMFTransform>(); break; }
                if (_transform == null) return false;

                var outMediaType = MediaFactory.MFCreateMediaType();
                outMediaType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                outMediaType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
                outMediaType.Set(MF_MT_AVG_BITRATE, (uint)(Math.Max(500, bitrateKbps) * 1000));
                outMediaType.Set(MF_MT_INTERLACE_MODE, 2u);
                outMediaType.Set(MF_MT_FRAME_SIZE, Pack((uint)width, (uint)height));
                outMediaType.Set(MF_MT_FRAME_RATE, Pack((uint)_fps, 1));
                outMediaType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                _transform.SetOutputType(0, outMediaType, 0);

                var inMediaType = MediaFactory.MFCreateMediaType();
                inMediaType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                inMediaType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
                inMediaType.Set(MF_MT_INTERLACE_MODE, 2u);
                inMediaType.Set(MF_MT_FRAME_SIZE, Pack((uint)width, (uint)height));
                inMediaType.Set(MF_MT_FRAME_RATE, Pack((uint)_fps, 1));
                inMediaType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                _transform.SetInputType(0, inMediaType, 0);

                _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

                var outInfo2 = _transform.GetOutputStreamInfo(0);
                _outBufferSize = Math.Max(outInfo2.Size, width * height * 3 / 2);

                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                AsyncLog.Append(Path.Combine(Path.GetTempPath(), "soullink-mf-encoder.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  TryInitialize 失败: " + ex.Message + Environment.NewLine);
                _transform = null;
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
            if (nv12 == null || nv12.Length == 0) return null;

            try
            {
                var sample = MediaFactory.MFCreateSample();
                var buf = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
                buf.Lock(out IntPtr pData, out _, out _);
                Marshal.Copy(nv12, 0, pData, nv12.Length);
                buf.Unlock();
                buf.CurrentLength = nv12.Length;
                sample.AddBuffer(buf);
                sample.SampleTime = _frameIndex * _frameDuration;
                sample.SampleDuration = _frameDuration;
                _frameIndex++;

                _transform.ProcessInput(0, sample, 0);

                var outBuf = new OutputDataBuffer { StreamID = 0 };
                var outSample = MediaFactory.MFCreateSample();
                var outMemBuf = MediaFactory.MFCreateMemoryBuffer(_outBufferSize);
                outMemBuf.CurrentLength = 0;
                outSample.AddBuffer(outMemBuf);
                outBuf.Sample = outSample;

                var status = new ProcessOutputStatus();
                byte[]? result = null;
                while (true)
                {
                    var r = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out status);
                    if (!r.Success) break;
                    if (outBuf.Sample == null) break;
                    result = ExtractNals(outBuf.Sample);
                    outBuf.Sample = null;
                }
                return result;
            }
            catch (Exception ex)
            {
                AsyncLog.Append(Path.Combine(Path.GetTempPath(), "soullink-mf-encoder.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  Encode 失败: " + ex.Message + Environment.NewLine);
                return null;
            }
        }

        private static byte[]? ExtractNals(IMFSample sample)
        {
            var buf = sample.ConvertToContiguousBuffer();
            if (buf == null) return null;
            int len = buf.CurrentLength;
            if (len <= 0) return null;
            var bytes = new byte[len];
            buf.Lock(out IntPtr pData, out _, out _);
            Marshal.Copy(pData, bytes, 0, len);
            buf.Unlock();
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
            _transform = null;
        }
    }
}
