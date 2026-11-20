using System;
using System.Collections.Generic;
using System.Linq;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 「哪些节点依赖 BGI 日志 / 记录」的统一判定。
    ///
    /// ★ 为什么集中一处：用户要求「使用日志判断的直接不显示未绑定的用户」，
    ///   如果每个界面各自写一份清单，迟早会出现"编辑器能选、运行时没数据"
    ///   的静默失配（用户只会觉得自动化莫名不触发）。
    /// </summary>
    public static class BgiDataDependency
    {
        /// <summary>
        /// 该节点是否需要「已绑定的 BGI 路径」才能拿到数据。
        ///
        /// 依赖的是**读盘**：日志原文、配置组状态、狗粮记录、日志心跳。
        /// 不包含「BGI 是否运行」（那是进程枚举，不读盘，未绑定也能判断）。
        /// </summary>
        public static bool NeedsBoundBgi(FlowNodeType t) => t switch
        {
            // 日志原文
            FlowNodeType.TrigLogMatch => true,
            FlowNodeType.CondLogContains => true,
            FlowNodeType.CondLogMissing => true,
            FlowNodeType.CondLogHasError => true,

            // 配置组运行状态（来自日志解析）
            FlowNodeType.TrigScriptGroup => true,
            FlowNodeType.CondScriptGroupState => true,

            // 日志心跳 / 活跃度
            FlowNodeType.TrigBgiIdle => true,
            FlowNodeType.CondBgiActive => true,
            FlowNodeType.CondBgiLiveState => true,

            // 狗粮记录（按 BgiPath 定位 records 文件）
            FlowNodeType.TrigDogfoodRoute => true,
            FlowNodeType.CondDogfoodRoute => true,

            _ => false
        };

        /// <summary>
        /// 取该节点实际生效的用户名（空 = 用流程绑定的账号）。
        /// </summary>
        public static string EffectiveUser(FlowNode node, AutomationFlow flow, AppConfig? cfg)
        {
            string u = (node?.Get("user") ?? "").Trim();
            if (u.Length > 0) return SessionService.BareName(u);
            try
            {
                return (cfg ?? App.Config).Accounts
                    .FirstOrDefault(a => a.Id == flow?.AccountId)?.UserName ?? "";
            }
            catch { return ""; }
        }

        /// <summary>该节点背后的账号是否已绑定 BGI 路径。</summary>
        public static bool IsSatisfied(FlowNode node, AutomationFlow flow, AppConfig? cfg)
        {
            if (!NeedsBoundBgi(node.Type)) return true;
            string user = EffectiveUser(node, flow, cfg);
            return user.Length > 0 && BgiPathResolver.IsPinned(user);
        }

        /// <summary>
        /// 可用于「日志类节点目标用户」下拉的账号（只列已绑定）。
        /// ★ 留「本账号」由调用方自己判断——本账号未绑定时也不能列。
        /// </summary>
        public static List<Account> BoundAccounts(AppConfig? cfg = null)
            => AccountScope.Accounts(cfg)
                .Where(a => a != null && BgiPathResolver.IsPinned(a.UserName ?? ""))
                .ToList();

        /// <summary>未绑定时的统一提示文案。</summary>
        public const string UnboundHint = "该账号未绑定 BGI 路径，无法读取日志数据；请先到「设置 → BetterGI 路径」绑定。";
    }
}
