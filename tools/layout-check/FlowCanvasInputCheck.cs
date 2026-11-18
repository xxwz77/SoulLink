using System;using System.IO;using System.Runtime.InteropServices;using System.Threading.Tasks;using System.Windows;using System.Windows.Controls;using System.Windows.Interop;using System.Windows.Media;using System.Windows.Media.Imaging;using System.Windows.Threading;using SoulLink.Controls;using SoulLink.Models;
public static class FlowCanvasInputCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 [DllImport("user32.dll")]static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")]static extern void mouse_event(uint f,int x,int y,uint data,UIntPtr extra);
 [STAThread]public static int Main()
 {
  var app=new Application();var flow=new AutomationFlow();
  var a=new FlowNode{Id="a",Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange,X=100,Y=120};
  var b=new FlowNode{Id="b",Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange,X=450,Y=300};
  var c=new FlowNode{Id="c",Kind=FlowNodeKind.Action,Type=FlowNodeType.ActDelay,X=800,Y=480};flow.Nodes.AddRange(new[]{a,b,c});
  var canvas=new FlowCanvas();canvas.Load(flow);var status=new TextBlock{Height=28};
  var panel=new DockPanel();DockPanel.SetDock(status,Dock.Top);panel.Children.Add(status);panel.Children.Add(canvas);
  var win=new Window{Title="SoulLink isolated mouse check",Width=1200,Height=760,Left=60,Top=40,Topmost=true,Content=panel,Background=Brushes.White};win.ContentRendered+=async(_,__)=>await Run(win,canvas,flow,status,a,b,c);win.Show();app.Run(win);return fails;
 }
 static async Task Run(Window w,FlowCanvas canvas,AutomationFlow f,TextBlock status,FlowNode a,FlowNode b,FlowNode c)
 {
  try
  {
   await Frames(8);
   await Drag(canvas,a.X+114,a.Y+70,b.X+114,b.Y+20);
   await Frames(8);Check(f.Edges.Count==1&&f.Edges[0].From=="a"&&f.Edges[0].To=="b","普通模式真实鼠标拖拽底部出口可连线");
   canvas.PaintMode=true;await Frames(4);
   await Click(canvas,b.X+114,b.Y+70);await Frames(4);
   await Click(canvas,c.X+80,c.Y+30);await Frames(8);
   Check(f.Edges.Count==2&&f.Edges[1].From=="b"&&f.Edges[1].To=="c","画笔模式真实鼠标连续点击可连线");
   status.Text="edges="+f.Edges.Count;
   var rtb=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);rtb.Render(w);
   Directory.CreateDirectory("artifacts");var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(rtb));using var fs=File.Create("artifacts/flow-canvas-input.png");enc.Save(fs);
  }
  catch(Exception ex){Console.WriteLine("FAIL "+ex);fails++;}
  finally{w.Close();}
 }
 static Point Screen(FrameworkElement e,double x,double y)=>e.PointToScreen(new Point(x,y));
 static async Task Drag(FrameworkElement e,double x1,double y1,double x2,double y2)
 {
  var p=Screen(e,x1,y1);SetCursorPos((int)p.X,(int)p.Y);await Frames(3);mouse_event(2,0,0,0,UIntPtr.Zero);await Frames(3);
  for(int i=1;i<=8;i++){var q=Screen(e,x1+(x2-x1)*i/8,y1+(y2-y1)*i/8);SetCursorPos((int)q.X,(int)q.Y);await Frames(2);}mouse_event(4,0,0,0,UIntPtr.Zero);
 }
 static async Task Click(FrameworkElement e,double x,double y){var p=Screen(e,x,y);SetCursorPos((int)p.X,(int)p.Y);await Frames(3);mouse_event(2,0,0,0,UIntPtr.Zero);await Frames(2);mouse_event(4,0,0,0,UIntPtr.Zero);}
 static async Task Frames(int n){for(int i=0;i<n;i++){await Dispatcher.Yield(DispatcherPriority.Render);await Task.Delay(35);}}
}
