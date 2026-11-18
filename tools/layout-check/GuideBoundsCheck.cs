using System;using System.Collections.Generic;using System.Linq;using System.Reflection;using System.Windows;using System.Windows.Controls;using System.Windows.Media;using SoulLink.Models;using SoulLink.Views;
public static class GuideBoundsCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 [STAThread]public static int Main()
 {
  var app=new Application();
  app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/SoulLink;component/Theme/Styles.xaml",UriKind.Absolute)});
  var cfg=new AppConfig();var acc=new Account{Id="a",UserName="tester"};cfg.Accounts.Add(acc);
  var flow=new AutomationFlow{AccountId="a"};
  var e=new FlowEditorWindow(flow,acc,cfg,true){Width=1180,Height=820};
  e.ContentRendered+=(_,__)=>{
   try{
    var show=typeof(FlowEditorWindow).GetMethod("ShowFlowGuide",BindingFlags.Instance|BindingFlags.NonPublic)!;
    show.Invoke(e,new object[]{true});
    e.UpdateLayout();
    var layer=(Canvas)typeof(FlowEditorWindow).GetField("GuideLayer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(e)!;
    double W=layer.Width,H=layer.Height;
    Console.WriteLine("  overlay="+W+"x"+H);
    Check(W>500&&H>400,"遮罩尺寸有效");
    int idx=0;
    for(int step=0;step<6;step++){
      e.UpdateLayout();
      // 找出示例卡片（宽228高70的 Border）
      var cards=layer.Children.OfType<Canvas>().SelectMany(c=>c.Children.OfType<Border>())
        .Where(b=>Math.Abs(b.Width-228)<1).ToList();
      Console.WriteLine("  step"+(step+1)+" demoCards="+cards.Count);
      Check(cards.Count>0,"步骤"+(step+1)+"有示例模块");
      bool inside=true;
      foreach(var b in cards){
        double x=Canvas.GetLeft(b), y=Canvas.GetTop(b);
        if(x<0||y<0||x+228>W+0.5||y+70>H+0.5){inside=false;Console.WriteLine("    OUT x="+x+" y="+y);}
      }
      Check(inside,"步骤"+(step+1)+"示例全部在遮罩内");
      // 连接线端点也必须在遮罩内
      var links=layer.Children.OfType<Canvas>().SelectMany(c=>c.Children.OfType<System.Windows.Shapes.Path>()).ToList();
      bool linkOk=true;
      foreach(var lp in links){
        if(lp.Data is GeometryGroup gg)
          foreach(var g in gg.Children.OfType<LineGeometry>()){
            if(g.StartPoint.X<0||g.StartPoint.Y<0||g.EndPoint.X<0||g.EndPoint.Y<0
               ||g.StartPoint.X>W||g.EndPoint.X>W||g.StartPoint.Y>H||g.EndPoint.Y>H)
            {linkOk=false;Console.WriteLine("    LINK_OUT "+g.StartPoint+"->"+g.EndPoint);}
          }
      }
      if(links.Count>0) Check(linkOk,"步骤"+(step+1)+"连线端点未越界");
      // 点下一步
      var nav=layer.Children.OfType<StackPanel>().LastOrDefault();
      var btn=nav?.Children.OfType<Button>().LastOrDefault();
      if(btn==null){Check(false,"找到下一步按钮");break;}
      btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
      e.UpdateLayout();
    }
   }catch(Exception ex){Console.WriteLine("FAIL "+ex.GetType().Name+": "+ex.Message);fails++;}
   finally{e.Close();}
  };
  e.Show();app.Run(e);
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);
  return fails;
 }
}