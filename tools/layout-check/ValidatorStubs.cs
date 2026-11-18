using System.Collections.Generic;
namespace SoulLink.Services
{
 public static class BgiPathResolver{public static string GetPath(string userName)=>"";}
 public static class SessionService{public static string BareName(string name)=>name;public static string CurrentUser=>"";}
 public static class AccountScope{public static bool Allows(string name,SoulLink.Models.AppConfig cfg)=>true;}
}
namespace SoulLink.Models
{
 public sealed class AppSettings{public Dictionary<string,string>BgiPaths{get;set;}=new();}
 public sealed class Account{public string Id{get;set;}="";public string UserName{get;set;}="";}
 public sealed class ActionItem{public string Id{get;set;}="";}
 public sealed class AppConfig{public AppSettings Settings{get;set;}=new();public List<Account>Accounts{get;set;}=new();public List<ActionItem>Actions{get;set;}=new();}
}