using System.Collections.Generic;
using System.Linq;

// ★ 2026-11-24：ValidatorCheck 闭包补全（补 App 桩 + 补齐 BgiDataDependency 要用的成员）。
//   背景：ValidatorCheck 只链接源码文件、不链接 App.xaml.cs / AccountScope.cs / BgiPathResolver.cs，
//   而 BgiDataDependency.cs 用到：
//     L56  (cfg ?? App.Config).Accounts          -> 需要 App 桩
//     L67/76 BgiPathResolver.IsPinned(user)      -> 需要该成员
//     L75  AccountScope.Accounts(cfg)            -> 需要该成员
//   这里给最小替身，语义够用即可（本断言只验证 FlowValidator 的节点校验，不验证账号解析）。
//   注意：这些桩只在断言工程里存在，不影响产品。
public static class App
{
    public static SoulLink.Models.AppConfig Config { get; set; } = new SoulLink.Models.AppConfig();
}

namespace SoulLink.Services
{
    public static class BgiPathResolver
    {
        public static string GetPath(string userName)=>"";
        // 产品实现：Services/BgiPathResolver.cs:448 – 账号是否已绑定 BGI 路径
        public static bool IsPinned(string userName)=>false;
    }

    public static class SessionService
    {
        public static string BareName(string name)=>name;
        public static string CurrentUser=>"";
    }

    public static class AccountScope
    {
        public static bool Allows(string name,SoulLink.Models.AppConfig cfg)=>true;
        // 产品实现：Services/AccountScope.cs:23 – 返回参与自动化的账号列表
        public static List<SoulLink.Models.Account> Accounts(SoulLink.Models.AppConfig? cfg = null)
            => (cfg ?? App.Config).Accounts ?? new List<SoulLink.Models.Account>();
    }
}
namespace SoulLink.Models
{
    public sealed class AppSettings{public Dictionary<string,string>BgiPaths{get;set;}=new();}
    public sealed class Account{public string Id{get;set;}="";public string UserName{get;set;}="";}
    public sealed class ActionItem{public string Id{get;set;}="";}
    public sealed class AppConfig{public AppSettings Settings{get;set;}=new();public List<Account>Accounts{get;set;}=new();public List<ActionItem>Actions{get;set;}=new();}
}