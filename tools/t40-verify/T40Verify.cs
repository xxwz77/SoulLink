using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Services;

internal static class T40Verify
{
    private sealed class CaptureHandler : DelegatingHandler
    {
        public string LastDeviceId = "";
        public int DeviceIdCount;
        public CaptureHandler() : base(new HttpClientHandler { UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All }) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            DeviceIdCount = 0; LastDeviceId = "";
            if (request.Headers.TryGetValues("x-rpc-device_id", out var values))
                foreach (var v in values) { DeviceIdCount++; LastDeviceId = v; }
            return base.SendAsync(request, ct);
        }
    }

    private static int _fail;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name);
        if (!ok) _fail++;
    }

    private static string Mask(string s) => s.Length <= 8 ? "<masked>" : s.Substring(0, 8) + "…";

    public static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        string dataDir = Path.Combine(Path.GetTempPath(), "t40-verify-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("SOULLINK_DATA_DIR", dataDir);
        var handler = new CaptureHandler();
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        Console.WriteLine("==== T40-1 同一账号两次 CreateQr：device_id 相同 ====");
        var client = new MiyousheClient(null, http, "cn");
        var t1 = await client.CreateQrAsync(default, await MiyousheCredentialStore.GetOrCreateDeviceIdAsync("A4", default).ConfigureAwait(false)).ConfigureAwait(false);
        string d1 = handler.LastDeviceId;
        var t2 = await client.CreateQrAsync(default, await MiyousheCredentialStore.GetOrCreateDeviceIdAsync("A4", default).ConfigureAwait(false)).ConfigureAwait(false);
        string d2 = handler.LastDeviceId;
        Console.WriteLine("  第1次 device=" + Mask(d1) + "（头恰 " + DeviceCount(handler) + " 个） ticket1=" + Mask(t1.Ticket));
        Console.WriteLine("  第2次 device=" + Mask(d2) + "（头恰 " + DeviceCount(handler) + " 个） ticket2=" + Mask(t2.Ticket));
        Check(d1 == d2 && d1.Length == 36, "同账号两次 device_id 完全相同（修复前每次新 Guid）");
        Check(handler.DeviceIdCount == 1, "x-rpc-device_id 头恰 1 个");
        Check(t1.Ticket.Length > 10 && t2.Ticket.Length > 10, "两次都拿到真实 ticket（retcode=0 路径）");

        Console.WriteLine("==== T40-2 持久化：文件存在且内容一致 ====");
        string deviceFile = Path.Combine(dataDir, "miyoushe", "A4.device");
        string fileDevice = "";
        if (File.Exists(deviceFile)) fileDevice = (await File.ReadAllTextAsync(deviceFile).ConfigureAwait(false)).Trim();
        Console.WriteLine("  device 文件=" + (File.Exists(deviceFile) ? "存在" : "不存在") + " 内容=" + Mask(fileDevice));
        Check(File.Exists(deviceFile) && fileDevice == d1, "<DataDir>\\miyoushe\\A4.device 落盘且与请求头一致");

        Console.WriteLine("==== T40-3 重启模拟：重新读文件仍是同一 device ====");
        // 重新走 GetOrCreateDeviceIdAsync（模拟新进程：无内存缓存，本实现即文件读取路径）
        var d3 = await MiyousheCredentialStore.GetOrCreateDeviceIdAsync("A4", default).ConfigureAwait(false);
        Check(d3 == d1, "进程重启后（重读文件）仍是同一 device_id");

        Console.WriteLine("==== T40-4 不同账号不同 device ====");
        var dA2 = await MiyousheCredentialStore.GetOrCreateDeviceIdAsync("A2", default).ConfigureAwait(false);
        Console.WriteLine("  A2 device=" + Mask(dA2));
        Check(dA2 != d1 && dA2.Length == 36, "A2 与 A4 的 device_id 不同（不共用）");

        Console.WriteLine(_fail == 0 ? "T40-ALL-PASS" : "T40-FAILURES=" + _fail);
        try { Directory.Delete(dataDir, true); } catch { }
        return _fail == 0 ? 0 : 1;
    }

    private static int DeviceCount(CaptureHandler h) => h.DeviceIdCount;
}
