using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    internal sealed record MiyousheTaskExecution(
        string BatchId,string Account,string Region,string TaskId,string GameUid,
        string State,int Retcode,string Detail,int Attempts,long DurationMs,long TsMs);
    internal sealed record MiyousheBatch(string BatchId,string Account,string State,long StartedAtMs,long EndedAtMs);

    /// <summary>统一游戏/社区签到 HTTP；不实现任何验证码/挑战参数。</summary>
    internal interface IMiyousheTaskClient
    {
        Task<MiyousheTaskExecution> GameAsync(string batch,string account,MiyousheCredential credential,MiyousheGameProfile game,string date,CancellationToken ct);
        Task<MiyousheTaskExecution> CommunityAsync(string batch,string account,MiyousheCredential credential,MiyousheCommunityProfile task,IReadOnlyList<string> forums,int maxRetries,int baseBackoff,Func<int,Task> delay,CancellationToken ct);
    }
    internal class MiyousheTaskClient : IMiyousheTaskClient
    {
        private readonly HttpClient _http;
        private readonly string _region;
        internal MiyousheTaskClient(HttpClient http,string region) { _http=http;_region=region; }

        public virtual async Task<MiyousheTaskExecution> GameAsync(string batch,string account,MiyousheCredential credential,
            MiyousheGameProfile game,string date,CancellationToken ct)
        {
            long start=NowMs();string id=game.GameBiz+"@"+game.Region;
            if(!game.ActVerified || game.ActId.Length==0 || game.EndpointBase.Length==0)
                return new(batch,account,game.Region,id,"","skipped",0,"act_id/端点未核实，未发送请求",0,0,NowMs());
            string uid="",region="";
            try
            {
                string rolesUrl=MiyousheCatalog.Regions[game.Region].ApiBase+"/binding/api/getUserGameRolesByCookie?game_biz="+Uri.EscapeDataString(game.GameBiz);
                var roles=await SendAsync(new HttpRequestMessage(HttpMethod.Get,rolesUrl),credential,"game_biz="+Uri.EscapeDataString(game.GameBiz),null,ct).ConfigureAwait(false);
                if(!roles.TryGetProperty("list",out var list) || list.ValueKind!=JsonValueKind.Array || list.GetArrayLength()==0)
                    return new(batch,account,game.Region,id,"","skipped",0,"未绑定该游戏角色",1,NowMs()-start,NowMs());
                var first=list[0];uid=Text(first,"game_uid");region=Text(first,"region");
                string query="act_id="+Uri.EscapeDataString(game.ActId)+"&lang=zh-cn";
                if(uid.Length>0) query+="&uid="+Uri.EscapeDataString(uid)+"&region="+Uri.EscapeDataString(region);
                using(var infoRequest=new HttpRequestMessage(HttpMethod.Get,game.EndpointBase+"/info?"+query))
                {
                    var info=await SendAsync(infoRequest,credential,query,null,ct).ConfigureAwait(false);
                    if(Bool(info,"is_sign")) return new(batch,account,game.Region,id,uid,"already",0,"今日已签到",1,NowMs()-start,NowMs());
                }
                var bodyObject=game.Region=="os" ? new Dictionary<string,object>{{"act_id",game.ActId}}
                    : new Dictionary<string,object>{{"act_id",game.ActId},{"region",region},{"uid",uid}};
                string body=JsonSerializer.Serialize(bodyObject);
                using var signRequest=new HttpRequestMessage(HttpMethod.Post,game.EndpointBase+"/sign"+(game.Region=="os"?"?lang=zh-cn":""))
                { Content=new StringContent(body,Encoding.UTF8,"application/json") };
                await SendAsync(signRequest,credential,null,body,ct).ConfigureAwait(false);
                return new(batch,account,game.Region,id,uid,"success",0,"签到成功",1,NowMs()-start,NowMs());
            }
            catch(MiyousheRequestException ex) when(ex.Retcode==-5003)
            { return new(batch,account,game.Region,id,uid,"already",-5003,"今日已签到",1,NowMs()-start,NowMs()); }
            catch(Exception ex) when(ex is not OperationCanceledException)
            { return new(batch,account,game.Region,id,uid,StateFor(ex),Code(ex),"游戏签到失败（详见安全错误码）",1,NowMs()-start,NowMs()); }
        }

        public virtual async Task<MiyousheTaskExecution> CommunityAsync(string batch,string account,MiyousheCredential credential,
            MiyousheCommunityProfile task,IReadOnlyList<string> forums,int maxRetries,int baseBackoff,
            Func<int,Task> delay,CancellationToken ct)
        {
            long start=NowMs();string id=task.Id+"@"+task.Region;
            if(!MiyousheCatalog.Regions[task.Region].CommunitySupported)
                return new(batch,account,task.Region,id,"","skipped",0,"该区域社区任务端点未核实，未发送请求",0,0,NowMs());
            if(task.Id=="forum" && forums.Count==0)
                return new(batch,account,task.Region,id,"","skipped",0,"请至少选择一个讨论区",0,0,NowMs());
            int attempts=0;
            try
            {
                var posts=task.Id=="forum" ? Array.Empty<string>() : await GetPostsAsync(credential,forums,ct).ConfigureAwait(false);
                if(task.Id!="forum" && posts.Length==0)
                    return new(batch,account,task.Region,id,"","failed",-2,"未获得可用帖子，未执行社区任务",1,NowMs()-start,NowMs());
                int needed=task.Occurrences;
                for(int i=0;i<needed;i++)
                {
                    for(int retry=0;retry<=maxRetries;retry++)
                    {
                        attempts++;
                        using var request=BuildCommunity(task,credential,forums,posts,i);
                        try { await SendAsync(request,credential,null,null,ct).ConfigureAwait(false); break; }
                        catch(Exception ex) when(ex is not OperationCanceledException)
                        {
                            if(IsRisk(ex)) return new(batch,account,task.Region,id,"","risk",Code(ex),"触发风控，停止且不解验证码",attempts,NowMs()-start,NowMs());
                            if(retry>=maxRetries) return new(batch,account,task.Region,id,"","failed",Code(ex),"达到失败重试上限",attempts,NowMs()-start,NowMs());
                            int seconds=Math.Min(1800,baseBackoff*(1<<retry));
                            await delay(seconds).ConfigureAwait(false);
                        }
                    }
                    if(i+1<needed) await delay(0).ConfigureAwait(false);
                }
                return new(batch,account,task.Region,id,"","success",0,"社区子任务完成",attempts,NowMs()-start,NowMs());
            }
            catch(Exception ex) when(ex is not OperationCanceledException)
            { return new(batch,account,task.Region,id,"",StateFor(ex),Code(ex),"社区子任务失败",attempts,NowMs()-start,NowMs()); }
        }
        private async Task<string[]> GetPostsAsync(MiyousheCredential credential,IReadOnlyList<string> forums,CancellationToken ct)
        {
            string forum=forums.FirstOrDefault()??"2";
            if(!MiyousheCatalog.Forums.TryGetValue(forum,out var f)) f=MiyousheCatalog.Forums["2"];
            string query="forum_id="+f.ForumId+"&is_good=false&is_hot=false&page_size=20&sort_type=1";
            string url=MiyousheCatalog.Regions[_region].CommunityBase+"/post/api/getForumPostList?"+query;
            using var request=new HttpRequestMessage(HttpMethod.Get,url);
            var data=await SendAsync(request,credential,query,null,ct).ConfigureAwait(false);
            if(!data.TryGetProperty("list",out var list) || list.ValueKind!=JsonValueKind.Array) return Array.Empty<string>();
            var ids=new List<string>();
            foreach(var item in list.EnumerateArray())
                if(item.TryGetProperty("post",out var post) && post.TryGetProperty("post_id",out var id)) ids.Add(id.GetRawText().Trim('"'));
            return ids.Distinct().Take(5).ToArray();
        }

        private HttpRequestMessage BuildCommunity(MiyousheCommunityProfile task,MiyousheCredential credential,
            IReadOnlyList<string> forums,IReadOnlyList<string> posts,int index)
        {
            string baseUrl=MiyousheCatalog.Regions[_region].CommunityBase;
            if(task.Id=="forum")
            {
                string forum=forums[index%forums.Count];
                if(!MiyousheCatalog.Forums.ContainsKey(forum)) throw new InvalidOperationException("讨论区 ID 无效");
                var req=new HttpRequestMessage(HttpMethod.Post,baseUrl+task.Path)
                { Content=new StringContent(JsonSerializer.Serialize(new{gids=int.Parse(forum)}),Encoding.UTF8,"application/json") };
                return req;
            }
            string post=posts[Math.Min(index,posts.Count-1)];
            if(task.Id=="read") return new HttpRequestMessage(HttpMethod.Get,baseUrl+task.Path+"?post_id="+Uri.EscapeDataString(post));
            if(task.Id=="share") return new HttpRequestMessage(HttpMethod.Get,baseUrl+task.Path+"?entity_id="+Uri.EscapeDataString(post)+"&entity_type=1");
            return new HttpRequestMessage(HttpMethod.Post,baseUrl+task.Path)
            { Content=new StringContent(JsonSerializer.Serialize(new{post_id=post,is_cancel=false}),Encoding.UTF8,"application/json") };
        }

        private async Task<JsonElement> SendAsync(HttpRequestMessage request,MiyousheCredential credential,
            string? query,string? body,CancellationToken ct)
        {
            var profile=MiyousheCatalog.Regions[_region];
            request.Headers.TryAddWithoutValidation("Cookie",Cookie(credential,profile));
            request.Headers.TryAddWithoutValidation("x-rpc-device_id",credential.DeviceId);
            request.Headers.TryAddWithoutValidation("x-rpc-client_type",_region=="cn"?"5":"4");
            request.Headers.TryAddWithoutValidation("x-rpc-app_version","2.70.1");
            request.Headers.TryAddWithoutValidation("Referer",profile.Referer);
            request.Headers.TryAddWithoutValidation("User-Agent","SoulLink/3.3");
            string salt=_region=="cn"?App.Config?.Settings?.MiyousheDsSalt??"":App.Config?.Settings?.MiyousheOsDsSalt??"";
            if(salt.Length==0) throw new InvalidOperationException("该区域 DS salt 未配置");
            request.Headers.TryAddWithoutValidation("DS",MiyousheClient.CreateDs(salt,body,query));
            using var response=await _http.SendAsync(request,ct).ConfigureAwait(false);
            string text=await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if(text.Length>1_000_000) throw new InvalidDataException("响应过大");
            if(!response.IsSuccessStatusCode) throw new MiyousheRequestException((int)response.StatusCode,"请求失败");
            try
            {
                using var doc=JsonDocument.Parse(text);int code=Int(doc.RootElement,"retcode");
                if(code!=0) throw new MiyousheRequestException(code,"请求失败");
                if(!doc.RootElement.TryGetProperty("data",out var data)) throw new InvalidDataException("缺少 data");
                return data.Clone();
            }
            catch(JsonException){throw new InvalidDataException("响应格式无效");}
        }

        private static string Cookie(MiyousheCredential c,MiyousheRegionProfile p)
            => string.Join("; ",new[]{p.LTokenName+"="+c.LToken,p.LtUidName+"="+c.LtUid,
                p.CookieTokenName+"="+c.CookieToken,p.AccountIdName+"="+c.AccountId,p.StokenName+"="+c.SToken}
                .Where(v=>!v.EndsWith("=",StringComparison.Ordinal)));
        private static string Text(JsonElement e,string name)=>e.TryGetProperty(name,out var v)?v.ValueKind==JsonValueKind.String?v.GetString()??"":v.GetRawText():"";
        private static int Int(JsonElement e,string name)=>int.TryParse(Text(e,name),out int v)?v:0;
        private static bool Bool(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&(v.ValueKind==JsonValueKind.True||Text(e,name)=="1");
        private static int Code(Exception ex)=>ex is MiyousheRequestException r?r.Retcode:-1;
        private static bool IsRisk(Exception ex)=>ex is MiyousheRequestException r&&(r.Retcode is 1034 or 10035 or 5003 or 10041 or 429);
        private static string StateFor(Exception ex)=>IsRisk(ex)?"risk":ex is MiyousheRequestException r&&r.Retcode is -100 or -10001?"needs_login":"failed";
        private static long NowMs()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}