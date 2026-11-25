using System;
using System.Collections.Generic;
using System.Linq;

namespace SoulLink.Services
{
    internal sealed record MiyousheRegionProfile(
        string Id, string ApiBase, string LedgerBase, string AppId, string Referer,
        string PassportCreate, string PassportQuery, string CommunityBase,
        string LTokenName, string LtUidName, string CookieTokenName, string AccountIdName,
        string StokenName, string SaltKey, bool CommunitySupported);
    internal sealed record MiyousheGameProfile(
        string GameBiz, string ActId, string SignGame, string RegionParam,
        string Region, string EndpointBase, bool ActVerified, string Verification);

    internal sealed record MiyousheCommunityProfile(
        string Id, string Region, string Path, string Method, int Occurrences, bool Verified);

    internal static class MiyousheCatalog
    {
        internal static readonly IReadOnlyDictionary<string,MiyousheRegionProfile> Regions =
            new Dictionary<string,MiyousheRegionProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["cn"]=new("cn","https://api-takumi.mihoyo.com","https://hk4e-api.mihoyo.com","bll8iq97cem8",
                    "https://www.miyoushe.com/","https://passport-api.miyoushe.com/account/ma-cn-passport/web/createQRLogin",
                    "https://passport-api.miyoushe.com/account/ma-cn-passport/web/queryQRLoginStatus","https://bbs-api.miyoushe.com",
                    "ltoken_v2","ltuid_v2","cookie_token_v2","account_id_v2","stoken_v2","cn",true),
                ["os"]=new("os","https://sg-public-api.hoyolab.com","","k4zvksw6yn4c",
                    "https://act.hoyolab.com/","https://passport-api.hoyoverse.com/account/ma-cn-passport/web/createQRLogin",
                    "https://passport-api.hoyoverse.com/account/ma-cn-passport/web/queryQRLoginStatus","https://bbs-api-os.hoyolab.com",
                    "ltoken","ltuid","cookie_token","account_id","stoken","os",false)
            };

        internal static readonly IReadOnlyDictionary<string,MiyousheGameProfile> Games = BuildGames();
        internal static readonly IReadOnlyDictionary<string,MiyousheCommunityProfile> Community = BuildCommunity();
        internal static readonly IReadOnlyDictionary<string,(int Id,int ForumId,string Name)> Forums =
            new Dictionary<string,(int,int,string)> { ["1"]=(1,1,"崩坏3"),["2"]=(2,26,"原神"),
                ["3"]=(3,30,"崩坏学园2"),["6"]=(6,52,"星穹铁道"),["8"]=(8,57,"绝区零") };

        private static IReadOnlyDictionary<string,MiyousheGameProfile> BuildGames()
        {
            var list = new[] {
                new MiyousheGameProfile("hk4e_cn","e202311201442471","hk4e","region","cn","https://api-takumi.mihoyo.com/event/luna",false,"home 实测 -500001；t16 骨架保留，需账号复核"),
                new MiyousheGameProfile("hkrpg_cn","e202304121516551","hkrpg","region","cn","https://api-takumi.mihoyo.com/event/luna",true,"home 实测 retcode=0 awards=30"),
                new MiyousheGameProfile("nap_cn","e202406242138391","zzz","region","cn","https://act-nap-api.mihoyo.com/event/luna/zzz",false,"home 实测 -500001；未核实，默认禁用"),
                new MiyousheGameProfile("bh3_cn","e202306201626331","bh3","region","cn","https://api-takumi.mihoyo.com/event/luna",true,"home 实测 retcode=0 awards=30"),
                new MiyousheGameProfile("bh2_cn","e202203291431091","bh2","region","cn","https://api-takumi.mihoyo.com/event/luna",true,"home 实测 retcode=0 awards=30"),
                new MiyousheGameProfile("hk4e_global","e202102251931481","hk4e","","os","https://sg-hk4e-api.hoyolab.com/event/sol",true,"home 实测 retcode=0 awards=30"),
                new MiyousheGameProfile("hkrpg_global","e202303301540311","hkrpg","","os","https://sg-public-api.hoyolab.com/event/luna/os",true,"home 实测 retcode=0 awards=30"),
                new MiyousheGameProfile("nap_global","e202406031448091","zzz","","os","https://sg-act-nap-api.hoyolab.com/event/luna/zzz/os",false,"home 实测 -500001；未核实，默认禁用"),
                new MiyousheGameProfile("bh3_global","e202110291205111","bh3","","os","https://sg-public-api.hoyolab.com/event/mani",true,"home 实测 retcode=0 awards=30"),
                new MiyousheGameProfile("bh2_global","","bh2","","os","",false,"未找到可靠公开 act_id；未核实，默认禁用")
            };
            return list.ToDictionary(x => x.GameBiz+"@"+x.Region,StringComparer.OrdinalIgnoreCase);
        }

        private static IReadOnlyDictionary<string,MiyousheCommunityProfile> BuildCommunity()
            => new Dictionary<string,MiyousheCommunityProfile>(StringComparer.OrdinalIgnoreCase) {
                ["like@cn"]=new("like","cn","/apihub/sapi/upvotePost","POST",5,false),
                ["share@cn"]=new("share","cn","/apihub/api/getShareConf","GET",1,false),
                ["read@cn"]=new("read","cn","/post/api/getPostFull","GET",3,false),
                ["forum@cn"]=new("forum","cn","/apihub/app/api/signIn","POST",1,false)
            };

        internal static IReadOnlyList<string> Validate(
            IReadOnlyDictionary<string,MiyousheRegionProfile>? regions=null,
            IReadOnlyDictionary<string,MiyousheGameProfile>? games=null,
            IReadOnlyDictionary<string,MiyousheCommunityProfile>? community=null)
        {
            regions ??= Regions; games ??= Games; community ??= Community;
            var missing=new List<string>();
            foreach(string region in new[]{"cn","os"}) if(!regions.ContainsKey(region)) missing.Add("region:"+region);
            foreach(string region in new[]{"cn","os"})
                foreach(string game in new[]{"hk4e","hkrpg","nap","bh3","bh2"})
                {
                    string biz=game+(region=="cn"?"_cn":"_global");
                    if(!games.ContainsKey(biz+"@"+region)) missing.Add("game:"+biz+"@"+region);
                }
            foreach(string task in new[]{"like","share","read","forum"})
                if(!community.ContainsKey(task+"@cn")) missing.Add("community:"+task+"@cn");
            return missing;
        }

        internal static IEnumerable<MiyousheGameProfile> ForRegion(string region)
            => Games.Values.Where(g=>g.Region.Equals(region,StringComparison.OrdinalIgnoreCase));
    }
}