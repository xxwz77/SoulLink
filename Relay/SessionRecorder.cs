using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SoulLink.Relay
{
    /// <summary>
    /// 目标会话内的**屏幕录制器**：内存环形缓冲 + 按需保存。
    ///
    /// ★★ 为什么必须在代理进程里录 ★★
    ///   画面按会话隔离：别的会话 CopyFromScreen 只能得到自己的桌面；
    ///   投 SYSTEM 计划任务也不行 —— SYSTEM 在会话 0，那里没有交互式桌面，截出来全黑
    ///   （2026-11-22 实测）。只有**身处目标会话**的进程才能截到那个用户的真实画面。
    ///
    /// ★★ 为什么是内存环形缓冲，不是一直写盘 ★★
    ///   3 个账号 10fps 持续录 = 388 GB/天 的 SSD 写入量，长期跑伤盘。
    ///   实测（1920x1080，质量 60）：JPEG 压缩后 126 KB/帧，30 秒只有 **37 MB/账号**；
    ///   而原始位图 7.9 MB/帧（30 秒 2.4 GB）—— 所以**只存压缩后的字节**。
    ///   内存里留最近 30 秒，SSD 写入 ≈ 0；只有「保存」时才落盘。
    ///
    /// ★★ 尺寸取**代理所在会话**，不是主程序桌面 ★★
    ///   目标 RDP 会话是 1920x1080，主程序自己的桌面可能是别的大小。
    ///   GetSystemMetrics 在代理进程里返回的就是目标会话的尺寸，天然正确。
    ///   （代理需声明 DPI 感知，否则 150% 缩放下会拿到逻辑尺寸。）
    ///
    /// ★ 生命周期依附代理：代理断开 = RDP 断 = 没有画面，录制线程随之结束。
    /// </summary>
    internal static class SessionRecorder
    {
        // ── Win32 ────────────────────────────────────────────────────────────
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        // ── 光标叠加（3.3.18）────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        private const int CURSOR_SHOWING = 0x00000001;
        private const int DI_NORMAL = 0x0003;

        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y,
            IntPtr hIcon, int cx, int cy, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h,
            IntPtr hdcSrc, int x1, int y1, uint rop);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

        private const uint SRCCOPY = 0x00CC0020;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        // ── 缓存目录：跟程序目录走（用户要求「跟程序一个目录」）─────────────
        /// <summary>
        /// 截图/录像的落盘目录。默认 &lt;程序目录&gt;\capture，可用环境变量覆盖。
        /// </summary>
        public static string CaptureRoot
        {
            get
            {
                // ① 显式指定优先（主程序投送代理时会设这个变量）
                string? env = Environment.GetEnvironmentVariable("SOULLINK_CAPTURE_DIR");
                if (!string.IsNullOrWhiteSpace(env)) return env!;

                // ★★ ② 用户要求「肯定是放主程序位置啊，新建个目录」★★
                //   主程序投送代理时会设 SOULLINK_CAPTURE_DIR 指向自己的目录下的 capture；
                //   没设（手动代理模式）才按程序目录推。
                //   注意：代理可能运行在 Program Files 下（不可写），
                //   所以 CanCreate 失败时必须回退，否则录制会静默失败。
                string exeDir;
                try { exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "."; }
                catch { exeDir = "."; }

                string preferred = Path.Combine(exeDir, "capture");
                if (CanCreate(preferred)) return preferred;

                // ③ 兜底：ProgramData（所有用户可写、跨会话可读）
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "SoulLinkShare", "capture");
            }
        }

        /// <summary>目录能否真正创建（只 CreateDirectory 判断不出权限，要真建一次）。</summary>
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

        // ── 环形缓冲 ─────────────────────────────────────────────────────────

        /// <summary>一帧：JPEG 字节 + 采集时间。只存压缩数据，不存位图。</summary>
        private sealed class Frame
        {
            public byte[] Jpeg = Array.Empty<byte>();
            public DateTime At;
        }

        private static readonly object Gate = new object();
        private static readonly Queue<Frame> Ring = new();
        private static Thread? _worker;
        private static volatile bool _stopRequested;
        private static int _fps = 15;
        private static int _quality = 75;
        private static int _keepSeconds = 30;
        private static int _sessionId = 0;
        private static string _status = "idle";
        private static long _totalFrames;
        private static int _width, _height;

        /// <summary>当前状态文本（给 recordstatus 命令用）。</summary>
        public static string StatusText()
        {
            lock (Gate)
            {
                long bytes = 0;
                foreach (var f in Ring) bytes += f.Jpeg.Length;
                return _status
                    + "|fps=" + _fps
                    + "|quality=" + _quality
                    + "|frames=" + Ring.Count
                    + "|bytes=" + bytes
                    + "|total=" + _totalFrames
                    + "|size=" + _width + "x" + _height
                    + "|session=" + _sessionId;
            }
        }

        // ── 启停 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 开始录制（重复调用会更新参数而不是重启线程）。
        /// fps 用户可选；quality 默认 75（要求清晰）；keepSeconds 为环形缓冲时长。
        /// </summary>
        public static bool Start(int fps, int quality, int keepSeconds, int sessionId, out string message)
        {
            fps = Math.Clamp(fps, 1, 30);
            quality = Math.Clamp(quality, 30, 95);
            keepSeconds = Math.Clamp(keepSeconds, 5, 600);

            lock (Gate)
            {
                _fps = fps;
                _quality = quality;
                _keepSeconds = keepSeconds;
                _sessionId = sessionId;

                if (_worker != null && _worker.IsAlive)
                {
                    message = "已在录制，" + _fps + "fps 质量" + _quality + " 保留" + _keepSeconds + "秒";
                    return true;
                }

                _stopRequested = false;
                _status = "recording";
                _worker = new Thread(Loop) { IsBackground = true, Name = "SoulLinkRecorder" };
                _worker.Start();
                message = "开始录制 " + _fps + "fps 质量" + _quality
                    + " 环形保留 " + _keepSeconds + " 秒，缓存目录=" + CaptureRoot;
                return true;
            }
        }

        public static void Stop()
        {
            _stopRequested = true;
        }

        /// <summary>代理退出前调用：停止录制并等线程收尾。</summary>
        public static void Shutdown(int waitMs = 1500)
        {
            try
            {
                _stopRequested = true;
                var t = _worker;
                if (t != null && t.IsAlive) t.Join(waitMs);
            }
            catch { }
        }

        // ── 录制循环 ─────────────────────────────────────────────────────────

        private static void Loop()
        {
            try
            {
                while (!_stopRequested)
                {
                    int fps, quality, keepSec;
                    lock (Gate) { fps = _fps; quality = _quality; keepSec = _keepSeconds; }

                    int interval = Math.Max(1, 1000 / fps);
                    var sw = Stopwatch.StartNew();

                    var jpeg = GrabJpeg(quality);
                    if (jpeg != null)
                    {
                        var now = DateTime.Now;
                        lock (Gate)
                        {
                            Ring.Enqueue(new Frame { Jpeg = jpeg, At = now });
                            _totalFrames++;
                            // 丢掉超过保留时长的旧帧
                            var cutoff = now.AddSeconds(-keepSec);
                            while (Ring.Count > 0 && Ring.Peek().At < cutoff) Ring.Dequeue();
                        }
                    }

                    int wait = interval - (int)sw.ElapsedMilliseconds;
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            catch { }
            finally
            {
                lock (Gate) { _status = "idle"; _worker = null; }
            }
        }

        // ── 抓帧 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 把当前鼠标光标叠加到已抓取的屏幕位图上。
        ///
        /// ★ 为什么要手工画：
        ///   BitBlt(SRCCOPY) 抓的是屏幕像素，而光标由 DWM 作为独立图层合成，
        ///   不会出现在抓取结果里。所以录像和网页实时画面里看不到鼠标。
        ///
        /// ★ 关键细节（每个都能让光标画歪）：
        ///   ① **热点**：光标图标左上角不等于实际指向点，
        ///      GetIconInfo 的 xHotspot/yHotspot 才是。不减去它，
        ///      光标会整体偏移（箭头尖端不在真正的位置上）。
        ///   ② **DPI**：本进程已 SetProcessDPIAware（见 Run 里），
        ///      GetCursorInfo 返回的是物理像素，与 BitBlt 的坐标系一致，
        ///      所以这里**不做**任何缩放换算 —— 多算一次反而会错。
        ///   ③ **图标句柄**：GetIconInfo 会创建两个 GDI 位图（mask/color），
        ///      必须 DeleteObject，否则每帧泄漏两个句柄，几分钟就耗尽 GDI。
        /// </summary>
        private static void DrawCursorOn(IntPtr hdc, int w, int h)
        {
            var ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf<CURSORINFO>();
            if (!GetCursorInfo(ref ci)) return;
            if ((ci.flags & CURSOR_SHOWING) == 0) return;   // 光标隐藏时不画
            if (ci.hCursor == IntPtr.Zero) return;

            int x = ci.ptScreenPos.X;
            int y = ci.ptScreenPos.Y;

            // 用热点修正实际绘制的左上角
            if (GetIconInfo(ci.hCursor, out ICONINFO info))
            {
                try
                {
                    x -= info.xHotspot;
                    y -= info.yHotspot;
                }
                finally
                {
                    if (info.hbmMask != IntPtr.Zero) { try { DeleteObject(info.hbmMask); } catch { } }
                    if (info.hbmColor != IntPtr.Zero) { try { DeleteObject(info.hbmColor); } catch { } }
                }
            }

            // 完全在画面外就不画（省一次绘制）
            if (x < -64 || y < -64 || x > w + 64 || y > h + 64) return;

            // 0,0 表示用图标原始尺寸
            DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }

        private static byte[]? GrabJpeg(int quality)
        {
            IntPtr hScreen = IntPtr.Zero, hMem = IntPtr.Zero, hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                int w = GetSystemMetrics(SM_CXSCREEN);
                int h = GetSystemMetrics(SM_CYSCREEN);
                if (w <= 0 || h <= 0) return null;
                _width = w; _height = h;

                hScreen = GetDC(IntPtr.Zero);
                if (hScreen == IntPtr.Zero) return null;
                hMem = CreateCompatibleDC(hScreen);
                hBmp = CreateCompatibleBitmap(hScreen, w, h);
                if (hMem == IntPtr.Zero || hBmp == IntPtr.Zero) return null;
                old = SelectObject(hMem, hBmp);
                if (!BitBlt(hMem, 0, 0, w, h, hScreen, 0, 0, SRCCOPY)) return null;

                // ★★ 把鼠标光标画进画面（3.3.18）★★
                //
                //   BitBlt 只抓屏幕像素 —— 光标是 DWM 单独合成的图层，**不在抓取结果里**。
                //   所以录像/实时画面里永远看不到鼠标（用户反馈「没有同步鼠标轨迹」）。
                //   RDP 客户端能看到光标，是因为它走的是另一条通道（客户端自己渲染）。
                //
                //   这里手工取光标位置+形状，再用 DrawIconEx 叠加到内存 DC 上。
                //   注意必须在 BitBlt 之后、Image.FromHbitmap 之前，才能进到最终 JPEG。
                try { DrawCursorOn(hMem, w, h); } catch { }

                using var bmp = Image.FromHbitmap(hBmp);
                using var ms = new MemoryStream();
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                bmp.Save(ms, JpegCodec(), ep);
                return ms.ToArray();
            }
            catch { return null; }
            finally
            {
                if (hMem != IntPtr.Zero && old != IntPtr.Zero) { try { SelectObject(hMem, old); } catch { } }
                if (hBmp != IntPtr.Zero) { try { DeleteObject(hBmp); } catch { } }
                if (hMem != IntPtr.Zero) { try { DeleteDC(hMem); } catch { } }
                if (hScreen != IntPtr.Zero) { try { ReleaseDC(IntPtr.Zero, hScreen); } catch { } }
            }
        }

        private static ImageCodecInfo? _jpeg;
        private static ImageCodecInfo JpegCodec()
        {
            if (_jpeg != null) return _jpeg;
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) { _jpeg = c; break; }
            return _jpeg ?? throw new InvalidOperationException("找不到 JPEG 编码器");
        }

        // ── 对外：取帧 / 保存 ────────────────────────────────────────────────

        /// <summary>取最近一帧的 JPEG 字节（实时预览用）。</summary>
        public static byte[]? GetLatestJpeg()
        {
            lock (Gate)
            {
                if (Ring.Count == 0) return null;
                Frame? last = null;
                foreach (var f in Ring) last = f;
                return last?.Jpeg;
            }
        }

        /// <summary>取指定序号帧（0 = 最旧）。给"翻看最近画面"用。</summary>
        public static byte[]? GetFrameAt(int index)
        {
            lock (Gate)
            {
                if (index < 0 || index >= Ring.Count) return null;
                int i = 0;
                foreach (var f in Ring)
                {
                    if (i++ == index) return f.Jpeg;
                }
                return null;
            }
        }

        public static int FrameCount { get { lock (Gate) return Ring.Count; } }

        /// <summary>
        /// 保存录制到磁盘。
        ///
        /// ★★ 目录规范（用户明确指定）★★
        ///   &lt;主程序目录&gt;\录制回放\&lt;用户名-昵称&gt;\&lt;时间-触发条件&gt;\frame-xxxxx.jpg
        ///   例：录制回放\A2-山客\2026-11-22-101500-日志关键字\
        ///
        /// ★ "前后 N 秒"的语义（用户选方案 A：前 N + 后 N，N ≤ 保留时长/2）：
        ///   环形缓冲只有**过去**的帧，没有未来的帧。所以：
        ///     ① 立刻把 [T-N, T] 这段（前 N 秒）落盘；
        ///     ② 继续录制，等 N 秒后再把 [T, T+N]（后 N 秒）追加进来。
        ///   整个过程是"延迟 N 秒完成"，不是瞬间保存。
        /// </summary>
        /// <param name="userTag">用户名-昵称，作为一级目录</param>
        /// <param name="trigger">触发条件描述，拼在目录名末尾</param>
        /// <param name="beforeSeconds">触发前保留秒数</param>
        /// <param name="afterSeconds">触发后追加秒数（0=只存前段）</param>
        public static string? SaveRecording(string userTag, string trigger,
            double beforeSeconds, double afterSeconds, int sessionId,
            out int frames, out string message)
        {
            frames = 0;
            message = "";
            try
            {
                // 目录名：时间-触发条件（时间精确到秒，避免同秒冲突）
                string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
                string safeTrigger = Sanitize(trigger);
                string folder = stamp + (safeTrigger.Length > 0 ? "-" + safeTrigger : "");

                string dir = Path.Combine(CaptureRoot, "录制回放",
                    string.IsNullOrWhiteSpace(userTag) ? ("会话" + sessionId) : Sanitize(userTag),
                    folder);
                Directory.CreateDirectory(dir);

                // ① 先把"前 N 秒"落盘
                int written = WriteSlice(dir, beforeSeconds, startIndex: 0);
                frames = written;

                // ② 需要"后 N 秒"就等一段时间再追加
                if (afterSeconds > 0.5)
                {
                    int waitMs = (int)Math.Min(afterSeconds * 1000, 60000);
                    int markIndex = written;   // 后面只追加新产生的帧
                    var until = DateTime.UtcNow.AddMilliseconds(waitMs);
                    while (DateTime.UtcNow < until) Thread.Sleep(100);

                    // 再写一段：从上次写到的位置之后继续
                    int more = WriteSliceFrom(dir, markIndex);
                    frames = written + more;
                }

                WriteMeta(dir, sessionId, frames, trigger, beforeSeconds, afterSeconds);

                // ★ 3.2.8：转成 MP4 并删除 JPEG 序列。
                //   用户原话：「保存录像就是mp4，不然一张张图片很难查看」。
                //   实测 276 帧 21.67MB → 0.09MB（静态桌面），真实游戏画面 1~3MB。
                string mp4 = TryEncodeMp4(dir, _fps, out string encodeMsg);
                if (mp4 != null)
                {
                    // 转码成功后删掉图片，只留 MP4 + meta
                    try
                    {
                        foreach (string f in Directory.GetFiles(dir, "frame-*.jpg"))
                            File.Delete(f);
                    }
                    catch { }
                    message = "已保存 " + frames + " 帧 → " + Path.GetFileName(mp4)
                        + "（" + (new FileInfo(mp4).Length / 1024) + " KB）";
                }
                else
                {
                    // 转码失败就保留 JPEG 序列，至少数据没丢
                    WriteEncodeError(dir, encodeMsg);
                    message = "已保存 " + frames + " 帧（MP4 转码失败，保留图片）：" + encodeMsg;
                }
                return dir;
            }
            catch (Exception ex) { message = "保存失败：" + ex.Message; return null; }
        }

        /// <summary>
        /// 把目录里的 frame-*.jpg 编码成 replay.mp4。
        /// ★ 3.2.8：改用系统自带 Media Foundation（Services.Mp4Writer），
        ///   不再依赖 ffmpeg —— 后者单个 exe 就 164~220 MB，会把安装包撑到 170 MB+，
        ///   而 MF H.264 零依赖、零体积，实测压缩 17.6 倍（4823KB → 273KB）。
        /// </summary>
        private static string? TryEncodeMp4(string dir, int fps, out string message)
        {
            string outPath = Path.Combine(dir, "replay.mp4");
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }

            // 1080p 用 6000kbps：文字清晰且体积可控；
            // 小分辨率按比例下调，避免无意义地浪费码率。
            int bitrate = 6000;
            if (_width > 0 && _height > 0)
            {
                int pixels = _width * _height;
                bitrate = (int)Math.Clamp(pixels / 350.0, 1500, 12000);
            }

            if (SoulLink.Services.Mp4Writer.TryEncodeDirectory(dir, outPath, fps, bitrate, out message))
                return outPath;
            return null;
        }

        private static void WriteEncodeError(string dir, string detail)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "encode-error.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine
                    + "MP4 转码失败，已保留 JPEG 序列。" + Environment.NewLine
                    + "原因：" + detail + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>把缓冲最后 N 秒的帧写到目录。</summary>
        private static int WriteSlice(string dir, double seconds, int startIndex)
        {
            List<Frame> copy;
            lock (Gate) copy = new List<Frame>(Ring);
            if (copy.Count == 0) return 0;

            var cutoff = DateTime.Now.AddSeconds(-Math.Max(0, seconds));
            int i = startIndex;
            foreach (var f in copy)
            {
                if (f.At < cutoff) continue;
                File.WriteAllBytes(Path.Combine(dir, "frame-" + i.ToString("D5") + ".jpg"), f.Jpeg);
                i++;
            }
            return i - startIndex;
        }

        /// <summary>把缓冲里"第 skip 帧之后"的新帧追加到目录（用于后 N 秒）。</summary>
        private static int WriteSliceFrom(string dir, int skip)
        {
            List<Frame> copy;
            lock (Gate) copy = new List<Frame>(Ring);
            if (copy.Count <= skip) return 0;

            int i = skip;
            for (int j = skip; j < copy.Count; j++)
            {
                File.WriteAllBytes(Path.Combine(dir, "frame-" + i.ToString("D5") + ".jpg"), copy[j].Jpeg);
                i++;
            }
            return i - skip;
        }

        private static void WriteMeta(string dir, int sessionId, int frames, string trigger,
            double before, double after)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "meta.json"),
                    "{\"sessionId\":" + sessionId
                    + ",\"frames\":" + frames
                    + ",\"fps\":" + _fps
                    + ",\"quality\":" + _quality
                    + ",\"size\":\"" + _width + "x" + _height + "\""
                    + ",\"trigger\":\"" + JsonEscape(trigger) + "\""
                    + ",\"beforeSeconds\":" + before.ToString("0.#")
                    + ",\"afterSeconds\":" + after.ToString("0.#")
                    + ",\"savedAt\":\"" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "\"}");
            }
            catch { }
        }

        private static string JsonEscape(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>Windows 文件名非法字符 → 下划线。</summary>
        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s.Trim())
            {
                if (c < 32 || "\\/:*?\"<>|".IndexOf(c) >= 0) sb.Append('_');
                else sb.Append(c);
            }
            string r = sb.ToString().Trim();
            if (r.Length > 60) r = r.Substring(0, 60);
            return r;
        }

        /// <summary>
        /// 单张截图（直接落盘，不进环形缓冲）。
        /// ★ 目录规范与录像一致：录制回放\&lt;用户名-昵称&gt;\&lt;时间-触发条件&gt;\
        /// </summary>
        public static bool CaptureOnce(string userTag, string trigger, int quality,
            int sessionId, out string path, out string message)
        {
            path = "";
            message = "";
            try
            {
                var jpeg = GrabJpeg(Math.Clamp(quality, 30, 95));
                if (jpeg == null) { message = "抓屏失败"; return false; }

                string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
                string safeTrigger = Sanitize(trigger);
                string folder = stamp + (safeTrigger.Length > 0 ? "-" + safeTrigger : "");

                string dir = Path.Combine(CaptureRoot, "录制回放",
                    string.IsNullOrWhiteSpace(userTag) ? ("会话" + sessionId) : Sanitize(userTag),
                    folder);
                Directory.CreateDirectory(dir);

                string file = Path.Combine(dir, "shot.jpg");
                File.WriteAllBytes(file, jpeg);

                try
                {
                    File.WriteAllText(Path.Combine(dir, "meta.json"),
                        "{\"sessionId\":" + sessionId
                        + ",\"kind\":\"screenshot\""
                        + ",\"quality\":" + quality
                        + ",\"size\":\"" + _width + "x" + _height + "\""
                        + ",\"trigger\":\"" + JsonEscape(trigger) + "\""
                        + ",\"savedAt\":\"" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "\"}");
                }
                catch { }

                path = file;
                return true;
            }
            catch (Exception ex) { message = ex.Message; return false; }
        }
    }
}