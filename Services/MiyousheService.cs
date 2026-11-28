using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    public sealed record MiyoushePullStatus(string BatchId, string Account, string GameUid, string State,
        string Detail, int Pages, int Inserted, long StartedAtMs, long UpdatedAtMs);

    /// <summary>扫码会话、单账号串行任务、失效退避与结果持久化。</summary>
    internal static class MiyousheService
    {
        private sealed class QrState { internal string Account=""; internal string Ticket=""; internal string Region="cn"; internal DateTime Expires; }
        private static readonly ConcurrentDictionary<string,QrState> QrSessions = new();
        private static readonly ConcurrentDictionary<string,MiyoushePullStatus> PullStatuses = new();
        private static readonly ConcurrentDictionary<string,DateTime> PausedUntil = new();
        private static readonly SemaphoreSlim NetworkGate = new(1,1);
        internal static event Action<string, string>? LedgerRefreshed;
        private static int _running;
        internal static int Running => Volatile.Read(ref _running);

        internal static Func<string,MiyousheClient>? ClientFactoryForTest;
        internal static Func<string,string,MiyousheClient>? RegionClientFactoryForTest;
        internal static Func<int,Task>? DelayForTest;
        internal static Func<DateTime>? LedgerNowForTest;
        internal static string EffectiveDsSalt(AppSettings? settings, string region)
            => region == "os"
                ? settings?.MiyousheOsDsSalt ?? ""
                : string.IsNullOrWhiteSpace(settings?.MiyousheDsSalt)
                    ? AppSettings.DefaultMiyousheDsSalt
                    : settings!.MiyousheDsSalt.Trim();

        internal static MiyousheClient Client(string? salt = null, string region="cn")
        {
            string configured = EffectiveDsSalt(App.Config?.Settings, region);
            return RegionClientFactoryForTest?.Invoke(salt ?? configured,region) ?? ClientFactoryForTest?.Invoke(salt ?? configured) ?? new(salt ?? configured, null, region);
        }

        internal static Task<MiyousheQrSession> BeginQrAsync(string account,CancellationToken ct) => BeginQrAsync(account,"cn",ct);
        internal static async Task<MiyousheQrSession> BeginQrAsync(string account,string region,CancellationToken ct)
        {
            RequireAccount(account);
            // ★ t40：按账号取持久化 device_id（老账号沿用凭据值；新账号生成一次落盘），
            //   不再每次 NewGuid（风控特征：服务端视角=每秒一台新设备）
            string device = await MiyousheCredentialStore.GetOrCreateDeviceIdAsync(account, ct).ConfigureAwait(false);
            var pair = await Client(null,region).CreateQrAsync(ct, device).ConfigureAwait(false);
            string id = Guid.NewGuid().ToString("N");
            QrSessions[id] = new QrState { Account=account,Region=region,Ticket=pair.Ticket,Expires=DateTime.UtcNow.AddMinutes(5) };
            return new(id,account,"Created",MiyousheClient.CreateQrPng(pair.Url),
                "请用米游社 App 扫码，并在手机端确认；二维码约 5 分钟有效");
        }

        internal static async Task<MiyousheQrSession> PollQrAsync(string id,CancellationToken ct)
        {
            if (!QrSessions.TryGetValue(id,out var state)) throw new InvalidOperationException("二维码会话不存在，请重新生成");
            if (state.Expires < DateTime.UtcNow) { QrSessions.TryRemove(id,out _); throw new InvalidOperationException("二维码已过期，请重新生成"); }
            try
            {
                var result = await Client(null,state.Region).PollQrAsync(state.Ticket,state.Account,ct).ConfigureAwait(false);
                if (result.Credential != null)
                {
                    QrSessions.TryRemove(id,out _);
                    PausedUntil.TryRemove(state.Account,out _);
                    await MiyousheStore.Shared.SetCredentialStateAsync(state.Account,"ok","扫码确认成功，凭据已加密保存",NowMs(),ct).ConfigureAwait(false);
                    return new(id,state.Account,"Confirmed","", "扫码成功，凭据已加密保存",
                        result.Credential.AccountId,MiyousheCredentialStore.Mask(result.Credential.Aid));
                }
                return new(id,state.Account,result.Status,"","等待手机端操作");
            }
            catch (MiyousheRequestException ex) when (ex.Retcode is -3501 or -3505)
            { QrSessions.TryRemove(id,out _); throw new InvalidOperationException(ex.Retcode == -3501 ? "二维码已过期，请重新生成" : "用户已取消扫码"); }
        }

        internal static async Task<MiyousheLoginStatus> LoginStatusAsync(string account,CancellationToken ct)
        {
            var status = await MiyousheCredentialStore.StatusAsync(account,ct).ConfigureAwait(false);
            if (status.State != "stored") return status;
            var lastCheck = await MiyousheStore.Shared.GetCredentialStateAsync(account,ct).ConfigureAwait(false);
            return lastCheck is { State: "needs_login" } && lastCheck.UpdatedAtMs >= status.UpdatedAtMs
                ? status with { State = lastCheck.State, Detail = lastCheck.Detail }
                : status;
        }

        // ── t59：角色列表与默认角色（主程序数据中心要用）─────────────────
        //
        //  为什么放这里：主程序的 DataCenterPage 直接调，不走 HTTP 回环端口 ——
        //  避免「TaskEdit 没起来就不可用」的耦合。
        //  读的是**本地 my_role 表**，不现场请求米游社（避免风控）。

        /// <summary>读该账号绑定的原神角色（本地库，不联网）。无角色时返回空列表 + 原因。</summary>
        public static async Task<(List<MiyousheRole> Roles, string Reason)> GetRolesForUiAsync(
            string account, CancellationToken ct = default)
        {
            var roles = new List<MiyousheRole>();
            try
            {
                roles.AddRange(await MiyousheStore.Shared.QueryRolesAsync(account, ct).ConfigureAwait(false));
            }
            catch { }
            if (roles.Count > 0) return (roles, "");

            string reason;
            try
            {
                var credential = await MiyousheCredentialStore.LoadAsync(account, "", ct).ConfigureAwait(false);
                reason = credential == null
                    ? "需要重新登录米游社"
                    : "已登录，但还没有角色数据（等一次拉取或签到后就有了）";
            }
            catch { reason = "需要重新登录米游社"; }
            return (roles, reason);
        }

        /// <summary>当前默认角色的 gameUid（没有则空串）。</summary>
        public static string GetDefaultRole(string account)
        {
            try
            {
                var map = App.Config?.Settings?.MiyousheDefaultRole;
                if (map != null && map.TryGetValue(account, out var uid)) return uid ?? "";
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 设为默认角色（持久化到 Settings.MiyousheDefaultRole）。
        /// ★ 写入前校验 UID 确实属于该账号的角色列表 —— 防止前端传错。
        /// </summary>
        public static async Task<bool> SetDefaultRoleAsync(string account, string gameUid, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(gameUid)) return false;
            var (roles, _) = await GetRolesForUiAsync(account, ct).ConfigureAwait(false);
            if (!roles.Any(r => string.Equals(r.GameUid, gameUid, StringComparison.OrdinalIgnoreCase)))
                return false;   // UID 不属于该账号，拒绝
            return SaveDefaultRole(account, gameUid);
        }

        private static readonly object DefaultRoleGate = new();

        // Callers validate the UID against the account's stored roles before saving.
        internal static bool SaveDefaultRole(string account, string gameUid)
        {
            lock (DefaultRoleGate)
            {
                var config = App.Config;
                if (config?.Settings == null) return false;
                if (string.Equals(GetDefaultRole(account), gameUid, StringComparison.OrdinalIgnoreCase)) return true;

                var roles = new Dictionary<string, string>(config.Settings.MiyousheDefaultRole, StringComparer.OrdinalIgnoreCase);
                if (gameUid.Length == 0) roles.Remove(account);
                else roles[account] = gameUid;
                config.Settings.MiyousheDefaultRole = roles;
                ConfigStore.Save(config);
                return true;
            }
        }

        internal sealed record WorkResult(bool ok, string? error = null, string? state = null,
            string? account = null, int roles = 0, int signed = 0, int entries = 0, DateTime? retryAfter = null);

        internal static async Task<WorkResult> RunForAccountAsync(string account,bool sign,bool ledger,CancellationToken ct,
            bool retryLogin = false, string? expectedLedgerUid = null)
        {
            RequireAccount(account);
            var credential = await MiyousheCredentialStore.LoadAsync(account,ct).ConfigureAwait(false);
            if (credential == null || (ledger && string.IsNullOrWhiteSpace(credential.CookieToken) &&
                string.IsNullOrWhiteSpace(credential.RawCookie)))
                return new WorkResult(false, "未登录，请先扫码", "needs_login");
            var lastCheck = await MiyousheStore.Shared.GetCredentialStateAsync(account,ct).ConfigureAwait(false);
            bool rejected = lastCheck is { State: "needs_login" } && lastCheck.UpdatedAtMs >= credential.UpdatedAt.ToUnixTimeMilliseconds();
            if (rejected && !retryLogin)
                return new WorkResult(false, lastCheck!.Detail, "needs_login");
            if (!(rejected && retryLogin) && PausedUntil.TryGetValue(account,out var until) && until > DateTime.Now)
                return new WorkResult(false, "账号处于退避期，请稍后重试或重新扫码", "failed", retryAfter: until);
            if (expectedLedgerUid != null && GetDefaultRole(account) != expectedLedgerUid)
                return new WorkResult(false, "默认角色已更改", "changed");
            if (!await NetworkGate.WaitAsync(0,ct).ConfigureAwait(false))
                return new WorkResult(false, "另一账号任务进行中，请稍后重试", "queued");
            Interlocked.Exchange(ref _running,1);
            bool healthAtStart=MiyousheStore.Shared.IsHealthy;
            try
            {
                var client = Client();
                var roles = await client.GetRolesAsync(account,credential,ct).ConfigureAwait(false);
                if (roles.Count == 0) return new WorkResult(false, "米游社账号未绑定原神角色", "skipped");
                string ledgerUid = ledger ? GetDefaultRole(account) : "";
                if (expectedLedgerUid != null && ledgerUid != expectedLedgerUid)
                    return new WorkResult(false, "默认角色已更改", "changed");
                string ledgerError = "";
                if (ledger && !roles.Any(r => r.GameUid == ledgerUid && ledgerUid.Length > 0))
                    ledgerError = ledgerUid.Length == 0 ? "请先为此账号设置默认原神角色" : "默认角色已不在该账号的角色列表，请重新选择";
                if (!sign && ledgerError.Length > 0) return new WorkResult(false, ledgerError, "default_role");
                int signed=0, entries=0;
                string? signError = null;
                foreach (var role in roles)
                {
                    if (sign)
                    {
                        string day=DateTime.Now.ToString("yyyy-MM-dd");
                        if (!await MiyousheStore.Shared.HasSignAsync(account,role.GameUid,day,ct).ConfigureAwait(false))
                        {
                            MiyousheSignResult result;
                            try
                            {
                                var (already,_) = await client.GetSignInfoAsync(credential,role,ct).ConfigureAwait(false);
                                result = already
                                    ? new MiyousheSignResult(account,role.GameUid,day,"already",0,"查询到今日已签到","",0,NowMs())
                                    : await client.SignAsync(account,credential,role,day,ct).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                result=SignFailure(account,role.GameUid,day,ex);
                                await HandleFailureAsync(account,ex,ct).ConfigureAwait(false);
                            }
                            await MiyousheStore.Shared.SaveSignAsync(result,ct).ConfigureAwait(false);
                            if (result.State is "success" or "already") signed++;
                            else signError ??= result.State;
                        }
                    }
                    if (!sign && role.GameUid != ledgerUid) continue;
                    if (ledger && ledgerError.Length == 0 && role.GameUid == ledgerUid)
                    {
                        var now = LedgerNowForTest?.Invoke() ?? DateTime.Now;
                        entries += await PullWithStatusAsync(client,account,credential,role,now,ct).ConfigureAwait(false);
                        if (entries > 0 && MiyousheStore.Shared.IsHealthy) NotifyLedgerRefreshed(account, ledgerUid);
                    }
                    await DelayAsync(ct).ConfigureAwait(false);
                }
                if (healthAtStart && !MiyousheStore.Shared.IsHealthy) throw new IOException("米游社数据库写入失败");
                // Raw ledger rows are already durable. Link only recent eligible entries in small batches.
                if (ledger && ledgerError.Length == 0)
                {
                    try { await MoraRouteIncome.UpdateAsync(account, ledgerUid, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        App.AppendErrorLog("MoraRouteIncome", ex);
                    }
                }
                if (signError != null)
                    return new WorkResult(false, "部分原神角色未完成签到", signError, account, roles.Count, signed, entries);
                PausedUntil.TryRemove(account,out _);
                // 查询期间可能已重新登录；这里只记任务状态，避免用旧 Cookie 覆写新凭据。
                await MiyousheStore.Shared.SetCredentialStateAsync(account,"ok","最近请求成功",NowMs(),ct).ConfigureAwait(false);
                if (ledgerError.Length > 0) return new WorkResult(false, ledgerError, account: account, signed: signed, entries: entries);
                return new WorkResult(true, account: account, roles: roles.Count, signed: signed, entries: entries);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var latest = await MiyousheCredentialStore.LoadAsync(account,ct).ConfigureAwait(false);
                if (latest?.UpdatedAt == credential.UpdatedAt)
                    await HandleFailureAsync(account,ex,ct).ConfigureAwait(false);
                string state = ex is MiyousheRequestException request && request.RequiresLogin ? "needs_login" : "failed";
                return new WorkResult(false, SafeError(ex), state);
            }
            finally { Interlocked.Exchange(ref _running,0); NetworkGate.Release(); }
        }

        private static void NotifyLedgerRefreshed(string account, string gameUid)
        {
            if (LedgerRefreshed is not { } handlers) return;
            foreach (Action<string, string> handler in handlers.GetInvocationList())
            {
                try { handler(account, gameUid); }
                catch (Exception) { LogBus.Warn("摩拉流水更新通知失败"); }
            }
        }

        internal static DateTime[] LedgerMonthsForBusinessDay(DateTime now)
        {
            DateTime month = now.Date.AddDays(1 - now.Day);
            DateTime businessDay = DogfoodRecordReader.CurrentBusinessDay(now);
            return businessDay < month ? [month, month.AddMonths(-1)] : [month];
        }

        internal static IReadOnlyList<MiyoushePullStatus> Statuses() => new List<MiyoushePullStatus>(PullStatuses.Values);
        internal static void ResetTest() { PausedUntil.Clear(); PullStatuses.Clear(); ClientFactoryForTest=null; DelayForTest=null; LedgerNowForTest=null; }
        private static async Task<int> PullWithStatusAsync(MiyousheClient client,string account,MiyousheCredential credential,
            MiyousheRole role,DateTime now,CancellationToken ct)
        {
            long start=NowMs(); string id=Guid.NewGuid().ToString("N");
            var status=new MiyoushePullStatus(id,account,role.GameUid,"running","逐条流水拉取中",0,0,start,start);
            PullStatuses[account+"\u001f"+role.GameUid]=status;
            await MiyousheStore.Shared.SavePullStatusAsync(status,ct).ConfigureAwait(false);
            MiyousheHost.NoteLedgerAttempt(account, role.GameUid, DateTimeOffset.FromUnixTimeMilliseconds(start));
            try
            {
                foreach (DateTime month in LedgerMonthsForBusinessDay(now))
                {
                    int previousPages = status.Pages, previousInserted = status.Inserted;
                    await client.PullLedgerAsync(account,credential,role,month,now.Date,async (pages,inserted,token) =>
                    {
                        status=status with { Pages=previousPages+pages,Inserted=previousInserted+inserted,UpdatedAtMs=NowMs() };
                        PullStatuses[account+"\u001f"+role.GameUid]=status;
                        await MiyousheStore.Shared.SavePullStatusAsync(status,token).ConfigureAwait(false);
                    },ct).ConfigureAwait(false);
                }
                status=status with { State=status.Inserted>0?"inserted":"skipped",Detail=status.Inserted>0?"分页原始流水已入库":"接口无新增原始流水",UpdatedAtMs=NowMs() };
            }
            catch (Exception ex)
            {
                string detail = ex is MiyousheRequestException request ? request.Message : "拉取中断，已完成页面保留";
                status=status with { State="failed",Detail=detail,UpdatedAtMs=NowMs() };
                PullStatuses[account+"\u001f"+role.GameUid]=status;
                await MiyousheStore.Shared.SavePullStatusAsync(status,CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            PullStatuses[account+"\u001f"+role.GameUid]=status;
            await MiyousheStore.Shared.SavePullStatusAsync(status,ct).ConfigureAwait(false);
            return status.Inserted;
        }

        private static MiyousheSignResult SignFailure(string account,string uid,string day,Exception ex)
        {
            int code=ex is MiyousheRequestException request?request.Retcode:-1;
            string state=code == -10001?"needs_login":code is 1034 or 10035 or 5003 or 10041?"risk":"failed";
            return new(account,uid,day,state,code,"签到失败；详见账号状态（不记录凭据或请求内容）","",0,NowMs());
        }

        private static async Task HandleFailureAsync(string account,Exception ex,CancellationToken ct)
        {
            string state="failed", detail="网络请求失败，稍后自动重试";
            int minutes=Math.Clamp(App.Config?.Settings?.MiyousheRetryMinutes??60,1,1440);
            if (ex is MiyousheRequestException request)
            {
                if (request.RequiresLogin) { state="needs_login";detail=request.Message; }
                else if (request.Retcode is 1034 or 10035 or 5003 or 10041)
                { state="risk";detail="触发风控，自动任务已暂停，请人工处理";minutes=Math.Max(minutes,1440); }
                else detail="接口返回失败（retcode="+request.Retcode+"）";
            }
            PausedUntil[account]=DateTime.Now.AddMinutes(minutes);
            await MiyousheStore.Shared.SetCredentialStateAsync(account,state,detail,NowMs(),ct).ConfigureAwait(false);
            // 固定文本：禁止 ex.ToString()/请求 URI/响应头，避免凭据经日志泄漏。
            App.AppendErrorLog("Miyoushe."+state,new InvalidOperationException("米游社任务失败；账号状态已更新，不记录请求或凭据"));
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static Task DelayAsync(CancellationToken ct) => DelayForTest?.Invoke(0) ?? Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(2,6)),ct);
        private static void RequireAccount(string account)
        { if (MiyousheCredentialStore.SafeAccount(account).Length==0) throw new ArgumentException("账号名无效"); }
        private static string SafeError(Exception ex)
            => ex is MiyousheRequestException ? ex.Message : "米游社任务失败，请稍后重试或检查配置";
    }
}
