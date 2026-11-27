using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>
    /// 挂机会话瘦身：清理 RDP 会话里与游戏/挂机无关的外围进程，
    /// 并可针对配置中的账号做注册表级的启动项优化。
    ///
    /// ★★ 为什么要做：★★
    ///   Windows 每个会话首次登录会自动拉起一批外围组件，实测单个会话
    ///   63 个进程 / 2.79 GB，其中游戏与必需进程只占约 250 MB。
    ///   挂机时没人看这些，纯属浪费内存、CPU 与电量。
    ///
    /// ★★ 安全边界（务必遵守）：★★
    ///   1. **白名单制**：只结束明确列出的进程名，其余一律不动。
    ///   2. **绝不碰** explorer / dwm / csrss / winlogon / svchost / SoulLink 自身。
    ///   3. **输入法保留**：ctfmon / ChsIME / TextInputHost / TabTip。
    ///      挂机时用不到，但用户随时可能连进去操作，杀了他会立刻发现打不了字。
    ///   4. **注册表只动配置里的账号**：见 ApplyStartupPolicy。
    ///   5. **不杀会自动重启的**：SearchHost / Widgets 杀了 3 秒后自己回来，
    ///      只能靠注册表根治，单纯杀毫无意义（实测）。
    ///   6. 本类**不引用 System.Management**（项目未引入该包），
    ///      所有进程信息走 Process API，注册表走 reg.exe 子进程。
    public static partial class SessionSlimmer
    {
        /// <summary>可安全结束、且不会自动重启的进程名。</summary>
        private static readonly string[] SafeToKill =
        {
            // 手机连接 / 跨设备（实测不会立刻重启）
            "PhoneExperienceHost",
            "CrossDeviceService",
            "CrossDeviceResume",
            // 游戏栏（挂机不需要，且会在后台常驻）
            "GameBar",
            "GameBarFTServer",
            "GameBarPresenceWriter",
        };

        public sealed record OptionalProcess(string Name, string Label, string Impact);
        public static IReadOnlyList<OptionalProcess> OptionalProcesses { get; } = new[]
        {
            new OptionalProcess("msedge", "Edge 浏览器", "会关闭该账号的 Edge 标签页及后台任务。"),
            new OptionalProcess("OneDrive", "OneDrive", "会中断该账号正在进行的文件同步。"),
            new OptionalProcess("ms-teams", "Microsoft Teams", "会退出该账号的 Teams，通话和通知会中断。"),
        };

        // ★★ 实测「杀了会自动重启」—— 清理无效，只能靠注册表策略 ★★
        //
        //   TextInputHost   杀后 20 秒内自动回来（77 MB）
        //   ShellHost       杀后 20 秒内自动回来（54 MB）
        //   SearchHost      同上（112 MB）
        //   Widgets         同上（54 MB）
        //
        //   这些不放进清理名单：杀了立刻重启，不仅没省下内存，
        //   反而多一次进程创建的 CPU 开销。界面上显示在
        //   「需启动项优化」一栏，靠注册表根治。

        // ★★ 明确不清理的「看起来无用但其实危险」的进程 ★★
        //
        //   以下这些在单个会话里也有实例，看似可以清，实测风险很高：
        //
        //   nvcontainer / NVDisplay.Container
        //     NVIDIA 驱动框架的宿主。虽然会话里也有一份，但杀掉会导致
        //     驱动框架重启 —— 游戏画面黑屏、DXGI 设备丢失。
        //     显存回收根本不会因为杀它而变快。
        //
        //   RtkAudUService64
        //     音频服务。挂机确实不出声，但杀掉可能让游戏音频初始化失败，
        //     部分游戏会因此卡在加载界面。
        //
        //   FileCoAuth
        //     OneDrive 文件协作。杀掉会触发一连串重试与重新拉起。
        //
        //   AppActions
        //     系统应用操作代理。它会随系统 UI 自动重启，杀了没有净收益。

        /// <summary>明确不清理的高风险进程（供界面说明）。</summary>
        public static readonly string[] RiskyNotRecommended =
        {
            "nvcontainer", "NVDisplay.Container",
            "RtkAudUService64", "FileCoAuth", "AppActions",
        };

        /// <summary>内置可清理进程名（始终生效）。</summary>
        public static IReadOnlyList<string> BuiltInKillNames => SafeToKill;

        /// <summary>系统保护名单（只读，供界面展示）。</summary>
        public static IReadOnlyList<string> ProtectedNames => NeverKill.OrderBy(x => x).ToList();

        /// <summary>
        /// 当前生效的清理名单。
        ///
        /// ★ 由三部分组成：
        ///   ① 内置名单 SafeToKill
        ///   ② 减去用户在设置里**移除**的项（SessionSlimDisabled）
        ///   ③ 加上用户**追加**的项（SessionSlimKillList）
        ///   保护名单 NeverKill 优先级最高，任何来源都不会让它生效。
        ///
        /// ★ 内置项也允许移除 —— 用户明确要求"默认选中的东西也要能去掉"。
        ///   原来内置项在界面上没有移除按钮，等于绑架用户。
        /// </summary>
        public static List<string> EffectiveKillNames() => EffectiveKillNameSet().ToList();

        private static readonly Lock KillListGate = new();

        private static HashSet<string> EffectiveKillNameSet()
        {
            lock (KillListGate)
            {
                var settings = App.Config?.Settings;
                IEnumerable<string> custom = settings?.SessionSlimKillList ?? Enumerable.Empty<string>();
                var effective = SafeToKill.Concat(custom)
                    .Select(Normalize)
                    .Where(name => name.Length > 0 && !IsProtected(name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (settings?.SessionSlimDisabled != null)
                    effective.ExceptWith(settings.SessionSlimDisabled.Select(Normalize));
                return effective;
            }
        }

        /// <summary>该项是否属于内置（仅供界面标注，判断的是"原始名单"）。</summary>
        public static bool IsBuiltIn(string name)
        {
            string n = Normalize(name);
            return SafeToKill.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>从清理名单移除一项（内置的会记入 disabled，自定义的直接删）。</summary>
        public static bool RemoveFromList(string name)
        {
            string n = Normalize(name);
            if (n.Length == 0) return false;
            try
            {
                bool changed;
                lock (KillListGate)
                {
                    var cfg = App.Config?.Settings;
                    if (cfg == null) return false;
                    changed = cfg.SessionSlimKillList?.RemoveAll(
                        x => string.Equals(Normalize(x), n, StringComparison.OrdinalIgnoreCase)) > 0;
                    if (IsBuiltIn(n))
                    {
                        cfg.SessionSlimDisabled ??= new List<string>();
                        if (!cfg.SessionSlimDisabled.Any(x => string.Equals(Normalize(x), n, StringComparison.OrdinalIgnoreCase)))
                        {
                            cfg.SessionSlimDisabled.Add(n);
                            changed = true;
                        }
                    }
                }
                if (changed) ConfigStore.Save(App.Config);
                return changed;
            }
            catch { return false; }
        }

        /// <summary>恢复默认：清空自定义追加项与内置禁用项。</summary>
        public static void ResetList()
        {
            try
            {
                lock (KillListGate)
                {
                    var cfg = App.Config?.Settings;
                    if (cfg == null) return;
                    cfg.SessionSlimKillList = new List<string>();
                    cfg.SessionSlimDisabled = new List<string>();
                }
                ConfigStore.Save(App.Config);
            }
            catch { }
        }

        /// <summary>规范化进程名：去空格、去 .exe 后缀。</summary>
        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim();
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);
            return s;
        }

        /// <summary>这个名字是否受保护（不允许清理）。</summary>
        public static bool IsProtected(string name)
        {
            string normalized = Normalize(name);
            if (NeverKill.Contains(normalized) || NeedsPolicyFixes.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return true;
            return App.Config?.Settings?.BgiPaths?.Values.Any(path =>
                string.Equals(Normalize(Path.GetFileName(path)), normalized, StringComparison.OrdinalIgnoreCase)) == true;
        }

        public static bool AddToList(string name)
        {
            string normalized = Normalize(name);
            lock (KillListGate)
            {
                var settings = App.Config?.Settings;
                if (settings == null || normalized.Length == 0 || IsProtected(normalized)) return false;
                settings.SessionSlimDisabled?.RemoveAll(item => string.Equals(Normalize(item), normalized, StringComparison.OrdinalIgnoreCase));
                if (EffectiveKillNameSet().Contains(normalized)) return true;
                settings.SessionSlimKillList ??= new List<string>();
                settings.SessionSlimKillList.Add(normalized);
                return true;
            }
        }

        /// <summary>
        /// 会自动重启、杀进程无意义的外围组件。
        /// 仅用于「统计可优化空间」，实际清理要靠注册表策略。
        /// </summary>
        private static readonly string[] NeedsPolicyFixes =
        {
            "SearchHost", "Widgets", "WidgetService",
            // 实测会自动重启的两个（2026-11-21 验证，20 秒内回来）
            "TextInputHost", "ShellHost",
        };

        /// <summary>绝对不能结束的进程名。即使配置写错也不动。</summary>
        private static readonly HashSet<string> NeverKill = new(StringComparer.OrdinalIgnoreCase)
        {
            // 系统关键
            "explorer", "dwm", "csrss", "winlogon", "wininit", "services", "lsass",
            "svchost", "System", "Idle", "smss", "fontdrvhost",
            "ShellExperienceHost", "StartMenuExperienceHost", "sihost", "runtimebroker", "taskhostw",
            "dllhost", "conhost", "LockApp", "LogonUI", "WmiPrvSE",
            "UserOOBEBroker", "FileCoAuth", "AppActions", "msedgewebview2", "WidgetsPlatformRuntime",
            "nvcontainer", "NVDisplay.Container", "RtkAudUService64", "audiodg", "MsMpEng", "NisSrv",
            // RDP 基础设施
            "rdpclip", "rdpinput", "RdpInput", "TermSrv",
            // 本程序
            "SoulLink", "SoulLink.Agent", "SoulLink-Stable", "SoulLink.Updater", "SoulLink.Broker",
            "SoulLink.TaskEdit", "sl-watchdog", "sl-rescue",
            "BetterGI", "BetterGenshinImpact", "ABGI", "YuanShen", "GenshinImpact", "YuanShenCloudGame",
            "ctfmon", "TabTip", "InputApp", "ChsIME", "ChtIME",
        };

        /// <summary>瘦身结果。</summary>
        public sealed class Result
        {
            public int Killed { get; set; }
            public long FreedBytes { get; set; }
            public List<string> Names { get; } = new();
            public string Message { get; set; } = "";
            public bool Any => Killed > 0;
        }

        // ── 统计 ────────────────────────────────────────────────────────

        /// <summary>会话进程概况。</summary>
        public sealed class SessionStat
        {
            public int SessionId { get; set; }
            public bool OwnerVerified { get; set; }
            public int Total { get; set; }
            public long TotalBytes { get; set; }
            /// <summary>可立即清理（白名单内、不会重启）。</summary>
            public int Killable { get; set; }
            public long KillableBytes { get; set; }
            public int Unverified { get; set; }
            /// <summary>需注册表策略才能优化的（会重启）。</summary>
            public int PolicyOnly { get; set; }
            public long PolicyOnlyBytes { get; set; }
            public Dictionary<string, (int Count, long Bytes)> KillableDetail { get; } = new(StringComparer.OrdinalIgnoreCase);
            internal List<SoulLink.Models.RemoteProcess> CleanupTargets { get; } = new();
        }

        // ── 注册表策略（仅限配置中的账号）────────────────────────────────

        /// <summary>单个账户的策略应用结果。</summary>
        public sealed class PolicyResult
        {
            public string UserName { get; set; } = "";
            public bool Ok { get; set; }
            public string Message { get; set; } = "";
        }

        public static PolicyResult ApplyStartupPolicy(string userName)
        {
            var result = new PolicyResult { UserName = userName };
            if (!CanSelectUser(SessionService.BareName(userName)) || !SystemLauncher.IsElevated)
            {
                result.Message = "需要管理员权限和已配置的挂机账号。";
                return result;
            }

            try
            {
                string bare = SessionService.BareName(userName);
                if (IsUserLoggedOn(bare))
                {
                    result.Message = "账号已登录。启用自动应用后，下次代理启动时生效。";
                    return result;
                }

                var account = userName.Contains('\\') ? new NTAccount(userName) : new NTAccount(Environment.MachineName, bare);
                var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
                using var profile = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid.Value);
                if (profile?.GetValue("ProfileImagePath") is not string directory)
                {
                    result.Message = "未找到该账号的本机配置文件。";
                    return result;
                }

                string hiveFile = Path.Combine(directory, "NTUSER.DAT");
                string mount = "SoulLink_" + Guid.NewGuid().ToString("N");
                string mountKey = @"HKU\" + mount;
                var status = RunReg(new[] { "load", mountKey, hiveFile });
                if (status.ExitCode != 0) { result.Message = "读取用户配置失败：" + status.Output; return result; }
                try
                {
                    using var hive = Microsoft.Win32.Registry.Users.OpenSubKey(mount, writable: true)
                        ?? throw new IOException("无法打开已挂载的配置。");
                    int changed = EdgeStartupPolicy.ApplyForUser(hive, true);
                    result.Ok = true;
                    result.Message = changed > 0 ? "已设置 Edge 启动策略，下次登录生效。" : "已有策略保持原值。";
                }
                finally { TryUnload(mountKey); }
            }
            catch (Exception ex) { result.Message = "应用失败：" + ex.Message; }
            return result;
        }

        /// <summary>
        /// 该账户当前是否已登录（有活动会话）。
        ///
        /// ★ 判定依据：会话列表里出现该用户名且会话号 > 0。
        ///   用户已登录时 NTUSER.DAT 被独占，reg load 会失败，
        ///   所以提前拦住并给出明确提示，而不是让底层报错。
        /// ★ 判断本身失败时**返回 true**（保守）：宁可拒绝操作，
        ///   也不要在状态不明时去改别人的注册表。
        /// </summary>
        public static bool IsUserLoggedOn(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return true;
            try
            {
                var sessions = SessionService.QueryAsync().GetAwaiter().GetResult();
                foreach (var s in sessions)
                {
                    if (s == null || s.Id <= 0) continue;
                    if (string.Equals(s.UserName, userName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch { return true; }
        }

        // ── 机器级策略（HKLM，class=Machine，全机生效）──────────────────

        public static PolicyResult ApplyMachinePolicy(bool enable)
        {
            var result = new PolicyResult { UserName = "(本机)" };
            if (!SystemLauncher.IsElevated) { result.Message = "需要管理员权限。"; return result; }
            try
            {
                var machine = Microsoft.Win32.Registry.LocalMachine;
                const string marker = @"SOFTWARE\SoulLink\SessionSlim\Machine";
                int changed = EdgeStartupPolicy.ApplyOwned(machine, @"SOFTWARE\Policies\Microsoft\Dsh",
                    marker, new[] { ("DisableWidgetsBoard", 1), ("AllowNewsAndInterests", 0) }, enable);
                changed += EdgeStartupPolicy.ApplyOwned(machine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                    marker, new[] { ("DisableSearch", 1) }, enable);
                result.Ok = true;
                result.Message = changed == 0 ? "已有系统策略保持原值。"
                    : enable ? "已写入小组件与搜索策略。Windows 可能在重新登录后应用。"
                    : "已撤销本工具写入且未被修改的策略。";
            }
            catch (Exception ex) { result.Message = "设置机器策略失败：" + ex.Message; }
            return result;
        }

        /// <summary>机器级策略当前是否已启用（只读探测 DisableWidgetsBoard）。</summary>
        public static bool IsMachinePolicyEnabled()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Policies\Microsoft\Dsh");
                var v = k?.GetValue("DisableWidgetsBoard");
                return v != null && int.TryParse(v.ToString(), out int i) && i != 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 用户级 Edge 预启动策略（HKCU，class=User）。由**代理在目标会话内**调用，
        /// 写当前进程（代理）自己的 HKCU —— 天然就是目标账号，绕开「宿主挂载已登录
        /// 用户 NTUSER.DAT 会失败」的死结（路径A）。
        ///
        /// ★ 幂等 + 写失败静默降级：这个方法由后台代理在启动时调用，绝不抛异常、
        ///   绝不阻塞代理进入注入循环；失败只记一行日志。
        /// ★ 由配置 SessionSlimUserPolicyEnabled 决定是否执行（主程序投送时经环境变量下发）。
        /// </summary>
        /// <param name="enable">true=写入禁用；false=删值恢复默认。</param>
        public static void ApplyUserPolicy(bool enable)
        {
            // ★ t7a：实现已抽取到 Services/EdgeStartupPolicy.cs（主程序与无 UI 代理共用同一份真实实现）。
            EdgeStartupPolicy.ApplyUserPolicy(enable);
        }

        // ── 内部工具 ────────────────────────────────────────────────────

        private static void TryUnload(string mountKey)
        {
            try { RunReg(new[] { "unload", mountKey }); } catch { }
        }

        private sealed class RegOut { public int ExitCode; public string Output = ""; }

        private static RegOut RunReg(string[] args)
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
            var o = p?.StandardOutput.ReadToEnd() ?? "";
            var e = p?.StandardError.ReadToEnd() ?? "";
            p?.WaitForExit(8000);
            return new RegOut
            {
                ExitCode = p?.ExitCode ?? -1,
                Output = (o + " " + e).Trim()
            };
        }
    }
}
