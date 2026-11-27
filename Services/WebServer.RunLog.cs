using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace SoulLink.Services;

public static partial class WebServer
{
    private static async Task<object> ApiBgiRunLogAsync(HttpListenerContext ctx)
    {
        var q = ctx.Request.QueryString;
        string? user = q["user"], runId = q["runId"], routeId = q["routeId"], day = q["day"];
        if (ctx.Request.HttpMethod != "GET" || string.IsNullOrWhiteSpace(user) || user.Length > 256 ||
            string.IsNullOrWhiteSpace(runId) || runId.Length > 4096 || routeId is { Length: 0 or > 8192 } ||
            !DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            date.Year is < 2000 or > 2999)
            return new { ok = false, error = "请指定账号、运行 ID 和有效日志日期" };

        string file = "better-genshin-impact" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";
        if (q["srcFile"] is string requested && !string.Equals(file, requested, StringComparison.Ordinal))
            return new { ok = false, error = "文件来源与日期不符" };
        if (!int.TryParse(q["offset"] ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out int offset) || offset > 1000000 ||
            !int.TryParse(q["limit"] ?? "100", NumberStyles.None, CultureInfo.InvariantCulture, out int limit) || limit is < 1 or > 200)
            return new { ok = false, error = "分页参数超出范围" };

        var rows = await RunRecordStore.ReadRunLogAsync(user, runId, routeId, day!, file,
            new DateTimeOffset(date).ToUnixTimeMilliseconds(), new DateTimeOffset(date.AddDays(1)).ToUnixTimeMilliseconds(),
            offset, limit).ConfigureAwait(false);
        if (rows == null) return new { ok = false, error = "日志索引暂不可用" };
        return new { ok = true, items = rows.Take(limit), hasMore = rows.Count > limit };
    }
}
