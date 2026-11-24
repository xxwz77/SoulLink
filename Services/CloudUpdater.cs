using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 云端更新检查与下载。
    ///
    /// ★ 服务器地址来自单一设置（ServerEndpoints），代码不硬编码任何域名；
    ///   未配置时静默短路，不发起任何请求。
    /// ★ 地址**不进入任何面向用户的文本**（对话框、日志、异常消息一律只写
    ///   "检查更新失败"这类中性描述）。
    /// </summary>
    public static class CloudUpdater
    {
        // ── 发布位置（客户端不展示，不要拼进任何 UI 文案）──
        // ★ 地址来自单一服务器设置（ServerEndpoints），不再硬编码任何域名。
        //   未配置时 Base 为空 → CheckAsync/DownloadAsync 直接短路，不发请求。

        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            // ★ 显式禁用系统代理：用户机器上常有指向 127.0.0.1:7897 的失效配置，
            //   走代理会「积极拒绝」，检查更新直接失败。
            var handler = new HttpClientHandler { UseProxy = false };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SoulLink-Updater");
            return c;
        }

        /// <summary>云端清单。</summary>
        public sealed class Manifest
        {
            public string Version { get; set; } = "";
            public string Notes { get; set; } = "";
            /// <summary>更新包文件名（zip）。</summary>
            public string File { get; set; } = "";
            public long Size { get; set; }
            public string Sha256 { get; set; } = "";
            public string PublishedAt { get; set; } = "";
            /// <summary>所需 .NET 桌面运行时版本（如 "11.0"）。</summary>
            public string MinRuntime { get; set; } = "";
        }

        /// <summary>当前运行的程序版本（如 "3.1.3"）。</summary>
        public static string CurrentVersion
        {
            get
            {
                try
                {
                    var asm = typeof(CloudUpdater).Assembly;
                    // 单文件发布时 asm.Location 为空，优先读 InformationalVersion
                    var attrs = asm.GetCustomAttributes(
                        typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                    if (attrs != null && attrs.Length > 0)
                    {
                        var info = ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0])
                                   .InformationalVersion;
                        if (!string.IsNullOrWhiteSpace(info))
                        {
                            int plus = info.IndexOf('+');
                            if (plus > 0) info = info.Substring(0, plus);
                            return Trim3(info!);
                        }
                    }
                    return Trim3(asm.GetName().Version?.ToString() ?? "1.0.0");
                }
                catch { return "1.0.0"; }
            }
        }

        private static string Trim3(string v)
        {
            var parts = (v ?? "").Split('.');
            return parts.Length >= 3 ? parts[0] + "." + parts[1] + "." + parts[2] : (v ?? "");
        }

        /// <summary>
        /// 解析版本号为可比较的结构。
        ///
        /// ★ 不能用 System.Version 直接 TryParse 后比较 "3.1.10"：
        ///   Version 的 Minor/Build 都是 int，看起来没问题 —— 但 Trim3 之后
        ///   如果某段是两位数，字符串比较会出错。这里显式按数值解析每一段。
        /// </summary>
        private static int[]? Parse(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim();
            int plus = v.IndexOf('+');
            if (plus > 0) v = v.Substring(0, plus);
            var parts = v.Split('.');
            if (parts.Length < 2) return null;
            var nums = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                // 允许 "1-beta" 这类后缀，取前面的数字部分
                var digits = new string(System.Array.FindAll(parts[i].ToCharArray(),
                    c => c >= '0' && c <= '9'));
                if (digits.Length == 0) return null;
                if (!int.TryParse(digits, out nums[i])) return null;
            }
            return nums;
        }

        /// <summary>按段比较两个版本号；a &lt; b 返回负数，相等返回 0。</summary>
        private static int Compare(int[] a, int[] b)
        {
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        /// <summary>
        /// 检查是否有新版本。返回 null 表示已是最新或检查失败（不抛异常，
        /// 因为这是后台静默检查，失败不应打扰用户）。
        /// </summary>
        public static async Task<Manifest?> CheckAsync(CancellationToken ct = default)
        {
            try
            {
                var m = await FetchManifestAsync(ct).ConfigureAwait(false);
                if (m == null) return null;

                var remote = Parse(m.Version);
                var local = Parse(CurrentVersion);
                if (remote == null || local == null) return null;
                if (Compare(remote, local) <= 0) return null;
                if (string.IsNullOrWhiteSpace(m.Sha256)) return null;
                return m;
            }
            catch { return null; }
        }

        /// <summary>读取云端清单。带时间戳绕过 CDN 缓存。</summary>
        public static async Task<Manifest?> FetchManifestAsync(CancellationToken ct = default)
        {
            // ★ 未配置服务器地址时不发任何请求（外部化后默认无内置地址）
            if (!ServerEndpoints.IsConfigured) return null;

            // ★ 加 ?t= 破缓存：CDN 刷新是异步的，不破缓存会读到上一个版本
            string url = ServerEndpoints.UpdateManifest + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<Manifest>(json, opt);
        }

        /// <summary>
        /// 下载新版本到临时目录并校验 SHA256。
        /// 进度回调参数为 0..1；未知总长时传 -1。
        /// </summary>
        public static async Task<string> DownloadAsync(
            Manifest m, IProgress<double>? progress, CancellationToken ct = default)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            if (string.IsNullOrWhiteSpace(m.Sha256))
                throw new InvalidOperationException("安装包缺少校验信息");

            // ★ 包名来自清单（形如 update-3.1.3.zip）：内容与文件名一一对应，
            //   不会被覆盖，所以 CDN 缓存多久都是正确的那一份。
            //   ★ 先校验再使用，防止清单被篡改成 "../xxx" 逃出临时目录。
            if (string.IsNullOrWhiteSpace(m.File)
                || m.File.Contains("..") || m.File.Contains('/') || m.File.Contains('\\'))
                throw new InvalidOperationException("更新包名无效");

            string dir = Path.Combine(Path.GetTempPath(), "SoulLinkUpdate");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, Path.GetFileName(m.File));

            if (File.Exists(dest))
            {
                try { File.Delete(dest); } catch { }
            }

            // ★ 未配置服务器地址时不发任何请求
            if (!ServerEndpoints.IsConfigured)
                throw new InvalidOperationException("未配置服务器地址");
            string url = ServerEndpoints.UpdateBase + Uri.EscapeDataString(m.File);
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? m.Size;
            long done = 0;
            var buffer = new byte[81920];

            using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                                            81920, useAsync: true))
            {
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (progress != null)
                    {
                        double p = total > 0 ? Math.Min(1.0, (double)done / total) : -1;
                        progress.Report(p);
                    }
                }
            }

            // ★ 校验：没有这一步，下载中断产生的残缺文件会被写进磁盘，
            //   用户下次启动直接崩溃且无法自愈。
            string actual = await ComputeSha256Async(dest, ct).ConfigureAwait(false);
            if (!string.Equals(actual, m.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(dest); } catch { }
                throw new InvalidOperationException("下载的文件校验不通过");
            }
            if (m.Size > 0)
            {
                long len = new FileInfo(dest).Length;
                if (len != m.Size)
                {
                    try { File.Delete(dest); } catch { }
                    throw new InvalidOperationException("下载的文件大小不正确");
                }
            }
            return dest;
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
        {
            using var sha = SHA256.Create();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                          81920, useAsync: true);
            var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
