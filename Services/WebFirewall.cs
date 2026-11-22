using System;
using System.Diagnostics;
using System.Text;

namespace SoulLink.Services
{
    /// <summary>
    /// 网页直连端口的防火墙放行。
    ///
    /// ★ 用户要求（2026-11-22）：「加 = 别的机器能用」
    ///
    ///   主程序自己开端口（10800）已能访问，但账号直连端口（10801、10802…）
    ///   是**代理进程**开的。用户机器上防火墙开着时，局域网里的手机/平板
    ///   连不上这些端口 —— 表现为"能打开页面但画面全是裂的"。
    ///
    /// ★ 做法：一次性放行一个**端口范围**，而不是每个账号一条规则。
    ///   账号数会变，规则不该跟着变。范围上限 16 个账号够用。
    ///
    /// ★ 只在启用网页直连时调用；删除规则用同样的名字，便于用户自己清理。
    /// </summary>
    internal static class WebFirewall
    {
        /// <summary>规则名前缀。删除时按这个前缀匹配。</summary>
        private const string RuleName = "SoulLink Web Direct";

        /// <summary>范围最多覆盖多少个端口（= 最多几个账号可直连）。</summary>
        private const int RangeSpan = 16;

        /// <summary>
        /// 确保放行 [basePort+1, basePort+RangeSpan] 范围。
        /// 返回 true 表示规则已就绪（或本来就允许，无需规则）。
        /// </summary>
        internal static bool EnsureAllowed(int basePort)
        {
            if (basePort <= 0) return false;
            try
            {
                // 防火墙全关时不需要规则（本机实测三个 profile 都是关的）
                if (!AnyProfileEnabled()) return true;

                int from = basePort + 1;
                int to = Math.Min(65535, basePort + RangeSpan);

                string args = "advfirewall firewall add rule " +
                              "name=\"" + RuleName + "\" " +
                              "dir=in action=allow protocol=TCP " +
                              "localport=" + from + "-" + to;

                var (ok, _) = Run("netsh.exe", args);
                return ok;
            }
            catch { return false; }
        }

        /// <summary>移除规则（用户关掉网页访问时调用，别留垃圾规则）。</summary>
        internal static void Remove()
        {
            try
            {
                if (!AnyProfileEnabled()) return;
                Run("netsh.exe", "advfirewall firewall delete rule name=\"" + RuleName + "\"");
            }
            catch { }
        }

        /// <summary>是否有任何一个防火墙配置档是开着的（全关就不用管）。</summary>
        private static bool AnyProfileEnabled()
        {
            try
            {
                // netsh 输出语言随系统变，用 ExitCode + 关键字都不稳；
                // 这里改用 PowerShell 无依赖的 COM 查询太重，直接调 netsh 看输出里
                // 是否出现 "ON"/"启用"/"开启" 任一形态。
                var (ok, output) = Run("netsh.exe", "advfirewall show allprofiles state");
                if (!ok || string.IsNullOrEmpty(output)) return false;

                foreach (var raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.IndexOf("ON", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    if (line.IndexOf("启用", StringComparison.Ordinal) >= 0) return true;
                    if (line.IndexOf("开启", StringComparison.Ordinal) >= 0) return true;
                }
                return false;
            }
            catch { return false; }
        }

        private static (bool ok, string output) Run(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) return (false, "");
                string stdout = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(8000);
                return (p.ExitCode == 0, stdout);
            }
            catch { return (false, ""); }
        }
    }
}
