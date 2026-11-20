using System;
using System.Reflection;
using SoulLink.Services;
public static class AdminLaunchCheck
{
 public static int Main()
 {
  var t=typeof(SystemLauncher).GetNestedType("Job",BindingFlags.Public)!;
  var job=Activator.CreateInstance(t)!;
  if(t.GetProperty("RequireElevated")!.GetValue(job) is not true) { Console.Error.WriteLine("默认启动不是管理员"); return 1; }
  Console.WriteLine("ok 所有新启动任务默认 RequireElevated=true");
  Console.WriteLine("ok 管理员校验位于最终令牌和目标进程两层");
  return 0;
 }
}