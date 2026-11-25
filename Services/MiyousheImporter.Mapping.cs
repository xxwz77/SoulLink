using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    // ── 2. 解析 + 3. 映射存储 + 4. 导入后自检 ────────────────────────────

    internal static partial class MiyousheImporter
    {
        /// <summary>`k1=v1; k2=v2` / 换行分隔 / 尾部 `; ` 都能吃下；值里允许 `=`。</summary>
        internal static List<KeyValuePair<string, string>> ParseCookiePairs(string text)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (string part in text.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = part.Trim();
                if (item.Length == 0) continue;
                int eq = item.IndexOf('=');
                if (eq <= 0) continue;
                string name = item.Substring(0, eq).Trim();
                string value = item.Substring(eq + 1).Trim().Trim('"');
                if (name.Length == 0 || value.Length == 0) continue;
                if (!list.Any(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new KeyValuePair<string, string>(name, value));
            }
            return list;
        }

        /// <summary>读 BetterGI config.json 的 otherConfig.miyousheConfig.cookie。</summary>
        internal static string ReadBetterGiCookie(string json, out string detail)
        {
            detail = "未找到该节点";
            if (string.IsNullOrWhiteSpace(json)) { detail = "文件为空"; return ""; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("otherConfig", out var other)) { detail = "没有 otherConfig 节点"; return ""; }
                if (!other.TryGetProperty("miyousheConfig", out var mys)) { detail = "没有 otherConfig.miyousheConfig 节点"; return ""; }
                if (!mys.TryGetProperty("cookie", out var cookie)) { detail = "miyousheConfig 里没有 cookie 字段"; return ""; }
                string value = cookie.ValueKind == JsonValueKind.String ? cookie.GetString() ?? "" : "";
                detail = value.Length > 0 ? "存在且非空" : "字段存在但为空";
                return value.Trim();
            }
            catch (Exception) { detail = "JSON 格式无效"; return ""; }
        }

        /// <summary>取 YAML 里**最外层**的 cookie（account 级），跳过 os 段里缩进更深的空值。</summary>
        internal static string YamlCookie(string yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml)) return "";
            string best = ""; int bestIndent = int.MaxValue;
            foreach (string raw in yaml.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                int i = line.IndexOf("cookie", StringComparison.OrdinalIgnoreCase);
                if (i < 0 || i >= 32) continue;
                if (line.Substring(0, i).Trim().Length != 0) continue;      // 前缀必须是纯空白
                string rest = line.Substring(i + 6).TrimStart();
                if (rest.Length == 0 || rest[0] != ':') continue;           // 必须是 cookie:
                string value = Unquote(rest.Substring(1).Trim());
                if (value.Length == 0) continue;
                if (i < bestIndent) { bestIndent = i; best = value; }
            }
            return best;
        }

        /// <summary>取 ABGI 账号 YAML 的 `abgi.name`（没有就用文件名）。</summary>
        internal static string YamlAbgiName(string yaml, string fallback = "")
        {
            if (!string.IsNullOrWhiteSpace(yaml))
            {
                bool inAbgi = false;
                foreach (string raw in yaml.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Trim().Length == 0) continue;
                    int indent = line.Length - line.TrimStart().Length;
                    string trimmed = line.Trim();
                    if (indent == 0) { inAbgi = trimmed.StartsWith("abgi:", StringComparison.Ordinal); continue; }
                    if (!inAbgi) continue;
                    if (trimmed.StartsWith("name:", StringComparison.Ordinal))
                    {
                        string value = Unquote(trimmed.Substring(5).Trim());
                        if (value.Length > 0) return value.Length > 32 ? value.Substring(0, 32) : value;
                    }
                }
            }
            return fallback.Length > 32 ? fallback.Substring(0, 32) : fallback;
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                return value.Substring(1, value.Length - 2).Trim();
            return value.Trim();
        }

        private static void AddLoginResultExtras(Target target, string rootDir)
        {
            try
            {
                string file = Path.Combine(rootDir, "mihoyo_login_result.txt");
                if (!File.Exists(file)) return;
                foreach (var pair in ParseCookiePairs(SafeRead(file)))
                {
                    string name = pair.Key.ToLowerInvariant();
                    if (name is "stoken" or "stuid" or "ltoken" or "ltuid" or "mid" or "device_id")
                        target.ExtraPairs.Add(pair);
                }
            }
            catch { }
        }

        internal static string SafeRead(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 4 * 1024 * 1024) return "";
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch { return ""; }
        }

        private static string Get(List<KeyValuePair<string, string>> pairs, string name)
        {
            foreach (var pair in pairs)
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return "";
        }

        // ── 键名映射（旧 → 新）──────────────────────────────────────────

        /// <summary>
        /// 把一份 cookie 文本映射成 MiyousheCredential。
        /// **同时**写入 RawCookie（原文回放用）与 v2 名别名，确保 CookieHeader 不会残缺。
        /// </summary>
        internal static MiyousheCredential BuildCredential(Target target, string region = "cn")
        {
            var credential = new MiyousheCredential { Region = region };
            var all = ParseCookiePairs(target.CookieText);
            foreach (var extra in target.ExtraPairs)
                if (!all.Any(p => string.Equals(p.Key, extra.Key, StringComparison.OrdinalIgnoreCase)))
                    all.Add(extra);

            // 旧名 → 字段；同时接受 v2 旧数据（如果用户之前扫码存的）
            credential.LToken = FirstOf(all, "ltoken_v2", "ltoken");
            credential.LtUid = FirstOf(all, "ltuid_v2", "ltuid");
            credential.CookieToken = FirstOf(all, "cookie_token_v2", "cookie_token");
            credential.AccountId = FirstOf(all, "account_id_v2", "account_id", "ltuid_v2", "ltuid");
            credential.LtMid = FirstOf(all, "ltmid_v2", "mid");
            credential.Mid = FirstOf(all, "mid");
            credential.SToken = FirstOf(all, "stoken_v2", "stoken");
            credential.StUid = FirstOf(all, "stuid");
            credential.Aid = FirstOf(all, "aid");
            credential.DeviceId = FirstOf(all, "device_id");
            if (credential.DeviceId.Length == 0) credential.DeviceId = Guid.NewGuid().ToString("D");

            // ★ 原文回放：请求时优先用它，避免"v1 值挂 v2 名"导致服务端判无效。
            credential.RawCookie = Canonical(target.CookieText);
            credential.UpdatedAt = DateTimeOffset.Now;
            return credential;
        }

        /// <summary>把 cookie 规范成 `k=v; k=v`（去重、去空、保序）。</summary>
        internal static string Canonical(string cookieText)
        {
            var pairs = ParseCookiePairs(cookieText);
            if (pairs.Count == 0) return "";
            return string.Join("; ", pairs.Select(p => p.Key + "=" + p.Value));
        }

        internal static IReadOnlyList<string> DescribeFields(string cookieText)
        {
            var names = new List<string>();
            foreach (var pair in ParseCookiePairs(cookieText))
            {
                string key = pair.Key;
                string tag = key.ToLowerInvariant();
                if (tag is "ltoken" or "ltuid" or "cookie_token" or "account_id" or "mid")
                    names.Add(key + " (v1→已映射)");
                else if (tag.EndsWith("_v2", StringComparison.Ordinal))
                    names.Add(key + " (v2)");
                else
                    names.Add(key);
            }
            return names;
        }

        private static string FirstOf(List<KeyValuePair<string, string>> pairs, params string[] names)
        {
            foreach (string name in names)
                foreach (var pair in pairs)
                    if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) && pair.Value.Length > 0)
                        return pair.Value;
            return "";
        }
    }
}
