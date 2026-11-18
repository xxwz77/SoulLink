using System;using System.IO;using System.Text;using SoulLink.Services;
public static class ManifestDebug
{
 public static int Main()
 {
  string src=@"E:\Documents\deepseek-harness\SoulLink\dist\SoulLink.exe";
  string dst=Path.Combine(Path.GetTempPath(),"fresh_"+Guid.NewGuid().ToString("N")+".exe");
  File.Copy(src,dst,true);
  var before=Encoding.UTF8.GetString(File.ReadAllBytes(dst));
  Console.WriteLine("beforeHasRequire="+before.Contains("level=\"requireAdministrator\""));
  bool ok=ManifestPatcher.MakeAsInvoker(dst);
  var after=Encoding.UTF8.GetString(File.ReadAllBytes(dst));
  Console.WriteLine("patched="+ok);
  Console.WriteLine("afterHasRequire="+after.Contains("level=\"requireAdministrator\""));
  int i=after.IndexOf("requestedExecutionLevel");
  Console.WriteLine("afterCtx="+(i<0?"(none)":after.Substring(i,60).Replace("\n"," ")));
  Console.WriteLine("sizeSame="+(new FileInfo(dst).Length==new FileInfo(src).Length));
  File.Delete(dst);
  return 0;
 }
}
