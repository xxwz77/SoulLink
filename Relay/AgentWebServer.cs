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

        /// <summary>共享口令。空串 = 不校验（已废弃：CheckToken 改为默认拒绝）。</summary>
        private static string _token = "";

        /// <summary>显式豁免：允许无口令访问（仅本机场景）。默认 false，由 SOULLINK_WEB_ALLOW_NO_TOKEN=1 下发。</summary>
        private static bool _allowNoToken;

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
                // ★ 外网安全（2026-11-23）：默认拒绝无口令访问。
                //   主程序在配置 WebAllowNoToken=true 时经 SOULLINK_WEB_ALLOW_NO_TOKEN=1 下发豁免。
                _allowNoToken = string.Equals(
                    Environment.GetEnvironmentVariable("SOULLINK_WEB_ALLOW_NO_TOKEN") ?? "",
                    "1", StringComparison.Ordinal);
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
                catch (Exception ex) { LogFailure("监听循环异常：" + ex); continue; }

                // 每条请求独立处理；HandleSafe 是同步入口，异步路由由它接管。
                _ = Task.Run(() => HandleSafe(ctx));
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void EnsureVideoDependencies()
        {
            _ = System.Reflection.Assembly.Load("Vortice.MediaFoundation");
        }

        private static void HandleSafe(HttpListenerContext ctx)
        {
            try
            {
                // 在同步入口先加载依赖，避免 async 路由的 JIT 异常穿过最外层 catch。
                if (ctx.Request.Url?.AbsolutePath == "/stream") EnsureVideoDependencies();
                Task task = HandleAsync(ctx);
                if (task.IsCompleted) task.GetAwaiter().GetResult();
                else _ = task.ContinueWith(t => OnRequestFailed(ctx, t.Exception?.GetBaseException()
                    ?? new InvalidOperationException("未知请求错误")), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                string path = ctx.Request.Url?.AbsolutePath ?? "/";
                string message = DescribeFailure(ex);
                LogFailure("请求失败 path=" + path + "：" + ex);
                try
                {
                    WriteText(ctx, 500, message).GetAwaiter().GetResult();
                }
                catch (Exception writeEx)
                {
                    // 已经开始 chunked 推流后不能再改写状态码；记录原因并正常关闭，不再无声 Abort。
                    LogFailure("请求失败后的响应无法写出（可能已发送响应头或客户端已断开）path=" + path
                        + "：" + writeEx.GetType().Name + " " + writeEx.Message);
                    try { ctx.Response.Close(); } catch (Exception closeEx) { Log("关闭失败响应异常：" + closeEx.Message); }
                }
            }
        }

        private static void OnRequestFailed(HttpListenerContext ctx, Exception ex)
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            LogFailure("异步请求失败 path=" + path + "：" + ex);
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(DescribeFailure(ex));
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch (Exception writeEx)
            {
                Log("异步失败响应无法写出：" + writeEx.Message);
                try { ctx.Response.Close(); } catch { }
            }
        }

        private static void Log(string message)
        {
            try { RelayAgent.LogQuiet("[web] " + message); }
            catch { }
        }

        private static void LogFailure(string message)
        {
            Log(message);
            // 请求级错误很少，允许同步落盘；异步队列未及时排空也不能丢失根因。
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "soullink-agent-web-error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        private static string DescribeFailure(Exception ex)
        {
            if (ex is FileNotFoundException or FileLoadException or TypeLoadException or DllNotFoundException)
                return "视频服务启动失败：Agent 目录缺少视频运行库（Vortice.MediaFoundation.dll 等），请补齐旁挂 DLL 后重试。";
            return "视频服务处理失败：" + ex.GetType().Name + "：" + ex.Message;
        }

        private static async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            string path = req.Url?.AbsolutePath ?? "/";

            // ── CORS 收口（2026-11-23）：不再用 `*` ──
            //   `*` 会让任意来源网页（含恶意站）跨源调 /api/input 注入键鼠。
            //   改为只回显「与请求 Host 同主机」或本机回环地址的 Origin，
            //   覆盖「主程序页面(同机不同端口) → 代理」的合法跨源；其余来源不发 CORS 头。
            string? corsOrigin = ResolveCorsOrigin(ctx);
            if (corsOrigin != null)
                ctx.Response.Headers["Access-Control-Allow-Origin"] = corsOrigin;
            ctx.Response.Headers["Access-Control-Allow-Headers"] = WebDirectTokenHeader + ", Content-Type";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";

            if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            // ── 口令 ──
            string? authError = CheckToken(ctx);
            if (authError != null)
            {
                await WriteText(ctx, 401, authError).ConfigureAwait(false);
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
                // ★ P1-A2：回显真实抓屏几何（尺寸 + 虚拟桌面原点），供网页端与 norm() 基准自检
                //   （对比 canvas 尺寸 vs 抓屏尺寸，不一致即提示「坐标基准不一致」）。
                bool geo = SessionRecorder.TryGetCaptureGeometry(out int geoW, out int geoH, out int geoX, out int geoY);
                await WriteJson(ctx, new
                {
                    ok = true,
                    account = _accountId,
                    session = _sessionId,
                    pid = Environment.ProcessId,
                    port = _port,
                    // ★ 当前实际生效的抓屏后端（DXGI-DDA / WGC / BitBlt），网页端用于「配置的 vs 实际」对照显示。
                    captureBackend = SessionRecorder.CaptureBackend,
                    captureWidth = geo ? geoW : 0,
                    captureHeight = geo ? geoH : 0,
                    captureOriginX = geo ? geoX : 0,
                    captureOriginY = geo ? geoY : 0
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
                // 同步入口已经做过依赖预检；这里继续兜底其他依赖的加载失败。
                try { EnsureVideoDependencies(); }
                catch (Exception ex)
                {
                    LogFailure("[stream] 视频运行库加载失败：" + ex);
                    await WriteText(ctx, 500, DescribeFailure(ex)).ConfigureAwait(false);
                    return;
                }
                try { await RunStreamAsync(ctx).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    string message = DescribeFailure(ex);
                    LogFailure("[stream] 启动失败：" + ex);
                    try { await WriteText(ctx, 500, message).ConfigureAwait(false); }
                    catch (Exception writeEx) { Log("[stream] 500 响应写出失败：" + writeEx.Message); }
                }
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
        /// 校验口令。返回 null = 放行；非 null = 拒绝原因（直接展示给客户端的中文提示）。
        ///
        /// ★★ 2026-11-23 安全收口：token 为空不再放行 ★★
        ///   旧行为：<c>if (string.IsNullOrEmpty(_token)) return true</c> —— 而
        ///   WebPassword 默认空串 + WebDirectToAgent 默认 true，意味着**新装未设口令**时
        ///   /live、/stream、/api/input 全部无鉴权开放，任意本机/局域网网页可注入键鼠。
        ///   新行为：空 token **默认拒绝**；仅当主程序显式下发
        ///   SOULLINK_WEB_ALLOW_NO_TOKEN=1（对应配置 WebAllowNoToken）时才放行，
        ///   覆盖「仅本机、不想设口令」的向后兼容场景。
        ///
        /// ★★ 为什么支持查询串：<img>/<video> 无法携带自定义请求头，只认
        ///   X-SoulLink-Token 的话网页实时画面会一直 401；故两种都接受。
        ///   查询串会把口令留在浏览器历史/日志里，属已知取舍，外网应改一次性票据。
        /// </summary>
        private static string? CheckToken(HttpListenerContext ctx)
        {
            if (string.IsNullOrEmpty(_token))
                return _allowNoToken ? null : "代理未配置访问口令，已拒绝（需在设置中设置网页口令，或显式开启免口令访问）";
            try
            {
                string got = ctx.Request.Headers[WebDirectTokenHeader] ?? "";
                if (string.Equals(got, _token, StringComparison.Ordinal)) return null;

                // 退路：查询串（给 img/video 用）
                string q = ctx.Request.QueryString["token"] ?? "";
                return string.Equals(q, _token, StringComparison.Ordinal) ? null : "口令错误";
            }
            catch { return "口令校验失败"; }
        }

        /// <summary>
        /// CORS Origin 收口（2026-11-23）：只放行「同主机」或「本机回环」来源。
        /// 返回 null = 不设置 Access-Control-Allow-Origin 头（浏览器视为不可跨源）。
        /// 覆盖合法场景：网页跑在主程序端口、直连代理端口时，Origin 的 Host 与请求 Host 相同。
        /// </summary>
        private static string? ResolveCorsOrigin(HttpListenerContext ctx)
        {
            try
            {
                string? origin = ctx.Request.Headers["Origin"];
                if (string.IsNullOrWhiteSpace(origin)) return null;
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return null;

                string reqHost = ctx.Request.Headers["Host"] ?? "";
                string originHost = u.IsDefaultPort ? u.Host : (u.Host + ":" + u.Port);
                if (string.Equals(originHost, reqHost, StringComparison.OrdinalIgnoreCase))
                    return origin;

                if (u.Host == "localhost" || u.Host == "127.0.0.1" || u.Host == "::1")
                    return origin;

                return null;
            }
            catch { return null; }
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
            try { jpeg = SessionRecorder.CaptureJpegOnce(75); } catch { }

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
        /// H.264 直播推流：订阅共享编码器的 Annex-B NAL，多客户端各自有界队列。
        /// 采集与编码由 SessionRecorder 的唯一视频线程负责；本方法不读 JPEG、不创建编码器。
        /// 新客户端从最近 IDR 起播，客户端断开后仅退订自己的队列。
        /// 复用 CheckToken（在 HandleAsync 统一校验），/stream 已在 WebDirect 白名单。
        /// </summary>
        private static Task RunStreamAsync(HttpListenerContext ctx)
        {
            try { return WriteStreamAsync(ctx); }
            catch (Exception ex)
            {
                // 捕获 WriteStreamAsync 被 JIT 时发生的依赖加载失败。
                LogFailure("[stream] 方法入口加载失败：" + ex);
                return WriteText(ctx, 500, DescribeFailure(ex));
            }
        }

        private static async Task WriteStreamAsync(HttpListenerContext ctx)
        {
            if (!SessionRecorder.TrySubscribe(out int viewerId, out byte[]? initial, out string error))
                throw new InvalidOperationException("共享视频管线不可用：" + error);
            try
            {
                if (initial == null)
                {
                    var wait = System.Diagnostics.Stopwatch.StartNew();
                    // 软件 MFT 可能先缓冲若干输入帧才产生 IDR；等待期不占用编码线程。
                    long lastNudge = -1000;   // ★ P1 加固：距上次主动请求 IDR 的毫秒，防高频重复
                    while (initial == null && wait.ElapsedMilliseconds < 25000)
                    {
                        initial = SessionRecorder.ReadViewerFrame(viewerId);
                        if (initial == null)
                        {
                            // ★ P1 加固：静止起播时 worker 可能因静止去重不再喂帧，MF 凑不齐输入帧
                            //   永远不出 IDR。这里每 500ms 主动再触发一次按需 IDR（双保险），让 worker
                            //   即使画面静止也强制编码下一帧，不依赖画面变化。
                            if (wait.ElapsedMilliseconds - lastNudge >= 500)
                            {
                                SessionRecorder.RequestKeyFrame();
                                lastNudge = wait.ElapsedMilliseconds;
                            }
                            await Task.Delay(20).ConfigureAwait(false);
                        }
                    }
                }
                if (initial == null)
                {
                    string detail = SessionRecorder.StreamReadiness;
                    LogFailure("[stream] 首帧等待超时：" + detail);
                    await WriteText(ctx, 503, "视频尚未就绪：" + detail).ConfigureAwait(false);
                    return;
                }
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "video/h264";
                ctx.Response.Headers["Cache-Control"] = "no-store, no-cache";
                ctx.Response.SendChunked = true;
                ctx.Response.KeepAlive = true;
                var stream = ctx.Response.OutputStream;
                int frames = 0;
                try
                {
                    await stream.WriteAsync(initial).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    frames++;
                    while (true)
                    {
                        byte[]? nal = SessionRecorder.ReadViewerFrame(viewerId);
                        if (nal != null)
                        {
                            await stream.WriteAsync(nal).ConfigureAwait(false);
                            await stream.FlushAsync().ConfigureAwait(false);
                            frames++;
                        }
                        // ★ P0（2026-11-24）：事件驱动等帧替代 Task.Delay(10) 轮询。
                        //   帧入队即被 SessionRecorder.WaitForFrame 的内核信号唤醒，消除每帧平均 +5ms（最坏 +10ms）轮询延迟税；
                        //   真阻塞（零 CPU 空转）。1000ms 兜底超时仅覆盖 worker 异常停止的边界（正常有观众时 500ms 保活帧必醒）。
                        else SessionRecorder.WaitForFrame(viewerId, 1000);
                    }
                }
                catch (Exception ex) { LogFailure("[stream] 客户端断开 frames=" + frames + "：" + ex); }
                finally { try { ctx.Response.Close(); } catch (Exception ex) { Log("[stream] 关闭响应异常：" + ex.Message); } }
            }
            finally { SessionRecorder.Unsubscribe(viewerId); }
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

                // ★ 2026-11-24 P1-A2（落点误差根因）：坐标基准从「主屏尺寸」改为「实际抓屏几何」。
                //   网页 norm() 按抓屏帧（canvas 物理分辨率）归一化到 [0,1]，agent 必须用同一基准反算。
                //   旧实现用 GetSystemMetrics(0/1)（主屏宽高）+ clamp 到主屏，当抓屏后端尺寸/原点 ≠ 主屏时
                //   （DDA 抓第一个枚举输出、WGC 抓前台窗口、多屏主屏原点非 0,0）落点必偏、副屏永远点不到。
                int w, h, ox, oy;
                if (!SessionRecorder.TryGetCaptureGeometry(out w, out h, out ox, out oy))
                {
                    // 兜底：尚未出帧 / 几何未知 → 回退主屏尺寸、原点 (0,0)（等价旧行为，仅保证不 NRE）
                    w = Math.Max(1, GetSystemMetrics(0));
                    h = Math.Max(1, GetSystemMetrics(1));
                    ox = 0; oy = 0;
                }
                bool hasX = dto.x >= 0;
                bool hasY = dto.y >= 0;
                int px = hasX ? ox + (int)Math.Round(dto.x * w) : ox;
                int py = hasY ? oy + (int)Math.Round(dto.y * h) : oy;

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
                    case "noop":    cmd = "noop";    break;
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
                    // ★ clamp 到「抓屏矩形」（虚拟桌面坐标），而非主屏；副屏（原点可为负）也能点到。
                    X = Math.Clamp(px, ox, ox + w - 1),
                    Y = Math.Clamp(py, oy, oy + h - 1),
                    Move = (cmd == "click" && hasX) ? 1 : 0,
                    Button = (cmd == "key" || cmd == "keydown" || cmd == "keyup" || cmd == "text")
                        ? keyName
                        : (dto.button ?? "left"),
                    Dx = dto.dx,
                    Dy = dto.dy,
                    Delta = dto.delta,
                    Count = dto.count
                };

                var resp = RelayAgent.ExecuteWeb(req);
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
