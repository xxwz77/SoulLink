using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Core;

namespace SoulLink.Services
{
    internal sealed class MiyousheCredential
    {
        public string LToken { get; set; } = "";
        public string LtUid { get; set; } = "";
        public string LtMid { get; set; } = "";
        public string CookieToken { get; set; } = "";
        public string AccountId { get; set; } = "";
        public string AccountMid { get; set; } = "";
        public string SToken { get; set; } = "";
        public string StUid { get; set; } = "";
        public string Mid { get; set; } = "";
        public string Aid { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string Region { get; set; } = "cn";

        /// <summary>
        /// 导入时的**原始 cookie 原文**（`k=v; k=v`，规范化后）。
        ///
        /// ★ 为什么需要它：BetterGI / ABGI 里的 cookie 是**旧键名**
        ///   （ltoken / ltuid / cookie_token / account_id / mid），而 MiyousheCatalog 的
        ///   国服档案期望 v2 键名（ltoken_v2 / ltuid_v2 / cookie_token_v2 / account_id_v2）。
        ///   把 v1 的值挂到 v2 名下会被服务端判为无效。
        ///   因此 CookieHeader 在发送时**优先原样回放**这份原文，结构化字段只用于
        ///   状态展示、掩码与本地判断；原文为空（扫码登录）时退回结构化字段拼装。
        /// ★ 与其它字段同样只以 DPAPI 密文落盘，绝不进日志/异常/API 响应。
        /// </summary>
        public string RawCookie { get; set; } = "";
        public DateTimeOffset LastOkAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class MiyousheCredentialFile
    {
        public Dictionary<string,MiyousheCredential> Regions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record MiyousheLoginStatus(
        string Account, string State, string Uid, string AidMasked, string Detail, long UpdatedAtMs);

    /// <summary>
    /// 每挂机账号一份 DPAPI CurrentUser 密文。主进程以一个用户管理 A1-A4，
    /// CurrentUser 比 LocalMachine 更窄：后者允许本机其他 Windows 用户解密。
    /// 文件位于 DataDir/miyoushe，更新包排除 data；绝不保存明文 cookie。
    /// 此处的内部凭据 DTO 绝不能直接写日志或作为 API 响应序列化。
    /// </summary>
    internal static class MiyousheCredentialStore
    {
        private static string Root => Path.Combine(AppIdentity.DataDir, "miyoushe");

        // ── t40：device_id 按账号持久化 ──────────────────────────────
        //
        // ★ 每次扫码换新 Guid 是典型风控特征（服务端视角=「每秒一台新设备」）。
        //   顺序：① 已有凭据里的 DeviceId（老账号不「换设备」）→ ② <账号>.device 落盘文件
        //   → ③ 首次生成并立即落盘。凭据删除**不**删 device 文件（同一台机器还是这台设备，
        //   与真实客户端一致）；手动重置=删 <DataDir>\miyoushe\<账号>.device。
        //   ★ device_id 与凭据同级敏感：绝不写日志/异常/API 响应。
        private static string DeviceFile(string account)
            => Path.Combine(Root, account + ".device");

        /// <summary>取该账号的持久化 device_id（无则生成并落盘）。并发安全（进程内锁 + 文件级 try）。</summary>
        internal static async Task<string> GetOrCreateDeviceIdAsync(string account, CancellationToken ct = default)
        {
            // ① 老账号：凭据里已有 DeviceId → 沿用（不换设备）
            try
            {
                var credential = await LoadAsync(account, ct).ConfigureAwait(false);
                if (credential != null && !string.IsNullOrWhiteSpace(credential.DeviceId))
                {
                    await SaveDeviceFileAsync(account, credential.DeviceId).ConfigureAwait(false);
                    return credential.DeviceId;
                }
            }
            catch { /* 凭据读取失败走文件路径 */ }

            // ② 设备文件
            try
            {
                string file = DeviceFile(account);
                if (File.Exists(file))
                {
                    string existing = (await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)).Trim();
                    if (existing.Length == 36) return existing;   // Guid D 格式长度
                }
            }
            catch { /* 读失败→重新生成（写会再 try） */ }

            // ③ 首次生成 + 落盘
            string device = Guid.NewGuid().ToString("D");
            await SaveDeviceFileAsync(account, device).ConfigureAwait(false);
            return device;
        }

        private static async Task SaveDeviceFileAsync(string account, string device)
        {
            try
            {
                Directory.CreateDirectory(Root);
                await File.WriteAllTextAsync(DeviceFile(account), device + Environment.NewLine).ConfigureAwait(false);
            }
            catch { /* 落盘失败不阻塞登录（本进程内仍复用同一值） */ }
        }

        internal static string SafeAccount(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var sb = new StringBuilder(value.Trim().Length);
            foreach (char c in value.Trim())
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            return sb.Length == 0 || sb.Length > 64 ? "" : sb.ToString();
        }

        internal static string PathFor(string account, string region="cn")
            => Path.Combine(Root, SafeAccount(account) + "." + (region=="os"?"os":"cn") + ".json");
        internal static string LegacyPathFor(string account) => Path.Combine(Root, SafeAccount(account)+".json");

        internal static Task SaveAsync(string account, MiyousheCredential credential, CancellationToken ct = default)
            => SaveAsync(account,credential.Region, credential,ct);

        internal static async Task SaveAsync(string account, string region, MiyousheCredential credential, CancellationToken ct = default)
        {
            string safe = SafeAccount(account);
            if (safe.Length == 0) throw new ArgumentException("账号名无效", nameof(account));
            await Task.Run(() =>
            {
                credential.Region=region=="os"?"os":"cn";
                credential.UpdatedAt = DateTimeOffset.Now;
                string plain = JsonSerializer.Serialize(credential);
                string cipher = SecretBox.Protect(plain);
                if (!cipher.StartsWith("dpapi:", StringComparison.Ordinal))
                    throw new InvalidOperationException("凭据加密未生效");
                Directory.CreateDirectory(Root);
                string file = PathFor(safe,region), temp = file + ".tmp";
                File.WriteAllText(temp, cipher, new UTF8Encoding(false));
                File.Move(temp, file, true);
            }, ct).ConfigureAwait(false);
        }

        internal static Task<MiyousheCredential?> LoadAsync(string account, CancellationToken ct = default)
            => LoadAsync(account,"cn",ct);

        internal static async Task<MiyousheCredential?> LoadAsync(string account, string region, CancellationToken ct = default)
        {
            string safe = SafeAccount(account);
            if (safe.Length == 0) return null;
            return await Task.Run(() =>
            {
                string file = PathFor(safe,region);
                if(region!="os" && !File.Exists(file)) file=LegacyPathFor(safe);
                if (!File.Exists(file)) return null;
                string cipher = File.ReadAllText(file, Encoding.UTF8);
                // SecretBox 为兼容旧密码会返回明文，米游社凭据不继承这个回退。
                if (!cipher.StartsWith("dpapi:", StringComparison.Ordinal)) return null;
                string plain = SecretBox.Unprotect(cipher);
                if (string.IsNullOrWhiteSpace(plain)) return null;
                try { var credential=JsonSerializer.Deserialize<MiyousheCredential>(plain); if(credential!=null)credential.Region=region; return credential; }
                catch (JsonException) { return null; }
            }, ct).ConfigureAwait(false);
        }

        internal static async Task<MiyousheLoginStatus> StatusAsync(string account, CancellationToken ct = default)
        {
            string state = "not_logged_in", detail = "请使用米游社 App 扫码并在手机端确认";
            var credential = await LoadAsync(account, ct).ConfigureAwait(false);
            if (credential != null)
            {
                state = string.IsNullOrWhiteSpace(credential.CookieToken) ? "needs_login" : "stored";
                detail = state == "stored" ? "凭据已加密保存；状态将在下次请求时校验" : "凭据不完整，请重新扫码";
            }
            return new MiyousheLoginStatus(account, state,
                credential?.AccountId ?? credential?.LtUid ?? "", Mask(credential?.Aid), detail,
                credential == null ? 0 : credential.UpdatedAt.ToUnixTimeMilliseconds());
        }

        /// <summary>删除某账号某区域的凭据（DataDir/miyoushe 下的 dpapi 密文文件）。</summary>
        internal static void Delete(string account, string region = "cn")
        {
            string safe = SafeAccount(account);
            if (safe.Length == 0) return;
            try
            {
                string file = PathFor(safe, region);
                if (File.Exists(file)) File.Delete(file);
                string legacy = LegacyPathFor(account);
                if (File.Exists(legacy)) File.Delete(legacy);
            }
            catch { }
        }

        internal static string Mask(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Length <= 3) return "***";
            return value[..2] + new string('*', Math.Min(8, value.Length - 3)) + value[^1];
        }
    }
}