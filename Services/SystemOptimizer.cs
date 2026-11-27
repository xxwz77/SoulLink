using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    public static class SystemOptimizer
    {
        private sealed record ServiceEntry(string Name, string Label, string Impact, bool AllowStop = true);

        private static readonly ServiceEntry[] Services =
        {
            new("SysMain", "内存预取（SysMain）", "用于应用预加载。若持续占用磁盘可暂时停止，应用冷启动可能变慢。"),
            new("DiagTrack", "连接用户体验与诊断", "停止后将暂停该服务收集和传输诊断数据。"),
            new("WSearch", "Windows 搜索索引", "文件、邮件和依赖 Windows 索引的搜索可能变慢或失去索引结果。"),
            new("FontCache", "字体缓存", "用于提升字体加载速度。停止后可能增加应用冷启动开销。"),
            new("BITS", "后台智能传输（BITS）", "Windows 更新和应用使用的后台下载服务。", false),
            new("Spooler", "打印后台（Print Spooler）", "暂停打印，包括使用此服务的 RDP 重定向打印机。"),
            new("MapsBroker", "离线地图管理", "使用已下载地图的应用在停止后无法访问离线地图。"),
            new("Fax", "传真", "停止后暂停 Windows 传真。"),
            new("RetailDemo", "零售演示", "仅用于 Windows 零售演示模式。"),
        };

        private static readonly SemaphoreSlim ChangeGate = new(1, 1);
        private static readonly HashSet<string> StoppedBySoulLink = new(StringComparer.OrdinalIgnoreCase);

        public sealed record ServiceState(string ServiceName, string DisplayName, string Reason, string StateText,
            bool Running, bool CanStop, bool CanRestore);

        public static List<ServiceState> QueryServices()
        {
            var list = new List<ServiceState>(Services.Length);
            foreach (var entry in Services)
            {
                try
                {
                    using var service = new ServiceController(entry.Name);
                    var status = service.Status;
                    bool running = status == ServiceControllerStatus.Running;
                    bool restore;
                    lock (StoppedBySoulLink) restore = StoppedBySoulLink.Contains(entry.Name);
                    string text = status switch
                    {
                        ServiceControllerStatus.Running => "运行中",
                        ServiceControllerStatus.Stopped => "已停止",
                        ServiceControllerStatus.Paused => "已暂停",
                        _ => "状态切换中",
                    };
                    list.Add(new(entry.Name, entry.Label, entry.Impact, text, running,
                        entry.AllowStop && running && service.CanStop, restore && status == ServiceControllerStatus.Stopped));
                }
                catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1060 })
                {
                    list.Add(new(entry.Name, entry.Label, entry.Impact, "未安装", false, false, false));
                }
                catch
                {
                    list.Add(new(entry.Name, entry.Label, entry.Impact, "无法读取状态", false, false, false));
                }
            }
            return list;
        }

        public static Task<(bool ok, string message)> StopServiceAsync(string serviceName) => ChangeServiceAsync(serviceName, true);
        public static Task<(bool ok, string message)> StartServiceAsync(string serviceName) => ChangeServiceAsync(serviceName, false);

        private static async Task<(bool ok, string message)> ChangeServiceAsync(string serviceName, bool stop)
        {
            var entry = Array.Find(Services, item => string.Equals(item.Name, serviceName, StringComparison.OrdinalIgnoreCase));
            if (entry == null || !entry.AllowStop) return (false, "此服务不在可操作名单内。");
            if (!SystemLauncher.IsElevated) return (false, "请以管理员身份运行 SoulLink 后重试。");
            await ChangeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => ChangeService(entry, stop)).ConfigureAwait(false);
            }
            finally { ChangeGate.Release(); }
        }

        private static (bool ok, string message) ChangeService(ServiceEntry entry, bool stop)
        {
            try
            {
                using var service = new ServiceController(entry.Name);
                var status = service.Status;
                if (stop)
                {
                    if (status != ServiceControllerStatus.Running || !service.CanStop) return (false, "当前状态不允许停止，请刷新后重试。");
                    // Let SCM reject the request if an active service depends on this one.
                    service.Stop(stopDependentServices: false);
                    lock (StoppedBySoulLink) StoppedBySoulLink.Add(entry.Name);
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    return (true, "已停止 " + entry.Label);
                }

                lock (StoppedBySoulLink)
                    if (!StoppedBySoulLink.Contains(entry.Name)) return (false, "此服务未由本次 SoulLink 停止。");
                if (status == ServiceControllerStatus.Stopped) service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                lock (StoppedBySoulLink) StoppedBySoulLink.Remove(entry.Name);
                return (true, "已恢复 " + entry.Label);
            }
            catch (Exception ex) { return (false, ex.InnerException?.Message ?? ex.Message); }
        }

        /// <summary>读取当前 Defender 的目录排除列表。</summary>
        public static (bool ok, string message, List<string> paths) GetDefenderExclusions()
        {
            // Get-MpPreference 只在 Defender 是活动杀软时可用；
            // 装了第三方杀软（火绒/360 等）时该 cmdlet 可能报错，属于正常情况。
            var (ok, output) = RunPowerShellSync(
                "try { $e=(Get-MpPreference -ErrorAction Stop).ExclusionPath; if($e){$e -join [char]10} ; exit 0 } catch { Write-Error $_; exit 1 }",
                30000);

            var paths = new List<string>();
            if (!ok) return (false, output.Length == 0 ? "无法读取 Defender 排除项（可能未启用 Defender 或装了第三方杀软）" : Shorten(output), paths);

            foreach (var raw in (output ?? "").Split('\n'))
            {
                string p = raw.Trim().TrimEnd('\r');
                if (p.Length > 0) paths.Add(p);
            }
            return (true, "", paths);
        }

        /// <summary>把一个目录加入 Defender 排除。需要管理员权限。</summary>
        public static (bool ok, string message) AddDefenderExclusion(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) return (false, "目录不能为空。");
            if (!Directory.Exists(path) || Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(Path.GetFullPath(path)) ?? ""))
                return (false, "请选择有效的数据子目录，整盘目录不能加入排除。");
            if (!SystemLauncher.IsElevated)
                return (false, "需要管理员权限。请以管理员身份运行 SoulLink 后再添加排除。");

            string esc = path.Replace("'", "''");
            var (ok, output) = RunPowerShellSync(
                "try { Add-MpPreference -ExclusionPath '" + esc + "' -ErrorAction Stop; exit 0 } catch { Write-Error $_; exit 1 }",
                30000);
            return ok
                ? (true, "已加入 Defender 排除：" + path)
                : (false, "加入排除失败：" + Shorten(output));
        }

        /// <summary>把一个目录从 Defender 排除中移除。需要管理员权限。</summary>
        public static (bool ok, string message) RemoveDefenderExclusion(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) return (false, "目录不能为空。");
            if (!SystemLauncher.IsElevated)
                return (false, "需要管理员权限。请以管理员身份运行 SoulLink 后再移除排除。");

            string esc = path.Replace("'", "''");
            var (ok, output) = RunPowerShellSync(
                "try { Remove-MpPreference -ExclusionPath '" + esc + "' -ErrorAction Stop; exit 0 } catch { Write-Error $_; exit 1 }",
                30000);
            return ok
                ? (true, "已移除 Defender 排除：" + path)
                : (false, "移除排除失败：" + Shorten(output));
        }

        private static (bool ok, string output) RunPowerShellSync(string script, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-EncodedCommand");
                psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
                    "$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script)));
                using var process = Process.Start(psi);
                if (process == null) return (false, "PowerShell 启动失败");
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    return (false, "PowerShell 超时");
                }
                return (process.ExitCode == 0, (output.GetAwaiter().GetResult() + "\n" + error.GetAwaiter().GetResult()).Trim());
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "（无输出）";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 240 ? s[..240] + "…" : s;
        }
    }
}
