using System;using System.Collections.Generic;using System.Windows;using System.Windows.Controls;using System.Windows.Media;using SoulLink.Controls;using SoulLink.Models;
public static class LampProbe
{
 [STAThread]public static int Main()
 {
  _=new Application();
  var canvas=new FlowCanvas();
  var f=new AutomationFlow();
  f.Nodes.Add(new FlowNode{Id="n1",Kind=FlowNodeKind.Condition,Type=FlowNodeType.TrigSessionOnline,X=50,Y=50});
  canvas.Load(f);
  canvas.Measure(new Size(900,700));canvas.Arrange(new Rect(0,0,900,700));canvas.UpdateLayout();
  var states=new Dictionary<string,PlcNodeState>(StringComparer.OrdinalIgnoreCase)
  { ["n1"]=new PlcNodeState{Value=true,Output=true,Available=true} };
  canvas.SetLiveStates(states);
  Console.WriteLine("debug="+canvas._debugLastStatus);
  // 直接读画布上所有 TextBlock
  foreach(var c in canvas.Children)
  {
    if(c is Border b && b.Child is Grid g)
      foreach(var gc in g.Children)
        if(gc is StackPanel sp)
          foreach(var x in sp.Children)
            if(x is TextBlock tb) Console.WriteLine("  TXT: "+tb.Text);
  }
  return 0;
 }
}