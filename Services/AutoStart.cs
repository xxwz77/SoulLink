using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 开机自启（限定用户 + 最高权限 + 不弹 UAC）。
    ///
    /// ★ 为什么用计划任务而不是注册表 Run 项：
    ///   本程序清单要求 requireAdministrator，Run 项启动时会弹 UAC，
    ///   用户每次开机都得点"是"。计划任务可设 RunLevel=Highest 直接提权。
    ///
    /// ★ 为什么用 XML 而不是 schtasks 命令行：
    ///   schtasks 的 /sc onlogon 不支持"指定用户 + 最高权限"的组合。
    /// </summary>
    public static class AutoStart
    {
        /// <summary>任务名按身份区分，两个版本各自独立。</summary>
        public static string TaskName => AppIdentity.Name + "AutoStart";

        /// <summary>当前登录用户（默认的自启账户）。</summary>
        public static string CurrentUser => Environment.UserName;

        public static bool IsEnabled()
        {
            try
            {
                var (ok, _) = Run("schtasks.exe", "/query /tn " + Q(TaskName), 8000);
                return ok;
            }
            catch { return false; }
        }

        /// <summary>查询任务绑定的用户（用于界面回显）。</summary>
        public static string? GetBoundUser()
        {
            try
            {
                var (ok, output) = Run("schtasks.exe",
                    "/query /tn " + Q(TaskName) + " /fo list /v", 10000);
                if (!ok) return null;
                foreach (var line in output.Replace("\r", "").Split('\n'))
                {
                    string t = line.Trim();
                    // 中英文系统的字段名
                    if (t.StartsWith("Run As User:", StringComparison.OrdinalIgnoreCase)
                        || t.StartsWith("运行用户:", StringComparison.Ordinal)
                        || t.StartsWith("运行方式用户:", StringComparison.Ordinal))
                    {
                        int i = t.IndexOf(':');
                        if (i >= 0) return t.Substring(i + 1).Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 为指定 Windows 用户创建开机自启任务。
        /// 该用户登录时启动，不弹 UAC。
        /// </summary>
        public static async Task<(bool ok, string message)> EnableAsync(string userName, string exePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(userName)) userName = CurrentUser;
                if (!File.Exists(exePath))
                    return (false, "找不到程序本体：" + exePath);

                await Task.Run(() => Run("schtasks.exe", "/delete /tn " + Q(TaskName) + " /f", 10000));

                string xml = BuildXml(userName, exePath);
                string tmp = Path.Combine(Path.GetTempPath(), AppIdentity.Name + "-autostart.xml");
                // 计划任务 XML 必须 UTF-16 LE
                File.WriteAllText(tmp, xml, new UnicodeEncoding(false, true));

                var (ok, msg) = await Task.Run(() =>
                    Run("schtasks.exe", "/create /tn " + Q(TaskName) + " /xml " + Q(tmp) + " /f", 30000));
                try { File.Delete(tmp); } catch { }

                if (!ok)
                    return (false, "创建自启任务失败：" + Shorten(msg));

                var (exists, _) = await Task.Run(() =>
                    Run("schtasks.exe", "/query /tn " + Q(TaskName), 8000));
                if (!exists)
                    return (false, "任务创建后查询不到，可能被杀软拦截");

                return (true, "已设置开机自启：「" + userName + "」登录时启动（不弹 UAC）");
            }
            catch (Exception ex) { return (false, "设置自启失败：" + ex.Message); }
        }

        public static async Task<(bool ok, string message)> DisableAsync()
        {
            try
            {
                if (!IsEnabled()) return (true, "自启已经是关闭状态");
                var (ok, msg) = await Task.Run(() =>
                    Run("schtasks.exe", "/delete /tn " + Q(TaskName) + " /f", 15000));
                return ok ? (true, "已关闭开机自启") : (false, "关闭自启失败：" + Shorten(msg));
            }
            catch (Exception ex) { return (false, "关闭自启失败：" + ex.Message); }
        }

        private static string BuildXml(string userName, string exePath)
        {
            string domain = Environment.UserDomainName;
            string fullUser = userName.Contains("\\") || userName.Contains("@")
                ? userName
                : domain + "\\" + userName;

            string workDir = Path.GetDirectoryName(exePath) ?? "";

            return @"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>SoulLink 开机自启（用户 " + XmlEscape(userName) + @" 登录时）</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>" + XmlEscape(fullUser) + @"</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>" + XmlEscape(fullUser) + @"</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>" + XmlEscape(exePath) + @"</Command>
      <WorkingDirectory>" + XmlEscape(workDir) + @"</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        }

        private static string XmlEscape(string s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                     .Replace("\"", "&quot;").Replace("'", "&apos;");

        private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";

        private static string Shorten(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "（无输出）";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 200 ? s : s.Substring(0, 200) + "…";
        }

        private static (bool ok, string output) Run(string file, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = SystemLauncher.ConsoleEncoding(),
                    StandardErrorEncoding = SystemLauncher.ConsoleEncoding()
                };
                using var p = Process.Start(psi);
                if (p == null) return (false, "无法启动进程");
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit(timeoutMs);
                return (p.ExitCode == 0, (o + " " + e).Trim());
            }
            catch (Exception ex) { return (false, ex.Message); }
        }
    }
}
