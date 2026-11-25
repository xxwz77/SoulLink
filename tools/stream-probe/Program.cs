using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using WinRT;

using WinIDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;
using WinCaptureFramePool = Windows.Graphics.Capture.Direct3D11CaptureFramePool;
using WinCaptureSession = Windows.Graphics.Capture.GraphicsCaptureSession;
using WinCaptureItem = Windows.Graphics.Capture.GraphicsCaptureItem;
using WinCaptureFrame = Windows.Graphics.Capture.Direct3D11CaptureFrame;

namespace StreamProbe
{
    internal static class Program
    {
        private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

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

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        private interface IDirect3DDxgiInterfaceAccess
        {
            [PreserveSig] int GetInterface([In] ref Guid iid, [Out] out IntPtr p);
        }

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private static readonly List<string> Log = new();
        private static void Put(string line) { lock (Log) Log.Add(line); Console.WriteLine(line); }

        // ═══ WGC 抓屏（沿用 wgc-probe 已验证的路径）═══

        private static ID3D11Device _device;
        private static ID3D11DeviceContext _ctx;
        private static ID3D11Texture2D _staging;

        private static ID3D11Device CreateDeviceHardware(out bool hardware)
        {
            hardware = true;
            try
            {
                var r = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out var dev);
                if (r.Success && dev != null) { _ctx = dev.ImmediateContext; return dev; }
            }
            catch { }
            hardware = false;
            try
            {
                var r = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out var dev);
                if (r.Success && dev != null) { _ctx = dev.ImmediateContext; return dev; }
            }
            catch { }
            return null;
        }

        private static WinIDirect3DDevice CreateWinRtDevice(ID3D11Device device)
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr inspectable);
            if (hr != 0 || inspectable == IntPtr.Zero) return null;
            try { return MarshalInterface<WinIDirect3DDevice>.FromAbi(inspectable); }
            catch { Marshal.Release(inspectable); return null; }
        }

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc e, IntPtr l);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private static WinCaptureItem TryCreateForGameWindow()
        {
            WinCaptureItem result = null;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
                GetWindowThreadProcessId(h, out uint pid);
                string pname = ""; try { pname = Process.GetProcessById((int)pid).ProcessName; } catch { }
                if (pname.IndexOf("YuanShen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pname.IndexOf("Genshin", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var wid = new Windows.UI.WindowId((ulong)h.ToInt64());
                    var item = WinCaptureItem.TryCreateFromWindowId(wid);
                    if (item != null) { result = item; return false; }
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        // ═══ BGRA 回读 + 画时间戳 ═══

        private static byte[] ReadBackBgra(IntPtr texturePtr, int w, int h, out int stride)
        {
            stride = w * 4;
            using var src = new ID3D11Texture2D(texturePtr);
            var desc = src.Description;
            if (_staging == null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
            {
                _staging?.Dispose();
                var sd = new Texture2DDescription
                {
                    Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1,
                    Format = desc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read
                };
                _staging = _device.CreateTexture2D(sd);
            }
            _ctx.CopyResource(_staging, src);
            var mapped = _ctx.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int srcStride = (int)mapped.RowPitch;
                var tight = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * srcStride), tight, y * w * 4, w * 4);
                return tight;
            }
            finally { _ctx.Unmap(_staging, 0); }
        }

        /// <summary>在 BGRA 帧左上角画当前毫秒时间戳（System.Drawing 文字）。</summary>
        private static void DrawTimestamp(byte[] bgra, int w, int h)
        {
            try
            {
                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
                bmp.UnlockBits(bd);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                using var f = new System.Drawing.Font("Consolas", 28f, System.Drawing.FontStyle.Bold);
                using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
                using var fg = new System.Drawing.SolidBrush(System.Drawing.Color.Lime);
                string ts = DateTime.Now.ToString("HH:mm:ss.fff");
                var sz = g.MeasureString(ts, f);
                g.FillRectangle(bg, 0, 0, (int)sz.Width + 16, (int)sz.Height + 8);
                g.DrawString(ts, f, fg, 8, 4);
                var bd2 = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Marshal.Copy(bd2.Scan0, bgra, 0, bgra.Length);
                bmp.UnlockBits(bd2);
            }
            catch { }
        }

        private static byte[] BgraToNv12(byte[] bgra, int w, int h, int stride)
        {
            w &= ~1; h &= ~1;
            if (w <= 0 || h <= 0 || stride < w * 4 || bgra.Length < stride * h)
                throw new ArgumentException("BGRA 帧尺寸或 stride 无效");
            int ySize = w * h;
            var nv12 = new byte[w * h * 3 / 2];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride, yRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                    nv12[yRow + x] = (byte)((66 * r + 129 * g + 25 * b + 128) >> 8);
                }
            }
            for (int y = 0; y < h; y += 2)
            {
                int row0 = y * stride;
                int row1 = row0 + stride;
                int uvRow = ySize + (y / 2) * w;
                for (int x = 0; x < w; x += 2)
                {
                    int r = 0, g = 0, b = 0, count = 0;
                    for (int dy = 0; dy < 2 && y + dy < h; dy++)
                    {
                        int row = dy == 0 ? row0 : row1;
                        for (int dx = 0; dx < 2 && x + dx < w; dx++)
                        {
                            int i = row + (x + dx) * 4;
                            b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; count++;
                        }
                    }
                    r /= count; g /= count; b /= count;
                    nv12[uvRow + x] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    nv12[uvRow + x + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }
            return nv12;
        }

        // ═══ 编码器（复用探针逻辑，内联避免依赖主项目）═══

        private static IMFTransform _transform;
        private static int _outBufferSize;
        private static long _frameIndex;
        private static long _frameDuration;
        private static bool _asyncMode;   // true=硬件异步 MFT，false=软件同步 MFT
        private static bool _inputAccepted = true;  // 异步模式下 GetInputStatus 缓存

        private static bool InitEncoder(int w, int h, int fps, int bitrateKbps, bool preferHw)
        {
            try
            {
                MediaFactory.MFStartup(false);
                var inInfo = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_NV12 };
                var outInfo = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };

                // 优先硬件（异步），失败回退软件（同步）
                if (preferHw)
                {
                    var hwColl = MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, 4, inInfo, outInfo);
                    foreach (var act in hwColl)
                    {
                        try
                        {
                            _transform = act.ActivateObject<IMFTransform>();
                            // ★ 解锁异步模式（MF_TRANSFORM_ASYNC_UNLOCK = true）
                            try { _transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true); } catch { }
                            _asyncMode = true;
                            break;
                        }
                        catch { _transform = null; }
                    }
                }
                if (_transform == null)
                {
                    _asyncMode = false;
                    var swColl = MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, 3, inInfo, outInfo);
                    foreach (var act in swColl) { _transform = act.ActivateObject<IMFTransform>(); break; }
                }
                if (_transform == null) return false;
                Put("编码器模式: " + (_asyncMode ? "硬件异步(NVENC)" : "软件同步"));

                var outT = MediaFactory.MFCreateMediaType();
                outT.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                outT.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
                outT.Set(MF_MT_AVG_BITRATE, (uint)(bitrateKbps * 1000));
                outT.Set(MF_MT_INTERLACE_MODE, 2u);
                outT.Set(MF_MT_FRAME_SIZE, Pack((uint)w, (uint)h));
                outT.Set(MF_MT_FRAME_RATE, Pack((uint)fps, 1));
                outT.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                _transform.SetOutputType(0, outT, 0);

                var inT = MediaFactory.MFCreateMediaType();
                inT.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                inT.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
                inT.Set(MF_MT_INTERLACE_MODE, 2u);
                inT.Set(MF_MT_FRAME_SIZE, Pack((uint)w, (uint)h));
                inT.Set(MF_MT_FRAME_RATE, Pack((uint)fps, 1));
                inT.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                _transform.SetInputType(0, inT, 0);

                _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

                var oi = _transform.GetOutputStreamInfo(0);
                _outBufferSize = Math.Max(oi.Size, w * h * 3 / 2);
                _frameDuration = 10_000_000L / fps;
                _frameIndex = 0;
                return true;
            }
            catch (Exception ex) { Put("InitEncoder 异常: " + ex.Message); return false; }
        }

        private static ulong Pack(uint hi, uint lo) => ((ulong)hi << 32) | lo;

        private static byte[] EncodeNv12(byte[] nv12)
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

            byte[] result = null;
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

        // ★ 异步 MFT 输出泵：轮询 METransformHaveOutput 事件，排空输出 NAL
        private static byte[] EncodeNv12Async(byte[] nv12, byte[] pendingInput)
        {
            // 1. 喂输入（异步 MFT 用 GetInputStatus 判断是否接受）
            if (pendingInput != null)
            {
                var sample = MediaFactory.MFCreateSample();
                var buf = MediaFactory.MFCreateMemoryBuffer(pendingInput.Length);
                buf.Lock(out IntPtr pData, out _, out _);
                Marshal.Copy(pendingInput, 0, pData, pendingInput.Length);
                buf.Unlock();
                buf.CurrentLength = pendingInput.Length;
                sample.AddBuffer(buf);
                sample.SampleTime = _frameIndex * _frameDuration;
                sample.SampleDuration = _frameDuration;
                _frameIndex++;
                try { _transform.ProcessInput(0, sample, 0); } catch { }
            }

            // 2. 排空输出事件
            byte[] result = null;
            var gen = _transform.QueryInterfaceOrNull<IMFMediaEventGenerator>();
            if (gen == null) return null;

            while (true)
            {
                IMFMediaEvent ev;
                try { ev = gen.GetEvent(0); } catch { break; }
                if (ev == null) break;
                var et = ev.EventType;
                if (et == MediaEventTypes.TransformHaveOutput)
                {
                    var outBuf = new OutputDataBuffer { StreamID = 0 };
                    var outSample = MediaFactory.MFCreateSample();
                    var outMemBuf = MediaFactory.MFCreateMemoryBuffer(_outBufferSize);
                    outMemBuf.CurrentLength = 0;
                    outSample.AddBuffer(outMemBuf);
                    outBuf.Sample = outSample;
                    var status = new ProcessOutputStatus();
                    try
                    {
                        var r = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out status);
                        if (r.Success && outBuf.Sample != null)
                        {
                            var nals = ExtractNals(outBuf.Sample);
                            if (nals != null && nals.Length > 0) result = nals;
                        }
                    }
                    catch { }
                }
                else if (et == MediaEventTypes.TransformNeedInput)
                {
                    // 输入已空，可喂下一帧（由调用方通过返回值语义处理）
                    break;
                }
            }
            return result;
        }

        private static byte[] ExtractNals(IMFSample sample)
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

        // ═══ 显存监控 ═══

        private static (long used, long total) QueryVram()
        {
            try
            {
                // ★ 用临时文件捕获输出，避免 ReadToEnd 与 stderr 的经典死锁
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nvsmi_" + Guid.NewGuid().ToString("N") + ".txt");
                var psi = new ProcessStartInfo("nvidia-smi.exe", "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                // 并行读 stderr，避免阻塞
                var errTask = p.StandardError.ReadToEndAsync();
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } }
                string o = outTask.Result ?? "";
                errTask.Wait(1000);
                var parts = o.Trim().Split(',');
                if (parts.Length >= 2 && long.TryParse(parts[0].Trim(), out long u) && long.TryParse(parts[1].Trim(), out long t))
                    return (u, t);
            }
            catch { }
            return (0, 0);
        }

        private static bool BetterGiAlive()
        {
            foreach (var p in Process.GetProcessesByName("BetterGI"))
                if (p.SessionId == 18) return true;
            return false;
        }

        // ═══ 主流程 ═══

        private static volatile bool _stopRequested;
        private static volatile byte[] _latestFrame;
        private static volatile int _encFrames;
        private static volatile int _arrivedFrames;
        private static double _grabMs, _encodeMs;
        private static string _firstPumpError;

        private static int RunColorTest()
        {
            foreach (var color in new[] { ("red", (byte)0, (byte)0, (byte)255), ("blue", (byte)255, (byte)0, (byte)0) })
            {
                var bgra = new byte[4 * 4 * 4];
                for (int i = 0; i < bgra.Length; i += 4)
                {
                    bgra[i] = color.Item2; bgra[i + 1] = color.Item3; bgra[i + 2] = color.Item4;
                }
                var nv12 = BgraToNv12(bgra, 4, 4, 16);
                Console.WriteLine(color.Item1 + ": Y=" + nv12[0] + " U=" + nv12[16] + " V=" + nv12[17]);
            }
            return 0;
        }

        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            if (args.Contains("-ColorTest")) return RunColorTest();
            int port = 18099;
            int totalMs = 60000;  // 60 秒上限
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-Port" && i + 1 < args.Length) port = int.Parse(args[++i]);
                else if (args[i] == "-Sec" && i + 1 < args.Length) totalMs = int.Parse(args[++i]) * 1000;
            }

            Put("════ /stream 端到端延迟探针（A1 会话 WGC→H264→HttpListener）════");
            Put("SessionId=" + Process.GetCurrentProcess().SessionId + " 身份=" + System.Security.Principal.WindowsIdentity.GetCurrent().Name);
            var v0 = QueryVram();
            Put($"初始显存 used={v0.used} total={v0.total}");

            // 1. WGC 抓游戏窗口
            _device = CreateDeviceHardware(out bool hw);
            if (_device == null) { Put("❌ D3D11 设备失败"); return 1; }
            Put("D3D11: " + (hw ? "硬件" : "WARP"));
            var winDev = CreateWinRtDevice(_device);
            if (winDev == null) { Put("❌ WinRT 设备失败"); return 1; }
            var item = TryCreateForGameWindow();
            if (item == null) { Put("❌ 未找到游戏窗口"); return 1; }
            Put($"✓ CreateForWindow 成功 size={item.Size.Width}x{item.Size.Height}");

            var fmt = Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized;
            var pool = WinCaptureFramePool.CreateFreeThreaded(winDev, fmt, 2, item.Size);
            var session = pool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled = false; } catch { }
            try { session.IsBorderRequired = false; } catch { }

            int frameW = item.Size.Width, frameH = item.Size.Height;

            // 2. 编码器
            int ew = frameW & ~1, eh = frameH & ~1;
            bool preferHw = true;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "-Soft") preferHw = false;
            if (!InitEncoder(ew, eh, 30, 8000, preferHw)) { Put("❌ 编码器失败"); return 1; }
            Put("✓ 编码器就绪 " + ew + "x" + eh + " @30fps 8Mbps");

            // 3. HttpListener
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/stream/");
            listener.Start();
            Put($"✓ HttpListener 监听 127.0.0.1:{port}/stream");

            // 帧泵：FrameArrived 只置信号（**不在回调里 ReadBack**，避免 WGC 回调线程上做重活/COM 生命周期崩溃）
            var frameSignal = new ManualResetEventSlim(false);
            pool.FrameArrived += (sender, _) =>
            {
                _arrivedFrames++;
                frameSignal.Set();
            };

            session.StartCapture();
            Put("✓ StartCapture 已开始");

            // 编码泵线程：信号触发 → 排干帧池取最新帧 → ReadBack → 画时间戳 → 编码
            var frameQueue = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
            var encThread = new Thread(() =>
            {
                while (!_stopRequested)
                {
                    if (!frameSignal.Wait(200)) continue;
                    frameSignal.Reset();
                    try
                    {
                        // 排干帧池，只留最新一帧
                        WinCaptureFrame latest = null;
                        while (pool.TryGetNextFrame() is WinCaptureFrame f)
                        {
                            if (latest != null) latest.Dispose();
                            latest = f;
                        }
                        if (latest == null) continue;
                        using (latest)
                        {
                            var surface = latest.Surface;
                            var nativeObj = (surface as IWinRTObject)?.NativeObject;
                            if (nativeObj == null) continue;
                            IntPtr accessPtr = nativeObj.ThisPtr;
                            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                            Guid iidTex = IID_ID3D11Texture2D;
                            int hr = access.GetInterface(ref iidTex, out IntPtr texPtr);
                            // ★ 不手动 Release(accessPtr)：RCW 拥有它，手动 Release 会与 Finalize 双重释放（0xC0000005）
                            if (hr != 0 || texPtr == IntPtr.Zero) continue;
                            try
                            {
                                var sw = Stopwatch.StartNew();
                                int dummyStride;
                                var bgra = ReadBackBgra(texPtr, frameW, frameH, out dummyStride);
                                sw.Stop();
                                if (bgra == null) continue;
                                _grabMs = sw.Elapsed.TotalMilliseconds;
                                DrawTimestamp(bgra, frameW, frameH);

                                var sw2 = Stopwatch.StartNew();
                                var nv12 = BgraToNv12(bgra, ew, eh, frameW * 4);
                                var nal = _asyncMode ? EncodeNv12Async(nv12, nv12) : EncodeNv12(nv12);
                                sw2.Stop();
                                if (nal != null && nal.Length > 0)
                                {
                                    _encodeMs = sw2.Elapsed.TotalMilliseconds;
                                    _encFrames++;
                                    while (frameQueue.Count > 3) frameQueue.TryDequeue(out _);
                                    frameQueue.Enqueue(nal);
                                }
                            }
                            finally { }  // ReadBackBgra 内部用 using 拥有并释放 texPtr
                        }
                    }
                    catch (Exception ex) { if (_firstPumpError == null) _firstPumpError = ex.GetType().Name + ": " + ex.Message; }
                }
            }) { IsBackground = true, Name = "StreamEncode" };
            encThread.Start();

            // /stream 响应线程：从队列取 NAL 写 chunked
            var acceptThread = new Thread(() =>
            {
                try
                {
                    Task<System.Net.HttpListenerContext> pending = null;
                    while (!_stopRequested)
                    {
                        if (pending == null) pending = listener.GetContextAsync();
                        // ★ 任务跨循环保留：客户端连接完成的是同一个任务，不能每轮丢弃重建
                        pending.Wait(500);
                        if (pending.Status != TaskStatus.RanToCompletion) continue;
                        var ctx = pending.Result;
                        pending = null;
                        var resp = ctx.Response;
                        resp.StatusCode = 200;
                        resp.ContentType = "video/h264";
                        resp.Headers["Cache-Control"] = "no-store";
                        resp.SendChunked = true;
                        resp.KeepAlive = true;
                        Put("✓ 客户端连接 /stream");
                        var stream = resp.OutputStream;
                        var stop = DateTime.Now.AddMilliseconds(totalMs);
                        try
                        {
                            while (!_stopRequested && DateTime.Now < stop)
                            {
                                if (frameQueue.TryDequeue(out byte[] nal))
                                    stream.Write(nal, 0, nal.Length);
                                else
                                    Thread.Sleep(5);
                            }
                        }
                        catch { }
                        try { resp.Close(); } catch { }
                    }
                }
                catch { }
            }) { IsBackground = true, Name = "StreamAccept" };
            acceptThread.Start();

            // 主循环：显存监控 + 超时 + 帧数日志
            var swTotal = Stopwatch.StartNew();
            int lastLogged = 0;
            while (swTotal.ElapsedMilliseconds < totalMs)
            {
                Thread.Sleep(2000);
                var v = QueryVram();
                long free = v.total - v.used;
                int frames = _encFrames;
                if (frames != lastLogged || _arrivedFrames % 10 == 0)
                {
                    Put($"到达={_arrivedFrames} 编码帧={frames} 抓屏={_grabMs:F1}ms 编码={_encodeMs:F1}ms 显存 free={free}MiB" + (_firstPumpError != null ? " 泵错误=" + _firstPumpError : ""));
                    lastLogged = frames;
                }
                else if (_firstPumpError != null)
                {
                    Put("泵错误=" + _firstPumpError);
                    _firstPumpError = null; // 只报一次
                }
                // 保护：空闲显存 < 300MB 或 BetterGI 消失 → 立即停
                if (free > 0 && free < 300) { Put("⚠ 显存 <300MB，立即停止"); break; }
                if (!BetterGiAlive()) { Put("⚠ BetterGI 消失，立即停止"); break; }
            }

            Put("════ 停止：总运行 " + swTotal.ElapsedMilliseconds + "ms，编码 " + _encFrames + " 帧 ════");
            _stopRequested = true;
            try { session?.Dispose(); } catch { }
            try { pool?.Dispose(); } catch { }
            try { listener.Stop(); } catch { }
            try { MediaFactory.MFShutdown(); } catch { }
            return 0;
        }
    }
}
