using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MediaProbe
{
    /// <summary>
    /// SoulLink 媒体能力探测（只读 Spike）。
    ///
    /// ★ 目的：
    ///   STREAM-PERF-AUDIT §4.5(e) 提出「DXGI Desktop Duplication + MF H.264 硬编」，
    ///   但明确标注「RDP 会话下 Desktop Duplication 行为与物理桌面不同，需实测」，
    ///   并把它列为「先在 RDP 会话内做可行性 spike（1~2 天验证能否拿到桌面帧）」。
    ///   本程序就是这个 spike，输出直接喂给 docs/research/MEDIA-CAPABILITY-20261122.md。
    ///
    /// ★ 只读性（本程序不改变系统任何持久状态）：
    ///   · 只创建/销毁自己的 D3D 设备、duplication 对象、GDI 位图
    ///   · 不写配置、不改注册表、不启停服务、不结束任何进程、不碰 dist/
    ///   · 唯一的可选落盘是 -Out 指定的诊断报告（Markdown）
    ///
    /// ★ 为什么每一步都单独打错误码：
    ///   这类探测最常见的失败模式是「整条链失败但不知道断在哪」。
    ///   RDP 会话下有三个完全不同的断点，对应完全不同的方案：
    ///     ① D3D11CreateDevice 就失败          → 该会话没有可用 D3D 适配器
    ///     ② 枚举不到输出 / 拿不到 IDXGIOutput1 → 适配器层面不可见
    ///     ③ DuplicateOutput 返回 0x887A0004   → 有输出但桌面复制被禁（RDP 最常见）
    ///   所以必须分开报，不能笼统写「不支持」。
    /// </summary>
    internal static class Program
    {
        private const int WarmupFrames = 5;
        private const int MeasureFrames = 60;

        private static readonly List<ProbeResult> Results = new();

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            var opts = ParseArgs(args);

            Banner();
            Console.WriteLine();

            // 把 Add/Section 暴露给 MfProbe / Conclusion（它们在不同的文件里）
            Access.Add = Add;
            Access.Section = Section;

            // ═════════════════════════════════════════════════════════
            // 1. 会话环境 —— 先确认是不是 RDP，它决定后面每一条怎么解读
            // ═════════════════════════════════════════════════════════
            Section("1. 会话环境");
            bool isRemote = Native.GetSystemMetrics(Native.SM_REMOTESESSION) != 0;
            bool isRemoteCtl = Native.GetSystemMetrics(Native.SM_REMOTECONTROL) != 0;
            Native.ProcessIdToSessionId((uint)Environment.ProcessId, out uint sid);
            uint consoleSid = Native.WTSGetActiveConsoleSessionId();

            Add("SM_REMOTESESSION", isRemote ? "RDP" : "LOCAL",
                isRemote ? "运行在 RDP 会话内 → 结论可直接用于目标场景"
                         : "运行在本地控制台会话内，**不是 RDP**");
            Add("SM_REMOTECONTROL", isRemoteCtl ? "被控" : "否", "远程控制标志");
            Add("进程 SessionId", "INFO", $"session {sid}（活动控制台 = {consoleSid}）");
            Add("屏幕分辨率", "INFO", $"{Native.GetSystemMetrics(Native.SM_CXSCREEN)}x{Native.GetSystemMetrics(Native.SM_CYSCREEN)}");
            Add("监视器数量", "INFO", Native.GetSystemMetrics(Native.SM_CMONITORS).ToString());

            int dwmHr = Native.DwmIsCompositionEnabled(out bool dwmComp);
            Add("DWM 合成", dwmHr >= 0 ? (dwmComp ? "ON" : "OFF") : "PROBE-FAIL",
                dwmHr >= 0 ? $"hr=0x{dwmHr:X8}" : $"DwmIsCompositionEnabled hr=0x{dwmHr:X8}");

            // ═════════════════════════════════════════════════════════
            // 2. DXGI 适配器 / 输出
            // ═════════════════════════════════════════════════════════
            Section("2. DXGI 适配器 / 输出");
            int adapterCount = 0, outputTotal = 0;
            try
            {
                using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (int i = 0; ; i++)
                {
                    Result ahr = factory.EnumAdapters1((uint)i, out IDXGIAdapter1 adapter);
                    if (!ahr.Success || adapter == null) break;
                    adapterCount++;
                    var d = adapter.Description1;
                    string name = d.Description.TrimEnd('\0');

                    var outs = new List<string>();
                    for (int j = 0; ; j++)
                    {
                        Result ohr = adapter.EnumOutputs((uint)j, out IDXGIOutput output);
                        if (!ohr.Success || output == null) break;
                        var od = output.Description;
                        int w = od.DesktopCoordinates.Right - od.DesktopCoordinates.Left;
                        int h = od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top;
                        outs.Add($"[{j}] {od.DeviceName.ToString().TrimEnd('\0')} {w}x{h}");
                        output.Dispose();
                    }
                    outputTotal += outs.Count;
                    Add($"适配器 #{i}", outs.Count > 0 ? "OK" : "0-输出",
                        $"{name}  VRAM={d.DedicatedVideoMemory / 1048576}MB  "
                        + (outs.Count > 0 ? string.Join(" ", outs) : "无可见输出（RDP 常见）"));
                    adapter.Dispose();
                }
                if (adapterCount == 0) Add("DXGI 适配器", "FAIL", "一个都没枚举到");
            }
            catch (Exception ex) { Add("DXGI 工厂/适配器", "FAIL", ex.Message); }

            // ═════════════════════════════════════════════════════════
            // 3. D3D11 设备（硬件 / WARP）
            // ═════════════════════════════════════════════════════════
            Section("3. D3D11 设备");
            ID3D11Device device = null;
            ID3D11DeviceContext context = null;
            string devKind = "(无)";

            foreach (var (kind, dt) in new[] { ("硬件", DriverType.Hardware), ("WARP", DriverType.Warp) })
            {
                try
                {
                    var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
                    // Vortice 的重载：返回 Result，out device / out featureLevel / out context
                    Result hr = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                        IntPtr.Zero, dt, DeviceCreationFlags.BgraSupport, levels,
                        out ID3D11Device dev, out FeatureLevel fl, out ID3D11DeviceContext ctx);

                    if (hr.Success && dev != null)
                    {
                        Add($"D3D11 设备（{kind}）", "OK", $"succeeded, featureLevel={fl}, hr=0x{hr.Code:X8}");
                        if (device == null) { device = dev; context = ctx; devKind = kind; }
                        else { ctx?.Dispose(); dev?.Dispose(); }
                        break;
                    }
                    Add($"D3D11 设备（{kind}）", "FAIL", $"hr=0x{hr.Code:X8} {HrText(hr.Code)}");
                }
                catch (Exception ex) { Add($"D3D11 设备（{kind}）", "FAIL", ex.Message); }
            }

            if (device == null) Add("选用设备", "FAIL", "硬件与 WARP 均失败 → DXGI 桌面复制无法测试");

            // ═════════════════════════════════════════════════════════
            // 4. ★ 核心：Desktop Duplication
            // ═════════════════════════════════════════════════════════
            Section("4. DXGI Desktop Duplication（核心）");
            DupProbe dup = null;
            if (device == null) Add("DuplicateOutput", "SKIP", "无 D3D11 设备");
            else dup = TryDuplicate(device);

            // ═════════════════════════════════════════════════════════
            // 5. 采集耗时：桌面复制 vs BitBlt（现有路径）
            // ═════════════════════════════════════════════════════════
            Section($"5. 采集耗时对比（各 {MeasureFrames} 帧）");
            if (dup != null) MeasureDuplication(dup);
            else Add("桌面复制单帧耗时", "SKIP", "duplication 不可用");

            double bitBltAvg = MeasureBitBlt();

            // ═════════════════════════════════════════════════════════
            // 6. H.264 编码器枚举（硬 / 软）
            // ═════════════════════════════════════════════════════════
            Section("6. Media Foundation H.264 编码器枚举");
            MfInfo mf = null;
            try { mf = MfProbe.Run(device); }
            catch (Exception ex) { Add("MF 探测", "FAIL", ex.Message); }

            // ═════════════════════════════════════════════════════════
            // 7. 编码实测：GDI+ JPEG 基线 vs MF H.264
            // ═════════════════════════════════════════════════════════
            Section($"7. 编码实测（{MeasureFrames} 帧 1080p）");
            double jpegAvg = MeasureJpegBaseline();
            if (mf != null) mf.ReportEncodeBench(jpegAvg);

            // ═════════════════════════════════════════════════════════
            // 8. 60fps 可行性结论
            // ═════════════════════════════════════════════════════════
            Section("8. 60fps 可行性结论");
            var lines = Conclusion.Build(dup, dup?.Stats, bitBltAvg, jpegAvg, mf, isRemote, device != null);
            foreach (var l in lines) Console.WriteLine("  " + l);

            // ═════════════════════════════════════════════════════════
            // 汇总
            // ═════════════════════════════════════════════════════════
            Console.WriteLine();
            Console.WriteLine("════════════════════════════════════════════════════════");
            Console.WriteLine("  汇总");
            Console.WriteLine("════════════════════════════════════════════════════════");
            int ok = 0, fail = 0, other = 0;
            foreach (var r in Results)
            {
                if (r.Status == "OK") ok++;
                else if (r.Status == "FAIL") fail++;
                else other++;
            }
            Console.WriteLine($"  OK={ok}  FAIL={fail}  其它={other}  共 {Results.Count} 项");
            Console.WriteLine();
            Console.WriteLine("  ★ 会话性质：" + (isRemote
                ? "本次在 **RDP 会话**内运行 → 结论直接适用于「代理跑在 RDP 会话里」的目标场景。"
                : "本次在 **本地控制台**会话内运行（SM_REMOTESESSION=0）→ "
                  + "**不能**代表 RDP 会话内的行为，必须按 README「RDP 复测」再跑一次才算数。"));

            if (!string.IsNullOrEmpty(opts.Out))
            {
                try
                {
                    WriteReport(opts.Out, isRemote, lines);
                    Console.WriteLine("  报告已写出：" + opts.Out);
                }
                catch (Exception ex) { Console.WriteLine("  报告写出失败：" + ex.Message); }
            }

            dup?.Dispose();
            context?.Dispose();
            device?.Dispose();

            return fail == 0 ? 0 : 1;
        }

        // ══════════════════════════════════════════════════════════════
        //  Desktop Duplication
        // ══════════════════════════════════════════════════════════════
        internal sealed class DupStats
        {
            public List<double> AcquireMs = new();
            public int Timeouts, OtherErr, FramesWithContent;
            public double DirtyRectAvgPct = -1;   // 平均脏矩形占屏比
            public int DirtyRectSamples;
            public int Width, Height;
        }

        internal sealed class DupProbe : IDisposable
        {
            public IDXGIOutputDuplication Duplication;
            public DupStats Stats;
            public int OutputIndex;
            public string DeviceName = "";

            public void Dispose() => Duplication?.Dispose();
        }

        /// <summary>
        /// 逐个输出尝试 DuplicateOutput。
        ///
        /// ★ 为什么每个输出都要试：本机有三个显示适配器（含 GameViewer / OrayIddDriver
        ///   两个虚拟显示器）。只试第 0 个很容易得出错误的否定结论。
        /// </summary>
        private static DupProbe TryDuplicate(ID3D11Device device)
        {
            var failures = new List<string>();
            int tried = 0;

            try
            {
                using IDXGIDevice dxgiDev = device.QueryInterface<IDXGIDevice>();
                using IDXGIAdapter adapter = dxgiDev.GetAdapter();

                for (int oi = 0; ; oi++)
                {
                    Result ohr = adapter.EnumOutputs((uint)oi, out IDXGIOutput output);
                    if (!ohr.Success || output == null) break;
                    tried++;

                    string devName = "?";
                    int w = 0, h = 0;
                    try
                    {
                        var od = output.Description;
                        devName = od.DeviceName.ToString().TrimEnd('\0');
                        w = od.DesktopCoordinates.Right - od.DesktopCoordinates.Left;
                        h = od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top;
                    }
                    catch { }

                    try
                    {
                        // IDXGIOutput1 是 DuplicateOutput 的前置接口：拿不到它说明是 DXGI 1.0 输出
                        IDXGIOutput1 output1;
                        try { output1 = output.QueryInterface<IDXGIOutput1>(); }
                        catch (Exception ex)
                        {
                            failures.Add($"输出 {oi}（{devName}）：QueryInterface<IDXGIOutput1> 失败 — {ex.Message}");
                            continue;
                        }

                        // ★ Vortice 的 DuplicateOutput 成功时直接返回接口，失败时抛
                        //   SharpGenException，真正的 HRESULT 在 ex.ResultCode 里。
                        //   必须把它取出来报 —— 0x887A0004 和其它原因的处理方式完全不同。
                        using (output1)
                        {
                            try
                            {
                                IDXGIOutputDuplication duplication = output1.DuplicateOutput(device);
                                Add("DuplicateOutput", "OK", $"输出 {oi}（{devName} {w}x{h}）succeeded");
                                return new DupProbe
                                {
                                    Duplication = duplication,
                                    OutputIndex = oi,
                                    DeviceName = devName,
                                    Stats = new DupStats { Width = w, Height = h },
                                };
                            }
                            catch (SharpGenException ex)
                            {
                                int code = ex.ResultCode.Code;
                                failures.Add($"输出 {oi}（{devName} {w}x{h}）hr=0x{code:X8} {HrText(code)}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"输出 {oi}（{devName}）异常：{ex.GetType().Name}: {ex.Message}");
                    }
                    finally { output.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                Add("DuplicateOutput", "FAIL", "前置异常：" + ex.Message);
                return null;
            }

            if (tried == 0) Add("DuplicateOutput", "FAIL", "该设备下 0 个输出可枚举");
            else
            {
                for (int i = 0; i < failures.Count; i++)
                    Add($"DuplicateOutput 尝试 {i + 1}/{failures.Count}", "FAIL", failures[i]);
                Add("DuplicateOutput 结论", "FAIL", "所有输出都失败，详见上面逐条 hr");
            }
            return null;
        }

        /// <summary>
        /// 测「拿到一帧」的耗时。
        ///
        /// ★ 关键理解：AcquireNextFrame 会**阻塞**到有新帧或超时。
        ///   桌面静止时它会一直等到超时，那样算出的「耗时」其实是超时值。
        ///   这里用 timeout=0 非阻塞轮询，并把「真的拿到新帧」与「无变化超时」分开统计。
        /// </summary>
        private static void MeasureDuplication(DupProbe dup)
        {
            var s = dup.Stats;
            int total = MeasureFrames + WarmupFrames;

            for (int i = 0; i < total; i++)
            {
                var sw = Stopwatch.StartNew();
                Result hr = dup.Duplication.AcquireNextFrame(0, out OutduplFrameInfo info, out IDXGIResource resource);
                sw.Stop();

                if (hr.Success && resource != null)
                {
                    try
                    {
                        // 脏矩形占比：这是「脏矩形编码」方案能省多少带宽/编码量的直接依据。
                        //
                        // ★ 第一次实现时这里恒为 100%，原因是把 GetFrameDirtyRects 的
                        //   dirtyRectsBufferSize 参数误当成「元数据总大小」传了进去。
                        //   那个参数是**调用方提供的缓冲区字节数**，必须 = rects.Length * 16。
                        //   传小了会失败、传大了/错了会读到无意义数据，算出恒 100%。
                        if (info.TotalMetadataBufferSize > 0 && info.AccumulatedFrames > 0)
                        {
                            try
                            {
                                var rects = new RawRect[64];
                                uint bufBytes = (uint)(rects.Length * 16);   // sizeof(RECT) = 16
                                Result dr = dup.Duplication.GetFrameDirtyRects(bufBytes, rects, out uint needed);
                                if (dr.Success && needed > 0)
                                {
                                    int count = (int)(needed / 16);
                                    if (count > rects.Length) count = rects.Length;
                                    long area = 0;
                                    for (int k = 0; k < count; k++)
                                    {
                                        long w = rects[k].Right - rects[k].Left;
                                        long h = rects[k].Bottom - rects[k].Top;
                                        if (w > 0 && h > 0) area += w * h;
                                    }
                                    double pct = 100.0 * area / Math.Max(1, s.Width * s.Height);
                                    if (pct > 100) pct = 100;
                                    s.DirtyRectAvgPct = s.DirtyRectAvgPct < 0 ? pct : (s.DirtyRectAvgPct * 0.7 + pct * 0.3);
                                    s.DirtyRectSamples++;
                                    s.FramesWithContent++;
                                }
                            }
                            catch { /* 元数据拿不到不影响主结论 */ }
                        }
                        if (i >= WarmupFrames) s.AcquireMs.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    finally
                    {
                        resource.Dispose();
                        dup.Duplication.ReleaseFrame();
                    }
                }
                else if (hr.Code == unchecked((int)0x887A0027)) s.Timeouts++;
                else s.OtherErr++;

                Thread.Sleep(4);   // 约 1/4 帧间隔，避免把 CPU 拉满影响测量
            }

            if (s.AcquireMs.Count > 0)
            {
                var sorted = new List<double>(s.AcquireMs); sorted.Sort();
                double avg = 0; foreach (var v in sorted) avg += v; avg /= sorted.Count;
                Add("桌面复制 单帧耗时", "OK",
                    $"n={sorted.Count} 均值={avg:F3}ms p95={sorted[(int)(sorted.Count * 0.95)]:F3}ms 最大={sorted[^1]:F3}ms");
                Add("桌面复制 无变化超时", "INFO", $"{s.Timeouts}/{total}（画面静止时正常）");
                Add("桌面复制 理论帧率上限", "INFO", $"{1000.0 / Math.Max(0.001, avg):F0} fps（仅采集，不含编码）");
                if (s.DirtyRectAvgPct >= 0)
                    Add("脏矩形平均占比", "INFO", $"{s.DirtyRectAvgPct:F1}%（n={s.DirtyRectSamples} 帧，脏矩形编码能省的编码量上限）");
            }
            else
            {
                Add("桌面复制 单帧耗时", "FAIL", $"未取得新帧（超时 {s.Timeouts}，其它错误 {s.OtherErr}）");
            }

            // 额外做一次「阻塞 100ms」的尝试：验证真机在静止时是否也能被超时唤醒
            try
            {
                var sw = Stopwatch.StartNew();
                Result hr = dup.Duplication.AcquireNextFrame(100, out _, out IDXGIResource res);
                sw.Stop();
                if (hr.Success && res != null) { res.Dispose(); dup.Duplication.ReleaseFrame(); }
                Add("桌面复制 阻塞式取帧", "INFO",
                    $"timeout=100ms 实测等待 {sw.Elapsed.TotalMilliseconds:F1}ms，code=0x{hr.Code:X8}");
            }
            catch (Exception ex) { Add("桌面复制 阻塞式取帧", "FAIL", ex.Message); }
        }

        /// <summary>
        /// BitBlt 基线 —— SoulLink 现在的真实采集路径（Relay/SessionRecorder.cs:327）。
        ///
        /// ★ 单独测 BitBlt（不含 JPEG）。STREAM-PERF-AUDIT 里「13~16ms」是
        ///   BitBlt + JPEG 编码的**合并值**（WebServer.cs:136-137 注释），
        ///   这里拆开正是为了消掉那个混淆。
        /// </summary>
        private static double MeasureBitBlt()
        {
            // ★ 必须用**真实桌面像素尺寸**，不能用 SM_CXSCREEN：
            //   本机 SM_CXSCREEN 返回的是缩放后的逻辑尺寸（1707x960），
            //   而实际桌面是 2560x1440 —— 用错尺寸等于少拷一半像素，耗时会被系统性低估。
            var (w, h) = Native.GetRealDesktopSize();
            if (w <= 0 || h <= 0) { Add("BitBlt 基线", "FAIL", "拿不到桌面尺寸"); return -1; }

            IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBmp = IntPtr.Zero, oldObj = IntPtr.Zero;
            try
            {
                screenDc = Native.GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                { Add("BitBlt 基线", "FAIL", $"GetDC(NULL) err={Marshal.GetLastWin32Error()}"); return -1; }

                memDc = Native.CreateCompatibleDC(screenDc);
                hBmp = Native.CreateCompatibleBitmap(screenDc, w, h);
                if (hBmp == IntPtr.Zero)
                { Add("BitBlt 基线", "FAIL", $"CreateCompatibleBitmap err={Marshal.GetLastWin32Error()}"); return -1; }
                oldObj = Native.SelectObject(memDc, hBmp);

                var times = new List<double>();
                int failAt = -1;
                for (int i = 0; i < MeasureFrames + WarmupFrames; i++)
                {
                    var sw = Stopwatch.StartNew();
                    bool ok = Native.BitBlt(memDc, 0, 0, w, h, screenDc, 0, 0, Native.SRCCOPY);
                    sw.Stop();
                    if (!ok) { failAt = i; break; }
                    if (i >= WarmupFrames) times.Add(sw.Elapsed.TotalMilliseconds);
                    Thread.Sleep(4);
                }

                if (failAt >= 0)
                { Add("BitBlt 基线", "FAIL", $"第 {failAt} 帧 BitBlt 返回 false err={Marshal.GetLastWin32Error()}"); return -1; }

                var sorted = new List<double>(times); sorted.Sort();
                double avg = 0; foreach (var v in sorted) avg += v; avg /= sorted.Count;
                Add($"BitBlt 单帧（{w}x{h}）", "OK",
                    $"n={sorted.Count} 均值={avg:F3}ms p95={sorted[(int)(sorted.Count * 0.95)]:F3}ms 最大={sorted[^1]:F3}ms");
                Add("BitBlt 理论帧率上限", "INFO", $"{1000.0 / Math.Max(0.001, avg):F0} fps（仅 BitBlt，不含编码）");
                return avg;
            }
            catch (Exception ex) { Add("BitBlt 基线", "FAIL", ex.Message); return -1; }
            finally
            {
                if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero) Native.SelectObject(memDc, oldObj);
                if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp);
                if (memDc != IntPtr.Zero) Native.DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>
        /// GDI+ JPEG 编码基线（现有采集路径的编码部分）。
        /// 用一张 1080p 的拟真内容（渐变 + 噪点）编码，质量 75 与线上一致。
        /// </summary>
        private static double MeasureJpegBaseline()
        {
            int w = Math.Max(640, Native.GetSystemMetrics(Native.SM_CXSCREEN));
            int h = Math.Max(480, Native.GetSystemMetrics(Native.SM_CYSCREEN));
            if (w > 1920) w = 1920;
            if (h > 1080) h = 1080;

            try
            {
                using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                // 造一点接近真实桌面的内容：避免纯色导致 JPEG 编码异常快，得出乐观结论
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(28, 32, 40));
                    using var b1 = new SolidBrush(Color.FromArgb(60, 90, 140));
                    for (int i = 0; i < 12; i++) g.FillRectangle(b1, 40 + i * 60, 60 + (i % 4) * 90, 220, 70);
                    using var pen = new Pen(Color.FromArgb(200, 210, 220), 2);
                    for (int i = 0; i < 20; i++) g.DrawLine(pen, 20, 30 + i * 45, w - 20, 30 + i * 45);
                    using var f = new Font("Consolas", 16);
                    using var tb = new SolidBrush(Color.Gainsboro);
                    for (int i = 0; i < 15; i++) g.DrawString($"log line {i:D3}  soulink probe sample text", f, tb, 30, 300 + i * 26);
                }

                // 找一个 JPEG 编码器
                ImageCodecInfo jpg = null;
                foreach (var c in ImageCodecInfo.GetImageEncoders())
                    if (c.FormatID == ImageFormat.Jpeg.Guid) { jpg = c; break; }
                if (jpg == null) { Add("GDI+ JPEG 编码", "FAIL", "找不到 JPEG 编码器"); return -1; }

                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);

                long bytes = 0;
                var times = new List<double>();
                using var ms = new MemoryStream(1024 * 512);
                for (int i = 0; i < MeasureFrames + WarmupFrames; i++)
                {
                    ms.SetLength(0);
                    var sw = Stopwatch.StartNew();
                    bmp.Save(ms, jpg, ep);
                    sw.Stop();
                    bytes = ms.Length;
                    if (i >= WarmupFrames) times.Add(sw.Elapsed.TotalMilliseconds);
                }

                var sorted = new List<double>(times); sorted.Sort();
                double avg = 0; foreach (var v in sorted) avg += v; avg /= sorted.Count;
                Add($"GDI+ JPEG 编码（{w}x{h} q75）", "OK",
                    $"n={sorted.Count} 均值={avg:F3}ms p95={sorted[(int)(sorted.Count * 0.95)]:F3}ms 单帧={bytes / 1024}KB");
                Add("JPEG 理论帧率上限", "INFO", $"{1000.0 / Math.Max(0.001, avg):F0} fps（仅编码）");
                Add("JPEG 折算带宽", "INFO", $"{bytes / 1048576.0 * 30 * 60 / 60:F2} MB/s @30fps（单账号）");
                return avg;
            }
            catch (Exception ex) { Add("GDI+ JPEG 编码", "FAIL", ex.Message); return -1; }
        }

        // ══════════════════════════════════════════════════════════════
        //  工具
        // ══════════════════════════════════════════════════════════════
        private static void Add(string name, string status, string detail)
        {
            Results.Add(new ProbeResult(name, status, detail));
            Console.WriteLine($"  [{status,-8}] {name,-38} {detail}");
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("── " + title + " " + new string('─', Math.Max(4, 54 - title.Length)));
        }

        /// <summary>把常见 COM / DXGI 错误码翻成能直接指导决策的中文。</summary>
        internal static string HrText(int hr) => unchecked((uint)hr) switch
        {
            0x887A0004 => "DXGI_ERROR_UNSUPPORTED —— 该输出不支持桌面复制（RDP 会话最典型）",
            0x887A0022 => "DXGI_ERROR_NOT_CURRENTLY_AVAILABLE —— 复制会话已被占用",
            0x887A0027 => "DXGI_ERROR_WAIT_TIMEOUT —— 无新帧（不是错误）",
            0x887A0001 => "DXGI_ERROR_INVALID_CALL —— 参数不合法",
            0x887A0002 => "DXGI_ERROR_NOT_FOUND",
            0x887A0005 => "DXGI_ERROR_DEVICE_REMOVED —— 设备被移除",
            0x887A0006 => "DXGI_ERROR_DEVICE_HUNG",
            0x80070005 => "E_ACCESSDENIED —— 权限不足（安全桌面/锁屏）",
            0x8007000E => "E_OUTOFMEMORY",
            0x80004005 => "E_FAIL",
            0 => "S_OK",
            _ => "",
        };

        private static void Banner()
        {
            Console.WriteLine("════════════════════════════════════════════════════════");
            Console.WriteLine("  SoulLink 媒体能力探测（只读 Spike）");
            Console.WriteLine("  DXGI Desktop Duplication / Media Foundation H.264");
            Console.WriteLine("════════════════════════════════════════════════════════");
            Console.WriteLine("  .NET     : " + Environment.Version);
            Console.WriteLine("  OS       : " + Environment.OSVersion);
            Console.WriteLine("  架构     : " + RuntimeInformation.ProcessArchitecture);
            Console.WriteLine("  逻辑核   : " + Environment.ProcessorCount);
        }

        private static Options ParseArgs(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
                if ((args[i] == "-Out" || args[i] == "--out") && i + 1 < args.Length) o.Out = args[++i];
            return o;
        }

        private static void WriteReport(string path, bool isRemote, List<string> conclusion)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# media-probe 输出");
            sb.AppendLine();
            sb.AppendLine("- 时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("- 会话：" + (isRemote ? "**RDP**" : "本地控制台（非 RDP）"));
            sb.AppendLine("- .NET：" + Environment.Version);
            sb.AppendLine();
            sb.AppendLine("| 项 | 结果 | 详情 |");
            sb.AppendLine("|---|---|---|");
            foreach (var r in Results)
                sb.AppendLine($"| {r.Name} | {r.Status} | {r.Detail.Replace("|", "\\|")} |");
            sb.AppendLine();
            sb.AppendLine("## 结论");
            sb.AppendLine();
            foreach (var l in conclusion) sb.AppendLine("- " + l);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        internal sealed class Options { public string Out = ""; }
        internal sealed record ProbeResult(string Name, string Status, string Detail);
    }

    /// <summary>Win32 / gdi32 / dwmapi 的只读声明。</summary>
    internal static class Native
    {
        public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, SM_CMONITORS = 80;
        public const int SM_REMOTESESSION = 0x1000, SM_REMOTECONTROL = 0x2001;
        public const uint SRCCOPY = 0x00CC0020;

        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVMODE_HEAD
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODE_HEAD devMode);

        /// <summary>
        /// 拿真实桌面像素尺寸。
        /// ★ 不能用 SM_CXSCREEN —— 在 DPI 缩放 150% 的机器上它返回逻辑尺寸
        ///   （1707x960），而实际桌面是 2560x1440。测 BitBlt 必须用物理像素。
        /// </summary>
        public static (int W, int H) GetRealDesktopSize()
        {
            var dm = new DEVMODE_HEAD();
            dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE_HEAD>();
            // ENUM_CURRENT_SETTINGS = -1
            if (EnumDisplaySettingsW(null, -1, ref dm) && dm.dmPelsWidth > 0)
                return ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
            return (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        }
        [DllImport("kernel32.dll")] public static extern bool ProcessIdToSessionId(uint pid, out uint sid);
        [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("dwmapi.dll")] public static extern int DwmIsCompositionEnabled(out bool enabled);

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObj);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(
            IntPtr hDest, int x, int y, int w, int h, IntPtr hSrc, int sx, int sy, uint rop);
    }
}
