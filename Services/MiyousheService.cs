using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
        private static int _running;
        internal static int Running => Volatile.Read(ref _running);

        internal static Func<string,MiyousheClient>? ClientFactoryForTest;
        internal static Func<string,string,MiyousheClient>? RegionClientFactoryForTest;
        internal static Func<int,Task>? DelayForTest;
        internal static MiyousheClient Client(string? salt = null, string region="cn")
        {
            string configured = region=="os" ? App.Config?.Settings?.MiyousheOsDsSalt??"" : App.Config?.Settings?.MiyousheDsSalt??"";
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

        internal static Task<MiyousheLoginStatus> LoginStatusAsync(string account,CancellationToken ct)
            => MiyousheCredentialStore.StatusAsync(account,ct);

        internal static async Task<object> RunForAccountAsync(string account,bool sign,bool ledger,CancellationToken ct)
        {
            RequireAccount(account);
            var credential = await MiyousheCredentialStore.LoadAsync(account,ct).ConfigureAwait(false);
            if (credential == null) return new { ok=false,error="未登录，请先扫码" };
            if (PausedUntil.TryGetValue(account,out var until) && until > DateTime.Now)
                return new { ok=false,error="账号处于退避期，请稍后重试或重新扫码",retryAfter=until };
            if (!await NetworkGate.WaitAsync(0,ct).ConfigureAwait(false))
                return new { ok=false,error="另一账号任务进行中，请稍后重试",state="queued" };
            Interlocked.Exchange(ref _running,1);
            bool healthAtStart=MiyousheStore.Shared.IsHealthy;
            try
            {
                var client = Client();
                var roles = await client.GetRolesAsync(account,credential,ct).ConfigureAwait(false);
                if (roles.Count == 0) return new { ok=false,error="米游社账号未绑定原神角色" };
                int signed=0, entries=0;
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
                            await MiyousheStore.Shared.SaveSignAsync(result,ct).ConfigureAwait(false); signed++;
                        }
                    }
                    if (ledger)
                        entries += await PullWithStatusAsync(client,account,credential,role,DateTime.Now.Month,ct).ConfigureAwait(false);
                    await DelayAsync(ct).ConfigureAwait(false);
                }
                if (healthAtStart && !MiyousheStore.Shared.IsHealthy) throw new IOException("米游社数据库写入失败");
                // ★ t17：拉取完成后立即分类（消费方规则，失败不影响拉取结果）。
                if (ledger && entries > 0)
                {
                    try { _ = MoraClassifier.ClassifyAsync(account, ct); }
                    catch { /* 分类失败不影响拉取；下次拉取/手动触发会补 */ }
                }
                credential.LastOkAt=DateTimeOffset.Now;
                await MiyousheCredentialStore.SaveAsync(account,credential,ct).ConfigureAwait(false);
                await MiyousheStore.Shared.SetCredentialStateAsync(account,"ok","最近请求成功",NowMs(),ct).ConfigureAwait(false);
                return new { ok=true,account,roles=roles.Count,signed,entries };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { await HandleFailureAsync(account,ex,ct).ConfigureAwait(false); return new { ok=false,error=SafeError(ex) }; }
            finally { NetworkGate.Release(); Interlocked.Exchange(ref _running,0); }
        }

        internal static IReadOnlyList<MiyoushePullStatus> Statuses() => new List<MiyoushePullStatus>(PullStatuses.Values);
        internal static void ResetTest() { PausedUntil.Clear(); PullStatuses.Clear(); ClientFactoryForTest=null; DelayForTest=null; }
        private static async Task<int> PullWithStatusAsync(MiyousheClient client,string account,MiyousheCredential credential,
            MiyousheRole role,int month,CancellationToken ct)
        {
            long start=NowMs(); string id=Guid.NewGuid().ToString("N");
            var status=new MiyoushePullStatus(id,account,role.GameUid,"running","逐条流水拉取中",0,0,start,start);
            PullStatuses[account+"\u001f"+role.GameUid]=status;
            await MiyousheStore.Shared.SavePullStatusAsync(status,ct).ConfigureAwait(false);
            try
            {
                await client.PullLedgerAsync(account,credential,role,month,async (pages,inserted,token) =>
                {
                    status=status with { Pages=pages,Inserted=inserted,UpdatedAtMs=NowMs() };
                    PullStatuses[account+"\u001f"+role.GameUid]=status;
                    await MiyousheStore.Shared.SavePullStatusAsync(status,token).ConfigureAwait(false);
                },ct).ConfigureAwait(false);
                status=status with { State=status.Inserted>0?"inserted":"skipped",Detail=status.Inserted>0?"分页原始流水已入库":"接口无新增原始流水",UpdatedAtMs=NowMs() };
            }
            catch
            {
                status=status with { State="failed",Detail="拉取中断；已完成页面保留",UpdatedAtMs=NowMs() };
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
                if (request.Retcode == -10001) { state="needs_login";detail="凭据失效，请重新扫码"; }
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