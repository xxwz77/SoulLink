using System;
using System.IO;

namespace SoulLink.Services
{
    /// <summary>
    /// 进程身份：让「本地挂机验证版」和「测试版」完全隔离。
    ///
    /// ★ 为什么以程序文件名作为身份：
    ///   自救工具用 Process.GetProcessesByName("SoulLink") 找进程，
    ///   只要文件名不同，它永远杀不到另一个版本。
    ///   同时单实例互斥体 / IPC 管道 / 配置目录 / relay 管道 / 临时任务目录
    ///   全部由这一个名字派生，改文件名即完成全套隔离，不会漏改。
    ///
    /// 用法：
    ///   SoulLink.exe       → 测试版（身份 "SoulLink"）
    ///   SoulLink-Stable.exe → 挂机验证版（身份 "SoulLink-Stable"）
    ///
    /// 也可以显式指定：命令行加 --identity=名字，优先级最高。
    /// </summary>
    public static class AppIdentity
    {
        private static readonly Lazy<string> _name = new Lazy<string>(Resolve);

        /// <summary>身份名（用于管道、互斥体、目录名、任务名）。</summary>
        public static string Name => _name.Value;

        /// <summary>配置目录：%LOCALAPPDATA%\&lt;身份&gt;</summary>
        public static string ConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

        /// <summary>单实例互斥体名。</summary>
        public static string MutexName => @"Global\" + Name + ".SingleInstance.v1";

        /// <summary>主 IPC 管道名。</summary>
        public static string IpcPipeName => Name + ".IPC.v1";

        /// <summary>relay 管道前缀。</summary>
        public static string RelayPipePrefix => Name + "_Relay_";

        /// <summary>relay 事件管道后缀。</summary>
        public const string RelayEventSuffix = "_Events";

        /// <summary>SYSTEM 任务名前缀。</summary>
        public static string TaskPrefix => Name + "Run_";

        /// <summary>临时工作目录。</summary>
        public static string TempRoot => Path.Combine(Path.GetTempPath(), Name);

        private static string Resolve()
        {
            // ① 命令行显式指定优先
            try
            {
                foreach (var arg in Environment.GetCommandLineArgs())
                {
                    if (arg != null && arg.StartsWith("--identity=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = arg.Substring("--identity=".Length).Trim();
                        if (v.Length > 0) return Sanitize(v);
                    }
                }
            }
            catch { }

            // ② 用当前可执行文件名（不含扩展名）
            try
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path))
                {
                    string file = Path.GetFileNameWithoutExtension(path);
                    if (!string.IsNullOrWhiteSpace(file)) return Sanitize(file);
                }
                var asm = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
                if (!string.IsNullOrWhiteSpace(asm)) return Sanitize(asm!);
            }
            catch { }

            return "SoulLink";
        }

        /// <summary>只保留字母数字、点、横线、下划线，避免路径/管道名非法字符。</summary>
        private static string Sanitize(string raw)
        {
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_') sb.Append(c);
            }
            var s = sb.ToString();
            return s.Length == 0 ? "SoulLink" : s;
        }
    }
}
