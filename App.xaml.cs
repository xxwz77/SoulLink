using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SoulLink.Models;
using SoulLink.Services;
using SoulLink.Views;

namespace SoulLink
{
    public partial class App : Application
    {
        public static AppConfig Config { get; private set; } = new AppConfig();

        /// <summary>
        /// 配置被**外部**修改后触发（目前唯一来源：网页端 /api/settings）。
        ///
        /// ★★ 为什么要有它（2026-11-22 用户反馈）★★
        ///    「我在网页设置之后，应用里没变」
        ///
        ///   网页写的是同一个 App.Config.Settings，但已经打开的控件
        ///   不会自己重读 —— 界面停在旧值上。而"切页才刷新"也不够：
        ///   用户可能就停在这一页不动，永远看不到变化。
        ///
        ///   所以改成**推**：写配置的地方主动喊一声，订阅者自己刷新。
        ///   事件在 UI 线程上抛（调用方负责切线程），订阅者可以直接改控件。
        /// </summary>
        public static event Action? SettingsChanged;

        /// <summary>由修改配置的一方调用，通知界面刷新。</summary>
        public static void NotifySettingsChanged()
        {
            try { SettingsChanged?.Invoke(); } catch { }
        }

        private static readonly StringBuilder Boot = new StringBuilder();
        /// <summary>启动阶段面包屑，出问题时第一手线索。</summary>
        public static void Breadcrumb(string step)
        {
            try
            {
                Boot.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + step);
                Directory.CreateDirectory(ConfigStore.ConfigDir);
                File.WriteAllText(Path.Combine(ConfigStore.ConfigDir, "boot.log"), Boot.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
        public static string[] StartArgs { get; private set; } = Array.Empty<string>();
        public static bool QuietStart { get; private set; }
        /// <summary>启动时要直接打开的页面（--page）. </summary>
        public static string StartupPage { get; private set; } = "";

        private TrayIcon? _tray;
        private Mutex? _instanceMutex;
        private MainWindow? _window;
        private InvokeRequest? _startupRequest;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        protected override async void OnStartup(StartupEventArgs e)
        {
            // ★ 名字叫 SoulLink.Agent.exe 的副本是"手动代理"，双击即用：
            //   没有显式参数时自动进入 --agent，用户不需要记参数。
            //   （Broker 安装时会把 SoulLink.exe 复制一份改名为 SoulLink.Agent.exe 并改写清单为 asInvoker）
            if ((e.Args == null || e.Args.Length == 0)
                && Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "")
                    .Equals("SoulLink.Agent", StringComparison.OrdinalIgnoreCase))
            {
                int agentCode = 1;
                try { agentCode = ManualAgent.Run(Array.Empty<string>()); }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(Path.GetTempPath(), "soullink-agent-crash.log"),
                            ex.ToString());
                    }
                    catch { }
                }
                Shutdown(agentCode);
                return;
            }

