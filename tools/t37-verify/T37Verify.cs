using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Services;

internal static class T37Verify
{
    /// <summary>包装 handler：克隆请求 → 统计同名头出现次数 + dump 全部头（根因验证用）。</summary>
    private sealed class CountingHandler : DelegatingHandler
    {
        public readonly Dictionary<string,int> Counts = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Dump = new();
        public CountingHandler() : base(new HttpClientHandler { UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All }) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Counts.Clear(); Dump.Clear();
            foreach (var h in request.Headers)
            {
                int n = h.Value.Count();
                Counts[h.Key] = n;
                Dump.Add(h.Key + ": " + string.Join(" | ", h.Value));
            }
            return base.SendAsync(request, ct);
        }
    }

    private static int _fail;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name);
        if (!ok) _fail++;
    }

    public static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var handler = new CountingHandler();
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        var client = new MiyousheClient(null, http, "cn");

        Console.WriteLine("==== T37-1 真实 createQRLogin：retcode=0 + ticket/url ====");
        try
        {
            var (ticket, url) = await client.CreateQrAsync(default).ConfigureAwait(false);
            Console.WriteLine("  ticket=" + ticket.Substring(0, Math.Min(24, ticket.Length)) + "…");
            Console.WriteLine("  url=" + url.Substring(0, Math.Min(60, url.Length)) + "…");
            Check(ticket.Length > 10, "拿到 ticket（非空）");
            Check(url.StartsWith("http"), "拿到 url");
        }
        catch (Exception ex)
        {
            Check(false, "CreateQrAsync 抛异常：" + ex.Message);
        }

        Console.WriteLine("==== T37-2 请求头 dump：x-rpc-app_id 只出现一次 ====");
        foreach (var line in handler.Dump) Console.WriteLine("    " + line);
        Check(handler.Counts.TryGetValue("x-rpc-app_id", out int n) && n == 1,
            "x-rpc-app_id 恰好 1 个（实际=" + n + "，修复前=2 → -3005）");

        Console.WriteLine("==== T37-3 PollQr 头形状（未扫状态，只验头不验凭据）====");
        try
        {
            await client.PollQrAsync("t37-probe-ticket", "_t37_probe_", default).ConfigureAwait(false);
        }
        catch { /* 未扫码 ticket → 预期返回非 Confirmed 状态或异常，不作为断言 */ }
        foreach (var line in handler.Dump) Console.WriteLine("    " + line);
        Check(handler.Counts.TryGetValue("x-rpc-app_id", out int n2) && n2 == 1,
            "PollQr 路径 x-rpc-app_id 也恰 1 个（实际=" + n2 + "）");

        Console.WriteLine(_fail == 0 ? "T37-ALL-PASS" : "T37-FAILURES=" + _fail);
        return _fail == 0 ? 0 : 1;
    }
}
