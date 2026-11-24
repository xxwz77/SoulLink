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
        /// <summary>修复失败 / 状态异常。</summary>
        Error
    }

    /// <summary>
    /// 注入代理（C:\Program Files\SoulLinkDev\SoulLink.Agent.exe）的版本自检与自动修复。
    ///
    /// ★ 背景（用户痛点）：主程序升级后，安装目录里的 SoulLink.Agent.exe 仍是旧版
    ///   （解压覆盖时 agent 进程占用文件、覆盖失败），Broker 投送前 ValidAgent 校验
    ///   版本不符 → 投送全部失败，但界面却显示「引擎就绪」。普通用户无法理解。
    ///
    /// ★ 本类职责：
    ///   · Inspect：按随包 version 文件（AgentVersion）比较「服务要求」与「安装目录 agent 版本」，
    ///     给出 5 态结论（纯读取，零写入）；
    ///   · RepairAsync：版本不一致/缺失时，把发布包目录（主程序同目录）里的
    ///     SoulLink.Agent.exe 复制到安装目录，复制后复校验；
    ///   · 版本一致时**零写入、零提权**（硬要求，避免每次启动改写 Program Files）。
    ///
    /// ★ 只替换 agent 文件，**不重建/重装 Broker 服务**（用户明确要求：升级不碰服务）。
    ///   服务本体（SoulLink.exe）的日常版本同步由更新器整包覆盖完成，与本类无关。
    /// </summary>
    public static class AgentSync
    {
        public const string ServiceName = "SoulLink.DevBroker";
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
            && BrokerInstall.ValidAgent(BrokerInstall.ServiceExe, TargetAgent);

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
            string? serviceRaw = BrokerInstall.TryReadAgentVersion(BrokerInstall.ServiceExe, out string serviceSrc);
            string? requiredRaw = BrokerInstall.ReadRequiredAgentVersion(BrokerInstall.ServiceExe);
            string? agentRaw = BrokerInstall.TryReadAgentVersion(TargetAgent, out string agentSrc);
            string serviceVer = serviceRaw ?? "";
            string requiredVer = requiredRaw ?? "";
            string agentVer = agentRaw ?? "";
            bool verdict = BrokerInstall.ValidAgent(BrokerInstall.ServiceExe, TargetAgent);
            tooltip = "当前程序 " + (selfVer.Length > 0 ? selfVer : "?")
                    + " · 服务 " + (serviceVer.Length > 0 ? serviceVer : "缺失")
                    + " · 要求代理 " + (requiredVer.Length > 0 ? requiredVer : "?")
                    + " · 注入代理 " + (agentVer.Length > 0 ? agentVer : "缺失")
                    + " · 判据 " + (verdict ? "通过" : "不通过")
                    + "（依据：" + serviceSrc + "/" + agentSrc + "）";

            if (!TryGetServiceStatus(out var status))
            {
                detail = "首次使用，请安装服务";
                return EngineState.NotInstalled;
            }
            if (status != ServiceControllerStatus.Running)
            {
                detail = "服务已安装，但未运行（" + status + "）";
                return EngineState.NotRunning;
            }
            if (!File.Exists(TargetAgent))
            {
                detail = "注入代理缺失";
                return EngineState.AgentNeedsUpdate;
            }
            if (!verdict)
            {
                LastVerdict = new AgentVerdict { Passed = false, ServiceVersion = requiredVer, AgentVersion = agentVer, Source = serviceSrc + "/" + agentSrc };
                detail = "注入代理版本低于主程序要求：要求 " + (requiredVer.Length > 0 ? requiredVer : "?")
                       + "，代理 " + (agentVer.Length > 0 ? agentVer : "缺失");
                return EngineState.AgentNeedsUpdate;
            }
            LastVerdict = new AgentVerdict { Passed = true, ServiceVersion = serviceVer, AgentVersion = agentVer, Source = serviceSrc + "/" + agentSrc };
            detail = "就绪";
            return EngineState.Ready;
        }

        /// <summary>自动修复（后台线程执行，不阻塞 UI）。</summary>
        public static Task<(bool ok, string message)> RepairAsync()
            => Task.Run<(bool ok, string message)>(RepairCore);

        private static (bool ok, string message) RepairCore()
        {
            string source = SourceAgent;
            string target = TargetAgent;

            // ★ 版本一致 → 零写入，立即返回。
            if (IsAgentCurrent)
                return (true, "注入代理已是最新（" + AgentVersionOf(target) + "），无需修复。");

            if (!File.Exists(source))
                return (false, "发布目录缺少注入代理 SoulLink.Agent.exe，请重新解压完整程序包。");

            if (!SystemLauncher.IsElevated)
                return (false, "需要管理员权限才能更新注入代理。请以管理员身份运行本程序后再试。");

            try { Directory.CreateDirectory(Path.GetDirectoryName(target) ?? BrokerInstall.Root); }
            catch (Exception ex) { return (false, "无法创建安装目录：" + ex.Message); }

            string? lastError = null;
            bool copied = false;
            for (int attempt = 0; attempt < 5 && !copied; attempt++)
            {
                try
                {
                    File.Copy(source, target, true);
                    copied = true;
                }
                catch (IOException ex) { lastError = ex.Message; }
                catch (UnauthorizedAccessException ex) { lastError = ex.Message; }

                if (copied) break;

                // 目标文件被占用：按精确 PID 停掉占用它的 agent 进程，等待解锁后重试。
                // ★ 只停路径与本 target 完全一致的进程，不误伤其它版本（如稳定版）的代理。
                int stopped = StopAgentsLocking(target);
                if (attempt == 0) lastError += "（已尝试停止 " + stopped + " 个占用中的注入代理进程）";
                Thread.Sleep(600);
            }

            if (!copied)
                return (false, "注入代理文件被占用，多次重试仍无法替换。请关闭所有正在投送的账号后重试。"
                    + (lastError != null ? "（" + lastError + "）" : ""));

            // ★ P0-1：version 文件必须跟着 agent 走，否则装完仍读不到 AgentVersion。
            CopyAgentVersionMarker(source, target);

            // 复校验：按 version 文件判据，版本不满足不算成功。
            if (!BrokerInstall.ValidAgent(BrokerInstall.ServiceExe, target))
                return (false, "注入代理替换后版本校验失败：主程序要求 "
                    + (BrokerInstall.ReadRequiredAgentVersion(BrokerInstall.ServiceExe) ?? "?")
                    + "，实际 " + AgentVersionOf(target));

            return (true, "注入代理已更新到 " + AgentVersionOf(target) + "，无需重装服务。");
        }

        /// <summary>把发布包里的 version 标记文件随 agent 一并复制到安装目录（P0-1 + t7a 补 required）。</summary>
        private static void CopyAgentVersionMarker(string sourceAgent, string targetAgent)
        {
            // ① agent 自身版本标记（跟着 agent exe 走）
            string src = BrokerInstall.VersionFileFor(sourceAgent);
            string dst = BrokerInstall.VersionFileFor(targetAgent);
            if (!File.Exists(src))
            {
                LogBus.Warn("注入代理 version 标记缺失", "源 " + src + " 不存在，回退到 FileVersion 判据");
            }
            else
            {
                try { File.Copy(src, dst, true); }
                catch (Exception ex)
                {
                    // ★ G3：静默失败必须留痕（本类 P0 的同类病）。
                    LogBus.Warn("注入代理 version 标记复制失败", "源 " + src + " → 目标 " + dst + " :: " + ex.Message);
                }
            }

            // ② 主程序要求的 agent 最低版本标记（跟着服务本体 SoulLink.exe 走，t7a 补）
            string srcReq = BrokerInstall.RequiredVersionFileFor(sourceAgent);
            string dstReq = BrokerInstall.RequiredVersionFileFor(targetAgent);
            if (!File.Exists(srcReq))
            {
                LogBus.Warn("要求的代理版本标记缺失", "源 " + srcReq + " 不存在，将按「无要求 → 放行」处理");
                return;
            }
            try { File.Copy(srcReq, dstReq, true); }
            catch (Exception ex)
            {
                LogBus.Warn("要求的代理版本标记复制失败", "源 " + srcReq + " → 目标 " + dstReq + " :: " + ex.Message);
            }
        }

        /// <summary>停止占用指定 agent 文件的进程（按精确 PID；只匹配同一路径的进程）。</summary>
        private static int StopAgentsLocking(string target)
        {
            int stopped = 0;
            try
            {
                string full = Path.GetFullPath(target);
                string procName = Path.GetFileNameWithoutExtension(AgentFileName);
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    try
                    {
                        string? path = null;
                        try { path = p.MainModule?.FileName; } catch { }
                        // 读到路径且不是本目标 → 别的版本/目录的代理，不碰。
                        if (path != null && !string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase))
                            continue;
                        p.Kill();
                        try { p.WaitForExit(3000); } catch { }
                        stopped++;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return stopped;
        }
    }
}
