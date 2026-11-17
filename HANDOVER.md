# SoulLink 项目交接文档

> 最后更新：2026-11-17 · v1.13.2 + 多窗口研究/事故总结

---

## 〇、新会话必读（2026-11-17 最新）

> **先读本文和 releases/RDP-WINDOWING-RESEARCH-SUMMARY-20261117.md，再决定做什么。**

### 构建与测试工作流（2026-11-17 用户明确要求）

- **日常直接发布到 `dist/`**，用 `dist/SoulLink.exe` 测试，不再另建 `dist-test/`。
- **只有重大变更**才做独立测试版（独立程序名 = 独立身份/配置/IPC，见 AppIdentity）。
- `dist/stable/` 是生产挂机版，改动前必须备份并取得明确授权。

判断"重大变更"的参照：动 RDP 生命周期 / ActiveX 容器 / 保护层时序 / 进程注入。
单纯 UI、文案、布局调整不算，直接进 `dist/`。

### 当前安全基线

- v1.13.2 + 0d12968（连接/注销动画不遮展开导航）。
- dist/ 是开发版；dist/stable/ 是生产挂机版，未修改且当前正在运行。
- 分屏、Canvas 内部窗口、独立顶级窗口、1:1 分屏实验源码已全部撤回。
- 撤回后 Release 构建通过，0 错误；没有因此启动应用或 RDP。
- f51fb0c 记录事故；0d12968 是分屏开发前功能基线。

### 绝对禁止

1. 不修改/回退 dist/stable/，除非用户重新明确授权。
2. 不操作 A1/A4；测试只用 A2/A3，且每次真实 RDP 验证需用户授权。
3. 所有进程操作必须按 PID + SessionId + 完整 exe 路径校验；relay 与主窗口分开。
4. **禁止运行 tools/rdp-layout-validation、tools/rdp-process-windowing 中的旧验证程序。**
5. 禁止在当前机器恢复同进程多 ActiveX 分屏、Canvas 内部窗口或独立顶级窗口原型。
6. 不以“再试一次”复现 0x119；先分析转储和驱动兼容性。

### RDP 功能验证状态

- 侧栏原生浮层、首次 Down/Up 消费、后台重连保护：有用户实测可用记录。
- 连接/注销动画遮挡导航：已修复并提交 0d12968，但无完整归档验收。
- 注销后重开保护层：最后修复未完整验收，不得写成已解决。
- 分屏/内部窗口/1:1：全部撤回，没有可交付实现。

### 多标签 + 多画面（2026-11-17 实现，待完整验收）

已实现并实测可用（SoulLink-TestRdpView 测试版）：

- 固定 Tile 容器：切标签/切布局只改格子几何与可见性，**绝不 Children.Clear()+Add()**。
- 三种排列：单个 / 双列（可滚动，保持 16:9）/ 网格（等分）。N 任意，4/6/9 都支持。
- 布局纯函数 `Services/ViewLayout.cs`，可用 `tools/layout-check` 脱离 RDP 验证。
- 保护层：**整块画布一个**（不是每格一个），锚定固定画布层，与 RDP 窗口彻底解绑。
- 重新上锁判据：**光标离开画布范围**，不是离开单个 RDP 矩形。
- 隐藏 RDP 原生窗口：用**移到屏幕外**（-32000），不用 SW_HIDE（SW_HIDE 会让绘制表面丢失→黑屏）。
- 分辨率协商：500ms 防抖 + 全局串行，切标签不再反复 UpdateSessionDisplaySettings。

已修的真 bug（都是本轮实测发现的）：

1. `MainWindow_Loaded` 无条件 `LaunchView.Activate()`，覆盖 `--page=` 参数，
   导致中控台从未初始化 → RDP 画面永不显示。
2. 隐藏/恢复路径**不对称**：隐藏无条件、恢复带 `_viewing != null` 前置条件，
   最小化/还原时 `_viewing` 为空 → 永不恢复 → 画面全空。
