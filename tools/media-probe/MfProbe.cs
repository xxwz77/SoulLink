using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D11;

namespace MediaProbe
{
    /// <summary>
    /// Media Foundation H.264 编码器探测。
    ///
    /// ★ 要回答两个问题：
    ///   1) 本机到底有**哪些** H.264 编码器？(硬件 MFT / 软件 MFT)
    ///   2) 所谓「硬件编码」在 SoulLink 这个场景里能不能真的用上、
    ///      并且真的比现有的 GDI+ JPEG 软编便宜？
    ///
    /// ★ 为什么枚举要用 MFTEnumEx 而不是 MFCreateSinkWriterFromURL：
    ///   SinkWriter 会自动挑一个编码器，但**不告诉你挑的是谁**、是不是硬编。
    ///   要判断「有没有硬件 MFT」，必须直接枚举 IMFActivate 并读它的
    ///   MFT_ENUM_HARDWARE_URL_Attribute —— 这是唯一可靠的判据。
    ///
    /// ★ 关于「硬件编码」的诚实说明（写进报告也用这段）：
    ///   · MFT_ENUM_FLAG_HARDWARE 返回的是「注册为硬件 MFT」的编码器
    ///     （NVENC / QSV / AMF 都算）。
    ///   · 但 NVENC 在消费级驱动上通常有并发会话数限制（历史上有 2~3 路限制，
    ///     新版驱动放宽到 5~8 路），而 SoulLink 是**一个账号一路**、
    ///     8 个账号同时录制 → 这个限制是真实风险，必须写进结论。
    ///   · 另外硬编要求输入是 GPU 纹理（D3D11 纹理）；如果采集侧拿到的是
    ///     BitBlt 的 GDI 位图，还得先上传到 GPU —— 那一段拷贝本身也有成本。
    ///   · 所以「有硬件 MFT」≠「一定能省 CPU」，本程序会尽量实测。
    /// </summary>
    internal sealed class MfInfo
    {
        public readonly List<string> HardwareEncoders = new();
        public readonly List<string> SoftwareEncoders = new();
        public readonly List<string> AsyncEncoders = new();
        public int StartupHr;
        public bool MfAvailable;
        public double H264EncodeMsAvg = -1;
        public long H264BytesLast;
        public string EncodeNote = "";
        public long H264FileBytesTotal;      // 40 帧 MP4 的真实总字节数
        public double H264WriteMs;           // 纯 WriteSample 累计（异步队列入队成本）
        public double H264FinalizeMs;        // Finalize 排空队列 + 写盘
        public bool H264Plausible;           // 结果是否通过体积自校验
        public bool H264IsLowerBound;        // 数字是否只是「下界」（异步编码未全排队）

        /// <summary>把编码实测结果与 JPEG 基线对比后输出。</summary>
        public void ReportEncodeBench(double jpegAvgMs)
        {
            var add = Access.Add;

            if (H264EncodeMsAvg > 0 && H264FileBytesTotal >= 300 * 1024)
            {
                string bound = H264IsLowerBound ? "（下界：异步 MFT，CPU 侧仅计提交成本）" : "";
                add("MF H.264 单帧编码", "OK",
                    $"均值={H264EncodeMsAvg:F3}ms{bound}" + (H264BytesLast > 0 ? $" 单帧≈{H264BytesLast / 1024}KB" : ""));
                add("  H.264 诊断", "INFO",
                    $"40帧MP4={H264FileBytesTotal / 1024}KB  WriteSample累计={H264WriteMs:F1}ms  Finalize={H264FinalizeMs:F1}ms");
                if (H264FileBytesTotal > 0 && H264FileBytesTotal < 300 * 1024)
                    add("  H.264 可信度", "WARN",
                        "40 帧体积远低于合理下限 → 样本未形成有效码流（本程序的 NV12 填充过于平坦），此耗时不作编码成本依据");
                if (jpegAvgMs > 0)
                {
                    double ratio = H264EncodeMsAvg / jpegAvgMs;
                    add("H.264 vs GDI+ JPEG", ratio <= 1 ? "OK" : "INFO",
                        $"{ratio:F2}× （<1 表示 H.264 更快）");
                }
            }
            else if (H264FileBytesTotal > 0 && H264FileBytesTotal < 300 * 1024)
            {
                // ★ 诚实：测得出数字，但数字不可信。宁可报不可用，也不给它一个漂亮的排名。
                add("MF H.264 单帧编码", "UNRELIABLE",
                    $"测得 {H264EncodeMsAvg:F3}ms 但 40 帧仅 {H264FileBytesTotal / 1024}KB（下限 300KB）"
                    + " → 样本未形成有效码流，耗时不作编码成本使用");
            }
            else if (!string.IsNullOrEmpty(EncodeNote))
            {
                add("MF H.264 单帧编码", "SKIP", EncodeNote);
            }
        }
    }

