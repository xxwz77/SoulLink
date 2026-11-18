using System;using System.Linq;using System.Reflection;using System.Windows;using System.Windows.Controls;using SoulLink.Models;using SoulLink.Views;
public static class PathPickerCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 [STAThread]public static int Main()
 {
  _=new Application();Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/SoulLink;component/Theme/Styles.xaml",UriKind.Absolute)});
  var cfg=new AppConfig();var account=new Account{Id="a",UserName="tester"};cfg.Accounts.Add(account);
  var flow=new AutomationFlow{AccountId="a"};
  var node=new FlowNode{Id="p",Kind=FlowNodeKind.Action,Type=FlowNodeType.ActLaunchProcess};
  foreach(var d in FlowNodeCatalog.Get(FlowNodeType.ActLaunchProcess)!.Params) node.Set(d.Key,d.Default);
  flow.Nodes.Add(node);
  try{
   var ed=new FlowEditorWindow(flow,account,cfg);
   var panel=(StackPanel)typeof(FlowEditorWindow).GetField("PropPanel",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(ed)!;
   var method=typeof(FlowEditorWindow).GetMethod("ShowPropPanel",BindingFlags.Instance|BindingFlags.NonPublic)!;
   method.Invoke(ed,new object[]{node});
   var buttons=panel.Children.OfType<StackPanel>().SelectMany(p=>p.Children.OfType<Grid>()).SelectMany(g=>g.Children.OfType<Button>()).ToList();
   var browse=buttons.FirstOrDefault(b=>(b.Content as string)?.Contains("浏览")==true);
   Check(browse!=null,"打开程序提供「浏览…」按钮");
   var boxes=panel.Children.OfType<StackPanel>().SelectMany(p=>p.Children.OfType<Grid>()).SelectMany(g=>g.Children.OfType<TextBox>()).ToList();
   Check(boxes.Count>=1,"提供可直接输入路径的文本框");
   if(boxes.Count>=1){boxes[0].Text=@"C:\Windows\notepad.exe";}
   Check(node.Get("path")==@"C:\Windows\notepad.exe","输入路径写回节点参数");
   ed.Close();
  }catch(Exception ex){Console.WriteLine("FAIL "+ex.GetType().Name+": "+ex.Message);fails++;}
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}