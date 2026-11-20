using System;
using System.Collections.Generic;
using System.Linq;

namespace SoulLink.Models
{
    public sealed class FlowNodeDef
    {
        public FlowNodeType Type { get; init; }
        public FlowNodeKind Kind { get; init; }
        public string Title { get; init; } = "";
        public string Desc { get; init; } = "";
        public string Glyph { get; init; } = "\uE8B7";
        public List<FlowParamDef> Params { get; init; } = new();
    }
    public enum FlowParamKind
    {
        Text = 0, Number = 1, Bool = 2, ActionPicker = 3, UserPicker = 4,
        OneDragonPicker = 5, ScriptGroupPicker = 6, Select = 7, FilePath = 8, ProcessName = 9,
        /// <summary>
        /// 按键捕获：不是让用户**手打** "Ctrl+A" 这种字面量，
        /// 而是直接**按下真实按键**，把组合键录进来。
        ///
        /// ★ 2.17.34：用户原话"按键录入不应该是文本框，用户按什么键就是什么键，
        ///   也可以组合键录入，这样才对"。
        ///   原来是个普通 TextBox，用户必须知道格式（F12 / CTRL+A）才能用，
        ///   拼错一个字母就静默失败 —— 根本不能算"录入"。
        /// </summary>
        KeyCapture = 10,
        /// <summary>
        /// 图片点选坐标：选一张截图 → 在图上点位置 → 存**归一化比例**，
        /// 运行时按目标账号的实际分辨率换算成像素坐标。
        /// 避免用户去猜 "960x540" 这种数字。
        /// </summary>
        CursorPick = 11
    }
    public sealed class FlowParamDef
    {
        public string Key { get; init; } = ""; public string Label { get; init; } = "";
        public FlowParamKind Kind { get; init; } = FlowParamKind.Text;
        public string Default { get; init; } = ""; public string Hint { get; init; } = "";
        public bool Required { get; init; } = true;
        public List<string> Options { get; init; } = new();
    }

    /// <summary>所有触点与 OUT 的唯一目录。旧 Trig 别名指向同一数据源，不产生重复菜单。</summary>
    public static class FlowNodeCatalog
    {
        public static string LastAddError = "";
        private static readonly Dictionary<FlowNodeType, FlowNodeDef> Map = new();
        private static FlowParamDef P(string key,string label,string value="",FlowParamKind kind=FlowParamKind.Text,string hint="",bool required=true,params string[] options)
            => new(){Key=key,Label=label,Default=value,Kind=kind,Hint=hint,Required=required,Options=options.ToList()};
        private static void D(FlowNodeType type,FlowNodeKind kind,string title,string desc,string glyph="\uE8B7",params FlowParamDef[] ps)
        {
            try
            {
                bool needsUser = type != FlowNodeType.TrigManual && type != FlowNodeType.CondTimeRange
                    && type != FlowNodeType.CondCompare && type != FlowNodeType.CondChoice && type != FlowNodeType.ActEndFlow && type != FlowNodeType.ActDelay;
                var list = ps.ToList();
                if (needsUser) list.Insert(0,P("user","目标用户","",FlowParamKind.UserPicker,"严格作用于所选账号",false));
                Map[type] = new FlowNodeDef { Type=type,Kind=kind,Title=title,Desc=desc,Glyph=glyph,Params=list };
            }
            catch(Exception ex) { LastAddError += type+": "+ex.Message+"\n"; }
        }
        private static void Alias(FlowNodeType oldType, FlowNodeType current) => Map[oldType] = Map[current];

