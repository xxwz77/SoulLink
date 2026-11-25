using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using SoulLink.Services;

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
        /// <summary>
        /// 会话瘦身：用户自定义的可清理进程名（不含 .exe，忽略大小写）。
        ///
        /// ★ 与内置白名单是**并集**关系：内置的那几个始终可清理，
        ///   这里追加用户自己加进去的。
        /// ★ 系统保护名单（explorer/dwm/输入法/SoulLink 自身等）在代码里硬编码，
        ///   即使这里写了也不会被执行 —— 见 SessionSlimmer.NeverKill。
        /// ★ 空 = 只用内置白名单（默认）。
        /// </summary>
        public List<string> SessionSlimKillList { get; set; } = new List<string>();

        /// <summary>
        /// 被用户从内置白名单里**移除**的项。
        /// ★ 内置项也必须能移除 —— 否则等于把默认选择强加给用户。
        /// 记录在这里而不是直接改内置数组，这样「恢复默认」能一键还原。
        /// </summary>
        public List<string> SessionSlimDisabled { get; set; } = new List<string>();

        /// <summary>
        /// 会话瘦身总开关：是否启用「分账号杀外围进程」。
        ///
        /// ★ 对应 SessionSlimmer.Slim（按账号杀 SafeToKill 白名单里不会自动重启的进程）。
        ///   只影响挂机账号，不影响主控制台与其他日常账号。
        /// ★ 默认 true：会话瘦身默认开启（白名单制，绝不碰 explorer/dwm/输入法/游戏本体）。
        /// </summary>
        public bool SessionSlimKillEnabled { get; set; } = true;

        /// <summary>
        /// 机器级启动项策略（HKLM，class=Machine，全机生效）。
        ///
        /// ★ 对应 SessionSlimmer.ApplyMachinePolicy：写
        ///   `HKLM\SOFTWARE\Policies\Microsoft\Dsh\DisableWidgetsBoard=1`（关小组件面板）、
        ///   `...\Dsh\AllowNewsAndInterests=0`（关新闻流）、
        ///   `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search\DisableSearch=1`（关任务栏搜索/SearchHost）。
        /// ★ 这几个策略 ADMX 定义均为 class=Machine，写 HKCU 无效，必须写 HKLM（需要管理员）。
        /// ★ 默认 false：影响整台机器（含主控制台），需用户显式开启。
        /// </summary>
        public bool SessionSlimMachinePolicyEnabled { get; set; }

        /// <summary>
        /// 用户级启动项策略（HKCU，class=User，按账号生效）。
        ///
        /// ★ 对应代理在目标会话启动时写当前用户 HKCU 的
        ///   `HKCU\SOFTWARE\Policies\Microsoft\Edge\StartupBoostEnabled=0` 与
        ///   `...\Edge\BackgroundModeEnabled=0`（Edge 预启动/后台，class=User，HKCU 有效）。
        /// ★ 由代理直接写自己的 HKCU，绕开「宿主挂载已登录用户 NTUSER.DAT 会失败」的死结。
        /// ★ 默认 false：Edge 预启动在挂机会话里实测收益为 0（msedge 不跑），需用户显式开启。
        /// </summary>
        public bool SessionSlimUserPolicyEnabled { get; set; }

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

        /// <summary>
        /// 数据中心「配置组运行记录」（SQLite）的留存天数 —— <b>用户可自行设置留存时间</b>。
        ///
        /// ★ 3.3.41：运行记录落 SQLite（数据目录下的 run-db/run.db），超过本天数的行由
        ///   <see cref="Services.RunRecordHost"/> 自动清理（启动一次 + 跨天一次，走 TickHub）。
        /// ★ 默认 7 天。合法范围 <b>[1, 365]</b>：
        ///   缺失 / ≤0 / 非数字 → 7（老配置没有这个字段时自然取默认值，见 ConfigStore.Load）；
        ///   &gt;365 → 365。
        /// ★ 与 <see cref="BgiBackupKeep"/>（0 = 不限制）语义不同：本字段 <b>0 不表示「无限」</b>，
        ///   否则库会无界增长（7 天 × 20 账号约 2~7 MB，无限留存没有意义）。
        /// ★ 刻意<b>不做界面</b>（用户明确要求不展示）：要改就手改 config.json。
        /// </summary>
        public int RunDbRetentionDays { get; set; } = 7;

        /// <summary>米游社自动签到总开关；默认开启（用户 2026-11-25 明确要签到，仍可手动关闭）。</summary>
        public bool MiyousheSignEnabled { get; set; } = true;
        /// <summary>每日签到小时（0-23，本地时间）；兼容字段，新配置优先用 MiyousheSignTime。</summary>
        public int MiyousheSignHour { get; set; } = 8;
        /// <summary>每日签到时刻（"HH:mm"，本地时间）；默认 00:10（用户 2026-11-25 定稿）。</summary>
        public string MiyousheSignTime { get; set; } = "00:10";
        /// <summary>摩拉数据每小时拉取开关；默认开启。</summary>
        public bool MiyousheLedgerEnabled { get; set; } = true;
        /// <summary>摩拉拉取间隔分钟数；默认 60（米游社数据约 1 小时延迟，再频繁无意义）。</summary>
        public int MiyousheLedgerIntervalMinutes { get; set; } = 60;
        /// <summary>国服 x4 DS salt（t20 解包双源确认，公开社区值）。若服务端改协议/作废，可在设置中覆盖。</summary>
        public string MiyousheDsSalt { get; set; } = "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs";
        /// <summary>每账号自动签到独立开关；缺省跟随全局开关。</summary>
        public Dictionary<string, bool> MiyousheSignAccounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>米游社拉取状态配置（鉴权失败/风控退避分钟数，默认 60）。</summary>
        public int MiyousheRetryMinutes { get; set; } = 60;
        /// <summary>国际服 DS salt 独立配置。t20 仅确认其极简请求不需 DS，未找到独立 x4 值，故不伪造默认 salt；需要时可覆盖。</summary>
        public string MiyousheOsDsSalt { get; set; } = "";
        /// <summary>每账号默认区域（cn/os），不改变一 Windows 账号一套凭据主体的边界。</summary>
        public Dictionary<string,string> MiyousheAccountRegions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>游戏/社区子任务开关，键为 task@region；缺省为关闭（未核实项目强制关闭）。</summary>
        public Dictionary<string,bool> MiyousheTaskEnabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>不参与列表，键为 account|task@region；命中记 skipped 而非 failed。</summary>
        public HashSet<string> MiyousheExcludedTasks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>讨论区 ID 列表；至少选一个才能执行 forum 子任务。</summary>
        public List<string> MiyousheForumIds { get; set; } = new();
        /// <summary>社区请求间隔，来源为参考项目 3-8s 经验的保守下限，可配置为 8-120s。</summary>
        public int MiyousheCommunityDelaySeconds { get; set; } = 12;
        /// <summary>社区失败重试上限（不含首次），默认 2；指数退避 30/60/120... 秒，最大 1800s。</summary>
        public int MiyousheCommunityMaxRetries { get; set; } = 2;
        public int MiyousheCommunityBackoffBaseSeconds { get; set; } = 30;

        /// <summary>
        /// 上次用过的**米游社凭据导入路径**（ABGI 主文件夹 / BetterGI 根 / 具体文件）。
        ///
        /// ★ 用途：用户下次点「一键导入」时不用再选一遍路径（`path` 留空即用它）。
        /// ★ 只存路径字符串，**不存任何 cookie/stoken**；凭据本体只在
        ///   `&lt;DataDir&gt;\miyoushe\&lt;账号&gt;.cn.json` 以 DPAPI 密文保存。
        /// </summary>
        public string MiyousheImportPath { get; set; } = "";

        /// <summary>
        /// 米游社**默认原神角色**（t50）：账号名 → GameUid。
        ///
        /// ★ 用户需求原文：「如果有绑定多个原神账号，应该要可以切换，设为默认，
        ///   不然要取哪个数据呢？」
        /// ★ 一个米游社账号可以绑多个原神角色（不同区服/不同小号）。拉取与签到时
        ///   **全部角色都会处理**（不跳过任何一个，见 MiyousheService），
        ///   本配置只决定「界面默认展示/高亮哪一个」，以及**数据归属的默认视角**。
        /// ★ 缺省（没有该账号的键）= 未设默认 → 界面取角色列表第一项并标注「未设默认」。
        /// ★ 只存 GameUid 字符串，**不存任何凭据**。
        /// </summary>
        public Dictionary<string, string> MiyousheDefaultRole { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string LastExportPath { get; set; } = "";
        /// <summary>自动化编辑器首次打开时显示引导动画；用户勾选“不再弹出”后置为 false。</summary>
        public bool ShowFlowGuide { get; set; } = true;

        /// <summary>
        /// 会话连接后延迟回收工作集（t12）。默认开启。
        /// 硬关：环境变量 SOULLINK_TRIM_DISABLED=1（不落盘即可停）。
        /// </summary>
        public bool WorkingSetTrimEnabled { get; set; } = true;
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
        /// ★ 服务器地址（单一设置项）：联机大厅 / 路线更新 / 客户端更新三处共用的基地址。
        ///   · 用户自定义，例如 https://服务器地址 或 http://服务器地址:端口；
        ///   · 空 = 未配置：三处功能都静默短路，不发任何请求；
        ///   · 老用户的 <see cref="RouteUpdate"/>.CloudUrl 若不为空，会在迁移时回填到这里。
        /// </summary>
        public string ServerBaseUrl { get; set; } = "";

        /// <summary>
        /// 联机大厅服务端地址（历史字段，保留兼容）：
        /// ★ 2.17 以前是独立的联机大厅地址；现与 ServerBaseUrl 合并。
        ///   加载时若 ServerBaseUrl 为空且此字段非空，会回填到 ServerBaseUrl。
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

        /// <summary>
        /// 进入/退出 RDP 全屏的热键，默认 F11。
        /// ★ 3.2.4：原来写死在 ConsolePage 里，用户无法改。
        ///   而 F11 有概率被 RDP 控件自己的键盘处理抢走，导致"进去了出不来"；
        ///   开放成可配置后，用户可以换成 RDP 不吞的组合键。
        /// </summary>
        public string FullScreenHotkey { get; set; } = "F11";

        /// <summary>
        /// 退出全屏的热键（可选，留空 = 不启用）。
        ///
        /// ★★ 3.2.4 修正 ★★
        ///   最初我把默认值定成 "Escape" —— 那是**错的**。
        ///   游戏里 Esc 是高频键（菜单/取消/暂停），全屏时被钩子吞掉等于游戏没法玩。
        ///   用户明确指出：「必须是自定义，游戏需要 esc，你怎么不跟我商量？」
        ///
        ///   现在的规则：
        ///     · **默认留空 = 完全不钩任何键**，全屏只靠 FullScreenHotkey 一个键进出；
        ///     · 想要额外的退出键，由用户自己在设置里录（可录 Esc，也可录别的）；
        ///     · 任何热键都只在全屏时生效，非全屏一律原样透传给画面，绝不吞键。
        /// </summary>
        public string ExitFullScreenHotkey { get; set; } = "";

        // ══════════ 屏幕录制（3.2.8）══════════
        //
        //  需求（用户原话）：
        //    「在 RDP 通用配置里，增加一个设置栏目，对目标用户（RDP 配置里有的用户，
        //      可多选）开启录像功能，然后用户可以设置录像时长」
        //    「一直录，只留最近 30 秒，旧帧自动删」
        //    「10fps / 15fps / 30fps 用户可选」
        //    「肯定要清晰」

        /// <summary>开启录制的账号 Id 列表（用户多选）。空 = 不录任何账号。</summary>
        public List<string> RecordAccountIds { get; set; } = new();

        /// <summary>录制帧率：10 / 15 / 30，用户可选。</summary>
        public int RecordFps { get; set; } = 5;

        /// <summary>直播帧率（30/45/60），与录像 RecordFps 完全独立。</summary>
        public int StreamFps { get; set; } = 30;

        /// <summary>JPEG 质量。用户要求清晰，默认 75（越高越清晰也越大）。</summary>
        public int RecordQuality { get; set; } = 75;

        /// <summary>环形缓冲保留秒数。超过即丢弃最旧的帧。</summary>
        public int RecordKeepSeconds { get; set; } = 30;

        /// <summary>
        /// 抓屏后端：0=自动(DXGI DDA→WGC→BitBlt)，1=DXGI DDA，2=WGC，3=BitBlt。
        /// 非法值按自动档处理；运行中通过 captureParams 热切，不重投代理、不断 viewer。
        /// </summary>
        public int CaptureMode { get; set; } = 0;

        /// <summary>
        /// H.264 直播/录像码率（kbps）。默认 8000 = 8 Mbps。
        ///
        /// ★ 用户指定档位（可调）：
        ///   · 4000 省流 / 8000 标准(默认) / 15000 高画质 / 25000 无损
        /// ★ 对比：现有 JPEG 方案约 46 Mbps，H.264 8 Mbps 是它的约 1/6。
        /// ★ 实际输出会波动（CBR 模式 ±20%），建议网络留 20% 余量。
        /// </summary>
        public int VideoBitrateKbps { get; set; } = 8000;

        /// <summary>
        /// NVENC 编码器预设档位 1~7。
        /// ★ P1 最快、画质最低；P7 最慢、画质最高。默认 3 均衡。
        /// ★ 只对 NvencSdkEncoder（NVENC SDK D3D11 直通）生效；
        ///   软编/MF 路径忽略此项。
        /// ★ 经 RelayClient.CaptureEnv 下发为 SOULLINK_NVENC_PRESET 环境变量。
        /// </summary>
        public int NvencPreset { get; set; } = 3;

        /// <summary>
        /// 关键帧缓冲上调百分比（0~200，默认 0）。
        /// ★ 对应 Sunshine 的 vbv_percentage_increase：在「1 帧 bit 量」的 VBV 基础上
        ///   额外上调 N%，让 IDR 关键帧有更大的编码缓冲 → IDR 画质更好、体积更大。
        ///   0 = 不额外缓冲（IDR 压缩最强、体积最小，但画质偏低）。
        /// ★ 只对 NvencSdkEncoder 生效。经 SOULLINK_VBV_INCREASE_PERCENT 环境变量下发。
        /// </summary>
        public int VbvBufferIncreasePercent { get; set; } = 0;

        // ══════════ 网页访问（3.2.8）══════════
        //
        //  用户需求：「在设置里填写个端口号，ip+端口号可以打开网页」
        //  用途：手机等设备通过局域网访问录制回放、实时画面。

        /// <summary>网页服务端口。0 = 不启动（默认关闭，避免无谓监听）。</summary>
        public int WebPort { get; set; } = 0;

        /// <summary>
        /// 网页**直连代理**（3.3.5+）。
        ///
        /// ★ 用户要求：「网页端尽量隔离主客户端」「模拟按键也有独立出来」
        ///
        ///   true  = 实时画面 / 录像回放 / 按键注入 都由代理自己对外服务，
        ///           主程序只提供「账号 → 端口」映射表，不碰任何流量；
        ///   false = 全部经主程序转发（旧的单端口模式，保留以便回退）。
        ///
        /// 端口规则见 Services/WebDirect.cs：
        ///   <c>WebPort + 账号在 Accounts 里的序号 + 1</c>
        /// 一个账号一个端口，不做汇聚 —— 实测 Windows 端口独占，
        /// 两个进程绑同一端口时第二个会静默失效（收不到任何连接）。
        /// </summary>
        public bool WebDirectToAgent { get; set; } = true;

        /// <summary>
        /// 主程序**回环内部端口**（3.3.45 / t24）。
        ///
        /// ★ 架构变更（设计文档 §11.4 / §17.1）：
        ///   · 对外入口 = 独立服务 <c>SoulLink.TaskEdit.exe</c> 独占 <see cref="WebPort"/>；
        ///   · 主程序改为只绑 <c>http://127.0.0.1:&lt;本端口&gt;/</c>，外部不可直达；
        ///   · 独立服务把非 <c>/taskedit/*</c> 的请求反代到这个端口。
        /// ★ 默认 0 = 自动：取 <c>WebPort + 1</c>（被占用则顺延，见 WebHost.ResolveInternalPort）。
        ///   写死一个值也能用，但同时改 WebPort 时会不一致，所以默认交给自动推导。
        /// ★ 本端口**不需要**防火墙放行（回环流量不经过防火墙）。
        /// </summary>
        public int WebInternalPort { get; set; } = 0;

        /// <summary>网页访问是否需要口令（空 = 不需要）。局域网内可选。</summary>
        public string WebPassword { get; set; } = "";

        /// <summary>
        /// 显式豁免：允许代理直连**无口令访问**（仅建议本机回环场景）。
        /// ★ 2026-11-23 安全收口：默认 false。代理侧 CheckToken 在 token 为空时
        ///   默认拒绝（401），仅当此项为 true 才放行 —— 用显式开关代替「空串=放行」，
        ///   避免「刚装好未设口令 → /api/input 无鉴权开放」的默认裸奔。
        /// </summary>
        public bool WebAllowNoToken { get; set; } = false;

        /// <summary>
        /// 网页登录令牌（持久化，让浏览器"记住密码"）。
        /// ★ 用户反馈：每次都要重新输口令很烦。
        ///   这里把 token 存进配置，Cookie 带一年有效期，
        ///   只要口令没改、配置没清，同一浏览器就一直免密。
        /// 改口令 → 旧 token 失效（因为比对的是当前 token，用户可手动换）。
        /// </summary>
        public string WebAuthToken { get; set; } = "";

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
            /// <summary>
            /// 高：动态背景 **跟随显示器刷新率（最高 60fps）** + 呼吸灯，全部保留。
            ///
            /// ★ 3.0.1：原来高档是写死的 30fps。用户反馈「动画卡卡的」——
            ///   30fps 对缓慢流动的粒子/丝带确实能看出顿挫。
            ///   现在改为 0 间隔 = 每帧都推进，由 CompositionTarget.Rendering
            ///   的天然节奏驱动（60Hz 屏就是 60fps，144Hz 屏也按刷新率走）。
            /// </summary>
            public const int High = 0;
            /// <summary>
            /// 中（默认，推荐）：**背景仍在动**，30fps，保留呼吸灯。
            ///
            /// ★ 3.0.1：从 10fps 提到 30fps。10fps 的流动背景视觉上明显卡顿，
            ///   而 30fps 已经是「流畅」的公认门槛。CPU 代价靠降低单帧复杂度
            ///   （DreamScene.Detail = 1）来抵消，而不是靠砍帧率。
            /// </summary>
            public const int Medium = 1;
            /// <summary>低：背景与呼吸灯全部静止（画面冻结，最省电）。</summary>
            public const int Low = 2;

            /// <summary>
            /// 动态背景的帧间隔（毫秒）。**0 = 每帧都推进（跟随显示器刷新率）**。
            ///
            /// 历史：
            ///   2.17.46 —— 中档从"关动画"改成 10fps，解决了档位断层；
            ///   3.0.1   —— 高档 30→60fps、中档 10→30fps，并修掉帧节奏抖动
            ///              （见 AuroraBackdrop.OnFrame 的差一帧推进）。
            ///              同时靠 DreamScene.Detail 降低中档单帧成本，
            ///              而不是靠砍帧率省 CPU —— 砍帧率换来的是观感损失。
            /// </summary>
            public static int BackdropFrameMs(int tier) => tier switch
            {
                High => 0,      // 0 = 每帧推进，跟随显示器刷新（60fps / 144fps）
                Medium => 33,   // 30fps：流畅门槛
                _ => 0,         // 低档：完全静止
            };

            /// <summary>
            /// 是否「每帧推进」（对应 BackdropFrameMs = 0 的动态档）。
            /// ★ 高档按显示器刷新率推进，所以间隔是 0；
            ///   但低档的间隔也是 0（表示"永不推进"），两者必须靠档位本身区分。
            /// </summary>
            public static bool EveryFrame(int tier) => tier == High;

            /// <summary>
            /// 是否播放动态背景（中/高都播，低档静止）。
            ///
            /// ★★ 2026-11-21 修复 ★★
            ///   原来写的是 `BackdropFrameMs(tier) > 0`，但高档的 FrameMs 是 0
            ///   （表示"每帧推进"），于是高档被判成"不播动画" ——
            ///   表现为「选了高（完整动效）却只有静态背景、画面一动不动」。
            ///
            ///   根因是 0 这个值承担了两种相反含义：
            ///     High → 0 = 间隔为零，每帧都推
            ///     Low  → 0 = 永不推进
            ///   所以判断必须看档位本身，不能再从间隔值反推。
            /// </summary>
            public static bool Backdrop(int tier) => tier == High || tier == Medium;

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
        /// ★ 历史字段：旧的路线云端地址（清单或压缩包地址）。
        ///   2.17 起路线更新走单一服务器设置（AppSettings.ServerBaseUrl），不再用内置默认源。
        ///   保留它只为迁移老配置：[JsonIgnore] 不再序列化，ConfigStore.Load 会把它回填到
        ///   ServerBaseUrl 后清空。新代码不得直接读它（读 <see cref="ServerEndpoints.RouteManifest"/>）。
        /// </summary>
        [JsonIgnore]
        public string CloudUrl { get; set; } = "";

        /// <summary>
        /// 实际使用的云端清单地址。
        /// ★ 改为从单一服务器设置派生（不再有任何内置默认域名）；未配置时为空串。
        /// </summary>
        [JsonIgnore]
        public string EffectiveCloudUrl => ServerEndpoints.RouteManifest;

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

    /// <summary>目标会话里的一个进程</summary>
    public class RemoteProcess
    {
        public int Pid { get; set; }
        public string Name { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string Title { get; set; } = "";
        public string StartTime { get; set; } = "";

        public string Display => string.IsNullOrEmpty(Title) ? Name + "  (" + Pid + ")" : Title + "  (" + Pid + ")";

        /// <summary>
        /// 中文友好名。进程名（CrossDeviceService.exe 之类）普通用户看不懂，
        /// 会话瘦身页要让人一眼知道"这是什么东西、能不能关"。
        /// 未收录的返回空串，界面回退到显示原始名。
        /// </summary>
        public string FriendlyName
        {
            get
            {
                string key = Name.ToLowerInvariant();
                if (key.EndsWith(".exe")) key = key.Substring(0, key.Length - 4);
                return key switch
                {
                    "msedge" => "Edge 浏览器",
                    "msedgewebview2" => "Edge 内嵌浏览器组件",
                    "widgets" => "Windows 小组件",
                    "widgetservice" => "小组件服务",
                    "searchhost" => "开始菜单搜索",
                    "startmenuexperiencehost" => "开始菜单",
                    "shellexperiencehost" => "系统外壳",
                    "phoneexperiencehost" => "手机连接",
                    "crossdeviceservice" => "跨设备服务",
                    "crossdeviceresume" => "跨设备续接",
                    "gamebar" => "游戏栏",
                    "gamebarftserver" => "游戏栏服务",
                    "gamebarpresencewriter" => "游戏栏状态",
                    "explorer" => "桌面与文件管理器",
                    "dwm" => "桌面窗口管理器",
                    "rdpclip" => "远程剪贴板",
                    "rdpinput" => "远程输入",
                    "ctfmon" => "输入法",
                    "chsime" => "中文输入法",
                    "textinputhost" => "输入体验",
                    "tabtip" => "触摸键盘",
                    "svchost" => "系统服务宿主",
                    "runtimebroker" => "系统权限代理",
                    "sihost" => "系统进程宿主",
                    "taskhostw" => "系统任务宿主",
                    "fontdrvhost" => "字体驱动",
                    "csrss" => "客户端服务",
                    "winlogon" => "登录进程",
                    "nvcontainer" => "NVIDIA 容器",
                    "nvdisplay.container" => "NVIDIA 显示服务",
                    "rtkauduservice64" => "Realtek 声卡服务",
                    "smartscreen" => "SmartScreen 筛选",
                    "filecoauth" => "文件协作授权",
                    "useroobebroker" => "系统设置代理",
                    "defendersessionhelper" => "Defender 会话辅助",
                    // 游戏与挂机工具（不做清理，只是让列表可读）
                    "yuanshen" => "原神",
                    "genshinimpact" => "原神",
                    "bettergi" => "BetterGI 自动化",
                    "auto-bgi" => "AutoBGI",
                    "multiplayerhoeingassistant" => "联机锄地助手",
                    "mojang" => "Minecraft 启动器",
                    "zfgamebrowser" => "游戏内置浏览器",
                    "edgegameassist" => "Edge 游戏助手",
                    "appactions" => "系统应用操作",
                    _ => ""
                };
            }
        }

        /// <summary>
        /// 同名进程合并后的全部 PID。空表示只有这一个实例。
        /// ★ 列表里同名进程合并成一行显示，但「结束进程」要按这里的
        ///   全部实例执行 —— 否则用户以为清理干净了，实际还剩十几个。
        /// </summary>
        public List<int> DuplicatePids { get; set; } = new List<int>();

        /// <summary>该行代表几个实例（≥1）。</summary>
        public int InstanceCount => DuplicatePids.Count > 0 ? DuplicatePids.Count : 1;

        /// <summary>PID 列显示文本：多个时显示「首个 +N」。</summary>
        public string PidText => InstanceCount == 1
            ? Pid.ToString()
            : Pid + "  +" + (InstanceCount - 1);

        /// <summary>
        /// 列表显示用：有中文名时显示「中文名（进程名）」，否则只显示进程名。
        /// ★ 单行而不是两行 —— 两行在紧凑列表里会把行高撑得很高、
        ///   而且同一个进程重复出现时两行文字显得非常拥挤。
        /// </summary>
        public string DisplayLabel
        {
            get
            {
                string fn = FriendlyName;
                string baseText = string.IsNullOrEmpty(fn) ? Name : fn + "（" + Name + "）";
                return InstanceCount > 1 ? baseText + "  ×" + InstanceCount : baseText;
            }
        }

        /// <summary>
        /// 图标（Segoe MDL2 Assets 字形）。用于进程列表左侧，
        /// 让一行行进程有视觉区分，而不是纯文字墙。
        /// </summary>
        public string IconGlyph
        {
            get
            {
                string key = Name.ToLowerInvariant();
                if (key.EndsWith(".exe")) key = key.Substring(0, key.Length - 4);
                return key switch
                {
                    "msedge" or "msedgewebview2" => "\uE774",          // 地球
                    "widgets" or "widgetservice" => "\uE71D",          // 磁贴
                    "searchhost" => "\uE721",                          // 搜索
                    "startmenuexperiencehost" or "shellexperiencehost" => "\uE700",
                    "phoneexperiencehost" or "crossdeviceservice" or "crossdeviceresume" => "\uE8EA", // 设备
                    "gamebar" or "gamebarftserver" or "gamebarpresencewriter" => "\uE7FC", // 游戏
                    "explorer" => "\uE756",                            // 文件夹
                    "dwm" => "\uE7F4",                                 // 桌面
                    "rdpclip" or "rdpinput" => "\uE8A7",
                    "ctfmon" or "chsime" or "textinputhost" or "tabtip" => "\uE765", // 键盘
                    "svchost" or "runtimebroker" or "sihost" or "taskhostw" => "\uE9F5", // 齿轮
                    "nvcontainer" or "nvdisplay.container" => "\uE7F4",
                    _ => "\uE7C3"                                       // 通用方块
                };
            }
        }
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