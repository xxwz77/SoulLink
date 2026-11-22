# 引用核对审计（CITATION-AUDIT）

> 日期：2026-11-22
> 作者：citation-auditor（dsh-team `soullink-stream-opt` / task `task_mwro086t3rcspv`）
> 核对对象：`STREAM-PERF-AUDIT-20261122.md`、`MOBILE-REMOTE-PLAN-20261122.md`（磁盘当前版本 v1.2）、`WEB-EXTENSION-PLAN-20261122.md`（补核，2026-11-22 18:09 已生成）。
> 方法：grep 全量抓取两份文档中形如 `文件:行号` / `L行号` 的引用（不抽样），逐条读真实文件对应行。
> 状态定义：MATCH=行号正确且内容一致；DRIFT=内容对得上但行号偏了（给出真实行号）；WRONG=内容对不上；MISSING=文件或行不存在。
> 统计口径：同一 file:line 在同一文档内重复出现合并为一条（出现次数注明），统计按合并后条目数。

---

## 一、STREAM-PERF-AUDIT-20261122.md（39 条）

| # | 引用（真实出现位置） | 状态 | 真实位置 | 说明 |
|---|---|---|---|---|
| 1 | `WebServer.cs:136-137`（文档 L16/L63/L104/L108，4×） | MATCH | Services/WebServer.cs L135-137 | 注释原文「每个命令在代理侧要 BitBlt + JPEG 编码一次（1920x1080 实测 13~16ms）」，确为**合并值**，文档表述与注释一致 |
| 2 | `Relay/SessionRecorder.cs:321`（L61） | MATCH | L321 | `hScreen = GetDC(IntPtr.Zero);` |
| 3 | `SessionRecorder.cs:324`（L62/L108/L300/L399，4×） | MATCH | L324 | `hBmp = CreateCompatibleBitmap(hScreen, w, h);`（L323 是 CreateCompatibleDC，未混淆） |
| 4 | `SessionRecorder.cs:327`（L63/L108/L140/L398，4×） | MATCH | L327 | `if (!BitBlt(hMem, 0, 0, w, h, hScreen, 0, 0, SRCCOPY))` |
| 5 | `SessionRecorder.cs:337`→`DrawCursorOn:308`（L64/L175/L277，3×） | MATCH | L337 调用 `DrawCursorOn(hMem, w, h)`；L308 是 `DrawIconEx(...)` | 两处行号均正确 |
| 6 | `SessionRecorder.cs:339`（L65/L141） | MATCH | L339 | `using var bmp = Image.FromHbitmap(hBmp);` |
| 7 | `SessionRecorder.cs:343`（L66/L109/L142/L400，4×） | MATCH | L343 | `bmp.Save(ms, JpegCodec(), ep);` |
| 8 | `SessionRecorder.cs:344`（L67/L401） | MATCH | L344 | `return ms.ToArray();` |
| 9 | `SessionRecorder.cs:240`（L68） | MATCH | L240 | `Ring.Enqueue(new Frame { Jpeg = jpeg, At = now });` |
| 10 | `SessionRecorder.cs:374`（L69/L143/L300/L402，4×） | MATCH | L374 | `foreach (var f in Ring) last = f;`（GetLatestJpeg 内，确为 O(n) 遍历取尾帧） |
| 11 | `AgentWebServer.cs:244`（L70/L110/L144/L404，4×） | MATCH | Relay/AgentWebServer.cs L244 | `await ctx.Response.OutputStream.WriteAsync(jpeg)` |
| 12 | `web-index.html:771`（L72） | MATCH | docs/web-index.html L771 | `.then(r => { ... return r.blob(); })`（L770 是 fetch） |
| 13 | `web-index.html:783`（L73） | MATCH | L783 | `const objUrl = URL.createObjectURL(b);` |
| 14 | `web-index.html:784`（L74/L111/L405，3×） | MATCH | L784 | `f.src = objUrl;`（`<img>` 赋值 → 浏览器解码） |
| 15 | `Program.cs:1-226`（L81/L409，2×） | MATCH | Server/Program.cs L1-226 | 范围内确无媒体转发；除文档点名的 rooms/depart-config/heartbeat 外还含 `/api/ping`、`/api/device/register`、每小时定时清理（L207-219），描述不完全穷举但方向正确 |
| 16 | `LobbyConnection.cs:58`（L82） | MATCH | Services/LobbyConnection.cs L58 | `new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) }`，确为每 10s |
| 17 | `web-index.html:759`（L83/L170） | MATCH | L759-762 | `const base = directBase(cur);` 起直连拼 `/live` URL |
| 18 | `SessionRecorder.cs:222-257`（L93/L234，2×） | MATCH | L222-257 | `private static void Loop()` 方法体范围正确 |
| 19 | `web-index.html:754-802`（L94） | MATCH | L754-802 | `function pullFrame()` 起止范围正确 |
| 20 | `SessionRecorder.cs:226`（L96/L396，2×） | MATCH | L226 | `while (!_stopRequested)`，确为不看观看者的死循环 |
| 21 | `WebServer.cs:165`（L96/L397，2×） | MATCH | Services/WebServer.cs L165 | `IsLiveActive()`；全仓 grep 仅 L162 定义 `MarkLiveActivity`、L165 定义 `IsLiveActive`，**无任何调用方**，文档"定义了却没人消费"断言属实 |
| 22 | `WebServer.cs:677`（L104） | MATCH | L676-677 | 注释「实测每次 45~64ms，双击就 200ms+」 |
| 23 | `WebServer.cs:1000`（L104） | MATCH | L1000 | 注释「实测 relay 往返仅 3ms，所以能做到接近实时」 |
| 24 | `AgentWebServer.cs:121`（L110/L256/L403，3×） | MATCH | L121 | `_ = Task.Run(() => HandleSafe(ctx));`，确为每请求一次 Task.Run |
| 25 | `web-index.html:734-802`（L171） | MATCH | L734-802 | `scheduleNext`（L734-738）+ `pullFrame` 串行调度 |
| 26 | `web-index.html:728`（L172/L407，2×） | MATCH | L728 | `if (failStreak > 0) return Math.min(1000 * Math.pow(2, failStreak - 1), 8000);`（1s→8s 退避） |
| 27 | `web-index.html:729,778`（L173/L195/L406，3×） | MATCH | L729 / L778 | L729 `if (stillStreak >= 6) return 1000;`；L778 `if (b.size === lastFrameBytes) stillStreak++; else stillStreak = 0;` 与文档引用的判据代码逐字一致 |
| 28 | `RelayAgent.cs:419`（L174/L408，2×） | **DRIFT** | Relay/RelayAgent.cs **L422** | L422 才是 `public int Move { get; set; }`；L419 在 Move 字段的 XML 注释内（注释内容正是"合并省一次往返/45~64ms"，内容相关，行号偏 3） |
| 29 | `AgentWebServer.cs:298`（L174/L408，2×） | MATCH | L298 | `Move = (cmd == "click" && px >= 0) ? 1 : 0,` |
| 30 | `web-index.html:757`（L176） | MATCH | L757 | `if (document.hidden || paused) { liveHandle = null; return; }` |
| 31 | `SessionRecorder.cs:131-247`（L177） | MATCH | L129-257 | 环形缓冲区段（Frame 类 + Ring + Loop + Enqueue），行号范围与内容吻合 |
| 32 | `SessionRecorder.cs:142`（L234） | MATCH | L142 | `private static int _fps = 15;`，确为单一 `_fps` |
| 33 | `WebServer.cs:146-165`（L242） | MATCH | L146-165 | `_lastLiveRequest`（L146）至 `IsLiveActive()`（L165） |
| 34 | `web-index.html:734`（L256/L407，2×） | MATCH | L734 | `function scheduleNext(delay)`（setTimeout 串行调度） |
| 35 | `web-index.html:887`（L278） | MATCH | L887 | `$('scr').addEventListener('pointermove', ...)`，内有节流 + `norm()` |
| 36 | `web-index.html:822-831`（L283） | MATCH | L822-831 | `function norm(e)` 方法体范围正确（含黑边 offset 换算） |
| 37 | `web-index.html:783-787`（L301） | MATCH | L783-787 | createObjectURL + `f.src = objUrl` + revokeObjectURL |
| 38 | `SessionRecorder.cs:234`（L396） | MATCH | L234 | `var jpeg = GrabJpeg(quality);` |
| 39 | `WebServer.cs:985-1040`（L410） | MATCH | L985-1040 | `WriteLiveFrame` 方法体范围正确，确为读盘转发兜底 |

