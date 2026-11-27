using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace SoulLink.Services;

public static partial class WebServer
{
    private static async Task HandleBgiEditor(HttpListenerContext ctx, string path)
    {
        ctx.Response.Headers["Cache-Control"] = "no-store";
        var req = ctx.Request;
        bool status = path.EndsWith("/status", StringComparison.Ordinal);
        try
        {
            if (req.HttpMethod != (status ? "GET" : "POST")) throw new EditorFault(405, "请求方法错误");
            string account, name = "";
            if (status)
            {
                if (req.QueryString.Count != 1 || req.QueryString.GetValues("account") is not [var user] || req.HasEntityBody)
                    throw new EditorFault(400, "须提供唯一的账号");
                account = user;
            }
            else
            {
                if (req.QueryString.Count != 0) throw new EditorFault(400, "控制接口不接受查询参数");
                if (!EditorSameOrigin(ctx)) throw new EditorFault(403, "须从同源编辑器操作");
                if (!string.Equals(req.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                    throw new EditorFault(415, "须提交 JSON");
                if (req.ContentLength64 is <= 0 or > 4096) throw new EditorFault(400, "请求长度无效");
                byte[] bytes = new byte[(int)req.ContentLength64];
                await req.InputStream.ReadExactlyAsync(bytes);
                (account, name) = ReadEditorCommand(bytes);
                if ((!path.EndsWith("/stop", StringComparison.Ordinal) || name.Length > 0) && !BgiEditorRuntime.SafeName(name))
                    throw new EditorFault(400, "一条龙名称无效");
            }
            var target = BgiEditorRuntime.Resolve(account);
            if (!status && target.SessionId < 0) throw new EditorFault(409, "账号未登录，可编辑配置；启动或停止 BGI 需要会话");
            using var lease = status ? null : target.Lock();
            target.RequireUnchanged();
            using var process = target.Inspect();
            bool running = process != null;
            if (!status && path.EndsWith("/stop", StringComparison.Ordinal))
            {
                if (process == null) throw new EditorFault(409, "BGI 已停止");
                await target.Stop(process);
                running = false;
            }
            else if (!status)
            {
                if (running) throw new EditorFault(409, "请先停止当前 BGI");
                var result = await SystemLauncher.LaunchEditorDragonAsync(target, name);
                if (!result.Ok || result.SessionMismatch || result.Pid <= 0) throw new EditorFault(503, "未能确认 BGI 启动，请刷新状态");
                target.RequireUnchanged();
                using var launched = target.Inspect();
                if (launched?.Id != result.Pid) throw BgiEditorRuntime.Unknown();
                running = true;
            }
            string binding = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(target.Exe.ToUpperInvariant())));
            await WriteJson(ctx, new { ok = true, account = target.User, binding, state = running ? "running" : "stopped" });
        }
        catch (Exception ex)
        {
            var err = ex as EditorFault ?? (ex is JsonException ? new EditorFault(400, "JSON 无效") : BgiEditorRuntime.Unknown());
            await WriteJson(ctx, new { ok = false, state = "unknown", error = err.Message }, err.Status);
        }
    }

    internal static (string Account, string Name) ReadEditorCommand(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var obj = doc.RootElement;
        if (obj.ValueKind != JsonValueKind.Object || obj.EnumerateObject().Count() != 2 ||
            !obj.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.String ||
            !obj.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            throw new EditorFault(400, "仅接受 account 与 name 字符串");
        return (account.GetString()!, name.GetString()!);
    }

    private static bool EditorSameOrigin(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        string? expected = req.Url?.GetLeftPart(UriPartial.Authority);
        if (IsLoopbackRequest(ctx) && req.Headers["X-Forwarded-Host"] is string host)
            expected = req.Headers["X-Forwarded-Proto"] + "://" + host;
        return BgiEditorRuntime.Equal(req.Headers["Origin"], expected) && expected != null &&
            req.Headers["Sec-Fetch-Site"] is null or "same-origin";
    }
}
