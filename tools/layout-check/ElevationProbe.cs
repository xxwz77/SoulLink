using System;using System.Diagnostics;using System.IO;using System.Linq;using SoulLink.Services;
public static class ElevationProbe
{
 public static int Main()
 {
  string src=@"E:\Documents\deepseek-harness\SoulLink\dist\SoulLink.exe";
  string dir=Path.Combine(Path.GetTempPath(),"sl740_"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(dir);
  string orig=Path.Combine(dir,"orig.exe"); string patched=Path.Combine(dir,"patched.exe");
  File.Copy(src,orig,true); File.Copy(src,patched,true);
  if(!ManifestPatcher.MakeAsInvoker(patched)) { Console.WriteLine("PATCH_FAIL"); return 2; }
  string markerOrig=Path.Combine(dir,"orig.marker"), markerPatched=Path.Combine(dir,"patched.marker");
  string jobPath=Path.Combine(dir,"job.json");
  File.WriteAllText(jobPath, "{}");
  int r1=RunTask("SL740Orig", orig, "--system-run " + Quote(jobPath));
  int r2=RunTask("SL740Patched", patched, "--system-run " + Quote(jobPath));
  Console.WriteLine("ORIG_LAST_RESULT="+r1);
  Console.WriteLine("PATCHED_LAST_RESULT="+r2);
  Console.WriteLine("ORIG_IS_740="+(r1==740));
  Console.WriteLine("PATCHED_NOT_740="+(r2!=740));
  try { Directory.Delete(dir,true); } catch { }
  return (r1==740 && r2!=740) ? 0 : 1;
 }
 static string Quote(string s)=>"\""+s+"\"";
 static int RunTask(string name,string exe,string args)
 {
  string tr=Quote(exe)+" "+args;
  Exec("schtasks.exe","/create /tn "+Quote(name)+" /tr "+Quote(tr)+" /sc once /st 23:59 /ru SYSTEM /rl HIGHEST /f");
  Exec("schtasks.exe","/run /tn "+Quote(name));
  System.Threading.Thread.Sleep(4000);
  string q=Exec("schtasks.exe","/query /tn "+Quote(name)+" /v /fo LIST");
  int code=-1;
  foreach(var raw in q.Split('\n'))
  {
   var line=raw.Trim();
   if(!line.StartsWith("Last Result:")) continue;
   var v=line.Substring("Last Result:".Length).Trim();
   if(int.TryParse(v, out int parsed)) code=parsed;
  }
  if(code==-1) Console.WriteLine("  raw=" + q.Replace("\r",""));
  Exec("schtasks.exe","/delete /tn "+Quote(name)+" /f");
  return code;
 }
 static string Exec(string file,string args)
 {
  var psi=new ProcessStartInfo(file,args){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
  using var p=Process.Start(psi)!; string o=p.StandardOutput.ReadToEnd()+p.StandardError.ReadToEnd(); p.WaitForExit(30000); return o;
 }
}
