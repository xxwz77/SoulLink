// ============================================================================
// sl-watchdog —— SoulLink 主程序外置假死看门狗（2026-11-24）
//
// 来源：BENCH-YOMORI-02-AGENT-ARCH.md §3.1③（对标 Yomori.Watchdog）：
//   进程内看门狗在「全部线程一起卡死」时自己也被调度饿死（对方三次漏报实证）；
//   必须独立进程。探测判据沿用对方踩坑固化的三态（不看 GetLastError / IsHungAppWindow）。
//
// 用法：
//   sl-watchdog.exe              常驻：监视 SoulLink 主程序，假死→日志+线程快照+MiniDump
//   sl-watchdog.exe --once       单次探测并打印结果（自检）
//   sl-watchdog.exe --pid <pid>  只监视指定 PID（调试）
//
// 行为边界（与 Yomori 一致——只观测不动进程）：
//   发现假死：写日志 + NtQuerySystemInformation 线程快照 + 假死持续>90s 抓一次全内存 dump
//             （cooldown 10min，防磁盘写满）。**绝不杀/重启主程序**（救援交给 sl-rescue 人工决策）。
//   日志：%ProgramData%SoulLinkwatchdogwatchdog.log（5MB 截断保留末尾）——
//             不写 exe 同级：安装目录标准用户无写权限（Yomori 实测踩坑，静默失败一行不留）。
// ============================================================================
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SoulLink.Watchdog;

public static class Program
{
    // ── 找主程序窗口 ──────────────────────────────────────────────────
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    // ── 假死探测（Yomori 固化判据）───────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);

    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;

