using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 独立服务的监听与路由（设计文档 §17.1 / §18.1）。
    ///
    /// 路由只有两类：
    ///   · <c>/taskedit/*</c> → 本服务自处理（P0 只有占位页 + 登录 + 健康检查；
    ///     真正的配置读写是 P1 的数据面，本任务明确不做）；
    ///   · 其余全部 → 反代到主程序回环端口（<see cref="ReverseProxy"/>）。
    ///
    /// 启动顺序（<see cref="Program"/>）：单实例互斥 → 读配置 → 绑端口 → 起循环。
    /// 端口被占用**绝不静默换端口**：用户要在路由器/openP2P 里转发这个固定端口，
    /// 换端口等于让转发规则失效，必须明确报错让人改。
    /// </summary>
    internal sealed partial class TaskEditServer
    {
        private readonly ServiceConfig _config;
        private readonly AuthGate _auth;
        private readonly ReverseProxy _proxy;
        /// <summary>与主程序的进程间共享密钥（见 <see cref="ServiceKey"/>）。</summary>
        private readonly ServiceKey _key;
        private readonly CancellationTokenSource _cts = new();
        private HttpListener _listener;

        internal TaskEditServer(ServiceConfig config, AuthGate auth)
        {
            _config = config;
            _auth = auth;
            _key = ServiceKey.FromEnvironment();   // 环境变量，不落盘不落日志
            // ★ 3.3.46（t36）：把主程序的 webAuthToken 交给反代。
            //   反代用它注入 soullink_sid，主程序才会把请求当作「已认证」。
            //   没有这一步，客户端带来的 soullink_te 主程序不认 → 全站 401。
            _proxy = new ReverseProxy(config.InternalPort, config.WebAuthToken);
        }

        /// <summary>本实例是否已配置进程间 key（未配置时 rebind 一律 401）。</summary>
        internal bool HasServiceKey => _key.Configured;

        /// <summary>实际监听的端口（0 = 未启动）。</summary>
        internal int Port => _listener?.IsListening == true ? _config.WebPort : 0;

        /// <summary>
        /// 绑定并开始监听。返回 false 表示没起来（原因已打印）。
        /// </summary>
        internal bool Start()
        {
            int port = _config.WebPort;
            if (port <= 0)
            {
                Console.WriteLine("[TaskEdit] WebPort=0（未启用网页），本服务不启动。");
                return false;
            }
            if (port < 1024 || port > 65535)
            {
                Console.Error.WriteLine("[TaskEdit] WebPort=" + port + " 不合法：必须是 1024~65535。");
                return false;
            }

            var listener = new HttpListener();
            // ★ 绑 http://+:<port>/ = 所有网卡，远程（端口转发/openP2P/组网）才可达。
            listener.Prefixes.Add("http://+:" + port + "/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                try { listener.Close(); } catch { }
                Console.Error.WriteLine("[TaskEdit] 无法监听端口 " + port + "：" + ex.Message);
                if (ex.ErrorCode == 5)
                    Console.Error.WriteLine("         权限不足：绑定 + 前缀需要管理员权限（或先执行 netsh http add urlacl）。");
                else
                    Console.Error.WriteLine("         端口可能已被占用。请在主程序设置里换一个网页端口后重试" +
                                            "（本服务不会自动换端口：该端口是你端口转发的目标，必须稳定）。");
                return false;
            }
            catch (Exception ex)
            {
                try { listener.Close(); } catch { }
                Console.Error.WriteLine("[TaskEdit] 监听失败：" + ex.GetType().Name + " " + ex.Message);
                return false;
            }

            _listener = listener;
            _ = Task.Run(() => LoopAsync(listener, _cts.Token));

            try { SoulLink.Services.WebFirewall.EnsureAllowed(port); } catch { }
            Console.WriteLine("[TaskEdit] 已监听 http://+:" + port + "/  （对外唯一入口）");
            if (_proxy.HasBackend) Console.WriteLine("[TaskEdit] 反代目标 http://127.0.0.1:" + _config.InternalPort + "/");
            else Console.WriteLine("[TaskEdit] ⚠ 未收到主程序内部端口：所有代理请求都会返回「主程序未运行」页");
            return true;
        }

        internal void Stop()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            try { SoulLink.Services.WebFirewall.Remove(); } catch { }
            Console.WriteLine("[TaskEdit] 已停止监听。");
        }

        private async Task LoopAsync(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx = null;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleAsync(ctx, ct), ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception) { if (ct.IsCancellationRequested) break; }
                _ = ctx;
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            try
            {
                string path = (ctx.Request.Url?.AbsolutePath ?? "/").Trim();

                // ── /taskedit/* ：本服务自处理 ──
                if (path.StartsWith("/taskedit", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleTaskEditAsync(ctx, path, ct).ConfigureAwait(false);
                    return;
                }

                // ── 其余：鉴权后反代 ──
                if (!_auth.Check(ctx, out string reason))
                {
                    await WriteUnauthorizedAsync(ctx, reason).ConfigureAwait(false);
                    return;
                }
                if (path.StartsWith("/api/bgi/editor/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_auth.MatchesAnyCookie(ctx))
                    {
                        await WriteUnauthorizedAsync(ctx, "请先登录后再操作账号 BGI").ConfigureAwait(false);
                        return;
                    }
                    if (ctx.Request.HttpMethod == "POST" && !TaskEditApi.SameOrigin(ctx.Request))
                    {
                        await WriteTextAsync(ctx, 403, "需要同源请求").ConfigureAwait(false);
                        return;
                    }
                }
                await _proxy.ForwardAsync(ctx, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                try { ctx.Response.Abort(); } catch { }
            }
        }

        private async Task HandleTaskEditAsync(HttpListenerContext ctx, string path, CancellationToken ct)
        {
            // 健康检查：对外**不鉴权**（穿透工具保活/负载均衡探活用），但分层返回。
            //   ★ 不带 key（或 key 错）时**只回最小信息**：绝不泄露内部端口 ——
            //     内部端口是攻击面的一部分（知道它就能直连主程序回环）。
            // 健康检查 / 内部端口热切换（两段都在 TaskEditProbe.cs 的 partial 里）
            if (await TryHandleProbeAsync(ctx, path, ct).ConfigureAwait(false)) return;

            // 登录页/登录提交：自己处理（唯一不需要已登录的入口）
            if (path.Equals("/taskedit/login", StringComparison.OrdinalIgnoreCase))
            {
                await HandleLoginAsync(ctx, ct).ConfigureAwait(false);
                return;
            }

            if (!_auth.Check(ctx, out string reason))
            {
                await WriteUnauthorizedAsync(ctx, reason).ConfigureAwait(false);
                return;
            }

            // ★ 前端静态资源（shell.css / *.js）：文本，走 gzip。
            //   放在页面路由之前，避免被当成分区页处理。
            if (TaskEditStatic.IsStaticPath(path))
            {
                if (TaskEditStatic.TryServe(ctx, path)) return;
                await WriteTextAsync(ctx, 404, "静态资源不存在").ConfigureAwait(false);
                return;
            }

            // ★ 任务编辑页（真界面）：左树 + 右画布（设计文档 §3.1）。
            if (path.Equals("/taskedit", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/taskedit/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/taskedit/index", StringComparison.OrdinalIgnoreCase))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(TaskEditStatic.Page());
                long len = bytes.Length;
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                string ae = ctx.Request.Headers["Accept-Encoding"] ?? "";
                if (Compression.ShouldCompress(ae, "text/html", "", len, false))
                {
                    byte[] gz = Compression.Gzip(bytes);
                    ctx.Response.Headers["Content-Encoding"] = "gzip";
                    ctx.Response.Headers["Vary"] = "Accept-Encoding";
                    ctx.Response.ContentLength64 = gz.Length;
                    await ctx.Response.OutputStream.WriteAsync(gz, 0, gz.Length, ct).ConfigureAwait(false);
                }
                else
                {
                    ctx.Response.ContentLength64 = len;
                    await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                }
                ctx.Response.Close();
                return;
            }

            // P1 数据面 API（/taskedit/api/*）；其它 /taskedit/* 仍 404
            string qBgi = ctx.Request.QueryString["bgi"] ?? "";
            if (TaskEditApi.TryHandle(ctx, path, qBgi, _config.ConfigPath, _auth, _proxy.EditorState))
                return;
            await WriteTextAsync(ctx, 404, "未知的任务编辑路径").ConfigureAwait(false);
        }


        private async Task WriteUnauthorizedAsync(HttpListenerContext ctx, string reason)
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            string html = Pages.Login(reason ?? "");
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            long len = bytes.Length;
            string ae = ctx.Request.Headers["Accept-Encoding"] ?? "";
            if (Compression.ShouldCompress(ae, "text/html", "", len, false))
            {
                byte[] gz = Compression.Gzip(bytes);
                ctx.Response.Headers["Content-Encoding"] = "gzip";
                ctx.Response.Headers["Vary"] = "Accept-Encoding";
                ctx.Response.ContentLength64 = gz.Length;
                await ctx.Response.OutputStream.WriteAsync(gz, 0, gz.Length).ConfigureAwait(false);
            }
            else
            {
                ctx.Response.ContentLength64 = len;
                await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
            ctx.Response.Close();
        }

        private static async Task WriteJsonAsync(HttpListenerContext ctx, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            ctx.Response.Close();
        }

        private static async Task WriteTextAsync(HttpListenerContext ctx, int code, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            ctx.Response.Close();
        }
    }
}