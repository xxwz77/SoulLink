using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SoulLink.Models;
using SoulLink.Services;

public static class PlcScanCheck
{
    static int fails;
    static void Check(bool ok,string text){Console.WriteLine((ok?"ok   ":"FAIL ")+text);if(!ok)fails++;}
    static FlowNode C(string id,FlowNodeType type=FlowNodeType.CondTimeRange)=>new(){Id=id,Kind=FlowNodeKind.Condition,Type=type};
    static FlowNode A(string id,FlowNodeType type=FlowNodeType.ActRunAction)=>new(){Id=id,Kind=FlowNodeKind.Action,Type=type};
    static FlowNode End(string id="end")=>new(){Id=id,Kind=FlowNodeKind.Terminate,Type=FlowNodeType.ActEndFlow,DayBoundaryHour=4};
    static bool dailyDaily(AutomationFlow f,string expected)=>f.DailyLatchKey==expected;
    static void Link(AutomationFlow f,FlowNode a,FlowNode b)=>FlowGraphRules.Connect(f,a,b,"",out _);
    static AutomationFlow Graph(bool end=false)
    {
        var f=new AutomationFlow();var c=C("c");var a=A("a");f.Nodes.AddRange(new[]{c,a});Link(f,c,a);
        if(end){var e=End();f.Nodes.Add(e);Link(f,a,e);}f.EnsurePlcModel();return f;
    }
    static Task<FlowActionResult> Ok(FlowNode n)=>Task.FromResult(new FlowActionResult{Ok=true,Message="ok"});
    static Task<FlowRunner.RunResult> Scan(AutomationFlow f,Func<FlowNode,bool> read,Action<FlowNode>? write=null,DateTime? at=null,Func<FlowActionResult,Task>? checkpoint=null)
        =>PlcScanEngine.ScanAsync(f,n=>Task.FromResult<PlcSignal>(read(n)),n=>{write?.Invoke(n);return Ok(n);},now:at,checkpoint:checkpoint);

