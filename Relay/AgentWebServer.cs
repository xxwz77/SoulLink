using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
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
        private static readonly ConcurrentDictionary<HttpListenerResponse, byte> ActiveStreams = new();
        private static readonly ConcurrentDictionary<WebSocket, byte> ActiveInputs = new();

        internal static void CloseRemoteConnections()
        {
            foreach (var response in ActiveStreams.Keys) try { response.Abort(); } catch { }
            foreach (var socket in ActiveInputs.Keys) try { socket.Abort(); } catch { }
        }

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
        /// ★ P0 传输层（2026-11-24）：/stream 流协议版本。
        ///   2 = /stream?v=2 使用「4 字节大端长度前缀 + 帧数据」帧边界语义；
        ///   旧客户端不传 v（或 v=1）继续走裸 Annex-B 字节流（兼容 Android / 旧网页）。
        /// </summary>
        private const int StreamProtocolVersion = 2;

        /// <summary>按需 IDR 限流：上次成功请求关键帧的时刻（Environment.TickCount64 毫秒）。</summary>
        private static long _lastIdrAtMs;
        private const long IdrMinIntervalMs = 300;   // 两次 IDR 请求最小间隔（Moonlight 教训：狂请求会拥塞崩溃）

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

            if (!SessionRecorder.RemoteControlEnabled && path != "/api/info")
            {
                await WriteText(ctx, 403, "网页远控已关闭").ConfigureAwait(false);
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
                    remoteControlEnabled = SessionRecorder.RemoteControlEnabled,
                    // ★ P0 传输层：流协议版本。网页端据此决定 /stream 是否带 v=2（帧边界）。
                    streamVersion = StreamProtocolVersion,
                    // ★ 当前实际生效的抓屏后端（DXGI-DDA / WGC / BitBlt），网页端用于「配置的 vs 实际」对照显示。
                    captureBackend = SessionRecorder.CaptureBackend,
                    captureWidth = geo ? geoW : 0,
                    captureHeight = geo ? geoH : 0,
                    captureOriginX = geo ? geoX : 0,
                    captureOriginY = geo ? geoY : 0,
            // ★★ 2026-11-24（T34）：暴露**实测**推送帧率，与配置帧率区分 ★★
            //   用户看到浏览器里 `fps: 66.0 (render)` 会以为「推送 66fps」，
            //   但那是**浏览器渲染帧率**（requestAnimationFrame/合成器），
            //   与「服务端实际推了多少帧」是两件事：
            //     · 渲染 fps 可以高于接收 fps（浏览器重复绘制同一帧）；
            //     · 接收 fps 才反映服务端真实产出能力。
            //   这里给出服务端侧的两个真实数字，供页面区分显示：
            //     configuredFps = 用户配置的目标帧率（StreamFps/RecordFps 里当前生效的那个）
            //     actualFps     = 最近 Ring 内真实帧的时间跨度算出的**实测**帧率
            configuredFps = SessionRecorder.CurrentFps,
            actualFps = SessionRecorder.MeasuredFps
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

            if (path == "/api/idr" && req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleIdrAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (path == "/ws/input")
            {
                await HandleInputWebSocketAsync(ctx).ConfigureAwait(false);
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

            if (!SessionRecorder.RemoteControlEnabled)
            {
                await WriteText(ctx, 403, "网页远控已关闭").ConfigureAwait(false);
                return;
            }
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
            {
                await WriteText(ctx, SessionRecorder.RemoteControlEnabled ? 503 : 403, error).ConfigureAwait(false);
                return;
            }
            ActiveStreams.TryAdd(ctx.Response, 0);
            try
            {
                if (initial == null)
                {
                    var wait = System.Diagnostics.Stopwatch.StartNew();
                    // 软件 MFT 可能先缓冲若干输入帧才产生 IDR；等待期不占用编码线程。
                    long lastNudge = -1000;   // ★ P1 加固：距上次主动请求 IDR 的毫秒，防高频重复
                    while (initial == null && SessionRecorder.HasViewer(viewerId) && wait.ElapsedMilliseconds < 25000)
                    {
                        initial = SessionRecorder.ReadViewerFrame(viewerId);
                        if (initial == null)
                        {
                            // ★ P1 加固：静止起播时 worker 可能因静止去重不再喂帧，MF 凑不齐输入帧
                            //   永远不出 IDR。这里每 500ms 主动再触发一次按需 IDR（双保险），让 worker
                            //   即使画面静止也强制编码下一帧，不依赖画面变化。
                            if (wait.ElapsedMilliseconds - lastNudge >= 500)
                            {
                                // ★ 2026-11-24（T26）：本循环已有 500ms 闸门，用不节流版本，
                                //   否则会被 RequestKeyFrame 的 200ms 节流挡掉而「看起来发了其实没发」。
                                SessionRecorder.RequestKeyFrameUnthrottled();
                                lastNudge = wait.ElapsedMilliseconds;
                            }
                            await SessionRecorder.WaitForFrameAsync(viewerId, 500).ConfigureAwait(false);
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
                if (!SessionRecorder.HasViewer(viewerId))
                {
                    await WriteText(ctx, 403, "网页远控已关闭").ConfigureAwait(false);
                    return;
                }
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "video/h264";
                ctx.Response.Headers["Cache-Control"] = "no-store, no-cache";
                ctx.Response.SendChunked = true;
                ctx.Response.KeepAlive = true;
                var stream = ctx.Response.OutputStream;
                // ★ P0 传输层（2026-11-24）：帧边界语义。/stream?v=2 时每帧前加 4 字节大端长度前缀，
                //   网页端按长度切帧不再扫起始码（网络抖动下起始码扫描会切错帧致花屏/卡死）。
                bool framed = string.Equals(ctx.Request.QueryString["v"], "2", StringComparison.Ordinal);
                async Task WriteFrameAsync(byte[] data)
                {
                    if (framed) await WriteFramedAsync(stream, data).ConfigureAwait(false);
                    else await stream.WriteAsync(data).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                int frames = 0;
                try
                {
                    await WriteFrameAsync(initial).ConfigureAwait(false);
                    frames++;
                    while (SessionRecorder.HasViewer(viewerId))
                    {
                        byte[]? nal = SessionRecorder.ReadViewerFrame(viewerId);
                        if (nal != null)
                        {
                            await WriteFrameAsync(nal).ConfigureAwait(false);
                            frames++;
                        }
                        // ★ P0（2026-11-24）：事件驱动等帧替代 Task.Delay(10) 轮询。
                        //   帧入队即被 SessionRecorder.WaitForFrame 的内核信号唤醒，消除每帧平均 +5ms（最坏 +10ms）轮询延迟税；
                        //   真阻塞（零 CPU 空转）。1000ms 兜底超时仅覆盖 worker 异常停止的边界（正常有观众时 500ms 保活帧必醒）。
                        else await SessionRecorder.WaitForFrameAsync(viewerId, 1000).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { LogFailure("[stream] 客户端断开 frames=" + frames + "：" + ex); }
                finally { try { ctx.Response.Close(); } catch (Exception ex) { Log("[stream] 关闭响应异常：" + ex.Message); } }
            }
            finally
            {
                ActiveStreams.TryRemove(ctx.Response, out _);
                SessionRecorder.Unsubscribe(viewerId);
            }
        }

        /// <summary>
        /// 写一帧：4 字节大端长度前缀 + 帧数据（帧边界语义，/stream?v=2）。
        /// 长度前缀每帧一个独立 4 字节缓冲（多 viewer 并发写不能用共享静态缓冲，会互相覆盖）。
        /// </summary>
        private static async Task WriteFramedAsync(Stream stream, byte[] data)
        {
            byte[] len = new byte[4];
            len[0] = (byte)(data.Length >> 24);
            len[1] = (byte)(data.Length >> 16);
            len[2] = (byte)(data.Length >> 8);
            len[3] = (byte)data.Length;
            await stream.WriteAsync(len, 0, 4).ConfigureAwait(false);
            await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
        }

        /// <summary>
        /// ★ P0 传输层（2026-11-24）：按需 IDR。POST /api/idr → SessionRecorder.RequestKeyFrame()。
        /// 前端解码失败/花屏时先请求 IDR 而非断流重连（重连代价极大：新订阅 + 等按需 IDR + 首帧）。
        /// ★ 必须限流：Moonlight 教训——网络不稳时前端会狂请求 IDR，不限流会把
        ///   RequestKeyFrame（置位 _forceKeyFrame + 唤醒 worker）打成风暴 → 编码器反复重建拥塞。
        ///   这里服务端按「距上次 ≥ IdrMinIntervalMs」限流（客户端也有一层，双保险）。
        /// </summary>
        private static async Task HandleIdrAsync(HttpListenerContext ctx)
        {
            try
            {
                long now = Environment.TickCount64;
                long last = Interlocked.Read(ref _lastIdrAtMs);
                if (now - last < IdrMinIntervalMs)
                {
                    await WriteJson(ctx, new { ok = false, error = "限流：请稍后再请求关键帧" }).ConfigureAwait(false);
                    return;
                }
                Interlocked.Exchange(ref _lastIdrAtMs, now);
                // ★ 2026-11-24（T26）：本端点已自带 300ms 限流（IdrMinIntervalMs），
                //   走**不节流**版本，避免与 RequestKeyFrame 内部的 200ms 节流叠加后
                //   出现「HTTP 返回 ok=true，实际却没置位」的谎言。
                SessionRecorder.RequestKeyFrameUnthrottled();
                await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteJson(ctx, new { ok = false, error = ex.Message }).ConfigureAwait(false);
            }
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
                await WriteJson(ctx, ExecuteInput(body)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteJson(ctx, new { ok = false, error = ex.Message }).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 把一条输入指令的 JSON 文本解析并执行，返回给客户端的结果对象（HTTP 与 WebSocket 共用）。
        /// 返回 { ok, error } 匿名对象；调用方（WriteJson / WebSocket 文本回写）负责序列化。
        /// </summary>
        private static object ExecuteInput(string body)
        {
            if (!SessionRecorder.RemoteControlEnabled) return new { ok = false, error = "网页远控已关闭" };
            var dto = System.Text.Json.JsonSerializer.Deserialize<InputDto>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto == null) return new { ok = false, error = "请求格式错误" };

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
                // Web page lifecycle release must reach the same agent implementation as other inputs.
                case "releaseall": cmd = "releaseall"; break;
                case "move":    cmd = "move";    break;
                case "wheel":   cmd = "wheel";   break;
                case "key":     cmd = "key";     break;
                case "keydown": cmd = "keydown"; break;
                case "keyup":   cmd = "keyup";   break;
                case "moverel": cmd = "moverel"; break;
                case "text":    cmd = "text";    break;
                case "noop":    cmd = "noop";    break;
                // ★★ FIX-2026-11-24（KEYBOARD-TOGGLE-SYNC）：键盘三锁状态读取 / 同步。
                //   注意：这里是**独立于 RelayAgent.Execute 的白名单**，两处都要加，
                //   否则网页/手机调用会得到「未知动作」而桌面管道能用（很容易漏）。
                case "keystate": cmd = "keystate"; break;
                case "keysync":  cmd = "keysync";  break;
                default:
                    return new { ok = false, error = "未知动作 " + dto.action };
            }

            // key / keydown / keyup 复用 key 字段；text 走 text 字段；
            // click / down / up 走 button 字段（与老网页语义一致）。
            string keyName = cmd == "text" ? (dto.text ?? "") : (dto.key ?? "");
            // ★ keysync 的目标状态也走 key 字段（传值形如 "caps=1,num=0,scroll=1" 或 "1,0,1"）。
            var req = new RelayAgent.Request
            {
                Cmd = cmd,
                // ★ clamp 到「抓屏矩形」（虚拟桌面坐标），而非主屏；副屏（原点可为负）也能点到。
                X = Math.Clamp(px, ox, ox + w - 1),
                Y = Math.Clamp(py, oy, oy + h - 1),
                // ★ 2026-11-24 修复（AUDIT-20261124-17）：down/up 带坐标时同样定位，
                //   否则网页 pointerup 发的最终坐标被丢弃，up 落在最后一次 8ms 节流 move 的旧位置。
                Move = ((cmd == "click" || cmd == "down" || cmd == "up") && hasX) ? 1 : 0,
                Button = (cmd == "key" || cmd == "keydown" || cmd == "keyup" || cmd == "text"
                          || cmd == "keysync" || cmd == "keystate")
                    ? keyName
                    : (dto.button ?? "left"),
                // RelayAgent.Request uses integer relative deltas; retain fractional sensitivity
                // remainder client-side and round only whole accumulated units here.
                Dx = (int)Math.Truncate(dto.dx),
                Dy = (int)Math.Truncate(dto.dy),
                Delta = dto.delta,
                Count = dto.count
            };

            var resp = RelayAgent.ExecuteWeb(req);
            // ★ P0 输入触发（2026-11-24）：注入成功 → 立即安排抓帧（方案 B）。
            //   光标/界面变化是用户预期马上看到的；节流与开关在 NotifyInputActivity 内部。
            //
            //   ★★ 2026-11-24（t24 P0-3）：up / click 必须**无视 50ms 限流** ★★
            //     原实现所有动作一视同仁，而 NotifyInputActivity 内部有
            //     「50ms 内重复调用直接丢弃」（SessionRecorder.cs:1179）。
            //     拖动时 pointermove 每 8ms 一条、每条都刷新 _lastInputTriggerMs，
            //     于是**松手那一帧的触发极可能落进 50ms 窗口被丢弃** ——
            //     远端其实已经松开了，但**画面没有刷新**，
            //     用户看到的正是「判定我没松开、要等很久才落位」。
            //
            //     up / releaseall / click 是用户唯一能直接感知的操作终点，
            //     它们必须无条件刷新一次画面（代价只是多编一帧）。
            bool urgent = cmd == "up" || cmd == "releaseall" || cmd == "click";
            if (resp.Ok)
            {
                SessionRecorder.NotifyInputActivity(urgent);
                // ★★ 2026-11-24（T26 第④处）：urgent 动作必须**同时升级为关键帧** ★★
                //
                //   【为什么只抓帧不够】
                //     NotifyInputActivity 只是置 _inputActivityPending → worker 抓一帧，
                //     但编出来的是**普通 P 帧（delta）**。
                //     而客户端在 firstKey === false 状态（web-index.html:1536）时
                //     **只接受 key/SPS/PPS，丢弃所有 delta**。
                //   ⇒ 服务端「抓了帧、也发了」，客户端「收到、但整帧丢弃」，
                //     用户看到的仍然是「松手没反应」。
                //     这正是用户说的「输出操作会触发关键帧，但没有松开这个触发」。
                //
                //   【为什么是 urgent 而不是全部】
                //     up/releaseall/click 发生在「画面即将静止」的时刻（松手后桌面不再变化），
                //     也是客户端最可能处在等 IDR 窗口里的时刻；
                //     而 move 是连续高频的，给它升 IDR 只会打出 IDR 风暴。
                //
                //   【节流】RequestKeyFrame 内部已有 200ms 进程内节流（防止连点/拖拽尾部风暴），
                //     这里直接调用即可；返回 false 表示被节流，属预期，不影响抓帧。
                if (urgent) SessionRecorder.RequestKeyFrame();
            }
            // ★ 2026-11-24 光标回传（一步到位修复「网页没有光标」）：
            //   前端 #scr 设了 cursor:none（防本机影子与帧内光标双影），
            //   因此必须由服务端把**宿主真实光标位置**回传，前端画 rcCursor。
            //   之前只做了前端消费端、服务端从未回传 → 画面区完全无光标（比旧蓝球更糟）。
            //   这里在每条输入响应里附带 cursor{nx,ny,visible}：
            //     nx/ny = 相对抓屏矩形的归一化坐标（与前端 norm() 同一基准）；
            //     visible = 光标是否在抓屏区域内。
            //   ★ 必须在执行完注入之后再读（读到的是注入后的新位置），否则回显永远慢一拍。
            int cw, ch, cox, coy;
            if (!SessionRecorder.TryGetCaptureGeometry(out cw, out ch, out cox, out coy))
            {
                cw = Math.Max(1, GetSystemMetrics(0));
                ch = Math.Max(1, GetSystemMetrics(1));
                cox = 0; coy = 0;
            }
            object cursorInfo = CursorEcho(cw, ch, cox, coy);
            // ★★ FIX-2026-11-24（KEYBOARD-TOGGLE-SYNC）：keystate / keysync 需要把
            //   「读到的三锁状态」回给调用方，而通用响应只带 ok/error/cursor。
            //   这里**只对新动作**追加 state/mask 字段，其它动作的响应体一个字节都不变
            //   （前端与手机端是宽松解析，多字段无影响；老调用方完全无感）。
            if (cmd == "keystate" || cmd == "keysync")
                return new
                {
                    ok = resp.Ok,
                    error = resp.Ok ? null : resp.Message,
                    cursor = cursorInfo,
                    state = resp.Message,           // 人类可读："Caps=ON Num=off Scroll=off → ..."
                    mask = resp.Info                 // "mask=3"
                };
            return new { ok = resp.Ok, error = resp.Ok ? null : resp.Message, cursor = cursorInfo };
        }

        /// <summary>
        /// ★ 2026-11-24：读取宿主真实光标并换算成抓屏矩形内的归一化坐标。
        ///   GetCursorPos 可能失败（跨会话/权限），失败时 visible=false，前端静默不画。
        /// </summary>
        private static object CursorEcho(int w, int h, int ox, int oy)
        {
            try
            {
                if (!GetCursorPos(out POINT pt)) return new { nx = 0.0, ny = 0.0, visible = false };
                // ★ T41e（2026-11-25 用户纠正「光标由游戏决定」）：只判坐标 inside 不够——
                //   3D 游戏全屏时系统隐藏光标（GetCursorInfo 无 CURSOR_SHOWING），
                //   此时 visible 必须 false（客户端不画影子光标）；游戏呼出菜单/2D 场景
                //   系统恢复光标 → visible 自动回 true。服务端跟随系统状态，客户端零判断。
                //   P/Invoke 复用 BitBltCapture 的 internal CURSORINFO/GetCursorInfo（不重复声明）。
                var ci = new BitBltCapture.CURSORINFO();
                ci.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<BitBltCapture.CURSORINFO>();
                bool showing = BitBltCapture.GetCursorInfo(ref ci)
                               && (ci.flags & BitBltCapture.CURSOR_SHOWING) != 0;
                bool inside = pt.X >= ox && pt.X < ox + w && pt.Y >= oy && pt.Y < oy + h;
                double nx = (pt.X - ox) / (double)Math.Max(1, w);
                double ny = (pt.Y - oy) / (double)Math.Max(1, h);
                return new { nx, ny, visible = inside && showing };
            }
            catch { return new { nx = 0.0, ny = 0.0, visible = false }; }
        }

        /// <summary>
        /// ★ P0 传输层（2026-11-24）：输入 WebSocket 持久通道。move/click/wheel/key 走 WS（低延迟 +
        ///   天然保序，顺带解决「按键走一条 FIFO、move 各走一条」的问题），HTTP POST /api/input 保留为降级。
        ///   HttpListener 经 AcceptWebSocketAsync 原生支持 RFC 6455 升级，无需第三方库。
        ///   复用 ExecuteInput，与 HTTP 输入完全同一套语义（同一坐标基准、同一 RelayAgent.Execute）。
        /// </summary>
        private static async Task HandleInputWebSocketAsync(HttpListenerContext ctx)
        {
            if (!ctx.Request.IsWebSocketRequest)
            {
                await WriteText(ctx, 400, "需要 WebSocket 升级").ConfigureAwait(false);
                return;
            }
            // 安全：WebSocket 无 CORS 预检，恶意站可对无口令代理发起跨站 WS 劫持（CSWSH）。
            // 显式校验 Origin（同主机/本机回环才放行），口径与 ResolveCorsOrigin 一致。
            string? origin = ctx.Request.Headers["Origin"];
            if (!string.IsNullOrWhiteSpace(origin) && ResolveCorsOrigin(ctx) == null)
            {
                await WriteText(ctx, 403, "来源不被允许").ConfigureAwait(false);
                return;
            }
            try
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
                using var ws = wsCtx.WebSocket;
                ActiveInputs.TryAdd(ws, 0);
                try
                {
                    var buffer = new byte[64 * 1024];
                    while (SessionRecorder.RemoteControlEnabled && ws.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult result;
                        using (var ms = new MemoryStream())
                        {
                            do
                            {
                                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);
                                ms.Write(buffer, 0, result.Count);
                            } while (!result.EndOfMessage);

                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); } catch { }
                                break;
                            }
                            if (result.MessageType != WebSocketMessageType.Text) continue;   // 只接受文本 JSON 指令
                            if (!SessionRecorder.RemoteControlEnabled) break;

                            string body = Encoding.UTF8.GetString(ms.ToArray());
                            object respObj;
                            try { respObj = ExecuteInput(body); }
                            catch (Exception ex) { respObj = new { ok = false, error = ex.Message }; }
                            string respJson = System.Text.Json.JsonSerializer.Serialize(respObj);
                            var respBytes = Encoding.UTF8.GetBytes(respJson);
                            await ws.SendAsync(new ArraySegment<byte>(respBytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                finally { ActiveInputs.TryRemove(ws, out _); }
            }
            catch (Exception ex)
            {
                LogFailure("[ws/input] WebSocket 异常：" + ex);
            }
        }

        /// <summary>网页传来的输入请求（坐标为相对值）。</summary>
        private sealed class InputDto
        {
            public string? action { get; set; }
            public double x { get; set; } = -1;
            public double y { get; set; } = -1;
            // Relative pointer movement may be fractional after sensitivity scaling.
            public double dx { get; set; }
            public double dy { get; set; }
            public string? button { get; set; }
            public string? key { get; set; }
            public string? text { get; set; }
            public int delta { get; set; }
            public int count { get; set; } = 1;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        // ★ 2026-11-24：光标回传用（读宿主真实光标位置，前端画 rcCursor）
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out POINT lpPoint);

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
