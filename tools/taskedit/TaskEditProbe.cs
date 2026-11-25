using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// /taskedit/health 与 /taskedit/rebind（主程序探活与上游热切换）。
    /// 单独成文件控制 <see cref="TaskEditServer"/> 的体积。
    /// </summary>
    internal sealed partial class TaskEditServer
    {
        /// <summary>处理探活/热切换。返回 true = 已处理（调用方直接 return）。</summary>
        private async Task<bool> TryHandleProbeAsync(HttpListenerContext ctx, string path, CancellationToken ct)
        {
        if (path.Equals("/taskedit/health", StringComparison.OrdinalIgnoreCase))
        {
            bool trusted = _key.Matches(ctx);
            if (trusted)
            {
                await WriteJsonAsync(ctx, "{\"ok\":true,\"service\":\"SoulLink.TaskEdit\",\"port\":" +
                    _config.WebPort + ",\"backend\":" + (_proxy.HasBackend ? "true" : "false") +
                    ",\"internalPort\":" + _proxy.InternalPort +
                    ",\"pid\":" + Environment.ProcessId + ",\"trusted\":true}").ConfigureAwait(false);
            }
            else
            {
                // 最小信息：服务活着 + 对外端口 + 是否有上游（布尔），**不含 internalPort / pid**
                await WriteJsonAsync(ctx, "{\"ok\":true,\"service\":\"SoulLink.TaskEdit\",\"port\":" +
                    _config.WebPort + ",\"backend\":" + (_proxy.HasBackend ? "true" : "false") +
                    ",\"trusted\":false}").ConfigureAwait(false);
            }
            return true;
        }
        
        // ★ 内部端口热切换：主程序重启后内部端口会变，本服务被要求改指向。
        //
        //   ★★ 两层防御（Lead 2026-11-25 要求，缺一不可）★★
        //     ① 来源必须回环 —— 远程绝不能改我们的上游指向（即使 key 泄露也不行）
        //     ② 必须带正确的 X-TaskEdit-Key —— 回环只证明"同机"，不证明"是我们的主程序"。
        //        本机任何普通权限进程都能从回环调用，没有 key 就能把上游指向它自己的服务。
        //   状态码区分：403 = 不是本机（来源问题）；401 = 缺 key / key 错（身份问题）。
        if (path.Equals("/taskedit/rebind", StringComparison.OrdinalIgnoreCase))
        {
            if (!AuthGate.IsLoopback(ctx))
            {
                await WriteTextAsync(ctx, 403, "只允许本机调用").ConfigureAwait(false);
                return true;
            }
            if (!_key.Matches(ctx))
            {
                await WriteTextAsync(ctx, 401, "缺少或错误的 " + ServiceKey.HeaderName).ConfigureAwait(false);
                return true;
            }
            bool post = ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
            if (!post)
            {
                await WriteTextAsync(ctx, 405, "请用 POST").ConfigureAwait(false);
                return true;
            }
            int port = 0;
            string q = ctx.Request.QueryString["internalPort"] ?? "";
            if (!int.TryParse(q, out port))
            {
                // 也接受 body 里的 {"internalPort": N}
                try
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding,
                        false, 1024, true);
                    string body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    int at = body.IndexOf("internalPort", StringComparison.OrdinalIgnoreCase);
                    if (at >= 0)
                    {
                        int colon = body.IndexOf(':', at);
                        if (colon > 0)
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(body.Substring(colon + 1), @"\d{2,5}");
                            if (m.Success) int.TryParse(m.Value, out port);
                        }
                    }
                }
                catch { }
            }
            if (port < 1024 || port > 65535)
            {
                await WriteTextAsync(ctx, 400, "internalPort 无效").ConfigureAwait(false);
                return true;
            }
            _proxy.Rebind(port);
            Console.WriteLine("[TaskEdit] 上游已切换到 127.0.0.1:" + port + "（主程序重启后复用本实例）");
            await WriteJsonAsync(ctx, "{\"ok\":true,\"internalPort\":" + port + "}").ConfigureAwait(false);
            return true;
        }

        return false;
        }
    }
}