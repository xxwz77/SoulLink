using System;using System.IO;using System.Text;
public static class ManifestDebug2
{
 public static int Main()
 {
  var d=File.ReadAllBytes(@"E:\Documents\deepseek-harness\SoulLink\dist\SoulLink.exe");
  int pe=BitConverter.ToInt32(d,0x3C);int optSize=BitConverter.ToUInt16(d,pe+20);int opt=pe+24;int magic=BitConverter.ToUInt16(d,opt);
  int dirOff=magic==0x20B?opt+112:opt+96;int resRva=BitConverter.ToInt32(d,dirOff+16);
  int resOff=0;int sections=BitConverter.ToUInt16(d,pe+6);int ss=opt+optSize;
  for(int i=0;i<sections;i++){int s=ss+i*40;uint va=BitConverter.ToUInt32(d,s+12);uint sz=BitConverter.ToUInt32(d,s+16);uint raw=BitConverter.ToUInt32(d,s+20);
    if((uint)resRva>=va&&(uint)resRva<va+Math.Max(sz,1u)){resOff=(int)(raw+(resRva-va));break;}}
  Console.WriteLine("resDirOffset="+resOff.ToString("X"));
  int n=BitConverter.ToUInt16(d,resOff+12)+BitConverter.ToUInt16(d,resOff+14);
  Console.WriteLine("entries="+n);
  for(int i=0;i<n;i++){int e=resOff+16+i*8;uint name=BitConverter.ToUInt32(d,e);uint off=BitConverter.ToUInt32(d,e+4);
    string label=(name&0x80000000)!=0?("str@0x"+(name&0x7FFFFFFF).ToString("X")):name.ToString();
    Console.WriteLine("  type="+label+" off=0x"+off.ToString("X"));}
  return 0;
 }
}
