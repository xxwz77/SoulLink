using System;using System.Reflection;using System.Threading;using System.Windows;using SoulLink.Controls;using SoulLink.Models;
public static class FlowCanvasCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 [STAThread]public static int Main()
 {
  var f=new AutomationFlow();var a=new FlowNode{Id="a",Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange,X=0,Y=0};var b=new FlowNode{Id="b",Kind=FlowNodeKind.Condition,Type=FlowNodeType.CondTimeRange,X=350,Y=150};var c=new FlowNode{Id="c",Kind=FlowNodeKind.Action,Type=FlowNodeType.ActDelay,X=700,Y=300};var e=new FlowNode{Id="e",Kind=FlowNodeKind.Terminate,Type=FlowNodeType.ActEndFlow,X=1050,Y=450};f.Nodes.AddRange(new[]{a,b,c,e});
  var canvas=new FlowCanvas{PaintMode=true};canvas.Load(f);canvas.Measure(new Size(2400,1600));canvas.Arrange(new Rect(0,0,2400,1600));
  var t=typeof(FlowCanvas);var begin=t.GetMethod("BeginLink",BindingFlags.Instance|BindingFlags.NonPublic)!;var paint=t.GetMethod("TryPaintLink",BindingFlags.Instance|BindingFlags.NonPublic)!;var finish=t.GetMethod("FinishLink",BindingFlags.Instance|BindingFlags.NonPublic)!;
  begin.Invoke(canvas,new object[]{a,"",null});paint.Invoke(canvas,new object[]{b,new Point(350,150),true});paint.Invoke(canvas,new object[]{c,new Point(700,300),true});
  Check(f.Edges.Count==2&&f.Edges[0].From=="a"&&f.Edges[0].To=="b"&&f.Edges[1].From=="b"&&f.Edges[1].To=="c","画笔同一路径连续连接 A→B→C");
  paint.Invoke(canvas,new object[]{a,new Point(0,0),true});Check(f.Edges.Count==2,"环被拒绝且已有连线保持");
  paint.Invoke(canvas,new object[]{b,new Point(350,150),true});Check(f.Edges.Count==2,"重复连线不增加");
  paint.Invoke(canvas,new object[]{e,new Point(1050,450),true});Check(f.Edges.Count==3&&f.Edges[2].From=="c"&&f.Edges[2].To=="e","可连接 END，并结束本次串联");
  begin.Invoke(canvas,new object[]{e,"",null});Check(f.Edges.Count==3,"END 不能继续连出");
  canvas.PaintMode=false;Check(!canvas.PaintMode,"关闭画笔清理状态");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}
