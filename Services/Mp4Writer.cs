using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SoulLink.Services
{
    /// <summary>
    /// 用 Windows 自带 Media Foundation 把 JPEG 帧序列写成 H.264 MP4。
    ///
    /// ★★ 为什么不用 ffmpeg ★★
    ///   ffmpeg 单个 exe 就 164~220 MB，会让安装包从 7 MB 涨到 170 MB+。
    ///   系统自带 MF H.264 编码器零依赖、零体积，效果同级。
    ///
    /// ★★ 为什么不用 C# 手写 ComImport 接口（关键教训）★★
    ///   这条路翻车了三轮，原因是三个隐蔽的坑，任何一个都只会给出
    ///   误导性的错误码（0xC00D36E6 = MF_E_INVALIDMEDIATYPE），不指向真正原因：
    ///
    ///     ① IMFAttributes 的真实方法顺序里，GetAllocatedString(10) 和
    ///        GetAllocatedBlob(13) 极易被漏掉。漏一个 → 之后全体后移一格 →
    ///        SetUINT32 实际调到 DeleteAllItems（同样返回 S_OK）→
    ///        属性没写进去 → AddStream 报 MF_E_INVALIDMEDIATYPE。
    ///
    ///     ② IMFSample 在 mfobjects.idl 里标了 [local]，**不能跨 COM 边界编组**，
    ///        .NET 的 ComImport 调用它会直接 AccessViolation。必须走 vtable 直调。
    ///
    ///     ③ IMFMediaBuffer 的 IID 是 045fa593-8799-42b8-bc8d-8968c6453507，
    ///        容易误写成 066a-4794... 那个。
    ///
    ///   正确做法：从 wine-mirror/wine 的 mfobjects.idl / mfidl.idl
    ///   （与微软官方 IDL 同源）提取真实顺序，再写接口。
    ///   所以下面 IMFAttributes 的槽位注释请**不要改动顺序**。
    ///
    /// ★ 实测（1920x1080，60 帧真实桌面录像）：
    ///   JPEG 序列 4823 KB → MP4 273 KB（17.6 倍），ffprobe 校验 h264/Main/yuv420p 通过。
    /// </summary>
    internal static class Mp4Writer
    {
        // ── Media Foundation P/Invoke ────────────────────────────────────
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

        // ── 属性键（GUID）─────────────────────────────────────────────────
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

        /// <summary>QI 出 IMFAttributes（只有 28 个方法，容易核对）。</summary>
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

        // ── 对外入口 ─────────────────────────────────────────────────────

        /// <summary>
        /// 把 JPEG 文件序列编码成 MP4。
        /// </summary>
        /// <param name="jpegFiles">按顺序排好的 JPEG 路径</param>
        /// <param name="outputMp4">输出的 mp4 路径</param>
        /// <param name="fps">帧率</param>
        /// <param name="bitrateKbps">码率（1080p 建议 4000~8000）</param>
        public static bool TryEncode(IReadOnlyList<string> jpegFiles, string outputMp4, int fps,
            int bitrateKbps, out string message)
        {
            message = "";
            if (jpegFiles == null || jpegFiles.Count == 0) { message = "没有帧"; return false; }
            try
            {
                EnsureMfStarted();
                EncodeCore(jpegFiles, outputMp4, Math.Max(1, fps), Math.Max(500, bitrateKbps));
                var fi = new FileInfo(outputMp4);
                if (!fi.Exists || fi.Length == 0) { message = "输出的 MP4 为空"; return false; }
                message = "OK " + (fi.Length / 1024) + " KB";
                return true;
            }
            catch (Exception ex) { message = ex.Message; return false; }
        }

        /// <summary>把目录里的 frame-*.jpg 编码成 replay.mp4。</summary>
        public static bool TryEncodeDirectory(string dir, string outputMp4, int fps,
            int bitrateKbps, out string message)
        {
            var list = new List<string>();
            try
            {
                var files = Directory.GetFiles(dir, "frame-*.jpg");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                list.AddRange(files);
            }
            catch (Exception ex) { message = "读取帧失败：" + ex.Message; return false; }
            if (list.Count == 0) { message = "目录里没有 frame-*.jpg"; return false; }
            return TryEncode(list, outputMp4, fps, bitrateKbps, out message);
        }

        /// <summary>
        /// 把已编码 Annex-B H.264 直接封装成 MP4，不解码、不重编码。
        /// 保存时调用本机 ffmpeg 的 -c copy；实时管线不依赖它，也不打包额外工具。
        /// </summary>
        public static bool TryWriteH264(IReadOnlyList<byte[]> frames, int width, int height, int fps,
            string outputMp4, out string message)
        {
            message = "";
            if (frames == null || frames.Count == 0 || width <= 0 || height <= 0)
            { message = "没有可封装的 H.264 帧或尺寸无效"; return false; }
            System.Diagnostics.Process? process = null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false
                };
                foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "h264",
                    "-framerate", Math.Max(1, fps).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-i", "pipe:0", "-c", "copy", "-y", outputMp4 }) psi.ArgumentList.Add(arg);
                process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffmpeg（需要系统 PATH 中可用）");
                System.Threading.Tasks.Task<string> errors = process.StandardError.ReadToEndAsync();
                try
                {
                    foreach (var frame in frames)
                    {
                        if (frame == null || frame.Length == 0) continue;
                        process.StandardInput.BaseStream.Write(frame, 0, frame.Length);
                    }
                    process.StandardInput.Close();
                }
                catch (IOException) { /* ffmpeg 已退出，下面读取退出码和错误 */ }
                if (!process.WaitForExit(30000))
                {
                    process.Kill(true);
                    process.WaitForExit(5000);
                    message = "ffmpeg 封装超时";
                    return false;
                }
                string error = errors.GetAwaiter().GetResult();
                if (process.ExitCode != 0) { message = "ffmpeg 封装失败：" + error.Trim(); return false; }
                var file = new FileInfo(outputMp4);
                if (!file.Exists || file.Length == 0) { message = "封装后的 MP4 为空：" + error.Trim(); return false; }
                message = "OK " + file.Length / 1024 + " KB";
                return true;
            }
            catch (Exception ex) { message = "H.264 封装异常：" + ex.Message; return false; }
            finally { try { process?.Dispose(); } catch { } }
        }

        // ── 编码核心 ─────────────────────────────────────────────────────

        private static void EncodeCore(IReadOnlyList<string> jpegFiles, string outputMp4, int fps, int bitrateKbps)
        {
            // 用第一帧确定尺寸
            int width, height;
            using (var probe = Image.FromFile(jpegFiles[0]))
            {
                width = probe.Width;
                height = probe.Height;
            }
            // yuv420p 要求偶数尺寸
            if ((width & 1) != 0) width--;
            if ((height & 1) != 0) height--;

            IntPtr targetType = IntPtr.Zero, inputType = IntPtr.Zero;
            IMFSinkWriter? writer = null;
            try
            {
                // ① 输出类型：H.264
                Check(MFCreateMediaType(out targetType), "MFCreateMediaType(out)");
                var ot = AsAttributes(targetType);
                Guid k = K_MAJOR, v = V_VIDEO; ot.SetGUID(ref k, ref v);
                k = K_SUBTYPE; v = V_H264; ot.SetGUID(ref k, ref v);
                k = K_BITRATE; ot.SetUINT32(ref k, (uint)(bitrateKbps * 1000));
                k = K_INTERLACE; ot.SetUINT32(ref k, 2);        // Progressive
                k = K_PROFILE; ot.SetUINT32(ref k, 77);         // Main Profile
                SetPair(ot, K_SIZE, (uint)width, (uint)height);
                SetPair(ot, K_RATE, (uint)fps, 1);
                SetPair(ot, K_PAR, 1, 1);

                // ② SinkWriter
                Check(MFCreateSinkWriterFromURL(outputMp4, IntPtr.Zero, IntPtr.Zero, out writer), "SinkWriter");
                Check(writer.AddStream(targetType, out uint streamIndex), "AddStream");

                // ③ 输入类型：RGB32（BGRA）
                Check(MFCreateMediaType(out inputType), "MFCreateMediaType(in)");
                var it = AsAttributes(inputType);
                k = K_MAJOR; v = V_VIDEO; it.SetGUID(ref k, ref v);
                k = K_SUBTYPE; v = V_RGB32; it.SetGUID(ref k, ref v);
                k = K_INTERLACE; it.SetUINT32(ref k, 2);
                k = K_INDEP; it.SetUINT32(ref k, 1);            // 每帧独立
                SetPair(it, K_SIZE, (uint)width, (uint)height);
                SetPair(it, K_RATE, (uint)fps, 1);
                SetPair(it, K_PAR, 1, 1);

                Check(writer.SetInputMediaType(streamIndex, inputType, IntPtr.Zero), "SetInputMediaType");
                Check(writer.BeginWriting(), "BeginWriting");

                // ④ 逐帧写入
                int stride = width * 4;
                int bufSize = stride * height;
                long frameDuration = 10_000_000L / fps;    // 100ns
                var raw = new byte[bufSize];

                for (int i = 0; i < jpegFiles.Count; i++)
                {
                    if (!LoadAsBgraBottomUp(jpegFiles[i], raw, width, height, stride))
                        continue;   // 单帧坏掉就跳过，不让整段录像作废

                    Check(MFCreateMemoryBuffer((uint)bufSize, out var mb), "MFCreateMemoryBuffer");
                    mb.Lock(out IntPtr dst, out _, out _);
                    Marshal.Copy(raw, 0, dst, bufSize);
                    mb.Unlock();
                    mb.SetCurrentLength((uint)bufSize);

                    Check(MFCreateSample(out IntPtr sample), "MFCreateSample");
                    // ★ IMFSample 是 [local] 接口，必须走 vtable 直调
                    SampleVtable.AddBuffer(sample, mb);
                    SampleVtable.SetSampleTime(sample, i * frameDuration);
                    SampleVtable.SetSampleDuration(sample, frameDuration);

                    Check(writer.WriteSample(streamIndex, sample), "WriteSample");
                    Marshal.Release(sample);
                    Marshal.ReleaseComObject(mb);
                }

                Check(writer.Finalize_(), "Finalize");
            }
            finally
            {
                if (writer != null) { try { Marshal.ReleaseComObject(writer); } catch { } }
                if (targetType != IntPtr.Zero) { try { Marshal.Release(targetType); } catch { } }
                if (inputType != IntPtr.Zero) { try { Marshal.Release(inputType); } catch { } }
            }
        }

        /// <summary>
        /// JPEG → BGRA 缓冲，并**上下翻转**。
        /// ★ MF 的 RGB32 输入是自下而上（bottom-up）的，直接拷会得到倒置画面。
        /// </summary>
        private static bool LoadAsBgraBottomUp(string path, byte[] raw, int width, int height, int stride)
        {
            try
            {
                using var src = Image.FromFile(path);
                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                    g.DrawImage(src, 0, 0, width, height);
                }
                var bd = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    IntPtr sp = bd.Scan0;
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(IntPtr.Add(sp, y * bd.Stride), raw, (height - 1 - y) * stride, stride);
                }
                finally { bmp.UnlockBits(bd); }
                return true;
            }
            catch { return false; }
        }

        // ── IMFSample vtable 直调 ────────────────────────────────────────
        //
        //  mfobjects.idl：IMFSample : IMFAttributes（33 个），之后才是自己的方法：
        //    33 GetSampleFlags   38 SetSampleDuration
        //    34 SetSampleFlags   39 GetBufferCount
        //    35 GetSampleTime    40 GetBufferByIndex
        //    36 SetSampleTime    41 ConvertToContiguousBuffer
        //    37 GetSampleDuration 42 AddBuffer
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

    // ═══════════════════════════════════════════════════════════════════
    //  COM 接口定义
    //  ★★ 槽位顺序来自 wine-mirror/wine 的 mfobjects.idl（与微软官方同源）
    //     改动前请先核对：https://github.com/wine-mirror/wine/blob/master/include/mfobjects.idl
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// IMFAttributes —— 权威顺序（0 基）：
    ///   0 GetItem              10 GetAllocatedString  ← 极易漏！
    ///   1 GetItemType          11 GetBlobSize
    ///   2 CompareItem          12 GetBlob
    ///   3 Compare              13 GetAllocatedBlob   ← 极易漏！
    ///   4 GetUINT32            14 GetUnknown
    ///   5 GetUINT64            15 SetItem
    ///   6 GetDouble            16 DeleteItem
    ///   7 GetGUID              17 DeleteAllItems
    ///   8 GetStringLength      18 SetUINT32  ★
    ///   9 GetString            19 SetUINT64  ★
    ///                          20 SetDouble
    ///                          21 SetGUID    ★
    ///                          22 SetString
    /// </summary>
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem();                                            // 0
        [PreserveSig] int GetItemType();                                        // 1
        [PreserveSig] int CompareItem();                                        // 2
        [PreserveSig] int Compare();                                            // 3
        [PreserveSig] int GetUINT32(ref Guid g, out uint v);                    // 4
        [PreserveSig] int GetUINT64(ref Guid g, out ulong v);                   // 5
        [PreserveSig] int GetDouble(ref Guid g, out double v);                  // 6
        [PreserveSig] int GetGUID(ref Guid g, out Guid v);                      // 7
        [PreserveSig] int GetStringLength();                                    // 8
        [PreserveSig] int GetString();                                          // 9
        [PreserveSig] int GetAllocatedString();                                 // 10 ← 漏了就全错
        [PreserveSig] int GetBlobSize();                                        // 11
        [PreserveSig] int GetBlob();                                            // 12
        [PreserveSig] int GetAllocatedBlob();                                   // 13 ← 漏了就全错
        [PreserveSig] int GetUnknown();                                         // 14
        [PreserveSig] int SetItem();                                            // 15
        [PreserveSig] int DeleteItem();                                         // 16
        [PreserveSig] int DeleteAllItems();                                     // 17
        [PreserveSig] int SetUINT32(ref Guid g, uint v);                        // 18 ★
        [PreserveSig] int SetUINT64(ref Guid g, ulong v);                       // 19 ★
        [PreserveSig] int SetDouble(ref Guid g, double v);                      // 20
        [PreserveSig] int SetGUID(ref Guid g, ref Guid v);                      // 21 ★
        [PreserveSig] int SetString(ref Guid g, [MarshalAs(UnmanagedType.LPWStr)] string v); // 22
        [PreserveSig] int SetBlob();                                            // 23
        [PreserveSig] int SetUnknown();                                         // 24
        [PreserveSig] int LockStore();                                          // 25
        [PreserveSig] int UnlockStore();                                        // 26
        [PreserveSig] int GetCount(out uint c);                                 // 27
    }

    /// <summary>IMFMediaBuffer —— IID 是 8799-42b8，别写成 066a-4794。</summary>
    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr p, out uint max, out uint cur);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint len);
        [PreserveSig] int SetCurrentLength(uint len);
        [PreserveSig] int GetMaxLength(out uint len);
    }

    /// <summary>
    /// IMFSinkWriter —— 只声明用到的方法（调用走 vtable 前 N 个，顺序必须一致）。
    /// </summary>
    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IntPtr targetType, out uint idx);
        [PreserveSig] int SetInputMediaType(uint idx, IntPtr inputType, IntPtr encParams);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(uint idx, IntPtr sample);
        [PreserveSig] int SendStreamTick(uint idx, long ts);
        [PreserveSig] int PlaceMarker(uint idx, IntPtr ctx);
        [PreserveSig] int NotifyEndOfSegment(uint idx);
        [PreserveSig] int Flush(uint idx);
        [PreserveSig] int Finalize_();
    }
}
