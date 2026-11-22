using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Relay
{
    /// <summary>
    /// 代理侧网页服务：网页**直连代理**，不经过主程序。
    ///
    /// ★★ 用户要求（2026-11-22）★★
    ///   「网页端尽量隔离主客户端」
    ///   「模拟按键也有独立出来」
    ///
    ///   只服务**代理独有**的两样东西：
    ///     GET  /live        最新一帧 JPEG（数据只存在于代理内存里，主程序拿不到）
    ///     POST /api/input   键鼠注入（必须在目标会话内执行）
    ///     GET  /api/info    身份回显，便于排查
    ///
    /// ★★ 为什么不提供 /file/（历史录像）★★
    ///
    ///   历史录像已经在**主程序目录**的硬盘上：
    ///     &lt;程序目录&gt;\capture\录制回放\...\replay.mp4
    ///   代理跟这个目录只有"写"的关系（录制时存进去），没有任何读的职责。
    ///   代理和主程序在同一台机器、读同一块硬盘，让代理去服务这些文件
    ///   是**纯多余的一跳** —— 不减负，只增加一个提权进程上的攻击面。
    ///   所以历史回放由主程序自己的 /file/ 直接读盘（见 WebServer.WriteFile）。
    ///
    ///   判断依据很简单：数据是不是只有代理能拿到？
    ///     · 实时帧 → 只有代理内存里有 → 必须代理发
    ///     · 磁盘文件 → 主程序直接能读 → 不需要代理
    ///
    /// ★ 端口分配见 Services/WebDirect.cs：一个账号一个端口，按配置顺序。
    ///
    /// ★ 安全边界（重要）：
    ///   · 只放行 WebDirect.IsAllowedPath() 白名单里的路径，代理不是通用文件服务器；
    ///   · 口令：主程序通过 SOULLINK_WEB_TOKEN 环境变量下发，
    ///     网页每次请求带 X-SoulLink-Token 头。空 = 不校验（用户没设口令时）。
    /// </summary>
    internal static class AgentWebServer
    {
        private static HttpListener? _listener;
        private static CancellationTokenSource? _cts;
        private static int _port;

        /// <summary>正在监听的端口（0 = 未启动）。</summary>
        internal static int Port => _listener?.IsListening == true ? _port : 0;

        /// <summary>共享口令。空串 = 不校验。</summary>
        private static string _token = "";

        /// <summary>本代理所属账号的 Id（用于 /api/info 回显，便于排查）。</summary>
        private static string _accountId = "";

        /// <summary>本代理的会话号（/api/info 用）。</summary>
        private static int _sessionId;

        /// <summary>
        /// 启动。端口被占用时**不自动顺延** —— 端口是按账号算出来的固定值，
        /// 顺延会让网页拿到的映射表与实际不符。占用了就报错，由日志暴露问题。
        /// </summary>
        internal static bool Start(int port, string token, string accountId, Action<string> log)
        {
            if (port <= 0) return false;
            if (_listener != null && Port == port) return true;

            try
            {
                _token = token ?? "";
                _accountId = accountId ?? "";
                _sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;

                var l = new HttpListener();
                // 代理是提权进程，绑任意前缀不需要 urlacl（实测）。
                l.Prefixes.Add("http://+:" + port + "/");
                l.Start();

                _listener = l;
                _port = port;
                _cts = new CancellationTokenSource();

                _ = Task.Run(() => LoopAsync(l, _cts.Token));
                log("网页直连已启动 port=" + port + " account=" + _accountId + " session=" + _sessionId);
                return true;
            }
            catch (Exception ex)
            {
                log("网页直连启动失败 port=" + port + "：" + ex.Message);
                try { _listener?.Close(); } catch { }
                _listener = null;
                _port = 0;
                return false;
            }
        }

        internal static void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            _port = 0;
        }

        private static async Task LoopAsync(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext? ctx = null;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { continue; }

                // 每条请求独立处理，不阻塞 accept 循环
                _ = Task.Run(() => HandleSafe(ctx));
            }
        }

        private static async Task HandleSafe(HttpListenerContext ctx)
        {
            try { await HandleAsync(ctx).ConfigureAwait(false); }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        private static async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            string path = req.Url?.AbsolutePath ?? "/";

            // ── CORS：网页跑在主程序的端口上，访问代理是跨源 ──
            //   <img> 本身不受限，但 fetch/XHR 调用 /api/input 必须放行。
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = WebDirectTokenHeader + ", Content-Type";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";

            if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            // ── 口令 ──
            if (!CheckToken(ctx))
            {
                await WriteText(ctx, 401, "需要口令").ConfigureAwait(false);
                return;
            }

            // ── 路径白名单 ──
            if (!Services.WebDirect.IsAllowedPath(path))
            {
                await WriteText(ctx, 404, "404").ConfigureAwait(false);
                return;
            }

            if (path == "/api/info")
            {
                await WriteJson(ctx, new
                {
                    ok = true,
                    account = _accountId,
                    session = _sessionId,
                    pid = Environment.ProcessId,
                    port = _port
                }).ConfigureAwait(false);
                return;
            }

            if (path == "/live")
            {
                await WriteLiveAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (path == "/stream")
            {
                await WriteStreamAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (path == "/api/input" && req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleInputAsync(ctx).ConfigureAwait(false);
                return;
            }

            await WriteText(ctx, 404, "404").ConfigureAwait(false);
        }

        private const string WebDirectTokenHeader = "X-SoulLink-Token";

        /// <summary>
        /// 校验口令。
        ///
        /// ★★ 为什么要支持查询串：★★
        ///   <c>&lt;img src&gt;</c> / <c>&lt;video src&gt;</c> **无法携带自定义请求头**，
        ///   浏览器只会发最普通的 GET。只认 X-SoulLink-Token 的话，
        ///   网页上的实时画面会一直 401（实测踩到：页面有框无图）。
        ///
        ///   所以两种都接受：
        ///     · fetch() 调用 → 走请求头（不会把口令写进 URL/日志）
        ///     · img/video  → 走 ?token= 查询串
        ///
        ///   查询串会把口令留在浏览器历史/代理日志里，属于已知取舍；
        ///   局域网内部使用可接受，真要更严就得上一次性票据。
        /// </summary>
        private static bool CheckToken(HttpListenerContext ctx)
        {
            if (string.IsNullOrEmpty(_token)) return true;
            try
            {
                string got = ctx.Request.Headers[WebDirectTokenHeader] ?? "";
                if (string.Equals(got, _token, StringComparison.Ordinal)) return true;

                // 退路：查询串（给 img/video 用）
                string q = ctx.Request.QueryString["token"] ?? "";
                return string.Equals(q, _token, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>
        /// 实时画面：**直接从内存环形缓冲取**，不写盘、不等文件。
        ///
        /// ★ 这是直连最大的收益：原来要"代理写盘 → 主程序轮询等文件变化
        ///   （最多 600ms）→ 主程序读回"，现在一次调用就返回。
        /// </summary>
        private static async Task WriteLiveAsync(HttpListenerContext ctx)
        {
            byte[]? jpeg = null;
            try { jpeg = SessionRecorder.GetLatestJpeg(); } catch { }

            if (jpeg == null || jpeg.Length == 0)
            {
                await WriteText(ctx, 404, "缓冲里没有帧").ConfigureAwait(false);
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            ctx.Response.ContentLength64 = jpeg.Length;
            await ctx.Response.OutputStream.WriteAsync(jpeg).ConfigureAwait(false);
            ctx.Response.Close();
        }

        /// <summary>
        /// H.264 直播推流（第1步，单客户端）：chunked 输出 Annex-B NAL。
        ///
        /// ★ 数据流：SessionRecorder 环形缓冲最新 JPEG → 解码 BGRA → NV12
        ///   → MfFrameEncoder（IMFTransform）→ 长度前缀 NAL → Annex-B → chunked 写出。
        ///
        /// ★ 第1步从现有 JPEG 帧起步（不新开抓屏后端），保证录像/直播共用同一数据源；
        ///   后续再让 SessionRecorder 直接产 NV12（省一次 JPEG 编解码）。
        ///
        /// ★ 复用 CheckToken（在 HandleAsync 统一校验），/stream 已在 WebDirect 白名单。
        /// </summary>
        private static async Task WriteStreamAsync(HttpListenerContext ctx)
        {
            // 先等一帧
            byte[]? jpeg = null;
            int tries = 0;
            while (jpeg == null && tries < 50)
            {
                try { jpeg = SessionRecorder.GetLatestJpeg(); } catch { }
                if (jpeg == null) { await Task.Delay(20).ConfigureAwait(false); tries++; }
            }
            if (jpeg == null || jpeg.Length == 0)
            {
                await WriteText(ctx, 404, "缓冲里没有帧").ConfigureAwait(false);
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "video/h264";
            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache";
            ctx.Response.SendChunked = true;
            ctx.Response.KeepAlive = true;

            var enc = new SoulLink.Services.MfFrameEncoder();
            try
            {
                // 首帧解出尺寸
                int w = 0, h = 0;
                using (var ms = new MemoryStream(jpeg))
                using (var bmp = new System.Drawing.Bitmap(ms))
                {
                    w = bmp.Width; h = bmp.Height;
                }
                if (w <= 0 || h <= 0) { await WriteText(ctx, 404, "无法解析帧").ConfigureAwait(false); return; }

                if (!enc.TryInitialize(w, h, 15, 8000))
                {
                    await WriteText(ctx, 500, "编码器初始化失败").ConfigureAwait(false);
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var stream = ctx.Response.OutputStream;
                int frames = 0;

                // 推流循环：客户端断开或 30 秒后停止（第1步单客户端演示）。
                while (frames < 450 && sw.ElapsedMilliseconds < 30000)
                {
                    byte[]? cur = null;
                    try { cur = SessionRecorder.GetLatestJpeg(); } catch { }
                    if (cur == null || cur.Length == 0) { await Task.Delay(20).ConfigureAwait(false); continue; }

                    byte[]? nv12;
                    try { nv12 = DecodeJpegToNv12(cur); } catch { nv12 = null; }
                    if (nv12 == null) { await Task.Delay(20).ConfigureAwait(false); continue; }

                    byte[]? nal = null;
                    try { nal = enc.EncodeNv12(nv12, frames * 1000000L / 15); } catch { }
                    if (nal != null && nal.Length > 0)
                    {
                        // ★ MF 软 H.264 MFT 输出**已经是 Annex-B**（00 00 00 01 起始码），
                        //   直接写出即可，不需要再转换（实测首字节 00 00 00 01 09）。
                        await stream.WriteAsync(nal).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                        frames++;
                    }
                    await Task.Delay(66).ConfigureAwait(false);  // 约 15fps
                }
            }
            catch (Exception)
            {
                // 客户端断开等异常，静默结束流。
            }
            finally
            {
                enc.Dispose();
                try { ctx.Response.Close(); } catch { }
            }
        }

        /// <summary>JPEG → NV12（复用 MfFrameEncoder 所需输入）。</summary>
        private static byte[]? DecodeJpegToNv12(byte[] jpeg)
        {
            using var ms = new MemoryStream(jpeg);
            using var bmp = new System.Drawing.Bitmap(ms);
            int w = bmp.Width, h = bmp.Height;
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var bgra = new byte[Math.Abs(bd.Stride) * h];
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, bgra, 0, bgra.Length);
                return BgraToNv12(bgra, w, h, Math.Abs(bd.Stride));
            }
            finally { bmp.UnlockBits(bd); }
        }

        private static byte[] BgraToNv12(byte[] bgra, int w, int h, int stride)
        {
            int ySize = w * h;
            var nv12 = new byte[w * h * 3 / 2];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int yRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                    nv12[yRow + x] = (byte)((66 * r + 129 * g + 25 * b + 128) >> 8);
                }
            }
            for (int i = ySize; i < nv12.Length; i++) nv12[i] = 128;
            return nv12;
        }


        /// <summary>
        /// 键鼠注入：**在本会话内执行**，完全不经主程序。
        ///
        /// ★ 用户要求：「模拟按键也有独立出来」
        ///
        ///   直接复用代理既有的指令分发（RelayAgent.Execute），语义与管道通道完全一致，
        ///   所以网页点击/按键与桌面控制台的行为不会出现两套实现漂移。
        ///
        ///   请求体：{ "action": "click|down|up|move|wheel|key|keydown|keyup|moverel|text",
        ///              "x":0.5, "y":0.5, "button":"left", "delta":120,
        ///              "key":"Ctrl+Shift+A", "text":"你好", "dx":10, "dy":-5, "count":1 }
        ///   坐标是 **0~1 的相对值**，由代理按本会话实际分辨率换算。
        /// </summary>
        private static async Task HandleInputAsync(HttpListenerContext ctx)
        {
            try
            {
                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await sr.ReadToEndAsync().ConfigureAwait(false);

                var dto = System.Text.Json.JsonSerializer.Deserialize<InputDto>(body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (dto == null) { await WriteJson(ctx, new { ok = false, error = "请求格式错误" }).ConfigureAwait(false); return; }

                int w = Math.Max(1, GetSystemMetrics(0));
                int h = Math.Max(1, GetSystemMetrics(1));
                int px = dto.x >= 0 ? (int)Math.Round(dto.x * w) : -1;
                int py = dto.y >= 0 ? (int)Math.Round(dto.y * h) : -1;

                // 坐标 → 绝对位置；按键/滚轮不需要坐标
                string cmd;
                switch ((dto.action ?? "").ToLowerInvariant())
                {
                    case "click":   cmd = "click";   break;
                    case "down":    cmd = "down";    break;
                    case "up":      cmd = "up";      break;
                    case "move":    cmd = "move";    break;
                    case "wheel":   cmd = "wheel";   break;
                    case "key":     cmd = "key";     break;
                    case "keydown": cmd = "keydown"; break;
                    case "keyup":   cmd = "keyup";   break;
                    case "moverel": cmd = "moverel"; break;
                    case "text":    cmd = "text";    break;
                    default:
                        await WriteJson(ctx, new { ok = false, error = "未知动作 " + dto.action }).ConfigureAwait(false);
                        return;
                }

                // key / keydown / keyup 复用 key 字段；text 走 text 字段；
                // click / down / up 走 button 字段（与老网页语义一致）。
                string keyName = cmd == "text" ? (dto.text ?? "") : (dto.key ?? "");
                var req = new RelayAgent.Request
                {
                    Cmd = cmd,
                    X = Math.Clamp(px, 0, w - 1),
                    Y = Math.Clamp(py, 0, h - 1),
                    Move = (cmd == "click" && px >= 0) ? 1 : 0,
                    Button = (cmd == "key" || cmd == "keydown" || cmd == "keyup" || cmd == "text")
                        ? keyName
                        : (dto.button ?? "left"),
                    Dx = dto.dx,
                    Dy = dto.dy,
                    Delta = dto.delta,
                    Count = dto.count
                };

                var resp = RelayAgent.Execute(req);
                await WriteJson(ctx, new { ok = resp.Ok, error = resp.Ok ? null : resp.Message }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteJson(ctx, new { ok = false, error = ex.Message }).ConfigureAwait(false);
            }
        }

        /// <summary>网页传来的输入请求（坐标为相对值）。</summary>
        private sealed class InputDto
        {
            public string? action { get; set; }
            public double x { get; set; } = -1;
            public double y { get; set; } = -1;
            public int dx { get; set; }
            public int dy { get; set; }
            public string? button { get; set; }
            public string? key { get; set; }
            public string? text { get; set; }
            public int delta { get; set; }
            public int count { get; set; } = 1;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private static async Task WriteText(HttpListenerContext ctx, int code, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }

        private static async Task WriteJson(HttpListenerContext ctx, object data)
        {
            var bytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(data));
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }
    }
}
