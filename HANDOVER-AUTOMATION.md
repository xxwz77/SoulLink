# SoulLink 自动化流程交接文档

**更新时间**：2026-11-18 13:25  
**工作目录**：`E:\Documents\deepseek-harness\SoulLink`

## 当前状态

- 生命周期、显式分支收尾、END 日界、共享投影、画笔与状态灯回归已通过。
- 全部检查（含真实鼠标拖拽/点击连线、真实编辑器渲染状态灯）与 Release 构建通过。
- 13:55 发布：66,331,591 字节，SHA256 `F44247C3296D8D4AD16E45D7BC841FA2E2B6FDB94BC3850C448904B88D5101FB`；启动无新增错误。
- `dist/stable/` 未修改；生产 `SoulLink.exe` 仍为 66,183,320 字节，时间 2026-11-16 22:19:29。
- 大量既有未提交 RDP、远程管理、数据中心改动均保留，没有重置或覆盖。
- 用户尚未实际操作验证连续画笔及新编辑器布局；自动化验证不等同于人工验收。

## 已固定的运行语义

1. 实时进程、会话等状态逐轮判断；日志、定时、手动等瞬时信号只在本轮运行内保留，无超时。
2. 运行选择持久化在 `RunDayKey`、`WaitingActions` 与 `RunningActions`。串行等待末端，普通扇出并行等待全部。
3. 新“选择分支”触点 `CondChoice` 只走一路；普通模块多出口默认并行。同刻多路满足时按连线列表优先级选择第一路。
4. **日界只属于 END 节点**（`FlowNode.DayBoundaryHour`，00:00/04:00）。有 END：完整收尾后写入今日标记，当天不再扫描；到日界自动清除标记并开始新一轮。无 END：不设日界，每轮完成清状态后可再次触发（循环执行）。
5. END 节点属性显示“今日已执行”，并提供“清除今日已执行标记”，用户可当天重跑一次（写入 `RunResetRequested`）。
6. 状态灯走独立的只读预览（`FlowPreview` / `Canvas.SetLiveStates`），即使流程校验失败或尚未保存也会亮；预览不写任何运行字段、不执行动作。
6. 狗粮、配置组、统计从同一原始采集派生，按 `(账号, 业务日, 日界)` 缓存；数据中心明确显示日界。
7. 进程所有者由 `ProcessOwnerSnapshot` 每轮共享；正则编译有界缓存；触点不再逐流程读文件/枚举进程。
8. 外部动作先保存执行意图。若进程在结果保存前退出，重启显示“结果待确认”，不自动重放；核对后可手动重试。
9. `LifecycleVersion=1` 显式迁移旧实验状态：保留明确 END 锁存，保守重置旧等待进度。新流程默认 00:00；旧 Dogfood 默认 04:00 不变。
11. 连线支持两种鼠标方式：拖拽下方出口；或依次点击起点、目标模块（端口已放大到 24px）。画笔模式同样可点击连续连线。
12. 动作返回成功表示执行请求成功，不代表 BGI 配置组等远程长任务自身已跑完；需要用状态/日志条件等待实际完成。

## 性能记录

`BusinessDayCheck` 对同一已采集账号日志共享投影，1/10/100 个流程的源读取次数均为 3；100 个缓存读取约 0.025–0.055 ms。该数值是独立投影微基准，不是包含 Windows 会话/进程查询的整机扫描耗时。

## 验证

必跑命令：

```powershell
dotnet run -c Release --project tools\layout-check\PlcLogicCheck.csproj
dotnet run -c Release --project tools\layout-check\PlcScanCheck.csproj
dotnet run -c Release --project tools\layout-check\PlcLogMatcherCheck.csproj
dotnet run -c Release --project tools\layout-check\BranchCheck.csproj
dotnet run -c Release --project tools\layout-check\ValidatorCheck.csproj
dotnet run -c Release --project tools\layout-check\PlcConfigCheck.csproj
dotnet run -c Release --project tools\layout-check\LifecycleCheck.csproj
dotnet run -c Release --project tools\layout-check\BusinessDayCheck.csproj
dotnet run -c Release --project tools\layout-check\FlowCanvasCheck.csproj
dotnet run -c Release --project tools\layout-check\FlowEditorCheck.csproj
dotnet run -c Release --project tools\layout-check\BgiDataCenterCheck.csproj
dotnet run -c Release --project tools\layout-check\BgiStartGuardCheck.csproj
dotnet run -c Release --project tools\layout-check\ChannelDiscoveryCheck.csproj
dotnet run -c Release --project tools\layout-check\AutomationDataCheck.csproj
dotnet run -c Release --project tools\layout-check\TimeoutCheck.csproj
dotnet build -c Release --no-restore
```

## 发布约束与发布记录

- 日常发布仅替换 `dist/SoulLink.exe`，禁止运行会删除整个 dist 的旧 `build.bat`。
- `dist/stable/` 不得擅动。发布前关闭正在运行的开发版，先暂存发布并校验 hash，再复制。
- 旧开发版备份：`releases/automation-pre-publish-20261118-1320/SoulLink.exe`。
- 首次生命周期发布（13:18:59）：66,328,651 字节，SHA256 `64DC6FFF82074CB4B33D21E18BD80B6E62532A922C9DE38754787A2D93E24B3A`。
- 启动检查发现大厅空 FontFamily 旧问题，已做局部修复；最终全回归、编辑器真实控件测试和暂存发布通过。
- 最终发布（13:24:08）：66,328,844 字节，SHA256 `5690963D696C360870ECE1A2F3745C28729336BC48C481C2DD7C206A8AC8AD6F`。
- 最终 boot.log：2026-11-18 13:24:31 启动，39 模块、22 条件、14 动作、1 END；主窗口正常显示，发布后 error.log 新增 0 字节。
- 工作区未提交，不要把源码存在或测试通过等同于人工完整验收。
