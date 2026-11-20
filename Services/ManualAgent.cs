using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SoulLink.Services
{
    /// <summary>
    /// 手动启动的注入代理。
    ///
    /// ★ 为什么需要它：
    ///   自动投送要跨会话创建进程（SYSTEM 计划任务 或 Broker 服务），
    ///   这条链路上任何一环出问题（权限、令牌、服务、杀软、并发生成代理）
    ///   都会表现为"按侧键没反应"，而且排查要跨会话、看多处日志。
    ///   手动代理是用户在目标会话里**自己双击启动**的，天然就在正确的会话和桌面上，
    ///   不依赖任何提权或跨会话机制，出问题时日志也就在眼前。
    ///
    /// 用法：在目标账号的远程桌面里双击 SoulLink.exe 并加 --agent
    ///       （或直接运行 SoulLink.Agent.exe，它已内置该默认行为）。
    ///
    /// 管道发现：宿主侧管道名是 &lt;身份&gt;_Relay_&lt;会话号&gt;_&lt;随机8位&gt;。
    ///   代理读自己的会话号，只在 \\.\pipe\ 里找带这个会话号的那条，
    ///   所以不需要用户输入，也不会连到别的账号。
    /// </summary>
    public static class ManualAgent
    {
        /// <summary>手动模式下等待宿主管道出现的默认时长。用户在目标会话里启动后，宿主可能还没建好管道。</summary>
        public const int DefaultWaitSeconds = 60;

        /// <summary>
        /// 在 \\.\pipe\ 里找当前会话对应的宿主管道。
        /// 返回管道名（不含 _Events 后缀），找不到返回 null。
        /// </summary>
        public static string? FindHostPipe(int sessionId, out string detail)
        {
            detail = "";
            if (sessionId <= 0) { detail = "无效的会话号"; return null; }

            string prefix = AppIdentity.RelayPipePrefix;               // 形如 "SoulLink_Relay_"
            string want = prefix + sessionId + "_";                    // 形如 "SoulLink_Relay_6_"
            var hits = new List<string>();

            try
            {
                foreach (var raw in Directory.GetFiles(@"\\.\pipe\"))
                {
                    string name = Path.GetFileName(raw);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith(AppIdentity.RelayEventSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!name.StartsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
                    // 后缀必须是 8 位十六进制，避免把 SoulLink_Relay_6x_... 误认成会话 6
                    string tail = name.Substring(want.Length);
                    if (tail.Length != 8 || !tail.All(IsHex)) continue;
                    hits.Add(name);
                }
            }
            catch (Exception ex)
            {
                detail = "枚举管道失败：" + ex.Message;
                return null;
            }

            if (hits.Count == 0)
            {
                detail = "会话 " + sessionId + " 没有等待中的宿主管道（请先在 SoulLink 主程序里对该账号连接或投送通道）";
                return null;
            }

            // 同一会话理论上只会有一条；万一有多条，取字典序最小的，行为确定。
            hits.Sort(StringComparer.OrdinalIgnoreCase);
            detail = hits.Count == 1
                ? "找到宿主管道 " + hits[0]
                : "找到 " + hits.Count + " 条候选管道，选用 " + hits[0] + "（其余：" + string.Join(", ", hits.Skip(1)) + "）";
            return hits[0];
        }

        private static bool IsHex(char c)
            => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        /// <summary>
        /// 手动代理入口。参数：
        ///   --agent [--pipe &lt;管道名&gt;] [--wait &lt;秒&gt;] [--identity=&lt;名字&gt;]
        /// 未给 --pipe 时按当前会话号自动发现；未发现则最多等待 --wait 秒。
        /// </summary>
        public static int Run(string[] args)
        {
            int sessionId = Process.GetCurrentProcess().SessionId;
            string? explicitPipe = ReadOption(args, "--pipe");
            int waitSeconds = DefaultWaitSeconds;
            string? waitText = ReadOption(args, "--wait");
            if (int.TryParse(waitText, out int w) && w >= 0 && w <= 3600) waitSeconds = w;

            // ① 抢占"本会话手动代理"标记：已有代理在跑就直接退出。
            //    ★ 必须放在最前面。否则第二个代理会照常启动、等宿主断开才退出，
            //      用户看不出发生了什么，而两个代理争用同一桌面输入正是
            //      "操作异常 / 视角乱跳"的成因。
            if (!ClaimManualAgent(AppIdentity.Name, sessionId, out string claimReason))
            {
                Log(claimReason + "，本次退出，避免争用同一桌面输入");
                return 4;
            }
            Log("已取得本会话手动代理标记 · 会话 " + sessionId);

            // ② 已显式指定管道：直接交给 relay 实现。
            if (!string.IsNullOrWhiteSpace(explicitPipe))
            {
                Log("使用指定管道 " + explicitPipe);
                return Relay.RelayAgent.Run(new[] { "--relay", explicitPipe!.Trim() });
            }

            // ③ 自动发现：给宿主一点时间把管道建好（用户常先开代理再点连接）。
            var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
            string? pipe = null;
            string detail = "";
            bool logged = false;
            while (DateTime.UtcNow < deadline)
            {
                pipe = FindHostPipe(sessionId, out detail);
                if (pipe != null) break;
                if (!logged) { Log("等待宿主管道 · 会话 " + sessionId + " · " + detail); logged = true; }
                System.Threading.Thread.Sleep(700);
            }

            if (pipe == null)
            {
                Log("放弃：" + detail);
                return 3;
            }

            Log("会话 " + sessionId + " · " + detail);
            return Relay.RelayAgent.Run(new[] { "--relay", pipe });
        }

        private static string? ReadOption(string[] args, string key)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length ? args[i + 1] : "";
                if (args[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring(key.Length + 1);
            }
            return null;
        }

        /// <summary>
        /// 导出可直接双击运行的手动代理到指定目录。
        /// 用主程序副本改名为 SoulLink.Agent.exe 并改写清单为 asInvoker，
        /// 这样普通用户令牌也能启动（主程序本身是 requireAdministrator）。
        /// </summary>
        public static (bool ok, string message) Export(string targetDirectory)
        {
            try
            {
                string self = SystemLauncher.ResolveSelfExe();
                if (string.IsNullOrWhiteSpace(self) || !File.Exists(self))
                    return (false, "找不到 SoulLink.exe 本体");

                if (string.IsNullOrWhiteSpace(targetDirectory))
                    return (false, "未指定导出目录");
                Directory.CreateDirectory(targetDirectory);

                string target = Path.Combine(targetDirectory, "SoulLink.Agent.exe");
                // 已存在且内容一致就不用重复写（避免占用中的文件写失败）
                if (File.Exists(target))
                {
                    try
                    {
                        var a = new FileInfo(self); var b = new FileInfo(target);
                        if (a.Length == b.Length) return (true, "已存在同尺寸代理：" + target);
                    }
                    catch { }
                }
                if (File.Exists(target)) { try { File.SetAttributes(target, FileAttributes.Normal); File.Delete(target); } catch { } }

                File.Copy(self, target, true);
                if (!ManifestPatcher.MakeAsInvoker(target))
                {
                    byte[] bytes = File.ReadAllBytes(target);
                    if (!System.Text.Encoding.ASCII.GetString(bytes).Contains("level=\"asInvoker\""))
                        return (false, "代理清单改写失败，生成的文件无法以普通权限启动");
                }
                return (true, "已生成：" + target);
            }
            catch (Exception ex) { return (false, "生成代理失败：" + ex.Message); }
        }

        // ── 进程识别 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 手动代理的会话级互斥体名。代理持有它表示"本会话已有手动代理"。
        ///
        /// ★ 为什么用互斥体而不是解析命令行：
        ///   手动代理和 Broker 自动代理的文件名都是 SoulLink.Agent.exe，
        ///   只能靠命令行里的 --relay 区分。但跨进程读命令行要解析 PEB，
        ///   不同 Windows 版本布局不同，实测极难稳定（读到过 36312 这种假 Length，
        ///   导致命令行被悄悄截断、判定出错）。命名互斥体由内核维护，
        ///   存在性判断是一步原子操作，跨会话可见，且代理退出时自动释放，不会残留。
        /// </summary>
        public static string ManualAgentMutexName(string identityName, int sessionId)
            => @"Global\" + identityName + ".ManualAgent." + sessionId + ".v1";

        /// <summary>当前进程持有的手动代理互斥体（代理进程存活期间一直持有）。</summary>
        private static System.Threading.Mutex? _manualMutex;

        /// <summary>
        /// 声明"本进程就是本会话的手动代理"。抢占失败说明已有别人在跑。
        /// 返回 true = 抢占成功（继续启动）；false = 已有手动代理。
        /// </summary>
        public static bool ClaimManualAgent(string identityName, int sessionId, out string detail)
        {
            detail = "";
            try
            {
                // initiallyOwned: true → 若已存在同名互斥体，会立刻抛出，达到"抢占"语义。
                var mutex = new System.Threading.Mutex(true, ManualAgentMutexName(identityName, sessionId), out bool created);
                if (!created)
                {
                    mutex.Dispose();
                    detail = "会话 " + sessionId + " 已有手动代理在运行";
                    return false;
                }
                _manualMutex = mutex;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                detail = "会话 " + sessionId + " 已有手动代理在运行（互斥体被占用）";
                return false;
            }
            catch (Exception ex)
            {
                detail = "互斥体创建异常：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 某会话是否有用户手动启动的代理在跑（宿主侧判断，用于"手动优先"）。
        /// 通过尝试打开同名互斥体实现：能打开说明有人持有。
        /// </summary>
        public static bool HasManualAgent(int sessionId, string identityName)
        {
            if (sessionId <= 0) return false;
            try
            {
                if (!System.Threading.Mutex.TryOpenExisting(ManualAgentMutexName(identityName, sessionId), out var m))
                    return false;
                m.Dispose();
                return true;
            }
            catch { return false; }
        }

        /// <summary>列出当前有手动代理的会话号（诊断用）。</summary>
        public static System.Collections.Generic.List<int> FindManualAgentSessions(string identityName, int maxSession = 256)
        {
            var list = new System.Collections.Generic.List<int>();
            for (int s = 1; s <= maxSession; s++)
                if (HasManualAgent(s, identityName)) list.Add(s);
            return list;
        }



        private static void Log(string message)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(),
                    "soullink-agent-" + Process.GetCurrentProcess().Id + ".log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
    }
}