3. 恢复坐标读取的是**宿主自己**的 `PointToScreen`，而宿主已被移到屏幕外 → 自我锁死。
   改用画面格子的屏幕矩形。
4. `ConsolePage` 里 `InputText` / `OptPanel` 叠在 RDP 上，被原生窗口盖住（airspace），
   `InputText` 已移到标签条那一行。
5. `--verbose-log` 命令行参数会让程序启动即退出（boot.log 停在 tray created），未修。

⚠ 调试时注意：`Debug()` 默认**丢弃**非关键日志，排查必须先设环境变量
`SOULLINK_VERBOSE_LOG=1`，否则会误判"代码没执行"。

### 研究结论速览

- 1Remote：同进程 Host + 顶级 Dragablz 标签窗口；不是主窗口内多视口分屏。
- SimpleRemote：同进程单窗口多标签，切换不断连、不重建 ActiveX；无同屏自由分屏。
- MobaXterm：主控 + MobaRTE 子进程；**已反汇编确认**机制为"主程序把宿主面板 HWND 用
  `-mobahwnd <HWND>` 交给子进程 → 子进程 `SetParent` 挂成该面板的子窗口"，不是独立顶级窗口。
  子→主协调无 IPC：`WM_MOUSEACTIVATE` 里 `FindWindowA(nil,"UIMainClass")` + `SetForegroundWindow`。
  分辨率变化走"断开重连"，不是在线 `UpdateSessionDisplaySettings`。
  详见 releases/MOBAXTERM-RDP-EMBEDDING-DEEP-DIVE-20261117.md。
- 后续若稳定单窗口标签，可参考 SimpleRemote；若未来继续分屏，先评估 MobaXterm 式子进程内嵌子窗口。

### 事故证据

- 2026-11-17 15:09 左右分屏验证版运行后系统死机重启。
- Bugcheck 0x119 VIDEO_SCHEDULER_INTERNAL_ERROR；NVIDIA nvlddmkm Event 153。
- 转储 C:\Windows\Minidump\111726-18203-01.dmp。
- 只确认时间关联与高风险组合，未完成内核栈分析，不能宣称某行代码为直接根因。

详细文档：

- releases/RDP-WINDOWING-RESEARCH-SUMMARY-20261117.md
- releases/RDP-SPLIT-INCIDENT-20261117.md
- releases/1REMOTE-WINDOWING-RESEARCH-20261117.md
- releases/SIMPLEREMOTE-TABBED-RDP-RESEARCH-20261117.md
- releases/MOBAXTERM-RDP-ARCHITECTURE-RESEARCH-20261117.md
- releases/MOBAXTERM-RDP-EMBEDDING-DEEP-DIVE-20261117.md（反汇编级机制确认，含复现脚本说明）

---

## 一、这是什么

**SoulLink** 是一个 WPF (.NET 8) 写的**多开挂机中控台**，用于在一台 Windows 上同时管理多个 RDP 会话里的原神账号。

核心能力：

| 功能 | 说明 |
|---|---|
| **中控台** | 内嵌 RDP 窗口，直接在软件里操作各账号的桌面 |
| **在线会话** | 自动发现本机所有 Windows 会话（query user） |
| **启动台** | 快捷动作（启动 BGI、跑一条龙等）+ 自动化流程 |
| **联机大厅** | 4 人协同跑图的组队、匹配、发车 |
| **更新 BGI** | 批量把新版 BGI 解压覆盖到各账号目录 |
| **自动化** | 节点图编排：触发器 → 条件 → 执行器 |

---

## 二、目录结构

