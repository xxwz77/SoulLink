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

        /// <summary>
        /// 执行前先停止目标账号正在运行的 BetterGI。
        /// 一条龙/配置组必须从 BGI 空闲状态启动，否则会被"已有任务在跑"挡住。
        /// </summary>
        public bool StopBgiFirst { get; set; } = true;

        /// <summary>执行前是否同时关闭目标账号的原神（YuanShen.exe）。</summary>
        public bool CloseGenshin { get; set; } = false;

        /// <summary>
        /// 停止 BGI / 游戏后，**等进程完全退出**之外再额外等待的秒数。
        /// 与 DelaySeconds（执行前延时）相互独立。
        /// </summary>
        public int StopWaitSeconds { get; set; } = 2;

        /// <summary>
        /// 定时执行计划。为空表示不启用（手动执行）。
        /// 启用后会写入 Windows 计划任务，即使 SoulLink 没开也会按时执行。
        /// </summary>
        public ScheduleSpec? Schedule { get; set; }

        [JsonIgnore]
        public bool IsClose => Kind == ActionKind.Close;

        [JsonIgnore]
        public bool HasSchedule => Schedule != null && Schedule.Enabled;
    }

    /// <summary>定时类型。</summary>
    public enum ScheduleKind
    {
        /// <summary>每天固定时刻</summary>
        Daily = 0,
        /// <summary>每周指定星期几</summary>
        Weekly = 1,
        /// <summary>每月指定几号</summary>
        Monthly = 2,
        /// <summary>每年指定月日</summary>
        Yearly = 3,
        /// <summary>固定间隔（分钟级/小时级）</summary>
        Interval = 4,
        /// <summary>只执行一次</summary>
        Once = 5
    }

    /// <summary>
    /// 一条动作的定时计划。
    /// ★ 所有类型都精确到**秒**：时刻型用 Hour/Minute/Second，间隔型用 IntervalSeconds。
    /// </summary>
    public class ScheduleSpec
    {
        public bool Enabled { get; set; }

        /// <summary>
        /// 已废弃：Windows 计划任务方案已移除。
        /// 现在定时统一由 SoulLink 进程内调度器执行
        /// （计划任务要求 BGI 以目标账号身份运行，实现复杂且易出错）。
        /// </summary>
        public bool UseWindowsTask { get; set; } = false;

        public ScheduleKind Kind { get; set; } = ScheduleKind.Daily;

        // ── 时刻型（Daily/Weekly/Monthly/Yearly/Once）───────────────
        /// <summary>时 0-23</summary>
        public int Hour { get; set; } = 4;
        /// <summary>分 0-59</summary>
        public int Minute { get; set; } = 0;
        /// <summary>秒 0-59（精确到秒）</summary>
        public int Second { get; set; } = 0;

        // ── Weekly ────────────────────────────────────────────────
        /// <summary>星期几，0=周日 … 6=周六</summary>
        public List<int> WeekDays { get; set; } = new List<int>();

        // ── Monthly ───────────────────────────────────────────────
        /// <summary>几号 1-31</summary>
        public int DayOfMonth { get; set; } = 1;

        // ── Yearly ────────────────────────────────────────────────
        public int Month { get; set; } = 1;

        // ── Interval ──────────────────────────────────────────────
        /// <summary>间隔秒数（例如 3600 = 每小时）</summary>
        public int IntervalSeconds { get; set; } = 3600;

        // ── Once ──────────────────────────────────────────────────
        /// <summary>只一次的执行时刻（本地时间）</summary>
        public DateTime OnceAt { get; set; } = DateTime.Now.AddHours(1);

        /// <summary>
        /// 每次执行完成后，延时 N 秒再允许下一次。
        /// 用于"跑完一轮等一会儿再跑下一轮"的场景（如一条龙耗时较长）。
        /// ★ 对 Interval 类型是叠加在间隔之上的缓冲；
        ///   对其他类型是"上一次还在跑就跳过"的保护窗口。
        /// </summary>
        public int PostRunDelaySeconds { get; set; } = 0;

        /// <summary>上次实际执行时间（用于间隔计算与界面显示）。</summary>
        public DateTime? LastRunAt { get; set; }

        /// <summary>
        /// 已执行次数。用于"每次执行后延后 N 秒"的递增计算。
        /// ★ 语义：第 n 次执行时刻 = 基准时刻 + (n-1) × PostRunDelaySeconds
        ///   例：基准 4:00、延后 60 秒
        ///       第1次 4:00:00、第2次 4:01:00、第3次 4:02:00 …
        ///   延后 86400 秒（1 天）时：第2次 = 4:00 + 24h = 第3天 4:00
        /// </summary>
        public int RunCount { get; set; }

        /// <summary>
        /// 是否暂停。
        /// ★ 用户在主界面点了暂停，或者（计划任务模式下）计划任务被手动删除时，
        ///   这个标记会置为 true，界面显示"已暂停"。
        /// </summary>
        public bool Paused { get; set; }

        /// <summary>暂停原因（显示用），例如"计划任务已被删除"。</summary>
        public string PauseReason { get; set; } = "";

        /// <summary>
        /// 延后单位，仅用于**界面显示**（秒/分/时/天）。
        /// 实际计算一律用 PostRunDelaySeconds。
        /// </summary>
        public int PostRunDelayUnit { get; set; } = 1;   // 0=秒 1=分 2=时 3=天

        /// <summary>把单位换算成秒。</summary>
        public static int UnitToSeconds(int unit) => unit switch
        {
            3 => 86400,   // 天
            2 => 3600,    // 时
            1 => 60,      // 分
            _ => 1        // 秒
        };

        /// <summary>界面上的"延后 X 单位"文字。</summary>
        [JsonIgnore]
        public string PostRunDelayDisplay
        {
            get
            {
                if (PostRunDelaySeconds <= 0) return "不延后";
                int unitSec = UnitToSeconds(PostRunDelayUnit);
                int v = PostRunDelaySeconds / Math.Max(1, unitSec);
                string unitName = PostRunDelayUnit switch
                {
                    3 => "天", 2 => "小时", 1 => "分钟", _ => "秒"
                };
                return "每次延后 " + v + " " + unitName;
            }
        }

        /// <summary>人类可读的描述。</summary>
        [JsonIgnore]
        public string Display
        {
            get
            {
                string at = Hour.ToString("00") + ":" + Minute.ToString("00") + ":" + Second.ToString("00");
                switch (Kind)
                {
                    case ScheduleKind.Daily: return "每天 " + at;
                    case ScheduleKind.Weekly:
                    {
                        if (WeekDays.Count == 0) return "每周（未选星期） " + at;
                        var names = new[] { "日", "一", "二", "三", "四", "五", "六" };
                        var parts = new List<string>();
                        foreach (var d in WeekDays)
                            if (d >= 0 && d < 7) parts.Add("周" + names[d]);
                        return string.Join("、", parts) + " " + at;
                    }
                    case ScheduleKind.Monthly: return "每月 " + DayOfMonth + " 号 " + at;
                    case ScheduleKind.Yearly: return "每年 " + Month + " 月 " + DayOfMonth + " 号 " + at;
                    case ScheduleKind.Interval:
                    {
                        if (IntervalSeconds >= 3600 && IntervalSeconds % 3600 == 0)
                            return "每 " + (IntervalSeconds / 3600) + " 小时";
                        if (IntervalSeconds >= 60 && IntervalSeconds % 60 == 0)
                            return "每 " + (IntervalSeconds / 60) + " 分钟";
                        return "每 " + IntervalSeconds + " 秒";
                    }
                    case ScheduleKind.Once:
                        return "只一次 " + OnceAt.ToString("yyyy-MM-dd HH:mm:ss");
                    default: return at;
                }
            }
        }
    }

    /// <summary>账号：user + password + 可选的固定会话 ID</summary>
    public class Account
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string UserName { get; set; } = "";
        /// <summary>DPAPI 加密后的密码（Base64）</summary>
        /// <summary>
        /// 加密保存的登录密码。
        /// ★ 用途仅限 **RDP 连接时自动登录**（IMsTscNonScriptable.ClearTextPassword），
        ///   省去每次连接都要手输密码。
        /// ★ 与"跨会话投送"无关：投送走 SYSTEM 计划任务 + WTSQueryUserToken，不用凭据。
        /// </summary>
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

        /// <summary>
        /// 动作编辑窗口下拉里的提示后缀。
        /// 用户可以直接输入任意用户名（执行时再匹配会话），
        /// 这里只是把"已保存过凭据的账号"标出来，方便复用。
        /// </summary>
        [JsonIgnore]
        public string SessionTag => string.IsNullOrWhiteSpace(DisplayName)
            ? "（已保存）"
            : ("（" + DisplayName + "）");

        /// <summary>提示后缀颜色。</summary>
        ///
        /// ★ 2.17.51（.NET 11 迁移）：原来在 getter 里每次 new 一个画刷，
        ///   而 WPF 绑定会对同一属性反复求值。颜色是常量，冻成静态只读即可，
        ///   像素结果完全一致（同色同 alpha），只是不再重复分配。
        [JsonIgnore]
        public System.Windows.Media.Brush SessionTagBrush { get; } = FrozenColor(0x88, 0x99, 0xB8);

        private static System.Windows.Media.SolidColorBrush FrozenColor(byte r, byte g, byte b)
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

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
        /// <summary>仅管理挂机账号：全局隐藏并禁止主用户作为自动化/更新/投送目标。</summary>
        public bool OnlyAutomationAccounts { get; set; }
        /// <summary>服务端发起的动作也执行（CLI / IPC 调用）</summary>
        public bool AllowRemoteInvoke { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;

        /// <summary>
        /// BGI 备份每个账号保留几次。
        ///
        /// ★ 2.20.0：<b>0 = 不限制</b>（保留全部历史备份）。
        ///   默认 3。设为 0 后程序永不自动删备份，由用户自己管理，
        ///   适合「想留完整版本历史」或「备份放在大容量盘上」的场景。
        /// </summary>
        public int BgiBackupKeep { get; set; } = 3;

        /// <summary>
        /// 路线更新下载的云端包缓存保留天数。
        ///
        /// ★ 2.21.2：<b>0 = 不限制</b>（永不自动清）。默认 30 天。
        ///   缓存删了可以重新下载，所以按天清理是安全的；
        ///   但给一个较长的默认值（30 天），避免用户想回档时包没了。
        /// </summary>
        public int RouteCacheKeepDays { get; set; } = 30;
        public string LastExportPath { get; set; } = "";
        /// <summary>自动化编辑器首次打开时显示引导动画；用户勾选“不再弹出”后置为 false。</summary>
        public bool ShowFlowGuide { get; set; } = true;
        public bool UseBrokerChannel { get; set; }

        /// <summary>
        /// 手动代理模式。
        ///
        /// ★ 语义：完全不自动投送，只在本机建好管道等待用户手动启动代理。
        ///   开启时：停止本账号的自动投送、清理该会话已有的自动代理、建立监听。
        ///   关闭时：撤销监听，恢复自动投送。
        ///   程序启动时读取此开关；开着就直接进入等待状态，不投送任何东西。
        ///
        /// ★ 为什么做成显式开关而不是"检测有没有手动代理"：
        ///   手动代理和自动代理同名同进程，靠进程/命令行反推既不可靠也很难解释。
        ///   开关是用户明确声明的状态，逻辑简单、可预期。
        /// </summary>
        public bool ManualAgentMode { get; set; }

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

        /// <summary>
        /// 每个用户当前选定的 BetterGI 路径（用户名 → BetterGI.exe 完整路径）。
        ///
        /// ★ 语义：这是"用户明确指定或系统自动选中的那一个"。
        ///   由 BgiPathResolver 统一维护，不要再直接读写它判断"有没有装 BGI"。
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string> BgiPaths { get; set; }
            = new System.Collections.Generic.Dictionary<string, string>(
                System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 每个用户"见过"的全部 BetterGI 路径（用户名 → 路径列表）。
        ///
        /// ★ 为什么需要：用户可能同时装了多个 BGI（不同盘、不同版本）。
        ///   只记一个会导致"换到第二个之后，第一个的信息就丢了"，
        ///   界面上也看不出到底有几个可用。
        /// ★ 自动收集：每次探测到正在运行的 BGI 就并入这里，去重并规范化路径。
        /// </summary>
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> BgiKnownPaths { get; set; }
            = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>(
                System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 用户手动「切换」过的用户（用户名集合）。
        ///
        /// ★ 语义：这个用户身上，**人指定的优先于自动探测**。
        ///   没在这个集合里的用户，探测到哪个就用哪个（并在界面上标注"自动"）。
        ///   用户点一次「切换」就加入，点「恢复自动」就移出。
        /// </summary>
        public System.Collections.Generic.List<string> BgiPinnedUsers { get; set; }
            = new System.Collections.Generic.List<string>();

        /// <summary>
        /// 「自动绑定 → 手动绑定」的一次性迁移已完成。
        ///
        /// ★ 为什么需要：2.17.44 及更早的 <c>BgiPathResolver.Resolve</c> 会在
        ///   <c>persist:true</c> 时把自动探测到的路径写进 <see cref="BgiPaths"/>，
        ///   使"用户是否显式绑定过"无法判断。2.17.45 起只有 <c>Pin()</c> 能写，
        ///   并且启动时按「路径存在且该 BGI 正在运行」补一次绑定，避免
        ///   升级后所有账号突然变成"未绑定"导致数据不再刷新。
        /// ★ 迁移只做一次；之后绑定完全由用户在设置页点按钮决定。
        /// </summary>
        public bool BgiMigrated { get; set; }

        /// <summary>
        /// 联机大厅服务端地址（例如 http://121.4.78.52:45080）。
        /// 空 = 用默认地址。
        /// </summary>
        public string LobbyServerUrl { get; set; } = "";

        /// <summary>
        /// 发车前是否关闭原神（按账号独立）。
        /// ★ 停 BGI 是固定行为（一条龙/配置组都要求空闲），不需要配置。
        ///   关原神才是可选项，存在本地即可，不用过服务器。
        /// </summary>
        public Dictionary<string, bool> DepartCloseGenshin { get; set; }
            = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>
        /// 界面动效档位（性能 vs 观感）。
        ///
        /// ★ 实测（本机 2026-11-20，无 RDP 画面、仅主窗口）：
        ///   · 动态背景（梦幻星海）单独约占 61 个单核百分点
        ///     —— 每 33ms 全量重绘 3×24 层丝带 + 76 粒子 + 轨道系统；
        ///   · 呼吸灯（GlowDot）单独约占 27 个单核百分点
        ///     —— 每 60ms InvalidateVisual，且旧实现每次都新建渐变画刷。
        ///
        /// ★ 2.17.46 复核：同一场景重新分帧计时，旧渲染 1.08 ms/帧 → 新 0.29 ms/帧
        ///   （丝带烘焙、画刷/几何全缓存、静止档整帧烘焙）。
        ///   因此三档重新划分：高 30fps / 中 10fps / 低静止。
        ///
        ///   默认「中」：10fps 背景 + 呼吸灯，约 3 个单核百分点。
        ///   另外动画在**窗口失焦时一律暂停**，所以挂机场景即使选「高」也会自动降下来。
        /// </summary>
        public int EffectTier { get; set; } = 1;

        /// <summary>兼容旧配置：早期只有一个 AnimateBackdrop 布尔开关。</summary>
        public bool? AnimateBackdrop { get; set; }

        /// <summary>
        /// 档位 → 行为。集中在这里判断，避免各处散落魔法数字。
        /// </summary>
        public static class EffectTierLevel
        {
            /// <summary>高：全部保留。</summary>
            public const int High = 0;
            /// <summary>中：动态背景降到 10fps，保留呼吸灯（默认，推荐）。</summary>
            public const int Medium = 1;
            /// <summary>低：全部静止。</summary>
            public const int Low = 2;

            /// <summary>
            /// 动态背景的帧间隔（毫秒）。0 = 不播放（静止画面）。
            ///
            /// ★ 2.17.46：原来「中」档直接关掉背景动画（Backdrop(tier) => tier &lt;= High），
            ///   等于中档和低档只差一个呼吸灯，档位断层。用户反馈「中画质应该还会动」。
            ///
            ///   之所以当初只能做成"关"，是因为旧渲染每帧 1.0 ms（2560×1368 实测），
            ///   30fps 就要吃掉 60% 单核。2.17.46 把每帧压到 0.29 ms（丝带/画刷/几何
            ///   全部缓存），10fps 只要 ~3% 单核 —— 中档终于放得下动画了。
            /// </summary>
            public static int BackdropFrameMs(int tier) => tier switch
            {
                High => 33,     // 30fps
                Medium => 100,  // 10fps：还在动，但成本是三十分之一的量级
                _ => 0,         // 低档：完全静止
            };

            /// <summary>是否播放动态背景（中/高都播，低档静止）。</summary>
            public static bool Backdrop(int tier) => BackdropFrameMs(tier) > 0;

            /// <summary>呼吸灯是否脉冲（低档时保持静态点亮）。</summary>
            public static bool GlowPulse(int tier) => tier <= Medium;
        }

        /// <summary>
        /// 保护层边框颜色（#AARRGGBB）：锁定态时框住 RDP 画布的那个框。
        ///
        /// ★ 为什么是高亮边框而不是画面上的半透明覆盖层：
        ///   覆盖层是独立的分层窗口（AllowsTransparency=True = WS_EX_LAYERED），
        ///   压到 mstscax 的 DirectComposition 表面上会让那块区域退出硬件合成、
        ///   变成纯黑（2026-11-19 已定论）。而边框是主窗口内部的 WPF Border，
        ///   和 RDP 共用同一个重定向表面，不产生新的合成层，所以安全。
        ///
        /// 空字符串 = 用主题默认色（焦点紫）。
        /// </summary>
        public string ShieldBorderColor { get; set; } = "";

        /// <summary>RDP 焦点边框粗细（DIP）。0 = 不画高亮（保留 1px 极淡边界）。</summary>
        public double ShieldBorderThickness { get; set; } = 2.0;

        /// <summary>
        /// 画面区排列：0=单个（只显示焦点画面）1=双列（可滚动浏览）2=网格（等分）。
        /// ★ 只影响"怎么摆"，不影响远端分辨率——缩放模式在连接时就已确定。
        /// </summary>
        public int RdpLayoutMode { get; set; } = 0;

        /// <summary>
        /// 画面在流式布局里的顺序（账号 Id 列表）。
        /// 没打开画面的账号跳过；新账号追加到末尾。
        /// </summary>
        public System.Collections.Generic.List<string> RdpViewOrder { get; set; }
            = new System.Collections.Generic.List<string>();

        /// <summary>
        /// 旧版单目标字段（2.14.0 只有它）。
        /// ★ 仅为兼容旧配置保留：加载时会迁移进 <see cref="RouteUpdate"/>.Targets。
        ///   新代码不要再读写它。
        /// </summary>
        public string RouteUpdatePath { get; set; } = "";

        /// <summary>路线更新的云端地址、多目标与定时设置。</summary>
        public RouteUpdateSettings RouteUpdate { get; set; } = new RouteUpdateSettings();
    }

    /// <summary>一个路线更新目标（通常是某个账号的 AutoPathing 目录）。</summary>
    public class RouteUpdateTarget
    {
        /// <summary>路线库根目录，包内第一层文件夹直接落在这里。</summary>
        public string Path { get; set; } = "";

        /// <summary>本目标是否参与更新（界面上打勾）。</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// 路线更新设置。
    ///
    /// ★ 语义：这是**路线库的根目录**，压缩包里的"第一层文件夹"直接落在这里。
    ///   BetterGI 的路线实际位于 &lt;BGI 目录&gt;\User\AutoPathing，
    ///   例如包内是「CD采集路线\12小时狗粮\…」，更新后就是
    ///   &lt;目标根目录&gt;\CD采集路线\12小时狗粮\…，
    ///   只删除同名的第一层文件夹，其余文件夹和文件一律不动。
    /// </summary>
    public class RouteUpdateSettings
    {
        /// <summary>更新目标列表（每个账号一行，可勾选）。</summary>
        public List<RouteUpdateTarget> Targets { get; set; } = new List<RouteUpdateTarget>();

        /// <summary>
        /// 官方默认更新源（清单地址）。
        /// ★ 用途：用户没改过设置时自动使用它；界面上**绝不显示**这个地址。
        /// </summary>
        public const string DefaultCloudUrl = "https://cdn.example.invalid/pathing/update.txt";

        /// <summary>
        /// 云端地址：清单或压缩包地址。
        /// ★ 界面不提供输入框、不显示地址。
        /// ★ 不写进配置文件（[JsonIgnore]）：免得用户打开 config.json 就看到服务器地址，
        ///   每次从默认源取。
        /// </summary>
        [JsonIgnore]
        public string CloudUrl { get; set; } = DefaultCloudUrl;

        /// <summary>
        /// 实际使用的云端地址：用户没配置过就用默认源。
        /// ★ [JsonIgnore]：这是计算属性，不参与序列化 ——
        ///   漏了它会让默认地址被写进 config.json（实测踩到）。
        /// </summary>
        [JsonIgnore]
        public string EffectiveCloudUrl =>
            string.IsNullOrWhiteSpace(CloudUrl) ? DefaultCloudUrl : CloudUrl.Trim();

        /// <summary>
        /// 云端清单里当前包的版本标识。
        /// ★ 存文件指纹（大小+哈希）而不是清单里写的原始包名 ——
        ///   包名会暴露服务器的文件命名，用户要求界面上绝不出现。
        /// </summary>
        public string CloudVersion { get; set; } = "";

        /// <summary>上次成功更新时的版本指纹。与 CloudVersion 相同 = 无更新，跳过。</summary>
        public string LastAppliedVersion { get; set; } = "";

        /// <summary>
        /// 本地保留的历史包数量（含当前）。超过就删最旧的。
        /// </summary>
        public int KeepArchives { get; set; } = 3;

        /// <summary>
        /// 本地路线包路径（上一个选中的包，方便重复使用）。
        /// ★ 与云端地址同时存在时：**本地包优先**，只有本地留空才去下载。
        /// </summary>
        public string LocalArchive { get; set; } = "";

        /// <summary>替换前是否把旧文件夹移入备份目录。</summary>
        public bool BackupBeforeReplace { get; set; } = true;

        /// <summary>定时更新计划（复用动作的 ScheduleSpec 与 ScheduleCalculator）。</summary>
        public ScheduleSpec Schedule { get; set; } = new ScheduleSpec();

        /// <summary>上次执行时间与结果，用于界面显示。</summary>
        public DateTime? LastRunAt { get; set; }
        public string LastResult { get; set; } = "";

        /// <summary>
        /// 把旧的单目标字段迁移进 Targets。
        /// 返回 true 表示配置发生了迁移（调用方据此决定是否保存）。
        /// </summary>
        public bool MigrateFromLegacy(AppSettings settings)
        {
            if (settings == null) return false;

            bool changed = false;
            if (Targets == null) { Targets = new List<RouteUpdateTarget>(); changed = true; }

            string legacy = settings.RouteUpdatePath ?? "";
            if (Targets.Count == 0 && legacy.Trim().Length > 0)
            {
                Targets.Add(new RouteUpdateTarget { Path = legacy.Trim(), Enabled = true });
                changed = true;
            }

            if (Schedule == null) { Schedule = new ScheduleSpec(); changed = true; }
            if (Targets.FindAll(t => t == null).Count > 0)
            {
                Targets.RemoveAll(t => t == null);
                changed = true;
            }

            return changed;
        }

        /// <summary>参与更新的目标（去重、去掉空路径，保持顺序）。</summary>
        public List<string> ActiveTargets()
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Targets ?? new List<RouteUpdateTarget>())
            {
                if (t == null || !t.Enabled) continue;
                string p = (t.Path ?? "").Trim().Trim('"');
                if (p.Length == 0) continue;
                if (!seen.Add(p)) continue;
                list.Add(p);
            }
            return list;
        }
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

        /// <summary>
        /// 节点式自动化流程。每个流程绑定一个账号，互不干扰。
        /// </summary>
        public List<AutomationFlow> Automations { get; set; } = new List<AutomationFlow>();
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