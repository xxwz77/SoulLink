using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 反向代理：把非 <c>/taskedit/*</c> 的请求透明转发给主程序回环端口
    /// （设计文档 §18.1 路由表 / §18.2 风险结论）。
    ///
    /// ── 三条硬规则（对应设计文档 §18.2）────────────────────────────────
    ///   ① **流式转发，绝不整段缓冲**：用 <c>ResponseHeadersRead</c> 拿到响应头就
    ///      开始往客户端写，边读边写。现有页面有录像流（WriteStreamWithRange）
    ///      与分块视频，整段缓冲会直接把内存打爆。
    ///   ② **方法/路径/查询串/请求体/请求头原样透传**，特别是 Range / Cookie /
    ///      Accept-Encoding —— 少一个 Range 视频就没法拖动进度条。
    ///   ③ **主程序未运行要给人话**：连接失败/超时返回 200 的错误页（不是 500 白屏），
    ///      页面上直接说「主程序未运行」并给出该做什么。
    ///
    /// ── 关于鉴权与 Cookie ─────────────────────────────────────────────
    ///   本服务已经在 <see cref="AuthGate"/> 统一拦过一道，转发时**不再附带任何
    ///   额外内部令牌**给主程序：主程序监听的是 127.0.0.1 回环，只有本机可达，
    ///   而本服务本身运行在同一台机器上。这样主程序侧零改动、零新凭证，
    ///   也不会因为「内部令牌」再多一处要保管的秘密。
    ///   客户端的原始 Cookie 会透传（主程序页面自己的登录态仍按原逻辑工作）。
    /// </summary>
    internal sealed partial class ReverseProxy
    {
        private static readonly HttpClient Http = CreateClient();

        /// <summary>主程序网页会话的 Cookie 名（Services/WebServer.cs 的 AuthCookie）。</summary>
        private const string UpstreamCookieName = "soullink_sid";

        private volatile string _baseUrl;
        private volatile int _internalPort;

        /// <summary>
        /// 主程序的访问令牌（config.json 的 webAuthToken）。
        /// 反代时会以 <c>soullink_sid=&lt;token&gt;</c> 注入，让主程序把本服务当作已认证来源。
        /// 空 = 未读到（主程序可能还没生成），此时不带 Cookie，靠主程序自身的判断。
        /// </summary>
        private volatile string _upstreamToken;

        internal ReverseProxy(int internalPort, string upstreamToken = null)
        {
            _internalPort = internalPort;
            _baseUrl = internalPort > 0 ? "http://127.0.0.1:" + internalPort : "";
            _upstreamToken = (upstreamToken ?? "").Trim();
        }

        /// <summary>更新主程序令牌（配置热重载/主程序重新生成 token 后调用）。</summary>
        internal void SetUpstreamToken(string token) => _upstreamToken = (token ?? "").Trim();

        internal bool HasBackend => _internalPort > 0;

        internal int InternalPort => _internalPort;

        /// <summary>
        /// 换上游端口（主程序重启后内部端口会变，本服务可以被要求**热切换**）。
        ///
        /// ★ 为什么需要它：主程序重启后会用一个新的回环端口（旧的已释放），
        ///   若本服务还指向旧端口就会一直返回「主程序未运行」。
        ///   有了这个方法，主程序可以**复用**已经在跑的实例，只把新端口告诉它 ——
        ///   不用杀掉旧实例再起新的（那会有端口释放竞态与短暂不可用）。
        /// </summary>
        internal void Rebind(int internalPort)
        {
            _internalPort = internalPort > 0 ? internalPort : 0;
            _baseUrl = internalPort > 0 ? "http://127.0.0.1:" + internalPort : "";
        }

        private static HttpClient CreateClient()
        {
            // ★ UseCookies=false：Cookie 由我们逐请求透传，不能让 handler 自己维护容器，
            //   否则多个浏览器会话会串味。
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,   // 压缩由我们自己决定
                AllowAutoRedirect = false,                            // 302 要原样给浏览器
                ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true
            };
            return new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan,   // 流式响应不能有总超时（长视频会跑很久）
                DefaultRequestVersion = HttpVersion.Version11,
            };
        }

        internal string EditorState(string account, string root)
        {
            string url = _baseUrl, token = _upstreamToken;
            if (url.Length == 0 || token.Length == 0) return "unknown";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url + "/api/bgi/editor/status?account=" + Uri.EscapeDataString(account));
                req.Headers.Add("Cookie", UpstreamCookieName + "=" + token);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var response = Http.Send(req, timeout.Token);
                if (!response.IsSuccessStatusCode) return "unknown";
                using var doc = System.Text.Json.JsonDocument.Parse(response.Content.ReadAsStream());
                var result = doc.RootElement;
                if (!result.GetProperty("ok").GetBoolean() || !string.Equals(account, result.GetProperty("account").GetString(), StringComparison.OrdinalIgnoreCase)) return "unknown";
                string exe = Path.GetFullPath(Path.Combine(root, "BetterGI.exe")).ToUpperInvariant();
                string binding = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(exe)));
                if (result.GetProperty("binding").GetString() != binding) return "unknown";
                return result.GetProperty("state").GetString() ?? "unknown";
            }
            catch { return "unknown"; }
        }

        /// <summary>转发一个请求。</summary>
        internal async Task ForwardAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            if (!HasBackend)
            {
                await WriteBackendDownAsync(ctx, "本服务未收到主程序内部端口（主程序可能未启动）");
                return;
            }

            HttpRequestMessage request = null;
            HttpResponseMessage response = null;
            try
            {
                request = BuildRequest(ctx);
                // ★ ResponseHeadersRead：拿到头就返回，body 后面边读边写。
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                await RelayResponseAsync(ctx, response, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { ctx.Response.Abort(); } catch { }
            }
            catch (HttpRequestException)
            {
                await SafeBackendDownAsync(ctx);
            }
            catch (IOException)
            {
                await SafeBackendDownAsync(ctx);
            }
            catch (Exception)
            {
                await SafeBackendDownAsync(ctx);
            }
            finally
            {
                try { response?.Dispose(); } catch { }
                try { request?.Dispose(); } catch { }
            }
        }

        private HttpRequestMessage BuildRequest(HttpListenerContext ctx)
        {
            string rawPath = ctx.Request.RawUrl ?? "/";
            if (!rawPath.StartsWith("/", StringComparison.Ordinal)) rawPath = "/" + rawPath;
            var request = new HttpRequestMessage(new HttpMethod(ctx.Request.HttpMethod), _baseUrl + rawPath);

            // ── 请求体：有就流式送过去（不整段读进内存）──
            if (ctx.Request.HasEntityBody)
            {
                request.Content = new StreamContent(ctx.Request.InputStream, 64 * 1024);
                string ct = ctx.Request.ContentType ?? "";
                if (ct.Length > 0)
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
                if (ctx.Request.ContentLength64 > 0)
                    request.Content.Headers.ContentLength = ctx.Request.ContentLength64;
            }
            else if (ctx.Request.HttpMethod is "POST" or "PUT" or "PATCH")
            {
                // 空 body 的 POST 也要带 0 长度，否则部分接口会等不到结尾
                request.Content = new ByteArrayContent(Array.Empty<byte>());
            }

            // ── 请求头透传 ──
            foreach (string name in ctx.Request.Headers.AllKeys)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (IsHopByHop(name)) continue;
                if (name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                // ★★ 3.3.46（t36）：**不透传 Cookie**，改为注入主程序自己的凭据（见下方）
                //   客户端带来的是 TaskEdit 的身份（soullink_te），主程序认的是 soullink_sid。
                //   透传过去只会让主程序看到「一个不认识的 cookie」→ 走非回环分支 → 401。
                if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;
                string value = ctx.Request.Headers[name] ?? "";
                if (value.Length == 0) continue;
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    try { request.Content?.Headers.TryAddWithoutValidation(name, value); } catch { }
                }
            }

            // ★★ 反代职责：**用自己的凭据**访问主程序回环（Lead 采纳的修复方向 1）★★
            //
            //   两层鉴权必须分工，不能重叠：
            //     ① 客户端 → TaskEdit：TaskEdit 的 AuthGate（口令 → soullink_te），只鉴一次；
            //     ② TaskEdit → 主程序：本服务持主程序 WebAuthToken，以主程序自己的
            //        Cookie 名下发，主程序按「已认证」处理。
            //
            //   为什么不靠「回环免密」：主程序虽只绑 127.0.0.1，但本机任何进程都能连；
            //   靠 IP 放行等于「同机即信任」，与用户要求的安全边界不符。
            //   带上真实 token 后，即便有人绕过 TaskEdit 直连内部端口也得有 token。
            //
            //   token 来源：TaskEdit 启动时读 config.json 的 webAuthToken（ServiceConfig），
            //   与主程序 EnsureAuthToken() 是同一个值（主程序首次生成后落盘）。
            try
            {
                string token = _upstreamToken ?? "";
                if (token.Length > 0)
                    request.Headers.TryAddWithoutValidation("Cookie", UpstreamCookieName + "=" + token);
            }
            catch { }

            // 让主程序知道真实来源（诊断用；不参与鉴权）
            try
            {
                string client = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "";
                if (client.Length > 0)
                {
                    request.Headers.TryAddWithoutValidation("X-Forwarded-For", client);
                    request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");
                    request.Headers.TryAddWithoutValidation("X-Forwarded-Host", ctx.Request.UserHostName ?? "");
                }
            }
            catch { }
            return request;
        }

        /// 「主程序未运行」页。用 <b>200</b> 而不是 500：
        /// 这是**用户可理解的服务状态**，不是服务器内部错误；
        /// 浏览器与穿透工具看到 500 会当故障重试/报警，看到 200 + 说明才符合预期。
        /// </summary>
        internal static async Task WriteBackendDownAsync(HttpListenerContext ctx, string detail)
        {
            try
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                byte[] bytes = Encoding.UTF8.GetBytes(Pages.BackendDown(detail));
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                ctx.Response.Close();
            }
            catch { }
        }
        private async Task RelayResponseAsync(HttpListenerContext ctx, HttpResponseMessage response, CancellationToken ct)
        {
            var res = ctx.Response;

            // ── 状态码 ──
            res.StatusCode = (int)response.StatusCode;

            // ── 响应头（含 Content-Range / Content-Type / Set-Cookie）──
            string contentType = response.Content?.Headers.ContentType?.ToString() ?? "";
            string contentRange = "";
            long contentLength = -1;
            bool hasSetCookie = false;

            foreach (var header in response.Headers)
            {
                string name = header.Key;
                if (IsHopByHop(name)) continue;
                if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string value in header.Value)
                {
                    try
                    {
                        if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                        { AppendSetCookie(res, value); hasSetCookie = true; }
                        else res.Headers[name] = value;
                    }
                    catch { }
                }
            }
            foreach (var header in response.Content.Headers)
            {
                string name = header.Key;
                if (IsHopByHop(name)) continue;
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                { contentLength = response.Content.Headers.ContentLength ?? -1; continue; }
                if (name.Equals("Content-Range", StringComparison.OrdinalIgnoreCase))
                { contentRange = string.Join(",", header.Value); }
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                { contentType = string.Join(",", header.Value); continue; }
                foreach (string value in header.Value)
                {
                    try { res.Headers[name] = value; } catch { }
                }
            }
            _ = hasSetCookie;

            if (contentType.Length > 0) { try { res.ContentType = contentType; } catch { } }
            if (contentRange.Length > 0) { try { res.Headers["Content-Range"] = contentRange; } catch { } }

            bool head = ctx.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

            // ── gzip 决策（只对「长度已知的完整文本」）──
            string acceptEncoding = ctx.Request.Headers["Accept-Encoding"] ?? "";
            bool compress = !contentRange.Equals("", StringComparison.Ordinal)
                ? false
                : Compression.ShouldCompress(acceptEncoding, contentType, contentRange, contentLength, head);

            if (compress)
            {
                // 长度已知 → 一次性读完再压（此时 contentLength < 上限，且是非流式文本）
                byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                byte[] gz = Compression.Gzip(body);
                res.Headers["Content-Encoding"] = "gzip";
                res.Headers["Vary"] = "Accept-Encoding";
                res.ContentLength64 = gz.Length;
                if (!head) await res.OutputStream.WriteAsync(gz, 0, gz.Length, ct).ConfigureAwait(false);
                res.Close();
                return;
            }

            // ── 流式透传（不缓冲）──
            if (contentLength >= 0)
            {
                try { res.ContentLength64 = contentLength; } catch { }
            }
            else
            {
                try { res.SendChunked = true; } catch { }
            }

            if (!head)
            {
                using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await CopyStreamAsync(src, res.OutputStream, ct).ConfigureAwait(false);
            }
            try { res.Close(); } catch { }
        }

        /// <summary>边读边写（64 KB 缓冲，单次只驻留一个块，绝不整段缓存）。</summary>
        private static async Task CopyStreamAsync(Stream source, Stream target, CancellationToken ct)
        {
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        private static void AppendSetCookie(HttpListenerResponse res, string value)
        {
            try
            {
                string existing = res.Headers["Set-Cookie"] ?? "";
                res.Headers["Set-Cookie"] = existing.Length == 0 ? value : existing + "," + value;
            }
            catch { }
        }

        private static bool IsHopByHop(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "connection": case "keep-alive": case "proxy-authenticate":
                case "proxy-authorization": case "te": case "trailer":
                case "transfer-encoding": case "upgrade": case "expect":
                    return true;
                default: return false;
            }
        }

        // ── 降级：主程序未运行 ──────────────────────────────────────────

        private static async Task SafeBackendDownAsync(HttpListenerContext ctx)
        {
            try
            {
                if (ctx.Response.OutputStream.CanWrite)
                    await WriteBackendDownAsync(ctx, "主程序未运行或内部端口无响应");
            }
            catch { }
        }
    }
}