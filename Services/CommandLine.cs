using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>命令行解析：soulink open A2 "D:\x.exe"</summary>
    public static class CommandLine
    {
        public sealed class Parsed
        {
            public string Verb { get; set; } = "";
            public List<string> Positional { get; } = new List<string>();
            public Dictionary<string, string> Options { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public bool WantsHelp { get; set; }
            public bool WantsQuiet { get; set; }
        }

        private static readonly HashSet<string> Verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "open","launch","start","close","stop","kill","restart","run","invoke","toggle"
        };

        private static readonly string[] ValueOptions =
        {
            "action","name","kind","op","do","exe","path","process","proc","user","account",
            "session","sid","delay","args","arguments","arg","page"
        };

        public static Parsed Parse(string[] args)
        {
            var p = new Parsed();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                if (a.Length == 0) continue;

                if (a.StartsWith("/?") || a == "-?" || a == "-h" || a == "--help" || a == "help")
                {
                    p.WantsHelp = true;
                    continue;
                }

                if (a == "--quiet" || a == "-q" || a == "/q")
                {
                    p.WantsQuiet = true;
                    continue;
                }

                if (a.StartsWith("--"))
                {
                    string key = a.Substring(2);
                    string val = "1";
                    int eq = key.IndexOf('=');
                    if (eq >= 0) { val = key.Substring(eq + 1); key = key.Substring(0, eq); }
                    else if (!IsFlag(key) && i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                        val = args[++i];
                    p.Options[key] = val;
                    continue;
                }

                if ((a.StartsWith("-") || a.StartsWith("/")) && a.Length > 1 && !a.Contains(":\\") && !a.StartsWith("/?"))
                {
                    string key = a.Substring(1);
                    if (IsValueOption(key))
                    {
                        string val = "1";
                        int eq = key.IndexOf('=');
                        if (eq >= 0) { val = key.Substring(eq + 1); key = key.Substring(0, eq); }
                        else if (i + 1 < args.Length) val = args[++i];
                        p.Options[key] = val;
                    }
                    else
                    {
                        p.Options[key] = "1";
                    }
                    continue;
                }

                if (p.Verb.Length == 0 && Verbs.Contains(a))
                {
                    p.Verb = a.ToLowerInvariant();
                    continue;
                }

                p.Positional.Add(a.Trim('\"'));
            }

            if (p.Verb.Length == 0)
            {
                if (p.Options.ContainsKey("toggle")) p.Verb = "toggle";
                else if (p.Options.ContainsKey("action")) p.Verb = "invoke";
            }
            return p;
        }

        private static bool IsFlag(string key) =>
            key.Equals("quiet", StringComparison.OrdinalIgnoreCase)
            || key.Equals("help", StringComparison.OrdinalIgnoreCase)
            // 纯诊断开关：不消费下一个参数，也不该让进程进入"命令模式"。
            || key.Equals("verbose-log", StringComparison.OrdinalIgnoreCase);

        private static bool IsValueOption(string key) =>
            ValueOptions.Contains(key.ToLowerInvariant());

        public static InvokeRequest ToRequest(Parsed p)
        {
            var r = new InvokeRequest { Quiet = true };

            string verb = p.Verb.ToLowerInvariant();
            ActionKind? kindFromVerb =
                verb == "close" || verb == "stop" || verb == "kill" ? ActionKind.Close :
                verb == "restart" ? ActionKind.Restart :
                verb.Length > 0 ? ActionKind.Launch : (ActionKind?)null;

            // --action <名字> 指已保存的快捷动作；--kind open|close|restart 指动作类型
            r.Action = Get(p, "action", "name");
            string? explicitKind = Get(p, "kind", "op", "do");
            if (!string.IsNullOrEmpty(explicitKind))
            {
                var k = ParseKind(explicitKind);
                if (k.HasValue) r.Kind = k.Value;
            }
            else if (kindFromVerb.HasValue)
            {
                r.Kind = kindFromVerb.Value;
            }

            r.Process = Get(p, "exe", "path", "process", "proc");
            r.Account = Get(p, "account", "target", "as");
            r.User = Get(p, "user");
            // 密码已废弃：投送走 SYSTEM 计划任务 + WTSQueryUserToken，不需要任何凭据
            r.Arguments = Get(p, "args", "arguments", "arg");

            string? sid = Get(p, "session", "sid");
            if (!string.IsNullOrEmpty(sid) && int.TryParse(sid, out int s)) r.Session = s;

            string? delay = Get(p, "delay");
            if (!string.IsNullOrEmpty(delay) && int.TryParse(delay, out int d)) r.Delay = Math.Max(0, d);

            // 位置参数：open A2  D:\x.exe
            var pos = p.Positional.ToList();
            if (pos.Count > 0 && pos[0].StartsWith("--"))
            {
            }
            if (string.IsNullOrEmpty(r.Account) && pos.Count >= 1 && !LooksLikePath(pos[0]))
                r.Account = pos[0];
            else if (string.IsNullOrEmpty(r.Account) && pos.Count >= 2)
                r.Account = pos[0];

            if (string.IsNullOrEmpty(r.Process))
            {
                var candidate = pos.FirstOrDefault(LooksLikePath);
                if (candidate != null) r.Process = candidate;
            }

            return r;
        }

        private static ActionKind? ParseKind(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "open": case "launch": case "start": case "1": return ActionKind.Launch;
                case "close": case "stop": case "kill": case "0": return ActionKind.Close;
                case "restart": case "reboot": case "2": return ActionKind.Restart;
                default: return null;
            }
        }

        private static string? Get(Parsed p, params string[] keys)
        {
            foreach (var k in keys)
                if (p.Options.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v;
            return null;
        }

        private static bool LooksLikePath(string s) =>
            s.Contains("\\") || s.Contains(":\\") || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || s.Contains('/');

        public const string HelpText =
@"SoulLink · 跨会话执行器（在指定用户的桌面静默启动 / 关闭程序）

用法：
  SoulLink.exe open  A2  ""D:\AAA挂机专区\A2\BetterGI\BetterGI.exe""      打开
  SoulLink.exe close A2  ""D:\AAA挂机专区\A2\BetterGI\BetterGI.exe""      关闭
  SoulLink.exe restart A2 ""...\BetterGI.exe""                             重启
  SoulLink.exe run --action ""A2 打开 BetterGI""                               执行已保存的快捷动作
  SoulLink.exe open --kind close --user A2 --exe ""...\BetterGI.exe""         临时指定类型

可选参数：
  --user <用户名>        直接用账号名，不必事先在界面里建账号
  --session <ID>         强制指定目标会话 ID，跳过自动查找（一般不用，按用户名自动匹配）
  --args ""...""          附加启动参数
  --delay <秒>           延时执行
  --quiet                只返回退出码（默认）
  --page <页面>          仅打开界面并切到指定页（launch/sessions/procs/cli/settings）

退出码：0 = 成功，1 = 失败，2 = 参数错误，3 = 目标账号无会话

点击界面里的「复制命令」按钮可以直接生成一行能用的命令。";
    }
}