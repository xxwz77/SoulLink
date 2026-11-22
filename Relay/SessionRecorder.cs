using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        // ── 抓屏后端（第一步 = BitBlt，第二步 = WGC）──────────────────────────
        //
        // ★ 抓帧的 Win32 细节已搬到 IScreenCapture 实现（见 BitBltCapture.cs / WgcCapture.cs）。
        //   SessionRecorder 只负责环形缓冲 / 录制 / 直播调度，不碰抓屏细节。
        //
        // ★ 后端选择（CaptureMode）：
        //   0 = BitBlt（默认）；1 = WGC（版本化 TFM 才有实现）。
        //   代理进程读环境变量 SOULLINK_CAPTURE_MODE（主程序投送时下发，与 SOULLINK_REC_FPS 同模式）。
        //   WGC 初始化失败 → 回退 BitBlt，保证画面永不消失。
        private static IScreenCapture Capture = CreateCapture();

        private static IScreenCapture CreateCapture()
        {
            int mode = 0;
            string raw = Environment.GetEnvironmentVariable("SOULLINK_CAPTURE_MODE") ?? "";
            if (int.TryParse(raw, out int parsed)) mode = parsed;

            if (mode == 1)
            {
#if WGC
                try
                {
                    var wgc = new WgcCapture();
                    // 首次抓帧探活：成功才用 WGC，失败回退 BitBlt
                    if (wgc.CaptureJpeg(75, out _, out _) != null) return wgc;
                }
                catch { }
#endif
            }
            return new BitBltCapture();
        }

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
                // ★ 2026-11-22：把「实际在用的抓屏后端」报上去。
                //   用户诉求：「界面显示当前实际用的哪个」，因为配置选了 WGC 也可能
                //   因显存不足/身份不对而静默降级到 BitBlt，光看配置看不出来。
                string backend = "unknown";
                try { backend = Capture?.Name ?? "unknown"; } catch { }

                return _status
                    + "|fps=" + _fps
                    + "|quality=" + _quality
                    + "|frames=" + Ring.Count
                    + "|bytes=" + bytes
                    + "|total=" + _totalFrames
                    + "|size=" + _width + "x" + _height
                    + "|session=" + _sessionId
                    + "|capture=" + backend;
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

                    var jpeg = Capture.CaptureJpeg(quality, out int cw, out int ch);
                    if (jpeg != null)
                    {
                        if (cw > 0 && ch > 0) { _width = cw; _height = ch; }
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
                var jpeg = Capture.CaptureJpeg(Math.Clamp(quality, 30, 95), out int cw, out int ch);
                if (jpeg == null) { message = "抓屏失败"; return false; }
                if (cw > 0 && ch > 0) { _width = cw; _height = ch; }

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