## 二、MOBILE-REMOTE-PLAN-20261122.md（v1.2，37 条）

| # | 引用（真实出现位置） | 状态 | 真实位置 | 说明 |
|---|---|---|---|---|
| 1 | `WebFirewall.cs L44-47`（L14/L27/L43/L250，4×） | MATCH | Services/WebFirewall.cs L44-47 | netsh `dir=in action=allow protocol=TCP localport=<from>-<to>`，确无 `remoteip=`、无 `profile=` |
| 2 | `AgentWebServer.cs L314-323`（InputDto）（L15/L226/L230/L232/L317/L344，6×） | MATCH | L314-323 | `private sealed class InputDto`，字段仅 `action/x/y/button/key/delta/count`；**确无 `text`、无 `dx/dy`**，文档断言属实 |
| 3 | `AgentWebServer.cs L280-291`（switch）（L15/L231/L232/L317，4×） | MATCH | L280-291 | switch 语句在 L280，case click/down/up/move/wheel/key 在 L282-287，default 收尾在 L291；**确无 `moverel`、无 `text` case** |
| 4 | `AgentWebServer.cs L77`（L27/L249/L284，3×） | MATCH | L77 | `l.Prefixes.Add("http://+:" + port + "/");`，确为绑全网卡 |
| 5 | `AgentWebServer.cs L176-179`（L37） | MATCH | L176-179 | `if (path == "/live") { await WriteLiveAsync(ctx); return; }` |
| 6 | `AgentWebServer.cs L182-186` + `HandleInputAsync L261`（L38） | MATCH | L182-186 / L261 | `/api/input` POST 路由 + `private static async Task HandleInputAsync`，action 支持 click/down/up/move/wheel/key，坐标 0~1 相对值（L275-276 `dto.x * w`） |
| 7 | `AgentWebServer.cs L163-174`（L39） | MATCH | L163-174 | `/api/info` 路由及回显 JSON |
| 8 | `WebServer.cs L360` → `ApiSessions L726`（L40） | MATCH | L360 / L726 | `/api/sessions` 路由 + `private static object ApiSessions()`；L824 `directPort`、L866-874 `directToken` 均存在 |
| 9 | `WebServer.cs L364-365` → `WriteFile L439`（L41） | MATCH | L364-365 / L439 | `/file/` 路由 + `WriteFile` 方法，Range 206 支持在 L465-521 |
| 10 | `WebServer.cs L103` `IsAuthorized`（L42） | MATCH | L103 | `private static bool IsAuthorized(HttpListenerContext ctx)`，Cookie 鉴权 |
| 11 | `AgentWebServer.cs L208` `CheckToken`（L42） | MATCH | L208 | `private static bool CheckToken(...)`，L213 头 + L217 查询串两种都接受 |
| 12 | `AgentWebServer.cs L210`（L42/L66/L234/L251/L284/L317，6×） | MATCH | L210 | `if (string.IsNullOrEmpty(_token)) return true;`，空口令放行断言属实 |
| 13 | `WebDirect.cs L45-53`（L43） | MATCH | L45-53 | `PortFor`，`basePort + accountIndex + 1`（L51） |
| 14 | `Program.cs L52-203`（L44） | MATCH | Server/Program.cs L52-203 | L52 `/api/ping` 起至 L203 heartbeat 结束，全是状态接口，无字节转发 |
| 15 | `web-index.html L703-802`（L56） | MATCH | L703-802 | 拉帧循环变量声明至 `pullFrame` 结束 |
| 16 | `web-index.html L822-831` + `L828`（L57/L189，2×） | MATCH | L822-831 / L828 | `norm(e)`；L828 确有 `e.touches ? e.touches[0].clientX : e.clientX` |
| 17 | `web-index.html L805-820`（L58） | MATCH | L805-820 | `async function sendInput(...)`，走直连 + 令牌头 |
| 18 | `AgentWebServer L282` switch（L65） | **DRIFT** | L280 | switch 语句在 L280；L282 是第一个 case（`case "click": cmd = "click"; break;`） |
| 19 | `RelayAgent L623` switch（L65/L231/L317，3×） | MATCH | L623 | `internal static Response Execute(Request req)`；其 switch 语句在 L625 |
| 20 | `AgentWebServer L138-140`（CORS）（L66/L233/L251/L284/L294/L317，6×） | MATCH | L138-140 | L138 `Access-Control-Allow-Origin = "*"` + L139 Headers + L140 Methods，确为 `*` |
| 21 | `web-index.html L841-919`（L80） | MATCH | L841-919 | pointerdown（841）至 pointercancel 收尾（919） |
| 22 | `web-index.html L846`（L81） | MATCH | L846 | `const btn = e.button === 2 ? 'right' : ...` |
| 23 | `web-index.html L924`（L81） | MATCH | L924 | `addEventListener('contextmenu', e => e.preventDefault())` |
| 24 | `web-index.html L920`（L82） | MATCH | L920 | `addEventListener('wheel', ...)` |
| 25 | `web-index.html L938`（L83/L195，2×） | MATCH | L938（排除修饰键在 L940） | `document.addEventListener('keydown', ...)`；L940 `if (['Control','Alt','Shift','Meta'].includes(e.key)) return;` 确实排除纯修饰键 |
| 26 | `web-index.html L5`（L84/L166，2×） | MATCH | L5 | `viewport` 为 `width=device-width, initial-scale=1, viewport-fit=cover`，**确无** `user-scalable=no` / `maximum-scale=1` |
| 27 | `web-index.html L757`（L86/L164/L315，3×） | MATCH | L757 | `if (document.hidden || paused) { liveHandle = null; return; }`，与文档引文逐字一致；全文件 grep 确无 `visibilitychange`/`pageshow`/`focus`/`online`/`offline` 监听 |
| 28 | `web-index.html L792-800`（L86） | MATCH | L792-801 | catch 失败退避块，只覆盖请求失败 |
| 29 | `web-index.html L913` `sendInput('click')`（L154） | **DRIFT** | **L914** | L914 才是 `else { ripple(e); sendInput('click', p.nx, p.ny, { button: btn }); }`；L913 是 `if (moved) sendInput('up', ...)` 行 |
| 30 | `web-index.html L234`（L186） | MATCH | L234 | `#scr{...touch-action:none...}` |
| 31 | `web-index.html L896`（L187） | MATCH | L896 | `if (Math.hypot(dx, dy) < 8) {`，确为 8px 阈值 |
| 32 | `RelayAgent.Request L404-422`（L226） | MATCH | L404-423 | `Request` 类 L404 起，含 `Dx`(L409)/`Dy`(L410)/`Move`(L422)/`Hotkey`(L414) 等字段 |
| 33 | `RelayAgent.cs L155-156` `SendInput` P/Invoke（L231） | MATCH | L155-156 | `[DllImport("user32.dll")] SendInput`；现有常量仅 `INPUT_KEYBOARD=1`、`KEYEVENTF_KEYUP=0x0002`（L158-159），**确无 `KEYEVENTF_UNICODE` 常量**（0x4 数值本身正确） |
| 34 | `RelayAgent.Request L409-410` 已有 `Dx/Dy`（L232） | MATCH | L409-410 | `public int Dx { get; set; }` / `public int Dy { get; set; }`，确为位移字段 |
| 35 | `RelayAgent.Execute L675` `case "moverel"` 已实现相对位移（L232/L317，2×） | MATCH | L675-704 | `case "moverel":` 确在 L675，用 `req.Dx/req.Dy`（L678）分段 `mouse_event(MOUSEEVENTF_MOVE, ...)`，相对位移实现完整 |
| 36 | `AgentWebServer.CheckToken L216-218`（L235） | MATCH | L216-218 | 查询串 `?token=` 退路（L217-218） |
| 37 | `WebFirewall 注释 L12`「加 = 别的机器能用」（L250） | **DRIFT** | **L10** | 该注释在 L10（`/// ★ 用户要求（2026-11-22）：「加 = 别的机器能用」`）；L12 是其下三行说明文字 |

