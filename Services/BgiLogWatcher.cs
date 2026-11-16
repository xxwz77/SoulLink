using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// BGI 实时日志读取。
    ///
    /// 依据 docs/bgi-realtime-log-reader.md：
    ///   BGI 用 Serilog 写 &lt;程序目录&gt;\log\better-genshin-impactYYYYMMDD.log，
    ///   每写一条就 flush，所以直接读文件增量即可做到准实时，不需要 IPC 或助手。
    ///
    /// 定位方式：枚举 BetterGI.exe 进程 → 按 SessionId 对应到 Windows 账号
    /// → 日志目录就是 exe 同级的 log\。这样用户换路径、升级版本都不用手工配置。
    /// </summary>
    public static class BgiLogWatcher
    {
        /// <summary>一个会话里找到的 BGI 实例。</summary>
        public sealed class Instance
        {
            public int SessionId { get; init; }
            public int Pid { get; init; }
            public string ExePath { get; init; } = "";
            public string LogFile { get; init; } = "";
            public long Length { get; init; }
            public DateTime LastWrite { get; init; }
        }

        /// <summary>
        /// 检测哪些会话已有注入通道（继电器进程在线）。
        ///
        /// 判定依据：本版本的 SoulLink 进程里带 "--relay" 且管道名含
        /// "&lt;身份&gt;_Relay_&lt;会话号&gt;_" 的，就是该会话的注入代理。
        /// 不依赖 WMI 查命令行（项目没引 System.Management），改用 .NET 自身能力。
        /// </summary>
        public static HashSet<int> DiscoverChannelSessions()
        {
            var result = new HashSet<int>();
            try
            {
                // ★ 直接枚举命名管道：每个会话的注入通道都会创建
                //     \\.\pipe\<身份>_Relay_<会话号>_<随机串>
                //   管道存在 = 通道在线。这比读进程命令行可靠得多
                //   （.NET 8 的 Process 没有 CommandLine 属性，需读 PEB）。
                string prefix = AppIdentity.RelayPipePrefix;   // SoulLink_Relay_
                foreach (var path in Directory.GetFiles(@"\\.\pipe\"))
                {
                    string name = Path.GetFileName(path);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith("_Events", StringComparison.OrdinalIgnoreCase)) continue;  // 事件管道跳过

                    string rest = name.Substring(prefix.Length);      // "6_e6940012"
                    int us = rest.IndexOf('_');
                    if (us <= 0) continue;
                    if (int.TryParse(rest.Substring(0, us), out int sid) && sid > 0)
                        result.Add(sid);
                }
            }
            catch { }
            return result;
        }

        /// <summary>从命令行字符串解析出会话号（形如 ...Relay_6_abc123）。</summary>
        public static int ParseRelaySessionId(string? commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return -1;
            try
            {
                string prefix = AppIdentity.RelayPipePrefix;
                int i = commandLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return -1;
                i += prefix.Length;
                int j = i;
                while (j < commandLine.Length && char.IsDigit(commandLine[j])) j++;
                if (j == i) return -1;
                return int.TryParse(commandLine.Substring(i, j - i), out int sid) ? sid : -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// 枚举所有正在运行的 BGI，按会话号返回（同一会话只取最新的一个日志）。
        /// </summary>
        public static Dictionary<int, Instance> Discover()
        {
            var result = new Dictionary<int, Instance>();
            try
            {
                foreach (var p in Process.GetProcessesByName("BetterGI"))
                {
                    try
                    {
                        string exe = p.MainModule?.FileName ?? "";
                        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;

                        string logDir = Path.Combine(Path.GetDirectoryName(exe) ?? "", "log");
                        if (!Directory.Exists(logDir)) continue;

                        // 取该目录下最新的日志文件（BGI 按天滚动）
                        string? newest = null;
                        DateTime newestTime = DateTime.MinValue;
                        foreach (var f in Directory.GetFiles(logDir, "better-genshin-impact*.log"))
                        {
                            var t = File.GetLastWriteTime(f);
                            if (t > newestTime) { newestTime = t; newest = f; }
                        }
                        if (newest == null) continue;

                        var fi = new FileInfo(newest);
                        result[p.SessionId] = new Instance
                        {
                            SessionId = p.SessionId,
                            Pid = p.Id,
                            ExePath = exe,
                            LogFile = newest,
                            Length = fi.Length,
                            LastWrite = fi.LastWriteTime
                        };
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 读取日志文件的最后 N 行（用于实时显示）。
        /// ★ 只读尾部：日志可能几十 MB，不能整文件读。
        /// </summary>
        public static (string text, DateTime lastWrite) TailLines(string path, int maxLines = 40)
        {
            try
            {
                if (!File.Exists(path)) return ("", DateTime.MinValue);

                var fi = new FileInfo(path);
                // 只读最后 64KB，够 ~40 行
                long readSize = Math.Min(fi.Length, 64 * 1024);
                long start = fi.Length - readSize;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[readSize];
                int got = fs.Read(buf, 0, buf.Length);

                // BGI 日志是 UTF-8
                string all = Encoding.UTF8.GetString(buf, 0, got);

                // 从头丢弃半行（不是行首的残留）
                if (start > 0)
                {
                    int nl = all.IndexOf('\n');
                    if (nl >= 0 && nl + 1 < all.Length) all = all.Substring(nl + 1);
                }

                var lines = all.Replace("\r", "").Split('\n');
                int skip = Math.Max(0, lines.Length - maxLines);
                var sb = new StringBuilder();
                for (int i = skip; i < lines.Length; i++)
                {
                    string ln = lines[i];
                    if (ln.Trim().Length == 0) continue;   // BGI 日志有大量空行
                    sb.AppendLine(ln);
                }
                return (sb.ToString(), fi.LastWriteTime);
            }
            catch (Exception ex) { return ("读取失败：" + ex.Message, DateTime.MinValue); }
        }
    }
}
