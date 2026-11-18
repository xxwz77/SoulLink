using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using SoulLink.Services;
public static class BgiDataCenterCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 static string Account(string root,string name,string key,string group,string dragon)
 {
  string path=Path.Combine(root,name);Directory.CreateDirectory(Path.Combine(path,"User","ScriptGroup"));Directory.CreateDirectory(Path.Combine(path,"User","OneDragon"));
  File.WriteAllText(Path.Combine(path,"User","config.json"),"{\"cancelTaskHotkey\":\""+key+"\",\"cancelTaskHotkeyType\":\"KeyboardMonitor\"}");
  File.WriteAllText(Path.Combine(path,"User","ScriptGroup",group+".json"),"{}");File.WriteAllText(Path.Combine(path,"User","OneDragon",dragon+".json"),"{}");return path;
 }
 public static int Main()
 {
  string root=Path.Combine(Path.GetTempPath(),"SoulLink-BgiDataCenter-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  string a=Account(root,"A","F12","A-组","A-龙"),b=Account(root,"B","NumPad9","B-组","B-龙");
  var da=BgiDataCenter.ReadFiles("A",Path.Combine(a,"BetterGI.exe"));var db=BgiDataCenter.ReadFiles("B",Path.Combine(b,"BetterGI.exe"));
  Check(da.OneDragonConfigs[0]=="A-龙"&&da.ScriptGroups[0]=="A-组"&&da.CancelTaskHotkey=="F12"&&da.CancelTaskHotkeyType=="KeyboardMonitor","账号 A 读取自己的目录和快捷键");
  Check(db.OneDragonConfigs[0]=="B-龙"&&db.ScriptGroups[0]=="B-组"&&db.CancelTaskHotkey=="NumPad9","账号 B 不串用 A 的数据");
Check(string.Equals(da.DirectoryOf("User"),Path.Combine(a,"User"),StringComparison.OrdinalIgnoreCase),"配置目录跟随账号路径");
  var empty=BgiDataCenter.ReadFiles("C","");Check(empty.Error.Contains("尚未设置")&&empty.ScriptGroups.Count==0,"没有路径时返回明确状态而非借用其它账号");
  File.WriteAllText(Path.Combine(a,"User","config.json"),"{\"cancelTaskHotkey\":\"F11\",\"cancelTaskHotkeyType\":\"KeyboardMonitor\"}");
  var changed=BgiDataCenter.ReadFiles("A",Path.Combine(a,"BetterGI.exe"));Check(changed.CancelTaskHotkey=="F11","配置文件变更后重新读取快捷键");
  var all=new List<BgiAccountData>();
  foreach(var name in new[]{"A1","A2","A3","lummy"})
  {string p=Account(root,name,"F12",name+"-组",name+"-龙");all.Add(BgiDataCenter.ReadFiles(name,Path.Combine(p,"BetterGI.exe")));}
  Check(all.Count==4&&all.Select(x=>x.UserName).Distinct().Count()==4,"保存四个账号就分别读取四个账号，不限制双账号");
  Check(all.All(x=>x.ScriptGroups.Count==1&&x.OneDragonConfigs.Count==1),"每个保存账号都有自己的配置目录");
  File.WriteAllText(Path.Combine(a,"User","config.json"),"{\"cancelTaskHotkey\":\"F12\",\"cancelTaskHotkeyType\":\"KeyboardMonitor\",\"autoPickEnabled\":true}");
  var filtered=BgiDataCenter.ReadFiles("A",Path.Combine(a,"BetterGI.exe"));
  Check(filtered.Hotkeys.Count==1&&filtered.Hotkeys.ContainsKey("cancelTaskHotkey"),"快捷键集合不包含监听类型和启用开关");
  Check(filtered.DisplayHotkeys.Count==1&&filtered.DisplayHotkeys[0].Name=="停止当前脚本"&&filtered.DisplayHotkeys[0].Key=="F12","只输出中文功能名和明确按键");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}