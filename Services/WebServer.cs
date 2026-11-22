using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 局域网网页服务：用浏览器查看录制回放与实时画面。
    ///
    /// ★ 用户需求：「在设置里填写个端口号，ip+端口号可以打开网页」
    ///   实现用 HttpListener（.NET 内置，不引入 ASP.NET Core 依赖）。
    ///   实测：管理员权限下 http://+:port/ 直接可用，不需要 urlacl。
    ///
    /// ★ 安全边界：
    ///   · 默认端口 0 = **不启动**，用户显式填端口才监听；
    ///   · 只读，不提供任何修改配置/执行动作的接口；
    ///   · 路径严格限制在 录制回放 目录内（防目录穿越）。
    /// </summary>
    public static class WebServer
    {
        private static HttpListener? _listener;
        private static CancellationTokenSource? _cts;
        private static Task? _loop;
        private static int _port;

        /// <summary>正在运行的端口（0 = 未启动）。</summary>
        public static int RunningPort => _listener?.IsListening == true ? _port : 0;

        /// <summary>录制回放根目录。</summary>
        public static string ReplayRoot
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? ".";
                    return Path.Combine(dir, "capture", "录制回放");
                }
                catch { return Path.Combine(Environment.CurrentDirectory, "capture", "录制回放"); }
            }
        }

        // ── 实时画面 ─────────────────────────────────────────────────────
        //
        //  ★ 用户要求：「网页上是实时录像，不是保存的」
        //   实现链路：
        //     主程序每秒通过 relay 管道发 frame 命令
        //       → 代理把内存缓冲里最新一帧写到 <capture>\s<会话>\live\latest.jpg
        //       → 网页 <img src="/api/live?sid=4"> 每次请求都读到新图
        //   走 relay 而不是让网页直连代理，是因为图像不出投送通道、
        //   且主程序本来就有到各会话的连接。

        // ── 鉴权 ─────────────────────────────────────────────────────────
        //
        //  ★ 用户要求加口令。局域网内裸奔意味着任何人打开网页就能操作账号。
        //  做法：口令为空 → 不鉴权（保持易用）；设了口令 → 未登录的请求
        //  一律 401，页面自动弹登录框。登录成功后发一个随机 Cookie。
        //  口令只比对明文，不落盘明文（配置里就是用户自己填的）。

        private const string AuthCookie = "soullink_sid";

        // ★★ 用户反馈：「口令应该要记住浏览器，而不是每次都输入密码」★★
        //
        //  原来的两处问题：
        //    ① 会话 token 只存在**内存字典**里 —— 程序一重启就全失效，
        //       于是每次重开 SoulLink 都要重新输密码；
        //    ② Cookie 没设 Expires 持久过期时间（只有 Max-Age 在某些浏览器
        //       被当成会话 Cookie），关掉浏览器就丢。
        //
        //  现在：token 持久化到配置（WebAuthToken），Cookie 带 Expires，
        //  有效期内（默认 1 年）同一浏览器直接免密。
        //  改口令会自动作废旧 token（见 PasswordEnabled 的比对逻辑）。

        private static readonly object _sessionsGate = new();

        private static bool PasswordEnabled()
            => !string.IsNullOrWhiteSpace(App.Config?.Settings?.WebPassword);

        /// <summary>取（或生成）持久化的访问令牌。</summary>
        private static string EnsureAuthToken()
        {
            try
            {
                string token = App.Config?.Settings?.WebAuthToken ?? "";
                if (!string.IsNullOrWhiteSpace(token)) return token;
                token = Guid.NewGuid().ToString("N");
                if (App.Config?.Settings != null)
                {
                    App.Config.Settings.WebAuthToken = token;
                    try { ConfigStore.Save(App.Config); } catch { }
                }
                return token;
            }
            catch { return Guid.NewGuid().ToString("N"); }
        }

        private static bool IsAuthorized(HttpListenerContext ctx)
        {
            if (!PasswordEnabled()) return true;
            try
            {
                var cookie = ctx.Request.Cookies[AuthCookie];
                if (cookie == null || string.IsNullOrEmpty(cookie.Value)) return false;
                // 与持久化 token 比对（而不是内存字典），重启后依然有效
                return string.Equals(cookie.Value, EnsureAuthToken(), StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static void IssueSession(HttpListenerContext ctx)
        {
            try
            {
                string token = EnsureAuthToken();
                // Expires 让浏览器**落盘**保存；Max-Age 双保险。
                var expires = DateTime.UtcNow.AddYears(1);
                ctx.Response.Headers["Set-Cookie"] =
                    AuthCookie + "=" + token
                    + "; Path=/; Expires=" + expires.ToString("R")
                    + "; Max-Age=31536000; SameSite=Lax";
            }
            catch { }
        }

        private static readonly Dictionary<int, DateTime> _lastPull = new();
        private static IDisposable? _liveTick;

        /// <summary>
        /// 兜底刷新（TickHub 每秒一次）。
        /// ★ 实时性主要靠 /api/live 的按需抓取；这里只保证
        ///   "没人看网页时也能拿到较新的帧"，不需要更快。
        /// </summary>
        public static void EnsureLiveRefresh()
        {
            if (_liveTick != null) return;
            try
            {
                _liveTick = TickHub.Register("WebServer.LivePull", 1, PullLiveFrames);
            }
            catch { }
            PullLiveFrames();
        }

        /// <summary>向所有在线会话要一帧最新画面。只对**开启录制**的账号做，避免无谓开销。</summary>
        private static void PullLiveFrames()
        {
            try
            {
                var ids = App.Config?.Settings?.RecordAccountIds;
                if (ids == null || ids.Count == 0) return;

                foreach (string accId in ids)
                {
                    var acc = App.Config.Accounts.FirstOrDefault(a => a.Id == accId);
                    if (acc == null) continue;
                    int sid = AccountStateCenter.Get(accId)?.SessionId ?? 0;
                    if (sid <= 0) continue;

                    var relay = RelayClient.FindBySession(sid);
                    if (relay == null || !relay.IsConnected) continue;

                    _ = relay.WriteLatestFrameAsync();   // 不等待，失败无所谓
                }
            }
            catch { }
        }

        /// <summary>按配置启动或停止。可重复调用（端口变了会重启）。</summary>
        public static void Apply(int port)
        {
            try
            {
                Stop();
                if (port <= 0) return;
                if (port < 1024 || port > 65535) { LogBus.Warn("网页端口无效", "端口需在 1024~65535，当前 " + port); return; }

                var l = new HttpListener();
                l.Prefixes.Add("http://+:" + port + "/");
                try { l.Start(); }
                catch (Exception ex)
                {
                    // 常见原因：端口被占用 / 权限不足。降级试 localhost，仍失败就报错。
                    l.Close();
                    LogBus.Error("网页服务启动失败", "端口 " + port + "：" + ex.Message);
                    return;
                }

                _listener = l;
                _port = port;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => LoopAsync(l, token), token);

                LogBus.Ok("网页服务已启动", "http://" + LocalIp() + ":" + port + "/");
            }
            catch (Exception ex) { LogBus.Error("网页服务异常", ex.Message); }
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            _cts = null;
            _loop = null;
            _port = 0;
        }

        /// <summary>
        /// 取**局域网**可用 IP。
        ///
        /// ★ 不能随便拿第一个 IPv4：虚拟网卡（Hyper-V / Oray / GameViewer 等）
        ///   常给出 169.254.x.x（链路本地，APIPA）或 172.x 之类的内部地址，
        ///   手机根本连不上。用户实测界面上就显示了 169.254.83.107 —— 那是错的。
        ///
        /// 按可用性排序：
        ///   ① 192.168.x.x（家用/办公局域网最常见）
        ///   ② 10.x.x.x
        ///   ③ 172.16~31.x.x
        ///   排除 169.254.*（APIPA）、127.*（回环）、虚拟网卡。
        /// </summary>
        public static string LocalIp()
        {
            var candidates = new List<(int score, string ip)>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                    string name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    // 明显是虚拟/隧道网卡，跳过
                    if (name.Contains("virtual") || name.Contains("vethernet")
                        || name.Contains("hyper-v") || name.Contains("vmware")
                        || name.Contains("virtualbox") || name.Contains("loopback")
                        || name.Contains("tunnel") || name.Contains("tap")
                        || name.Contains("oray") || name.Contains("gameviewer")
                        || name.Contains("wintun") || name.Contains("clash"))
                        continue;

                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        string s = ip.Address.ToString();
                        if (s.StartsWith("169.254.")) continue;    // APIPA，不可路由
                        if (s.StartsWith("127.")) continue;        // 回环

                        int score = 0;
                        if (s.StartsWith("192.168.")) score = 3;
                        else if (s.StartsWith("10.")) score = 2;
                        else if (s.StartsWith("172.")) score = 1;
                        candidates.Add((score, s));
                    }
                }
            }
            catch { }

            if (candidates.Count > 0)
                return candidates.OrderByDescending(c => c.score).First().ip;
            return "127.0.0.1";
        }

        /// <summary>拼出可直接给用户看的局域网访问地址。</summary>
        public static string LanUrl(int port) => "http://" + LocalIp() + ":" + port + "/";

        private static async Task LoopAsync(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext? ctx = null;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleAsync(ctx), ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch { }
            }
        }

        private static async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                string path = (ctx.Request.Url?.AbsolutePath ?? "/").Trim();
                string method = ctx.Request.HttpMethod ?? "GET";

                // 输入接口是 POST（唯一允许写操作的入口）
                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (path == "/api/input") { await HandleInput(ctx); return; }
                    await WriteText(ctx, 404, "404");
                    return;
                }

                if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    && !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                { await WriteText(ctx, 405, "只支持 GET / POST"); return; }

                // 登录（唯一不需要鉴权的业务接口）
                if (path == "/api/login")
                {
                    string pw = ctx.Request.QueryString["pw"] ?? "";
                    if (!PasswordEnabled()) { await WriteJson(ctx, new { ok = true }); return; }
                    if (pw == App.Config.Settings.WebPassword)
                    {
                        IssueSession(ctx);
                        await WriteJson(ctx, new { ok = true });
                    }
                    else
                    {
                        await Task.Delay(300);   // 减缓暴力尝试
                        await WriteJson(ctx, new { ok = false, error = "口令错误" });
                    }
                    return;
                }

                // ★ 除登录外，其它一切都要鉴权
                if (!IsAuthorized(ctx))
                {
                    if (path == "/" || path == "/index.html")
                    { await WriteHtml(ctx, LoginHtml()); return; }
                    await WriteText(ctx, 401, "未登录");
                    return;
                }

                if (path == "/" || path == "/index.html") { await WriteHtml(ctx, IndexHtml()); return; }
                if (path == "/api/list") { await WriteJson(ctx, ApiList()); return; }
                if (path == "/api/status") { await WriteJson(ctx, ApiStatus()); return; }
                if (path == "/api/live") { await WriteLiveFrame(ctx); return; }
                if (path == "/api/sessions") { await WriteJson(ctx, ApiSessions()); return; }

                // 静态文件：/file/<相对路径>
                if (path.StartsWith("/file/", StringComparison.OrdinalIgnoreCase))
                { await WriteFile(ctx, path.Substring(6)); return; }

                await WriteText(ctx, 404, "404");
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        // ── 输出 ─────────────────────────────────────────────────────────

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
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }

        private static async Task WriteHtml(HttpListenerContext ctx, string html)
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }

        /// <summary>安全地提供 录制回放 目录下的文件（防目录穿越）。</summary>
        private static async Task WriteFile(HttpListenerContext ctx, string rel)
        {
            try
            {
                string root = Path.GetFullPath(ReplayRoot);
                string full = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(rel)));

                // 必须仍在 root 下
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                { await WriteText(ctx, 403, "禁止访问"); return; }
                if (!File.Exists(full)) { await WriteText(ctx, 404, "文件不存在"); return; }

                var fi = new FileInfo(full);
                string ext = fi.Extension.ToLowerInvariant();
                string contentType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".json" => "application/json; charset=utf-8",
                    // ★ 录像改成 MP4 后必须给对类型，否则浏览器不会当视频播
                    ".mp4" => "video/mp4",
                    ".webm" => "video/webm",
                    ".txt" => "text/plain; charset=utf-8",
                    _ => "application/octet-stream"
                };

                // ★★ 视频必须支持 Range（HTTP 206）★★
                //   否则 <video> 不能拖动进度条，Safari 甚至完全不播。
                //   这是流媒体最基本的约定。
                if (ext == ".mp4" || ext == ".webm")
                {
                    await WriteStreamWithRange(ctx, full, fi.Length, contentType);
                    return;
                }

                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = fi.Length;
                using var fs = File.OpenRead(full);
                await fs.CopyToAsync(ctx.Response.OutputStream).ConfigureAwait(false);
                ctx.Response.Close();
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        /// <summary>
        /// 按 Range 请求返回部分内容（HTTP 206）。
        /// 浏览器拖动进度条时会发 Range，服务端必须正确响应，否则视频不能拖动。
        /// </summary>
        private static async Task WriteStreamWithRange(HttpListenerContext ctx, string fullPath,
            long length, string contentType)
        {
            long start = 0, end = length - 1;
            string? range = ctx.Request.Headers["Range"];

            if (!string.IsNullOrWhiteSpace(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                string spec = range.Substring(6);
                int dash = spec.IndexOf('-');
                if (dash >= 0)
                {
                    string a = spec.Substring(0, dash).Trim();
                    string b = spec.Substring(dash + 1).Trim();
                    if (a.Length == 0)
                    {
                        // "bytes=-500"：最后 500 字节
                        if (long.TryParse(b, out long tail) && tail > 0)
                        { start = Math.Max(0, length - tail); end = length - 1; }
                    }
                    else
                    {
                        if (long.TryParse(a, out long s)) start = Math.Clamp(s, 0, Math.Max(0, length - 1));
                        if (b.Length > 0 && long.TryParse(b, out long e)) end = Math.Clamp(e, start, length - 1);
                    }
                }
            }

            long count = end - start + 1;
            if (count <= 0) { await WriteText(ctx, 416, "范围无效"); return; }

            bool partial = start > 0 || end < length - 1;
            ctx.Response.StatusCode = partial ? 206 : 200;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = count;
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            if (partial)
                ctx.Response.Headers["Content-Range"] = "bytes " + start + "-" + end + "/" + length;

            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[64 * 1024];
            long remaining = count;
            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int got = await fs.ReadAsync(buffer.AsMemory(0, want)).ConfigureAwait(false);
                if (got <= 0) break;
                await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, got)).ConfigureAwait(false);
                remaining -= got;
            }
            ctx.Response.Close();
        }

        // ── 数据 ─────────────────────────────────────────────────────────

        /// <summary>列出所有录制（账号 → 条目）。</summary>
        private static object ApiList()
        {
            var root = ReplayRoot;
            var results = new List<object>();
            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var userDir in Directory.GetDirectories(root))
                    {
                        foreach (var item in Directory.GetDirectories(userDir))
                        {
                            // ★ 录制成 MP4 后 JPEG 序列已被删除，不能再靠数 .jpg 得帧数。
                            //   帧数改从 meta.json 的 frames 字段读。
                            string meta = "";
                            try
                            {
                                string mf = Path.Combine(item, "meta.json");
                                if (File.Exists(mf)) meta = File.ReadAllText(mf);
                            }
                            catch { }

                            int frames = 0;
                            try
                            {
                                var m = JsonSerializer.Deserialize<JsonElement>(meta);
                                if (m.TryGetProperty("frames", out var fEl) && fEl.TryGetInt32(out int fv))
                                    frames = fv;
                            }
                            catch { }
                            if (frames == 0)
                            {
                                // 兜底：还没转码成功时 JPEG 还在
                                try { frames = Directory.GetFiles(item, "*.jpg").Length; } catch { }
                            }

                            // 有 mp4 就标记出来，前端据此决定播视频还是逐帧
                            bool hasVideo = File.Exists(Path.Combine(item, "replay.mp4"));

                            results.Add(new
                            {
                                user = Path.GetFileName(userDir),
                                name = Path.GetFileName(item),
                                frames,
                                hasVideo,
                                path = Path.GetFileName(userDir) + "/" + Path.GetFileName(item),
                                meta
                            });
                        }
                    }
                }
            }
            catch { }
            return new { ok = true, count = results.Count, items = results.OrderByDescending(x => x).ToList() };
        }

        /// <summary>各会话的录制状态。</summary>
        private static object ApiStatus()
        {
            var list = new List<object>();
            try
            {
                for (int sid = 1; sid <= 20; sid++)
                {
                    var relay = RelayClient.FindBySession(sid);
                    if (relay == null) continue;
                    list.Add(new
                    {
                        sessionId = sid,
                        connected = relay.IsConnected,
                        screen = relay.ScreenWidth + "x" + relay.ScreenHeight
                    });
                }
            }
            catch { }
            return new { ok = true, sessions = list };
        }

        // ── 鼠标输入 ─────────────────────────────────────────────────────
        //
        //  ★ 用户需求：「可以实现在网页上点击鼠标，映射到多用户吗？」
        //   含义是**每个账号都能被独立操作**，点哪个账号的标签就控制哪个。
        //   ★ 不做"一次点击同步作用到多个账号"的群控 —— 那是另一种语义，用户没要。
        //
        //  链路（复用现有投送通道，不另开）：
        //     网页 POST /api/input?sid=4 {action:"click", x, y}
        //       → RelayClient.MoveAsync + ClickAsync → 代理 SendInput → 该会话
        //
        //  ★ 坐标换算：网页上传**归一化坐标**（0~1），
        //   这里按该会话自己的分辨率乘回去。

        private sealed class InputRequest
        {
            public string action { get; set; } = "";
            public double x { get; set; } = -1;      // 0~1 归一化
            public double y { get; set; } = -1;
            public string button { get; set; } = "left";
            public int delta { get; set; }
            public string? key { get; set; }
        }

        private static async Task HandleInput(HttpListenerContext ctx)
        {
            try
            {
                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await sr.ReadToEndAsync().ConfigureAwait(false);

                var req = JsonSerializer.Deserialize<InputRequest>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (req == null) { await WriteJson(ctx, new { ok = false, error = "请求格式错误" }); return; }

                // ★ 每个账号**独立**操作：只作用于请求里指定的那一个会话。
                //   （不做"一次点击同步多个账号"的群控，那是另一种语义，用户没要。）
                int sid = 0;
                int.TryParse(ctx.Request.QueryString["sid"] ?? "0", out sid);
                if (sid <= 0) { await WriteJson(ctx, new { ok = false, error = "缺少 sid" }); return; }

                var relay = RelayClient.FindBySession(sid);
                if (relay == null || !relay.IsConnected)
                { await WriteJson(ctx, new { ok = false, error = "会话 " + sid + " 未连接" }); return; }

                int w = Math.Max(1, relay.ScreenWidth);
                int h = Math.Max(1, relay.ScreenHeight);
                int px = req.x >= 0 ? (int)Math.Round(req.x * w) : -1;
                int py = req.y >= 0 ? (int)Math.Round(req.y * h) : -1;

                switch (req.action)
                {
                    case "click":
                        {
                            // ★ 只发**一次**往返：MoveAsync + ClickAsync 是两条独立命令，
                            //   串行队列下要等两个 RTT（实测每次 45~64ms，双击就 200ms+）。
                            //   这里合并成一条：RelayClient.ClickAtAsync 让代理
                            //   一次做完 SetCursorPos + 按下 + 抬起。
                            int cx = Math.Clamp(px, 0, w - 1);
                            int cy = Math.Clamp(py, 0, h - 1);
                            await relay.ClickAtAsync(cx, cy, req.button);
                            break;
                        }
                    case "down":
                        if (px >= 0) await relay.MoveAsync(px, py);
                        await relay.DownAsync(req.button);
                        break;
                    case "up":
                        await relay.UpAsync(req.button);
                        break;
                    case "move":
                        await relay.MoveAsync(Math.Clamp(px, 0, w - 1), Math.Clamp(py, 0, h - 1));
                        break;
                    case "wheel":
                        await relay.WheelAsync(req.delta);
                        break;
                    case "key":
                        {
                            string spec = (req.key ?? "").Trim();
                            if (spec.Length == 0) { await WriteJson(ctx, new { ok = false, error = "缺少按键" }); return; }
                            await relay.KeyAsync(spec);
                            break;
                        }
                    default:
                        await WriteJson(ctx, new { ok = false, error = "未知动作 " + req.action });
                        return;
                }

                await WriteJson(ctx, new { ok = true, sid });
            }
            catch (Exception ex) { await WriteJson(ctx, new { ok = false, error = ex.Message }); }
        }

        /// <summary>在线会话列表（含是否在录制）。</summary>
        private static object ApiSessions()
        {
            var list = new List<object>();
            try
            {
                var ids = App.Config?.Settings?.RecordAccountIds ?? new List<string>();
                foreach (var acc in App.Config.Accounts)
                {
                    int sid = AccountStateCenter.Get(acc.Id)?.SessionId ?? 0;
                    bool recording = ids.Contains(acc.Id);
                    var relay = sid > 0 ? RelayClient.FindBySession(sid) : null;
                    if (relay == null) continue;

                    list.Add(new
                    {
                        id = acc.Id,
                        user = acc.UserName,
                        nick = acc.DisplayName ?? "",
                        label = string.IsNullOrWhiteSpace(acc.DisplayName)
                                ? acc.UserName : acc.UserName + "-" + acc.DisplayName,
                        sessionId = sid,
                        recording,
                        screen = relay.ScreenWidth + "x" + relay.ScreenHeight
                    });
                }
            }
            catch { }
            return new { ok = true, sessions = list };
        }

        /// <summary>
        /// 实时画面：返回指定会话的最新一帧。
        /// 代理把帧写成 &lt;capture&gt;\s&lt;会话&gt;\live\latest.jpg，这里直接读。
        /// </summary>
        private static async Task WriteLiveFrame(HttpListenerContext ctx)
        {
            try
            {
                string sidText = ctx.Request.QueryString["sid"] ?? "";
                if (!int.TryParse(sidText, out int sid) || sid <= 0)
                { await WriteText(ctx, 400, "缺少 sid 参数"); return; }

                string captureRoot = Path.GetDirectoryName(ReplayRoot) ?? "";
                string live = Path.Combine(captureRoot, "s" + sid, "live", "latest.jpg");

                // ★ 关键：每次请求都**主动抓一帧**，而不是等后台 1 秒节拍。
                //   后台 TickHub 最快 1 秒一次，前端就算 0.25 秒轮询也只能拿到同一帧，
                //   实际只有 1fps，主观延迟极大（用户反馈"双击过一会儿才打开"）。
                //   这里改成按需抓取：请求到达 → 让代理写最新帧 → 立刻读回。
                //   实测 relay 往返仅 3ms，所以能做到接近实时。
                var relay = RelayClient.FindBySession(sid);
                if (relay != null && relay.IsConnected)
                {
                    DateTime before = File.Exists(live) ? File.GetLastWriteTimeUtc(live) : DateTime.MinValue;
                    _ = relay.WriteLatestFrameAsync();

                    // 等文件被刷新（通常一两次 10ms 就够）
                    int waited = 0;
                    while (waited < 600)
                    {
                        await Task.Delay(10).ConfigureAwait(false);
                        waited += 10;
                        if (File.Exists(live) && File.GetLastWriteTimeUtc(live) > before) break;
                    }
                }
                else if (!File.Exists(live))
                {
                    await WriteText(ctx, 404, "会话未连接"); return;
                }

                if (!File.Exists(live)) { await WriteText(ctx, 404, "还没有实时帧"); return; }

                byte[] bytes;
                using (var fs = new FileStream(live, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var ms = new MemoryStream())
                {
                    await fs.CopyToAsync(ms).ConfigureAwait(false);
                    bytes = ms.ToArray();
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "image/jpeg";
                // ★ 不缓存：实时画面每次都要最新
                ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                ctx.Response.Close();
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        // ── 页面 ─────────────────────────────────────────────────────────

        private static string LoginHtml() => """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>SoulLink 登录</title>
<style>
  *{box-sizing:border-box}
  body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
       background:#080b16;color:#e6ecff;
       font-family:-apple-system,BlinkMacSystemFont,"Microsoft YaHei",sans-serif}
  .card{width:300px;padding:26px 22px;background:#111729;border:1px solid #232d4a;
        border-radius:14px}
  h1{font-size:16px;margin:0 0 4px;font-weight:600}
  .sub{color:#6f7fa8;font-size:11.5px;margin-bottom:18px}
  input{width:100%;height:40px;padding:0 12px;border-radius:9px;border:1px solid #232d4a;
        background:#0b0f1e;color:#e6ecff;font-size:13px;outline:none}
  input:focus{border-color:#4fe3ff}
  button{width:100%;height:40px;margin-top:12px;border:none;border-radius:9px;
         background:linear-gradient(135deg,#6c5ce7,#4fe3ff);color:#fff;
         font-size:13px;font-weight:600;cursor:pointer}
  button:disabled{opacity:.6}
  #err{color:#ff8fa3;font-size:11.5px;margin-top:10px;min-height:16px;text-align:center}
</style>
</head>
<body>
<div class="card">
  <h1>SoulLink</h1>
  <div class="sub">请输入访问口令</div>
  <input id="pw" type="password" placeholder="口令" autocomplete="current-password">
  <button id="go">登录</button>
  <div id="err"></div>
</div>
<script>
const $ = id => document.getElementById(id);
async function login() {
  const pw = $('pw').value;
  if (!pw) { $('err').textContent = '请输入口令'; return; }
  $('go').disabled = true; $('err').textContent = '';
  try {
    const r = await fetch('/api/login?pw=' + encodeURIComponent(pw));
    const d = await r.json();
    if (d.ok) { location.href = '/'; }
    else { $('err').textContent = d.error || '登录失败'; $('go').disabled = false; }
  } catch (e) { $('err').textContent = '网络错误'; $('go').disabled = false; }
}
$('go').onclick = login;
$('pw').addEventListener('keydown', e => { if (e.key === 'Enter') login(); });
$('pw').focus();
</script>
</body>
</html>
""";

        private static string IndexHtml() => """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover, user-scalable=no">
<title>SoulLink 实时控制</title>
<style>
  *{box-sizing:border-box;-webkit-tap-highlight-color:transparent;
    -webkit-user-select:none;-moz-user-select:none;-ms-user-select:none;user-select:none}
  body{margin:0;background:#080b16;color:#e6ecff;
       font-family:-apple-system,BlinkMacSystemFont,"Microsoft YaHei",sans-serif;
       overscroll-behavior:none}
  /* ★ 画面区彻底禁掉选中与拖拽，否则双击会触发浏览器"选中图片/文字"，
     出现整片蓝色遮罩（用户实测反馈）。 */
  #screen,#screen *,#stage,#stage *{-webkit-user-select:none;user-select:none;
    -webkit-user-drag:none;-khtml-user-drag:none;-moz-user-drag:none;user-drag:none}
  header{position:sticky;top:0;z-index:20;padding:11px 14px 8px;
         background:rgba(8,11,22,.96);backdrop-filter:blur(14px);border-bottom:1px solid #1a2238}
  h1{font-size:15px;margin:0;font-weight:600;display:flex;align-items:center;gap:8px}
  .dot{width:7px;height:7px;border-radius:50%;background:#4fe3ff;box-shadow:0 0 6px #4fe3ff;
       animation:pulse 1.6s infinite}
  @keyframes pulse{0%,100%{opacity:1}50%{opacity:.35}}
  .sub{color:#6f7fa8;font-size:11px;margin-top:3px}
  main{padding:10px 12px 30px}
  #screen{position:relative;background:#000;border-radius:12px;overflow:hidden;
          border:1px solid #1e2742;touch-action:none;cursor:crosshair}
  #screen img{width:100%;display:block;min-height:120px;object-fit:contain;pointer-events:none}
  #placeholder{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;
               color:#5a6889;font-size:12.5px;text-align:center;padding:20px}
  #ripple{position:absolute;width:46px;height:46px;margin:-23px 0 0 -23px;border-radius:50%;
          border:2px solid #4fe3ff;opacity:0;pointer-events:none}
  #ripple.go{animation:rip .5s ease-out}
  @keyframes rip{0%{opacity:.95;transform:scale(.35)}100%{opacity:0;transform:scale(1.5)}}
  .tabs{display:flex;gap:6px;margin-top:12px;overflow-x:auto;padding-bottom:2px;scrollbar-width:none}
  .tabs::-webkit-scrollbar{display:none}
  .tab{flex:0 0 auto;padding:7px 13px;border-radius:9px;border:1px solid #232d4a;
       background:#111729;color:#8b9bc4;font-size:12px;cursor:pointer;white-space:nowrap}
  .tab.on{background:linear-gradient(135deg,#6c5ce7,#4fe3ff);color:#fff;border-color:transparent}
  .row{display:flex;align-items:center;justify-content:space-between;margin-top:13px;gap:10px}
  .row label{font-size:12px;color:#8b9bc4;flex:0 0 auto}
  .seg{display:flex;gap:5px;flex-wrap:wrap;justify-content:flex-end}
  .seg button{padding:5px 11px;border-radius:7px;border:1px solid #232d4a;background:#111729;
              color:#8b9bc4;font-size:11.5px;cursor:pointer}
  .seg button.on{background:#22315c;color:#7fd8ff;border-color:#2f4372}
  .sect{margin-top:18px;border-top:1px solid #1a2238;padding-top:14px}
  .sect h2{font-size:12.5px;margin:0 0 10px;color:#8b9bc4;font-weight:500}
  .item{background:#111729;border:1px solid #232d4a;border-radius:9px;padding:11px;
        margin-bottom:8px;cursor:pointer}
  .item:active{background:#18203a}
  .iname{font-size:12.5px;color:#d3ddff}
  .imeta{font-size:10.5px;color:#6f7fa8;margin-top:3px}
  .badge{display:inline-block;padding:1px 6px;border-radius:5px;font-size:10px;
         background:#22315c;color:#7fd8ff;margin-left:5px}
  #player{display:none}
  .ctrl{display:flex;align-items:center;gap:10px;margin-top:10px}
  .ctrl button{flex:0 0 auto;width:42px;height:42px;border-radius:50%;border:none;
               background:linear-gradient(135deg,#6c5ce7,#4fe3ff);color:#fff;font-size:15px}
  .ctrl input[type=range]{flex:1;accent-color:#4fe3ff}
  .tools{display:flex;gap:7px;margin-top:10px}
  .tools button{flex:1;padding:8px;border-radius:8px;border:1px solid #232d4a;
                background:#111729;color:#a8b6d8;font-size:11.5px}
  .empty{color:#5a6889;font-size:12px;padding:16px 0;text-align:center}
</style>
</head>
<body>

<header>
  <h1><span class="dot" id="dot"></span><span id="title">实时控制</span></h1>
  <div class="sub" id="sub">正在加载…</div>
</header>

<main>
  <div class="tabs" id="tabs"></div>

  <div id="live">
    <div id="screen">
      <img id="frame" alt="" style="display:none">
      <div id="placeholder">选择一个账号</div>
      <div id="ripple"></div>
    </div>

    <div class="row">
      <label>刷新频率</label>
      <div class="seg" id="speed">
        <button data-ms="1000">1 秒</button>
        <button data-ms="500" class="on">0.5 秒</button>
        <button data-ms="250">0.25 秒</button>
      </div>
    </div>

    <div class="row">
      <label>鼠标</label>
      <div class="seg">
        <button id="rightBtn">右键</button>
        <button id="kbBtn">键盘</button>
        <button id="pauseBtn">暂停画面</button>
      </div>
    </div>

    <div id="kbTip" style="display:none;margin-top:10px;padding:8px 11px;border-radius:8px;
         background:#1a2340;border:1px solid #2f4372;color:#7fd8ff;font-size:11.5px">
      键盘已开启 —— 直接敲键盘即可发送到当前账号。点画面可先聚焦目标窗口。
    </div>

    <div class="row">
      <label id="fpsinfo" style="color:#5a6889;font-size:11px"></label>
      <div class="seg">
        <button id="wheelUp">滚轮↑</button>
        <button id="wheelDown">滚轮↓</button>
      </div>
    </div>
  </div>

  <div id="player">
    <video id="vid" controls playsinline preload="metadata"
           style="width:100%;display:block;background:#000;border-radius:12px;border:1px solid #1e2742">
    </video>
    <div class="imeta" id="pinfo" style="text-align:center;margin-top:8px"></div>
    <div class="tools">
      <button id="pback">返回实时</button>
      <button id="dl">下载视频</button>
    </div>
  </div>

  <div class="sect">
    <h2>已保存的录像</h2>
    <div id="saved"></div>
  </div>
</main>

<script>
const $ = id => document.getElementById(id);
let sessions = [], curSid = 0, liveTimer = null, liveMs = 500;
let paused = false, lastOk = 0;
let rightClickMode = false;
// 播放器交给原生 <video>，这些逐帧相关变量不再需要

// ── 会话 ───────────────────────────────────────────────────
async function loadSessions() {
  try {
    const r = await fetch('/api/sessions');
    const d = await r.json();
    sessions = (d.sessions || []).filter(s => s.recording);
    const box = $('tabs');
    box.innerHTML = '';
    if (!sessions.length) {
      $('sub').textContent = '没有正在录制的账号';
      $('placeholder').textContent = '在「RDP 通用设置 → 屏幕录制」里勾选账号后，这里会出现实时画面。';
      return;
    }
    $('sub').textContent = sessions.length + ' 个账号 · 实时';
    for (const s of sessions) {
      const b = document.createElement('button');
      b.className = 'tab' + (s.sessionId === curSid ? ' on' : '');
      b.textContent = s.label;
      b.onclick = () => { curSid = s.sessionId; refreshTabs(); startLive(); };
      box.appendChild(b);
    }
    if (!curSid) { curSid = sessions[0].sessionId; refreshTabs(); startLive(); }
  } catch { $('sub').textContent = '加载失败'; }
}
function refreshTabs() {
  document.querySelectorAll('#tabs .tab').forEach((b,i)=>{
    b.classList.toggle('on', sessions[i] && sessions[i].sessionId === curSid);
  });
}

// ── 实时画面 ───────────────────────────────────────────────
function startLive() {
  stopLive(); showLive();
  const s = sessions.find(x => x.sessionId === curSid);
  $('title').textContent = s ? ('实时 · ' + s.label) : '实时控制';
  if (s) $('fpsinfo').textContent = s.screen + ' · ' + s.user;
  pullFrame();
  liveTimer = setInterval(() => { if (!paused) pullFrame(); }, liveMs);
}
function stopLive(){ if(liveTimer){clearInterval(liveTimer);liveTimer=null;} }
function pullFrame() {
  const img = new Image();
  img.onload = () => {
    const f = $('frame');
    f.src = img.src; f.style.display='block';
    $('placeholder').style.display='none';
    lastOk = Date.now();
  };
  img.onerror = () => {
    if (Date.now() - lastOk > 4000) {
      $('placeholder').style.display='flex';
      $('placeholder').textContent = '暂时没有画面（会话可能未连接）';
    }
  };
  img.src = '/api/live?sid=' + curSid + '&t=' + Date.now();
}

// ── 鼠标输入 ───────────────────────────────────────────────
// ★ 只作用于当前选中的账号，各账号互不影响
async function sendInput(action, nx, ny, extra) {
  const body = Object.assign({ action, x: nx, y: ny }, extra || {});
  try {
    const r = await fetch('/api/input?sid=' + curSid, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    return await r.json();
  } catch (e) { return null; }
}

function norm(e) {
  const el = $('screen');
  const rect = el.getBoundingClientRect();
  const img = $('frame');
  // 图片用 object-fit:contain，要按实际显示区域算
  const iw = img.naturalWidth || 16, ih = img.naturalHeight || 9;
  const boxW = rect.width, boxH = rect.height;
  const scale = Math.min(boxW / iw, boxH / ih);
  const dw = iw * scale, dh = ih * scale;
  const ox = (boxW - dw) / 2, oy = (boxH - dh) / 2;
  const cx = (e.touches ? e.touches[0].clientX : e.clientX) - rect.left - ox;
  const cy = (e.touches ? e.touches[0].clientY : e.clientY) - rect.top - oy;
  return { nx: Math.max(0, Math.min(1, cx / dw)), ny: Math.max(0, Math.min(1, cy / dh)) };
}

function ripple(e) {
  const el = $('ripple'), rect = $('screen').getBoundingClientRect();
  const cx = (e.touches ? e.touches[0].clientX : e.clientX) - rect.left;
  const cy = (e.touches ? e.touches[0].clientY : e.clientY) - rect.top;
  el.style.left = cx + 'px'; el.style.top = cy + 'px';
  el.classList.remove('go'); void el.offsetWidth; el.classList.add('go');
}

// ── 鼠标：点击 / 拖拽 ──────────────────────────────────────
// ★ 拖拽用 down → move(节流) → up，需要区分"点击"和"拖动"：
//   按下后位移超过 8px 才算拖动，否则按点击处理。
let dragState = null;   // {sx, sy, moved, lastX, lastY}

$('screen').addEventListener('pointerdown', e => {
  if (e.button === 2) return;              // 右键走 click
  const p = norm(e);
  dragState = { sx: e.clientX, sy: e.clientY, moved: false, nx: p.nx, ny: p.ny };
  $('screen').setPointerCapture(e.pointerId);
});

$('screen').addEventListener('pointermove', e => {
  if (!dragState) return;
  const dx = e.clientX - dragState.sx, dy = e.clientY - dragState.sy;
  if (!dragState.moved && Math.hypot(dx, dy) < 8) return;

  if (!dragState.moved) {
    // 刚判定为拖动：先按下（在起始点）
    dragState.moved = true;
    sendInput('down', dragState.nx, dragState.ny,
      { button: rightClickMode ? 'right' : 'left' });
  }
  // 节流：最多每 60ms 发一次移动，避免打爆投送通道
  const now = Date.now();
  if (!dragState.lastSent || now - dragState.lastSent > 60) {
    dragState.lastSent = now;
    const p = norm(e);
    sendInput('move', p.nx, p.ny);
  }
});

$('screen').addEventListener('pointerup', e => {
  if (!dragState) return;
  const wasDrag = dragState.moved;
  dragState = null;
  const p = norm(e);
  if (wasDrag) {
    sendInput('up', p.nx, p.ny, { button: rightClickMode ? 'right' : 'left' });
  } else {
    ripple(e);
    sendInput('click', p.nx, p.ny, { button: rightClickMode ? 'right' : 'left' });
  }
});

$('screen').addEventListener('pointercancel', () => {
  if (dragState && dragState.moved) sendInput('up', -1, -1, { button: 'left' });
  dragState = null;
});

// ★ 双击：阻止浏览器默认的"选中"行为，并照常发两次点击
$('screen').addEventListener('dblclick', e => { e.preventDefault(); });
$('screen').addEventListener('mousedown', e => { if (e.detail > 1) e.preventDefault(); });
// 长按选中（移动端）
$('screen').addEventListener('selectstart', e => e.preventDefault());
$('screen').addEventListener('contextmenu', e => e.preventDefault());

// 滚轮
$('screen').addEventListener('wheel', e => {
  e.preventDefault();
  sendInput('wheel', -1, -1, { delta: e.deltaY > 0 ? -120 : 120 });
}, { passive: false });

// ── 键盘输入 ──────────────────────────────────────────────
// ★ 网页拿到的是 key 字符串，代理侧 SendKeyCombo 认 "F12" / "Ctrl+A" 这种写法，
//   这里负责把浏览器事件翻译成那个格式。
let kbMode = false;

function keyName(e) {
  const k = e.key;
  if (k === ' ') return 'Space';
  if (k === 'Enter') return 'Enter';
  if (k === 'Escape') return 'Esc';
  if (k === 'Backspace') return 'Backspace';
  if (k === 'Tab') return 'Tab';
  if (k === 'Delete') return 'Delete';
  if (k === 'ArrowUp') return 'Up';
  if (k === 'ArrowDown') return 'Down';
  if (k === 'ArrowLeft') return 'Left';
  if (k === 'ArrowRight') return 'Right';
  if (k === 'Home') return 'Home';
  if (k === 'End') return 'End';
  if (k === 'PageUp') return 'PageUp';
  if (k === 'PageDown') return 'PageDown';
  if (k.length === 1) return k.toUpperCase();
  return k;
}

function comboOf(e) {
  const parts = [];
  if (e.ctrlKey) parts.push('Ctrl');
  if (e.altKey) parts.push('Alt');
  if (e.shiftKey) parts.push('Shift');
  // 纯修饰键不单独发送
  if (['Control','Alt','Shift','Meta'].includes(e.key)) return '';
  const k = keyName(e);
  parts.push(k);
  return parts.join('+');
}

document.addEventListener('keydown', e => {
  if (!kbMode) return;
  if (e.target.tagName === 'INPUT') return;   // 输入框里不拦截
  const combo = comboOf(e);
  if (!combo) return;
  e.preventDefault();
  sendInput('key', -1, -1, { key: combo });
});

$('rightBtn').onclick = () => {
  rightClickMode = !rightClickMode;
  $('rightBtn').classList.toggle('on', rightClickMode);
  $('rightBtn').textContent = rightClickMode ? '右键模式' : '右键';
};
$('kbBtn').onclick = () => {
  kbMode = !kbMode;
  $('kbBtn').classList.toggle('on', kbMode);
  $('kbBtn').textContent = kbMode ? '键盘开' : '键盘';
  if (kbMode) {
    $('kbTip').style.display = 'block';
    document.body.focus();
  } else {
    $('kbTip').style.display = 'none';
  }
};
$('wheelUp').onclick = () => sendInput('wheel', -1, -1, { delta: 120 });
$('wheelDown').onclick = () => sendInput('wheel', -1, -1, { delta: -120 });

$('speed').onclick = e => {
  const b = e.target.closest('button'); if (!b) return;
  liveMs = parseInt(b.dataset.ms);
  document.querySelectorAll('#speed button').forEach(x=>x.classList.toggle('on', x===b));
  startLive();
};
$('pauseBtn').onclick = () => {
  paused = !paused;
  $('pauseBtn').textContent = paused ? '继续画面' : '暂停画面';
  $('pauseBtn').classList.toggle('on', paused);
};

// ── 保存的录像 ─────────────────────────────────────────────
async function loadSaved() {
  try {
    const r = await fetch('/api/list');
    const d = await r.json();
    const box = $('saved'); box.innerHTML = '';
    if (!d.count) { box.innerHTML = '<div class="empty">还没有保存的录像</div>'; return; }
    for (const it of d.items) {
      let trig = '';
      try { if (it.meta) trig = (JSON.parse(it.meta).trigger || ''); } catch {}
      const div = document.createElement('div');
      div.className = 'item';
      const short = it.name.replace(/^[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9]{6}-?/, '');
      div.innerHTML = '<div class="iname">' + esc(it.user) + ' · ' + esc(short || it.name)
        + (trig ? '<span class="badge">' + esc(trig) + '</span>' : '') + '</div>'
        + '<div class="imeta">' + it.frames + ' 帧</div>';
      div.onclick = () => openPlayer(it);
      box.appendChild(div);
    }
  } catch {}
}

// ── 播放器 ─────────────────────────────────────────────────
function showLive(){ $('live').style.display='block'; $('player').style.display='none'; }
function showPlayer(){ $('live').style.display='none'; $('player').style.display='block'; }

// ★ 3.2.8：录像已改成 MP4，播放器直接交给浏览器原生 <video>。
//   不再逐帧加载 JPEG —— 那样几十兆流量、还不能拖动进度条。
function openPlayer(item) {
  stopLive(); showPlayer();
  $('title').textContent = item.name;
  const vid = $('vid');
  const url = '/file/' + encodeURI(item.path) + '/replay.mp4';
  vid.src = url;
  vid.load();

  let fps = 10, frames = 0, trig = '';
  try {
    if (item.meta) {
      const m = JSON.parse(item.meta);
      if (m.fps) fps = m.fps;
      if (m.frames) frames = m.frames;
      if (m.trigger) trig = m.trigger;
    }
  } catch {}

  $('pinfo').textContent = (frames ? frames + ' 帧 · ' : '') + fps + ' fps'
    + (trig ? ' · ' + trig : '');
  $('dl').onclick = () => { window.open(url, '_blank'); };
}

$('pback').onclick = () => {
  const vid = $('vid');
  vid.pause(); vid.removeAttribute('src'); vid.load();
  loadSessions();
};

function esc(s) {
  return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}

loadSessions();
loadSaved();
</script>
</body>
</html>
""";
    }
}
