using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SoulLink.Server;

/// <summary>
/// SoulLink 联机大厅服务端。
///
/// 部署：Ubuntu 上运行 SoulLink.Server（自包含发布，不需要装 .NET）
/// 数据：同目录 soul-link.db（SQLite 单文件）
/// 身份：请求头 X-Device-Code 传设备码
///
/// 所有接口都在 /api/ 下，返回 JSON。
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // 端口：默认 5080，可用 --urls 或环境变量覆盖
        builder.WebHost.UseUrls(
            Environment.GetEnvironmentVariable("SOULLINK_URLS") ?? "http://0.0.0.0:5080");

        // 数据库放程序目录
        string dbPath = Path.Combine(AppContext.BaseDirectory, "soul-link.db");
        var db = new Db(dbPath);
        var lobby = new Lobby(db);

        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(lobby);

        var app = builder.Build();

        // ── 启动时清一次闲置房间（3 天）──
        try
        {
            int n = db.PurgeIdleRooms(3);
            if (n > 0)
                app.Logger.LogInformation("启动清理：删除 {N} 个闲置超过 3 天的房间", n);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "启动清理失败");
        }

        // ══════════ 健康检查 ══════════

        app.MapGet("/api/ping", () => Results.Json(new
        {
            ok = true,
            time = DateTime.UtcNow,
            version = typeof(Program).Assembly.GetName().Version?.ToString() ?? ""
        }));

        // ══════════ 设备注册 ══════════

        app.MapPost("/api/device/register", (HttpRequest req, Db d) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));

            string name = req.Query["name"].ToString();
            bool isNew = d.UpsertDevice(code, name);

            return Results.Json(new
            {
                ok = true,
                isNew,
                message = isNew ? "已注册新设备" : "已更新设备"
            });
        });

        // ══════════ 房间 ══════════

        // 列表
        app.MapGet("/api/rooms", (HttpRequest req, Lobby l) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));

            return Results.Json(l.ListRooms(code));
        });

        // 详情
        app.MapGet("/api/rooms/{id}", (string id, HttpRequest req, Lobby l) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));

            var r = l.GetRoom(id, code);
            return r == null ? Results.NotFound(ApiResult.Bad("房间不存在")) : Results.Json(r);
        });

        // 建私人房（发车人数 2-4 可选）
        app.MapPost("/api/rooms/create", (CreateRoomRequest body, HttpRequest req, Lobby l) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));

            var (ok, msg, room) = l.CreateFour(body.Title, body.Code, code, body.DepartSize);
            return Results.Json(new { ok, message = msg, room });
        });

        // 建大小号房（成对，每边发车人数 2-4 可选）
        app.MapPost("/api/rooms/create-pair", (CreatePairRequest body, HttpRequest req, Lobby l) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));

            var (ok, msg, big, small) = l.CreatePair(body.Title, body.BigCode, body.SmallCode, code, body.BigSize, body.SmallSize);
            return Results.Json(new { ok, message = msg, big, small });
        });

        // 加入（房间码即通行证）
        app.MapPost("/api/rooms/join", (JoinRequest body, HttpRequest req, Lobby l) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));

            var (ok, msg, room) = l.Join(body.Code, code, body.DisplayName, body.RoleName);
            return Results.Json(new { ok, message = msg, room });
        });

        // 离开
        app.MapPost("/api/rooms/{id}/leave", (string id, HttpRequest req, Lobby l) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));

            return Results.Json(ApiResult.Good(l.Leave(id, code) ? "已离开" : "不在该房间"));
        });

        // 发车
        app.MapPost("/api/rooms/depart", (DepartRequest body, HttpRequest req, Lobby l, Db d) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));

            var (ok, msg, targets) = l.Depart(body.RoomId);
            if (!ok) return Results.Json(new { ok, message = msg, targets });

            // 发车后自动清理（成员下线）
            l.DepartAndClear(body.RoomId);
            d.TouchDevice(code);

            return Results.Json(new { ok, message = msg, targets });
        });

        // 改房间码
        app.MapPost("/api/rooms/change-code", (ChangeCodeRequest body, HttpRequest req, Lobby l) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));

            var (ok, msg) = l.ChangeCode(body.RoomId, body.NewCode, code);
            return Results.Json(new { ok, message = msg });
        });

        // 关闭房间
        app.MapDelete("/api/rooms/{id}", (string id, HttpRequest req, Lobby l) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));

            var (ok, msg) = l.Delete(id, code);
            return Results.Json(new { ok, message = msg });
        });

        // ══════════ 发车动作配置 ══════════

        app.MapGet("/api/depart-config", (HttpRequest req, Lobby l) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));
            return Results.Json(l.GetDepartConfig(code));
        });

        app.MapPost("/api/depart-config", (DepartConfigDto body, HttpRequest req, Lobby l) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));

            l.SetDepartConfig(code, body);
            return Results.Json(ApiResult.Good("已保存"));
        });

        // ══════════ 心跳 ══════════

        // 客户端定期调用；同时顺带清理掉线座位
        app.MapPost("/api/heartbeat", (HttpRequest req, Lobby l) =>
        {
            string code = GetDevice(req);
            if (code.Length == 0) return Results.BadRequest(ApiResult.Bad("缺少设备码"));

            l.Heartbeat(code);
            int expired = l.ExpireStale();

            return Results.Json(new { ok = true, expired });
        });

        // ══════════ 定时清理（每小时）══════════

        var timer = new Timer(_ =>
        {
            try
            {
                int n = db.PurgeIdleRooms(3);
                if (n > 0) app.Logger.LogInformation("定时清理：删除 {N} 个闲置房间", n);

                // 掉线座位也要清
                int exp = lobby.ExpireStale();
                if (exp > 0) app.Logger.LogInformation("掉线清理：{N} 个座位已下线", exp);
            }
            catch { }
        }, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        app.Lifetime.ApplicationStopping.Register(() => timer.Dispose());

        Console.WriteLine("SoulLink 服务端启动：http://0.0.0.0:5080");
        Console.WriteLine("数据库：" + dbPath);

        app.Run();
    }

    /// <summary>从请求头取设备码。</summary>
    private static string GetDevice(HttpRequest req)
    {
        return req.Headers[Hdr.Device].ToString().Trim();
    }
}