## 三、WEB-EXTENSION-PLAN-20261122.md（31 条）

| # | 引用（真实出现位置） | 状态 | 真实位置 | 说明 |
|---|---|---|---|---|
| 1 | `docs/web-index.html` 实际 1218 行（L16） | MATCH | docs/web-index.html 共 1218 行 | 行数核实一致 |
| 2 | 950 行 WPF 画布（L13） | MATCH | Controls/FlowCanvas.cs 共 950 行 | 行数核实一致 |
| 3 | 2750 行编辑器（L13） | MATCH | Views/FlowEditorWindow.xaml.cs 共 2750 行 | 行数核实一致 |
| 4 | 代理 `Access-Control-Allow-Origin: *`（L14/L481/L485） | MATCH | Relay/AgentWebServer.cs L138 | `ctx.Response.Headers["Access-Control-Allow-Origin"] = "*"` |
| 5 | `BgiDataCenter` 读 `<BGI>\User\config.json` + 列 OneDragon/ScriptGroup 文件名、缓存 1 秒、stamp 判重（L42） | MATCH | BgiDataCenter.cs L142-144（读配置/列名）、L87-101（1 秒缓存 + Stamp 比对）、L110-131（Stamp） | 全部机制与描述一致 |
| 6 | `BgiLogCenter` 是日志唯一读取者；BGI 写 `log\better-genshin-impactYYYYMMDD.log` 每条 flush；watcher 标脏 + 1 秒合并窗口增量解析（L43） | MATCH | BgiLogWatcher.cs L16（日志路径）、L17（每写一条 flush）、L778-839（FileSystemWatcher + 1 秒合并 + TickHub 兜底） | 与描述一致 |
| 7 | 尾窗最多 800 行 / 200,000 字符（L44/L77） | MATCH | BgiLogCenter.cs L35 `TailMaxLines = 800`、L37 `TailBudgetChars = 200_000` | 常量核实一致 |
| 8 | `BgiStatusParser` 仅做解析、`BgiTaskPreparer` 是控制路径（L45/L574） | MATCH | BgiStatusParser.cs L33 `class BgiStatus`/解析类；BgiTaskPreparer.cs 停止/清场逻辑 | 类职责与描述一致 |
| 9 | `BgiLogWatcher` 历史实测：几十 MB 日志 + 反复进程枚举抬升 CPU/内存（L50） | MATCH | BgiLogWatcher.cs L272-279（dotnet-trace 实测 Discover 7.15% + Core 5.99%）、BgiLogSnapshot.cs L22（53MB/749k 行）、HANDOFF-2.17.45 L89（53MB/749k 行耗时 850-1423ms→130-555ms） | 历史实测记录存在 |
| 10 | 历史样本 53 MB/749k 行、60 MB（L80） | MATCH | BgiLogSnapshot.cs L22（749k 行/53 MB）、BgiLogWatcher.cs L504/L736（60MB）、DailyStatsCache.cs L204 | 数值出处属实 |
| 11 | 主站经 `IsAuthorized` 检查 `soullink_sid` Cookie（L56） | MATCH | WebServer.cs L66 `AuthCookie = "soullink_sid"`、L103 `IsAuthorized`（L108 读 Cookie） | 一致 |
| 12 | **主站没有名为 `CheckToken` 的方法**；`CheckToken` 位于代理（L56/L321/L578） | MATCH | 全文件 grep WebServer.cs 无 `CheckToken` 定义（仅 L846 注释提及）；Relay/AgentWebServer.cs L208 定义 | 断言属实 |
| 13 | 主站 POST 前置白名单只放行 `/api/input`、`/api/settings`，其余 POST 直接 404（L57/L321/L578） | MATCH | WebServer.cs L303-323 | 白名单仅这两个，L321 `await WriteText(ctx, 404, "404")` 兜底 |
| 14 | 非 GET/HEAD/POST 会被 405（L58） | MATCH | WebServer.cs L325-327 | `{ await WriteText(ctx, 405, "只支持 GET / POST"); return; }` |
| 15 | 代理 CheckToken 接受 `X-SoulLink-Token` 或 `?token=`（L59） | MATCH | AgentWebServer.cs L213（头）+ L217（查询串） | 一致 |
| 16 | 页面运行时读 exe 同目录文件；需跑 `scripts/sync-webpages.ps1`（L60/L579） | MATCH | WebServer.cs L386-404（LoadIndexHtml 磁盘优先）；Scripts/sync-webpages.ps1 存在（生成 WebPages.cs + 同步 dist） | 一致 |
| 17 | `BgiLogSnapshot.PicksByGroup`（L75） | MATCH | BgiLogSnapshot.cs L85-86 | `public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> PicksByGroup` |
| 18 | 尾窗预算字符封顶（L95→L44） | MATCH | BgiLogCenter.cs L95 注释 + L794-799 逐行挤出逻辑 | 一致 |
| 19 | `BgiLogCenter.Entry.Version` 已是内部可见变化版本（L102） | MATCH | BgiLogCenter.cs L117 `public long Version;`（Entry 内），L126/L489/L551/L671 多处 `Version++` | 属实 |
| 20 | 快照并非所有嵌套对象都深拷贝（`Status`/`Today`/组记录仍有共享引用）（L104） | MATCH | BgiLogCenter.cs L846-871 `Build()`：`Status = e.Status`、`Today = e.Today` 直接赋引用；`PicksByGroup`（L842-844）、`Tail`（L868 ToArray）、`Groups`（L862 All()）为新拷贝 | 断言属实：Status/Today 确为共享引用 |
| 21 | `FlowNodeCatalog` / `FlowGraphRules` 为唯一目录与图规则（L336/L575/L576） | MATCH | Models/FlowNodeCatalog.cs L47 `public static class FlowNodeCatalog`；Models/FlowGraphRules.cs L10 `public static class FlowGraphRules` | 类存在 |
| 22 | 连线规则：禁自连/环/重复线、END 不得出边、触点 LD/AND/OR、NOT、并行/选择分支（L336） | MATCH | FlowValidator.cs L26-35（重复连线/悬空/END 出边）、L99（环）、L39（LD 触点上游）、FlowGraphRules.cs（CanReach 等图规则）、PlcLogicCheck.cs 覆盖 OR/NOT/并行/选择 | 与代码一致 |
| 23 | `FlowValidator.Validate(candidateClone, App.Config)` 现有可直接调用（L364） | MATCH | FlowValidator.cs L19 `public static Report Validate(AutomationFlow flow, AppConfig? cfg = null)`，public static | 服务端可直接调用（见第 30 条判定） |
| 24 | `EnsurePlcModel()` 会修改对象（L366/L377） | MATCH | FlowModels.cs L227 `public void EnsurePlcModel()`，内部 L231-240 对 PlcPower/ActionResults/ScheduleFireCount/DelayUntil/WaitingActions 空则初始化、L251-286 EngineVersion<2 迁移并清 PlcPower/ActionResults | 确有副作用（迁移 + 填充字典），"只能跑在克隆上"表述属实 |
| 25 | 返回结构化 Issue：severity/nodeId/paramKey/target/message；HasError 拒绝保存（L367） | **DRIFT** | FlowValidator.cs L11 / L15 | 字段名实际为 **`IsError`（bool）而非 `severity`**，其余 `Node`（nodeId 对应）/`ParamKey`/`Target`/`Message` 存在；`HasError`（L15）存在。文档把 bool 字段说成 severity 字段，属字段名偏差 |
| 26 | 校验覆盖账号存在/BGI 路径/绑定日志依赖/快捷动作/时间/数字/正则/重复悬空连线/环/END 数量（L368） | MATCH | FlowValidator.cs L51-53（账号）、L77-84（BGI 路径）、L89-96（绑定日志依赖）、L97（快捷动作）、L54-74（时间/数字）、L75-76（正则）、L26-34（重复/悬空/END 出边）、L99（环）、L104-107（END 数量） | 全部存在 |
| 27 | 正则仅校验语法、无超时/长度限制（L369） | MATCH | FlowValidator.cs L75-76 `try { _=new Regex(...); } catch ...`，无 Timeout/长度参数 | 属实 |
| 28 | `ConfigStore.Save` 同目录 tmp + File.Replace，catch 降级 `File.Copy(overwrite:true)`（L395） | MATCH | ConfigStore.cs L173-184：L173 tmp、L177 File.Replace、L183 `File.Copy(tmp, path, true); File.Delete(tmp);` | 与描述逐字一致 |
| 29 | `config-before-flow-migration.json` 存在（L405） | MATCH | ConfigStore.cs L149-150 | `string backup = Path.Combine(dir ?? "", "config-before-flow-migration.json")` |
| 30 | （第 3 点判定，见下）`FlowValidator` 能否被服务端独立调用 | 判定见 §四 | FlowValidator.cs L9/L19 | 见 §四 |
| 31 | WPF 编辑器持共享 `_flow`、画布拖动直接改 `Nodes/X/Y`、调度器枚举同一 `App.Config.Automations`（L360/L564/L577） | MATCH | FlowEditorWindow.xaml.cs L18 `private readonly AutomationFlow _flow`（直接持有引用，非克隆）、L36 `_cfg = cfg`、L889-890/L583-584（拖动直接写 node.X/Y，FlowCanvas.cs L583-584 同）、L2692-2693（保存直接 `_cfg.Automations.Add(_flow)` + `ConfigStore.Save(_cfg)`）；ScheduleRunner.cs L129 `foreach (var flow in cfg.Automations.ToList())`；FlowRunner.cs L59 `Running.Add(flow.Id)` 仅流程级防重入、L104/L107/L110 运行中按状态差异 `ConfigStore.Save(cfg)` | "未点保存的图可能已经影响运行"断言属实：拖动即改共享对象，调度器同一引用枚举 |

