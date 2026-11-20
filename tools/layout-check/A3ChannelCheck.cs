using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SoulLink.Models;
using SoulLink;
using SoulLink.Services;
public static class A3ChannelCheck
{
 public static async Task<int> Main()
 {
  var sessions=await SessionService.QueryAsync();
  var target=sessions.FirstOrDefault(s=>string.Equals(s.UserName,"A3",StringComparison.OrdinalIgnoreCase));
  if(target==null){Console.WriteLine("A3_NOT_FOUND");return 2;}
  Console.WriteLine("A3_SESSION="+target.Id);
  var cfg=ConfigStore.Load(); cfg.Settings.UseBrokerChannel=true;
  App.Config.Settings.UseBrokerChannel=true;
  var account=new Account{UserName="A3",DisplayName="A3"};
  var relay=new RelayClient("SoulLink_Relay_"+target.Id+"_"+Guid.NewGuid().ToString("N").Substring(0,8),target.Id);
  typeof(RelayClient).GetProperty("AgentPid")?.SetValue(relay,0);
  Console.WriteLine("BEGIN_DEPLOY");
  var result=await relay.DeployAsync(account,10000).WaitAsync(TimeSpan.FromSeconds(12));
  Console.WriteLine("DEPLOY_OK="+result.ok+" MESSAGE="+result.message);
  Console.WriteLine("CONNECTED="+relay.IsConnected);
  relay.Dispose();
  return result.ok?0:1;
 }
}