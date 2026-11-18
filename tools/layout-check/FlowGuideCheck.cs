using System;using System.Linq;using System.Reflection;using System.Windows;using System.Windows.Controls;using SoulLink.Models;using SoulLink.Views;
public static class FlowGuideCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 [STAThread]public static int Main()
 {
  _=new Application();Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/SoulLink;component/Theme/Styles.xaml",UriKind.Absolute)});
  var cfg=new AppConfig();var acc=new Account{Id="a",UserName="tester"};cfg.Accounts.Add(acc);
  var flow=new AutomationFlow{AccountId="a"};flow.Nodes.Add(new FlowNode{Id="c",Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange});
  try{
   var ed=new FlowEditorWindow(flow,acc,cfg);
   var layer=(Canvas)typeof(FlowEditorWindow).GetField("GuideLayer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(ed)!;
   Check(layer.Children.Count==0,"未显示时引导层为空");
   var show=typeof(FlowEditorWindow).GetMethod("ShowFlowGuide",BindingFlags.Instance|BindingFlags.NonPublic)!;
   show.Invoke(ed,new object[]{true});
   Check(layer.Children.Count>=1,"引导层渲染出高亮与卡片");
   var card=layer.Children.OfType<Border>().LastOrDefault();
   Check(card!=null,"存在说明卡片");
   var close=typeof(FlowEditorWindow).GetMethod("CloseGuide",BindingFlags.Instance|BindingFlags.NonPublic)!;
   close.Invoke(ed,null);
   Check(layer.Children.Count==0,"关闭后引导层清空");
   // 默认弹一次后不再自动弹
   Check(cfg.Settings.ShowFlowGuide,"默认开启引导");
   ed.Close();
  }catch(Exception ex){Console.WriteLine("FAIL "+ex.GetType().Name+": "+ex.Message);fails++;}
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}