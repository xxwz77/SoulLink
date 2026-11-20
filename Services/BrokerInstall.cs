using System;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;

namespace SoulLink.Services
{
    /// <summary>固定测试版目录。Broker 只接受这个受系统 ACL 保护的位置。</summary>
    public static class BrokerInstall
    {
        public const string DirectoryName = "SoulLinkDev";
        public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), DirectoryName);
        public static string ServiceExe => Path.Combine(Root, "SoulLink.exe");
        public static string AgentExe => Path.Combine(Root, "SoulLink.Agent.exe");

        /// <summary>Broker 通道代理的进程名（不带 .exe）。界面识别通道时要按它匹配。</summary>
        public static string AgentProcessName => Path.GetFileNameWithoutExtension(AgentExe);
        public static bool IsFixedTestPath(string? path) => string.Equals(
            Path.GetFullPath(path ?? ""), ServiceExe, StringComparison.OrdinalIgnoreCase);

        public static bool ValidAgent(string source, string agent)
        {
            try
            {
                if (!string.Equals(Path.GetFullPath(agent), AgentExe, StringComparison.OrdinalIgnoreCase)) return false;
                using var a = File.OpenRead(source); using var b = File.OpenRead(agent);
                // 清单由安装器从本版本主程序生成；其余版本必须一致。
                return FileVersionInfo.GetVersionInfo(source).FileVersion == FileVersionInfo.GetVersionInfo(agent).FileVersion;
            }
            catch { return false; }
        }

        /// <summary>单文件发布的 BaseDirectory 可能是临时目录，必须以真实 exe 旁的工具为准。</summary>
        public static string FindScript(string action)
        {
            string exe = SystemLauncher.ResolveSelfExe();
            string directory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
            string script = Path.Combine(directory, "tools", "broker-" + action + ".ps1");
            if (File.Exists(script)) return Path.GetFullPath(script);
            string local = Path.Combine(AppContext.BaseDirectory, "broker-" + action + ".ps1");
            return File.Exists(local) ? Path.GetFullPath(local) : script;
        }

        public static string AgentFor(string source)
        {
            if (!IsFixedTestPath(source)) return source;
            if (!File.Exists(AgentExe) || !ValidAgent(source, AgentExe)) return "";
            return AgentExe;
        }
    }
}
