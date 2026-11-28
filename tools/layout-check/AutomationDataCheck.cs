using System;using SoulLink.Services;
public static class AutomationDataCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main()
 {
  string log="[2026-11-18 09:00:00.000] [INF] 配置组 \"A组\" 加载完成，共3个脚本，开始执行\n[2026-11-18 09:10:00.000] [INF] 配置组 \"A组\" 执行结束";
  var t=new ScriptGroupTracker();t.Update(log);var r=t.Get("A组")!;
  Check(r.State==ScriptGroupState.Finished,"配置组状态供条件与页面共用");
  Check(r.StartedAt==new DateTime(2026,11,18,9,0,0)&&r.EndedAt==new DateTime(2026,11,18,9,10,0),"开始结束使用日志真实时间而非读取时间");
  Check(r.TimeIsExact&&r.Duration==TimeSpan.FromMinutes(10),"页面可显示精确事件时间和耗时");
  var beforeMidnight=new DateTime(2026,11,25,23,59,59,123);
  var afterMidnight=new DateTime(2026,11,26,0,0,1,456);
  const string firstFile="better-genshin-impact20261125.log", secondFile="better-genshin-impact20261126.log";
  const string startBody="配置组 \"共享配置组\" 加载完成，共3个脚本，开始执行\r\n";
  const string endBody="配置组 \"共享配置组\" 执行结束\r\n";
  var firstAccount=new ScriptGroupTracker(); var secondAccount=new ScriptGroupTracker();
  firstAccount.Update(new BgiLogBatch("[23:59:59.123] [INF] header\r\n"+startBody,firstFile));
  secondAccount.Update(new BgiLogBatch("[00:00:01.456] [INF] header\r\n"+startBody,secondFile));
  firstAccount.Update(new BgiLogBatch(endBody,firstFile,beforeMidnight));
  var firstRun=firstAccount.Get("共享配置组")!; var secondRun=secondAccount.Get("共享配置组")!;
  Check(firstRun.StartedAt==beforeMidnight&&firstRun.EndedAt==beforeMidnight,"续行跨日志块继承同一文件的事件时间");
  Check(secondRun.StartedAt==afterMidnight&&secondRun.State==ScriptGroupState.Running,"同名配置组按账号和日志文件日期独立跟踪");
  var interrupted=new ScriptGroupTracker();
  interrupted.Update("[2026-11-18 10:00:00] 配置组 \"砍树\" 加载完成，共2个脚本，开始执行");
  interrupted.Update("[2026-11-18 10:05:00] 配置组 \"CD采集-额外狗粮\" 加载完成，共1个脚本，开始执行");
  Check(interrupted.Get("砍树")!.State==ScriptGroupState.Interrupted,"砍树被后续配置组接替后显示已中断而非执行中");
  Check(interrupted.Get("CD采集-额外狗粮")!.State==ScriptGroupState.Running,"只有最新启动的配置组显示执行中");
  Check(!ScriptGroupTracker.ValidGroupName("The operation was canceled."),"异常对象名不被当成配置组");
  var live=new BgiStatus();
  DateTime eventTime=new(2026,11,26,12,0,0);
  live.Update(new BgiLogBatch("[12:00:00.000] [INF] [Test]\r\n成功切换角色:\"菲林斯\"\r\n",secondFile));
  live.Update(new BgiLogBatch("[12:00:01.000] [INF] [Test]\r\n\"菲林斯\" 点按元素战技，cd:3.7 秒\r\n",secondFile));
  var attempt=live.GetLiveActivity(eventTime.AddSeconds(2));
  Check(attempt.Character?.Text=="菲林斯"&&attempt.Character.At==eventTime&&attempt.Skill?.Outcome=="尝试","角色和战技按日志头时间记录，按键不标记释放成功");
  live.Update(new BgiLogBatch("[12:00:02.500] [INF] [Test]\r\n优先第 \"1\" 盾奶位 \"伊涅芙\" 释放战技：\"成功\"\r\n",secondFile));
  live.Update(new BgiLogBatch("[12:00:03.000] [INF] [Test]\r\n交互或拾取：\"使用回血料理\"\r\n",secondFile));
  var actual=live.LiveActivity.Filter(eventTime.AddSeconds(4));
  Check(actual.Skill?.Outcome=="成功"&&actual.Skill.Text=="伊涅芙 释放战技"&&actual.Skill.At==eventTime.AddSeconds(2.5)
      &&actual.Food?.Text=="使用回血料理"&&actual.Action?.Text=="伊涅芙 · 释放战技","明确施放结果与料理用各自的日志时间");
  Check(live.LiveActivity.Filter(eventTime.AddSeconds(15)).Action==null&&live.LiveActivity.Filter(eventTime.AddSeconds(15)).Food!=null
      &&live.LiveActivity.Filter(eventTime.AddMinutes(3)) is { Character.Text: "菲林斯", Action: null, Skill: null, Food: null },
      "动作、战技和料理到期，已确认的出战角色保留");
  var unstamped=new BgiStatus();
  unstamped.Update(new BgiLogBatch("动作：下落攻击\r\n",secondFile));
  Check(unstamped.GetLiveActivity(eventTime)==default,"没有日志时间戳的动作不进入实时状态");
  live.Update(new BgiLogBatch("[12:00:04.000] [INF] 动作：下落攻击\n",secondFile));
  Check(live.GetLiveActivity(eventTime.AddSeconds(4)).Action?.Text=="菲林斯 · 下落攻击","带时间的普通动作进入实时状态");
  live.Update(new BgiLogBatch("[12:00:05.000] [INF] 动作：下落攻击 结束\n",secondFile));
  Check(live.GetLiveActivity(eventTime.AddSeconds(5)).Action==null,"已经结束的明确动作立即清除");
  live.Update(new BgiLogBatch("[12:00:06.000] [INF] 配置组 \"新组\" 加载完成，共1个脚本，开始执行\n",secondFile));
  Check(live.GetLiveActivity(eventTime.AddSeconds(6)) is { Character.Text: "菲林斯", Action: null, Skill: null, Food: null },
      "新配置组清空上一组的动作，同一场景已确认角色仍保留");
  live.Update(new BgiLogBatch("[12:00:07.000] [INF] \"伊涅芙\" 当前出战\n[12:00:08.000] [INF] 任务已停止\n",secondFile));
  Check(live.GetLiveActivity(eventTime.AddSeconds(8))==default&&live.Stopped,"停止立即清理已确认的出战角色");

  var mapLive=new BgiStatus();
  void MapLine(int seconds, string line) => mapLive.Update(new BgiLogBatch(
      $"[12:00:{seconds:00}.000] [INF] [Test]\r\n{line}\r\n",secondFile));
  MapLine(0,"成功切换角色:\"菲林斯\"");
  Check(mapLive.GetLiveActivity(eventTime).Action?.Text=="切换角色 菲林斯","明确切换成功可短暂作为最新动作");
  MapLine(1,"→ 开始执行地图追踪任务: \"地图路线.json\"");
  Check(mapLive.GetLiveActivity(eventTime.AddSeconds(1)).Action?.Text=="菲林斯 · 正在执行地图追踪","地图任务明确开始后展示已确认角色和地图追踪");
  MapLine(2,"动作：下落攻击");
  Check(mapLive.GetLiveActivity(eventTime.AddSeconds(2)).Action?.Text=="菲林斯 · 下落攻击","战斗动作优先于地图追踪");
  MapLine(3,"未识别到战斗结束");
  Check(mapLive.GetLiveActivity(eventTime.AddSeconds(3)).Action?.Text=="菲林斯 · 下落攻击","未识别到结束时保留战斗动作");
  MapLine(4,"检测到战斗结束，结束战斗操作线程");
  Check(mapLive.GetLiveActivity(eventTime.AddSeconds(4)).Action?.Text=="菲林斯 · 正在执行地图追踪","战斗结束后恢复地图追踪");
  MapLine(5,"动作：下落攻击");
  MapLine(6,"动作：下落攻击 结束");
  Check(mapLive.GetLiveActivity(eventTime.AddSeconds(6)).Action?.Text=="菲林斯 · 正在执行地图追踪","单个动作结束后恢复地图追踪");
  MapLine(7,"动作：下落攻击");
  MapLine(8,"成功切换角色:\"伊涅芙\"");
  Check(mapLive.GetLiveActivity(eventTime.AddSeconds(8)).Action?.Text=="伊涅芙 · 正在执行地图追踪","切换角色不重新归属上一角色的动作");
  MapLine(9,"优先第 \"1\" 盾奶位 \"菲林斯\" 释放战技：\"成功\"");
  MapLine(10,"交互或拾取：\"使用回血料理\"");
  var castMap=mapLive.GetLiveActivity(eventTime.AddSeconds(10));
  Check(castMap.Action?.Text=="菲林斯 · 释放战技"&&castMap.Character?.Text=="伊涅芙"&&castMap.Food?.At==eventTime.AddSeconds(10),
      "施放日志使用自己的角色，料理保留自己的时间");
  Check(mapLive.GetLiveActivity(eventTime.AddSeconds(21)).Action?.Text=="伊涅芙 · 正在执行地图追踪","战斗动作过期后恢复仍在运行的地图追踪");
  var continuedMap=new BgiStatus(); continuedMap.InheritMapActivityFrom(mapLive);
  var continued=continuedMap.GetLiveActivity(eventTime.AddSeconds(10));
  Check(continued.Action?.Text=="伊涅芙 · 正在执行地图追踪"&&continued.Skill==null&&continued.Food==null,"文件换日只继承地图追踪和已确认角色");
  MapLine(11,"→ 脚本执行结束: \"别的任务.json\"");
  Check(mapLive.GetLiveActivity(eventTime.AddSeconds(21)).Map!=null,"另一个脚本结束不清理当前地图追踪");
  MapLine(12,"→ 脚本执行结束: \"地图路线.json\", 耗时: 0分10.000秒");
  Check(mapLive.GetLiveActivity(eventTime.AddSeconds(12)).Action==null&&mapLive.LiveActivity.Map==null
      &&mapLive.GetLiveActivity(eventTime.AddSeconds(12)).Skill!=null,"匹配的路线完成后清除当前动作，保留独立的技能记录");
  var unknownActor=new BgiStatus();
  unknownActor.Update(new BgiLogBatch("[12:00:00.000] [INF] 动作：下落攻击\n",secondFile));
  Check(unknownActor.GetLiveActivity(eventTime).Action?.Text=="下落攻击","没有已确认角色时只展示原动作");
  var avatarLive=new BgiStatus();
  const string primaryScope="[Primary:S2:P410:T30000]", childScope="[ChildSession:S2:P420:T30001]";
  void AvatarLine(int seconds, string body, string scope=primaryScope) => avatarLive.Update(new BgiLogBatch(
      $"[13:00:{seconds:00}.000] [INF] {scope} BetterGenshinImpact.GameTask.Common.TaskControl\r\n{body}\r\n",secondFile));
  BgiLiveActivity ActorView(int seconds) => avatarLive.GetLiveActivity(new DateTime(2026,11,26,13,0,0).AddSeconds(seconds));
  AvatarLine(0,"→ 开始执行地图追踪任务: \"路线.json\"");
  AvatarLine(1,"[第 1/2 轮 联机身份] 开始第 1/3 条线路: \"路线.json\"，本线路预计用时 2分");
  AvatarLine(2,"传送完成，返回主界面");
  AvatarLine(3,"识别到的队伍角色:\"夜兰,纳西妲,雷电将军,枫原万叶\"");
  Check(ActorView(3).Character==null,"传送后的多人名单独立于当前角色");
  AvatarLine(4,"当前所有编号边缘均为白色（背景过白），通过内部黑色像素识别出战编号为2，存在黑色数字的角色编号有3个，总角色数量4",childScope);
  Check(ActorView(4).Character==null,"出战编号只和同一日志会话的名单匹配");
  AvatarLine(5,"当前所有编号边缘均为白色（背景过白），通过内部黑色像素识别出战编号为2，存在黑色数字的角色编号有3个，总角色数量4");
  Check(ActorView(5).Character?.Text=="纳西妲"&&ActorView(240).Action?.Text=="纳西妲 · 正在执行地图追踪",
      "出战序号落到真实队伍顺序，稳定角色超过两分钟不消失");
  AvatarLine(5,"成功切换角色:\"伊涅芙\"",childScope);
  Check(ActorView(5).Character?.Text=="纳西妲","子会话的切换角色日志不覆盖主会话已确认角色");
  AvatarLine(6,"切换角色失败，最后一次尝试，当前角色编号:-1，期望角色编号:4");
  Check(ActorView(6).Character==null,"明确识别为未知的出战槽位清理旧角色");
  AvatarLine(7,"成功切换角色：“枫原万叶”");
  AvatarLine(8,"传送完成，返回主界面");
  Check(ActorView(8).Character?.Text=="枫原万叶"&&ActorView(8).Character?.At.Second==7,"同世界传送保留已确认角色及原始时间");
  AvatarLine(9,"尝试切换至队伍: \"好感队\"");
  Check(ActorView(9).Character?.Text=="枫原万叶","队伍切换尝试不清理当前角色");
  AvatarLine(10,"切换队伍成功: \"好感队\"");
  Check(ActorView(10).Character==null&&avatarLive.Round=="1/2"&&avatarLive.RouteFile=="路线"&&avatarLive.RouteEta=="2分",
      "已切换队伍使旧角色无效，任务、路线和 ETA 保持");
  AvatarLine(11,"成功切换角色：“芙宁娜”");
  AvatarLine(12,"不在对方世界，开始尝试加入");
  Check(ActorView(12).Character==null,"加入他人世界之前撤下原世界角色");
  AvatarLine(13,"当前处于联机状态，且在别人世界中，联机人数3人");
  AvatarLine(14,"识别到的队伍角色:\"枫原万叶\"");
  Check(ActorView(14).Character?.Text=="枫原万叶"&&ActorView(14).Action?.Text=="枫原万叶 · 正在执行地图追踪",
      "三人联机客人只可控制一个角色，实际 A2 形式可确认出战角色");
  AvatarLine(15,"当前处于联机状态，且在别人世界中，联机人数2人");
  AvatarLine(16,"识别到的队伍角色:\"夜兰,枫原万叶\"");
  Check(ActorView(16).Character==null,"两人联机多角色时不默认第一个");
  AvatarLine(17,"当前出战角色未正确识别，通过出战标识推测角色编号为2");
  AvatarLine(18,"识别到的队伍角色:\"夜兰,茜特菈莉(Xia)\"");
  Check(ActorView(18).Character?.Text=="茜特菈莉","识别过程记录的出战标识与随后输出的名单按槽位匹配");
  AvatarLine(19,"识别到的队伍角色:\"夜兰,未知角色\"");
  AvatarLine(20,"切换角色失败，最后一次尝试，当前角色编号:2，期望角色编号:1");
  Check(ActorView(20).Character==null,"当前槽位未知时不使用目标角色或猜测");
  AvatarLine(21,"成功切换角色：“夜兰”");
  AvatarLine(22,"传送完成，返回主界面","[Primary:S2:P430:T31000]");
  Check(ActorView(22).Character==null,"新的主运行进程不继承上一进程角色");
  AvatarLine(23,"成功切换角色:\"夜兰\"","[Primary:S2:P430:T31000]");
  AvatarLine(24,"→ 开始执行JS脚本: \"同场景内的新脚本\"","[Primary:S2:P430:T31000]");
  AvatarLine(25,"→ 开始执行地图追踪任务: \"新路线.json\"","[Primary:S2:P430:T31000]");
  Check(ActorView(25).Action?.Text=="夜兰 · 正在执行地图追踪","同一进程内新脚本与路线启动保留已确认角色");
  var progress=new BgiStatus();
  progress.Update("[第 1/2 轮 联机身份] 开始第 1/3 条线路: \"同一路线.json\"，本线路预计用时 1分2秒，本轮预计剩余 8分\n");
  progress.Update("[第 1/2 轮] 开始第 1/3 条线路: \"同一路线.json\"\n");
  Check(progress.Round=="1/2"&&progress.RoleName=="联机身份"&&progress.RouteEta=="1分2秒"&&progress.RoundRemain=="8分"
      &&progress.LiveActivity.Character==null,"同路进度更新保留轮次和 ETA，联机 identity 不作游戏角色");
  progress.Update("[第 1/2 轮] 开始第 2/3 条线路: \"另外路线.json\"\n");
  Check(progress.RouteFile=="另外路线"&&progress.Round=="1/2"&&progress.RoundRemain=="8分"&&progress.RouteEta=="","路线切换清空上一条路线的 ETA");
  progress.Update("本线路预计用时 2分\n");
  Check(progress.RouteEta=="2分","独立的 ETA 正文补齐当前路线");
  progress.Update("→ 开始执行JS脚本: \"新任务\"\n");
  Check(progress.TaskName=="JS脚本"&&progress.RouteFile=="新任务"&&progress.Round==""&&progress.RouteEta=="","明确的新任务清理上一任务的轮次和预计用时");
  var unknown=new DogfoodRecord();unknown.Reason="找不到记录文件";Check(!unknown.IsValid&&unknown.Route==DogfoodEndingRoute.Unknown,"无狗粮记录不会误判为 A 或 B");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}