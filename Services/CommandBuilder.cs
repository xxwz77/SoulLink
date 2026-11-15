using System;
using System.IO;
using System.Linq;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>生成一段可以直接丢进计划任务 / 批处理 / PowerShell 的命令。</summary>
    public static class CommandBuilder
    {
        public static string ExePath
        {
            get
            {
                try
                {
                    var p = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return p;
                }
                catch { }
                return Path.Combine(AppContext.BaseDirectory, "SoulLink.exe");
            }
        }

        public static string ForAction(ActionItem a)
        {
            string verb = a.Kind == ActionKind.Close ? "close" : a.Kind == ActionKind.Restart ? "restart" : "open";
            string exe = Quote(ExePath);
            return exe + " " + verb + " --action " + Quote(a.Name) + " --quiet";
        }

        public static string ForTarget(string user, string exePath, ActionKind kind, int? session = null, string? args = null)
        {
            string verb = kind == ActionKind.Close ? "close" : kind == ActionKind.Restart ? "restart" : "open";
            string s = Quote(ExePath) + " " + verb + " --user " + Quote(user) + " --exe " + Quote(exePath) + " --quiet";
            if (session.HasValue) s += " --session " + session.Value;
            if (!string.IsNullOrWhiteSpace(args)) s += " --args " + Quote(args!);
            return s;
        }

        public static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";

        /// <summary>生成的 .bat 内容（与用户原来那个 bat 等价，但不需要手填会话 ID）</summary>
        public static string BatchFor(ActionItem a, AppConfig cfg)
        {
            var account = cfg.Accounts.FirstOrDefault(x => x.Id == a.AccountId);
            string verb = a.Kind == ActionKind.Close ? "close" : a.Kind == ActionKind.Restart ? "restart" : "open";
            string user = account?.UserName ?? "用户名";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("rem SoulLink 生成的命令 · 无需手填会话 ID");
            sb.AppendLine("rem 目标账号：" + user);
            sb.AppendLine(Quote(ExePath) + " " + verb + " --action " + Quote(a.Name) + " --quiet");
            sb.AppendLine("exit /b %errorlevel%");
            return sb.ToString();
        }
    }
}
