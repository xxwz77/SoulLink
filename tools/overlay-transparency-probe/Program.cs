using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoulLink.Services;

public static class Program
{
 [STAThread]public static int Main()
 {
  Directory.CreateDirectory("artifacts/overlay-transparency-probe");
  var app=new Application();app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/SoulLink;component/Theme/Styles.xaml")});
  var owner=new Window{Width=800,Height=500,Left=100,Top=100,Background=new LinearGradientBrush(Color.FromRgb(225,83,123),Color.FromRgb(67,136,211),45)};
  var anchor=new Border{Margin=new Thickness(50),Child=new TextBlock{Text="保护层测试：下方背景必须完整可见",Foreground=Brushes.White,FontSize=24,Margin=new Thickness(20)}};
  owner.Content=anchor;
  owner.Loaded+=async(_,_)=>{
   owner.Activate();await Task.Delay(500);
   var shield=new Border{Background=Brushes.Transparent,Cursor=System.Windows.Input.Cursors.Arrow,IsHitTestVisible=true};
   int clicks=0;shield.MouseLeftButtonUp+=(_,_)=>clicks++;
   var overlay=new OwnedOverlayWindow(owner,anchor,shield,"RDP 保护层",allowInactiveOwner:true);
   overlay.ShowOverlay(anchor.ActualWidth,anchor.ActualHeight);await Task.Delay(900);
   Capture("real-overlay-transparent");
   await Task.Delay(600);Capture("shield-content-transparent");
   shield.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonUpEvent});
   Log("transparent="+(shield.Background==Brushes.Transparent)+" requested="+overlay.IsRequested+" visible="+overlay.NativeVisible+" click="+clicks+" style=0x"+GetStyle(overlay).ToString("X"));
   overlay.HideOverlay();overlay.Close();owner.Close();
  };
  app.Run(owner);return 0;
 }
 [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="GetWindowLongW")]static extern IntPtr Get32(IntPtr h,int i);
 [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]static extern IntPtr Get64(IntPtr h,int i);
 static long GetStyle(Window w){var h=new System.Windows.Interop.WindowInteropHelper(w).Handle;return IntPtr.Size==8?Get64(h,-20).ToInt64():Get32(h,-20).ToInt32();}
 static void Capture(string name){using var bmp=new System.Drawing.Bitmap(800,500);using(var g=System.Drawing.Graphics.FromImage(bmp))g.CopyFromScreen(100,100,0,0,bmp.Size);bmp.Save("artifacts/overlay-transparency-probe/"+name+".png",System.Drawing.Imaging.ImageFormat.Png);}
 static void Log(string text){File.AppendAllText("artifacts/overlay-transparency-probe/real-overlay.log",text+"\n");Console.WriteLine(text);}
}