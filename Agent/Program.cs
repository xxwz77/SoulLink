using System;
using System.IO;

namespace SoulLink.Agent
{
    // ★ t7a 无 UI 注入代理入口：复刻主程序 App.xaml.cs:69-166 的 --agent / --relay 分流。
    //   · 无参数且文件名为 SoulLink.Agent.exe → 手动代理（双击即用）
    //   · --agent → 手动代理
    //   · --relay <pipe> → 注入代理（Broker / 计划任务投送，参数契约含 --require-elevated / --deploy-nonce）
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                if ((args == null || args.Length == 0)
                    && string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? ""),
                        "SoulLink.Agent", StringComparison.OrdinalIgnoreCase))
                {
                    return Services.ManualAgent.Run(Array.Empty<string>());
                }

                if (args != null && args.Length >= 1 && args[0].Equals("--agent", StringComparison.OrdinalIgnoreCase))
                {
                    return Services.ManualAgent.Run(args);
                }

                if (args != null && args.Length >= 1 && args[0].Equals("--relay", StringComparison.OrdinalIgnoreCase))
                {
                    return Relay.RelayAgent.Run(args);
                }

                return 2;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "soullink-agent-crash.log"),
                        ex.ToString());
                }
                catch { }
                return 1;
            }
        }
    }
}
