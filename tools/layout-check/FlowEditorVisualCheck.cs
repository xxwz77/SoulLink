using System;using System.Collections.Generic;using System.IO;using System.Linq;using System.Threading.Tasks;using System.Windows;using System.Windows.Controls;using System.Windows.Media;using System.Windows.Media.Imaging;using System.Windows.Threading;using SoulLink.Models;using SoulLink.Views;
public static class FlowEditorVisualCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 [STAThread]public static int Main()
 {
  var app=new Application();app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/SoulLink;component/Theme/Styles.xaml",UriKind.Absolute)});
  var cfg=new AppConfig();var account=new Account{Id="a",UserName="tester"};cfg.Accounts.Add(account);
  var flow=new AutomationFlow{AccountId="a"};
  flow.Nodes.Add(new FlowNode{Id="t",Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange,X=70,Y=170,
     Params=new Dictionary<string,string>{{"from","00:00"},{"to","23:59"}}});
  flow.Nodes.Add(new FlowNode{Id="n",Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange,X=70,Y=330,
     Params=new Dictionary<string,string>{{"from","03:00"},{"to","03:01"}}});
  var e=new FlowEditorWindow(flow,account,cfg,true);
  e.ContentRendered+=async(_,__)=>await Verify(e);e.Show();app.Run(e);return fails;
 }
 static async Task Verify(FlowEditorWindow e)
 {
  try
  {
   await Task.Delay(2600);
   await Dispatcher.Yield(DispatcherPriority.Render);
   var rtb=new RenderTargetBitmap((int)e.ActualWidth,(int)e.ActualHeight,96,96,PixelFormats.Pbgra32);rtb.Render(e);
   Directory.CreateDirectory("artifacts");
   var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(rtb));using(var fs=File.Create("artifacts/editor-lights.png"))enc.Save(fs);
   // 绿色状态灯 (0x6E,0xF0,0xC8) 像素计数
   int lit=0,unlit=0;int w=rtb.PixelWidth,h=rtb.PixelHeight;var px=new byte[w*h*4];rtb.CopyPixels(px,w*4,0);
   for(int i=0;i<px.Length;i+=4){byte b=px[i],g=px[i+1],r=px[i+2];if(g>200&&r<140&&b>150)lit++;if(r>90&&r<150&&g>110&&g<160&&b>170&&b<220)unlit++;}
   Console.WriteLine($"  litPixels={lit} unlitPixels={unlit}");
   Check(lit>60,"真实编辑器绘制了“满足”绿灯像素");
   Check(unlit>0,"不满足条件使用另一颜色，灯不是全部冻结");
  }catch(Exception ex){Console.WriteLine("FAIL "+ex);fails++;}finally{e.Close();}
 }
}
