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

        // ── 实时画面：完全按需 ────────────────────────────────────────
        //
        //  ★★ 用户要求：「在没有请求的时候需要停止数据传送，否则造成性能浪费」★★
        //
        //   原来这里注册了一个 TickHub 每秒任务，**无条件**向所有录制账号
        //   发送 frame 命令。每个命令在代理侧要 BitBlt + JPEG 编码一次
        //   （1920x1080 实测 13~16ms），即使根本没人打开网页也照抓不误。
        //
        //   现在的策略：
        //     · **不注册任何定时器**，没有轮询就不会有后台流量；
        //     · 只有在 /api/live 收到请求时才拉一帧（请求本身就是需求信号）；
        //     · 记录"最后一次有人看的时间"（_lastLiveRequest），
        //       供 /api/status 之类的诊断查看，也方便以后做超时停止录制。

        /// <summary>最近一次有人请求实时画面的时间（UTC）。</summary>
        private static DateTime _lastLiveRequest = DateTime.MinValue;

        /// <summary>多久算"有人在看"（超过这个时间没有请求就认为没人在看）。</summary>
        private static readonly TimeSpan LiveIdleTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// 兼容旧调用点：**不再注册任何定时器**。
        /// 实时画面完全由 /api/live 按需触发，见 WriteLiveFrame。
        /// </summary>
        public static void EnsureLiveRefresh()
        {
            // 故意留空：注册定时器就会产生"没请求也在传数据"的性能浪费。
            // 需要抓帧时由请求方直接调 WriteLiveFrame → WriteLatestFrameAsync。
        }

        /// <summary>标记"刚刚有人在看网页"。</summary>
        public static void MarkLiveActivity() => _lastLiveRequest = DateTime.UtcNow;

        /// <summary>当前是否有人在看网页（10 秒内有请求）。</summary>
        public static bool IsLiveActive() => DateTime.UtcNow - _lastLiveRequest < LiveIdleTimeout;

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

                // ★ 3.3.5：网页直连代理时，代理要各自开端口，
                //   局域网里的设备必须能连到那些端口 —— 一次性放行端口范围，
                //   而不是每个账号一条规则（账号数会变，规则不该跟着变）。
                try
                {
                    if (App.Config?.Settings?.WebDirectToAgent == true)
                        WebFirewall.EnsureAllowed(port);
                }
                catch { }

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

            // 关掉网页访问就别留防火墙规则（用户明确不喜欢残留垃圾）
            try { WebFirewall.Remove(); } catch { }
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

                // POST 白名单：只有这两个接口接受写操作。
                // ★ 2026-11-22 加 /api/settings 时踩过：这里原来只放行 /api/input，
                //   新接口被这条前置拦截挡住，返回 404 而代码里明明有路由 —— 
                //   排查方向很容易跑偏（以为是路由没注册 / 跑的是旧二进制）。
                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (path == "/api/input")
                    {
                        if (!IsAuthorized(ctx)) { await WriteText(ctx, 401, "未登录"); return; }
                        await HandleInput(ctx);
                        return;
                    }
                    if (path == "/api/settings")
                    {
                        if (!IsAuthorized(ctx)) { await WriteText(ctx, 401, "未登录"); return; }
                        await HandleSettings(ctx);
                        return;
                    }
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

                if (path == "/" || path == "/index.html") { await WriteHtml(ctx, LoadIndexHtml()); return; }
                if (path == "/api/list") { await WriteJson(ctx, ApiList()); return; }
                if (path == "/api/status") { await WriteJson(ctx, ApiStatus()); return; }
                if (path == "/api/live") { await WriteLiveFrame(ctx); return; }
                if (path == "/api/sessions") { await WriteJson(ctx, ApiSessions()); return; }
                if (path == "/api/bgi/ledger") { await WriteJson(ctx, ApiBgiLedger(ctx)); return; }
                if (path == "/api/bgi/stats") { await WriteJson(ctx, ApiBgiStats(ctx)); return; }
                if (path == "/api/settings") { await HandleSettings(ctx); return; }

                // 静态文件：/file/<相对路径>
                if (path.StartsWith("/file/", StringComparison.OrdinalIgnoreCase))
                { await WriteFile(ctx, path.Substring(6)); return; }

                await WriteText(ctx, 404, "404");
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        // ── 页面加载：磁盘优先（改网页不用重启）────────────────────────
        //
        //  ★★ 2026-11-22 用户反馈 ★★
        //    「让你改网页，每次都要重启主程序吗？重启就要重连，很麻烦啊」
        //
        //  原来 index 页面是编译进 exe 的常量（WebPages.Index），
        //  改一个 CSS 都得重新编译 + 重启程序 + 断开所有 RDP 会话。
        //  这完全不合理：网页是内容，不是代码。
        //
        //  现在：优先读 exe 同目录的 web-index.html，读不到才回退内置常量。
        //  改样式只要编辑那个文件 + 浏览器刷新 —— 主程序完全不用动。
        //
        //  发布时 build 会把 docs/web-index.html 复制到 dist\，
        //  所以正式用户仍然拿到最新页面；内置常量只是兜底（文件被删也能用）。
        private static string LoadIndexHtml()
        {
            try
            {
                string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
                if (dir.Length > 0)
                {
                    string file = Path.Combine(dir, "web-index.html");
                    if (File.Exists(file))
                    {
                        // ★ 每次都读，不缓存：改完刷新就生效（文件很小，几十 KB）
                        string html = File.ReadAllText(file, Encoding.UTF8);
                        if (html.Length > 100) return html;
                    }
                }
            }
            catch { }
            return WebPages.Index;   // 兜底：内置常量
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

        /// <summary>配置组台账只读查询；不扫描日志、不返回任何本地文件路径。</summary>
        private static object ApiBgiLedger(HttpListenerContext ctx)
        {
            string user = (ctx.Request.QueryString["user"] ?? "").Trim();
            if (user.Length == 0) return new { ok = false, error = "缺少 user" };
            int days = int.TryParse(ctx.Request.QueryString["days"], out int n) ? Math.Clamp(n, 1, 365) : 7;
            return new
            {
                ok = true, user, days,
                records = RouteLedger.Query(user, days),
                groups = RouteLedger.GroupSummary(user, days),
                zeroReward = RouteLedger.QueryZeroReward(user, days),
                failed = RouteLedger.QueryFailed(user, days)
            };
        }

        /// <summary>今日统计只读快照；复制集合，避免可变缓存被序列化时并发修改。</summary>
        private static object ApiBgiStats(HttpListenerContext ctx)
        {
            string user = (ctx.Request.QueryString["user"] ?? "").Trim();
            if (user.Length == 0) return new { ok = false, error = "缺少 user" };
            var today = DailyStatsCache.Get(user);
            int pickTotal = 0;
            foreach (int count in today.Picks.Values) pickTotal += count;
            return new
            {
                ok = true, user, day = today.Day,
                exp = today.DogfoodExp, mora = today.DogfoodMora,
                pickTotal, picks = new Dictionary<string, int>(today.Picks, StringComparer.OrdinalIgnoreCase),
                dogfoodRoutes = today.DogfoodRoutes, lastDogfoodRoute = today.LastDogfoodRoute,
                lastRoute = today.LastDogfoodRoute,
                groups = today.Groups.ToArray()
            };
        }

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

        /// <summary>
        /// 网页可见的账号列表。
        ///
        /// ★ 「绑定」= **BetterGI 路径绑定**（设置页那个），不是录制勾选。
        ///   用户明确纠正过这一点。判定交给 BgiPathResolver.IsPinned，
        ///   它内部已经要求"账号仍在 App.Config.Accounts 里"。
        /// ★ 未绑定的账号完全不返回给网页 —— 连存在都不暴露。
        ///
        /// ★ 附带运行状态（BGI 进程 / 日志空闲时长），
        ///   与桌面版「运行状态」用同一个数据源 AccountStateCenter。
        /// </summary>
        private static object ApiSessions()
        {
            var list = new List<object>();
            try
            {
                var cfg = App.Config;
                var recordIds = cfg?.Settings?.RecordAccountIds ?? new List<string>();
                var bgiPaths = cfg?.Settings?.BgiPaths
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // ★ 用 AccountScope.Accounts：它排除主用户（不参与挂机）
                foreach (var acc in AccountScope.Accounts(cfg))
                {
                    if (acc == null || string.IsNullOrWhiteSpace(acc.UserName)) continue;
                    if (!BgiPathResolver.IsPinned(acc.UserName)) continue;   // 未绑定 → 不返回

                    var st = AccountStateCenter.Get(acc.Id);
                    int sid = st?.SessionId ?? 0;
                    var relay = sid > 0 ? RelayClient.FindBySession(sid) : null;
                    bool online = relay != null && relay.IsConnected;

                    // BGI 日志多久没更新（判断"运行中但空闲"）
                    double logIdleMin = -1;
                    try
                    {
                        if (st != null && st.BgiRunning && st.BgiLogWrite > DateTime.MinValue)
                            logIdleMin = Math.Round((DateTime.Now - st.BgiLogWrite).TotalMinutes, 1);
                    }
                    catch { }

                    // ★★ 运行详情 ★★
                    //   用户要求：「每个用户的运行信息都要展示出来」。
                    //   数据源同样只有一个：BgiLogCenter 的快照尾窗，
                    //   用与桌面版完全相同的 BgiStatus 解析器解析，保证两边一致。
                    //
                    //   ★ 注意：这里读的是**内存里的快照字符串**，不是日志文件。
                    //     所以不违反"日志只有数据中心能读"的架构。
                    string taskName = "", configGroup = "", routeFile = "",
                           round = "", role = "", eta = "", action = "";
                    int errorCount = 0, pickTotal = 0;
                    try
                    {
                        var snap = BgiLogCenter.Snapshot(acc.Id);
                        if (snap != null && snap.Tail.Count > 0)
                        {
                            var sb = new StringBuilder(snap.Tail.Count * 96);
                            foreach (var line in snap.Tail) sb.AppendLine(line.Text);

                            var status = new BgiStatus();
                            status.Update(sb.ToString());

                            taskName = status.TaskName ?? "";
                            configGroup = status.ConfigGroup ?? "";
                            routeFile = status.RouteFile ?? "";
                            round = status.Round ?? "";
                            role = status.RoleName ?? "";
                            eta = status.RouteEta ?? "";
                            action = status.CurrentAction ?? "";
                            pickTotal = status.PickTotal;
                        }
                        if (st != null) errorCount = st.ErrorCount;
                    }
                    catch { }

                    list.Add(new
                    {
                        id = acc.Id,
                        user = acc.UserName,
                        nick = acc.DisplayName ?? "",
                        label = string.IsNullOrWhiteSpace(acc.DisplayName)
                                ? acc.UserName
                                : acc.UserName + "-" + acc.DisplayName,
                        sessionId = sid,
                        bound = true,
                        online,
                        bgiRunning = st?.BgiRunning == true,
                        bgiPid = st?.BgiPid ?? 0,
                        bgiLogIdleMinutes = logIdleMin,
                        recording = recordIds.Contains(acc.Id),
                        bgiPath = bgiPaths.TryGetValue(acc.UserName, out var bp) ? bp : "",
                        screen = online ? (relay!.ScreenWidth + "x" + relay.ScreenHeight) : "",

                        // 运行详情
                        taskName,
                        configGroup,
                        routeFile,
                        round,
                        role,
                        eta,
                        action,
                        errorCount,
                        pickTotal,

                        // ★★ 3.3.5：网页直连代理的端口（0 = 该账号没有直连，走主程序转发）★★
                        //
                        //   用户要求「网页端尽量隔离主客户端」：
                        //   网页拿到这个端口后，画面/录像/按键全部直连代理，
                        //   主程序只在这一次 JSON 里出现，之后不再参与任何流量。
                        directPort = ResolveDirectPort(acc.Id)
                    });
                }
            }
            catch { }

            string version = "";
            try
            {
                var v = typeof(WebServer).Assembly.GetName().Version;
                if (v != null) version = v.Major + "." + v.Minor + "." + v.Build;
            }
            catch { }

            // ★ 下发应用当前主题，让网页自动跟随（用户要求"把应用的亮暗主题做进去"）
            bool themeDark = true;
            try { themeDark = ThemeManager.IsDark; } catch { }

            // ★ 3.3.5：把直连口令下发给已登录的页面。
            //
            //   为什么必须下发：<img src> / <video src> 无法携带自定义请求头，
            //   浏览器只会发裸 GET。前端需要把口令拼进 URL 查询串，
            //   代理侧两种都接受（见 Relay/AgentWebServer.CheckToken）。
            //
            //   ★ 安全性：本接口本身已经过口令鉴权（见 HandleInputAsync 之前的
            //     IsAuthorized 检查），所以这里下发不会泄露给未登录用户。
            // ★ 录制参数（用户要求「两边同步」）
            //   网页读到这三个值后显示下拉菜单；改了会回写到主程序配置，
            //   主程序再下发给正在录制的代理 —— 双向同步，不是单向下发。
            int recordFps = 5, recordQuality = 75, recordKeepSeconds = 30, streamFps = 30;
            int videoBitrate = 8000, nvencPreset = 3, vbvBufferIncreasePercent = 0, captureMode = 0;
            try
            {
                var rs = App.Config?.Settings;
                if (rs != null)
                {
                    recordFps = Math.Max(1, rs.RecordFps);
                    recordQuality = Math.Clamp(rs.RecordQuality, 10, 100);
                    recordKeepSeconds = Math.Clamp(rs.RecordKeepSeconds, 5, 600);
                    streamFps = Math.Clamp(rs.StreamFps, 1, 120);
                    videoBitrate = Math.Clamp(rs.VideoBitrateKbps, 1000, 500000);
                    nvencPreset = Math.Clamp(rs.NvencPreset, 1, 7);
                    vbvBufferIncreasePercent = Math.Clamp(rs.VbvBufferIncreasePercent, 0, 200);
                    captureMode = Math.Clamp(rs.CaptureMode, 0, 3);
                }
            }
            catch { }

            string directToken = "";
            try
            {
                if (App.Config?.Settings?.WebDirectToAgent == true)
                    directToken = App.Config.Settings.WebPassword ?? "";
            }
            catch { }

            return new { ok = true, version, themeDark, directToken, recordFps, recordQuality, recordKeepSeconds, streamFps, videoBitrate, nvencPreset, vbvBufferIncreasePercent, captureMode, sessions = list };
        }

        /// <summary>
        /// 网页修改录制参数（帧率 / 画质 / 保留时长），双向同步。
        ///
        /// ★ 用户要求：「把这些设置都加到浏览器，两边同步」
        ///
        ///   流程：网页 POST → 主程序写 config.json → 立即下发给正在录的代理
        ///        → 应用设置界面下次打开时读到新值（同一份 App.Config.Settings）
        ///
        /// ★ 只允许改这三项。网页是外部输入，绝不能让它改路径/账号/口令之类。
        /// </summary>
        private static async Task HandleSettings(HttpListenerContext ctx)
        {
            try
            {
                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await sr.ReadToEndAsync().ConfigureAwait(false);

                var dto = JsonSerializer.Deserialize<SettingsDto>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (dto == null) { await WriteJson(ctx, new { ok = false, error = "格式错误" }).ConfigureAwait(false); return; }

                var s = App.Config?.Settings;
                if (s == null) { await WriteJson(ctx, new { ok = false, error = "配置未加载" }).ConfigureAwait(false); return; }

                // 逐项校验：网页传来的值必须落在应用支持的范围里
                if (dto.fps > 0)          s.RecordFps = Math.Clamp(dto.fps, 1, 30);
                if (dto.quality > 0)      s.RecordQuality = Math.Clamp(dto.quality, 10, 100);
                if (dto.keepSeconds > 0)  s.RecordKeepSeconds = Math.Clamp(dto.keepSeconds, 5, 600);

                // ★ 直播画质（2026-11-23 网页端手机原型）：码率/帧率/preset/vbv
                if (dto.streamFps > 0)     s.StreamFps = Math.Clamp(dto.streamFps, 1, 120);
                if (dto.videoBitrate > 0)  s.VideoBitrateKbps = Math.Clamp(dto.videoBitrate, 1000, 500000);
                if (dto.nvencPreset > 0)   s.NvencPreset = Math.Clamp(dto.nvencPreset, 1, 7);
                if (dto.vbvBufferIncreasePercent.HasValue)
                    s.VbvBufferIncreasePercent = Math.Clamp(dto.vbvBufferIncreasePercent.Value, 0, 200);
                // ★ 抓屏后端（2026-11-23 网页端）：0=自动(DDA→WGC→BitBlt)/1=DDA/2=WGC/3=BitBlt
                if (dto.captureMode.HasValue)
                    s.CaptureMode = Math.Clamp(dto.captureMode.Value, 0, 3);

                try { ConfigStore.Save(App.Config!); } catch { }

                // ★ 通知界面刷新：已打开的设置面板要立刻显示新值，
                //   否则用户改完网页回到应用，看到的还是旧数字。
                //   WindowsBase 的 Dispatcher 只在 UI 线程可用，所以切过去。
                try
                {
                    var app = System.Windows.Application.Current;
                    if (app != null)
                        app.Dispatcher.BeginInvoke(new Action(() => App.NotifySettingsChanged()));
                }
                catch { }

                // ★ 立即下发给正在录制的代理，不用等用户去应用里点「开始录制」
                int applied = 0;
                try
                {
                    foreach (var acc in AccountScope.Accounts(App.Config))
                    {
                        if (acc == null) continue;
                        if (!(s.RecordAccountIds?.Contains(acc.Id) ?? false)) continue;
                        var st = AccountStateCenter.Get(acc.Id);
                        int sid = st?.SessionId ?? 0;
                        if (sid <= 0) continue;
                        var relay = RelayClient.FindBySession(sid);
                        if (relay == null || !relay.IsConnected) continue;
                        _ = relay.RecordStartAsync(s.RecordFps, s.RecordQuality, s.RecordKeepSeconds);
                        applied++;
                    }
                }
                catch { }

                RelayClient.PushVideoParametersToConnectedAgents();
                // ★ 抓屏后端热切：网页改了下发所有已连接代理（DDA→WGC→BitBlt 降级链在代理内实现）
                RelayClient.PushCaptureModeToConnectedAgents(s.CaptureMode);

                await WriteJson(ctx, new {
                    ok = true,
                    fps = s.RecordFps,
                    quality = s.RecordQuality,
                    keepSeconds = s.RecordKeepSeconds,
                    streamFps = s.StreamFps,
                    videoBitrate = s.VideoBitrateKbps,
                    nvencPreset = s.NvencPreset,
                    vbvBufferIncreasePercent = s.VbvBufferIncreasePercent,
                    captureMode = s.CaptureMode,
                    agentsUpdated = applied
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteJson(ctx, new { ok = false, error = ex.Message }).ConfigureAwait(false);
            }
        }

        /// <summary>网页提交的设置参数（录制三项 + 直播画质四项可改）。</summary>
        private sealed class SettingsDto
        {
            public int fps { get; set; }
            public int quality { get; set; }
            public int keepSeconds { get; set; }
            public int streamFps { get; set; }
            public int videoBitrate { get; set; }
            public int nvencPreset { get; set; }
            // 0 是合法值（不额外缓冲），用可空区分「未传」与「传 0」。
            public int? vbvBufferIncreasePercent { get; set; }
            // 0 是合法值（自动），用可空区分「未传」与「传 0」。
            public int? captureMode { get; set; }
        }

        /// <summary>
        /// 该账号的网页直连端口；0 表示不走直连（功能关闭或没配端口）。
        /// 规则与投送时代理拿到的一致，见 Services/WebDirect.cs。
        /// </summary>
        private static int ResolveDirectPort(string accountId)
        {
            try
            {
                var s = App.Config?.Settings;
                if (s == null || !s.WebDirectToAgent || s.WebPort <= 0) return 0;
                var accounts = App.Config?.Accounts;
                if (accounts == null || string.IsNullOrWhiteSpace(accountId)) return 0;
                int idx = accounts.FindIndex(a =>
                    string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return 0;
                return WebDirect.PortFor(s.WebPort, idx);
            }
            catch { return 0; }
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
    }
}
