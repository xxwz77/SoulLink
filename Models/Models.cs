using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SoulLink.Models
{
    /// <summary>动作类型</summary>
    public enum ActionKind
    {
        Launch = 0,
        Close = 1,
        Restart = 2
    }

    /// <summary>
    /// 一条“快捷动作”：绑定某个账号 + 某个程序 + 打开/关闭。
    /// 对应批处理里的 TARGET / TARGET_EXE 两行。
    /// </summary>
    public class ActionItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "新动作";
        public string AccountId { get; set; } = "";
        /// <summary>程序完整路径；Close 时也可留空表示按路径匹配规则</summary>
        public string ExePath { get; set; } = "";
        /// <summary>启动参数（Launch / Restart 时附加）</summary>
        public string Arguments { get; set; } = "";
        public ActionKind Kind { get; set; } = ActionKind.Launch;
        /// <summary>延时秒数，0 表示立即</summary>
        public int DelaySeconds { get; set; } = 0;

        [JsonIgnore]
        public bool IsClose => Kind == ActionKind.Close;
    }

    /// <summary>账号：user + password + 可选的固定会话 ID</summary>
    public class Account
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string UserName { get; set; } = "";
        /// <summary>DPAPI 加密后的密码（Base64）</summary>
        public string PasswordCipher { get; set; } = "";
        /// <summary>0 = 自动查找会话</summary>
        public int SessionId { get; set; } = 0;
        public string Note { get; set; } = "";
        /// <summary>用户列表持久化排序；拖动时重排为连续值。</summary>
        public int SortOrder { get; set; } = 0;

        // ── 内嵌 RDP 控制台用的连接信息 ────────────────────────────────
        /// <summary>
        /// RDP 连接地址。留空则自动探测（见 RdpHostProbe）：
        /// 本机所有环回 RDP 客户端在服务端看来都是 127.0.0.1，靠“会话号推地址”并不可靠，
        /// 必须以实际能连上、且落点正确的地址为准。
        /// </summary>
        public string RdpHost { get; set; } = "";
        public int RdpPort { get; set; } = 3389;
        /// <summary>登录名（RDP 里的大小写常与 Windows 账户不同，比如 A2）</summary>
        public string RdpUserName { get; set; } = "";
        /// <summary>显示名 / 标签页标题</summary>
        public string DisplayName { get; set; } = "";
        /// <summary>自定义图标（PNG 的 base64），为空则用首字母色块</summary>
        public string IconBase64 { get; set; } = "";

        // ── 每用户独立的画面参数 ──────────────────────────────────
        // ★ 默认值直接给具体数值（不再用 0 表示"跟随全局"）：
        //   这些是每个账号自己的连接参数，不该依赖全局设置，
        //   否则新建账号后还要逐个调整（用户明确要求：新建即为这套值）。
        public int RdpWidth { get; set; } = 1920;
        public int RdpHeight { get; set; } = 1080;
        /// <summary>0=跟随窗口 1=拉伸 2=原大小</summary>
        public int RdpScaleMode { get; set; } = 1;
        /// <summary>色深：16 位在挂机场景下带宽更省、帧率更稳</summary>
        public int RdpColorDepth { get; set; } = 16;
        public bool RdpAutoDisconnect { get; set; } = true;
        public bool RdpLowBandwidth { get; set; } = false;
        /// <summary>音频输出：0=本机播放 1=远端播放 2=禁用</summary>
        public int RdpAudioMode { get; set; } = 0;
        /// <summary>把本机默认录音设备重定向到远端</summary>
        public bool RdpAudioCapture { get; set; } = false;
        /// <summary>NetworkConnectionType：0=自动，1~6=调制解调器到 LAN</summary>
        public int RdpNetworkConnectionType { get; set; } = 6;
        /// <summary>性能预设：0=自动 1=省带宽 2=均衡 3=高画质</summary>
        public int RdpPerformanceMode { get; set; } = 3;
        /// <summary>组合键：0=本机 1=远端 2=仅全屏时远端</summary>
        public int RdpKeyboardHookMode { get; set; } = 1;
        public bool RdpRedirectPrinters { get; set; } = false;
        public bool RdpRedirectClipboard { get; set; } = false;
        public bool RdpRedirectSmartCards { get; set; } = false;
        public bool RdpRedirectPorts { get; set; } = false;
        public bool RdpRedirectDrives { get; set; } = false;

        [JsonIgnore]
        public string Caption => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : UserName;

        /// <summary>RDP 登录名：没单独填就用 Windows 用户名</summary>
        [JsonIgnore]
        public string EffectiveRdpUserName => string.IsNullOrWhiteSpace(RdpUserName) ? UserName : RdpUserName;

        /// <summary>
        /// 地址解析：优先用显式配置；没配置时用探测结果 <paramref name="probedHost"/>；
        /// 都没有才退回按会话号猜（并明确标记为不可靠）。
        /// </summary>
        public string ResolveHost(int sessionId, string? probedHost = null)
        {
            if (!string.IsNullOrWhiteSpace(RdpHost)) return RdpHost.Trim();
            if (!string.IsNullOrWhiteSpace(probedHost)) return probedHost!.Trim();
            return sessionId > 0 && sessionId < 255 ? "127.0.0." + sessionId : "127.0.0.1";
        }
    }

    public class AppSettings
    {

        /// <summary>允许在找不到会话时回退到当前用户会话</summary>
        public bool AllowCurrentSessionFallback { get; set; } = true;
        /// <summary>服务端发起的动作也执行（CLI / IPC 调用）</summary>
        public bool AllowRemoteInvoke { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public string LastExportPath { get; set; } = "";

        // ── 开机自启 ────────────────────────────────────────────────────
        /// <summary>
        /// 自启任务绑定的 Windows 账户（该账户登录时启动程序）。
        /// 空 = 使用当前登录用户。
        /// </summary>
        public string AutoStartUser { get; set; } = "";

        /// <summary>
        /// 启动后自动连接 RDP 的账号 Id 列表（可多选）。
        /// 与 AutoStartUser 无关：前者是谁来启动程序，这里是启动后连哪些 RDP 用户。
        /// </summary>
        public System.Collections.Generic.List<string> AutoConnectAccountIds { get; set; }
            = new System.Collections.Generic.List<string>();

        // ── 内嵌 RDP 控制台 ────────────────────────────────────────────
        /// <summary>控制台默认分辨率</summary>
        public int RdpWidth { get; set; } = 1280;
        public int RdpHeight { get; set; } = 720;
        /// <summary>色深</summary>
        public int RdpColorDepth { get; set; } = 32;
        /// <summary>连接前自动把目标会话踢成 Disc，从而复用真实挂机会话（HANDOFF 10.3）</summary>
        public bool RdpAutoDisconnectFirst { get; set; } = true;
        /// <summary>缩放模式 0=跟随窗口 1=拉伸 2=原大小</summary>
        public int RdpScaleMode { get; set; } = 1;
        /// <summary>30 位色/字体平滑等性能标志</summary>
        public bool RdpLowBandwidth { get; set; } = false;
        /// <summary>自由视角灵敏度倍率（高 DPI 鼠标调大）</summary>
        public double LookSensitivity { get; set; } = 4.0;
        /// <summary>鼠标锁定开关热键，例如 "Ctrl+Alt+Q"</summary>
        public string LockHotkey { get; set; } = "Ctrl+Alt+Q";
        /// <summary>0=跟随系统 1=浅色 2=深色</summary>
        public int ThemeMode { get; set; } = 0;
        /// <summary>true=点击 RDP 画面才接收键鼠，移出画面立即释放</summary>
        public bool RdpCaptureOnClick { get; set; } = false;
    }

    public class AutomationProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "新建配置组";
        public List<string> AccountIds { get; set; } = new List<string>();
        public List<string> ActionIds { get; set; } = new List<string>();
        public bool Enabled { get; set; }
        public string DailyTime { get; set; } = "08:00";
        public string LastRun { get; set; } = "";
    }

    public class AppConfig
    {
        public int Version { get; set; } = 1;
        public AppSettings Settings { get; set; } = new AppSettings();
        public List<Account> Accounts { get; set; } = new List<Account>();
        public List<ActionItem> Actions { get; set; } = new List<ActionItem>();
        public List<AutomationProfile> AutomationProfiles { get; set; } = new List<AutomationProfile>();
    }

    /// <summary>query user 解析出的会话信息</summary>
    public class SessionInfo
    {
        public string UserName { get; set; } = "";
        public string SessionName { get; set; } = "";
        public int Id { get; set; }
        public string State { get; set; } = "";
        public string IdleTime { get; set; } = "";
        public string LogonTime { get; set; } = "";

        public bool IsActive => string.Equals(State, "Active", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 是否本机的主会话（物理控制台，通常就是当前操作的桌面）。
        /// query user 里这类会话的 SESSIONNAME 是 "console"（可带 ">" 前缀），
        /// 远程会话则是 "rdp-tcp#N"。
        /// </summary>
        public bool IsConsoleSession
        {
            get
            {
                string n = (SessionName ?? "").TrimStart('>', '*').Trim();
                return n.StartsWith("console", StringComparison.OrdinalIgnoreCase);
            }
        }
        public bool IsDisconnected => string.Equals(State, "Disc", StringComparison.OrdinalIgnoreCase)
                                      || State.StartsWith("Disc", StringComparison.OrdinalIgnoreCase);

        public string Badge => IsActive ? "在线" : IsDisconnected ? "断开" : State;

        /// <summary>
        /// 带单位的空闲时长（query user 的 IDLE TIME 列是"分钟数"或一些特殊标记）。
        ///
        /// 原始输出只有数字，界面上看不出是秒还是分钟，容易误读（用户反馈）。
        /// 这里统一翻译成可读文字：
        ///   空 / "-" / "无"       → 无
        ///   "."                  → 刚刚操作
        ///   纯数字 N             → N 分钟
        /// </summary>
        public string IdleDisplay
        {
            get
            {
                string raw = (IdleTime ?? "").Trim();
                if (raw.Length == 0 || raw == "-" || raw == "无"
                    || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
                    return "无";
                if (raw == ".") return "刚刚操作";

                // ★ query user 的空闲格式有两种：
                //   不足 1 小时：纯数字（分钟），如 "26"
                //   超过 1 小时：时:分，如 "6:59"、"1:05"
                //   超过 1 天：天:时:分，如 "2:06:59"
                //   原来只处理了纯数字，所以 "6:59" 原样显示，用户看不出是什么。
                if (raw.Contains(":"))
                {
                    var parts = raw.Split(':');
                    int d = 0, h = 0, m = 0;
                    if (parts.Length == 2)
                    {
                        int.TryParse(parts[0], out h);
                        int.TryParse(parts[1], out m);
                    }
                    else if (parts.Length >= 3)
                    {
                        int.TryParse(parts[0], out d);
                        int.TryParse(parts[1], out h);
                        int.TryParse(parts[2], out m);
                    }
                    return FormatDuration(d, h, m);
                }

                // 纯数字 = 分钟
                if (int.TryParse(raw, out int mins))
                {
                    if (mins <= 0) return "刚刚操作";
                    return FormatDuration(0, mins / 60, mins % 60);
                }
                return raw;   // 兜底
            }
        }

        /// <summary>
        /// 空闲总分钟数（用于分级着色）。
        /// 解析不出来时返回 -1（表示"无空闲信息"）。
        /// </summary>
        public int IdleTotalMinutes
        {
            get
            {
                string raw = (IdleTime ?? "").Trim();
                if (raw.Length == 0 || raw == "-" || raw == "无"
                    || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
                    return -1;
                if (raw == ".") return 0;

                if (raw.Contains(":"))
                {
                    var parts = raw.Split(':');
                    int d = 0, h = 0, m = 0;
                    if (parts.Length == 2)
                    {
                        int.TryParse(parts[0], out h);
                        int.TryParse(parts[1], out m);
                    }
                    else if (parts.Length >= 3)
                    {
                        int.TryParse(parts[0], out d);
                        int.TryParse(parts[1], out h);
                        int.TryParse(parts[2], out m);
                    }
                    return d * 1440 + h * 60 + m;
                }
                return int.TryParse(raw, out int mins) ? mins : -1;
            }
        }

        /// <summary>
        /// 空闲分级：
        ///   0 = 绿色（&lt; 5 分钟，刚操作过，挂机正常）
        ///   1 = 黄色（5~30 分钟，可能已停止操作）
        ///   2 = 红色（&gt; 30 分钟，长时间无输入，注意是否掉线/卡死）
        ///   -1 = 无空闲信息（不参与分级，按原样显示）
        /// </summary>
        public int IdleLevel
        {
            get
            {
                int mins = IdleTotalMinutes;
                if (mins < 0) return -1;
                if (mins < 5) return 0;
                if (mins < 30) return 1;
                return 2;
            }
        }

        /// <summary>把天/时/分拼成可读文字，省略为 0 的高位单位。</summary>
        private static string FormatDuration(int days, int hours, int minutes)
        {
            if (days > 0)
                return hours > 0 ? (days + " 天 " + hours + " 小时")
                                 : (days + " 天");
            if (hours > 0)
                return minutes > 0 ? (hours + " 小时 " + minutes + " 分")
                                   : (hours + " 小时");
            if (minutes > 0) return minutes + " 分钟";
            return "刚刚操作";
        }

        /// <summary>
        /// 登录时间的友好显示。
        ///
        /// query user 给的是 "2026/11/16 3:52"，只到分钟、没有秒；
        /// 直接显示对挂机用户不直观（看不出"已经挂了多久"）。
        /// 这里补上相对时间：今天/昨天 + 具体时刻 + 已登录时长。
        /// </summary>
        public string LogonDisplay
        {
            get
            {
                string raw = (LogonTime ?? "").Trim();
                if (raw.Length == 0) return "—";
                if (!DateTime.TryParse(raw, out var t)) return raw;

                string day;
                var now = DateTime.Now;
                if (t.Date == now.Date) day = "今天";
                else if (t.Date == now.Date.AddDays(-1)) day = "昨天";
                else if (t.Date == now.Date.AddDays(-2)) day = "前天";
                else day = t.ToString("yyyy/MM/dd");

                string clock = t.ToString("HH:mm");
                var span = now - t;
                string elapsed = span.TotalDays >= 1
                    ? ((int)span.TotalDays + " 天")
                    : (span.TotalHours >= 1
                        ? ((int)span.TotalHours + " 小时")
                        : (Math.Max(0, (int)span.TotalMinutes) + " 分钟"));

                return day + " " + clock + " · 已在线 " + elapsed;
            }
        }
    }

    /// <summary>目标会话里的一个进程</summary>
    public class RemoteProcess
    {
        public int Pid { get; set; }
        public string Name { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string Title { get; set; } = "";
        public string StartTime { get; set; } = "";

        public string Display => string.IsNullOrEmpty(Title) ? Name + "  (" + Pid + ")" : Title + "  (" + Pid + ")";
    }

    public class ExecResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public int ExitCode { get; set; }
        public string Command { get; set; } = "";
        public long ElapsedMs { get; set; }
        public int AffectedCount { get; set; }

        public static ExecResult Fail(string msg) => new ExecResult { Ok = false, Message = msg };
        public static ExecResult Success(string msg) => new ExecResult { Ok = true, Message = msg };
    }

    /// <summary>命令行 / IPC 传进来的调用参数</summary>
    public class InvokeRequest
    {
        public string? Action { get; set; }
        public string? Account { get; set; }
        public string? User { get; set; }
        public string? Process { get; set; }
        public string? Password { get; set; }
        public int? Session { get; set; }
        public int Delay { get; set; } = 0;
        public bool Quiet { get; set; } = true;
        public ActionKind Kind { get; set; } = ActionKind.Launch;
        public string? Arguments { get; set; }
        public bool WaitForExit { get; set; } = true;
        /// <summary>仅为唤起已运行的界面（第二个实例点图标时）</summary>
        public bool Show { get; set; }
        /// <summary>唤起界面时要切换到的页面</summary>
        public string? UiPage { get; set; }
    }

    public class IpcResponse
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public string Command { get; set; } = "";
        public int ExitCode { get; set; }
        public long ElapsedMs { get; set; }
        public int AffectedCount { get; set; }
        public List<string> Detail { get; set; } = new List<string>();
    }
}