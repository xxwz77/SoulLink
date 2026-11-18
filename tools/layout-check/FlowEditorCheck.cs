using System;using System.Linq;using System.Reflection;using System.Windows;using System.Windows.Controls;using SoulLink.Models;using SoulLink.Views;
public static class FlowEditorCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 [STAThread]public static int Main()
 {
  _=new Application();Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/SoulLink;component/Theme/Styles.xaml",UriKind.Absolute)});
  var cfg=new AppConfig();var account=new Account{Id="a",UserName="tester"};cfg.Accounts.Add(account);
  var loop=new AutomationFlow{AccountId="a"};var c1=new FlowNode{Id="c1",Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange};loop.Nodes.Add(c1);
  try
  {
   var e1=new FlowEditorWindow(loop,account,cfg);
   var state=(TextBlock)typeof(FlowEditorWindow).GetField("DailyStateText",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(e1)!;
   Check(state.Text.Contains("今日会运行")&&state.Text.Contains("重复"),"无 END 的流程明确显示今日会运行且可重复");e1.Close();

   var daily=new AutomationFlow{AccountId="a"};var c2=new FlowNode{Id="c2",Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange};var end=new FlowNode{Id="e",Kind=FlowNodeKind.Terminate,Type=FlowNodeType.ActEndFlow,DayBoundaryHour=4};daily.Nodes.AddRange(new[]{c2,end});daily.Edges.Add(new FlowEdge{From="c2",To="e"});
   daily.DailyLatchKey=PlcLogic.DayKey(DateTime.Now,4);daily.RunComplete=true;
   var e2=new FlowEditorWindow(daily,account,cfg);
   var canvas=(SoulLink.Controls.FlowCanvas)typeof(FlowEditorWindow).GetField("Canvas",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(e2)!;
   canvas.Select(end);
   var panel=(StackPanel)typeof(FlowEditorWindow).GetField("PropPanel",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(e2)!;
   var combos=panel.Children.OfType<ComboBox>()
       .Where(b=>b.Items.OfType<ComboBoxItem>().Any(i=>(i.Content as string)?.Contains("04:00")==true)).ToList();
   var checks=panel.Children.OfType<CheckBox>().Where(b=>(b.Content as string)?.Contains("今天运行")==true).ToList();
   Check(combos.Count>=1&&combos[0].Items.Count==2,"END 节点属性内提供 00:00/04:00 日界");
   Check(checks.Count==1,"END 节点提供今日运行开关");
   checks[0].IsChecked=true;
   Check(daily.ToggleTodayRequested==true||daily.RunResetRequested,"恢复今日运行动作可持久化");
   checks[0].IsChecked=false;
   Check(daily.ToggleTodayRequested==false||daily.IsDailyComplete,"取消勾选即设为今日不运行");
   e2.Close();
  }
  catch(Exception ex){Console.WriteLine("FAIL "+ex);fails++;}
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}
