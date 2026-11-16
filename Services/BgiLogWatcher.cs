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
                // ★ 按请求行数估算读取量：BGI 中文日志每行约 120~200 字节，
                //   还有大量空行，所以按 400 字节/行保守估算。
                //   原来固定 64KB，请求 400 行时实际只覆盖 ~150 行，
                //   导致"状态行在更早位置"时解析不到（界面显示"等待日志中的状态信息"）。
                long estimate = Math.Min(fi.Length, (long)Math.Max(64, maxLines) * 400L + 8192L);
                long readSize = Math.Min(fi.Length, Math.Max(64 * 1024, estimate));
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

        /// <summary>
        /// 从文件尾部**分段向前**扫描，直到取够或有足够数据。
        ///
        /// ★ 为什么需要它：实测 20261107 那份日志里，
        ///   "配置组=xxx" 出现在第 16594 行，而文件有 59 万行 ——
        ///   只读尾部 800 行根本拿不到配置组，界面就显示"—"。
        ///   这里按 512KB 一段往前回溯，最多回溯 totalBudget 字节，
        ///   既能覆盖"状态行很早"的情况，又不会把 60MB 全读进内存。
        /// </summary>
        public static string TailLinesDeep(string path, int maxLines = 400,
            long totalBudget = 16L * 1024 * 1024)
        {
            try
            {
                if (!File.Exists(path)) return "";

                var fi = new FileInfo(path);
                long budget = Math.Min(fi.Length, totalBudget);
                long seg = 512 * 1024;
                long pos = fi.Length;                 // 从尾部开始
                long consumed = 0;
                var acc = new StringBuilder();

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                while (pos > 0 && consumed < budget && CountLines(acc) < maxLines)
                {
                    long take = Math.Min(seg, pos);
                    long start = pos - take;
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[take];
                    int got = fs.Read(buf, 0, buf.Length);
                    if (got <= 0) break;

                    string part = Encoding.UTF8.GetString(buf, 0, got);
                    // 如果不在文件开头，前面可能是半行，先接到已有的前面
                    acc.Insert(0, part);
                    consumed += take;
                    pos = start;

                    // 防内存爆：超过 8MB 文本就停
                    if (acc.Length > 8 * 1024 * 1024) break;
                }

                string all = acc.ToString();
                // 若没读到文件头，丢掉最前面那半行
                if (pos > 0)
                {
                    int nl = all.IndexOf(Convert.ToChar(10));
                    if (nl >= 0 && nl + 1 < all.Length) all = all.Substring(nl + 1);
                }

                var lines = all.Replace("\r", "").Split(Convert.ToChar(10));
                int skip = Math.Max(0, lines.Length - maxLines);
                var sb = new StringBuilder();
                for (int i = skip; i < lines.Length; i++)
                {
                    string ln = lines[i];
                    if (ln.Trim().Length == 0) continue;
                    sb.AppendLine(ln);
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// 扫整天日志，按**配置组**归类统计拾取。
        ///
        /// ★ 为什么必须单独扫全天：增量解析只看得到最近几行，
        ///   而拾取散布在全天几十万行里。
        ///
        /// 配置组边界的判定（实测日志）：
        ///   · 配置组="CD采集-全采集"   ← 拾取诊断行里带出来，最频繁、最可靠
        ///   · 锄地一条龙任务启动 [配置组: "xxx"]
        ///   · 启用一条龙配置：xxx
        /// 拾取行（交互或拾取："xxx"）归属于**它之前最近出现的配置组**。
        /// </summary>
        /// <summary>
        /// 扫整天日志，返回：
        ///   · byGroup  —— 每个配置组各捡了什么（用于"展开全天"）
        ///   · lastGroup —— 最后一个配置组名
        ///   · roundPicks —— **最后一轮任务**（最后一次"任务启动"之后）的拾取
        ///
        /// ★ 为什么要单独算 roundPicks：
        ///   配置组名可能一整天都叫同一个（比如"CD采集-全采集"），
        ///   按配置组汇总得到的是"整天总量"，不是"这一轮"。
        ///   真正的轮次边界是日志里的：
        ///     → "任务启动！"
        ///     锄地一条龙任务启动 [配置组: "xxx"]，数据目录: "..."
        ///   最后一次"任务启动"之后的拾取，才是当前这一轮的收获。
        /// </summary>
        public static (System.Collections.Generic.Dictionary<string,
                         System.Collections.Generic.Dictionary<string, int>> byGroup,
                      string lastGroup)
            CountDailyPicksByGroup(string path)
        {
            var byGroup = new System.Collections.Generic.Dictionary<string,
                System.Collections.Generic.Dictionary<string, int>>();
            string current = "";
            try
            {
                if (!File.Exists(path)) return (byGroup, "");

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);

                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    // ① 先看有没有配置组标识（三种写法）
                    string? g = TryReadGroup(line);
                    if (g != null && g != "无")
                    {
                        current = g;
                        continue;
                    }

                    // ② 再看是不是拾取行
                    int i = line.IndexOf("交互或拾取", StringComparison.Ordinal);
                    if (i < 0) continue;

                    int q1 = line.IndexOf('"', i);
                    if (q1 < 0) continue;
                    int q2 = line.IndexOf('"', q1 + 1);
                    if (q2 < 0) continue;

                    string item = line.Substring(q1 + 1, q2 - q1 - 1).Trim();
                    if (item.Length == 0 || item == "无有效结果") continue;

                    string key = string.IsNullOrEmpty(current) ? "（未标注配置组）" : current;
                    if (!byGroup.TryGetValue(key, out var bag))
                    {
                        bag = new System.Collections.Generic.Dictionary<string, int>();
                        byGroup[key] = bag;
                    }
                    if (bag.TryGetValue(item, out int c)) bag[item] = c + 1;
                    else bag[item] = 1;
                }
            }
            catch { }
            return (byGroup, current);
        }

        /// <summary>
        /// 在指定 BGI 安装目录里找"今天"的日志文件。
        /// 用于区分"今天跑过但已退出" 和 "今天还没启动过"。
        /// </summary>
        public static string FindTodayLog(string bgiExePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(bgiExePath)) return "";
                string? dir = Path.GetDirectoryName(bgiExePath);
                if (string.IsNullOrEmpty(dir)) return "";

                string logDir = Path.Combine(dir, "log");
                if (!Directory.Exists(logDir)) return "";

                string today = Path.Combine(logDir,
                    "better-genshin-impact" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                return File.Exists(today) ? today : "";
            }
            catch { return ""; }
        }

        /// <summary>从一行日志里读配置组名（兼容三种写法），没有则返回 null。</summary>
        private static string? TryReadGroup(string line)
        {
            try
            {
                // 配置组="CD采集-全采集"
                int i = line.IndexOf("配置组=\"", StringComparison.Ordinal);
                if (i >= 0)
                {
                    int s = i + 5;
                    int e = line.IndexOf('"', s);
                    if (e > s) return line.Substring(s, e - s).Trim();
                }

                // 配置组: "联机锄地-混合精英"   （任务启动行）
                int j = line.IndexOf("配置组:", StringComparison.Ordinal);
                if (j >= 0)
                {
                    int s = j + 4;
                    int q1 = line.IndexOf('"', s);
                    int q2 = q1 >= 0 ? line.IndexOf('"', q1 + 1) : -1;
                    if (q1 >= 0 && q2 > q1) return line.Substring(q1 + 1, q2 - q1 - 1).Trim();
                }

                // 启用一条龙配置：灭绝提瓦特
                int k = line.IndexOf("启用一条龙配置", StringComparison.Ordinal);
                if (k >= 0)
                {
                    int c = line.IndexOfAny(new[] { '：', ':' }, k);
                    if (c >= 0) return line.Substring(c + 1).Trim();
                }

                // ★ 配置组 "狗粮批发" 加载完成，共3个脚本，开始执行
                //   这种"计划/装载行"也必须识别，否则当前配置组会一直停在
                //   上一个组（实测后果：270 件狗粮被算到"等待至4-00"名下）。
                int j2 = line.IndexOf("配置组 \"", StringComparison.Ordinal);
                if (j2 >= 0)
                {
                    int s2 = j2 + 5;
                    int e2 = line.IndexOf('"', s2);
                    if (e2 > s2) return line.Substring(s2, e2 - s2).Trim();
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 统计整天日志的错误/警告/失败次数。
        ///
        /// ★ 为什么要单独做：状态解析只处理"增量"，无法回溯全天；
        ///   而"每日报错次数"必须覆盖当天全部内容。
        ///   这里流式读文件（不全量进内存），60MB 大约 1 秒，放后台线程。
        /// </summary>
        public static (int errors, int warns, int fails) CountDailyErrors(string path)
        {
            int errors = 0, warns = 0, fails = 0;
            try
            {
                if (!File.Exists(path)) return (0, 0, 0);

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);

                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.IndexOf("[ERR]", StringComparison.Ordinal) >= 0) errors++;
                    else if (line.IndexOf("[WRN]", StringComparison.Ordinal) >= 0) warns++;

                    if (line.IndexOf("执行失败", StringComparison.Ordinal) >= 0
                        || line.IndexOf("未成功完成", StringComparison.Ordinal) >= 0
                        || line.IndexOf("一条龙前置动作未成功", StringComparison.Ordinal) >= 0)
                        fails++;
                }
            }
            catch { }
            return (errors, warns, fails);
        }

        private static int CountLines(StringBuilder sb)
        {
            int n = 0;
            for (int i = 0; i < sb.Length; i++) if (sb[i] == Convert.ToChar(10)) n++;
            return n;
        }
    }

    /// <summary>
    /// 单个 BGI 日志文件的实时跟随器。
    ///
    /// ★ 为什么需要它：原来靠"每 3 秒轮询读尾部"，BGI 写入到界面显示
    ///   最坏要等 3 秒多，用户感觉日志有延迟。
    ///   改用 FileSystemWatcher：文件一有写入就立刻唤醒读取，
    ///   BGI 那边 flush（实测 0.16 秒）后界面几乎同时更新。
    ///
    /// ★ 只读设计（依据 docs/bgi-realtime-log-reader.md）：
    ///   FileShare.ReadWrite | Delete，不独占、不写回，BGI 与助手可同时读。
    /// ★ 每个跟随器只服务一个文件，自己维护字节偏移；
    ///   文件被轮转/删除时重新打开（重新从头部读，避免跳过内容）。
    /// </summary>
    public sealed class LogFollower : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly object _sync = new object();
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private readonly Action<string> _onNewLines;
        private long _offset;
        private string _path;
        private bool _busy;

        /// <summary>
        /// 开始跟随。<paramref name="initialLines"/> 是首次回填的行数（0 = 只跟新增）。
        /// </summary>
        public LogFollower(string path, System.Windows.Threading.Dispatcher dispatcher,
            Action<string> onNewLines, int initialLines = 60, bool deepBackfill = false)
        {
            _path = path;
            _dispatcher = dispatcher;
            _onNewLines = onNewLines;

            // 首次回填。
            // ★ deepBackfill：从尾部往前分段回溯最多 16MB，
            //   用于"状态行埋在几十万行之前"的情况（实测 20261107 日志）。
            //   普通日志面板不需要回溯这么深，保持浅读即可。
            string text;
            if (deepBackfill)
            {
                text = BgiLogWatcher.TailLinesDeep(path, initialLines);
            }
            else
            {
                var r = BgiLogWatcher.TailLines(path, initialLines);
                text = r.text;
            }
            if (!string.IsNullOrEmpty(text)) onNewLines(text);

            try { _offset = new FileInfo(path).Length; } catch { _offset = 0; }

            string dir = Path.GetDirectoryName(path) ?? "";
            _watcher = new FileSystemWatcher(dir)
            {
                Filter = Path.GetFileName(path),
                NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite,
                // 1 秒合并：BGI 写日志很频繁，逐个事件读会打满 CPU
                // 但 1 秒远小于原来 3 秒的轮询间隔
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, __) => Pump();
            _watcher.Created += (_, __) => Pump();

            // 兜底：某些网络盘/异常情况下 watcher 不触发，1 秒补漏
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += (_, __) => Pump();
            timer.Start();
            _fallbackTimer = timer;
        }

        private readonly System.Windows.Threading.DispatcherTimer? _fallbackTimer;

        /// <summary>读取新增字节并回调。</summary>
        private void Pump()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                long len;
                try { len = new FileInfo(_path).Length; } catch { return; }

                // 文件变短 = 被轮转/重建，从头重读
                if (len < _offset)
                {
                    _offset = 0;
                }
                if (len == _offset) return;

                long toRead = len - _offset;
                if (toRead > 256 * 1024) toRead = 256 * 1024;   // 单次上限，避免大爆发卡顿

                string chunk;
                using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(_offset, SeekOrigin.Begin);
                    var buf = new byte[toRead];
                    int got = fs.Read(buf, 0, buf.Length);
                    if (got <= 0) return;
                    _offset += got;
                    chunk = Encoding.UTF8.GetString(buf, 0, got);
                }

                // 丢弃不完整末行（等下次补齐），避免出现半行
                int lastNl = chunk.LastIndexOf('\n');
                if (lastNl < 0) return;                       // 全是残行
                if (lastNl < chunk.Length - 1)
                {
                    // 回退偏移，让残行下次重读
                    _offset -= Encoding.UTF8.GetByteCount(chunk.Substring(lastNl + 1));
                    chunk = chunk.Substring(0, lastNl + 1);
                }

                chunk = chunk.Replace("\r\n", "\n").Replace("\r", "");
                // 去掉空行（BGI 日志每条之间有空行）
                var sb = new StringBuilder();
                foreach (var ln in chunk.Split("\n"))
                    if (ln.Trim().Length > 0) sb.AppendLine(ln);

                string text = sb.ToString();
                if (text.Length == 0) return;
                _dispatcher.BeginInvoke(new Action(() => _onNewLines(text)));
            }
            catch { }
            finally { _busy = false; }
        }

        public void Dispose()
        {
            try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { }
            try { _fallbackTimer?.Stop(); } catch { }
        }
    }
}
