using System;
using System.IO;
using System.Text;
using System.Threading;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// SoulLink.TaskEdit 入口（设计文档 §11.4 候选 B / §17.1）。
    ///
    /// ── 启动参数（由主程序传入，用户不需要手敲）──────────────────────
    ///   --port &lt;对外端口&gt;        默认取 config.json 的 settings.webPort
    ///   --internal-port &lt;内部端口&gt; 主程序回环监听端口（反代目标）
    ///   --no-firewall              不调 netsh（测试/无权限环境）
    ///   --check                    只校验配置与端口可用性，不常驻（给主程序探活用）
    ///
    /// ── 内部端点口的传递方式（本任务的选型说明）────────────────────────
    ///   选 **命令行参数为主 + config.json 的 webInternalPort 为兜底**：
    ///   ① 主程序启动本服务时把内部端口作为参数传进来，是一次性的、随进程生命周期
    ///      自然失效的值，**不需要跨重启持久化**，也不会在磁盘上留任何东西；
    ///   ② 参数没传（用户手动双击 exe）时，读 config.json 的 webInternalPort；
    ///   ③ 两者都没有 → 仍然正常启动监听，只是反代返回「主程序未运行」页，
    ///      并在控制台明确提示。**不猜端口**（猜错会转发到别人的服务上）。
    ///   对比其它方案：临时文件容易被残留的旧文件误导；注册表要写权限；
    ///   动态探测端口号更不可靠（同机可能有多个 SoulLink 实例）。
    /// </summary>
    public static class Program
    {
        /// <summary>单实例互斥体名（按身份隔离，与主程序同一套 AppIdentity 规则）。</summary>
        private static string MutexName => @"Local\" + SoulLink.Services.AppIdentity.Name + ".TaskEdit.v1";

        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var opts = ParseArgs(args);

            if (opts.Check)
            {
                var probe = ServiceConfig.Load(opts.InternalPort > 0 ? opts.InternalPort : 0);
                int port = opts.Port > 0 ? opts.Port : probe.WebPort;
                Console.WriteLine("[TaskEdit] config=" + probe.ConfigPath);
                Console.WriteLine("[TaskEdit] webPort=" + port + "  internalPort=" + probe.InternalPort);
                foreach (string w in probe.Warnings) Console.WriteLine("[TaskEdit] 警告：" + w);
                if (port <= 0) { Console.WriteLine("[TaskEdit] WebPort=0（未启用网页）"); return 3; }
                bool free = IsPortFree(port);
                Console.WriteLine("[TaskEdit] 端口 " + port + (free ? " 可用" : " 已被占用"));
                return free ? 0 : 4;
            }

            Mutex mutex = null;
            try
            {
                mutex = new Mutex(true, MutexName, out bool created);
                if (!created)
                {
                    // ★ 单实例：两个进程抢同一端口只会有一个静默失效（Windows 端口独占），
                    //   必须**明确告诉用户**而不是假装启动成功。
                    Console.Error.WriteLine("[TaskEdit] 已经有一个 SoulLink.TaskEdit 实例在运行，本进程退出。");
                    Console.Error.WriteLine("           如需重启，请先关闭现有实例（或退出主程序后再试）。");
                    return 5;
                }

                var config = ServiceConfig.Load(opts.InternalPort);
                if (opts.Port > 0) configOverridePort(config, opts.Port);

                Console.WriteLine("[TaskEdit] 配置：" + config.ConfigPath);
                foreach (string w in config.Warnings) Console.WriteLine("[TaskEdit] 警告：" + w);

                if (config.WebPort <= 0)
                {
                    Console.WriteLine("[TaskEdit] WebPort=0（未启用网页），本服务不启动。");
                    Console.WriteLine("           请在主程序「应用设置」里填写网页端口后重试。");
                    return 3;
                }

                var auth = new AuthGate(config.WebPassword, config.WebAuthToken, out bool generated);
                if (generated)
                {
                    // ★ 首次启动生成的一次性令牌：打印到控制台（用户在本机看得到），
                    //   并尝试写回 config.json，否则重启后又要重新生成。
                    Console.WriteLine("");
                    Console.WriteLine("  ┌──────────────────────────────────────────────────────────┐");
                    Console.WriteLine("  │ 首次启动：已生成访问令牌（远程访问时输入它）              │");
                    Console.WriteLine("  │ " + auth.GeneratedToken.PadRight(56) + " │");
                    Console.WriteLine("  └──────────────────────────────────────────────────────────┘");
                    Console.WriteLine("");
                    if (config.TryPersistAuthToken(auth.GeneratedToken))
                        Console.WriteLine("[TaskEdit] 令牌已写入 config.json（settings.webAuthToken）");
                    else
                        Console.WriteLine("[TaskEdit] ⚠ 令牌未能写回配置：下次启动会重新生成，请手动记录");
                }

                var server = new TaskEditServer(config, auth);
                if (!opts.NoFirewall) { /* Start() 内部按开关调用 WebFirewall */ }
                if (!server.Start()) return 6;

                // ── 优雅退出：Ctrl+C / Ctrl+Break / 进程关闭 ──
                using var quit = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;    // 阻止默认的强制终止，走我们自己的清理
                    Console.WriteLine("[TaskEdit] 收到 Ctrl+C，正在停止…");
                    quit.Set();
                };
                AppDomain.CurrentDomain.ProcessExit += (_, __) => { try { server.Stop(); } catch { } };

                Console.WriteLine("[TaskEdit] 就绪。按 Ctrl+C 退出。");
                quit.Wait();
                server.Stop();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[TaskEdit] 致命错误：" + ex.GetType().Name + " " + ex.Message);
                try
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "soullink-taskedit-crash.log"),
                        ex.ToString(), new UTF8Encoding(false));
                }
                catch { }
                return 1;
            }
            finally
            {
                try { mutex?.ReleaseMutex(); } catch { }
                try { mutex?.Dispose(); } catch { }
            }
        }

        internal sealed class Options
        {
            internal int Port;
            internal int InternalPort;
            internal bool NoFirewall;
            internal bool Check;
        }

        private static Options ParseArgs(string[] args)
        {
            var o = new Options();
            if (args == null) return o;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                string next = i + 1 < args.Length ? args[i + 1] : "";
                if (a.Equals("--port", StringComparison.OrdinalIgnoreCase) && int.TryParse(next, out int p))
                { o.Port = p; i++; }
                else if (a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(a.Substring(7), out int p2)) o.Port = p2;
                else if (a.Equals("--internal-port", StringComparison.OrdinalIgnoreCase) && int.TryParse(next, out int ip))
                { o.InternalPort = ip; i++; }
                else if (a.StartsWith("--internal-port=", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(a.Substring(16), out int ip2)) o.InternalPort = ip2;
                else if (a.Equals("--no-firewall", StringComparison.OrdinalIgnoreCase)) o.NoFirewall = true;
                else if (a.Equals("--check", StringComparison.OrdinalIgnoreCase)) o.Check = true;
            }
            return o;
        }

        /// <summary>命令行显式端口覆盖配置（仅当参数给了才覆盖）。</summary>
        private static void configOverridePort(ServiceConfig config, int port)
        {
            try
            {
                var prop = typeof(ServiceConfig).GetProperty("WebPort",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public);
                prop?.SetValue(config, port);
            }
            catch { }
        }

        /// <summary>端口是否空闲（只测能否绑回环；真正绑定在 Start 里做）。</summary>
        private static bool IsPortFree(int port)
        {
            try
            {
                var l = new System.Net.HttpListener();
                l.Prefixes.Add("http://+:" + port + "/");
                l.Start();
                l.Stop();
                l.Close();
                return true;
            }
            catch { return false; }
        }
    }
}