        static FlowNodeCatalog()
        {
            D(FlowNodeType.TrigSchedule, FlowNodeKind.Condition, "定时·时刻",
                "到达设定时刻后满足；可设置每次递增延时与复位周期", "\uE823",
                P("time", "时刻", "04:00:00", hint: "HH:mm:ss（精确到秒）"),
                P("stepSeconds", "下次执行延时（秒）", "0", FlowParamKind.Number,
                  "每触发一次，下次时刻往后推这么多秒；0=固定时刻", false),
                P("resetAfter", "复位周期（次）", "0", FlowParamKind.Number,
                  "递增多少次后回到初始时刻；0=永不复位", false),
                P("catchUpHours", "补跑窗口（小时）", "12", FlowParamKind.Number, "0=不限窗口", false));
            D(FlowNodeType.TrigInterval,FlowNodeKind.Condition,"定时·间隔","每到间隔时满足一次，随后恢复不满足","\uE916",P("intervalSeconds","间隔秒","600",FlowParamKind.Number));
            D(FlowNodeType.TrigSessionOnline,FlowNodeKind.Condition,"会话在线","目标账号的会话当前在线","\uE7EE");
            D(FlowNodeType.TrigManual,FlowNodeKind.Condition,"手动","仅点击「立即运行」时满足","\uE768");
            D(FlowNodeType.TrigLogMatch,FlowNodeKind.Condition,"日志匹配","目标账号日志在时间窗内匹配关键字或正则","\uE8A5",
                P("pattern","匹配内容"),P("isRegex","按正则匹配","false",FlowParamKind.Bool),P("withinSeconds","最近多少秒","10",FlowParamKind.Number),
                P("level","只匹配级别","",FlowParamKind.Select,"",false,"全部|","INF|INF","WRN|WRN","ERR|ERR"));
            D(FlowNodeType.TrigBgiIdle,FlowNodeKind.Condition,"BGI 空闲","BGI 在运行且日志持续 N 秒未更新","\uE916",P("idleSeconds","空闲秒数","300",FlowParamKind.Number));
            D(FlowNodeType.CondBgiRunning,FlowNodeKind.Condition,"BGI 是否运行","检查目标账号 BGI 进程状态","\uE916",P("expect","期望在运行","true",FlowParamKind.Bool));
            D(FlowNodeType.CondBgiActive,FlowNodeKind.Condition,"BGI 日志活跃","BGI 日志最近 N 秒有更新","\uE916",P("withinSeconds","最近多少秒","120",FlowParamKind.Number));
            D(FlowNodeType.CondScriptGroupState,FlowNodeKind.Condition,"配置组状态","每轮读取当前状态；没有无效的 edge 参数","\uE8B7",
                P("group","配置组名","",FlowParamKind.ScriptGroupPicker),P("expect","期望状态","finished",FlowParamKind.Select,"" ,true,"从没跑过|never","执行中|running","已结束|finished","失败或被取消|failed"));
            D(FlowNodeType.CondDogfoodRoute,FlowNodeKind.Condition,"狗粮收尾路线","严格按目标账号、本流程所选业务日的记录；未知不等于 B","\uE8B7",P("expectA","期望是 A（走联机）","false",FlowParamKind.Bool,"不勾=期望 B"));
            D(FlowNodeType.CondGenshinRunning,FlowNodeKind.Condition,"原神是否运行","按目标账号会话检查 YuanShen.exe","\uE7FC",P("expect","期望在运行","true",FlowParamKind.Bool));
            D(FlowNodeType.CondTimeRange,FlowNodeKind.Condition,"时间段","本地时间在范围内，支持跨零点；端点包含","\uE823",P("from","开始（HH:mm）","00:00"),P("to","结束（HH:mm）","23:59"));
            D(FlowNodeType.CondLogContains,FlowNodeKind.Condition,"日志包含","按每条日志的真实时间戳判断时间窗","\uE8A5",
                P("pattern","匹配内容"),P("isRegex","按正则匹配","false",FlowParamKind.Bool),P("withinSeconds","最近多少秒","120",FlowParamKind.Number),
                P("level","只匹配级别","",FlowParamKind.Select,"",false,"全部|","INF|INF","WRN|WRN","ERR|ERR"));
            D(FlowNodeType.CondLogMissing,FlowNodeKind.Condition,"日志缺失","仅有可用日志时判断时间窗内未出现；无数据/坏正则不冒称缺失","\uE8A5",
                P("pattern","匹配内容"),P("isRegex","按正则匹配","false",FlowParamKind.Bool),P("withinSeconds","最近多少秒","120",FlowParamKind.Number));
            D(FlowNodeType.CondLogHasError,FlowNodeKind.Condition,"日志有报错","逐行判断时间窗内 ERR / 失败 / 异常，而不是仅看文件更新时间","\uE7BA",P("withinSeconds","最近多少秒","300",FlowParamKind.Number));
            D(FlowNodeType.CondLastResult,FlowNodeKind.Condition,"上一步结果","读取紧邻上游动作的执行结果；没有结果则不满足","\uE9D5",P("expect","期望结果","ok",FlowParamKind.Select,"",true,"成功|ok","失败|fail","已完成（不限成败）|any"));
            D(FlowNodeType.CondProcessExists,FlowNodeKind.Condition,"进程检查","严格检查目标用户，不会拿其它账号的进程冒充","\uE9D9",
                P("process","进程名"),P("expect","期望","存在",FlowParamKind.Select,"",true,"存在|存在","不存在|不存在"));
            D(FlowNodeType.CondBgiLiveState,FlowNodeKind.Condition,"BGI 活跃状态","按进程和日志心跳区分运行/空闲/停止；不是任务完成判断","\uE916",
                P("expect","期望状态","running",FlowParamKind.Select,"",true,"有日志心跳|running","进程在但空闲|idle","没进程|stopped"),P("idleMinutes","空闲阈值（分钟）","5",FlowParamKind.Number));
            D(FlowNodeType.CondSessionState,FlowNodeKind.Condition,"会话连接状态","在线、断开、无会话","\uE7EE",P("expect","期望状态","active",FlowParamKind.Select,"",true,"在线|active","离线|disc","没有会话|none"));
            D(FlowNodeType.CondWindowForeground,FlowNodeKind.Condition,"窗口在前台","通过目标账号投送通道读取前台窗口","\uE7FC",P("title","窗口标题包含（可空）","",hint:"留空=任意前台窗口",required:false));
            D(FlowNodeType.CondChoice,FlowNodeKind.Condition,"选择分支","连接多个出口时只执行其中满足的一路","\uE8CB");
            D(FlowNodeType.CondCompare,FlowNodeKind.Condition,"数值比较","时间统一为分钟；耗时统一为秒，不允许混比","\uE8EF",
                P("left","左值","now",FlowParamKind.Select,"",true,"当前时刻|now","上次耗时(秒)|lastElapsed"),P("op","比较方式",">",FlowParamKind.Select,"",true,"大于|>","大于等于|>=","小于|<","小于等于|<=","等于|==","不等于|!="),P("right","右值","04:00",hint:"时刻 HH:mm 或数字"));
            // 旧枚举别名只读取数据；菜单与执行共用当前触点定义。
            Alias(FlowNodeType.TrigScriptGroup,FlowNodeType.CondScriptGroupState);
            Alias(FlowNodeType.TrigDogfoodRoute,FlowNodeType.CondDogfoodRoute);

            D(FlowNodeType.ActRunAction,FlowNodeKind.Action,"执行快捷动作","执行已保存动作；显式目标用户会覆盖动作原账号","\uE768",P("actionId","选择动作","",FlowParamKind.ActionPicker));
            D(FlowNodeType.ActStopBgi,FlowNodeKind.Action,"停止 BGI","优先目标账号停止热键；超时可能结束目标 BGI 进程","\uE71A");
            D(FlowNodeType.ActStartBgi,FlowNodeKind.Action,"启动 BGI","在目标会话启动 BetterGI；成功指启动请求已接受","\uE768",P("args","启动参数","",required:false));
            D(FlowNodeType.ActStartOneDragon,FlowNodeKind.Action,"执行一条龙","清场后启动指定一条龙；不代表任务已跑完","\uE8FD",P("name","一条龙名称（可空）","",FlowParamKind.OneDragonPicker,"留空=页面选定",false));
            D(FlowNodeType.ActStartGroups,FlowNodeKind.Action,"执行配置组","先检查组名再清场，避免空参数先停止旧任务","\uE8FD",P("groups","配置组名","",FlowParamKind.ScriptGroupPicker,"从当前账号的 BGI 配置组中选择"));
            D(FlowNodeType.ActCloseGenshin,FlowNodeKind.Action,"关闭原神","关闭目标会话的 YuanShen.exe","\uE711");
            // ★★ 2.17.37：坐标改为"选一张截图 → 图上点选 → 按目标分辨率自动换算" ★★
            //
            //   用户原话："鼠标xy，有点不方便，可以选择一张图片，在图片选择点击位置，
            //   然后根据目标账号分辨率，自动计算出坐标。比如选择一张游戏截图，可以算出坐标。"
            //
            //   存储方式：
            //     image   —— 参照图路径（可为空；空则用旧的手填 x/y）
            //     pickX/pickY —— 在参照图上点的位置，**归一化到 0~1**
            //                    （存比例而不是像素：换一张同比例截图、或目标分辨率变了，
            //                      坐标仍然对得上，这是"自动计算"的关键）
            //     x/y     —— 旧字段保留：归一化坐标 × 目标分辨率 后写回，便于其它地方读取
            //   CursorPick 是新的参数类型，只负责图片点选 + 换算预览。
            D(FlowNodeType.ActMouseClick, FlowNodeKind.Action, "鼠标点击", "选一张截图，在图上点一下要点的位置；运行时按目标分辨率换算", "\uE8B0",
                P("image", "参照截图", "", FlowParamKind.CursorPick, "选一张该账号当前画面的截图，然后在图上点要点击的位置"),
                P("x", "X", "960", FlowParamKind.Number, "由上图自动换算；也可手动微调", false),
                P("y", "Y", "540", FlowParamKind.Number, "由上图自动换算；也可手动微调", false),
                P("button", "按键", "left", FlowParamKind.Select, "", true, "左键|left", "右键|right", "中键|middle"),
                P("count", "点击次数", "1", FlowParamKind.Number));
            D(FlowNodeType.ActMouseMove, FlowNodeKind.Action, "鼠标移动", "选一张截图，在图上点一下要移动到的位置", "\uE8B0",
                P("image", "参照截图", "", FlowParamKind.CursorPick, "选一张该账号当前画面的截图，然后在图上点位置"),
                P("x", "X", "960", FlowParamKind.Number, "由上图自动换算；也可手动微调", false),
                P("y", "Y", "540", FlowParamKind.Number, "由上图自动换算；也可手动微调", false));
            D(FlowNodeType.ActKeyPress, FlowNodeKind.Action, "模拟按键", "点一下输入框，然后直接按下要发送的按键（支持组合键）", "\uE765",
                P("keys", "按键", "", FlowParamKind.KeyCapture, "点一下输入框再按键；组合键直接一起按"),
                P("times", "次数", "1", FlowParamKind.Number));
            D(FlowNodeType.ActMouseWheel,FlowNodeKind.Action,"鼠标滚轮","正数上滚，负数下滚","\uE8B0",P("delta","滚动量","120",FlowParamKind.Number));
            D(FlowNodeType.ActLaunchProcess,FlowNodeKind.Action,"打开程序","在目标会话启动程序；启动成功不代表进程持续运行","\uE768",P("path","程序路径","",FlowParamKind.FilePath),P("args","启动参数","",required:false));
            D(FlowNodeType.ActKillProcess,FlowNodeKind.Action,"关闭进程","只操作目标账号；不存在目标进程属于幂等成功","\uE711",P("process","进程名","",FlowParamKind.ProcessName),P("force","强制结束","true",FlowParamKind.Bool));
            D(FlowNodeType.ActJoinRoom,FlowNodeKind.Action,"上线房间","调用房间服务；成功以服务端响应为准","\uE8FA",P("code","房间码"));
            D(FlowNodeType.ActDelay,FlowNodeKind.Action,"等待","条件满足后开始等待，到时继续；条件中断则重新计时","\uE916",P("seconds","秒数","10",FlowParamKind.Number));
            D(FlowNodeType.ActEndFlow,FlowNodeKind.Terminate,"每天完成","到达后当天停止扫描；到本节点日界自动重置。属性里可手动切换今天是否运行","\uE71A");
        }
        public static FlowNodeDef? Get(FlowNodeType t) => Map.TryGetValue(t,out var d) ? d : null;
        public static string TitleOf(FlowNodeType t) => Get(t)?.Title ?? t.ToString();
        public static string GlyphOf(FlowNodeType t) => Get(t)?.Glyph ?? "\uE8B7";
        public static readonly FlowParamDef TargetUserParam = P("user","目标用户","",FlowParamKind.UserPicker,required:false);
        public static List<FlowNodeDef> ByKind(FlowNodeKind kind)
        {
            var wanted = kind == FlowNodeKind.Trigger ? FlowNodeKind.Condition : kind;
            return Map.Where(kv => kv.Value.Kind == wanted).GroupBy(kv=>kv.Value).Select(g=>g.First().Value)
                .OrderBy(d => d.Kind == FlowNodeKind.Condition ? 0 : 1).ThenBy(d=>(int)d.Type).ToList();
        }
        public static List<FlowNodeDef> All() => Map.Select(kv => new FlowNodeDef{Type=kv.Key,Kind=kv.Value.Kind,Title=kv.Value.Title,Desc=kv.Value.Desc,Glyph=kv.Value.Glyph,Params=kv.Value.Params}).OrderBy(d=>(int)d.Type).ToList();
    }
}