### 追问 1（第 3 点）：FlowValidator 能否被服务端独立调用

**判定：能。** 依据行号：

- `Services/FlowValidator.cs` L9：`public static class FlowValidator`；L19：`public static Report Validate(AutomationFlow flow, AppConfig? cfg = null)` —— public static，无需 UI 上下文、无 WPF 依赖（文件仅 using System/System.Collections.Generic/System.Linq/SoulLink.Models，L1-4）。
- 已有非 UI 调用方先例：`Services/FlowRunner.cs` L56（非 manual 运行前调用 `FlowValidator.Validate(flow, cfg)`），运行在后台调度路径，不在 UI 线程。
- 限制事实（非阻塞）：L22 调 `flow.EnsurePlcModel()`，该方法（FlowModels.cs L227-286）会修改对象（填充字典 + EngineVersion 迁移），所以文档"必须在候选克隆上运行"的约束成立；`Report.Issue` 的错误标记字段是 `IsError`（bool）不是 `severity`（枚举），文档 L367 的字段名与代码不一致（见第 25 条 DRIFT）。

### 追问 2（第 4 点）：「已有可复用」与「全新设计」区分

| 类别 | 项 | 依据行号 |
|---|---|---|
| **已有可复用** | FlowValidator 服务端校验 | FlowValidator.cs L19（public static） |
| **已有可复用** | BgiLogCenter 快照 + Version 版本号 | BgiLogCenter.cs L117/L190（Snapshot） |
| **已有可复用** | BgiDataCenter 名称目录 + 1s 缓存 + stamp | BgiDataCenter.cs L87-101/L110-131 |
| **已有可复用** | ScriptGroupTracker/DailyStats 今日统计 | ScriptGroupTracker.cs L26/L65；DailyStatsCache.cs L12/L36（Picks） |
| **已有可复用** | ConfigStore tmp+File.Replace 原子写 | ConfigStore.cs L173-178 |
| **已有可复用** | 主站 Cookie 鉴权、POST 白名单机制 | WebServer.cs L103-114、L303-323 |
| **已有可复用** | FlowNodeCatalog/FlowGraphRules 目录与图规则 | FlowNodeCatalog.cs L47；FlowGraphRules.cs L10 |
| **全新设计** | WebAuth read/edit 分级鉴权 | 全仓 grep 无 `WebAuth`，仅方案文档 L26/L321 提出 |
| **全新设计** | editRevision 乐观锁 | 全仓 grep `AutomationFlow`（FlowModels.cs L9-389）无 `editRevision` 字段 |
| **全新设计** | FlowEditService 统一编辑事务/单流程编辑门 | 全仓 grep 无 `FlowEditService` |
| **全新设计** | flow-backups/{flowId}/ 逐流程备份 | 全仓 grep 无 `flow-backups`（现有仅 ConfigStore.cs L149 的整配置迁移备份） |
| **全新设计** | web-audit JSONL 审计日志 | 全仓 grep 无 `web-audit` |
| **全新设计** | 确认凭证 /api/flow-confirmations | 全仓 grep 无 |
| **全新设计** | /assets/ 静态路由、ES Module 目录结构 | 全仓 grep 无；docs/web/ 目录不存在 |
| **全新设计** | WPF 草稿克隆隔离 | FlowEditorWindow.xaml.cs L18 当前直接持有共享 `_flow`，无克隆 |