```
SoulLink/
├── SoulLink.csproj          主程序（WPF）
├── App.xaml.cs              启动入口
│
├── Models/
│   ├── Models.cs            配置模型（AppSettings、Account、Action…）
│   ├── LobbyModels.cs       联机大厅模型
│   ├── FlowModels.cs        自动化流程模型
│   └── FlowNodeCatalog.cs   节点类型目录
│
├── Services/
│   ├── SessionService.cs    会话发现（query user / qwinsta）
│   ├── SystemLauncher.cs    跨会话启动进程
│   ├── ActionService.cs     快捷动作执行
│   ├── FlowRunner.cs        自动化流程图执行
│   ├── FlowValidator.cs     流程图校验
│   ├── BgiStatusParser.cs   BGI 日志解析
│   ├── BgiLogWatcher.cs     BGI 日志监听
│   ├── BgiTaskPreparer.cs   ★ BGI 停止（快捷键 + 杀进程）
│   ├── BgiUpdater.cs        ★ BGI 更新（解压覆盖）
│   ├── OwnedOverlayWindow.cs ★ RDP 空域原生浮层（Owner / 非 Topmost）
│   ├── DeviceIdentity.cs    设备码
│   ├── LobbyApi.cs          ★ 联机大厅 HTTP 客户端
│   ├── LobbyConnection.cs   心跳 / 重连 / 状态
│   ├── ScheduleRunner.cs    定时任务
│   └── ConfigStore.cs       配置读写
│
├── Views/
│   ├── MainWindow.xaml      主窗口 + 导航
│   ├── ConsolePage.xaml     中控台（RDP）
│   ├── LaunchPage.xaml      启动台
│   ├── LobbyPage.xaml       联机大厅
│   ├── BgiUpdatePage.xaml   ★ 更新 BGI
│   ├── SettingsPage.xaml    设置
│   ├── FlowEditorWindow.xaml 流程编辑器
│   ├── ThemedMessageWindow.xaml 主题弹窗
│   └── ...
│
├── Controls/
│   ├── RdpConsole.cs        RDP ActiveX 封装
│   └── FlowCanvas.cs        流程图画布
│
├── Server/                  ★ 联机大厅服务端（独立项目）
│   ├── SoulLink.Server.csproj
│   ├── Program.cs           API 端点
│   ├── Db.cs                SQLite 数据层
│   ├── Lobby.cs             业务逻辑
│   ├── Dto.cs               数据契约
│   ├── deploy.sh            Ubuntu 部署脚本
│   └── README.md            服务端文档
│
├── bgi-scripts/
│   └── AAA_联机罐子/        ★ BGI 协同跑图脚本
│       ├── main.js
│       ├── settings.json
│       ├── assets/routes/   内置路线
│       ├── assets/targetItems/ 拾取物品图
│       └── targets/         白名单名字图
│
├── tools/
│   └── 7za/7za.exe          ★ 解压工具（构建依赖，必须入库）
│
├── dist/                    发布产物
│   ├── SoulLink.exe
│   ├── 7za.exe
│   └── sl-rescue.exe
│
├── releases/                版本归档
│   ├── v1.13.0-reliable/       回退版本
│   ├── v1.13.2-reliable/
│   ├── SoulLink-v1.13.2-Update.zip
│   └── rollback.ps1         回退脚本
│
└── dist/stable/             ★★ 挂机版（生产使用，改动需谨慎）
```

---

## 三、当前状态（v1.13.2）

### 版本信息

| 项目 | 值 |
|---|---|
| 版本 | 1.13.2 |
| 提交 | fc19561（保护层修复）/ 917d379（归档） |
| 标签 | v1.13.2-reliable |
| 归档 | releases/v1.13.2-reliable/ |
| 更新包 | releases/SoulLink-v1.13.2-Update.zip (87.2 MB) |
| dist | 当前为 v1.13.2 后续小修复（含 0d12968 动画层级修复），SHA256 D29E9531…；不同于标签归档 exe |
| 归档校验 | v1.13.2-reliable 固定 exe 为 fc19561，SHA256 58005BCD…；dist/stable/ 未修改 |
| 回退 | releases/rollback.ps1 → v1.13.0-reliable |

### 服务端

