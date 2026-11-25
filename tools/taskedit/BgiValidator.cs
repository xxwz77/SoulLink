using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 字段级校验（设计文档 §B / §4.3 源码规则）+ 能力集校验（§6.4）。
    ///
    /// ── 校验规则来源（源码级）────────────────────────────────────
    ///   OneDragonFlowConfig 的 partial void On*Changed：
    ///     AutoBossRunCount < 1 → 回 1；AutoBossTotalRunCountLimit / CompletedRunCount /
    ///     ReviveRetryCount < 0 → 回 0；AutoBossSpecifyRunCount=false 时清 transient/fragile。
    ///   ScriptGroupProject.Type ∈ {Javascript, KeyMouse, Pathing, Shell, SoloTask}
    ///   Status ∈ {Enabled, Disabled}
    /// </summary>
    internal static class BgiValidator
    {
        internal sealed class Issue
        {
            internal string Path = "";
            internal string Message = "";
            internal bool Blocking = true;   // 是否阻断保存
        }

        internal static readonly string[] ProjectTypes = { "Javascript", "KeyMouse", "Pathing", "Shell", "SoloTask" };
        internal static readonly string[] ProjectStatuses = { "Enabled", "Disabled" };
        internal static readonly string[] Schedules = { "Daily", "EveryTwoDays", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        /// <summary>茶包独有字段（公版无）。含 autoFightConfig.useOfficialAutoFight 与角色特化字段。</summary>
        internal static readonly string[] TeabagOnlyFields =
        {
            "config.pathingConfig.autoFightConfig.useOfficialAutoFight",
            "config.pathingConfig.autoFightConfig.chascaXSensitivity",
            "config.pathingConfig.autoFightConfig.chascaYSensitivity",
            "config.pathingConfig.autoFightConfig.chascaLegendaryRotateInterval",
            "config.pathingConfig.autoFightConfig.chascaRotateCountLimit",
            "config.pathingConfig.autoFightConfig.chascaBulletThreshold",
            "config.pathingConfig.autoFightConfig.chascaSequenceSlotCount",
            "config.pathingConfig.autoFightConfig.sandroneChargeTimeSequence",
            "config.pathingConfig.autoFightConfig.sandroneChargePreAimX",
            "config.pathingConfig.autoFightConfig.sandroneChargeRotateSpeed",
            "config.pathingConfig.autoFightConfig.arlecchinoBurstLowHpGateEnabled",
            "config.pathingConfig.autoFightConfig.arlecchinoAutoEnabled",
            "config.pathingConfig.autoFightConfig.mavuikaMotorcycleCheckEnabled",
            "config.pathingConfig.autoFightConfig.qiKong",
            "config.pathingConfig.autoFightConfig.skillCdForQ",
            "config.pathingConfig.autoFightConfig.specializedFrameIntervalMs",
            "config.pathingConfig.autoFightConfig.onlyPickEliteDropsMode",
            "config.pathingConfig.autoFightConfig.finishDetectConfig",
        };

        /// <summary>校验一个配置组（返回问题列表）。</summary>
        internal static List<Issue> ValidateGroup(JsonNode node)
        {
            var issues = new List<Issue>();
            if (node == null) { issues.Add(new Issue { Path = "$", Message = "配置组为空" }); return issues; }

            // name 必填
            if (string.IsNullOrWhiteSpace(node["name"]?.GetValue<string>()))
                issues.Add(new Issue { Path = "name", Message = "配置组名不能为空" });

            // projects 数组：Type / Status 枚举合法性
            if (node["projects"] is JsonArray arr)
            {
                int i = 0;
                foreach (var p in arr.OfType<JsonObject>())
                {
                    string basePath = "projects[" + i + "]";
                    string type = p["type"]?.GetValue<string>() ?? "";
                    if (type.Length > 0 && !ProjectTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                        issues.Add(new Issue { Path = basePath + ".type", Message = "非法项目类型：" + type });
                    string status = p["status"]?.GetValue<string>() ?? "";
                    if (status.Length > 0 && !ProjectStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
                        issues.Add(new Issue { Path = basePath + ".status", Message = "非法状态：" + status });
                    string schedule = p["schedule"]?.GetValue<string>() ?? "";
                    if (schedule.Length > 0 && !Schedules.Contains(schedule, StringComparer.OrdinalIgnoreCase)
                        && !LooksLikeCron(schedule))
                        issues.Add(new Issue { Path = basePath + ".schedule", Message = "非法执行周期：" + schedule });
                    i++;
                }
            }
            return issues;
        }

        /// <summary>校验一条龙（AutoBoss 范围规则，源码级）。</summary>
        internal static List<Issue> ValidateOneDragon(JsonNode node)
        {
            var issues = new List<Issue>();
            if (node == null) { issues.Add(new Issue { Path = "$", Message = "一条龙为空" }); return issues; }

            if (node["Name"] is JsonNode n && string.IsNullOrWhiteSpace(n.GetValue<string>()))
                issues.Add(new Issue { Path = "Name", Message = "一条龙名不能为空" });

            // AutoBoss 范围
            CheckInt(node, "AutoBossRunCount", v => v < 1, issues, "AutoBossRunCount 必须 ≥1");
            CheckInt(node, "AutoBossTotalRunCountLimit", v => v < 0, issues, "AutoBossTotalRunCountLimit 不能为负");
            CheckInt(node, "AutoBossCompletedRunCount", v => v < 0, issues, "AutoBossCompletedRunCount 不能为负");
            CheckInt(node, "AutoBossReviveRetryCount", v => v < 0, issues, "AutoBossReviveRetryCount 不能为负");
            return issues;
        }

        private static void CheckInt(JsonNode node, string key, Func<int,bool> bad, List<Issue> issues, string msg)
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<int>(out int val) && bad(val))
                issues.Add(new Issue { Path = key, Message = msg });
        }

        private static bool LooksLikeCron(string schedule)
        {
            // BGI 自定义周期是 Cron 表达式（至少含一个空格或 *）
            return schedule.IndexOf(' ', StringComparison.Ordinal) >= 0 || schedule.Contains('*');
        }

        /// <summary>
        /// 能力集校验（§6.4）：目标版本不支持当前配置里的字段时，报出「字段 X 在 <版本> 上不支持」。
        /// 返回问题列表（每条 Message 里带「保留 / 剔除」提示）。
        /// </summary>
        internal static List<Issue> ValidateCapabilities(JsonNode node, string channel, string baseVersion)
        {
            var issues = new List<Issue>();
            if (node == null || string.Equals(channel, "teabag", StringComparison.OrdinalIgnoreCase)) return issues;

            // 非茶包（公版/未知）→ 茶包独有字段全部报出
            foreach (string field in TeabagOnlyFields)
            {
                if (HasPath(node, field))
                {
                    issues.Add(new Issue
                    {
                        Path = field,
                        Message = "字段 " + field + " 在 " + (channel == "unknown" ? "未知" : "公版 " + baseVersion) + " 上不支持（茶包独有）。请选择：保留（BGI 会忽略）或 剔除（删除该字段）",
                        Blocking = false,
                    });
                }
            }
            return issues;
        }

        /// <summary>按点分路径探测 node 里是否有该字段。</summary>
        private static bool HasPath(JsonNode node, string dotPath)
        {
            JsonNode cur = node;
            foreach (string seg in dotPath.Split('.'))
            {
                if (cur is not JsonObject obj) return false;
                if (!obj.TryGetPropertyValue(seg, out var next)) return false;
                cur = next;
            }
            return true;
        }

        /// <summary>剔除能力集不支持字段（给「剔除」操作）。</summary>
        internal static void StripTeabagOnly(JsonNode node)
        {
            foreach (string field in TeabagOnlyFields)
            {
                var segs = field.Split('.');
                JsonNode cur = node;
                for (int i = 0; i < segs.Length - 1; i++)
                {
                    if (cur is not JsonObject obj) break;
                    if (!obj.TryGetPropertyValue(segs[i], out var next)) break;
                    cur = next;
                }
                if (cur is JsonObject parent && parent.TryGetPropertyValue(segs[^1], out _))
                    parent.Remove(segs[^1]);
            }
        }
    }
}
