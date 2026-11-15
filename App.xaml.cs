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

            Breadcrumb("OnStartup begin");
            StartArgs = e.Args ?? Array.Empty<string>();
            Config = ConfigStore.Load();
            ThemeManager.Apply();

            Breadcrumb("config loaded: " + ConfigStore.ConfigPath);

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
                              k.Equals("quiet", StringComparison.OrdinalIgnoreCase));

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
            _tray = new TrayIcon("SoulLink · 跨会话执行器");
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