## 四、四类高频错误专项核查结果

| 高频错误类型 | 核查结果 |
|---|---|
| ① 把注释里的合并值当单项耗时（"BitBlt + JPEG 编码一次 13~16ms"） | 文档表述正确：STREAM-PERF-AUDIT §1.2/§2.2 均已明确标注 13~16ms 为 `WebServer.cs:136-137` 注释的**合并值**，且注释原文（L136-137「BitBlt + JPEG 编码一次」）与文档一致。未发现把合并值当 BitBlt 单项耗时的地方 |
| ② 把"已定义但没人调用"说成"已启用"（IsLiveActive） | 文档断言属实：全仓 grep `IsLiveActive()`/`MarkLiveActivity()` 仅命中定义处（Services/WebServer.cs L162、L165），无任何调用方；STREAM-PERF-AUDIT 的表述正是"定义了却没人消费"，未反转 |
| ③ 把 DTO 里不存在的字段说成已支持（InputDto text/dx/dy） | 文档断言属实：`AgentWebServer.cs` L314-323 的 `InputDto` 仅 `action/x/y/button/key/delta/count`，无 `text`、无 `dx/dy`；MOBILE-REMOTE-PLAN v1.2 的表述是"没有 text，也没有 dx/dy"，与代码一致 |
| ④ 行号指向不相干代码 | 未发现指向不相干代码的引用。全部 5 处 DRIFT（RelayAgent.cs:419、AgentWebServer.cs:282、web-index.html:913、WebFirewall 注释 L12、FlowValidator Issue 字段名 severity→IsError）均为行号偏 1~3 行落在同一逻辑块，或字段名偏差但其余字段对应存在，内容仍相关 |