    internal static class MfProbe
    {
        // ── Media Foundation 常量 ─────────────────────────────────────
        private const int MF_VERSION = 0x00020070;
        private const int MFSTARTUP_LITE = 1;

        private const int MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
        private const int MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
        private const int MFT_ENUM_FLAG_HARDWARE = 0x00000004;
        private const int MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;

        private const int MFMediaType_Video = 0x00000024;   // 仅作记录
        private static readonly Guid MFMediaType_VideoGuid = new("73646976-0000-0010-8000-00AA00389B71");   // MFMediaType_Video

        // 属性键
        private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid MF_SA_D3D11_AWARE = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
        private static readonly Guid MF_SA_D3D11_BINDFLAGS = new("eacf97ad-065c-440c-bee1-fde8be01108a");
        private static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        private static readonly Guid MFT_ENUM_HARDWARE_URL_Attribute = new("2fb866ac-b078-4942-ab6c-003d05cda674");
        private static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

        private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00AA00389B71");

        private const int MF_E_TOPO_CODEC_NOT_FOUND = unchecked((int)0xC00D5212);

        [StructLayout(LayoutKind.Sequential)]
        private struct MFT_REGISTER_TYPE_INFO { public Guid guidMajorType; public Guid guidSubtype; }

        [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int dwFlags);
        [DllImport("mfplat.dll")] private static extern int MFShutdown();
        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr ppMFType);
        [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IntPtr ppIMFSample);
        [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IntPtr ppBuffer);
        [DllImport("mfplat.dll")] private static extern int MFTEnumEx(
            Guid guidCategory, int flags,
            IntPtr pInputType, IntPtr pOutputType,
            out IntPtr pppMFTActivate, out int pnumMFTActivate);

        [DllImport("mfreadwrite.dll")]
        private static extern int MFCreateSinkWriterFromURL(
            [MarshalAs(UnmanagedType.LPWStr)] string pwszOutputURL,
            IntPtr pByteStream, IntPtr pAttributes, out IntPtr ppSinkWriter);

