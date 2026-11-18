using System;using SoulLink.Services;
public static class BgiRealHotkeyCheck
{
 public static int Main(){var d=BgiDataCenter.ReadFiles("A1",@"D:\AAA挂机专区\A1\BetterGI\BetterGI.exe");Console.WriteLine("error="+d.Error);foreach(var h in d.DisplayHotkeys)Console.WriteLine(h.Name+" = "+h.Key);bool ok=d.DisplayHotkeys.Count>0&&d.DisplayHotkeys.All(x=>x.Key.Length>0&&!x.Key.Contains("{")&&!x.Key.Contains("Monitor"));Console.WriteLine(ok?"ALL PASSED":"FAILED");return ok?0:1;}
}