## 五、Lead 追问项判定（RelayAgent 内部是否已有相对位移能力）

**判定：为真（TRUE）。** 依据行号：

- `RelayAgent.Request` L409-410：`public int Dx { get; set; }` / `public int Dy { get; set; }` —— 字段确实存在，且类型/命名就是相对位移用途。
- `RelayAgent.Execute` L675 `case "moverel":`（L675-704）：读取 `req.Dx/req.Dy`（L678），按 `(|dx|+|dy|)/120+1` 分段（最多 20 段）调用 `mouse_event(MOUSEEVENTF_MOVE, sx, sy, ...)`，含余数补发，相对位移实现完整。
- HTTP 层未接线同样为真：`AgentWebServer.cs` L280-291 的 switch 无 `moverel` case，`InputDto`（L314-323）无 `dx/dy` 字段，`HandleInputAsync` 构造 `RelayAgent.Request` 时（L293-302）不填 Dx/Dy。
- 结论事实链：**代理管道协议层已有完整相对位移实现，仅 HTTP `/api/input` 层缺字段与路由**；MOBILE-REMOTE-PLAN §4.5 改动 3 的行号引用与该事实链全部核实一致。

## 六、统计

核对文档 3 份（WEB-EXTENSION-PLAN-20261122.md 已于 2026-11-22 18:09 生成，补核见 §三）；去重后引用条目 **107 条**（STREAM-PERF-AUDIT 39 + MOBILE-REMOTE-PLAN 37 + WEB-EXTENSION-PLAN 31）：

**总引用数 107 / MATCH 102 / DRIFT 5 / WRONG 0 / MISSING 0**

DRIFT 明细（5 条）：
1. `RelayAgent.cs:419`（STREAM-PERF-AUDIT，真 422）
2. `AgentWebServer.cs:282`（MOBILE-REMOTE-PLAN，真 280）
3. `web-index.html:913`（MOBILE-REMOTE-PLAN，真 914）
4. `WebFirewall.cs` 注释 L12（MOBILE-REMOTE-PLAN，真 L10）
5. `FlowValidator` Issue 字段 severity/nodeId/paramKey/target/message（WEB-EXTENSION-PLAN L367）：实际字段为 `IsError`（bool）而非 `severity`，其余 nodeId/paramKey/target/message 对应存在（FlowValidator.cs L11）

无 WRONG、无 MISSING。
