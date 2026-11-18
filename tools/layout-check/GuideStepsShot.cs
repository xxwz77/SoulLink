using System;using System.IO;using System.Linq;using System.Reflection;using System.Threading.Tasks;using System.Windows;
using System.Windows.Controls;using System.Windows.Media;using System.Windows.Media.Imaging;using System.Windows.Threading;using SoulLink.Models;using SoulLink.Views;
public static class GuideStepsShot
{
 [STAThread]public static int Main()
 {
  var app=new Application();
  app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/SoulLink;component/Theme/Styles.xaml",UriKind.Absolute)});
  var cfg=new AppConfig();var acc=new Account{Id="a",UserName="tester"};cfg.Accounts.Add(acc);
  var flow=new AutomationFlow{AccountId="a"};
  var e=new FlowEditorWindow(flow,acc,cfg,true){Width=1180,Height=820};
  e.ContentRendered+=async(_,__)=>{
   try{
    await Task.Delay(1000);
    var show=typeof(FlowEditorWindow).GetMethod("ShowFlowGuide",BindingFlags.Instance|BindingFlags.NonPublic)!;
    var next=typeof(FlowEditorWindow).GetMethod("RenderGuideCard",BindingFlags.Instance|BindingFlags.NonPublic)!;
    show.Invoke(e,new object[]{true});
    Directory.CreateDirectory("artifacts");
    for(int i=1;i<=6;i++){
      await Task.Delay(600);
      var rtb=new RenderTargetBitmap((int)e.ActualWidth,(int)e.ActualHeight,96,96,PixelFormats.Pbgra32);rtb.Render(e);
      var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(rtb));
      using(var fs=File.Create("artifacts/guide-step"+i+".png"))enc.Save(fs);
      // 点“下一步”
      var layer=(Canvas)typeof(FlowEditorWindow).GetField("GuideLayer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(e)!;
      var card=layer.Children.OfType<Border>().LastOrDefault();
      if(card?.Child is StackPanel sp){
        var nav=sp.Children.OfType<StackPanel>().LastOrDefault();
        var btn=nav?.Children.OfType<Button>().FirstOrDefault();
        btn?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
      }
    }
   }catch(Exception ex){Console.WriteLine("ERR "+ex.Message);}
   finally{e.Close();}
  };
  e.Show();app.Run(e);return 0;
 }
}