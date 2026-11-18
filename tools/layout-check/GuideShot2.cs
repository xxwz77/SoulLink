using System;using System.IO;using System.Linq;using System.Reflection;using System.Threading.Tasks;using System.Windows;using System.Windows.Controls;using System.Windows.Media;using System.Windows.Media.Imaging;using SoulLink.Models;using SoulLink.Views;
public static class GuideShot2
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
    await Task.Delay(900);
    var show=typeof(FlowEditorWindow).GetMethod("ShowFlowGuide",BindingFlags.Instance|BindingFlags.NonPublic)!;
    show.Invoke(e,new object[]{true});
    Directory.CreateDirectory("artifacts");
    for(int i=1;i<=6;i++){
      await Task.Delay(500);
      var rtb=new RenderTargetBitmap((int)e.ActualWidth,(int)e.ActualHeight,96,96,PixelFormats.Pbgra32);rtb.Render(e);
      var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(rtb));
      using(var fs=File.Create("artifacts/g2-step"+i+".png"))enc.Save(fs);
      var layer=(Canvas)typeof(FlowEditorWindow).GetField("GuideLayer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(e)!;
      var nav=layer.Children.OfType<StackPanel>().LastOrDefault();
      var btn=nav?.Children.OfType<Button>().LastOrDefault();
      btn?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }
   }catch(Exception ex){Console.WriteLine("ERR "+ex.Message);}
   finally{e.Close();}
  };
  e.Show();app.Run(e);return 0;
 }
}