        // ── COM 接口（手写 vtable：只用到很少几个方法，比引整个 MF 互操作库轻）──
        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            [PreserveSig] int GetItem(in Guid key, IntPtr value);
            [PreserveSig] int GetItemType(in Guid key, out int type);
            [PreserveSig] int CompareItem(in Guid key, IntPtr value, out int result);
            [PreserveSig] int Compare(IntPtr theirs, int matchType, out int result);
            [PreserveSig] int GetUINT32(in Guid key, out uint value);
            [PreserveSig] int GetUINT64(in Guid key, out ulong value);
            [PreserveSig] int GetDouble(in Guid key, out double value);
            [PreserveSig] int GetGUID(in Guid key, out Guid value);
            [PreserveSig] int GetStringLength(in Guid key, out uint length);
            [PreserveSig] int GetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint size, out uint length);
            [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
            [PreserveSig] int GetBlobSize(in Guid key, out uint size);
            [PreserveSig] int GetBlob(in Guid key, IntPtr buf, uint bufsize, out uint blobsize);
            [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buf, out uint size);
            [PreserveSig] int GetUnknown(in Guid key, in Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(in Guid key, IntPtr value);
            [PreserveSig] int DeleteItem(in Guid key);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(in Guid key, uint value);
            [PreserveSig] int SetUINT64(in Guid key, ulong value);
            [PreserveSig] int SetDouble(in Guid key, double value);
            [PreserveSig] int SetGUID(in Guid key, in Guid value);
            [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob(in Guid key, IntPtr buf, uint size);
            [PreserveSig] int SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
            [PreserveSig] int CopyAllItems(IntPtr dest);
        }

        [ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFActivate
        {
            // 继承 IMFAttributes 全部方法（只声明我们真实用到的那些，顺序必须完全一致）
            [PreserveSig] int GetItem(in Guid key, IntPtr value);
            [PreserveSig] int GetItemType(in Guid key, out int type);
            [PreserveSig] int CompareItem(in Guid key, IntPtr value, out int result);
            [PreserveSig] int Compare(IntPtr theirs, int matchType, out int result);
            [PreserveSig] int GetUINT32(in Guid key, out uint value);
            [PreserveSig] int GetUINT64(in Guid key, out ulong value);
            [PreserveSig] int GetDouble(in Guid key, out double value);
            [PreserveSig] int GetGUID(in Guid key, out Guid value);
            [PreserveSig] int GetStringLength(in Guid key, out uint length);
            [PreserveSig] int GetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint size, out uint length);
            [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
            [PreserveSig] int GetBlobSize(in Guid key, out uint size);
            [PreserveSig] int GetBlob(in Guid key, IntPtr buf, uint bufsize, out uint blobsize);
            [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buf, out uint size);
            [PreserveSig] int GetUnknown(in Guid key, in Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(in Guid key, IntPtr value);
            [PreserveSig] int DeleteItem(in Guid key);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(in Guid key, uint value);
            [PreserveSig] int SetUINT64(in Guid key, ulong value);
            [PreserveSig] int SetDouble(in Guid key, double value);
            [PreserveSig] int SetGUID(in Guid key, in Guid value);
            [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob(in Guid key, IntPtr buf, uint size);
            [PreserveSig] int SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
            [PreserveSig] int CopyAllItems(IntPtr dest);
            // IMFActivate 自身
            [PreserveSig] int ActivateObject(in Guid riid, out IntPtr ppv);
            [PreserveSig] int ShutdownObject();
            [PreserveSig] int DetachObject();
        }

        public static MfInfo Run(ID3D11Device device)
        {
            var info = new MfInfo();
            var add = Access.Add;

            int hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
            info.StartupHr = hr;
            if (hr < 0)
            {
                add("MFStartup", "FAIL", $"hr=0x{hr:X8} {Program.HrText(hr)}");
                return info;
            }
            info.MfAvailable = true;
            add("MFStartup", "OK", $"hr=0x{hr:X8}");

            IntPtr activates = IntPtr.Zero;
            int count = 0;
            try
            {
                var outType = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFMediaType_VideoGuid, guidSubtype = MFVideoFormat_H264 };
                IntPtr outPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
                Marshal.StructureToPtr(outType, outPtr, false);

                // ★★ 关键修正：硬件 MFT 必须用 MFT_ENUM_FLAG_HARDWARE 单独枚举。
                //    只用 SYNCMFT|ASYNCMFT 时，NVENC/QSV/AMF 这类硬件编码器
                //    **根本不会出现在结果里**，会得出「本机没有硬件编码器」的假阴性。
                //    这是第一次跑就踩到的坑，值得写进交接文档。
                var categories = new[] {
                    new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a"),   // CLSID_MFT_CATEGORY_VIDEO_ENCODER
                };
                var flagSets = new (string Label, int Flags)[] {
                    ("硬件",  MFT_ENUM_FLAG_HARDWARE),
                    ("软件",  MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT),
                };

                bool anyEnumOk = false;
                foreach (var cat in categories)
                {
                    foreach (var (label, flags) in flagSets)
                    {
                        IntPtr act2 = IntPtr.Zero;
                        int c2 = 0;
                        int ehr = MFTEnumEx(cat, flags, IntPtr.Zero, outPtr, out act2, out c2);
                        if (ehr < 0)
                        {
                            add($"MFTEnumEx({label})", "FAIL", $"hr=0x{ehr:X8} {Program.HrText(ehr)}");
                            continue;
                        }
                        anyEnumOk = true;
                        add($"MFTEnumEx({label})", c2 > 0 ? "OK" : "NONE",
                            c2 > 0 ? $"找到 {c2} 个" : "0 个");

                        for (int i = 0; i < c2; i++)
                        {
                            IntPtr p = Marshal.ReadIntPtr(act2, i * IntPtr.Size);
                            if (p == IntPtr.Zero) continue;
                            var act = Marshal.GetObjectForIUnknown(p) as IMFActivate;
                            if (act == null) { Marshal.Release(p); continue; }

                            string name = ReadString(act, MFT_FRIENDLY_NAME_Attribute);
                            string hwUrl = ReadString(act, MFT_ENUM_HARDWARE_URL_Attribute);
                            bool isHw = label == "硬件" || !string.IsNullOrEmpty(hwUrl);

                            uint d3dAware = 0;
                            try { act.GetUINT32(MF_SA_D3D11_AWARE, out d3dAware); } catch { }
                            uint asyncMft = 0;
                            try { act.GetUINT32(new Guid("0c6d5a0b-1f2b-4f0e-9a3e-8e1b2b3c4d5e"), out asyncMft); } catch { }

                            string line = $"{name}"
                                        + (string.IsNullOrEmpty(hwUrl) ? "" : $"  url={hwUrl}")
                                        + (d3dAware != 0 ? "  D3D11-aware" : "  非D3D11");

                            if (isHw) info.HardwareEncoders.Add(line); else info.SoftwareEncoders.Add(line);
                            add($"  {label}编码器 #{i}", isHw ? "HW" : "SW", line);

                            Marshal.Release(p);
                        }
                        if (act2 != IntPtr.Zero) Marshal.FreeCoTaskMem(act2);
                    }
                }
                Marshal.FreeHGlobal(outPtr);

                if (!anyEnumOk)
                    add("MFTEnumEx", "FAIL", "所有枚举调用都失败");
            }
            catch (Exception ex)
            {
                add("MFTEnumEx", "FAIL", ex.Message);
            }
            finally
            {
                // 每个枚举结果在循环内已释放，这里无需额外清理
            }

            // 汇总硬件编码可用性
            if (info.HardwareEncoders.Count > 0)
                add("硬件 H.264 编码器", "OK", $"{info.HardwareEncoders.Count} 个可用");
            else
                add("硬件 H.264 编码器", "NONE", "没有注册硬件 MFT → 只能用软件编码");

            // ② 实测一次编码耗时（用 SinkWriter 写到临时文件，测完即删）
            info.EncodeNote = "未测";
            try
            {
                string tmpMp4 = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "media-probe-" + Guid.NewGuid().ToString("N") + ".mp4");
                double ms;
                long bytes;
                bool ok = TryEncodeBench(tmpMp4, 1920, 1080, out ms, out bytes, out string note,
                    out info.H264FileBytesTotal, out info.H264WriteMs, out info.H264FinalizeMs);
                try { if (System.IO.File.Exists(tmpMp4)) System.IO.File.Delete(tmpMp4); } catch { }
                if (ok) { info.H264EncodeMsAvg = ms; info.H264BytesLast = bytes; info.H264Plausible = true; }
                else info.EncodeNote = note;
            }
            catch (Exception ex) { info.EncodeNote = ex.Message; }

            MFShutdown();
            return info;
        }

        private static string ReadString(IMFActivate act, Guid key)
        {
            try
            {
                int hr = act.GetStringLength(key, out uint len);
                if (hr < 0 || len == 0) return "";
                var sb = new StringBuilder((int)len + 1);
                hr = act.GetString(key, sb, (uint)sb.Capacity, out _);
                if (hr < 0) return "";
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// 实测 H.264 编码单帧耗时。
        ///
        /// ★ 为什么用 SinkWriter 而不是直接拿 IMFTransform：
        ///   SinkWriter 是 MF 保证可用的那条路（项目 Services/Mp4Writer.cs 已经在用它），
        ///   直接拿 MFT 还要处理输入类型协商、D3D 纹理/系统内存两套路径，属于 P1 工作。
        ///   这里的目标只是「量一个可比的数量级」，SinkWriter 足够。
        ///
        /// ★ 局限（必须写进报告）：
        ///   这条路径走的是系统内存 NV12 → 编码器，**不是** GPU 纹理直通，
        ///   所以它测出的「硬件编码」是「走 MF 但没有 GPU 零拷贝」的耗时。
        ///   真实硬编 + GPU 直通会更快，但需要采集侧直接产出 D3D11 纹理。
        /// </summary>
        private static bool TryEncodeBench(string outPath, int w, int h, out double msAvg, out long bytes, out string note,
            out long fileBytesTotal, out double writeMs, out double finalizeMs)
        {
            msAvg = -1; bytes = 0; note = ""; fileBytesTotal = 0; writeMs = 0; finalizeMs = 0;

            IntPtr writer = IntPtr.Zero;
            try
            {
                int hr = MFCreateSinkWriterFromURL(outPath, IntPtr.Zero, IntPtr.Zero, out writer);
                if (hr < 0 || writer == IntPtr.Zero)
                { note = $"MFCreateSinkWriterFromURL hr=0x{hr:X8} {Program.HrText(hr)}"; return false; }

                var sink = (IMFSinkWriter)Marshal.GetObjectForIUnknown(writer);

                // 输出类型：H.264
                hr = MFCreateMediaType(out IntPtr outType);
                if (hr < 0) { note = "MFCreateMediaType(out)"; return false; }
                var ot = (IMFAttributes)Marshal.GetObjectForIUnknown(outType);
                ot.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_VideoGuid);
                ot.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
                ot.SetUINT32(MF_MT_AVG_BITRATE, 4_000_000);
                ot.SetUINT32(MF_MT_INTERLACE_MODE, 2);                 // Progressive
                ot.SetUINT64(MF_MT_FRAME_SIZE, Pack(w, h));
                ot.SetUINT64(MF_MT_FRAME_RATE, Pack(30, 1));
                ot.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));

                hr = sink.AddStream(outType, out int streamIndex);
                Marshal.Release(outType);
                if (hr < 0) { note = $"AddStream hr=0x{hr:X8} {Program.HrText(hr)}"; return false; }

                // 输入类型：NV12
                hr = MFCreateMediaType(out IntPtr inType);
                if (hr < 0) { note = "MFCreateMediaType(in)"; return false; }
                var it = (IMFAttributes)Marshal.GetObjectForIUnknown(inType);
                it.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_VideoGuid);
                it.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
                it.SetUINT32(MF_MT_INTERLACE_MODE, 2);
                it.SetUINT64(MF_MT_FRAME_SIZE, Pack(w, h));
                it.SetUINT64(MF_MT_FRAME_RATE, Pack(30, 1));
                it.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));

                hr = sink.SetInputMediaType(streamIndex, inType, IntPtr.Zero);
                Marshal.Release(inType);
                if (hr < 0) { note = $"SetInputMediaType hr=0x{hr:X8} {Program.HrText(hr)}"; return false; }

                hr = sink.BeginWriting();
                if (hr < 0) { note = $"BeginWriting hr=0x{hr:X8} {Program.HrText(hr)}"; return false; }

                // NV12 缓冲区：w*h*1.5
                uint nv12Size = (uint)(w * h * 3 / 2);
                var times = new List<double>();
                const int frames = 40;

                for (int i = 0; i < frames; i++)
                {
                    hr = MFCreateMemoryBuffer(nv12Size, out IntPtr buf);
                    if (hr < 0) { note = $"MFCreateMemoryBuffer hr=0x{hr:X8}"; return false; }

                    var mfBuf = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(buf);
                    mfBuf.Lock(out IntPtr pData, out _, out _);
                    // 填一点有内容的数据（全 0 会让编码器走最省的分支，导致结果过于乐观）
                    FillNv12(pData, w, h);
                    mfBuf.Unlock();
                    mfBuf.SetCurrentLength(nv12Size);

                    hr = MFCreateSample(out IntPtr samplePtr);
                    if (hr < 0) { note = $"MFCreateSample hr=0x{hr:X8}"; return false; }
                    var sample = (IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
                    sample.AddBuffer((IMFMediaBuffer)Marshal.GetObjectForIUnknown(buf));
                    sample.SetSampleTime(i * 333333);      // 30fps，单位 100ns
                    sample.SetSampleDuration(333333);

                    var sw = Stopwatch.StartNew();
                    hr = sink.WriteSample(streamIndex, sample);
                    sw.Stop();

                    Marshal.Release(buf);
                    Marshal.Release(samplePtr);

                    if (hr < 0) { note = $"WriteSample hr=0x{hr:X8} {Program.HrText(hr)}"; return false; }
                    if (i >= 5) times.Add(sw.Elapsed.TotalMilliseconds);   // 前几帧含初始化
                }

                // ★★ 关键诚实性修正：不能用 WriteSample 的耗时当「编码耗时」。
                //
                //   踩坑记录：第一版跑出「H.264 单帧 0.020ms，比 JPEG 快 236 倍，单帧只有 3KB」。
                //   这是**假数据**。原因：NVIDIA 的 H.264 MFT 是**异步 MFT**，
                //   WriteSample 只把样本排进队列就立刻返回，真正编码在另一个线程；
                //   而 3KB/帧说明 40 帧里绝大多数还没被编码器消费。
                //   如果直接把 0.020ms 写进报告，会得出「硬编便宜到可以忽略」的危险结论。
                //
                //   正确口径：**端到端吞吐** = (写入 N 帧 + Finalize 等队列排空) / N。
                //   Finalize 会阻塞到所有样本编码完成并写盘，这个总时长才是真实成本。
                var swEnd = Stopwatch.StartNew();
                sink.DoFinalize();
                swEnd.Stop();

                Marshal.Release(writer);
                writer = IntPtr.Zero;

                long fileBytes = 0;
                try { fileBytes = new System.IO.FileInfo(outPath).Length; } catch { }

                double writeSum = 0; foreach (var v in times) writeSum += v;
                double totalMs = swEnd.Elapsed.TotalMilliseconds + writeSum;

                // 前 5 帧是预热：计入总时间（本来就是成本），但不计入分母
                int counted = Math.Max(1, frames - 5);
                msAvg = totalMs / counted;
                bytes = frames > 0 ? fileBytes / frames : 0;

                // ★★ 自校验：40 帧 1080p H.264 的合理体积下限。
                //   4 Mbps @30fps × 40 帧 ≈ 666KB；就算画面极简也不该低于 100KB。
                //   实测 <50KB 说明样本根本没真正被编码（异步队列没排空 / 编码器拒收），
                //   这时**必须报不可信**，而不是把一个漂亮的小数字写进报告。
                //   这正是本程序第一版「3KB/帧、0.020ms」那个假数据的检测点。
                const long MinPlausibleBytes = 300 * 1024;
                if (fileBytes < MinPlausibleBytes)
                {
                    note = $"结果不可信：{frames} 帧只产出 {fileBytes / 1024}KB（合理下限 300KB），"
                         + "说明样本未被真正编码（异步 MFT 队列未排空或编码器拒收），"
                         + "本次 H.264 耗时不作数";
                    return false;
                }

                fileBytesTotal = fileBytes; writeMs = writeSum; finalizeMs = swEnd.Elapsed.TotalMilliseconds;
                note = "";
                return true;
            }
            catch (Exception ex) { note = ex.GetType().Name + ": " + ex.Message; return false; }
            finally
            {
                if (writer != IntPtr.Zero) { try { Marshal.Release(writer); } catch { } }
            }
        }

        /// <summary>
        /// 填 NV12 缓冲区。
        ///
        /// ★ 这里必须造**足够熵**的内容，否则编码器会把整帧压成极少字节，
        ///   得出的「H.264 单帧耗时」会异常低 —— 那是数据造成的假象，不是编码器快。
        ///   第一版只画了纯渐变，40 帧只产出 150KB（正常应 ≥300KB），
        ///   程序自己的体积自校验把它拦下来了。现在改成拟真桌面：
        ///   Y 平面 = 大块色区 + 高频文本纹理 + 噪点；UV 平面给真实色度。
        /// </summary>
        private static void FillNv12(IntPtr p, int w, int h)
        {
            unsafe
            {
                byte* y = (byte*)p;
                // 确定性伪随机（不用 Random，保证每次运行内容一致、结果可复现）
                uint seed = 12345;
                for (int r = 0; r < h; r++)
                {
                    int rowBase = r * w;
                    int band = r / 40;                      // 40 行一条色带，模拟窗口/分区
                    for (int c = 0; c < w; c++)
                    {
                        seed = seed * 1664525u + 1013904223u;
                        int noise = (int)(seed >> 27) & 0x1F;   // 0..31 的确定噪声
                        // 大块区域 + 每 4 像素一条的「文本纹理」+ 噪声，接近真实桌面
                        int textEdge = ((c % 4) == 0) ? 60 : 0;
                        int v = 30 + band * 12 + (c * 90 / w) + textEdge + noise;
                        if (v > 255) v = 255;
                        y[rowBase + c] = (byte)v;
                    }
                }
                byte* uv = y + w * h;
                for (int r = 0; r < h / 2; r++)
                {
                    for (int c = 0; c < w / 2; c++)
                    {
                        uv[r * w + c * 2] = (byte)(90 + (c * 70 / (w / 2)));       // U
                        uv[r * w + c * 2 + 1] = (byte)(140 - (r * 60 / (h / 2)));  // V
                    }
                }
            }
        }

        private static ulong Pack(int a, int b) => ((ulong)(uint)a << 32) | (uint)b;

        // ── SinkWriter 接口（只声明用到的方法，顺序必须与 MF 头文件一致）──
        [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSinkWriter
        {
            [PreserveSig] int AddStream(IntPtr pTargetMediaType, out int pdwStreamIndex);
            [PreserveSig] int SetInputMediaType(int dwStreamIndex, IntPtr pInputMediaType, IntPtr pEncodingParameters);
            [PreserveSig] int BeginWriting();
            [PreserveSig] int WriteSample(int dwStreamIndex, IMFSample pSample);
            [PreserveSig] int SendStreamTick(int dwStreamIndex, long llTimestamp);
            [PreserveSig] int PlaceMarker(int dwStreamIndex, IntPtr pvContext);
            [PreserveSig] int NotifyEndOfSegment(int dwStreamIndex);
            [PreserveSig] int Flush(int dwStreamIndex);
            [PreserveSig] int DoFinalize();
            [PreserveSig] int GetServiceForStream(int dwStreamIndex, in Guid guidService, in Guid riid, out IntPtr ppvObject);
            [PreserveSig] int GetStatistics(int dwStreamIndex, IntPtr pStats);
        }

        [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
            [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
            [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
        }

        // ★★ IMFSample 的 vtable 前面有 33 个槽位属于 IMFAttributes。
        //   两次踩坑记录：
        //     第一次：只声明 IMFSample 自己的方法 → SetSampleTime 打到错误槽位 → 0xC0000005
        //     第二次：写成 `interface IMFSample : IMFAttributes` → **仍然崩溃**。
        //            原因：DuplicateOutput 出来的对象是按 IMFAttributes 的 IID 取到的，
        //            而 C# 的接口继承在这个 QI 场景下不等于手写完整 vtable。
        //   最终方案：把 IMFAttributes 的 33 个方法**显式重复声明一遍**，完全不依赖编译器展开。
        //   这是 Microsoft 官方 interop 清单的可靠做法（代价是啰嗦，但不会错）。
        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            // ── IMFAttributes 前缀（33 个，顺序必须与 mfobjects.h 完全一致）──
            [PreserveSig] int GetItem(in Guid key, IntPtr value);
            [PreserveSig] int GetItemType(in Guid key, out int type);
            [PreserveSig] int CompareItem(in Guid key, IntPtr value, out int result);
            [PreserveSig] int Compare(IntPtr theirs, int matchType, out int result);
            [PreserveSig] int GetUINT32(in Guid key, out uint value);
            [PreserveSig] int GetUINT64(in Guid key, out ulong value);
            [PreserveSig] int GetDouble(in Guid key, out double value);
            [PreserveSig] int GetGUID(in Guid key, out Guid value);
            [PreserveSig] int GetStringLength(in Guid key, out uint length);
            [PreserveSig] int GetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint size, out uint length);
            [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
            [PreserveSig] int GetBlobSize(in Guid key, out uint size);
            [PreserveSig] int GetBlob(in Guid key, IntPtr buf, uint bufsize, out uint blobsize);
            [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buf, out uint size);
            [PreserveSig] int GetUnknown(in Guid key, in Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(in Guid key, IntPtr value);
            [PreserveSig] int DeleteItem(in Guid key);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(in Guid key, uint value);
            [PreserveSig] int SetUINT64(in Guid key, ulong value);
            [PreserveSig] int SetDouble(in Guid key, double value);
            [PreserveSig] int SetGUID(in Guid key, in Guid value);
            [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob(in Guid key, IntPtr buf, uint size);
            [PreserveSig] int SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
            [PreserveSig] int CopyAllItems(IntPtr dest);
            // ── IMFSample 自身（14 个）──
            [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
            [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
            [PreserveSig] int GetSampleTime(out long phnsSampleTime);
            [PreserveSig] int SetSampleTime(long hnsSampleTime);
            [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
            [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
            [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
            [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
            [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
            [PreserveSig] int RemoveAllBuffers();
            [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
            [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
        }
    }
}

namespace MediaProbe
{
    /// <summary>
    /// Main 里的 Add/Section 是私有静态，MF 与结论模块要用 —— 用一个共享的访问点转发，
    /// 避免把 Program 的私有 API 全部改成 internal（那会让 Main 的意图变模糊）。
    /// </summary>
    internal static class Access
    {
        public static Action<string, string, string> Add;
        public static Action<string> Section;
    }
}
