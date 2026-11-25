using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SoulLink.Services
{
    /// <summary>单条导入结果。**只含字段名与掩码 uid，永不含 cookie 原文**。</summary>
    public sealed record MiyousheImportItem(
        string Account, bool Ok, string SourceKind, string SourceFile,
        IReadOnlyList<string> FoundFields, string MaskedUid, string Message,
        string SelfCheck, IReadOnlyList<string> Probed);

    /// <summary>一次导入（单个账号或批量）的结果汇总。</summary>
    public sealed record MiyousheImportOutcome(
        bool Ok, string Path, string SourceKind, int OkCount, int FailedCount,
        IReadOnlyList<MiyousheImportItem> Items, string Detail, IReadOnlyList<string> Probed);

    // ── 1. 路径识别 ─────────────────────────────────────────────────────

    /// <summary>
    /// 从**本地既有客户端**导入米游社凭据（免扫码）。
    ///
    /// ── 支持的来源（用户只给一个路径，程序自动判别）────────────────────
    ///   · BetterGI 根：`&lt;根&gt;\User\config.json` → `otherConfig.miyousheConfig.cookie`
    ///   · ABGI 根    ：`&lt;根&gt;\mysConfig.yaml` → `account.cookie`（YAML）
    ///   · ABGI 根    ：`&lt;根&gt;\MihoyoBBSTools\config\accounts\*.yaml` → `cookie`
    ///   · ABGI 根    ：`&lt;根&gt;\mihoyo_login_result.txt` → `stoken/stuid/...`（补全用）
    ///   · 也接受直接给上面任意一个**文件**路径
    ///   · path 留空或 `@bound` = 用该账号已绑定的 BetterGI 路径（BgiPathResolver）
    ///
    /// ── 关键：旧键名 → 新键名 双向兼容 ─────────────────────────────────
    ///   真实来源里的 cookie 是**旧键名**（ltoken/ltuid/cookie_token/account_id/mid），
    ///   而 MiyousheCatalog 的 cn 区域期望 v2 键名。若把 v1 值挂在 v2 名下，服务端判为无效。
    ///   所以这里同时做两件事：① 映射进结构化字段（状态展示/掩码/角色查询）；
    ///   ② 把**原始 cookie 原文**存进 MiyousheCredential.RawCookie，发请求时原样回放。
    ///
    /// ── 安全 ───────────────────────────────────────────────────────────
    ///   本类不写日志、不在异常消息里拼 cookie；返回值只给字段名与掩码。
    /// </summary>
    internal static partial class MiyousheImporter
    {
        internal const string KindBetterGi = "bettergi";
        internal const string KindAbgi = "abgi";

        /// <summary>一个待导入的数据源（文件 + cookie 文本 + 账号提示）。</summary>
        internal sealed class Target
        {
            internal string FilePath = "";
            internal string CookieText = "";
            internal string AccountHint = "";
            internal readonly List<KeyValuePair<string, string>> ExtraPairs = new();
        }

        /// <summary>
        /// 判别输入路径属于哪种来源，并收集所有可用数据源。
        /// 失败时 <paramref name="probed"/> 里逐条写明「找过什么、在不在」。
        /// </summary>
        internal static (string Kind, List<Target> Items, List<string> Probed, string Detail) Resolve(
            string inputPath, string account)
        {
            var probed = new List<string>();
            var items = new List<Target>();

            string input = (inputPath ?? "").Trim().Trim('"');
            if (input.Length == 0 || string.Equals(input, "@bound", StringComparison.OrdinalIgnoreCase))
            {
                string bound = "";
                try { bound = BgiPathResolver.GetBoundPath(account); } catch { }
                if (bound.Length == 0)
                {
                    probed.Add("账号「" + account + "」没有已绑定的 BetterGI 路径");
                    return ("", items, probed, "未指定路径，且该账号没有已绑定的 BetterGI 路径");
                }
                input = bound;
                probed.Add("使用已绑定路径：" + bound);
            }

            if (File.Exists(input)) return ResolveFile(input, probed);
            if (!Directory.Exists(input))
            {
                probed.Add("路径不存在：" + input);
                return ("", items, probed, "路径不存在（既不是文件也不是目录）");
            }
            return ResolveDirectory(input, probed, items);
        }

        private static (string, List<Target>, List<string>, string) ResolveFile(string input, List<string> probed)
        {
            var items = new List<Target>();
            string name = Path.GetFileName(input);
            string dir = Path.GetDirectoryName(input) ?? "";
            string text = SafeRead(input);
            if (text.Length == 0)
            {
                probed.Add(name + "：内容为空或不可读");
                return ("", items, probed, "文件不可读");
            }

            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                string cookie = ReadBetterGiCookie(text, out string fieldDetail);
                probed.Add(name + "：" + fieldDetail);
                if (cookie.Length > 0)
                {
                    items.Add(new Target { FilePath = input, CookieText = cookie });
                    return (KindBetterGi, items, probed, "识别为 BetterGI User/config.json");
                }
                return ("", items, probed, "该 JSON 里没有 otherConfig.miyousheConfig.cookie");
            }

            if (name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            {
                string cookie = YamlCookie(text);
                probed.Add(name + "：cookie " + (cookie.Length > 0 ? "非空" : "为空"));
                if (cookie.Length > 0)
                {
                    var t = new Target { FilePath = input, CookieText = cookie, AccountHint = YamlAbgiName(text) };
                    AddLoginResultExtras(t, dir);
                    items.Add(t);
                    return (KindAbgi, items, probed, "识别为 ABGI 账号 YAML");
                }
                return ("", items, probed, "该 YAML 里 cookie 为空");
            }

            var pairs = ParseCookiePairs(text);
            probed.Add(name + "：解析到 " + pairs.Count + " 个 k=v");
            if (Get(pairs, "ltoken").Length > 0 || Get(pairs, "cookie_token").Length > 0)
            {
                items.Add(new Target { FilePath = input, CookieText = text });
                return (KindAbgi, items, probed, "识别为 ABGI mihoyo_login_result.txt");
            }
            return ("", items, probed, "无法识别该文件的内容格式");
        }

        private static (string, List<Target>, List<string>, string) ResolveDirectory(
            string input, List<string> probed, List<Target> items)
        {
            string bgiConfig = Path.Combine(input, "User", "config.json");
            bool bgiConfigExists = File.Exists(bgiConfig);
            probed.Add("User/config.json：" + (bgiConfigExists ? "存在" : "不存在"));
            if (!bgiConfigExists)
            {
                string alt = Path.Combine(input, "config.json");
                if (File.Exists(alt)) { bgiConfig = alt; bgiConfigExists = true; probed.Add("config.json：存在（同级）"); }
            }
            if (bgiConfigExists)
            {
                string text = SafeRead(bgiConfig);
                string cookie = ReadBetterGiCookie(text, out string fieldDetail);
                probed.Add("otherConfig.miyousheConfig.cookie：" + fieldDetail);
                if (cookie.Length > 0)
                {
                    items.Add(new Target { FilePath = bgiConfig, CookieText = cookie });
                    return (KindBetterGi, items, probed, "识别为 BetterGI 根（含真实 cookie）");
                }
            }

            string mysCfg = Path.Combine(input, "mysConfig.yaml");
            bool mysExists = File.Exists(mysCfg);
            probed.Add("mysConfig.yaml：" + (mysExists ? "存在" : "不存在"));
            if (mysExists)
            {
                string text = SafeRead(mysCfg);
                string cookie = YamlCookie(text);
                probed.Add("mysConfig.yaml 的 account.cookie：" + (cookie.Length > 0 ? "非空" : "为空"));
                if (cookie.Length > 0)
                {
                    var t = new Target { FilePath = mysCfg, CookieText = cookie, AccountHint = YamlAbgiName(text) };
                    AddLoginResultExtras(t, input);
                    items.Add(t);
                }
            }

            string bbsDir = FindSubDir(input, "MihoyoBBSTools");
            probed.Add("MihoyoBBSTools：" + (bbsDir.Length > 0 ? "存在" : "不存在"));
            if (bbsDir.Length > 0)
            {
                string accDir = Path.Combine(bbsDir, "config", "accounts");
                probed.Add("MihoyoBBSTools/config/accounts：" + (Directory.Exists(accDir) ? "存在" : "不存在"));
                if (Directory.Exists(accDir)) CollectAccountYamls(accDir, input, items, probed);
            }

            if (items.Count > 0) return (KindAbgi, items, probed, "识别为 ABGI 根，共 " + items.Count + " 份 cookie");
            return ("", items, probed, "该目录既不是 BetterGI 根，也没找到可用的 ABGI cookie");
        }

        private static void CollectAccountYamls(string accDir, string rootDir, List<Target> items, List<string> probed)
        {
            List<string> files;
            try
            {
                files = Directory.GetFiles(accDir, "*.yaml").Concat(Directory.GetFiles(accDir, "*.yml")).ToList();
            }
            catch { return; }
            foreach (string file in files)
            {
                string text = SafeRead(file);
                string cookie = YamlCookie(text);
                if (cookie.Length == 0) { probed.Add(Path.GetFileName(file) + "：cookie 为空"); continue; }
                var t = new Target
                {
                    FilePath = file, CookieText = cookie,
                    AccountHint = YamlAbgiName(text, Path.GetFileNameWithoutExtension(file)),
                };
                AddLoginResultExtras(t, rootDir);
                items.Add(t);
                probed.Add(Path.GetFileName(file) + "：识别到 cookie");
            }
        }

        private static string FindSubDir(string parent, string name)
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(parent))
                    if (string.Equals(Path.GetFileName(dir), name, StringComparison.OrdinalIgnoreCase)) return dir;
            }
            catch { }
            return "";
        }
    }
}