| 项目 | 值 |
|---|---|
| 地址 | http://121.4.78.52:45080 |
| 服务 | soullink-server（systemd，开机自启） |
| 目录 | /opt/soullink-server |
| 数据库 | /opt/soullink-server/soul-link.db |
| 部署脚本 | Server/deploy.sh |

### BGI 脚本

`bgi-scripts/AAA_联机罐子/` —— 部署时整个目录拷到 `<BGI>\User\JsScript\` 下。

**已在 A1 单人模式验证跑通 5 条路线。**

### RDP 空域与保护层（v1.13.2）

- 展开侧栏和 RDP 保护层使用 OwnedOverlayWindow：真实 Win32 Owner、非 Topmost、不抢焦点。
- 首次点击保护层完整消费 Down/Up，MouseUp 后才解锁；移出、失活、切页后重新锁定。
- 原生浮层按物理像素定位；150% DPI、反复/快速收展及失活隔离测试通过。
- 用户已用 A2 对独立验收构建做真实 RDP UI 验证，确认基础浮层可用。
- v1.13.2 使用原生 Win32 可见性控制，失焦完整撤销鼠标、键盘和自由视角状态。
- 后台重连保护已由用户实测确认可用。
- 注销后重开改为销毁并重建保护层 HWND；此项最后的修复仍待用户实测。
- 独立验收版只用于测试并禁用了新代理投送；dist 正式版保留正常投送功能。

### 严重事故：分屏实验已撤回（2026-11-17）

- 分屏验证版运行后发生 Windows Bugcheck 0x119（VIDEO_SCHEDULER_INTERNAL_ERROR）并重启。
- 转储：C:\Windows\Minidump\111726-18203-01.dmp；NVIDIA nvlddmkm Event 153。
- 未提交的分屏/内部窗口/1:1 实验源码已全部撤回，恢复 0d12968 内容；构建通过。
- **禁止再次启动 tools/rdp-layout-validation 下的旧验证版。**
- 未修改 dist/stable/；dist/ v1.13.2 不含分屏实验。
- 详见 releases/RDP-SPLIT-INCIDENT-20261117.md。

---

## 四、关键约束（必须遵守）

### 红线

1. **`dist/stable/` 是生产挂机版**，未经明确许可**绝对不要**更新或回退。
   - 当前是 1.11.0（用户授权的版本）
   - 备份：SoulLink-Stable-1.11.0.exe、SoulLink-Stable-1.5.3.exe

2. **A1 和 A4 是核心账号，不能碰**
   - 不注入、不 tsdiscon、不 logoff、不 query target
   - 测试只用 A2 / A3

3. **必须管理员权限运行**（RDP ActiveX 注入需要）

4. **进程操作必须按 SessionId 过滤**
   ```csharp
   if (p.SessionId == targetSessionId) { ... }  // 才能动
   ```
   任何涉及 BGI / 原神 / 茶包助手的操作都遵守这条。

### 环境

- 机器用户：`lummy`
- 本地账号：`A1 A2 A3 A4`（大写）
- BGI 路径：`D:\AAA挂机专区\<user>\BetterGI\BetterGI.exe`
- `lummy` 没有 BGI
- 屏幕 2560×1440，DPI 150%（144）

---

## 五、重要技术细节

### 5.1 会话发现

`Services/SessionService.cs`

`query user` 的输出格式**随系统区域变化**：

```
常见：  2026/11/16 22:05
某些：  11.16 4:10        ← 点号分隔、无年份、单位小时
```

**解析必须宽松**：核心字段（用户 会话名 ID 状态）先认出，时间单独解析，失败不影响。

### 5.2 BGI 停止

`Services/BgiTaskPreparer.cs`

```
① 找该会话的 BetterGI 进程
② 读 <BGI>\User\config.json 的 cancelTaskHotkey
③ PostMessage 定点投递（不抢焦点）
④ 等 3 秒，没退就 Kill
⑤ 等 15 秒确认退出
```

**停 BGI 是必须的**：一条龙和配置组都要求 BGI 空闲，不先停会冲突。

### 5.3 BGI 更新

`Services/BgiUpdater.cs`

**为什么用 7za.exe 而不是托管解压：**

| 方案 | 519MB 包耗时 |
|---|---|
| SharpCompress（纯 C#） | 几分钟，CPU 满载风扇狂转 |
| 7z.dll COM 接口 | 该 dll 不支持，返回 0x80040111 |
| **7za.exe 命令行** | **5 秒** |

**关键坑：**
- 不能重定向 7za 的输出（管道缓冲区满了会死锁，卡在 87 个文件）
- 不能原地搬移剥顶层目录（会误删已解压内容）
- 正确做法：解压到 `_raw` 子目录，找出真正的内容根再复制

### 5.4 联机大厅

**客户端**：`Services/LobbyApi.cs` + `LobbyConnection.cs`
**服务端**：`Server/` （ASP.NET Core 8 + SQLite）

**业务规则：**
- 身份 = 设备码（首次运行生成，存 `device.json`）
- 只有私人房间：私人·四人 / 私人·大小号（成对）
- 房间码 = 通行证，只有创建者能看到
- 每设备最多 1 个四人房 + 1 对大小号房
- 大小号房两两绑定，成员不能跨房间
- 房间 3 天无活动自动销毁
- 建房不自动上线，要点「加入房间」

**发车：** 服务端下发所有成员的设备码 + 各自动作配置，客户端只执行自己那条。

### 5.5 BGI 协同跑图脚本

`bgi-scripts/AAA_联机罐子/`

**和原版「AAA狗粮联机团购」的区别：**

| | 团购脚本 | 本脚本 |
|---|---|---|
| 队伍结构 | 1P/2P/3P/4P 各跑一片 | 全员跑同一条 |
| 占位 | 需要 | 不需要 |
| 换人 | 跑完踢人 | 不换，一起跑完 |
| mojang | 需要 | 不需要 |

**白名单机制（移植自团购脚本）：**
1. 模板匹配：`targets/*.png` 在申请列表区域找
2. OCR 兜底：直接读文字
3. 相似度用编辑距离，文件名不必精确

**人数规则：**
- 白名单填 3 个 → 4 人队
- 填 2 个 → 3 人队
- 填 1 个 → 2 人队
- **全空 → 单人模式**（跳过组队直接跑，用于验证）

---

## 六、常用操作

### 构建

```powershell
cd E:\Documents\deepseek-harness\SoulLink
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

**产物必须包含三个文件：**
- SoulLink.exe
- 7za.exe（更新 BGI 功能必需）
- sl-rescue.exe

### 服务端更新

```bash
# 本地打包
cd Server
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o ../dist-server

# 上传 dist-server/SoulLink.Server 到服务器 /opt/soullink-server/

# 服务器上
systemctl stop soullink-server
chmod +x /opt/soullink-server/SoulLink.Server
systemctl start soullink-server
```

### 回退

```powershell
cd releases
.\rollback.ps1
```

### 版本升号流程

```
1. 改 SoulLink.csproj 的 <Version>
2. git commit
3. dotnet publish（让 ProductVersion 带正确 commit）
4. 归档到 releases/vX.Y.Z-reliable/
5. git tag -a vX.Y.Z-reliable
6. 打包 releases/SoulLink-vX.Y.Z-Update.zip
```

---

## 七、已知问题 / 待办

### 联机大厅

- [ ] 发车广播：其他成员客户端的执行目前靠各自轮询，未做实时推送
- [ ] 房间内成员列表刷新依赖 5 秒轮询，可以改 WebSocket

### BGI 更新

- [ ] 进度条在解压阶段是「不确定」状态（不知道总数）
- [ ] 可选：加「更新完自动启动 BGI」开关

### BGI 脚本

- [ ] 组队模式（2-4 人）尚未实战验证，只有单人模式跑通
- [ ] 拾取逻辑移植自原脚本，效果待验证
- [ ] 白名单 OCR 识别的准确率待观察

### 其他

- [ ] `dist/stable/` 待用户授权后更新到 1.13.0
- [ ] tools/ 下有些临时截图可以清理

---

## 八、调试技巧

### 强制打开指定页面

```
SoulLink.exe --page=<tag>
```

可用 tag：`launch` `console` `update` `sessions` `procs` `cli` `settings`

### 日志位置

```
%LOCALAPPDATA%\SoulLink\
├── error.log          异常堆栈
├── boot.log           启动记录
├── flow-menu.log      菜单调试
├── ipc.log            IPC 通信
└── rdp-debug-*.log    RDP 调试
```

### 备份位置

```
%LOCALAPPDATA%\SoulLink\bgi-backups\<账号>\User_时间戳
```

### 验证 BGI 是否更新成功

**不要看修改时间**（7za 保留包内时间戳）**也不要看版本号**（BGI 新版本不改内部版本号）。

**用哈希对比：**

```powershell
7za x 包.7z -o临时目录 -y
(Get-FileHash 临时目录\BetterGI\BetterGI.exe).Hash
(Get-FileHash D:\AAA挂机专区\A2\BetterGI\BetterGI.exe).Hash
# 相同 = 已更新
```

---

## 九、踩过的坑（血泪教训）

### UI 相关

| 坑 | 原因 | 解法 |
|---|---|---|
| 编辑器输入框无响应 | AuroraBackdrop 动画背景抢焦点 | 移除动画背景 |
| 菜单闪烁 / 打不开 | 三处叠加 bug（陈旧引用 / MenuLayer 覆盖 / DPI 坐标失配） | 改成纯 MouseEnter/MouseLeave + 共享关闭定时器 |
| ComboBox 选了不显示 | 自定义 ControlTemplate 只有 ToggleButton | 用原生模板 |
| 取消按钮执行了保存 | DialogResult = true 写错 | 改成 false |
| 页面空白 | _pages 注册成了内容控件而不是容器 | 注册容器 Grid |

### 进程相关

| 坑 | 原因 | 解法 |
|---|---|---|
| 错误杀掉其他账号的进程 | 只按进程名找 | 必须按 SessionId 过滤 |
| 解压卡在 87 个文件 | 重定向 stdout 但从不读取 → 管道死锁 | 不重定向，用 -bso0/-bsp0 |
| 解压后文件没了 | 原地 Directory.Move 时先删了目标目录 | 解压到独立 _raw 子目录 |

### 会话相关

| 坑 | 原因 | 解法 |
|---|---|---|
| 某些机器读不到会话 | query user 日期格式不同（11.16 4:10） | 正则放宽，时间单独解析 |

### 存档相关

| 坑 | 原因 | 解法 |
|---|---|---|
| ProductVersion 带旧 commit | 提交前构建的 | 提交后重新 publish |
| 7za.exe 没入库 | tools/ 被 gitignore | 改规则：tools/* + !tools/7za |

---

## 十、下一步建议

按优先级：

1. **组队实测** —— BGI 脚本的组队模式（2/3/4 人）还没实战验证过
2. **拾取验证** —— 确认 F 图标识别和物品匹配在游戏里有效
3. **服务端稳定性** —— 长时间运行后看 SQLite 有无问题
4. **更新 stable** —— 等用户明确授权后再动

---

## 附：重要文件速查

| 想改什么 | 看哪个文件 |
|---|---|
| 加导航页 | Views/MainWindow.xaml + .xaml.cs 的 _pages |
| 改会话解析 | Services/SessionService.cs 的 SessionRx |
| 改 BGI 停止逻辑 | Services/BgiTaskPreparer.cs |
| 改解压方式 | Services/BgiUpdater.cs 的 ExtractToTempAsync |
| 改大厅接口 | Server/Program.cs + Services/LobbyApi.cs |
| 改 BGI 脚本逻辑 | bgi-scripts/AAA_联机罐子/main.js |
| 改主题颜色 | Theme/Styles.xaml |
