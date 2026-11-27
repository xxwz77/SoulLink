using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>引擎（跨会话投送 Broker 通道）的 5 态。</summary>
    public enum EngineState
    {
        /// <summary>服务在跑且注入代理版本一致。</summary>
        Ready,
        /// <summary>服务未安装（首次使用）。</summary>
        NotInstalled,
        /// <summary>服务已安装但未运行。</summary>
        NotRunning,
        /// <summary>注入代理缺失或版本不一致。</summary>
        AgentNeedsUpdate,
        ServiceNeedsUpdate,
        ComponentsNeedUpdate,
        /// <summary>
        /// 引擎本身（服务 + 注入代理版本）正常，但**投送通道没有连着**。
        /// ★ 2026-11-24 P0-A：用户实测「界面一片绿，啥也用不了」——服务在跑、agent 版本对、
        ///   但 relay 管道已断/代理进程已死。原 Ready 判据完全不看「通道是否真的连着」。
        ///   此态必须渲染成**非绿**（琥珀），与「就绪」明确区分。
        /// </summary>
        ChannelDown,
        /// <summary>修复失败 / 状态异常。</summary>
        Error
    }

    /// <summary>
    /// 检查已安装的 Broker 与注入代理，并通过独立安装器应用组件更新。
    /// </summary>
    public static class AgentSync
    {
        public const string ServiceName = EngineComponentVersions.BrokerServiceName;
        public const string AgentFileName = "SoulLink.Agent.exe";

        /// <summary>
        /// ★ 2026-11-24 修「投送失败不反馈」：投送链路（RdpConsole 等）失败或手动重试时
        /// 触发，MainWindow 订阅后重新 Inspect 并渲染顶栏——引擎状态不再「只在启动检查一次」。
        /// </summary>
        public static event Action<string>? RecheckRequested;

        /// <summary>请求顶栏重查引擎状态（reason 进 Tooltip/日志）。</summary>
        public static void RequestRecheck(string reason)
        {
            try { RecheckRequested?.Invoke(reason); }
            catch { }
        }

        /// <summary>当前主程序 exe 路径。</summary>
        public static string SelfExe => SystemLauncher.ResolveSelfExe();

        /// <summary>当前主程序 FileVersion（如 "3.3.39.0"）。</summary>
        public static string SelfVersion
        {
            get
            {
                try { return FileVersionInfo.GetVersionInfo(SelfExe).FileVersion ?? ""; }
                catch { return ""; }
            }
        }

        /// <summary>发布包目录里的 agent（主程序同目录，拆分任务产出的独立 exe）。</summary>
        public static string SourceAgent
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(SelfExe);
                    if (string.IsNullOrEmpty(dir)) dir = AppIdentity.ExeDir;
                    return Path.Combine(dir, AgentFileName);
                }
                catch { return Path.Combine(AppIdentity.ExeDir, AgentFileName); }
            }
        }

        /// <summary>安装目录里的 agent（Broker 固定路径，受 ACL 保护）。</summary>
        public static string TargetAgent => BrokerInstall.AgentExe;

        /// <summary>读某个 exe 的 AgentVersion（三级回退），缺失返回 ""。</summary>
        public static string AgentVersionOf(string path) => BrokerInstall.ReadAgentVersion(path) ?? "";

        /// <summary>
        /// 公开的 agent 判据结果：顶栏「就绪」与 Broker「AGENT_INVALID」必须同源。
        /// t2 / 调用方只读这里的 Passed/Source，禁止自己再算一套版本口径。
        /// </summary>
        public struct AgentVerdict
        {
            public bool Passed;
            public string ServiceVersion;   // 服务本体要求的版本（归一化后），空 = 无标识
            public string AgentVersion;     // 安装目录 agent 版本（归一化后），空 = 无标识
            public string Source;           // 判定依据来源（ValidAgent 内部三级回退命中的那级）
        }

        /// <summary>安装目录 agent 版本是否满足服务要求（服务 Running 前提下，缺失/不足 = false）。</summary>
        public static bool IsAgentCurrent => File.Exists(TargetAgent)
            && BrokerInstall.ValidAgent(BrokerInstall.ServiceExe, TargetAgent)
            && BrokerInstall.ValidAgent(SelfExe, TargetAgent)
            && (EngineComponentVersions.Read(TargetAgent, EngineComponentVersions.AgentFile) is not { } installed
                || EngineComponentVersions.ReadMarker(BrokerInstall.VersionFileFor(TargetAgent)) == installed)
            && (EngineComponentVersions.Read(SourceAgent, EngineComponentVersions.AgentFile) is not { } offered
                || !EngineComponentVersions.NeedsUpdate(TargetAgent, EngineComponentVersions.AgentFile, offered));

        private static string SourceBroker => Path.Combine(Path.GetDirectoryName(SourceAgent)!, EngineComponentVersions.BrokerFile);

        public static bool RequiresUpdate(EngineState state)
            => state is EngineState.AgentNeedsUpdate or EngineState.ServiceNeedsUpdate or EngineState.ComponentsNeedUpdate;

        /// <summary>最近一次 Inspect 的判据结果（默认 false）。t2 只读，不自行重算。</summary>
        public static AgentVerdict LastVerdict = new AgentVerdict();

        /// <summary>Broker 服务是否已安装；true 时 status 为当前状态。</summary>
        public static bool TryGetServiceStatus(out ServiceControllerStatus status)
        {
            status = ServiceControllerStatus.Stopped;
            try
            {
                using var sc = new ServiceController(ServiceName);
                status = sc.Status;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 自检：给出引擎 5 态 + 说明 + Tooltip（含主程序/agent 版本对比）。
        /// ★ 纯读取，不做任何写入、不弹任何提权。
        /// </summary>
        public static EngineState Inspect(out string detail, out string tooltip)
        {
            string selfVer = SelfVersion;
            string serviceVer = BrokerProtocol.ServerExeVersion();
            string? requiredRaw = BrokerInstall.ReadRequiredAgentVersion(BrokerInstall.ServiceExe);
            string? sourceRequired = BrokerInstall.ReadRequiredAgentVersion(SelfExe);
            if (BrokerProtocol.ParseVersion(sourceRequired) is { } sourceVersion
                && (BrokerProtocol.ParseVersion(requiredRaw) is not { } serviceMinimum || sourceVersion > serviceMinimum))
                requiredRaw = sourceRequired;
            string? agentRaw = BrokerInstall.TryReadAgentVersion(TargetAgent, out string agentSrc);
            string requiredVer = requiredRaw ?? "";
            string agentVer = agentRaw ?? "";
            bool verdict = IsAgentCurrent;
            var offeredBroker = EngineComponentVersions.Read(SourceBroker, EngineComponentVersions.BrokerFile);
            var offeredAgent = EngineComponentVersions.Read(SourceAgent, EngineComponentVersions.AgentFile);
            bool brokerOld = !File.Exists(BrokerInstall.ServiceExe) || !BrokerProtocol.CheckServiceVersion(serviceVer).ok
                || (offeredBroker != null && (EngineComponentVersions.ReadInstalledBroker(BrokerInstall.ServiceExe) is not { } installedBroker
                    || installedBroker < offeredBroker));
            LastVerdict = new AgentVerdict { Passed = verdict && !brokerOld, ServiceVersion = serviceVer, AgentVersion = agentVer, Source = "文件版本/" + agentSrc };
            tooltip = "当前程序 " + (selfVer.Length > 0 ? selfVer : "?")
                    + " · 服务 " + (serviceVer.Length > 0 ? serviceVer : "缺失") + " / 随包 " + (offeredBroker?.ToString(3) ?? "缺失")
                    + " · 注入代理 " + (agentVer.Length > 0 ? agentVer : "缺失") + " / 随包 " + (offeredAgent?.ToString(3) ?? "缺失")
                    + " · 最低代理版本 " + (requiredVer.Length > 0 ? requiredVer : "?");

            if (!TryGetServiceStatus(out var status))
            {
                detail = "首次使用，请安装服务";
                return EngineState.NotInstalled;
            }
            if (brokerOld || !verdict)
            {
                detail = brokerOld && !verdict ? "Windows 服务与注入代理需要更新"
                    : brokerOld ? "Windows 服务需要更新" : "注入代理需要更新";
                if (!SystemLauncher.IsElevated) detail += "。请在设置中选择「更新引擎组件」并确认管理员授权。";
                return brokerOld && !verdict ? EngineState.ComponentsNeedUpdate
                    : brokerOld ? EngineState.ServiceNeedsUpdate : EngineState.AgentNeedsUpdate;
            }
            if (status != ServiceControllerStatus.Running)
            {
                detail = "服务已安装，但未运行（" + status + "）";
                return EngineState.NotRunning;
            }

            // ★ P0-A（2026-11-24）：「引擎就绪」必须同时反映「通道是否真的连着」。
            //   判据 = 服务 Running + agent 文件存在 + 版本通过（上面已全过）+ **至少一个已投送账号的 relay 通道 IsConnected**。
            //   · 信号 = RelayClient.GetLiveSessions()：投送成功 Register() 且 IsConnected==true 的会话集合（断开即 Unregister）。
            //   · 没有配置自动投送账号（AutoConnectAccountIds 为空）→ 无从判断通道，仍按 Ready（纯手动场景不算 ChannelDown）。
            //   · 配置了自动投送、但连一个活通道都没有 → ChannelDown（引擎在跑，但通道断了/代理死了）。
            //   纯读取：RelayClient 是进程内静态注册表，O(会话数)、0 子进程、0 命名管道枚举。
            if (HasConfiguredAutoDeploy() && !HasAnyLiveChannel())
            {
                detail = "引擎在跑，但投送通道未建立（代理可能已退出）";
                return EngineState.ChannelDown;
            }

            detail = "就绪";
            return EngineState.Ready;
        }

        /// <summary>是否配置了自动投送账号（AutoConnectAccountIds 非空）。纯读配置。</summary>
        private static bool HasConfiguredAutoDeploy()
        {
            try { return App.Config.Settings.AutoConnectAccountIds?.Count > 0; }
            catch { return false; }
        }

        /// <summary>是否至少有一条「真的活着」的 relay 通道（IsConnected + AgentPid 存活）。纯读进程内注册表。</summary>
        private static bool HasAnyLiveChannel()
        {
            try { return RelayClient.CountLiveChannels() > 0; }
            catch { return false; }
        }

        public static Task<(bool ok, string message)> RepairAsync(bool allowElevation = true)
            => Task.Run(() => UpdateComponentsAsync(allowElevation));

        private static int _updating;

        private static async Task<(bool ok, string message)> UpdateComponentsAsync(bool allowElevation)
        {
            if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0)
                return (false, "正在更新引擎组件。");
            try
            {
                var state = Inspect(out _, out _);
                if (!RequiresUpdate(state)) return (true, "引擎组件已是最新。");
                bool elevated = SystemLauncher.IsElevated;
                if (!elevated && !allowElevation) return (false, "需要管理员权限才能更新引擎组件。");
                var previousBroker = EngineComponentVersions.ReadInstalledBroker(BrokerInstall.ServiceExe);
                string exe = SelfExe;
                string setup = Path.Combine(Path.GetDirectoryName(exe)!, "tools", "SoulLink.BrokerSetup.exe");
                if (!File.Exists(setup)) return (false, "缺少引擎更新工具，请使用完整程序包。");
                var start = new ProcessStartInfo(setup)
                {
                    UseShellExecute = true,
                    Verb = elevated ? "" : "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                start.ArgumentList.Add("--update");
                start.ArgumentList.Add(exe);
                using var process = Process.Start(start);
                if (process == null) return (false, "无法启动引擎更新工具。");
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode == 6) return (false, "Windows 服务暂时无法重启，稍后会重试。");
                if (process.ExitCode == 7) return (false, "代理包的版本标记无效，请重新下载完整程序包。");
                if (process.ExitCode == 8) return (false, "服务安装路径需要修复，请打开「安装 / 管理服务」。");
                if (process.ExitCode != 0) return (false, "引擎组件更新失败（退出码 " + process.ExitCode + "），稍后会重试。");
                var currentBroker = EngineComponentVersions.ReadInstalledBroker(BrokerInstall.ServiceExe);
                if (previousBroker != currentBroker)
                {
                    BrokerProtocol.InvalidateServiceVersionCache();
                    BrokerProtocol.NotifyServiceUpdated();
                }
                if (RequiresUpdate(Inspect(out _, out _))) return (false, "当前程序目录中的组件版本不足，请先更新主程序。");
                return (true, state == EngineState.AgentNeedsUpdate
                    ? "注入代理已更新，下次投送时启用新版。"
                    : "引擎组件已更新。");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return (false, "已取消管理员授权。");
            }
            catch (Exception ex)
            {
                LogBus.Warn("引擎组件更新失败", ex.Message);
                return (false, "暂时无法更新引擎组件，稍后会重试。");
            }
            finally { Interlocked.Exchange(ref _updating, 0); }
        }
    }
}
