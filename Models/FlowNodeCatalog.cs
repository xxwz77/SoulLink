using System;
using System.Collections.Generic;

namespace SoulLink.Models
{
    /// <summary>节点类型的一项定义（用于界面展示和参数编辑）。</summary>
    public sealed class FlowNodeDef
    {
        public FlowNodeType Type { get; init; }
        public FlowNodeKind Kind { get; init; }
        /// <summary>显示名。</summary>
        public string Title { get; init; } = "";
        /// <summary>一句话说明。</summary>
        public string Desc { get; init; } = "";
        /// <summary>MDL2 图标。</summary>
        public string Glyph { get; init; } = "\uE8B7";
        /// <summary>参数定义（决定编辑器里显示哪些输入框）。</summary>
        public List<FlowParamDef> Params { get; init; } = new List<FlowParamDef>();
    }

    /// <summary>参数类型。</summary>
    public enum FlowParamKind
    {
        Text = 0,
        Number = 1,
        Bool = 2,
        /// <summary>从快捷动作里选（actionId）</summary>
        ActionPicker = 3,
        /// <summary>选择账号（user）；留空 = 本流程绑定的账号</summary>
        UserPicker = 4,
        /// <summary>选择 BGI 一条龙配置</summary>
        OneDragonPicker = 5,
        /// <summary>选择 BGI 配置组（可多选，逗号分隔）</summary>
        ScriptGroupPicker = 6
    }

    /// <summary>一个参数的定义。</summary>
    public sealed class FlowParamDef
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public FlowParamKind Kind { get; init; } = FlowParamKind.Text;
        public string Default { get; init; } = "";
        public string Hint { get; init; } = "";

