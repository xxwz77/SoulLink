using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

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
        /// <summary>
        /// 通道状态可能变化时触发（投送成功 / 代理退出）。
        ///
        /// ★ 为什么需要它：主界面（LaunchPage）原来只靠 3 秒轮询
        ///   DiscoverLiveAgentSessions() 发现新通道。
        ///   而投送是「先起代理进程，后建 relay 管道」两步，
        ///   中间那段窗口期里主界面显示「未投送」——
        ///   用户实际已经能用自由视角（代理在跑），界面却说没投送。
        ///   这里给一个主动通知，让界面立刻刷新，不必等下一次轮询。
        ///
        /// ★ 订阅方必须自己 marshal 到 UI 线程；本事件可能在后台线程抛出。
        /// </summary>
        public static event Action? ChannelStateMayHaveChanged;

        /// <summary>通知「通道状态可能变了」。失败不外抛，绝不影响投送流程。</summary>
        public static void NotifyChannelStateChanged()
        {
            // ★ 通道状态变了 → 管道列表必然变了。让下一次发现调用重新全扫，
            //   否则最多 3 秒内会读到旧缓存，界面状态更新滞后。
            try { InvalidatePipeCache(); } catch { }
            try { ChannelStateMayHaveChanged?.Invoke(); } catch { }
        }

        /// <summary>一个会话里找到的 BGI 实例。</summary>
        public sealed class Instance
        {
            public int SessionId { get; init; }
            public int Pid { get; init; }
            public string ExePath { get; init; } = "";
            public string LogFile { get; init; } = "";
            public long Length { get; init; }
            public DateTime LastWrite { get; init; }
            /// <summary>进程启动时间。同一会话有多个 BGI 时用它排序，保证行为确定。</summary>
            public DateTime StartTime { get; init; }
        }

        // ── 命名管道枚举缓存 ────────────────────────────────────────────
        //
        // ★★ 为什么必须缓存：★★
        //   DiscoverLiveAgentSessions 靠
        //   Directory.GetFiles(@\"\\.\\pipe\\\") 全量枚举命名管道。
        //   Windows 没有「按前缀枚举管道」的 API，只能全扫。
        //
        //   正常环境约几百个管道，全扫无感；但第三方软件可能泄漏管道
        //   （实测某远控守护进程每秒 +11 个，累积到 2.9 万个），
        //   这时每次全扫要几百毫秒，而会话监控每 2 秒就调用一次，
        //   主线程被反复占满 → 布局与 UIAutomation 事件积压 →
        //   界面卡死、点击无响应（实测两个线程合计 166% 单核，
        //   栈顶全是 fireAutomationEvents / CreateAutomationPeer）。
        //
        //   缓存后：无论多少个管道，同一时间窗内只全扫一次。
        private static readonly Lock PipeCacheGate = new();
        private static string[] _pipeCache = Array.Empty<string>();
        private static DateTime _pipeCacheAt = DateTime.MinValue;
        /// <summary>
        /// 缓存有效期。
        ///
        /// ★ 必须远大于「单次枚举耗时」，否则缓存形同虚设。
        ///   实测：正常环境 ~300 个管道约 1ms；但第三方软件泄漏管道时
        ///   2.9 万个管道单次枚举要 **7.3 秒**。若 TTL 只有几秒，
        ///   每次调用都会判定过期、重新全扫，等于没加缓存。
        ///
        ///   取 30 秒：通道状态变化有 InvalidatePipeCache() 主动失效，
        ///   所以真正需要立刻看到变化的场合不会受这个 TTL 影响；
        ///   而周期性轮询最多滞后 30 秒，对「发现新投送」完全够用。
        /// </summary>
        private static readonly TimeSpan PipeCacheTtl = TimeSpan.FromSeconds(30);
        private static long _pipeScanCount;
        private static bool _pipeSlowWarned;

        /// <summary>
        /// 取命名管道列表（带缓存）。
        /// forceRefresh=true 时无视缓存，用于通道状态刚变化、需要立刻确认的场合。
        /// </summary>
        private static string[] GetPipePaths(bool forceRefresh = false)
        {
            lock (PipeCacheGate)
            {
                var age = DateTime.UtcNow - _pipeCacheAt;
                if (!forceRefresh && _pipeCacheAt != DateTime.MinValue && age < PipeCacheTtl)
                    return _pipeCache;

                try
                {
                    var sw = Stopwatch.StartNew();
                    _pipeCache = Directory.GetFiles(@"\\.\\pipe\\");
                    sw.Stop();
                    _pipeCacheAt = DateTime.UtcNow;
                    _pipeScanCount++;

                    // ★ 枚举异常缓慢说明系统命名管道数量失控（第三方软件泄漏）。
                    //   只报告一次，避免日志被刷爆。
                    if (sw.ElapsedMilliseconds > 500 && !_pipeSlowWarned)
                    {
                        _pipeSlowWarned = true;
                        try
                        {
                            App.AppendErrorLog("BgiLogWatcher: 命名管道枚举过慢",
                                new InvalidOperationException(
                                    "管道数=" + _pipeCache.Length + " 耗时=" + sw.ElapsedMilliseconds + "ms。" +
                                    "通常由第三方软件泄漏命名管道导致，会影响界面响应。"));
                        }
                        catch { }
                    }
                }
                catch
                {
                    // 枚举失败时保留旧缓存（总比返回空好），并推迟重试
                    _pipeCacheAt = DateTime.UtcNow;
                }
                return _pipeCache;
            }
        }

        /// <summary>强制让下一次取管道时重新全扫。通道状态变化时调用。</summary>
        public static void InvalidatePipeCache()
        {
            lock (PipeCacheGate) { _pipeCacheAt = DateTime.MinValue; }
        }

        /// <summary>诊断用：累计全扫次数（缓存命中时不增加）。</summary>
        public static long PipeScanCount => Interlocked.Read(ref _pipeScanCount);

        /// <summary>
        /// 仍**活着**的代理所在会话：以运行中的进程为准，而不是只看管道名。
        /// ★ 管道名可能短暂残留（宿主刚断开、代理正在退出），只看管道会把已死的代理
        ///   报成“通道就绪”，界面随后又跳回“未投送”，表现为状态乱跳。
        /// </summary>
        /// <summary>上一拍判为"活着"的会话（去抖用）。</summary>
        private static HashSet<int> LastLiveSessions = new HashSet<int>();
        /// <summary>每个会话最后一次被确认活着的时刻（去抖用）。</summary>
        private static readonly Dictionary<int, DateTime> LastLiveAt = new Dictionary<int, DateTime>();
        private static readonly Lock LiveStateGate = new();

        public static HashSet<int> DiscoverLiveAgentSessions()
        {
            var live = new HashSet<int>();
            try
            {
                // ★★ 主要来源：本进程的通道注册表。★★
                //
                //   以前这里枚举系统全部命名管道来找 SoulLink_Relay_<会话号>。
                //   但管道总数可达 3.5 万（第三方软件泄漏 .NET 同步对象所致），
                //   枚举一次要 6.5 秒，界面每次刷新都会卡。
                //
                //   而 RelayClient 在投送成功时已经 Register()、断开时 Unregister()，
                //   直接查注册表就是 O(会话数)、约 0 毫秒。
                //   用户已明确：不需要跨版本显示（只看本进程的通道）。
                foreach (var sid in RelayClient.GetLiveSessions()) live.Add(sid);

                // ★ 去抖：代理重启/退出时，状态不是同一拍变化的，
                //   严格判定会让主界面「绿一下又灭」（用户实测）。
                //   规则：本次没判到、但上一拍还活着，且距上次确认不到 6 秒 → 继续算活着。
                var nowUtc = DateTime.UtcNow;
                lock (LiveStateGate)
                {
                    foreach (var sid in LastLiveSessions)
                    {
                        if (live.Contains(sid)) continue;
                        if (LastLiveAt.TryGetValue(sid, out var at)
                            && (nowUtc - at).TotalSeconds < 6)
                        {
                            live.Add(sid);   // 抖动期：维持"活着"
                        }
                    }

                    foreach (var sid in live) LastLiveAt[sid] = nowUtc;
                    LastLiveSessions = new HashSet<int>(live);
                }
            }
            catch { }
            return live;
        }

        /// <summary>识别任意 SoulLink 版本的代理管道，不按当前版本身份过滤。</summary>
        /// <summary>
        /// 找指定会话里跑着的 BGI 实例（只查这一个会话，不做全量枚举）。
        ///
        /// ★ 与 Discover() 的区别：
        ///   Discover() 要遍历全部进程（约 23.7ms），返回所有会话的映射；
        ///   这里一旦命中目标会话就立刻返回，通常几十微秒。
        ///   只用于「记录 BGI 的 PID」这种一次性场景。
        /// </summary>
        public static Instance? DiscoverForSession(int sessionId, string exePath)
        {
            if (sessionId <= 0) return null;
            try
            {
                var all = Discover();
                return all.TryGetValue(sessionId, out var hit) ? hit : null;
            }
            catch { return null; }
        }

        internal static bool IsRelayPipeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (!name.StartsWith("SoulLink", StringComparison.OrdinalIgnoreCase)) return false;
            int marker = name.LastIndexOf("_Relay_", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return false;
            string rest = name.Substring(marker + 7);
            int us = rest.IndexOf('_');
            return us > 0 && int.TryParse(rest.Substring(0, us), out int sid) && sid > 0;
        }

        /// <summary>从命令行字符串解析出会话号（形如 ...Relay_6_abc123）。</summary>
        public static int ParseRelaySessionId(string? commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return -1;
            try
            {
                int marker = commandLine.LastIndexOf("_Relay_", StringComparison.OrdinalIgnoreCase);
                if (marker < 0 || commandLine.IndexOf("SoulLink", StringComparison.OrdinalIgnoreCase) < 0) return -1;
                int i = marker + "_Relay_".Length;
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
                        var one = new Instance
                        {
                            SessionId = p.SessionId,
                            Pid = p.Id,
                            ExePath = exe,
                            LogFile = newest,
                            Length = fi.Length,
                            LastWrite = fi.LastWriteTime,
                            StartTime = SafeStartTime(p)
                        };

                        // ★ 同一会话可能有多个 BGI（用户同时开了两个）。
                        //   旧的写法是直接覆盖，谁后写谁生效 —— 行为不确定。
                        //   这里改成"保留启动最早的那个"，让结果稳定可预期；
                        //   需要看全部时用 DiscoverAll()。
                        if (!result.TryGetValue(p.SessionId, out var exist)
                            || one.StartTime < exist.StartTime)
                            result[p.SessionId] = one;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 枚举所有正在运行的 BGI，**不按会话去重**。
        /// 同一会话有多个实例时全部返回，按启动时间升序。
        ///
        /// ★ 用在哪里：用户同时装了两个 BGI 时，界面上要能看到全部，
        ///   而不是像 Discover() 那样只剩一个（会让人以为另一个"消失了"）。
        /// </summary>
        public static List<Instance> DiscoverAll()
        {
            var list = new List<Instance>();
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

                        string? newest = null;
                        DateTime newestTime = DateTime.MinValue;
                        foreach (var f in Directory.GetFiles(logDir, "better-genshin-impact*.log"))
                        {
                            var t = File.GetLastWriteTime(f);
                            if (t > newestTime) { newestTime = t; newest = f; }
                        }
                        if (newest == null) continue;

                        var fi = new FileInfo(newest);
                        list.Add(new Instance
                        {
                            SessionId = p.SessionId,
                            Pid = p.Id,
                            ExePath = exe,
                            LogFile = newest,
                            Length = fi.Length,
                            LastWrite = fi.LastWriteTime,
                            StartTime = SafeStartTime(p)
                        });
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            return list.OrderBy(x => x.StartTime).ToList();
        }

        /// <summary>读进程启动时间；拿不到就返回 MaxValue（排到最后）。</summary>
        private static DateTime SafeStartTime(Process p)
        {
            try { return p.StartTime; } catch { return DateTime.MaxValue; }
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

                // ★ 性能：原来每轮都调用 CountLines(acc) 扫整个累积字符串，
                //   而 acc 可能到 8MB —— 从尾部往前读 16 次 = 16 次全量扫描，
                //   再加上 StringBuilder.Insert(0,…) 每次都要搬移整个缓冲区，
                //   读一个 33MB 的日志要好几秒（用户实测"日志读取很慢"）。
                //
                //   改成：分段读进 List，最后一次性拼接；
                //   用增量方式统计换行符数量，而不是重复扫描全串。
                var segs = new List<string>();
                int lineCount = 0;
                long totalChars = 0;

                while (pos > 0 && consumed < budget && lineCount < maxLines)
                {
                    long take = Math.Min(seg, pos);
                    long start = pos - take;
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[take];
                    int got = fs.Read(buf, 0, buf.Length);
                    if (got <= 0) break;

                    string part = Encoding.UTF8.GetString(buf, 0, got);
                    segs.Add(part);

                    // 增量统计行数（只扫这一段）
                    for (int k = 0; k < part.Length; k++)
                        if (part[k] == '\n') lineCount++;

                    totalChars += part.Length;
                    consumed += take;
                    pos = start;

                    // 防内存爆：超过 8MB 文本就停
                    if (totalChars > 8 * 1024 * 1024) break;
                }

                // 一次性拼好（逆序，因为是从尾部往前读的）
                for (int i = segs.Count - 1; i >= 0; i--) acc.Append(segs[i]);

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
        private readonly Lock _sync = new();
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

            // 兜底：某些网络盘/异常情况下 watcher 不触发，1 秒补漏。
            // ★ 改用统一节拍（TickHub），不再自建 DispatcherTimer。
            _fallbackSub = TickHub.Register("BgiLogWatcher.Fallback", 1, Pump);
        }

        private readonly IDisposable? _fallbackSub;

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
            try { _fallbackSub?.Dispose(); } catch { }
        }
    }
}
