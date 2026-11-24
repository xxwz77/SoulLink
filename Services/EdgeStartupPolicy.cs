using System;
using System.Diagnostics;
using System.IO;

namespace SoulLink.Services
{
    /// <summary>
    /// 用户级 Edge 预启动策略（HKCU，class=User）。
    ///
    /// ★★ t7a 抽取（2026-11-24）★★
    ///   原实现 = Services/SessionSlimmer.ApplyUserPolicy。抽取理由：注入代理(SoulLink.Agent.exe)
    ///   只调用这一个方法（Relay/RelayAgent.cs 启动时），而 SessionSlimmer 其余方法引用 App.Config
    ///   与 SessionService（主程序 UI 依赖）。抽出来后：主程序与代理共用同一份真实实现，零 stub、零漂移。
    ///
    /// 语义：由**代理在目标会话内**调用，写当前进程（代理）自己的 HKCU —— 天然就是目标账号，
    ///   绕开「宿主挂载已登录用户 NTUSER.DAT 会失败」的死结（路径A）。
    ///   · 幂等 + 写失败静默降级：绝不抛异常、绝不阻塞代理进入注入循环；失败只落一行日志。
    ///   · 是否执行由主程序投送时的环境变量 SOULLINK_USER_POLICY 决定（见 RelayAgent.ApplyStartupUserPolicy）。
    /// </summary>
    public static class EdgeStartupPolicy
    {
        /// <summary>enable=true 写入禁用；false 删值恢复默认。</summary>
        public static void ApplyUserPolicy(bool enable)
        {
            try
            {
                if (enable)
                {
                    WriteDword(@"HKCU\SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0);
                    WriteDword(@"HKCU\SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0);
                }
                else
                {
                    DeleteValue(@"HKCU\SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled");
                    DeleteValue(@"HKCU\SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled");
                }
            }
            catch (Exception ex)
            {
                // 静默降级：只落日志，不抛出，不阻塞代理主循环。
                try { SoulLink.Relay.RelayAgent.LogQuiet("EdgeStartupPolicy.ApplyUserPolicy 失败：" + ex.Message); } catch { }
            }
        }

        private static void WriteDword(string keyFullPath, string name, int value)
            => RunReg(new[] { "add", keyFullPath, "/v", name, "/t", "REG_DWORD", "/d", value.ToString(), "/f" });

        private static void DeleteValue(string keyFullPath, string name)
            => RunReg(new[] { "delete", keyFullPath, "/v", name, "/f" });

        private static void RunReg(string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo("reg.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                p?.StandardOutput.ReadToEnd();
                p?.StandardError.ReadToEnd();
                p?.WaitForExit(8000);
            }
            catch { }
        }
    }
}
