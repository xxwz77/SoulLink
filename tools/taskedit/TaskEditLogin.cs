using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 登录（P0：口令 → 会话 Cookie）。
    /// 单独成文件是为了让 <see cref="TaskEditServer"/> 保持在单文件行数预算内；
    /// 逻辑上仍是同一个类（partial）。
    /// </summary>
    internal sealed partial class TaskEditServer
    {
        private async Task HandleLoginAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            bool post = ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
            string supplied = "";
            if (post)
            {
                try
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding,
                        false, 2048, true);
                    char[] buf = new char[2049];
                    int n = await reader.ReadBlockAsync(buf, 0, 2048).ConfigureAwait(false);
                    supplied = ExtractPassword(new string(buf, 0, n), ctx.Request.ContentType ?? "");
                }
                catch { }
            }
            supplied = supplied.Length > 0 ? supplied : (ctx.Request.QueryString["pw"] ?? "");

            string err = _auth.TryLogin(ctx, supplied);
            if (err == null)
            {
                _auth.IssueCookie(ctx);
                await WriteJsonAsync(ctx, "{\"ok\":true}").ConfigureAwait(false);
                return;
            }
            if (post)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                byte[] bytes = Encoding.UTF8.GetBytes(Pages.Login(err));
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                ctx.Response.Close();
                return;
            }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            byte[] form = Encoding.UTF8.GetBytes(Pages.Login(""));
            ctx.Response.ContentLength64 = form.Length;
            await ctx.Response.OutputStream.WriteAsync(form, 0, form.Length, ct).ConfigureAwait(false);
            ctx.Response.Close();
        }

        /// <summary>从表单（pw=...）或 JSON（{"pw":"..."}）里取出候选口令。</summary>
        internal static string ExtractPassword(string body, string contentType)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            if (contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int i = body.IndexOf("\"pw\"", StringComparison.OrdinalIgnoreCase);
                if (i < 0) i = body.IndexOf("\"password\"", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return "";
                int colon = body.IndexOf(':', i);
                if (colon < 0) return "";
                int q1 = body.IndexOf('"', colon + 1);
                if (q1 < 0) return "";
                int q2 = body.IndexOf('"', q1 + 1);
                return q2 > q1 ? body.Substring(q1 + 1, q2 - q1 - 1) : "";
            }
            foreach (string part in body.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq).Trim();
                if (!key.Equals("pw", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("password", StringComparison.OrdinalIgnoreCase)) continue;
                return Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
            }
            return "";
        }
    }
}
