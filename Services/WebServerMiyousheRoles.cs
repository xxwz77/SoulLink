using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 米游社原神角色列表与「默认角色」（t50）。
    ///
    /// ── 用户原话 ─────────────────────────────────────────────────────
    ///   「米游社那里显示一个17什么的账号，但是没有名字，分不清啊。
    ///    而且如果有绑定多个原神账号，应该要可以切换，设为默认，
    ///    不然要取哪个数据呢？」
    ///
    /// ── 为什么需要这个接口 ───────────────────────────────────────────
    ///   一个米游社账号可以绑定**多个原神角色**（不同区服 / 大号小号）。
    ///   `MiyousheService` 的拉取与签到**本来就会遍历全部角色**（不跳过任何一个），
    ///   但网页没有任何角色列表 UI —— 用户既看不到绑了几个号，也无法指定
    ///   「默认看哪个」，摩拉数据是谁的也不清楚。
    ///
    /// ── 设计取舍 ────────────────────────────────────────────────────
    ///   · 角色来源 = **本地米游社库**（`miyoushe.db` 的 roles 表），
    ///     **不现场请求米游社**：现场请求有延迟、会触发风控、离线时看不了。
    ///   · 默认角色只存 GameUid（`Settings.MiyousheDefaultRole`，账号→Uid），
    ///     不存任何凭据；写入前**校验该 Uid 确实在角色列表里**，防手滑写脏数据。
    ///   · 无凭据 / 无角色一律 `ok=true` + 空列表 + `reason`，
    ///     让界面能优雅降级（显示「需重新登录」而不是报错白屏）。
    /// </summary>
    public static partial class WebServer
    {
        /// <summary>GET /api/miyoushe/roles?user=账号 —— 角色列表 + 当前默认。</summary>
        private static async Task HandleMiyousheRolesAsync(HttpListenerContext ctx, string account)
        {
            await WriteMiyousheRolesAsync(ctx, account, null).ConfigureAwait(false);
        }

        /// <summary>POST /api/miyoushe/roles —— 设默认；<paramref name="wantUid"/> 空串表示清除。</summary>
        private static async Task HandleMiyousheRolesAsync(HttpListenerContext ctx, string account, string? wantUid)
        {
            await WriteMiyousheRolesAsync(ctx, account, wantUid ?? "").ConfigureAwait(false);
        }

        /// <summary>
        /// 统一实现：<paramref name="setToUid"/> 为 null = 只读；非 null = 先写再返回。
        /// </summary>
        private static async Task WriteMiyousheRolesAsync(HttpListenerContext ctx, string account, string? setToUid)
        {
            if (string.IsNullOrWhiteSpace(account))
            {
                await WriteJson(ctx, new { ok = false, error = "缺少 user" }).ConfigureAwait(false);
                return;
            }

            AppSettings? settings = App.Config?.Settings;

            // 角色列表来自本地库（limit=1：只要 roles 字段，不拉流水）
            MiyousheArchive archive;
            try { archive = await MiyousheStore.Shared.QueryAsync(account, 1).ConfigureAwait(false); }
            catch (Exception ex)
            {
                await WriteJson(ctx, new { ok = false, account, error = "读取米游社库失败：" + ex.Message }).ConfigureAwait(false);
                return;
            }

            IReadOnlyList<MiyousheRole> roles = archive.Roles ?? Array.Empty<MiyousheRole>();

            // ── 写入默认角色 ──────────────────────────────────────────
            string? writeError = null;
            if (setToUid != null)
            {
                if (settings == null) writeError = "配置未加载";
                else if (setToUid.Length == 0)
                {
                    settings.MiyousheDefaultRole.Remove(account);
                    try { ConfigStore.Save(App.Config!); } catch (Exception ex) { writeError = "保存失败：" + ex.Message; }
                }
                else
                {
                    bool exists = roles.Any(r => string.Equals(r.GameUid, setToUid, StringComparison.OrdinalIgnoreCase));
                    if (!exists) writeError = "该 UID 不在此账号的角色列表里";
                    else
                    {
                        settings.MiyousheDefaultRole[account] = setToUid;
                        try { ConfigStore.Save(App.Config!); } catch (Exception ex) { writeError = "保存失败：" + ex.Message; }
                    }
                }
            }

            // ── 读当前默认 ────────────────────────────────────────────
            string def = "";
            try { settings?.MiyousheDefaultRole.TryGetValue(account, out def); } catch { }
            def ??= "";

            var items = new List<object>(roles.Count);
            foreach (MiyousheRole r in roles)
            {
                items.Add(new
                {
                    gameUid = r.GameUid,
                    nickname = r.Nickname,
                    level = r.Level,
                    region = r.Region,
                    // 显示名：昵称优先，缺了退回 UID（绝不只剩一串数字却没有可辨识信息）
                    display = string.IsNullOrWhiteSpace(r.Nickname) ? r.GameUid : r.Nickname,
                    isDefault = string.Equals(r.GameUid, def, StringComparison.OrdinalIgnoreCase),
                });
            }

            string reason = "";
            if (roles.Count == 0)
            {
                string credState = archive.CredentialState?.State ?? "";
                reason = credState != "ok"
                    ? "需要重新登录米游社（凭据不可用）"
                    : "凭据有效，但还没有拉取到角色数据（点「立即拉取」或等待定时任务）";
            }
            else if (def.Length == 0)
            {
                reason = "有多个角色但未设默认，界面默认展示第一个";
            }

            await WriteJson(ctx, new
            {
                ok = writeError == null,
                account,
                count = items.Count,
                hasDefault = def.Length > 0,
                defaultRole = def,
                roles = items,
                reason,
                error = writeError,
            }).ConfigureAwait(false);
        }
    }
}
