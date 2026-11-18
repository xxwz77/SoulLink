using System;using System.Linq;using SoulLink.Models;using SoulLink.Services;
public static class AccountScopeCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main()
 {
  var cfg=new AppConfig();cfg.Accounts.Add(new Account{UserName="主用户"});foreach(var n in new[]{"A1","A2","A3","A4"})cfg.Accounts.Add(new Account{UserName=n});
  Check(AccountScope.Accounts(cfg).Count==5,"关闭模式保留全部账号");
  cfg.Settings.OnlyAutomationAccounts=true;Check(AccountScope.Accounts(cfg).Count==4,"开启后只显示挂机账号");
  Check(!AccountScope.Allows("主用户",cfg)&&!AccountScope.Allows("DOMAIN"+"\\"+"主用户",cfg),"主用户及带域前缀均被屏蔽");
  Check(AccountScope.Allows("A1",cfg),"挂机账号不受影响");
  Check(cfg.Accounts.Count==5,"隐藏不删除保存数据");
  cfg.Settings.OnlyAutomationAccounts=false;Check(AccountScope.Accounts(cfg).Count==5,"关闭后恢复全部账号");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}