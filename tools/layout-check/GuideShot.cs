using System;using System.IO;using System.Linq;using System.Reflection;using System.Threading.Tasks;using System.Windows;using System.Windows.Media;using System.Windows.Media.Imaging;using System.Windows.Threading;using SoulLink.Models;using SoulLink.Views;
public static class GuideShot
{
 [STAThread]public static int Main()
 {
  var app=new Application();app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/SoulLink;component/Theme/Styles.xaml",UriKind.Absolute)});
  var cfg=new AppConfig();var acc=new Account{Id="a",UserName="tester"};cfg.Accounts.Add(acc);
  var flow=new AutomationFlow{AccountId="a"};
  var e=new FlowEditorWindow(flow,acc,cfg,true);
  e.Width=1180;e.Height=820;
  e.ContentRendered+=async(_,__)=>{
   await Task.Delay(1200);
   var show=typeof(FlowEditorWindow).GetMethod("ShowFlowGuide",BindingFlags.Instance|BindingFlags.NonPublic)!;
   show.Invoke(e,new object[]{true});
   await Task.Delay(500);
   var rtb=new RenderTargetBitmap((int)e.ActualWidth,(int)e.ActualHeight,96,96,PixelFormats.Pbgra32);rtb.Render(e);
   var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(rtb));
   Directory.CreateDirectory("artifacts");
   using(var fs=File.Create("artifacts/flow-guide.png"))enc.Save(fs);
   e.Close();
  };
  e.Show();app.Run(e);return 0;
 }
}