        /// <summary>
        /// 是否必填（空值会让流程校验失败）。
        /// 默认 true；像"启动参数""一条龙名称"这类可以留空的设 false。
        /// </summary>
        public bool Required { get; init; } = true;
    }

    /// <summary>
    /// 所有可用节点的目录。
    /// 界面左侧的"模块库"和节点属性面板都从这里取定义。
    /// </summary>
    public static class FlowNodeCatalog
    {
        private static readonly Dictionary<FlowNodeType, FlowNodeDef> Map =
            new Dictionary<FlowNodeType, FlowNodeDef>();

        static FlowNodeCatalog()
        {
            // ── 触发器（只能有一个，界面上会限制）─────────────────
            Add(new FlowNodeDef
            {
                Type = FlowNodeType.TrigSchedule, Kind = FlowNodeKind.Trigger,
                Title = "定时触发", Glyph = "\uE823",
                Desc = "到达指定时刻时触发（精确到秒）",
                Params =
                {
                    new FlowParamDef { Key = "hour", Label = "时", Kind = FlowParamKind.Number, Default = "4" },
                    new FlowParamDef { Key = "minute", Label = "分", Kind = FlowParamKind.Number, Default = "0" },
                    new FlowParamDef { Key = "second", Label = "秒", Kind = FlowParamKind.Number, Default = "0" },
                    new FlowParamDef { Key = "intervalSeconds", Label = "间隔（秒，0=每天）", Kind = FlowParamKind.Number, Default = "0",
                                        Hint = "填大于 0 表示每隔这么多秒执行一次" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.TrigSessionOnline, Kind = FlowNodeKind.Trigger,
                Title = "会话上线", Glyph = "\uE7EE",
                Desc = "目标账号的会话上线时触发"
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.TrigBgiIdle, Kind = FlowNodeKind.Trigger,
                Title = "BGI 空闲", Glyph = "\uE916",
                Desc = "BGI 日志超过指定时间没有更新时触发",
                Params =
                {
                    new FlowParamDef { Key = "idleSeconds", Label = "空闲秒数", Kind = FlowParamKind.Number, Default = "300" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.TrigManual, Kind = FlowNodeKind.Trigger,
                Title = "手动触发", Glyph = "\uE768",
                Desc = "只在点「立即运行」时触发"
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.TrigLogMatch, Kind = FlowNodeKind.Trigger,
                Title = "日志匹配", Glyph = "\uE7BA",
                Desc = "BGI 日志里出现指定内容时触发（支持关键字或正则）",
                Params =
                {
                    new FlowParamDef { Key = "pattern", Label = "匹配内容", Kind = FlowParamKind.Text, Default = "",
                                        Hint = "例如：任务结束、执行失败、开始第" },
                    new FlowParamDef { Key = "isRegex", Label = "按正则匹配", Kind = FlowParamKind.Bool, Default = "false" },
                    new FlowParamDef { Key = "level", Label = "只匹配级别（可空）", Kind = FlowParamKind.Text, Default = "", Required = false,
                                        Hint = "INF / WRN / ERR，留空=全部" }
                }
            });

            // ── 条件 ──────────────────────────────────────────────
            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondBgiRunning, Kind = FlowNodeKind.Condition,
                Title = "BGI 是否在运行", Glyph = "\uE9D9",
                Desc = "检查目标账号有没有 BetterGI 进程",
                Params =
                {
                    new FlowParamDef { Key = "expect", Label = "期望（true=在运行）", Kind = FlowParamKind.Bool, Default = "true" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondBgiActive, Kind = FlowNodeKind.Condition,
                Title = "BGI 是否有产出", Glyph = "\uE9D2",
                Desc = "BGI 日志在最近 N 秒内有没有新内容",
                Params =
                {
                    new FlowParamDef { Key = "withinSeconds", Label = "最近多少秒内", Kind = FlowParamKind.Number, Default = "120" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondGenshinRunning, Kind = FlowNodeKind.Condition,
                Title = "原神是否在运行", Glyph = "\uE7FC",
                Desc = "检查目标账号有没有 YuanShen.exe",
                Params =
                {
                    new FlowParamDef { Key = "expect", Label = "期望（true=在运行）", Kind = FlowParamKind.Bool, Default = "true" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondTimeRange, Kind = FlowNodeKind.Condition,
                Title = "时间段", Glyph = "\uE823",
                Desc = "当前时间是否在指定区间内（如 04:00 - 06:00）",
                Params =
                {
                    new FlowParamDef { Key = "from", Label = "开始（HH:mm）", Kind = FlowParamKind.Text, Default = "00:00" },
                    new FlowParamDef { Key = "to", Label = "结束（HH:mm）", Kind = FlowParamKind.Text, Default = "23:59" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondLogContains, Kind = FlowNodeKind.Condition,
                Title = "日志包含", Glyph = "\uE8A5",
                Desc = "BGI 日志在最近 N 秒内出现过指定内容",
                Params =
                {
                    new FlowParamDef { Key = "pattern", Label = "匹配内容", Kind = FlowParamKind.Text, Default = "" },
                    new FlowParamDef { Key = "isRegex", Label = "按正则匹配", Kind = FlowParamKind.Bool, Default = "false" },
                    new FlowParamDef { Key = "withinSeconds", Label = "最近多少秒内", Kind = FlowParamKind.Number, Default = "120" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondLogMissing, Kind = FlowNodeKind.Condition,
                Title = "日志不含", Glyph = "\uE8A5",
                Desc = "BGI 日志在最近 N 秒内**没有**出现指定内容（异常检测）",
                Params =
                {
                    new FlowParamDef { Key = "pattern", Label = "匹配内容", Kind = FlowParamKind.Text, Default = "" },
                    new FlowParamDef { Key = "isRegex", Label = "按正则匹配", Kind = FlowParamKind.Bool, Default = "false" },
                    new FlowParamDef { Key = "withinSeconds", Label = "最近多少秒内", Kind = FlowParamKind.Number, Default = "120" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondLogHasError, Kind = FlowNodeKind.Condition,
                Title = "日志有报错", Glyph = "\uE7BA",
                Desc = "BGI 日志在最近 N 秒内出现过 ERR / 失败 / 异常",
                Params =
                {
                    new FlowParamDef { Key = "withinSeconds", Label = "最近多少秒内", Kind = FlowParamKind.Number, Default = "300" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondLastResult, Kind = FlowNodeKind.Condition,
                Title = "上一步结果", Glyph = "\uE9D5",
                Desc = "判断前面执行器的执行结果是成功还是失败",
                Params =
                {
                    new FlowParamDef { Key = "expect", Label = "期望结果", Kind = FlowParamKind.Text, Default = "ok",
                                        Hint = "ok=成功（默认）/ fail=失败 / any=不限" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondProcessExists, Kind = FlowNodeKind.Condition,
                Title = "进程检查", Glyph = "\uE9D9",
                Desc = "检查指定用户有没有某个进程在运行（可跨用户）",
                Params =
                {
                    new FlowParamDef { Key = "process", Label = "进程名", Kind = FlowParamKind.Text, Default = "",
                                        Hint = "例如：YuanShen、BetterGI、msedge（可带或不带 .exe）" },
                    new FlowParamDef { Key = "expect", Label = "期望", Kind = FlowParamKind.Text, Default = "存在",
                                        Hint = "存在 / 不存在" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondBgiLiveState, Kind = FlowNodeKind.Condition,
                Title = "BGI 活跃状态", Glyph = "\uE916",
                Desc = "区分 BGI 是【在跑且有产出】【进程在但空闲】【没运行】",
                Params =
                {
                    new FlowParamDef { Key = "expect", Label = "期望状态", Kind = FlowParamKind.Text, Default = "running",
                                        Hint = "running=在跑有产出 / idle=进程在但没产出 / stopped=没进程" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondSessionState, Kind = FlowNodeKind.Condition,
                Title = "会话连接状态", Glyph = "\uE7EE",
                Desc = "检查目标账号的会话是在线、离线还是完全没有连接",
                Params =
                {
                    new FlowParamDef { Key = "expect", Label = "期望状态", Kind = FlowParamKind.Text, Default = "active",
                                        Hint = "active=在线 / disc=离线（有会话但断开）/ none=没有会话" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondWindowForeground, Kind = FlowNodeKind.Condition,
                Title = "窗口在前台", Glyph = "\uE7FC",
                Desc = "检查目标账号的窗口是否在前台（配合窗口检查）",
                Params =
                {
                    new FlowParamDef { Key = "title", Label = "窗口标题包含（可空）", Kind = FlowParamKind.Text, Default = "", Required = false,
                                        Hint = "留空=只判断有没有前台窗口" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.CondCompare, Kind = FlowNodeKind.Condition,
                Title = "数值比较", Glyph = "\uE8EF",
                Desc = "比较两个值（时间或数字），如 当前时刻 > 04:00",
                Params =
                {
                    new FlowParamDef { Key = "left", Label = "左值", Kind = FlowParamKind.Text, Default = "now",
                                        Hint = "now=当前时刻 / nowTime=当前时分 / lastElapsed=上次耗时(秒) / 数字" },
                    new FlowParamDef { Key = "op", Label = "比较方式", Kind = FlowParamKind.Text, Default = ">",
                                        Hint = ">、>=、<、<=、==、!=" },
                    new FlowParamDef { Key = "right", Label = "右值", Kind = FlowParamKind.Text, Default = "04:00",
                                        Hint = "时间用 HH:mm；数字直接写" }
                }
            });

            // ── 执行器 ────────────────────────────────────────────
            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActRunAction, Kind = FlowNodeKind.Action,
                Title = "执行快捷动作", Glyph = "\uE768",
                Desc = "运行一个已保存的快捷动作（会带上它自己的停止BGI/关原神设置）",
                Params =
                {
                    new FlowParamDef { Key = "actionId", Label = "选择动作", Kind = FlowParamKind.ActionPicker, Default = "" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActStopBgi, Kind = FlowNodeKind.Action,
                Title = "停止 BGI", Glyph = "\uE71A",
                Desc = "停止目标账号正在运行的脚本（快捷键优先，失败则结束进程）"
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActStartBgi, Kind = FlowNodeKind.Action,
                Title = "启动 BGI", Glyph = "\uE768",
                Desc = "启动 BetterGI，可带命令行参数",
                Params =
                {
                    new FlowParamDef { Key = "args", Label = "参数（可空）", Kind = FlowParamKind.Text, Default = "", Required = false,
                                        Hint = "例如 --startOneDragon 炸车" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActStartOneDragon, Kind = FlowNodeKind.Action,
                Title = "执行一条龙", Glyph = "\uE8FD",
                Desc = "启动 BetterGI 并执行一条龙（先自动停掉正在跑的脚本）",
                Params =
                {
                    new FlowParamDef { Key = "name", Label = "一条龙名称（可空）", Kind = FlowParamKind.Text, Default = "", Required = false,
                                        Hint = "留空=执行 BGI 页面上选定的那条；填写则执行指定的一条龙" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActStartGroups, Kind = FlowNodeKind.Action,
                Title = "执行配置组", Glyph = "\uE8FD",
                Desc = "启动 BetterGI 并执行指定配置组（可多个，空格分隔）",
                Params =
                {
                    new FlowParamDef { Key = "groups", Label = "配置组名（可多个）", Kind = FlowParamKind.Text, Default = "",
                                        Hint = "多个用空格分隔，例如：CD采集-全采集 关闭游戏" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActCloseGenshin, Kind = FlowNodeKind.Action,
                Title = "关闭原神", Glyph = "\uE711",
                Desc = "结束目标账号的 YuanShen.exe（只影响该账号）"
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActMouseClick, Kind = FlowNodeKind.Action,
                Title = "鼠标点击", Glyph = "\uE8B0",
                Desc = "在目标账号的桌面上模拟鼠标点击",
                Params =
                {
                    new FlowParamDef { Key = "x", Label = "X 坐标", Kind = FlowParamKind.Number, Default = "960" },
                    new FlowParamDef { Key = "y", Label = "Y 坐标", Kind = FlowParamKind.Number, Default = "540" },
                    new FlowParamDef { Key = "button", Label = "按键（left/right/middle）", Kind = FlowParamKind.Text, Default = "left" },
                    new FlowParamDef { Key = "count", Label = "点击次数", Kind = FlowParamKind.Number, Default = "1" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActMouseMove, Kind = FlowNodeKind.Action,
                Title = "鼠标移动", Glyph = "\uE8B0",
                Desc = "把鼠标移动到目标账号桌面的指定位置",
                Params =
                {
                    new FlowParamDef { Key = "x", Label = "X 坐标", Kind = FlowParamKind.Number, Default = "960" },
                    new FlowParamDef { Key = "y", Label = "Y 坐标", Kind = FlowParamKind.Number, Default = "540" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActKeyPress, Kind = FlowNodeKind.Action,
                Title = "模拟按键", Glyph = "\uE765",
                Desc = "在目标账号的桌面上按一个键（支持 Ctrl+A 这类组合）",
                Params =
                {
                    new FlowParamDef { Key = "keys", Label = "按键", Kind = FlowParamKind.Text, Default = "",
                                        Hint = "例如：F12、Enter、Ctrl+A、Alt+F4" },
                    new FlowParamDef { Key = "times", Label = "按几次", Kind = FlowParamKind.Number, Default = "1" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActMouseWheel, Kind = FlowNodeKind.Action,
                Title = "鼠标滚轮", Glyph = "\uE8B0",
                Desc = "在目标账号桌面上滚动滚轮",
                Params =
                {
                    new FlowParamDef { Key = "delta", Label = "滚动量（正=上，负=下）", Kind = FlowParamKind.Number, Default = "120" }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActLaunchProcess, Kind = FlowNodeKind.Action,
                Title = "打开程序", Glyph = "\uE768",
                Desc = "在目标账号的会话里启动一个程序",
                Params =
                {
                    new FlowParamDef { Key = "path", Label = "程序路径", Kind = FlowParamKind.Text, Default = "",
                                        Hint = @"例如：C:\Program Files\xxx\app.exe" },
                    new FlowParamDef { Key = "args", Label = "启动参数（可空）", Kind = FlowParamKind.Text, Default = "", Required = false }
                }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActKillProcess, Kind = FlowNodeKind.Action,
                Title = "关闭进程", Glyph = "\uE711",
                Desc = "结束目标账号下的指定进程（只影响该账号）",
                Params =
                {
                    new FlowParamDef { Key = "process", Label = "进程名", Kind = FlowParamKind.Text, Default = "",
                                        Hint = "例如：YuanShen、BetterGI（可带或不带 .exe）" },
                    new FlowParamDef { Key = "force", Label = "强制结束", Kind = FlowParamKind.Bool, Default = "true" }
                }
            });

            // ── 结束（独立类别，琥珀色）──────────────────────────
            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActEndFlow, Kind = FlowNodeKind.Terminate,
                Title = "结束流程", Glyph = "\uE71A",
                Desc = "立即结束本次流程，不再执行后面的节点，条件同时清零",
                Params = { }
            });

            Add(new FlowNodeDef
            {
                Type = FlowNodeType.ActDelay, Kind = FlowNodeKind.Action,
                Title = "等待", Glyph = "\uE916",
                Desc = "暂停指定秒数后继续下一个节点",
                Params =
                {
                    new FlowParamDef { Key = "seconds", Label = "秒数", Kind = FlowParamKind.Number, Default = "10" }
                }
            });
        }

        /// <summary>
        /// 「目标用户」参数：所有节点都带，留空 = 本流程绑定的账号。
        /// ★ 用户要求：触发器/条件/执行器都要能指定用户，默认本用户。
        /// </summary>
        public static readonly FlowParamDef TargetUserParam = new FlowParamDef
        {
            Key = "user",
            Label = "目标用户（留空=本账号）",
            Kind = FlowParamKind.UserPicker,
            Default = "",
            Hint = "不填就作用于该流程绑定的账号；填了则作用于指定账号"
        };

        private static void Add(FlowNodeDef d)
        {
            // 每个节点都自动带上"目标用户"参数，避免逐个手写漏掉
            d.Params.Insert(0, TargetUserParam);
            Map[d.Type] = d;
        }

        public static FlowNodeDef? Get(FlowNodeType t)
            => Map.TryGetValue(t, out var d) ? d : null;

        public static string TitleOf(FlowNodeType t)
            => Get(t)?.Title ?? t.ToString();

        public static string GlyphOf(FlowNodeType t)
            => Get(t)?.Glyph ?? "\uE8B7";

        /// <summary>按类别列出，界面用来分组显示模块库。</summary>
        public static List<FlowNodeDef> ByKind(FlowNodeKind kind)
        {
            var list = new List<FlowNodeDef>();
            foreach (var kv in Map)
                if (kv.Value.Kind == kind) list.Add(kv.Value);
            list.Sort((a, b) => ((int)a.Type).CompareTo((int)b.Type));
            return list;
        }

        public static List<FlowNodeDef> All()
        {
            var list = new List<FlowNodeDef>();
            foreach (var kv in Map) list.Add(kv.Value);
            list.Sort((a, b) => ((int)a.Type).CompareTo((int)b.Type));
            return list;
        }
    }
}
