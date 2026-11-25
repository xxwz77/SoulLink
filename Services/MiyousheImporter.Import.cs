using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    // ── 4. 导入编排：解析 → 映射 → DPAPI 落盘 → 立即自检 ─────────────────

    internal static partial class MiyousheImporter
    {
        /// <summary>
        /// 执行一次导入。
        /// </summary>
        /// <param name="account">目标 SoulLink 账号；**允许任意名称**（不要求在 Accounts 列表里存在），
        /// 因为 A1 这类账号属于另一个实例，调试期仍需要能把凭据落到本实例做验证。</param>
        /// <param name="path">ABGI 主文件夹 / BetterGI 根 / 具体文件；留空或 `@bound` 用已绑定路径。</param>
        /// <param name="verify">是否导入后立刻用 getUserGameRolesByCookie 自检。</param>
        internal static async Task<MiyousheImportOutcome> ImportAsync(
            string account, string path, bool verify, CancellationToken ct)
        {
            string target = (account ?? "").Trim();
            if (MiyousheCredentialStore.SafeAccount(target).Length == 0)
                return new MiyousheImportOutcome(false, path ?? "", "", 0, 0,
                    Array.Empty<MiyousheImportItem>(), "账号名无效（只允许字母/数字/-/_/.，长度 1~64）",
                    Array.Empty<string>());

            var (kind, targets, probed, detail) = Resolve(path, target);
            if (targets.Count == 0)
                return new MiyousheImportOutcome(false, path ?? "", kind, 0, 0,
                    Array.Empty<MiyousheImportItem>(), detail, probed);

            var items = new List<MiyousheImportItem>();
            int ok = 0, failed = 0;
            int index = 0;
            foreach (var source in targets)
            {
                index++;
                // 多份来源时：第一份落到指定账号，其余用 YAML 里的 abgi.name 做账号名，
                // 保证批量导入不会互相覆盖（用户给的 path 是 ABGI 根时会命中这一类）。
                string useAccount = index == 1 || string.IsNullOrWhiteSpace(source.AccountHint)
                    ? target : SafeAccountOrFallback(source.AccountHint, target, index);

                var item = await ImportOneAsync(useAccount, source, kind, verify, ct).ConfigureAwait(false);
                items.Add(item);
                if (item.Ok) ok++; else failed++;
            }

            bool allOk = failed == 0;
            string summary = allOk
                ? "导入成功 " + ok + " 份；凭据已 DPAPI 加密保存"
                : "成功 " + ok + " 份，失败 " + failed + " 份";
            return new MiyousheImportOutcome(allOk, path ?? "", kind, ok, failed, items, summary, probed);
        }

        private static string SafeAccountOrFallback(string hint, string fallback, int index)
        {
            string safe = MiyousheCredentialStore.SafeAccount(hint);
            return safe.Length > 0 ? safe : fallback + "-" + index;
        }

        private static async Task<MiyousheImportItem> ImportOneAsync(
            string account, Target source, string kind, bool verify, CancellationToken ct)
        {
            IReadOnlyList<string> fields = DescribeFields(source.CookieText);
            string fileLabel;
            try { fileLabel = System.IO.Path.GetFileName(source.FilePath); } catch { fileLabel = ""; }

            MiyousheCredential credential;
            try { credential = BuildCredential(source); }
            catch (Exception)
            {
                return new MiyousheImportItem(account, false, kind, fileLabel, fields, "", "凭据解析失败", "", Array.Empty<string>());
            }

            string masked = MiyousheCredentialStore.Mask(
                credential.AccountId.Length > 0 ? credential.AccountId : credential.LtUid);

            try
            {
                await MiyousheCredentialStore.SaveAsync(account, credential.Region, credential, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 绝不回显异常内容（可能含序列化片段）。
                return new MiyousheImportItem(account, false, kind, fileLabel, fields, masked,
                    "凭据加密保存失败（DPAPI 不可用或数据目录不可写）", "", Array.Empty<string>());
            }

            if (!verify)
                return new MiyousheImportItem(account, true, kind, fileLabel, fields, masked,
                    "已导入并加密保存（未自检）", "skipped", Array.Empty<string>());

            string selfCheck; IReadOnlyList<string> roles;
            (selfCheck, roles) = await VerifyAsync(account, credential, ct).ConfigureAwait(false);
            return new MiyousheImportItem(account, true, kind, fileLabel, fields, masked,
                "已导入并加密保存", selfCheck, roles);
        }

        /// <summary>
        /// 导入后自检：getUserGameRolesByCookie。
        /// 只要能返回角色（uid/nickname/level）就证明这份 cookie 真实有效。
        /// 返回值里**只有 uid 与昵称**，不含任何凭据。
        /// </summary>
        internal static async Task<(string Status, IReadOnlyList<string> Roles)> VerifyAsync(
            string account, MiyousheCredential credential, CancellationToken ct)
        {
            try
            {
                var client = MiyousheService.Client(null, credential.Region);
                var roles = await client.GetRolesAsync(account, credential, ct).ConfigureAwait(false);
                if (roles.Count == 0) return ("凭据可请求但未绑定原神角色", Array.Empty<string>());
                var list = roles.Select(r =>
                    "uid=" + MiyousheCredentialStore.Mask(r.GameUid) +
                    " region=" + r.Region +
                    " nickname=" + (r.Nickname ?? "") +
                    " level=" + r.Level).ToList();
                return ("有效：已取到 " + roles.Count + " 个原神角色", list);
            }
            catch (MiyousheRequestException ex)
            {
                if (ex.Retcode == -10001) return ("凭据已失效（服务端判定未登录），请重新获取 cookie", Array.Empty<string>());
                return ("自检失败：接口返回 retcode=" + ex.Retcode, Array.Empty<string>());
            }
            catch (InvalidOperationException)
            {
                // AddAuthorized 在 salt 为空时抛这个 —— 是**配置未就绪**，不是凭据坏了。
                // 必须区分开，否则用户会以为导入失败而白白重扫一次二维码。
                return ("凭据已保存；自检跳过——未配置米游社 DS salt（在设置里更新 salt 后会自动校验）",
                    Array.Empty<string>());
            }
            catch (Exception)
            {
                return ("自检未完成：网络或配置问题（未记录任何凭据）", Array.Empty<string>());
            }
        }

        /// <summary>
        /// 只识别不导入：给网页「选择路径」用，回显判别依据。
        /// 不读取、不返回任何 cookie 值。
        /// </summary>
        internal static MiyousheImportOutcome Probe(string account, string path)
        {
            var (kind, targets, probed, detail) = Resolve(path, (account ?? "").Trim());
            var items = targets.Select((t, i) => new MiyousheImportItem(
                account ?? "", true, kind, System.IO.Path.GetFileName(t.FilePath),
                DescribeFields(t.CookieText), "", "识别成功（未导入）", "", Array.Empty<string>())).ToList();
            return new MiyousheImportOutcome(targets.Count > 0, path ?? "", kind, 0, 0, items, detail, probed);
        }
    }
}