            if (e.Args.Length > 0 && e.Args[0].Equals("--broker-setup", StringComparison.OrdinalIgnoreCase))
            {
                new BrokerWindow().Show(); return;
            }
            if (e.Args.Length > 0 && e.Args[0].Equals("--broker", StringComparison.OrdinalIgnoreCase))
            {
                try { await BrokerService.RunAsync(_cts.Token); }
                catch (Exception ex) { BrokerService.Trace("FATAL", ex.ToString()); }
                return;
            }
            // ★ 先分流 relay 模式：目标会话里的注入代理是纯后台进程，
            //   不建托盘、不建窗口、不碰配置，直接把线程交给管道循环。
            //   放在 base.OnStartup 之前，确保没有任何 UI 动作先跑起来。
            // SYSTEM 引导模式：由计划任务以 SYSTEM 拉起，负责把目标程序投进目标会话。
            // 同样必须在任何 UI 动作之前分流。
            if (e.Args != null
                && e.Args.Length >= 1
                && e.Args[0].Equals("--system-run", StringComparison.OrdinalIgnoreCase))
            {
                int code = 1;
                try { code = SystemLauncher.RunBootstrap(e.Args); }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(Path.GetTempPath(), "soullink-sysrun-crash.log"),
                            ex.ToString());
                    }
                    catch { }
                }
                Shutdown(code);
                return;
            }

            // ★ 手动代理：用户在目标会话里双击启动，自动寻找宿主管道。
            //   与自动投送并存，互不影响；它不走提权、不走服务、不走计划任务。
            if (e.Args != null
                && e.Args.Length >= 1
                && e.Args[0].Equals("--agent", StringComparison.OrdinalIgnoreCase))
            {
                int code = 1;
                try { code = ManualAgent.Run(e.Args); }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(Path.GetTempPath(), "soullink-agent-crash.log"),
                            ex.ToString());
                    }
                    catch { }
                }
                Shutdown(code);
                return;
            }

            if (e.Args != null
                && e.Args.Length >= 1
                && e.Args[0].Equals("--relay", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Relay.RelayAgent.Run(e.Args);
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(Path.GetTempPath(), "soullink-relay-crash.log"),
                            ex.ToString());
                    }
                    catch { }
                }
                Shutdown(0);
                return;
            }

            base.OnStartup(e);

            // ★ 用户要求：关闭应用就必须立刻停止所有自动化。
            //   原来没有 OnExit，调度器的后台 Task 会继续跑完一个流程
            //   （用户实测：点了关闭，流程还在执行）。
            Exit += (_, __) =>
            {
                try { Services.AccountStateCenter.Stop(); } catch { }
                try { Services.ScheduleRunnerHost.Shutdown(); } catch { }
                try { Services.RouteUpdateSchedulerHost.Shutdown(); } catch { }
                try { Services.WebServer.Stop(); } catch { }
            };

            DispatcherUnhandledException += (_, args) =>
            {
                AppendErrorLog("UI", args.Exception);
                LogBus.Error("未处理异常：" + args.Exception.Message, args.Exception.StackTrace ?? "");
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                AppendErrorLog("AppDomain", args.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                AppendErrorLog("Task", args.Exception);
                args.SetObserved();
            };

            // 后创建的窗口、原生浮层和菜单统一绑定当前主题；不改系统文件选择器与 RDP 内容。
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((_, args) =>
                {
                    if (args.Source is Window window) ThemeManager.ApplyWindow(window);
                }));

            // ★ 自检：把模块目录的实际内容写进 boot.log。
            //   之前多次出现"源码里有、界面没有"，靠猜排查浪费太多时间，
            //   这里直接输出权威结果，一看日志就知道到底注册了哪些。
            try
            {
                var all = Models.FlowNodeCatalog.All();
                var trig = Models.FlowNodeCatalog.ByKind(Models.FlowNodeKind.Trigger);
                var cond = Models.FlowNodeCatalog.ByKind(Models.FlowNodeKind.Condition);
                Breadcrumb("automation-ui: chinese-v2 modules=" + all.Count + " conditions=" + cond.Count
                         + " actions=" + Models.FlowNodeCatalog.ByKind(Models.FlowNodeKind.Action).Count
                         + " daily-finish=" + Models.FlowNodeCatalog.ByKind(Models.FlowNodeKind.Terminate).Count);
                foreach (var d in cond) Breadcrumb("  C " + d.Type + " " + d.Title);

                // ★ 端口布局标记（Bug 2/3 的判定依据）：
                //   "源码改了但看的是旧窗口"反复扯不清，加一行版本标记，
                //   启动后 grep 得到就是新版；grep 不到就说明跑的是旧 exe。
                Breadcrumb("automation-ports: input-top output-bottom; explicit-hint=full-reason click-to-select");

                string err = Models.FlowNodeCatalog.LastAddError;
                if (!string.IsNullOrWhiteSpace(err))
                    foreach (var line in err.Split('\n'))
                        if (line.Trim().Length > 0) Breadcrumb("  ADD-FAIL " + line.Trim());
            }
            catch (Exception ex)
            {
                Breadcrumb("catalog self-check FAILED: " + ex.GetType().Name + " " + ex.Message);
            }
            StartArgs = e.Args ?? Array.Empty<string>();
            Config = ConfigStore.Load();
            ThemeManager.Apply();

            Breadcrumb("config loaded: " + ConfigStore.ConfigPath);

            // 清理上次被强杀时遗留的投送任务与临时文件（正常退出会自行删除）。
            try { Services.LaunchSweeper.Sweep(); } catch { }

            // ★ 2.21.2：启动时按设置清理过期的备份与缓存。
            //   放后台线程：清理 872 MB 级目录可能要几秒，不能挡启动。
            //   只在「用户设了限制」时才动，不限制就完全跳过（避免误删）。
            _ = Task.Run(() =>
            {
                try
                {
                    int keep = Config.Settings.BgiBackupKeep;
                    if (keep > 0)
                    {
                        int n1 = Services.BgiUpdater.PruneBackups(keep);
                        int n2 = Services.RouteUpdater.PruneBackups(keep);
                        if (n1 + n2 > 0)
                            LogBus.Info("启动清理：旧备份", "共清理 " + (n1 + n2) + " 份");
                    }

                    int days = Config.Settings.RouteCacheKeepDays;
                    if (days > 0)
                    {
                        int n3 = Services.RouteUpdater.PruneCache(days);
                        if (n3 > 0)
                            LogBus.Info("启动清理：过期缓存", n3 + " 个文件（超过 " + days + " 天）");
                    }
                }
                catch { }
            });

            var parsed = CommandLine.Parse(StartArgs);
            QuietStart = parsed.WantsQuiet;

            if (parsed.WantsHelp)
            {
                ShowHelp();
                Shutdown(0);
                return;
            }

            bool uiOnly = parsed.Verb.Length == 0 && parsed.Options.Count > 0 &&
                          parsed.Options.Keys.All(k =>
                              k.Equals("page", StringComparison.OrdinalIgnoreCase) ||
                              k.Equals("show", StringComparison.OrdinalIgnoreCase) ||
                              k.Equals("quiet", StringComparison.OrdinalIgnoreCase) ||
                              // ★ 纯诊断开关：--verbose-log 只影响日志级别，
                              //   必须仍然启动界面。以前它被当成业务选项，
                              //   进程走命令模式执行完就退出，等于"文档里的开关根本不能用"。
                              k.Equals("verbose-log", StringComparison.OrdinalIgnoreCase));

            if (uiOnly)
            {
                StartupPage = parsed.Options.TryGetValue("page", out var pg) ? pg.ToLowerInvariant() : "";
            }

            if (!uiOnly && (parsed.Verb.Length > 0 || parsed.Options.Count > 0))
            {
                var req = CommandLine.ToRequest(parsed);
                req.Quiet = parsed.WantsQuiet || true;

                // 读取 --password-stdin / --passwordfile，避免密码进入命令行
                string? stdinPwd = await ReadPasswordAsync(parsed).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(stdinPwd)) req.Password = stdinPwd;

                bool single = Ipc.TryAcquire();
                if (!single)
                {
                    // 已有实例：把命令发过去，本进程无声退出（无窗口、无弹窗）
                    var resp = await Task.Run(() => Ipc.Send(req, 180000)).ConfigureAwait(true);
                    if (resp != null) WriteCommandLog(req, resp);
                    Shutdown(resp == null ? 1 : resp.Ok ? 0 : 1);
                    return;
                }

                _instanceMutex = Ipc.Owned;
                _startupRequest = req;
            }
            else
            {
                _instanceMutex = Ipc.TryAcquire() ? Ipc.Owned : null;
            }

            // 单实例：没有拿到互斥体说明已经有常驻实例了
            if (_instanceMutex == null)
            {
                if (_startupRequest != null)
                {
                    var resp = await Task.Run(() => Ipc.Send(_startupRequest!, 180000)).ConfigureAwait(true);
                    if (resp != null) WriteCommandLog(_startupRequest!, resp);
                    Shutdown(resp == null || resp.Ok ? 0 : 1);
                }
                else
                {
                    // 用户又双击了一次图标：让对方把窗口显示出来，本进程安静退出
                    await Task.Run(() => Ipc.Send(new InvokeRequest { Show = true, UiPage = StartupPage }, 8000))
                        .ConfigureAwait(true);
                    Shutdown(0);
                }
                return;
            }

            Breadcrumb("single instance acquired");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CreateTray();
            Breadcrumb("tray created");
            _ = Ipc.ListenAsync(HandleIpcAsync, _cts.Token);

            // 常驻服务：确认投送通道可用（需要管理员权限 + SeDebugPrivilege）
            _ = Task.Run(() =>
            {
                try
                {
                    SystemLauncher.EnablePrivileges();
                    if (SystemLauncher.IsElevated)
                        LogBus.Info("投送通道就绪", "SYSTEM 计划任务通道 · 管理员权限");
                    else
                        LogBus.Warn("缺少管理员权限", "跨会话投送需要「以管理员身份运行」");
                }
                catch { }
            });

            // ★ 启动时检查手动代理模式：开着就直接进入等待，不投送任何代理。
            //   这样用户设一次开关，之后每次启动都保持同样的行为。
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!ManualAgentMode.Enabled) return;
                    LogBus.Info("手动代理模式已开启", "不会自动投送；等待用户在目标会话启动代理");
                    int cleaned = ManualAgentMode.CleanAutoAgents();
                    if (cleaned > 0) LogBus.Info("已清理自动代理", cleaned + " 个");
                    var started = await ManualAgentMode.StartForOnlineAccountsAsync().ConfigureAwait(false);
                    if (started.Count > 0)
                        LogBus.Info("已准备等待管道", "会话 " + string.Join("、", started));
                }
                catch (Exception ex) { LogBus.Warn("初始化手动代理模式失败", ex.Message); }
            });

            if (_startupRequest != null)
            {
                // 首次以命令方式启动：执行后退出，不留界面
                var resp = await RequestHandler.HandleAsync(_startupRequest, Config).ConfigureAwait(true);
                WriteCommandLog(_startupRequest!, resp);
                Shutdown(resp.Ok ? 0 : 1);
                return;
            }

            Breadcrumb("showing main window");
            try
            {
                ShowMainWindow();
                Breadcrumb("main window shown: " + (_window?.IsVisible) + " state=" + (_window?.WindowState));

                // 调试用：--page=xxx 直接跳页
                string? page = null;
                foreach (var a in Environment.GetCommandLineArgs())
                    if (a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
                    { page = a.Substring(7); break; }
                if (page != null) _window?.NavigateToStartup(page);
            }
            catch (Exception ex)
            {
                AppendErrorLog("ShowMainWindow", ex);
                Breadcrumb("SHOW FAILED: " + ex.GetType().Name + " " + ex.Message);
                MessageBox.Show("界面启动失败：" + ex.Message, "SoulLink", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 命令模式下把结果写进 last-command.log —— 无窗口运行时用户看不到任何提示，
        /// 出问题时可以直接打开这个文件排查。
        /// </summary>
        private static void WriteCommandLog(InvokeRequest req, IpcResponse resp)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.ConfigDir);
                string path = Path.Combine(ConfigStore.ConfigDir, "last-command.log");
                var sb = new StringBuilder();
                sb.AppendLine("────── " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ──────");
                sb.AppendLine("参数  : " + string.Join(" ", StartArgs).Replace(
                    string.IsNullOrEmpty(req.Password) ? "\u0000" : req.Password, "***"));
                sb.AppendLine("结果  : " + (resp.Ok ? "成功" : "失败") + "  (exit " + resp.ExitCode + ", " + resp.ElapsedMs + " ms)");
                sb.AppendLine("消息  : " + resp.Message);
                if (!string.IsNullOrWhiteSpace(resp.Command)) sb.AppendLine("命令  : " + resp.Command);
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>
        /// 写一条**普通诊断**日志（不是异常）。
        ///
        /// ★ 为什么需要它：原来有些地方图省事，把正常事件
        ///   （"投送成功 会话19"）包成 new InvalidOperationException(msg)
        ///   丢进 AppendErrorLog，结果 error.log 里全是假的异常记录，
        ///   真出故障时反而找不到。正常事件就该走这里。
        ///
        /// 默认不落盘（保持 error.log 干净），需要时设环境变量
        /// SOULLINK_DIAG_LOG=1 打开，写入 diag-yyyyMMdd.log。
        /// </summary>
        public static void AppendDiagLog(string message)
        {
            if (!DiagLogEnabled) return;
            try
            {
                Directory.CreateDirectory(ConfigStore.ConfigDir);
                File.AppendAllText(
                    Path.Combine(ConfigStore.ConfigDir, "diag-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                    "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static readonly bool DiagLogEnabled = DetectDiagLog();

        private static bool DetectDiagLog()
        {
            try { return Environment.GetEnvironmentVariable("SOULLINK_DIAG_LOG") == "1"; }
            catch { return false; }
        }

        /// <summary>把未处理异常写进 %LOCALAPPDATA%\SoulLink\error.log，出问题时好排查。</summary>
        public static void AppendErrorLog(string source, Exception? ex)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.ConfigDir);
                File.AppendAllText(Path.Combine(ConfigStore.ConfigDir, "error.log"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + source + "] " +
                    (ex?.ToString() ?? "unknown") + Environment.NewLine + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static async Task<string?> ReadPasswordAsync(CommandLine.Parsed parsed)
        {
            try
            {
                if (parsed.Options.ContainsKey("password-stdin") || parsed.Options.ContainsKey("pwd-stdin"))
                {
                    string? line = await Task.Run(() => Console.In.ReadLine()).ConfigureAwait(false);
                    return line?.Trim();
                }

                if (parsed.Options.TryGetValue("passwordfile", out var f) ||
                    parsed.Options.TryGetValue("pwdfile", out f))
                {
                    if (File.Exists(f)) return File.ReadAllText(f).Trim();
                }
            }
            catch { }
            return null;
        }

        private void CreateTray()
        {
            _tray = new TrayIcon(string.Equals(AppIdentity.Name, "SoulLink", StringComparison.OrdinalIgnoreCase)
                ? "SoulLink · 跨会话执行器"
                : "SoulLink · 挂机验证版");
            _tray.ShowRequested += () => Dispatcher.Invoke(ShowMainWindow);
            _tray.ExitRequested += () => Dispatcher.Invoke(ExitApp);
            _tray.QuickOpenRequested += () => Dispatcher.Invoke(async () => await RunLastActionAsync());
            LogBus.Published += entry =>
            {
                if (entry.Level == LogLevel.Error && _tray != null)
                    _tray.Balloon("SoulLink", entry.Text, System.Windows.Forms.ToolTipIcon.Warning);
            };
        }

        private async Task RunLastActionAsync()
        {
            var action = Config.Actions.FirstOrDefault();
            if (action == null) { ShowMainWindow(); return; }
            var r = await ActionService.ExecuteAsync(action, Config);
            LogBus.Write(r.Ok ? LogLevel.Ok : LogLevel.Error, r.Message);
            _tray?.Balloon(action.Name, r.Message, r.Ok ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
        }

        private async Task<IpcResponse> HandleIpcAsync(InvokeRequest req)
        {
            if (req.Show)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ShowMainWindow();
                    if (!string.IsNullOrEmpty(req.UiPage)) _window?.SelectPage(req.UiPage!);
                });
                if (string.IsNullOrEmpty(req.Action) && string.IsNullOrEmpty(req.Process)
                    && string.IsNullOrEmpty(req.User) && string.IsNullOrEmpty(req.Account))
                    return new IpcResponse { Ok = true, Message = "已唤起界面。" };
            }

            if (!Config.Settings.AllowRemoteInvoke)
                return new IpcResponse { Ok = false, Message = "设置中已禁止命令调用。" };
            await Dispatcher.InvokeAsync(() => { }); // 保证在 UI 线程外安全执行
            return await Task.Run(() => RequestHandler.HandleAsync(req, Config)).ConfigureAwait(false);
        }

        public void ShowMainWindow()
        {
            if (_window == null)
            {
                _window = new MainWindow();
                _window.Closed += (_, __) =>
                {
                    bool toTray = Config.Settings.MinimizeToTray;
                    _window = null;
                    if (!toTray) ExitApp();
                };

                // 老流程没有 CreatedAt：按当前时间补上，
                // 避免"一启动就把当天更早的历史时间点当作补跑目标"。
                // 只在内存里补，下次保存时自然写盘。
                try
                {
                    foreach (var f in Config.Automations ?? new System.Collections.Generic.List<Models.AutomationFlow>())
                        if (f.CreatedAt == null) f.CreatedAt = DateTime.Now;
                }
                catch { }

                // ★ 一次性迁移：旧版 Resolve 会自动把路径写进 BgiPaths，
                //   导致"用户是否显式绑定过"无法判断。2.17.45 起只有 Pin() 能写，
                //   这里对「路径存在 + 该 BGI 正在运行」的账号补一次绑定，
                //   避免升级后所有账号突然变成"未绑定"而不再刷新日志数据。
                try
                {
                    int migrated = BgiPathResolver.MigrateAutoBindings();
                    if (migrated > 0) Breadcrumb("BGI 绑定迁移：" + migrated + " 个账号");
                }
                catch (Exception ex) { LogBus.Error("BGI 绑定迁移失败：" + ex.Message); }

                // 启动账号状态中心：每账号一份快照，供触发器/条件/界面统一取数。
                // ★ 必须先于调度器启动，否则第一轮 Tick 会拿不到快照而各自现查。
                try
                {
                    AccountStateCenter.Start(Config);
                    Breadcrumb("AccountStateCenter started");
                }
                catch (Exception ex)
                {
                    LogBus.Error("启动账号状态中心失败：" + ex.Message);
                }

                // 启动进程内定时调度器（只处理"仅运行时"的计划）
                try
                {
                    ScheduleRunnerHost.StartOnce(() => Config);
                }
                catch (Exception ex)
                {
                    LogBus.Error("启动定时调度失败：" + ex.Message);
                }

                // 启动局域网网页服务（端口来自设置，0 = 不启动）
                try
                {
                    Services.WebServer.Apply(Config?.Settings?.WebPort ?? 0);
                    Services.WebServer.EnsureLiveRefresh();
                }
                catch (Exception ex)
                {
                    LogBus.Error("启动网页服务失败：" + ex.Message);
                }

                // 启动路线更新的定时调度器（与动作调度互不影响）
                try
                {
                    RouteUpdateSchedulerHost.StartOnce();
                }
                catch (Exception ex)
                {
                    LogBus.Error("启动路线更新定时失败：" + ex.Message);
                }
            }

            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            _window.Focus();
        }

        public void ExitApp()
        {
            // 显式关闭模式下，先释放全局钩子与注入代理，再结束进程。
            foreach (Window window in Current.Windows)
                if (window is MainWindow main) main.ForceClose();
            try { ConfigStore.Save(Config); } catch { }
            // ★ 排空后台保存队列：保证最后一次保存写盘完成后再结束进程。
            try { ConfigStore.Flush(); } catch { }
            _cts.Cancel();
            _tray?.Dispose();
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            try { _instanceMutex?.Dispose(); } catch { }
            Shutdown(0);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // ★ 兜底：任何路径退出都要把用户的系统设置还原，
            //   绝不能把「指针加速」留在关闭状态（这是污染系统设置）。
            try { Services.PointerAcceleration.ForceRestore(); } catch { }
            try { System.Windows.Forms.Cursor.Show(); } catch { }
            // ★ 关键：绝不能把光标约束留在系统里（否则用户鼠标被困在旧矩形内）。
            try { Services.CursorClip.ForceRelease(); } catch { }

            _tray?.Dispose();
            base.OnExit(e);
        }

        private static void ShowHelp()
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "soullink-help.txt");
                File.WriteAllText(path, CommandLine.HelpText, new UTF8Encoding(false));
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { }
        }
    }
}