    public static async Task<int> Main()
    {
        var t=new DateTime(2026,11,18,4,0,0);
        // 无 END：循环执行——条件重新满足就再次执行，不设日界。
        int count=0;var f=Graph();
        await Scan(f,_=>true,_=>count++,t);Check(count==1&&!f.IsDailyComplete,"无 END 完成一轮不写日界锁存");
        await Scan(f,_=>false,_=>count++,t.AddSeconds(1));Check(count==1&&!f.RuntimeStates["c"].Value,"完成后实时灯继续反映当前条件");
        await Scan(f,_=>true,_=>count++,t.AddSeconds(2));Check(count==2,"无 END 条件重新满足可循环执行");
        Check(count==2,"无 END 循环执行（每轮完成都会重新获取条件）");

        var log=C("log",FlowNodeType.CondLogContains);var state=C("state");var timedOut=A("a");
        var timed=new AutomationFlow();timed.Nodes.AddRange(new[]{log,state,timedOut});Link(timed,log,state);Link(timed,state,timedOut);timed.EnsurePlcModel();int timedCount=0;
        Task<FlowRunner.RunResult> Timed(bool hit,bool ready,DateTime at)=>Scan(timed,n=>n.Id=="log"?hit:ready,_=>timedCount++,at);
        await Timed(true,false,t);Check(timedCount==0&&timed.SatisfiedContacts.Contains("log"),"瞬时 A 先满足并保留，等待状态 B");
        var timedResult=await Timed(false,true,t.AddHours(3));Check(timedCount==1&&!timed.RuntimeStates["log"].Value,"无超时；锁存触点熄灭，状态 B 仍可继续");Console.WriteLine("  detail: "+string.Join(" | ",timedResult.Steps));
        await Timed(false,false,t.AddHours(4));Check(timedCount==1,"无 END 断开后不重复执行");
        await Timed(true,true,t.AddHours(5));Check(timedCount==2,"无 END 可持续循环触发");

        var process=C("process",FlowNodeType.CondProcessExists);process.Set("expect","存在");var missingAction=A("a");var waitAppear=new AutomationFlow();waitAppear.Nodes.AddRange(new[]{process,missingAction});Link(waitAppear,process,missingAction);waitAppear.EnsurePlcModel();int appeared=0;
        await Scan(waitAppear,_=>false,_=>appeared++,t);await Scan(waitAppear,_=>false,_=>appeared++,t.AddMinutes(10));await Scan(waitAppear,_=>true,_=>appeared++,t.AddMinutes(11));
        Check(appeared==1,"进程不存在时等待出现，方向正确");
        var waitGone=Graph();waitGone.FindNode("c")!.Type=FlowNodeType.CondProcessExists;waitGone.FindNode("c")!.Set("expect","不存在");int gone=0;
        await Scan(waitGone,_=>false,_=>gone++,t);await Scan(waitGone,_=>true,_=>gone++,t.AddMinutes(20));Check(gone==1,"进程存在时等待消失，方向正确");

        var parallel=new AutomationFlow();var pc=C("c",FlowNodeType.TrigLogMatch);var d1=A("delay1",FlowNodeType.ActDelay);d1.Set("seconds","0");var d2=A("delay2",FlowNodeType.ActDelay);d2.Set("seconds","10");var left=A("left");var right=A("right");var pe=End();parallel.Nodes.AddRange(new[]{pc,d1,d2,left,right,pe});Link(parallel,pc,d1);Link(parallel,pc,d2);Link(parallel,d1,left);Link(parallel,d2,right);Link(parallel,left,pe);Link(parallel,right,pe);parallel.EnsurePlcModel();var ran=new HashSet<string>();
        await Scan(parallel,_=>true,n=>ran.Add(n.Id),t);Check(ran.SetEquals(new[]{"left"})&&!parallel.RunComplete,"并行第一条完成后不提前收尾或写 END");
        ran.Clear();var copy=JsonSerializer.Deserialize<AutomationFlow>(JsonSerializer.Serialize(parallel))!;
        await Scan(copy,_=>false,n=>ran.Add(n.Id),t.AddSeconds(5));Check(ran.Count==0&&!copy.RunComplete,"重启保留并行选择，不重复成功动作或跳过等待");
        await Scan(copy,_=>false,n=>ran.Add(n.Id),t.AddSeconds(10));Check(ran.SetEquals(new[]{"right"})&&copy.RunComplete&&copy.DailyLatchKey=="2026-11-18","全部并行动作完成后才 END");

        var select=new AutomationFlow();var sc=C("c");sc.BranchMode=FlowBranchMode.Choice;var yes=C("yes");yes.Logic=FlowLogicOp.AND;var no=C("no");no.Logic=FlowLogicOp.AND;no.Negated=true;var sa=A("a");var sb=A("b");select.Nodes.AddRange(new[]{sc,yes,no,sa,sb});Link(select,sc,yes);Link(select,sc,no);Link(select,yes,sa);Link(select,no,sb);select.EnsurePlcModel();var selectedRan=new List<string>();
        var selectResult=await Scan(select,_=>true,n=>selectedRan.Add(n.Id),t);Check(selectedRan.SequenceEqual(new[]{"a"})&&!select.IsDailyComplete,"选择分支只执行满足的一路");Console.WriteLine("  select: "+string.Join("/",selectedRan)+" latch="+select.IsDailyComplete+" | "+string.Join(" | ",selectResult.Steps));

        // 有 END：日界来自 END 节点（00:00 / 04:00），到日界自动重置并重新开始。
        var daily=Graph(true);daily.EndNode!.DayBoundaryHour=4;int dailyCount=0;
        await Scan(daily,_=>true,_=>dailyCount++,new DateTime(2026,11,18,3,59,59));
        Check(dailyDaily(daily,"2026-11-17"),"04:00 前写入前一业务日，END 记录今日已执行");
        await Scan(daily,_=>true,_=>dailyCount++,new DateTime(2026,11,18,3,59,59).AddSeconds(1));Check(dailyCount==2,"到 END 日界自动重置并重新执行");
        await Scan(daily,_=>true,_=>dailyCount++,new DateTime(2026,11,18,12,0,0));Check(dailyCount==2,"END 同一日内不再扫描");
        daily.RunResetRequested=true;
        await Scan(daily,_=>true,_=>dailyCount++,new DateTime(2026,11,18,12,1,0));Check(dailyCount==3,"手动清除今日标记后可当天重跑");

        // 双向开关：先设为今日不运行，再恢复今日运行。
        daily.ToggleTodayRequested=false;
        await Scan(daily,_=>true,_=>dailyCount++,new DateTime(2026,11,18,12,2,0));
        Check(dailyCount==3&&daily.IsDailyComplete,"可手动设为今日不运行，且不再执行");
        await Scan(daily,_=>true,_=>dailyCount++,new DateTime(2026,11,18,12,3,0));Check(dailyCount==3,"设为今日不运行后持续不再扫描");
        daily.ToggleTodayRequested=true;
        var restored=await Scan(daily,_=>true,_=>dailyCount++,new DateTime(2026,11,18,12,4,0));
        Console.WriteLine("  restore: count="+dailyCount+" latch='"+daily.DailyLatchKey+"' complete="+daily.IsDailyComplete+" msg="+restored.Message+" | "+string.Join(" | ",restored.Steps));
        Check(dailyCount==4&&!daily.IsDailyComplete,"可手动恢复今日运行并立即执行");
        await Scan(daily,_=>true,_=>dailyCount++,new DateTime(2026,11,18,12,5,0));
        Check(dailyCount==5,"恢复后可持续运行直到再次到达 END");

        var fail=Graph(true);int attempts=0;
        async Task<FlowRunner.RunResult> Fail(bool value,bool ok,DateTime at)=>await PlcScanEngine.ScanAsync(fail,_=>Task.FromResult<PlcSignal>(value),_=>{attempts++;return Task.FromResult(new FlowActionResult{Ok=ok,Message="result"});},now:at);
        await Fail(true,false,t);await Fail(true,false,t.AddSeconds(1));Check(attempts==1&&!fail.RunComplete,"失败不盲重试且不提前 END");
        await Fail(false,true,t.AddSeconds(2));await Fail(true,true,t.AddSeconds(3));Check(attempts==2&&fail.RunComplete,"失败输入断开后允许重试");

        var unknown=Graph();unknown.FindNode("c")!.Negated=true;int unknownOuts=0;
        await PlcScanEngine.ScanAsync(unknown,_=>Task.FromResult(new PlcSignal(false,false)),n=>{unknownOuts++;return Ok(n);},now:t);Check(unknownOuts==0&&!unknown.RuntimeStates["c"].Available,"未知输入不能因 NOT 导通");

        var directEnd=new AutomationFlow();var dc=C("c");var de=End();directEnd.Nodes.AddRange(new[]{dc,de});Link(directEnd,dc,de);directEnd.EnsurePlcModel();
        await Scan(directEnd,_=>true,null,t);Check(directEnd.IsDailyComplete,"条件直达 END 无普通动作也完成并锁存");
        var isoA=Graph();var isoB=Graph();int isoCount=0;
        await PlcScanEngine.ScanAsync(isoA,_=>Task.FromResult<PlcSignal>(false),n=>{isoCount++;return Ok(n);},now:t);
        await PlcScanEngine.ScanAsync(isoB,_=>Task.FromResult<PlcSignal>(true),n=>{isoCount++;return Ok(n);},now:t);
        Check(isoA.SatisfiedContacts.Count==0&&!isoB.IsDailyComplete&&isoCount==1,"流程 A 等待不阻塞 B，无 END 循环状态互不污染");
        var interrupted=Graph();int interruptCount=0;
        try{await PlcScanEngine.ScanAsync(interrupted,_=>Task.FromResult<PlcSignal>(true),n=>{interruptCount++;throw new InvalidOperationException("进程在副作用后退出");},now:t);}catch(InvalidOperationException){}
        var interruptedCopy=JsonSerializer.Deserialize<AutomationFlow>(JsonSerializer.Serialize(interrupted))!;
        await PlcScanEngine.ScanAsync(interruptedCopy,_=>Task.FromResult<PlcSignal>(true),n=>{interruptCount++;return Ok(n);},now:t.AddSeconds(1));
        Check(interruptCount==1&&interruptedCopy.RunningActions["a"].Message=="结果待确认","外部结果未知时重启不自动重放动作");
        await PlcScanEngine.ScanAsync(interruptedCopy,_=>Task.FromResult<PlcSignal>(true),n=>{interruptCount++;return Ok(n);},manual:true,now:t.AddSeconds(2));
        Check(interruptCount==2&&!interruptedCopy.RunningActions.ContainsKey("a")&&!interruptedCopy.IsDailyComplete,"人工核对后可手动重试未知结果");
        var checkpointFail=Graph(true);int durableAttempts=0;int saveCalls=0;bool faultThrown=false;
        try
        {
            await PlcScanEngine.ScanAsync(checkpointFail,_=>Task.FromResult<PlcSignal>(true),n=>{durableAttempts++;return Ok(n);},now:t,
                checkpoint:r=>++saveCalls==3?Task.FromException(new IOException("disk full")):Task.CompletedTask);
        }
        catch(IOException){faultThrown=true;}
        Check(faultThrown&&durableAttempts==1&&saveCalls==3&&!checkpointFail.IsDailyComplete&&checkpointFail.DailyLatchKey.Length==0,"结果检查点失败立即中止，不声明 END 完成");
        int checkpoints=0;var persisted=Graph(true);
        await Scan(persisted,_=>true,null,t,_=>{checkpoints++;return Task.CompletedTask;});Check(checkpoints>=3,"运行准备、动作结果、完整收尾均持久化");
        Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
    }
}
