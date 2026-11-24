using System;
using System.Threading;

namespace SoulLink.Broker
{
    /// <summary>
    /// 无 UI Broker 服务入口（t16 / T7b）。
    /// ★ 复刻 App.xaml.cs 的 --broker 分支（App.xaml.cs:93-98）：await BrokerService.RunAsync(ct)。
    ///   本 exe 的唯一用途就是当服务本体，由 SCM 以 LocalSystem 启动，命令行固定为 "SoulLink.Broker.exe" --broker。
    ///   不用 WPF Application，所以这里直接从 Main 进 BrokerService，不经过 App.OnStartup。
    /// </summary>
    internal static class BrokerProgram
    {
        private static int Main(string[] args)
        {
            using var cts = new CancellationTokenSource();
            // 手工前台运行时支持 Ctrl+C 优雅停（服务模式下 SCM 走 WindowsServiceLifetime，不依赖这里）。
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            try
            {
                SoulLink.Services.BrokerService.RunAsync(cts.Token).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                SoulLink.Services.BrokerService.Trace("FATAL", ex.ToString());
                return 1;
            }
        }
    }
}
