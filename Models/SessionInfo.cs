using System;

namespace SoulLink.Models
{
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
}
