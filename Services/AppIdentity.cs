using System;
using System.Diagnostics;
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

        // ═══════════════════════════════════════════════════════════════════
        //  ★★ 2.20.0：数据目录从 %LOCALAPPDATA% 改到「exe 所在目录」★★
        //
        //  用户诉求：C 盘小的机器负担太重（实测 BGI 备份一项就 872 MB）。
        //
        //  新策略（按优先级）：
        //    ① 环境变量 SOULLINK_DATA_DIR 显式指定           —— 最优先，便于测试/迁移
        //    ② exe 同目录下的 data\（可写就用它）             —— 默认行为
        //    ③ 回退到 %LOCALAPPDATA%\<身份>\                  —— 装在 Program Files 时
        //
        //  为什么①存在：用户可能想把数据放到别的盘（比如 D:\SoulLinkData）。
        //  为什么③存在：exe 若装在 C:\Program Files\，普通用户没有写权限，
        //    DataDir 会抛异常或写失败，必须有个安全的落点。
        //
        //  ★ 老数据不会自动迁移：旧目录里的东西还在原地，新目录是空的。
        //    这是刻意的——自动搬 872 MB 有风险（中途断电、权限、文件被占用），
        //    交给用户自己决定。设置页有「打开数据目录」按钮，搬不搬一目了然。
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>exe 所在目录（真实位置，不是 %TEMP% 解压目录）。</summary>
        public static string ExeDir
        {
            get
            {
                try
                {
                    string? p = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(p))
                    {
                        string? d = Path.GetDirectoryName(p);
                        if (!string.IsNullOrEmpty(d)) return d!;
                    }
                }
                catch { }

                // 回退：非单文件时两者相同；单文件时 BaseDirectory 指向解压目录，
                // 但 ProcessPath 一般都能拿到，走到这里说明已被裁剪。
                try { return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); }
                catch { return "."; }
            }
        }

        private static readonly Lazy<string> _dataDir = new Lazy<string>(ResolveDataDir);

        /// <summary>数据目录（配置、日志、BGI 备份都在这里）。</summary>
        public static string DataDir => _dataDir.Value;

        /// <summary>是否处于「便携模式」（数据在 exe 旁边）。</summary>
        public static bool IsPortable
        {
            get
            {
                try
                {
                    return string.Equals(
                        Path.GetFullPath(DataDir).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(Path.Combine(ExeDir, "data")).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        /// <summary>旧版数据目录（%LOCALAPPDATA%\身份），仅用于提示用户老数据在哪。</summary>
        public static string LegacyConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

        private static string ResolveDataDir()
        {
            // ① 环境变量显式指定
            try
            {
                string? env = Environment.GetEnvironmentVariable("SOULLINK_DATA_DIR");
                if (!string.IsNullOrWhiteSpace(env))
                {
                    string full = Path.GetFullPath(env!.Trim());
                    TryCreateWritable(full);
                    return full;
                }
            }
            catch { }

            // ② exe 同目录\data
            try
            {
                string portable = Path.Combine(ExeDir, "data");
                if (TryCreateWritable(portable)) return portable;
            }
            catch { }

            // ③ 回退 %LOCALAPPDATA%\身份
            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
            try { Directory.CreateDirectory(legacy); } catch { }
            return legacy;
        }

        /// <summary>
        /// 建目录并**实际写入一个探针文件**确认可写。
        ///
        /// ★ 只调 Directory.CreateDirectory 判断不出来：「装在 Program Files 下」
        ///   时，管理员运行时能建目录，但普通用户运行时写文件会被 UAC 虚拟化
        ///   或直接拒绝。必须真写一次才知道能不能用。
        /// </summary>
        private static bool TryCreateWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                try { Debug.WriteLine("[AppIdentity] 数据目录不可写：" + dir); } catch { }
                return false;
            }
        }

        /// <summary>配置目录（= DataDir，保留旧名字以免改动过大）。</summary>
        public static string ConfigDir => DataDir;

        /// <summary>单实例互斥体名。</summary>
        public static string MutexName => @"Global" + Name + ".SingleInstance.v1";

        /// <summary>主 IPC 管道名。</summary>
        public static string IpcPipeName => Name + ".IPC.v1";

        /// <summary>relay 管道前缀。</summary>
        public static string RelayPipePrefix => Name + "_Relay_";

        /// <summary>relay 事件管道后缀。</summary>
        public const string RelayEventSuffix = "_Events";

        /// <summary>SYSTEM 任务名前缀。</summary>
        public static string TaskPrefix => Name + "Run_";

        /// <summary>临时工作目录（仍放 %TEMP%，因为是一次性中间产物）。</summary>
        public static string TempRoot => Path.Combine(Path.GetTempPath(), Name);

        /// <summary>
        /// 手动代理的文件名。它是主程序的副本，必须归属主程序身份，
        /// 否则会被当成另一个版本，导致管道前缀对不上、永远找不到宿主管道。
        /// </summary>
        public const string AgentFileName = "SoulLink.Agent";

        /// <summary>当前进程是不是"手动代理"副本（按文件名判断，不看参数）。</summary>
        public static bool IsAgentExecutable
        {
            get
            {
                try
                {
                    string file = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
                    return string.Equals(file, AgentFileName, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

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

            // ② 手动代理副本：SoulLink.Agent.exe 属于主程序身份。
            //    ★ 它若按自己的文件名取身份，会得到 "SoulLink.Agent"，
            //      于是管道前缀变成 SoulLink.Agent_Relay_...，永远匹配不到宿主。
            try
            {
                if (IsAgentExecutable)
                {
                    // 配置里的身份名就是主程序文件名；取不到就用 SoulLink。
                    return "SoulLink";
                }
            }
            catch { }

            // ③ 用当前可执行文件名（不含扩展名）
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
