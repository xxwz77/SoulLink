using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

using WinIDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;
using WinDirect3D11CaptureFramePool = Windows.Graphics.Capture.Direct3D11CaptureFramePool;
using WinGraphicsCaptureSession = Windows.Graphics.Capture.GraphicsCaptureSession;
using WinGraphicsCaptureItem = Windows.Graphics.Capture.GraphicsCaptureItem;
using WinDirect3D11CaptureFrame = Windows.Graphics.Capture.Direct3D11CaptureFrame;
using WinIDirect3DSurface = Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface;

namespace WgcProbe
{
    internal static class Program
    {
        private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        private interface IDirect3DDxgiInterfaceAccess
        {
            [PreserveSig] int GetInterface([In] ref Guid iid, [Out] out IntPtr p);
        }

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, int rop);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        private static readonly List<string> Log = new();
        private static void Put(string line) { Log.Add(line); Console.WriteLine(line); }

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            string outPath = null;
            string snapPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-Out" && i + 1 < args.Length) outPath = args[++i];
                else if (args[i] == "-Snap" && i + 1 < args.Length) snapPath = args[++i];
            }

            Put("════ WGC 三种形态探测（目标会话本人身份）════");
            Put("进程 SessionId=" + Process.GetCurrentProcess().SessionId);
            Put("进程身份=" + System.Security.Principal.WindowsIdentity.GetCurrent().Name);
            Put("SM_REMOTESESSION=" + (GetSystemMetrics(0x1000) != 0 ? "1 (RDP 会话)" : "0 (本地控制台)"));
            Put("屏幕分辨率=" + GetSystemMetrics(0) + "x" + GetSystemMetrics(1));
            Put("");

            LogVram();

            // 1. D3D11 设备
            bool hardwareDevice = true;
            ID3D11Device device = CreateDeviceHardware(out hardwareDevice);
            if (device == null) { Put("❌ D3D11 硬件+WARP 都失败"); return Finish(1, outPath); }
            Put("D3D11 设备: " + (hardwareDevice ? "硬件 (RTX 4060)" : "WARP 软件光栅") + " OK");

            // 2. WinRT IDirect3DDevice
            WinIDirect3DDevice winDevice = CreateWinRtDevice(device);
            if (winDevice == null) { Put("❌ WinRT IDirect3DDevice 创建失败"); return Finish(1, outPath); }
            Put("WinRT IDirect3DDevice: OK");

            // 3. IsSupported（可能抛 0x80070424，不因抛异常就放弃）
            try { Put("GraphicsCaptureSession.IsSupported() = " + WinGraphicsCaptureSession.IsSupported()); }
            catch (Exception ex) { Put("IsSupported() 抛异常: 0x" + Marshal.GetHRForException(ex).ToString("X8") + " " + ex.Message + "（继续尝试）"); }

            // ═══ 形态 A：CreateForWindow（优先抓游戏窗口）═══
            Put("");
            Put("═══ 形态 A：CreateForWindow（优先游戏窗口）═══");
            WinGraphicsCaptureItem winItem = TryCreateForGameWindow();
            if (winItem == null) winItem = TryCreateForWindow();
            if (winItem != null)
            {
                if (!string.IsNullOrEmpty(snapPath))
                    Snapshot(winDevice, winItem, device, snapPath);
                else
                    Measure(winDevice, winItem, device, hardwareDevice, "CreateForWindow");
            }

            // ═══ 形态 B：CreateForMonitor ═══
            Put("");
            Put("═══ 形态 B：CreateForMonitor ═══");
            WinGraphicsCaptureItem monItem = TryCreateForMonitor();
            if (monItem != null) Measure(winDevice, monItem, device, hardwareDevice, "CreateForMonitor");

            // ═══ 形态 C：BitBlt 基线 ═══
            Put("");
            Put("═══ 形态 C：BitBlt 基线 ═══");
            MeasureBitBlt();

            Put("");
            Put("════ 探测完成 ════");
            return Finish(0, outPath);
        }

        private static void LogVram()
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi.exe", "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                string o = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                if (o.Length > 0) Put("GPU 显存(used,total MiB) = " + o);
            }
            catch { }
        }

        private static ID3D11Device CreateDeviceHardware(out bool hardware)
        {
            hardware = true;
            try
            {
                var r = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out var dev);
                if (r.Success) return dev;
                Put("硬件设备失败: 0x" + r.Code.ToString("X8") + "，尝试 WARP");
            }
            catch (Exception ex) { Put("硬件设备异常: " + ex.Message + "，尝试 WARP"); }

            hardware = false;
            try
            {
                var r = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out var dev);
                if (r.Success) return dev;
                Put("WARP 设备也失败: 0x" + r.Code.ToString("X8"));
            }
            catch (Exception ex) { Put("WARP 设备异常: " + ex.Message); }
            return null;
        }

        private static WinIDirect3DDevice CreateWinRtDevice(ID3D11Device device)
        {
            try
            {
                using var dxgi = device.QueryInterface<IDXGIDevice>();
                int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr inspectable);
                if (hr != 0 || inspectable == IntPtr.Zero)
                {
                    Put($"CreateDirect3D11DeviceFromDXGIDevice hr=0x{hr:X8}");
                    return null;
                }
                try { return MarshalInterface<WinIDirect3DDevice>.FromAbi(inspectable); }
                catch (Exception ex) { Put("FromAbi 异常: " + ex.Message); Marshal.Release(inspectable); return null; }
            }
            catch (Exception ex) { Put("CreateWinRtDevice 异常: " + ex.Message); return null; }
        }

        private static WinGraphicsCaptureItem TryCreateForMonitor()
        {
            try
            {
                var displayIds = Windows.Graphics.Display.DisplayServices.FindAll();
                Put($"DisplayServices.FindAll() = {displayIds.Length} 个显示器");
                foreach (var did in displayIds)
                {
                    try
                    {
                        var item = WinGraphicsCaptureItem.TryCreateFromDisplayId(did);
                        if (item != null)
                        {
                            Put("  ✓ CreateForMonitor(DisplayId=" + did.Value + ") 成功, size=" + item.Size.Width + "x" + item.Size.Height);
                            return item;
                        }
                        Put("  CreateForMonitor(DisplayId=" + did.Value + ") → null");
                    }
                    catch (Exception ex)
                    {
                        Put("  CreateForMonitor(DisplayId=" + did.Value + ") 异常: 0x" + Marshal.GetHRForException(ex).ToString("X8") + " " + ex.Message);
                    }
                }
            }
            catch (Exception ex) { Put("  DisplayServices.FindAll 异常: 0x" + Marshal.GetHRForException(ex).ToString("X8") + " " + ex.Message); }
            return null;
        }

        private static WinGraphicsCaptureItem TryCreateForWindow()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                var r = TryWindow(hwnd);
                if (r != null) return r;
            }
            Put("  前台窗口不可用，枚举可见顶层窗口…");
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) => { if (IsWindowVisible(h) && GetWindowTextLength(h) > 0) { found = h; return false; } return true; }, IntPtr.Zero);
            if (found != IntPtr.Zero) return TryWindow(found);
            Put("  无可用窗口");
            return null;
        }

        /// <summary>枚举顶层窗口，按标题/进程名找游戏窗口（YuanShen / Genshin 等）。</summary>
        private static WinGraphicsCaptureItem TryCreateForGameWindow()
        {
            WinGraphicsCaptureItem result = null;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
                uint pid = 0; GetWindowThreadProcessId(h, out pid);
                string pname = ""; try { pname = Process.GetProcessById((int)pid).ProcessName; } catch { }
                if (pname.IndexOf("YuanShen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pname.IndexOf("Genshin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pname.IndexOf("StarRail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pname.IndexOf("Honkai", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var r = TryWindow(h);
                    if (r != null) { result = r; return false; }
                }
                return true;
            }, IntPtr.Zero);
            if (result == null) Put("  未找到游戏窗口，回退前台/可见窗口");
            return result;
        }
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private static WinGraphicsCaptureItem TryWindow(IntPtr hwnd)
        {
            try
            {
                var wid = new Windows.UI.WindowId((ulong)hwnd.ToInt64());
                var item = WinGraphicsCaptureItem.TryCreateFromWindowId(wid);
                if (item != null)
                {
                    string title = ""; try { var sb = new System.Text.StringBuilder(256); GetWindowText(hwnd, sb, 256); title = sb.ToString(); } catch { }
                    Put("  ✓ CreateForWindow(hwnd=0x" + hwnd.ToInt64().ToString("X") + " \"" + title + "\") 成功, size=" + item.Size.Width + "x" + item.Size.Height);
                    return item;
                }
                Put("  CreateForWindow(hwnd=0x" + hwnd.ToInt64().ToString("X") + ") → null");
            }
            catch (Exception ex)
            {
                Put("  CreateForWindow(hwnd=0x" + hwnd.ToInt64().ToString("X") + ") 异常: 0x" + Marshal.GetHRForException(ex).ToString("X8") + " " + ex.Message);
            }
            return null;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        // ═══ 测量：帧到达率 + 内容有效性（非黑） + 单帧回读耗时 ═══

        private static void Measure(WinIDirect3DDevice winDevice, WinGraphicsCaptureItem item, ID3D11Device device, bool hardware, string tag)
        {
            WinDirect3D11CaptureFramePool pool = null;
            WinGraphicsCaptureSession session = null;
            try
            {
                var fmt = Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized;
                pool = WinDirect3D11CaptureFramePool.CreateFreeThreaded(winDevice, fmt, 2, item.Size);
                session = pool.CreateCaptureSession(item);
                try { session.IsCursorCaptureEnabled = false; } catch { }
                try { session.IsBorderRequired = false; } catch { }
                Put("  FramePool(容量2, CreateFreeThreaded) + Session: OK");

                // 统计：帧到达率 + 帧内容哈希（验证画面是否真的在更新）
                int arrived = 0;
                var intervals = new List<double>();
                var hashes = new HashSet<ulong>();
                long lastTs = 0;
                var sw = Stopwatch.StartNew();

                pool.FrameArrived += (sender, _) =>
                {
                    long now = Stopwatch.GetTimestamp();
                    if (lastTs != 0) intervals.Add((now - lastTs) * 1000.0 / Stopwatch.Frequency);
                    lastTs = now;
                    arrived++;
                    try
                    {
                        while (sender.TryGetNextFrame() is WinDirect3D11CaptureFrame f)
                        {
                            using (f)
                            {
                                ulong h = HashFrame(f, device);
                                if (h != 0) hashes.Add(h);
                            }
                        }
                    }
                    catch { }
                };

                session.StartCapture();
                Put("  StartCapture: OK，测 4 秒…");
                while (sw.ElapsedMilliseconds < 4000) System.Threading.Thread.Sleep(10);

                double fps = arrived / (sw.ElapsedMilliseconds / 1000.0);
                Put("");
                Put($"  [{tag}] 帧到达：{arrived} 帧 / {sw.ElapsedMilliseconds}ms → {fps:F1} fps");
                if (intervals.Count > 0)
                {
                    intervals.Sort();
                    double avg = 0; foreach (var d in intervals) avg += d; avg /= intervals.Count;
                    Put($"  [{tag}] 帧间隔：min={intervals[0]:F2} avg={avg:F2} p95={intervals[(int)(intervals.Count * 0.95)]:F2} max={intervals[intervals.Count - 1]:F2} ms");
                }
                Put($"  [{tag}] 帧内容哈希：去重后 {hashes.Count} 个不同画面（{arrived} 帧中）→ " + (hashes.Count >= Math.Min(10, arrived) ? "✅ 画面确实在持续更新" : hashes.Count >= 3 ? "⚠ 画面有变化但幅度小" : "静止画面（内容几乎不变）"));
                Put($"  [{tag}] → 60fps 预算 16.67ms。" + (fps >= 55 ? "✅ 帧到达率够 60fps" : fps >= 30 ? "⚠ ~30fps 档" : "❌ 帧到达率不足（可能画面静止）"));
            }
            catch (Exception ex)
            {
                Put($"  [{tag}] 测量异常: 0x" + Marshal.GetHRForException(ex).ToString("X8") + " " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { session?.Dispose(); } catch { }
                try { pool?.Dispose(); } catch { }
            }
        }

        /// <summary>抓一帧游戏画面，全帧回读，计算非黑占比并保存 BMP。</summary>
        private static void Snapshot(WinIDirect3DDevice winDevice, WinGraphicsCaptureItem item, ID3D11Device device, string savePath)
        {
            WinDirect3D11CaptureFramePool pool = null;
            WinGraphicsCaptureSession session = null;
            try
            {
                var fmt = Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized;
                pool = WinDirect3D11CaptureFramePool.CreateFreeThreaded(winDevice, fmt, 2, item.Size);
                session = pool.CreateCaptureSession(item);
                try { session.IsCursorCaptureEnabled = false; } catch { }
                try { session.IsBorderRequired = false; } catch { }
                Put("  FramePool + Session: OK，等待第一帧…");

                var gotFrame = new System.Threading.ManualResetEventSlim(false);
                WinDirect3D11CaptureFrame captured = null;
                pool.FrameArrived += (sender, _) =>
                {
                    try
                    {
                        var f = sender.TryGetNextFrame();
                        if (f != null && captured == null) { captured = f; gotFrame.Set(); }
                        else f?.Dispose();
                    }
                    catch { }
                };
                session.StartCapture();

                if (!gotFrame.Wait(5000)) { Put("  ❌ 5 秒内未收到帧（画面可能静止或不可捕获）"); return; }
                Put("  ✓ 收到一帧");

                // 全帧回读
                var (pixels, w, h, nonBlackRatio) = ReadFullFrame(captured, device);
                captured.Dispose();
                if (pixels == null) { Put("  ❌ 全帧回读失败"); return; }

                Put($"  帧尺寸 = {w}x{h}");
                Put($"  非黑像素占比 = {nonBlackRatio:P1}" + (nonBlackRatio > 0.5 ? " ✅ >50%" : " ⚠ ≤50%"));
                SaveBmp(savePath, pixels, w, h);
                Put($"  ✓ 已保存截图 → " + savePath);
            }
            catch (Exception ex)
            {
                Put("  Snapshot 异常: 0x" + Marshal.GetHRForException(ex).ToString("X8") + " " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { session?.Dispose(); } catch { }
                try { pool?.Dispose(); } catch { }
            }
        }

        private static (byte[] pixels, int w, int h, double nonBlack) ReadFullFrame(WinDirect3D11CaptureFrame frame, ID3D11Device device)
        {
            try
            {
                var surface = frame.Surface;
                if (surface == null) return (null, 0, 0, 0);
                var nativeObj = (surface as IWinRTObject)?.NativeObject;
                if (nativeObj == null) return (null, 0, 0, 0);
                IntPtr accessPtr = nativeObj.ThisPtr;
                if (accessPtr == IntPtr.Zero) return (null, 0, 0, 0);

                var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                Guid iidTex = IID_ID3D11Texture2D;
                int hr = access.GetInterface(ref iidTex, out IntPtr texPtr);
                Marshal.Release(accessPtr);
                if (hr != 0 || texPtr == IntPtr.Zero) return (null, 0, 0, 0);

                using var src = new ID3D11Texture2D(texPtr);
                var desc = src.Description;
                int w = (int)desc.Width, h = (int)desc.Height;

                if (_staging == null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
                {
                    _staging?.Dispose();
                    var sd = new Texture2DDescription
                    {
                        Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1,
                        Format = desc.Format,
                        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read
                    };
                    _staging = device.CreateTexture2D(sd);
                }

                device.ImmediateContext.CopyResource(_staging, src);
                var mapped = device.ImmediateContext.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                // BGRA → RGBA 输出，同时统计非黑
                var outPixels = new byte[w * h * 4];
                int srcStride = (int)mapped.RowPitch;
                long nonBlack = 0;
                try
                {
                    unsafe
                    {
                        byte* p = (byte*)mapped.DataPointer;
                        for (int y = 0; y < h; y++)
                        {
                            int srcOff = y * srcStride;
                            int dstOff = y * w * 4;
                            for (int x = 0; x < w; x++)
                            {
                                byte b = p[srcOff + x * 4 + 0];
                                byte g = p[srcOff + x * 4 + 1];
                                byte r = p[srcOff + x * 4 + 2];
                                byte a = p[srcOff + x * 4 + 3];
                                outPixels[dstOff + x * 4 + 0] = r;
                                outPixels[dstOff + x * 4 + 1] = g;
                                outPixels[dstOff + x * 4 + 2] = b;
                                outPixels[dstOff + x * 4 + 3] = 255;
                                if (r + g + b > 30) nonBlack++;
                            }
                        }
                    }
                }
                finally { device.ImmediateContext.Unmap(_staging, 0); }

                double ratio = (double)nonBlack / (w * h);
                return (outPixels, w, h, ratio);
            }
            catch { return (null, 0, 0, 0); }
        }

        /// <summary>写一个无压缩 24-bit BMP（自包含，不依赖外部库）。</summary>
        private static void SaveBmp(string path, byte[] rgba, int w, int h)
        {
            int rowSize = (w * 3 + 3) & ~3;  // 4 字节对齐
            int dataSize = rowSize * h;
            int fileSize = 54 + dataSize;
            var header = new byte[54];
            // BITMAPFILEHEADER
            header[0] = (byte)'B'; header[1] = (byte)'M';
            BitConverter.GetBytes(fileSize).CopyTo(header, 2);
            BitConverter.GetBytes(54).CopyTo(header, 10);
            // BITMAPINFOHEADER
            BitConverter.GetBytes(40).CopyTo(header, 14);
            BitConverter.GetBytes(w).CopyTo(header, 18);
            BitConverter.GetBytes(h).CopyTo(header, 22);
            header[26] = 1; header[28] = 24;  // 1 plane, 24bpp
            BitConverter.GetBytes(dataSize).CopyTo(header, 34);

            var body = new byte[dataSize];
            for (int y = 0; y < h; y++)
            {
                int srcOff = y * w * 4;
                int dstOff = (h - 1 - y) * rowSize;  // BMP 自底向上
                for (int x = 0; x < w; x++)
                {
                    body[dstOff + x * 3 + 0] = rgba[srcOff + x * 4 + 2]; // B
                    body[dstOff + x * 3 + 1] = rgba[srcOff + x * 4 + 1]; // G
                    body[dstOff + x * 3 + 2] = rgba[srcOff + x * 4 + 0]; // R
                }
            }
            var full = new byte[fileSize];
            Buffer.BlockCopy(header, 0, full, 0, 54);
            Buffer.BlockCopy(body, 0, full, 54, dataSize);
            System.IO.File.WriteAllBytes(path, full);
        }

        /// <summary>从 capture frame 取 ID3D11Texture2D，读 64 个采样点算一个粗哈希（不落盘、不回读全帧）。</summary>
        private static ulong HashFrame(WinDirect3D11CaptureFrame frame, ID3D11Device device)
        {
            try
            {
                var surface = frame.Surface;
                if (surface == null) return 0;
                // WinRT IDirect3DSurface → IDirect3DDxgiInterfaceAccess → ID3D11Texture2D
                var nativeObj = (surface as IWinRTObject)?.NativeObject;
                if (nativeObj == null) return 0;
                IntPtr accessPtr = nativeObj.ThisPtr;
                if (accessPtr == IntPtr.Zero) return 0;

                var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                Guid iidTex = IID_ID3D11Texture2D;
                int hr = access.GetInterface(ref iidTex, out IntPtr texPtr);
                Marshal.Release(accessPtr);
                if (hr != 0 || texPtr == IntPtr.Zero) return 0;

                using var src = new ID3D11Texture2D(texPtr);
                var desc = src.Description;

                // 建一个 staging 纹理（只第一次）
                if (_staging == null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
                {
                    _staging?.Dispose();
                    var sd = new Texture2DDescription
                    {
                        Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1,
                        Format = desc.Format,
                        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read
                    };
                    _staging = device.CreateTexture2D(sd);
                }

                device.ImmediateContext.CopyResource(_staging, src);
                var mapped = device.ImmediateContext.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                ulong hash = 1469598103934665603UL; // FNV offset basis
                try
                {
                    unsafe
                    {
                        byte* p = (byte*)mapped.DataPointer;
                        int stride = (int)mapped.RowPitch, w = (int)desc.Width, h = (int)desc.Height;
                        for (int i = 0; i < 64; i++)
                        {
                            int x = (i * 37) % Math.Max(1, w);
                            int y = (i * 61) % Math.Max(1, h);
                            int off = y * stride + x * 4;
                            for (int c = 0; c < 3; c++)
                            {
                                hash ^= p[off + c];
                                hash *= 1099511628211UL; // FNV prime
                            }
                        }
                    }
                }
                finally { device.ImmediateContext.Unmap(_staging, 0); }
                return hash;
            }
            catch { return 0; }
        }
        [ThreadStatic] private static ID3D11Texture2D _staging;

        private static void MeasureBitBlt()
        {
            int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
            if (w <= 0 || h <= 0) { Put("  BitBlt: 拿不到桌面尺寸"); return; }
            var costs = new List<double>();
            for (int i = 0; i < 30; i++)
            {
                IntPtr hScreen = GetDC(IntPtr.Zero);
                if (hScreen == IntPtr.Zero) { Put("  BitBlt: GetDC 失败"); return; }
                IntPtr hMem = CreateCompatibleDC(hScreen);
                IntPtr hBmp = CreateCompatibleBitmap(hScreen, w, h);
                IntPtr old = SelectObject(hMem, hBmp);
                var sw = Stopwatch.StartNew();
                BitBlt(hMem, 0, 0, w, h, hScreen, 0, 0, 0x00CC0020); // SRCCOPY
                sw.Stop();
                costs.Add(sw.Elapsed.TotalMilliseconds);
                SelectObject(hMem, old);
                DeleteObject(hBmp); DeleteDC(hMem); ReleaseDC(IntPtr.Zero, hScreen);
            }
            costs.Sort();
            double avg = 0; foreach (var d in costs) avg += d; avg /= costs.Count;
            Put($"  BitBlt({w}x{h})：min={costs[0]:F2} avg={avg:F2} max={costs[costs.Count - 1]:F2} ms（n={costs.Count}）");
            Put($"  → 60fps 预算 16.67ms。" + (avg < 16.67 ? "✅" : "❌ 光抓屏就超预算"));
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private static int Finish(int code, string outPath)
        {
            if (!string.IsNullOrWhiteSpace(outPath))
            {
                try { System.IO.File.WriteAllLines(outPath, Log, System.Text.Encoding.UTF8); } catch { }
            }
            return code;
        }
    }
}