    // ── MiniDump ─────────────────────────────────────────────────────
    private const uint MiniDumpWithFullMemory = 0x00000002;
    private const uint ProcessAccessForDump = 0x0410;   // QUERY_INFORMATION | VM_READ

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, IntPtr hFile, uint dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    // ── 线程快照（一次系统调用拿全部线程状态）────────────────────────
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int cls, IntPtr buffer, int length, out int returnLength);
    private const int SystemProcessInformation = 5;
    private const int ThreadArrayOffset = 256;   // x64 进程块内线程数组起始偏移
    private const int ThreadEntrySize = 80;      // x64 SYSTEM_THREAD_INFORMATION 大小

    // ── 参数（与 Yomori 同量级）──────────────────────────────────────
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(3);   // > 一次正常 UI 卡顿
    private const int ProbeTimeoutMs = 3000;                                    // UI 健康时 WM_NULL 微秒级
    private const int FailuresBeforeReport = 2;                                 // 连续 2 次才判定（防抖）
    private static readonly TimeSpan RereportInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DumpAfter = TimeSpan.FromSeconds(90);      // 假死持续 90s 才抓 dump
    private static readonly TimeSpan DumpCooldown = TimeSpan.FromMinutes(10);   // 两次 dump 最小间隔
    private const string TargetProcessName = "SoulLink";                        // 不含 .exe
    private const string StartupIndicator = "SoulLink.exe";                     // 排除 Updater/Agent/Watchdog 自身

    // ── 数据目录（ProgramData，标准用户可写）──────────────────────────
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SoulLink", "watchdog");
    private const string WatchdogLogName = "watchdog.log";
    private const string DumpDirName = "crashdumps";
    private const long MaxLogBytes = 5 * 1024 * 1024;

    private static readonly object Gate = new();
    private static StreamWriter _log;   // null = 未打开（disable nullable 下用 null 初始化）

    private static int _consecutiveFailures;
    private static bool _reported;
    private static DateTime _hangStartedAt;
    private static DateTime _lastReportAt;
    private static DateTime _lastDumpAt = DateTime.MinValue;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
        int? forcedPid = ReadIntArg(args, "--pid");

        try
        {
            return once ? RunOnce(forcedPid) : RunLoop(forcedPid);
        }
        catch (Exception ex)
        {
            // 看门狗自己绝不能无声无息死掉
            try { Log("看门狗异常退出：" + ex); } catch { }
            Console.Error.WriteLine("watchdog crashed: " + ex);
            return 1;
        }
        finally
        {
            try { _log?.Dispose(); } catch { }
        }
    }

    private static int RunOnce(int? forcedPid)
    {
        var (pid, hwnd) = FindTarget(forcedPid);
        if (pid == 0)
        {
            Console.WriteLine("[watchdog] 目标进程未找到（SoulLink.exe 不在运行）。");
            return 0;
        }
        var probe = Probe(hwnd);
        Console.WriteLine($"[watchdog] 目标 pid={pid} hwnd=0x{hwnd:X} => {probe}");
        return probe switch
        {
            ProbeResult.Hung => 2,     // 退出码语义：2=假死（脚本可判）
            ProbeResult.Alive => 0,
            _ => 0,
        };
    }

    private static int RunLoop(int? forcedPid)
    {
        FreeConsole();   // 常驻无人值守，别闪黑窗（开机自启场景）
        EnsureDataDirs();

        int? trackedPid = forcedPid;
        Log($"sl-watchdog 启动（常驻）。目标={TargetProcessName} 探测间隔={ProbeInterval.TotalSeconds:0}s 超时={ProbeTimeoutMs}ms 防抖={FailuresBeforeReport} dump={DumpAfter.TotalSeconds:0}s/{DumpCooldown.TotalMinutes:0}min");

        while (true)
        {
            Thread.Sleep(ProbeInterval);

            try
            {
                var (pid, hwnd) = FindTarget(trackedPid);

                // 目标没了：跟随新实例或空转
                if (pid == 0)
                {
                    if (trackedPid.HasValue)
                    {
                        Log($"--pid {trackedPid} 进程已退出，退出跟随模式。");
                        return 0;
                    }
                    ResetHangState();
                    continue;
                }

                if (trackedPid == null || trackedPid.Value != pid)
                {
                    if (_reported || _consecutiveFailures > 0)
                        Log("目标切换（旧目标已恢复或退出），新 pid=" + pid);
                    ResetHangState();
                    trackedPid = pid;
                }

                var result = Probe(hwnd);

                switch (result)
                {
                    case ProbeResult.Alive:
                        if (_reported)
                        {
                            var hungFor = DateTime.UtcNow - _hangStartedAt;
                            Log($"假死恢复。持续 {hungFor.TotalSeconds:0.0}s（pid={pid}）。");
                        }
                        ResetHangState();
                        break;

                    case ProbeResult.Hung:
                        _consecutiveFailures++;
                        if (!_reported && _consecutiveFailures >= FailuresBeforeReport)
                        {
                            _reported = true;
                            _hangStartedAt = DateTime.UtcNow;
                            _lastReportAt = DateTime.UtcNow;
                            Log($"★ 假死判定：pid={pid} 连续 {_consecutiveFailures} 次无响应（{(ProbeTimeoutMs / 1000.0):0.0}s 超时探测）。");
                            var threads = CaptureThreadSnapshot(pid);
                            if (threads != null) Log(threads);
                        }
                        else if (_reported && DateTime.UtcNow - _lastReportAt >= RereportInterval)
                        {
                            _lastReportAt = DateTime.UtcNow;
                            Log($"假死持续 {(DateTime.UtcNow - _hangStartedAt).TotalSeconds:0.0}s（pid={pid}）。");
                        }

                        if (_reported && DateTime.UtcNow - _hangStartedAt >= DumpAfter
                            && DateTime.UtcNow - _lastDumpAt >= DumpCooldown)
                        {
                            var dumpPath = TryTakeDump(pid);
                            if (dumpPath != null)
                            {
                                _lastDumpAt = DateTime.UtcNow;
                                Log($"已抓取转储：{dumpPath}");
                            }
                        }
                        break;

                    case ProbeResult.WindowGone:
                        // 正在关，不报
                        ResetHangState();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log("探测循环异常（继续）：" + ex.Message);
            }
        }
    }

    private static void ResetHangState()
    {
        _consecutiveFailures = 0;
        _reported = false;
    }

    // ── 探测核心：Yomori 固化三态（不看 GetLastError/IsHungAppWindow）──
    private enum ProbeResult { Alive, Hung, WindowGone }

    private static ProbeResult Probe(IntPtr hwnd)
    {
        IntPtr result;
        var rc = SendMessageTimeout(hwnd, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, (uint)ProbeTimeoutMs, out result);
        if (rc != 0) return ProbeResult.Alive;
        return IsWindow(hwnd) ? ProbeResult.Hung : ProbeResult.WindowGone;
    }

    // ── 目标发现：主程序主窗口（可见 + 属于 SoulLink.exe 进程，排除 Agent/Updater/Watchdog 自身）──
    private static (int pid, IntPtr hwnd) FindTarget(int? forcedPid)
    {
        if (forcedPid.HasValue)
        {
            var hwnd = FindMainWindow(forcedPid.Value);
            return (forcedPid.Value, hwnd);
        }

        int bestPid = 0;
        IntPtr bestHwnd = IntPtr.Zero;
        long bestStamp = 0;

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return true;   // 别监视自己

            try
            {
                var proc = Process.GetProcessById((int)pid);
                // 只认主程序：进程名 SoulLink（不是 SoulLink.Agent / SoulLink.Updater / sl-watchdog）
                if (!string.Equals(proc.ProcessName, TargetProcessName, StringComparison.OrdinalIgnoreCase))
                    return true;
                // 主窗口 = 有标题的最顶层可见窗
                var stamp = h.ToInt64();
                if (proc.MainWindowTitle.Length > 0 && proc.MainWindowHandle == h)
                {
                    bestPid = (int)pid;
                    bestHwnd = h;
                    return false;   // 找到了
                }
                if (bestHwnd == IntPtr.Zero && stamp > bestStamp)
                {
                    bestStamp = stamp;
                    bestPid = (int)pid;
                    bestHwnd = h;
                }
            }
            catch
            {
                // 进程刚退出等竞态：跳过这个窗口
            }
            return true;
        }, IntPtr.Zero);

        return (bestPid, bestHwnd);
    }

    private static IntPtr FindMainWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p == (uint)pid && IsWindowVisible(h))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ── 线程快照：NtQuerySystemInformation 一次调用（Yomori 同款）────────
    private static string CaptureThreadSnapshot(int pid)
    {
        try
        {
            var buffer = Marshal.AllocHGlobal(1 << 22);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, 1 << 22, out _);
                if (status != 0) return null;

                var offset = 0;
                while (offset < 1 << 22)
                {
                    var block = buffer + offset;
                    var nextSize = Marshal.ReadInt32(block, 0);
                    var blockPid = Marshal.ReadInt32(block, 64 + 8);   // SYSTEM_PROCESS_INFORMATION.ImageName 后
                    if (blockPid == pid)
                    {
                        var threadCount = Marshal.ReadInt32(block, 64 + 4 + 4 + 8 + 8 + 8 + 8 + 4 + 4 + 4 + 4 + 4 + 4 + 8 + 4 + 8 + 8 + 8 + 8 + 4 + 4);
                        var sb = new StringBuilder();
                        sb.AppendLine($"线程快照（pid={pid}，约 {threadCount} 线程）：");
                        var t = block + ThreadArrayOffset;
                        for (int i = 0; i < Math.Min(threadCount, 128); i++)
                        {
                            var kernelMs = Marshal.ReadInt64(t, 8);
                            var userMs = Marshal.ReadInt64(t, 16);
                            var state = Marshal.ReadByte(t, 40);
                            var waitReason = Marshal.ReadInt32(t, 44);
                            sb.AppendLine($"  tid? k={kernelMs}ms u={userMs}ms state={state} wait={waitReason}");
                            t += ThreadEntrySize;
                        }
                        return sb.ToString();
                    }
                    if (nextSize == 0) break;
                    offset += nextSize;
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    // ── MiniDump（假死持续超阈值，留可分析的全内存栈）─────────────────
    private static string TryTakeDump(int pid)
    {
        var dir = Path.Combine(DataDir, DumpDirName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"SoulLink-{pid}-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
        var handle = OpenProcess(ProcessAccessForDump, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var ok = MiniDumpWriteDump(handle, (uint)pid, fs.SafeFileHandle.DangerousGetHandle(),
                MiniDumpWithFullMemory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (!ok)
            {
                try { File.Delete(path); } catch { }
                return null;
            }
            return path;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ── 日志（ProgramData + 5MB 截断）─────────────────────────────────
    private static void EnsureDataDirs()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, DumpDirName));
    }

    private static void Log(string message)
    {
        lock (Gate)
        {
            try
            {
                var path = Path.Combine(DataDir, WatchdogLogName);
                if (_log == null)
                {
                    TruncateIfNeeded(path);
                    _log = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true };
                }
                _log.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
            }
            catch
            {
                // 日志失败不致命：探测照跑
            }
        }
    }

    private static void TruncateIfNeeded(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (info.Length <= MaxLogBytes) return;
            // 截断保留末尾一半
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(-MaxLogBytes / 2, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var tail = reader.ReadToEnd();
            File.WriteAllText(path, tail, new UTF8Encoding(false));
        }
        catch
        {
            // 截断失败就任由它继续长
        }
    }

    private static int? ReadIntArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var v))
                return v;
        }
        return null;
    }
}
