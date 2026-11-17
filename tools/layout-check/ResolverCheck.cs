using System;
using System.IO;

// 只验证 BgiPathResolver 的路径规范化规则（该规则与正式实现保持一致）。
// 完整解析逻辑依赖 App.Config / 进程探测，无法脱离主程序运行，
// 因此这里单独锁住"等价路径必须相同"这条最容易出错的规则。
public static class ResolverCheck
{
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            string p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            p = Path.GetFullPath(p);
            return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return (path ?? "").Trim(); }
    }

    public static int Main()
    {
        int fails = 0;
        void Check(bool c, string what)
        {
            if (!c) { Console.WriteLine("FAIL " + what); fails++; }
            else Console.WriteLine("ok   " + what);
        }

        var a = Normalize(@"C:\Tools\BGI\BetterGI.exe");
        var b = Normalize(@"C:\Tools\..\Tools\BGI\BetterGI.exe");
        Check(string.Equals(a, b, StringComparison.OrdinalIgnoreCase), "等价路径规范化后相同");
        Check(a.EndsWith("BetterGI.exe", StringComparison.OrdinalIgnoreCase), "保留文件名");

        var c1 = Normalize(@"C:\Tools\BGI\");
        Check(!c1.EndsWith(Path.DirectorySeparatorChar.ToString()), "去掉结尾分隔符");

        Check(Normalize("") == "", "空字符串 -> 空");
        Check(Normalize(null) == "", "null -> 空");

        var vars = Normalize(@"%SystemRoot%\System32");
        Check(!vars.Contains('%'), "展开环境变量");

        var quoted = Normalize("\"C:\\Tools\\BGI\\BetterGI.exe\"");
        Check(quoted.EndsWith("BetterGI.exe", StringComparison.OrdinalIgnoreCase), "去除引号");

        Console.WriteLine(fails == 0 ? "ALL RESOLVER CHECKS PASSED" : fails + " FAILURES");
        return fails;
    }
}
