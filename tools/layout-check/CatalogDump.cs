using System;using System.Linq;using SoulLink.Models;
public static class CatalogDump
{
 public static void Main()
 {
  foreach(var k in new[]{FlowNodeKind.Condition,FlowNodeKind.Action,FlowNodeKind.Terminate})
  {
   var list=FlowNodeCatalog.ByKind(k);
   Console.WriteLine("=== "+k+" ("+list.Count+") ===");
   foreach(var d in list) Console.WriteLine("  "+d.Type+" | "+d.Title+" | "+d.Desc);
  }
 }
}