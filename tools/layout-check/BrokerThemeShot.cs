using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoulLink.Views;
public static class BrokerThemeShot
{
 [STAThread] public static int Main()
 {
  var app=new Application(); app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/SoulLink;component/Theme/Styles.xaml")});
  var w=new BrokerWindow();
  w.ContentRendered+=async(_,_)=>{await Task.Delay(400);await Dispatcher.Yield(DispatcherPriority.Render);w.UpdateLayout();var b=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(w);var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(b));using var f=File.Create("artifacts/broker-window.png");e.Save(f);w.Close();};w.Show();app.Run(w);return 0;
 }
}