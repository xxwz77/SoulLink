using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>t19 扩展批次编排；t16 原神/流水路径保留，本类提供统一新接口。</summary>
    internal static class MiyousheBatchService
    {
        private static readonly SemaphoreSlim Gate=new(1,1);
        internal static Func<HttpClient,string,IMiyousheTaskClient>? ClientFactoryForTest;
        internal static Func<int,Task>? DelayForTest;
        internal static readonly List<int> BackoffCurveForTest=new();
        internal static readonly List<int> RequestIntervalsForTest=new();
        internal static Func<int,Task>? PacingForTest;
        internal static int Running => Gate.CurrentCount==0?1:0;

        internal static async Task<IReadOnlyList<MiyousheTaskExecution>> RunAsync(string account,bool scheduled,CancellationToken ct)
        {
            var settings=App.Config?.Settings ?? throw new InvalidOperationException("配置未加载");
            string batch=Guid.NewGuid().ToString("N");long start=NowMs();
            await MiyousheStore.Shared.SaveBatchAsync(new MiyousheBatch(batch,account,"running",start,0),ct).ConfigureAwait(false);
            var results=new List<MiyousheTaskExecution>();
            try
            {
                if (scheduled && (!settings.MiyousheSignEnabled || !MiyousheSignPreferences.AccountEnabled(settings, account)))
                {
                    results.Add(new(batch,account,"all","all","","skipped",0,"总开关关闭，全部不执行",0,0,NowMs()));
                    return results;
                }
                if(!await Gate.WaitAsync(0,ct).ConfigureAwait(false))
                {
                    results.Add(new(batch,account,"all","all","","queued",0,"另一批次进行中",0,0,NowMs()));
                    return results;
                }
                try
                {
                    string region = MiyousheSignPreferences.RegionFor(settings, account);
                    var credential=await MiyousheCredentialStore.LoadAsync(account,region,ct).ConfigureAwait(false);
                    if(credential==null)
                    {
                        results.Add(new(batch,account,region,"all","","needs_login",-10001,"该区域未登录，请先扫码",0,0,NowMs()));
                        return results;
                    }
                    using var http=new HttpClient();
                    var client=ClientFactoryForTest?.Invoke(http,region) ?? new MiyousheTaskClient(http,region);
                    foreach (var game in MiyousheSignPreferences.Projects(settings, account))
                    {
                        string id = MiyousheSignPreferences.ProjectId(game);
                        if (!MiyousheSignPreferences.ProjectEnabled(settings, account, id))
                        {
                            results.Add(Skip(batch,account,region,id,"不参与/分项开关关闭"));continue;
                        }
                        if(scheduled && await MiyousheStore.Shared.HasTaskSuccessAsync(account,id,DateTime.Now.ToString("yyyy-MM-dd"),ct).ConfigureAwait(false))
                        { results.Add(Skip(batch,account,region,id,"今日已完成，幂等跳过"));continue; }
                        if (id == MiyousheSignPreferences.DomesticGenshin)
                        {
                            var sign = await MiyousheService.RunForAccountAsync(account, true, false, ct).ConfigureAwait(false);
                            string state = sign.ok ? (sign.signed > 0 ? "success" : "already") : (sign.state ?? "failed");
                            results.Add(new(batch,account,region,id,"",state,0,sign.error ?? "原神签到已处理",0,0,NowMs()));
                            continue;
                        }
                        results.Add(await client.GameAsync(batch,account,credential,game,DateTime.Now.ToString("yyyy-MM-dd"),ct).ConfigureAwait(false));
                        if(PacingForTest!=null) await PacingForTest(Math.Clamp(settings.MiyousheCommunityDelaySeconds,8,120)).ConfigureAwait(false);
                        else await DelayAsync(0).ConfigureAwait(false);
                    }
                    foreach(var task in MiyousheCatalog.Community.Values.Where(t=>!scheduled && t.Region==region))
                    {
                        string id=task.Id+"@"+region;
                        if(!Enabled(settings,account,id))
                        {
                            results.Add(Skip(batch,account,region,id,"不参与/分项开关关闭"));continue;
                        }
                        if(scheduled && await MiyousheStore.Shared.HasTaskSuccessAsync(account,id,DateTime.Now.ToString("yyyy-MM-dd"),ct).ConfigureAwait(false))
                        { results.Add(Skip(batch,account,region,id,"今日已完成，幂等跳过"));continue; }
                        results.Add(await client.CommunityAsync(batch,account,credential,task,settings.MiyousheForumIds,
                            Math.Clamp(settings.MiyousheCommunityMaxRetries,0,3),Math.Clamp(settings.MiyousheCommunityBackoffBaseSeconds,5,300),
                            DelayAsync,ct).ConfigureAwait(false));
                    }
                }
                finally{Gate.Release();}
                return results;
            }
            finally
            {
                foreach(var row in results) await MiyousheStore.Shared.SaveTaskResultAsync(row,ct).ConfigureAwait(false);
                string state=results.Any(x=>x.State is "failed" or "risk" or "needs_login" or "queued")?"failed":"completed";
                await MiyousheStore.Shared.SaveBatchAsync(new MiyousheBatch(batch,account,state,start,NowMs()),ct).ConfigureAwait(false);
            }
        }

        private static MiyousheTaskExecution Skip(string batch,string account,string region,string id,string detail)
            => new(batch,account,region,id,"","skipped",0,detail,0,0,NowMs());
        private static bool Enabled(AppSettings settings,string account,string id)
            => settings.MiyousheTaskEnabled.TryGetValue(id,out bool enabled)&&enabled
               && !settings.MiyousheExcludedTasks.Contains(account+"|"+id);
        private static async Task DelayAsync(int seconds)
        {
            if(seconds>0) BackoffCurveForTest.Add(seconds);
            if(DelayForTest!=null){await DelayForTest(seconds).ConfigureAwait(false);return;}
            await Task.Delay(TimeSpan.FromSeconds(seconds>0?seconds:Math.Clamp(App.Config?.Settings?.MiyousheCommunityDelaySeconds??12,8,120))).ConfigureAwait(false);
        }
        private static long NowMs()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}