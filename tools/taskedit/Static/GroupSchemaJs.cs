using System;

namespace SoulLink.TaskEdit
{
    // 配置组设置的字段表（真实结构，从 GroupSettingsJs 拆出控制体积）。
    internal static partial class TaskEditStatic
    {
        private const string GroupSchemaJs = """
'use strict';
(function (TE) {

  TE.group.SECTIONS = [
    {
      id: 'pathing-main', title: '地图追踪行走配置', path: ['pathingConfig'],
      fields: [
        ['enabled', '启用地图追踪', 'bool'],
        ['autoPickEnabled', '行进中自动拾取', 'bool'],
        ['partyName', '队伍名', 'text'],
        ['mainAvatarIndex', '主角色序号', 'num'],
        ['guardianAvatarIndex', '护盾角色序号', 'num'],
        ['isVisitStatueBeforeSwitchParty', '换队前先到神像', 'bool'],
        ['recoverTiming', '恢复时机', 'num'],
        ['onlyInTeleportRecover', '仅在传送时恢复', 'bool'],
        ['jsScriptUseEnabled', '允许 JS 脚本使用', 'bool'],
        ['soloTaskUseFightEnabled', '独立任务使用战斗流程', 'bool'],
        ['distance', '寻路距离', 'num'],
        ['approachStopDistance', '接近停止距离', 'num'],
        ['travelMode', '移动模式', 'num'],
        ['autoRunEnabled', '自动奔跑', 'bool'],
        ['autoSkipEnabled', '自动跳过对话', 'bool'],
        ['quicklySkip', '快速跳过', 'bool'],
        ['skipDuring', '跳过间隔', 'num'],
        ['switchToWalkEnabled', '切换为行走', 'bool'],
        ['useGadgetIntervalMs', '使用小道具间隔(ms)', 'num'],
        ['hideOnRepeat', '重复时隐藏', 'bool'],
        ['teapotHurryOnAvatar', '尘歌壶加速角色', 'text'],
        ['newHurryOnAvatar', '新加速角色', 'text'],
        ['useNewHurrySystem', '使用新加速系统', 'bool'],
        ['mwkFlyEnabled', '玛薇卡飞行', 'bool'],
        ['mwkJumpFlyEnabled', '玛薇卡跳跃飞行', 'bool'],
        ['mwkJumpFlyDistance', '跳跃飞行距离', 'num'],
        ['mwkJumpFlyIntervalSeconds', '跳跃飞行间隔(s)', 'num'],
        ['mwkDisableSprintEnabled', '禁用冲刺', 'bool'],
        ['mwkJumpFlySprintCount', '跳跃飞行冲刺次数', 'num'],
        ['recoverAvatarIndex', '恢复角色序号', 'num'],
        ['combatScriptEndDelayMs', '战斗脚本结束延迟(ms)', 'num'],
        ['enablePickupTargetCount', '启用拾取目标计数', 'bool'],
        ['disableAutoFetchDispatch', '禁用自动派遣', 'bool'],
        ['redBloodSwitchOnly', '仅红血切换', 'bool'],
        ['autoEatEnabled', '自动吃食物（总开关）', 'bool'],
        ['autoFightEnabled', '自动战斗（总开关）', 'bool']
      ]
    },
    {
      id: 'fight', title: '战斗配置（pathingConfig.autoFightConfig）', path: ['pathingConfig', 'autoFightConfig'],
      note: '共 47 个字段，此处列出全部可编辑项；finishDetectConfig 单独一区。',
      fields: [
        ['useOfficialAutoFight', '使用公版自动战斗引擎', 'bool', 'teabag'],
        ['strategyName', '战斗策略', 'text'],
        ['teamNames', '强制指定队伍', 'text'],
        ['autoCombatEq', '自动战斗装备', 'bool'],
        ['fightFinishDetectEnabled', '自动检测战斗结束', 'bool'],
        ['actionSchedulerByCd', '按技能CD优化出招', 'textarea'],
        ['mavuikaMotorcycleCheckEnabled', '玛薇卡摩托检查', 'bool'],
        ['arlecchinoBurstLowHpGateEnabled', '仆人低血量大招门控', 'bool'],
        ['arlecchinoAutoEnabled', '仆人自动', 'bool'],
        ['qiKong', '气孔', 'num'],
        ['skillCdForQ', 'Q 技能CD', 'num'],
        ['chascaXSensitivity', '恰斯卡 X 灵敏度', 'num'],
        ['chascaYSensitivity', '恰斯卡 Y 灵敏度', 'num'],
        ['chascaLegendaryRotateInterval', '恰斯卡转圈间隔', 'num'],
        ['chascaRotateCountLimit', '恰斯卡转圈上限', 'num'],
        ['specializedFrameIntervalMs', '专用帧间隔(ms)', 'num'],
        ['chascaBulletThreshold', '恰斯卡子弹阈值', 'num'],
        ['chascaSequenceSlotCount', '恰斯卡序列槽位', 'num'],
        ['sandroneChargeTimeSequence', '桑多涅蓄力序列', 'text'],
        ['sandroneChargePreAimX', '桑多涅预瞄X', 'num'],
        ['sandroneChargeRotateSpeed', '桑多涅转速', 'num'],
        ['onlyPickEliteDropsMode', '只拾取精英掉落', 'enum',
          ['Closed', 'AllowAutoPickupForNonElite', 'DisableAutoPickupForNonElite']],
        ['pickDropsAfterFightEnabled', '战后扫描掉落', 'bool'],
        ['pickDropsAfterFightSeconds', '战后扫描时长(s)', 'num'],
        ['battleThresholdForLoot', '拾取战斗阈值', 'num'],
        ['kazuhaPickupEnabled', '万叶拾取', 'bool'],
        ['guardianAvatar', '护盾角色', 'text'],
        ['guardianCombatSkip', '护盾角色跳过战斗', 'bool'],
        ['guardianAvatarHold', '护盾角色长按', 'bool'],
        ['burstEnabled', '自动放大招', 'bool'],
        ['expKazuhaPickup', '经验万叶拾取', 'bool'],
        ['qinDoublePickUp', '琴双拾取', 'bool'],
        ['swimmingEnabled', '游泳检测', 'bool'],
        ['kazuhaPartyName', '万叶队伍名', 'text'],
        ['timeout', '战斗超时(秒)', 'num'],
        ['takeMedicineEnabled', '自动吃药', 'bool'],
        ['medicineInterval', '吃药间隔(ms)', 'num'],
        ['checkInterval', '检查间隔(ms)', 'num'],
        ['recoverMaxCount', '最大恢复次数', 'num'],
        ['endBloodCheackEnabled', '结束血量检查', 'bool'],
        ['qRecoverAvatar', 'Q 恢复角色', 'bool'],
        ['useEqList', '使用装备列表', 'text'],
        ['useSkillList', '使用技能列表', 'text'],
        ['kazuhaTime', '万叶时间', 'num']
      ]
    },
    {
      id: 'fightfinish', title: '战斗结束检测（autoFightConfig.finishDetectConfig）',
      path: ['pathingConfig', 'autoFightConfig', 'finishDetectConfig'],
      fields: [
        ['fastCheckEnabled', '快速检查', 'bool'],
        ['rotateFindEnemyEnabled', '转圈找敌', 'bool'],
        ['fastCheckParams', '快速检查参数', 'text'],
        ['fastCheckDelay', '快速检查延迟', 'num'],
        ['checkEndDelay', '结束检查延迟', 'text'],
        ['beforeDetectDelay', '检测前延迟', 'num'],
        ['rotaryFactor', '转圈系数(1-13)', 'num'],
        ['isFirstCheck', '首次检查', 'bool'],
        ['goDistance', '前进距离', 'num'],
        ['checkBeforeBurst', '大招前检查', 'bool'],
        ['rotationMode', '转圈模式', 'bool'],
        ['endModel', '结束模型', 'bool'],
        ['paimonEndModel', '派蒙结束模型', 'bool'],
        ['paimonEndCheckEnabled', '派蒙结束检查', 'bool'],
        ['paimonEndCheckDelay', '派蒙结束检查延迟', 'num'],
        ['doubleEndEnbled', '双重结束', 'bool'],
        ['doubleEndDelay', '双重结束延迟', 'num'],
        ['fightWaitNotEndTime', '等待未结束时间', 'num'],
        ['isReturnToFightPointDistanceLegal', '回战斗点距离合法', 'bool'],
        ['returnToFightPointEnabled', '返回战斗点', 'bool'],
        ['returnToFightPointIntervalMs', '返回间隔(ms)', 'num'],
        ['returnToFightPointTriggerDistance', '触发距离', 'num'],
        ['returnToFightPointStopDistance', '停止距离', 'num'],
        ['returnToFightPointTimeTriggerEnabled', '按时间触发返回', 'bool'],
        ['returnToFightPointTimeTriggerSeconds', '时间触发秒数', 'num'],
        ['skipFightEndCheckWhenEnemyVisible', '敌人可见时跳过检查', 'bool'],
        ['checkAfterSwitchAvatar', '换人后检查', 'bool'],
        ['blockCheckBeforeBattleSeconds', '战前阻断检查秒数', 'num']
      ]
    },
    {
      id: 'eatofficial', title: '自动吃食物（pathingConfig.autoEatConfig）',
      path: ['pathingConfig', 'autoEatConfig'],
      fields: [
        ['enabled', '启用自动吃食物', 'bool'],
        ['showNotification', '显示通知', 'bool'],
        ['checkInterval', '检查间隔(ms)', 'num'],
        ['eatInterval', '进食间隔(ms)', 'num'],
        ['testFoodName', '测试食物名', 'text'],
        ['defaultAtkBoostingDishName', '默认攻击料理', 'text'],
        ['defaultAdventurersDishName', '默认冒险家料理', 'text'],
        ['defaultDefBoostingDishName', '默认防御料理', 'text']
      ]
    },
    {
      id: 'fightofficial', title: '公版战斗引擎（pathingConfig.autoFightOfficialConfig）',
      path: ['pathingConfig', 'autoFightOfficialConfig'],
      note: '「使用公版自动战斗引擎」= true 时生效的一套独立配置。',
      fields: [
        ['strategyName', '战斗策略', 'text'],
        ['teamNames', '强制指定队伍', 'text'],
        ['fightFinishDetectEnabled', '自动检测战斗结束', 'bool'],
        ['actionSchedulerByCd', '按技能CD优化出招', 'textarea'],
        ['onlyPickEliteDropsMode', '只拾取精英掉落', 'enum',
          ['Closed', 'AllowAutoPickupForNonElite', 'DisableAutoPickupForNonElite']],
        ['pickDropsAfterFightEnabled', '战后扫描掉落', 'bool'],
        ['pickDropsAfterFightSeconds', '战后扫描时长(s)', 'num']
      ]
    },
    {
      id: 'shell', title: 'Shell（config.shellConfig）', path: ['shellConfig'],
      fields: [
        ['disable', '禁用', 'bool'],
        ['timeout', '超时(ms)', 'num'],
        ['noWindow', '不显示窗口', 'bool'],
        ['output', '输出', 'text']
      ]
    },
    {
      id: 'pickcfg', title: '配置组内拾取（config.autoPickConfig）', path: ['autoPickConfig'],
      fields: [
        ['whiteListEnabled', '白名单模式', 'bool'],
        ['blackListEnabled', '黑名单模式', 'bool']
      ]
    }
  ];

})(window.TE);
""";
    }
}
