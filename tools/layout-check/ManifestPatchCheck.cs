using System;using System.IO;using System.Text;using SoulLink.Services;
public static class ManifestPatchCheck
{
    static int fails;
    static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
    // 直接扫全文件里出现的清单等级片段（清单是 UTF-8 明文）
    static bool Has(string exe,string needle)=>Encoding.UTF8.GetString(File.ReadAllBytes(exe)).Contains(needle);
    public static int Main()
    {
        string src=@"E:\Documents\deepseek-harness\SoulLink\dist\SoulLink.exe";
        Check(File.Exists(src),"找到发布产物");
        string dst=Path.Combine(Path.GetTempPath(),"slpatch_"+Guid.NewGuid().ToString("N")+".exe");
        File.Copy(src,dst,true);
        Check(Has(dst,"requireAdministrator"),"原始产物要求管理员：SYSTEM 任务启动它会返回 740");
        long before=new FileInfo(dst).Length;
        bool ok=ManifestPatcher.MakeAsInvoker(dst);
        Check(ok,"清单补丁执行成功");
        Check(Has(dst,"asInvoker")&&!Has(dst,"requireAdministrator"),"引导副本改为 asInvoker，不再触发 740");
        Check(new FileInfo(dst).Length==before,"等长替换，PE 结构与大小不变");
        File.Delete(dst);
        Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);
        return fails;
    }
}
