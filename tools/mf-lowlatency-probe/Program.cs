using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

/// <summary>
/// MF 低延迟码流验证探针：微软软 H.264 MFT 编码固定图案，对比「设 ICodecAPI 前 vs 后」的 B 帧数量。
/// 验证 CODECAPI_AVLowLatencyMode + AVEncMPVDefaultBPictureCount=0 + AVEncCommonRateControlMode=CBR。
/// </summary>
internal static class Program
{
    static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00AA00389B71");
    static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    static ulong Pack(uint hi, uint lo) => ((ulong)hi << 32) | lo;

    public static int Main(string[] args)
    {
        bool setCodecApi = args.Length > 0 && args[0] == "-codecapi";
        bool lateCodecApi = args.Length > 0 && args[0] == "-codecapi-late";
        string outPath = args.Length > 1 && args[1].StartsWith("-out:") ? args[1].Substring(5) : Path.Combine(Path.GetTempPath(), "mf-lowlatency" + (setCodecApi ? "-api" : "-noapi") + ".h264");
        Console.WriteLine("=== MF 低延迟码流验证：" + (setCodecApi ? "设 ICodecAPI" : "不设 ICodecAPI（基线）") + " ===");
        MediaFactory.MFStartup(false);
        try
        {
            var inInfo = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_NV12 };
            var outInfo = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };
            using var swColl = MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, 3, inInfo, outInfo);

            IMFTransform transform = null;
            string mftName = "";
            foreach (var act in swColl)
            {
                mftName = act.GetString(FriendlyName);
                if (mftName.Equals("H264 Encoder MFT", StringComparison.OrdinalIgnoreCase))
                {
                    transform = act.ActivateObject<IMFTransform>();
                    break;
                }
            }
            if (transform == null) { Console.WriteLine("FAIL: 未找到微软软 H.264 MFT"); return 1; }
            Console.WriteLine("MFT: " + mftName);

            // ★ 模式1：SetOutputType 之前设（ffmpeg mfenc.c L869 的坑）
            if (setCodecApi) ApplyCodecApi(transform);

            using var outMediaType = MediaFactory.MFCreateMediaType();
            outMediaType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            outMediaType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            outMediaType.Set(MF_MT_AVG_BITRATE, 4_000_000u);
            outMediaType.Set(MF_MT_INTERLACE_MODE, 2u);
            outMediaType.Set(MF_MT_FRAME_SIZE, Pack(1280, 720));
            outMediaType.Set(MF_MT_FRAME_RATE, Pack(30, 1));
            outMediaType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            transform.SetOutputType(0, outMediaType, 0);

            // ★ 模式2：SetOutputType 之后设（测试微软软 MFT 是否需要这个时序）
            if (lateCodecApi) ApplyCodecApi(transform);

            using var inMediaType = MediaFactory.MFCreateMediaType();
            inMediaType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            inMediaType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
            inMediaType.Set(MF_MT_INTERLACE_MODE, 2u);
            inMediaType.Set(MF_MT_FRAME_SIZE, Pack(1280, 720));
            inMediaType.Set(MF_MT_FRAME_RATE, Pack(30, 1));
            inMediaType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            transform.SetInputType(0, inMediaType, 0);

            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            var outStreamInfo = transform.GetOutputStreamInfo(0);

            using var ms = new MemoryStream();
            int frames = 60;
            int gotFrames = 0;
            for (int f = 0; f < frames; f++)
            {
                var nv12 = MakeTestFrame(f);  // 1280x720 NV12

                using var sample = MediaFactory.MFCreateSample();
                using var buf = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
                buf.Lock(out IntPtr pData, out _, out _);
                Marshal.Copy(nv12, 0, pData, nv12.Length);
                buf.Unlock();
                buf.CurrentLength = nv12.Length;
                sample.AddBuffer(buf);
                sample.SampleTime = f * 333333L;
                sample.SampleDuration = 333333L;
                transform.ProcessInput(0, sample, 0);

                int outSize = Math.Max(outStreamInfo.Size, 1280 * 720 * 2);
                using var outSample = MediaFactory.MFCreateSample();
                using var outMemBuf = MediaFactory.MFCreateMemoryBuffer(outSize);
                outMemBuf.CurrentLength = 0;
                outSample.AddBuffer(outMemBuf);
                var outBuf = new OutputDataBuffer { StreamID = 0, Sample = outSample };
                var status = new ProcessOutputStatus();
                while (true)
                {
                    var r = transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out status);
                    if (!r.Success) break;
                    if (outBuf.Sample == null) break;
                    var nals = ExtractNals(outBuf.Sample);
                    if (nals != null && nals.Length > 0) { ms.Write(nals, 0, nals.Length); gotFrames++; }
                    outBuf.Sample = null;
                    outBuf.Sample = outSample;  // 复用
                    outSample.RemoveAllBuffers();
                    outSample.AddBuffer(outMemBuf);
                    outMemBuf.CurrentLength = 0;
                }
            }

            File.WriteAllBytes(outPath, ms.ToArray());
            Console.WriteLine("喂入 " + frames + " 帧，产出 NAL 帧 " + gotFrames + "，总字节 " + ms.Length);
            Console.WriteLine("输出: " + outPath);
            Console.WriteLine((gotFrames > 0 ? "PASS" : "FAIL"));
            return gotFrames > 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message); return 1; }
        finally { try { MediaFactory.MFShutdown(); } catch { } }
    }

    static void ApplyCodecApi(IMFTransform transform)
    {
        try
        {
            using var api = transform.QueryInterface<CodecApi>();
            if (api != null && api.NativePointer != IntPtr.Zero)
            {
                Console.WriteLine("ICodecAPI: available");
                TrySet(api, CodecApi.AVLowLatencyMode, 1, "AVLowLatencyMode=1");
                TrySet(api, CodecApi.AVEncMPVDefaultBPictureCount, 0, "DefaultBPictureCount=0");
                TrySet(api, CodecApi.AVEncCommonRateControlMode, 0, "RateControlMode=CBR(0)");
                TrySet(api, CodecApi.AVEncCommonMeanBitRate, 4_000_000u, "MeanBitRate=4000000");
            }
            else Console.WriteLine("ICodecAPI: unavailable");
        }
        catch (Exception ex) { Console.WriteLine("ICodecAPI 设置异常: " + ex.Message); }
    }

    static void TrySet(CodecApi api, Guid key, uint value, string label)
    {
        IntPtr variant = CodecApi.VariantUi4(value);
        try
        {
            int hr = api.Set(key, variant);
            Console.WriteLine(label + " SetValue HRESULT=0x" + hr.ToString("X8"));
        }
        finally { Marshal.FreeHGlobal(variant); }
    }

    static byte[] MakeTestFrame(int f)
    {
        int w = 1280, h = 720;
        int ySize = w * h;
        var nv12 = new byte[ySize + ySize / 2];
        // 有内容的图案（避免编码器压成极小），随时间变化保证 P 帧/B 帧有差异
        for (int y = 0; y < h; y++)
        {
            int band = (y + f * 10) / 60;
            for (int x = 0; x < w; x++)
            {
                int cb = (x + f * 8) / 100;
                int v = 30 + (band % 4) * 40 + (cb % 5) * 20 + ((x + f) % 7 == 0 ? 30 : 0);
                if (v > 255) v = 255;
                nv12[y * w + x] = (byte)v;
            }
        }
        for (int i = 0; i < ySize / 2; i += 2) { nv12[ySize + i] = 128; nv12[ySize + i + 1] = 128; }
        return nv12;
    }

    static byte[]? ExtractNals(IMFSample sample)
    {
        using var buf = sample.ConvertToContiguousBuffer();
        if (buf == null) return null;
        int len = buf.CurrentLength;
        var bytes = new byte[len];
        buf.Lock(out IntPtr pData, out _, out _);
        Marshal.Copy(pData, bytes, 0, len);
        buf.Unlock();
        return bytes;
    }
}

[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
internal sealed class CodecApi(IntPtr nativePointer) : ComObject(nativePointer)
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ValueDelegate(IntPtr self, ref Guid name, IntPtr value);

    public int Set(Guid name, IntPtr value) => Invoke(3, name, value);

    private int Invoke(int slot, Guid name, IntPtr value)
    {
        IntPtr vtbl = Marshal.ReadIntPtr(NativePointer);
        var call = Marshal.GetDelegateForFunctionPointer<ValueDelegate>(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size));
        return call(NativePointer, ref name, value);
    }

    public static readonly Guid AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");

    public static IntPtr VariantUi4(uint value)
    {
        IntPtr p = Marshal.AllocHGlobal(16);
        Marshal.Copy(new byte[16], 0, p, 16);
        Marshal.WriteInt16(p, (short)19);  // VT_UI4
        Marshal.WriteInt32(p, IntPtr.Size, unchecked((int)value));
        return p;
    }
}
