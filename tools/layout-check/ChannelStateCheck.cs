using System;using System.Windows.Media;using SoulLink.Models;using SoulLink.Views;
public static class ChannelStateCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static void Main()
 {
  var vm=new SessionVm(new SessionInfo{Id=3,UserName="A3"});
  Check(vm.ChannelText=="通道未投送","初始状态为未投送");
  vm.ChannelError="投送注入代理失败：等待 SYSTEM 引导进程超时（45 秒）";
  Check(vm.ChannelText.StartsWith("投送失败")&&vm.ChannelText.Contains("超时"),"失败时显示投送失败与原因");
  Check(((SolidColorBrush)vm.ChannelBrush).Color.R>200,"失败使用红色");
  vm.HasChannel=true;
  Check(vm.ChannelText=="通道就绪"&&vm.ChannelError.Length==0,"成功后就绪并清除失败原因");
  vm.HasChannel=false;
  Check(vm.ChannelText=="通道未投送","断开后回到未投送");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);Environment.Exit(fails);
 }
}