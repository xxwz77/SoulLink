# SoulLink 项目交接文档

> 给接手的新会话看。所有结论都经过**真机实测**，不是推测。
> 最后更新：2026-11-15（用户列表、导航、主题、RDP 捕获与自动化实测）

---

## 一、项目目标

把「自动查找 ID 启动其他用户程序.bat」升级成**多用户挂机中控台**：

1. 同时管理多个 Windows 用户会话（本机 5 个：lummy + a1~a4）
2. 跨会话启动/关闭程序，**不抢前台键鼠**
3. 内嵌 RDP 画面，**标签页切换**
4. **能转游戏视角**（RDP 原生做不到，需要注入补足）
5. 免安装、解压即用

---

## 二、环境事实（不要假设，以实测为准）

| 项目 | 值 |
| --- | --- |
| 系统 | Windows 11 专业工作站版 Insider Preview，Build 29698 |
| 显卡 | RTX 4060 Laptop，**真实显存 8188 MiB**（WMI 报 4GB 是 32 位溢出，别信） |
| NVENC | 已占用 4 会话，12fps / 365ms（向日葵 + UU 在跑） |
| 多用户 RDP | 靠 **RDPWrap 补丁**，非原生能力；系统更新会顶掉 |
| 会话 | 1=lummy(console)，2=a2，3=a3，4=a4，5=a1 |
| 环回地址 | 2/3/4/5 各有一条 Established 到 3389 |

**1Remote 的配置（D:\AAA挂机专区\远程本地多用户\1R\1Remote.db）：**

| 显示名 | 用户 | 地址 |
| --- | --- | --- |
| 思 | A1 | 127.0.0.2 |
| 山客 | A2 | 127.0.0.3 |
| 玄月 | A3 | 127.0.0.4 |
| 阿宴 | A4 | 127.0.0.5 |

密码：A2 = `12345678.`（其他用户同密码，未逐个验证）

---

## 三、已验证的技术结论（★ 最重要，别再重复试错）

### 3.1 跨会话注入 —— 两条路都已打通

| 能力 | 方式 | 实测结果 |
| --- | --- | --- |
| **转游戏视角** | `mouse_event(MOVE, dx, dy)` 相对位移 | ✅ **原神能转**（用户亲测） |
| **绝对定位** | `SetCursorPos(x, y)` | ✅ 4/4 精准，误差 0 |
| **点击** | `mouse_event(LEFTDOWN/LEFTUP)` | ✅ 命中 |
| **右键** | `mouse_event(RIGHTDOWN/RIGHTUP)` | ✅ 命中 |
| **滚轮** | `mouse_event(WHEEL, 120)` | ✅ 命中 |

**关键**：注入必须在**目标会话内部**执行。做法是把代理投进去，前台通过命名管道发指令。

**合成输入确实会产生 `WM_INPUT`**（之前判断"游戏会忽略"是错的，已推翻）。

### 3.2 跨会话权限 —— 必须真 SYSTEM

| 方案 | 结果 |
| --- | --- |
| 管理员令牌 + `WTSQueryUserToken` | ❌ 1314 |
| 管理员 + 窃取 SYSTEM 令牌 + **冒充** | ❌ 1314 |
| `CreateProcessWithTokenW` | ⚠️ **返回成功但进程落在调用者会话**，假成功，最坑 |
| **真 SYSTEM**（服务 or 计划任务）| ✅ **26ms**，落点正确 |

- 服务与**计划任务权限完全等价**；服务需安装，计划任务免安装
- 免安装方案：`schtasks /ru SYSTEM` 拉起 → 用完删除，实测 **459ms 起来，无残留**
- SYSTEM 进程内存：**25.5 MB 工作集**

### 3.3 会话隔离 —— 限制与解法

- 跨会话**看不到**目标会话的窗口（`EnumWindows` 返回 0）
- 跨会话**截不到**画面
- **解法**：代理投进目标会话，在里面采集。实测能拿到窗口列表 + 截图（1280×720）

### 3.4 ★ RDP 的坑（最重要，差点做错方向）

**用 RDP 连 `127.0.0.3` 不会接管已有会话，而是新建一个空会话！**

实测：a2 原有会话 #2 完好，同时多出一个 #6（新登录）。
- 原会话进程（auto-bgi、mojang）**没被杀**，挂机未中断
- 但**新会话是空桌面**，看不到挂机画面，注入也会打错目标

**正式版必须处理这一点**，选项：
- 用 `tscon` 把目标会话切到前台
- 或画面和操作走不同通道（RDP 出画面 + WTS 通道操作）

### 3.5 WPF 透明层不可行

RDP 控件必须用 `WindowsFormsHost`（HwndHost 空域），**WPF 元素无法绘制在其上层**，透明覆盖层会失效。

**替代方案**：全局低级鼠标钩子 `WH_MOUSE_LL`，在 RDP 区域返回 1 吞掉事件。
（此路**尚未完成验证**——测试程序没连上 RDP 就卡住了）

---

## 四、1Remote RDP 实现要点（已读源码）

源码：`https://api.github.com/repos/1Remote/1Remote/contents/Ui/...`（GitHub 直连不稳，用 API 取单文件稳定）

**关键文件：**
- `Ui/Model/Protocol/RDP.cs`（1065 行，配置模型）
- `Ui/View/Host/ProtocolHosts/AxMsRdpClient09Host.xaml.cs`（1118 行，控件宿主）

### 4.1 初始化必须严格按此顺序

```csharp
CreateRdpClient();        // AxMsRdpClient9NotSafeForScriptingEx
RdpInitServerInfo();      // Server / UserName / Domain / RDPPort / 密码
RdpInitStatic();          // 加密、重连、CredSSP、超时
RdpInitConnBar();         // 全屏连接栏
RdpInitRedirect();        // 剪贴板/驱动器/音频
RdpInitDisplay(w, h);     // 分辨率 ← 顺序错了会连不上
RdpInitPerformance();     // 色深/性能标志
RdpInitGateway();
ApplyRdpControlAdditionalSettings();
```

### 4.2 设置密码的正确方式（上次白屏就死在这）

```csharp
var secured = (MSTSCLib.IMsTscNonScriptable)_rdpClient.GetOcx();
secured.ClearTextPassword = password;
```
**不是** `ocx.ClearTextPassword`（会报 `0x80020006 未知名称`）

### 4.3 必设项（不设会出各种怪问题）

```csharp
_rdpClient.AdvancedSettings2.EncryptionEnabled = 1;
_rdpClient.AdvancedSettings5.EnableAutoReconnect = true;
_rdpClient.AdvancedSettings6.PublicMode = false;
_rdpClient.AdvancedSettings7.ConnectToAdministerServer = false;
_rdpClient.AdvancedSettings8.BitmapPersistence = 0;      // 修错误码 4360
_rdpClient.AdvancedSettings8.CachePersistenceActive = 0;
_rdpClient.AdvancedSettings9.EnableCredSspSupport = true;
_rdpClient.AdvancedSettings9.AuthenticationLevel = 0;    // 证书问题也照连
_rdpClient.AdvancedSettings6.ContainerHandledFullScreen = 1;
```

### 4.4 四种缩放模式

```csharp
enum ERdpWindowResizeMode {
    AutoResize = 0,          // 跟随窗口
    Stretch = 1,             // 固定分辨率 + 拉伸
    Fixed = 2,               // 固定分辨率 + 原大小
    StretchFullScreen = 3,
    FixedFullScreen = 4
}
```

### 4.5 分辨率设置

连接前：
```csharp
_rdpClient.DesktopWidth  = (int)(width  * (scaleFactor / 100.0));  // 必须乘 DPI
_rdpClient.DesktopHeight = (int)(height * (scaleFactor / 100.0));
```

连接后热更新：
```csharp
_rdpClient.UpdateSessionDisplaySettings(w, h, w, h, 0, scaleFactor, 100);
```

DPI：
```csharp
_rdpClient.SetExtendedProperty("DesktopScaleFactor", scaleFactor);
_rdpClient.SetExtendedProperty("DeviceScaleFactor", (uint)100);
```

**坑**：拖窗口时鼠标按住会连续触发 resize 导致闪屏，1Remote 的做法是**等鼠标松开再调整**。

### 4.6 自定义图标

与 RDP 控件无关，纯 UI 层：配置里存 `IconBase64`（PNG 的 base64），列表项直接渲染。

---

## 五、现有代码资产（6160 行，可直接复用）

位置：`E:\Documents\deepseek-harness\杂七杂八\SoulLink\`

| 文件 | 作用 | 复用价值 |
| --- | --- | --- |
| `Services/SessionService.cs` | `query user` 解析（**已处理中文 locale**） | ★★★ 直接复用 |
| `Services/ConfigStore.cs` | JSON 配置 + 状态 | ★★★ |
| `Services/Crypto.cs` | DPAPI 加解密 | ★★★ |
| `Services/PsExecService.cs` | PsExec 封装（新版做法要弃用，但参数演进逻辑可参考） | ★★ |
| `Services/ProcessService.cs` | 进程枚举/结束 | ★★★ |
| `Services/Ipc.cs` | 命名管道单实例 + 命令转发 | ★★★ |
| `Services/CommandLine.cs` | 命令行解析 | ★★ |
| `Services/LogBus.cs` | 日志总线 | ★★★ |
| `Theme/Styles.xaml` | 玻璃卡片 + 霓虹主题 | ★★★ |
| `Controls/Backdrop.cs` | 极光动画背景 | ★★ |
| `Views/*` | 各页面 | ★★ 需重构 |

**已发布成品**：`SoulLink.exe`（68MB 自包含单文件，验证可用）

---

## 六、新会话怎么开头

### 第一步：确认环境没变

```powershell
query user
# 应看到 lummy(1) / a2(2) / a3(3) / a4(4) / a1(5)
cd 'E:\Documents\deepseek-harness\杂七杂八\SoulLink'
dotnet build -c Release
```

### 第二步：读这三份资料

1. 本文件 `HANDOFF.md`（结论）
2. `README.md`（现有功能说明）
3. `Services/` 和 `Models/`（代码结构）

### 第三步：明确第一件事

**建议从「RDP 控件能连上并出画面」开始**，因为：
- 上次就卡在这（白屏，因为密码设置方式错了）
- 连不上，后面的钩子、注入、UI 都无从验证

**最小验证目标**：
```
一个 WPF 窗口
└─ AxMsRdpClient9 控件
   └─ 能连 127.0.0.3 / A2 / 12345678. 并显示 a2 桌面
```

做到这一步，再往上加钩子层。

---

## 七、待办清单

> ✅ 表示 2026-11-14 已完成。

- [x] **RDP 控件连接成功并出画面** ✅（非分层窗口 + 手写 AxHost，见第十章）
- [x] 标签页切换（中控台页，每个会话一块独立 RDP 画面）✅
- [x] 会话列表 + 状态监控 ✅
- [x] 「新建会话会空桌面」的解法 ✅（目标 Active 才 tsdiscon，Disc 直接复用）
- [x] 自定义图标、分辨率、缩放模式等配置项 ✅（账号级 RDP 地址 / 端口 / 登录名）
- [x] **SYSTEM 自动投放代理（计划任务免安装）** ✅ 见 11.1，626~727ms
- [x] **跨会话注入：绝对定位 + 点击 + 相对位移 + 转视角** ✅ 见 11.3
- [x] **坐标换算（UI 像素 → 远程像素，含 DPI/黑边）** ✅ 见 11.6
- [x] **双模式：点击悬停（绝对）+ 转视角（相对，按住右键）** ✅ 见 11.3
- [x] **移除 PsExec 依赖** ✅ 全流程改用 SYSTEM 通道 + SeDebugPrivilege
- [x] 键盘输入 ✅ **RDP 原生转发**，只需保证控件有焦点（见 12.8），不做注入
- [ ] 内嵌画面全屏 / 自适应分辨率（`UpdateSessionDisplaySettings`）
- [ ] 多标签预连接 + 内存占用优化（每块 RDP 画面约 60~120MB）
- [x] 全局钩子接管画面鼠标（含前台判定，不干扰别的程序）✅ 见 11.10
- [x] 无限转视角（光标捕获锁中心）✅ 见 11.10
- [x] 自由视角 / 指针双模式 ✅ 见 11.12
- [ ] 让 Disc 状态的会话也能注入（需要保持一个虚拟交互桌面，待研究）

## 八、踩过的坑（避免重复）

| 坑 | 说明 |
| --- | --- |
| WMI 显存报 4GB | 32 位溢出，实际 8GB，用 `nvidia-smi` |
| `query user` 中文解析 | 状态列是「运行中」不是 Active，正则要兼容 |
| PsExec `-d` 退出码 | 返回的是新进程 PID，不是错误码 |
| PsExec 参数顺序 | `psexec 选项 程序 程序参数`，参数在程序后面 |
| `CreateProcessWithTokenW` | 会假成功，进程落在调用者会话 |
| WPF 覆盖 RDP 控件 | 空域问题，WPF 元素画不到 HwndHost 上 |
| `SetCursorPos` 看似失效 | 会话内没有全屏窗口时，光标被限制在窗口区域内 |
| 构建被文件锁 | 程序在运行时会锁住 exe，先 `Stop-Process` 再 build |

---

## 九、验证工具位置（已清理，需要时重建）

本轮所有测试程序已删除。如需重测，核心代码片段：

**注入代理**（投进目标会话）：
```csharp
[DllImport("user32.dll")] static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
mouse_event(0x0001, dx, dy, 0, IntPtr.Zero);   // 相对移动（转视角）
mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);     // 左键按下
mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);     // 左键抬起
mouse_event(0x0800, 0, 0, 120, IntPtr.Zero);   // 滚轮
```

**投放命令**：
```powershell
$pe = "$env:LOCALAPPDATA\SoulLink\tools\PsExec.exe"
& $pe -u a2 -p "12345678." -i 2 -d -accepteula -nobanner "代理.exe"
```

---

## 十、2026-11-14 第二轮实测补充（★ 修正上文几条结论）

> 本轮把「内嵌 RDP 画面」从 0 做到能用：控件宿主、自动登录、会话复用、标签页中控台全部打通。
> 下面 10.6~10.9 是**真机踩出来的坑**，比 10.1~10.4 更值钱。

### 10.1 RDP 控件：不装 aximp 也能用 ✅

本机**没有** Windows SDK 的 `aximp.exe` / `tlbimp.exe`。可行做法是**手写 AxHost**：

```csharp
public sealed class RdpAxHost : System.Windows.Forms.AxHost
{
    public object Ocx { get; private set; }
    public RdpAxHost(string clsid) : base(clsid) { }
    protected override void AttachInterfaces()
    {
        try { Ocx = GetOcx(); } catch { Ocx = null; }
    }
}
// 用法：wfh.Child = ax; ax.CreateControl();   ← 必须 CreateControl 之后 GetOcx 才非 null
```

CLSID（本机实测可用）：
| CLSID | 名称 |
| --- | --- |
| `{A3BC03A0-041D-42E3-AD22-882B7865C9C5}` | Microsoft RDP Client Control - version 9 ← **已验证** |
| `{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}` | version 10 |
| `{A0C63C30-F08D-4AB4-907C-34905D770C7D}` | version 11 |

注意：`MsRdpClient9NotSafeForScriptingEx` 这个 ProgID **在本机不存在**，别照抄 1Remote。

### 10.2 ★ 密码设置：4.2 的写法不成立，但结论比想象的简单

```csharp
var secured = (IMsTscNonScriptable)_rdpClient.GetOcx();  // ❌ NotImplementedException
secured.ClearTextPassword = password;
```

原因：**.NET (Core) 5+ 不支持 `InterfaceIsIDispatch` 类型的 [ComImport] 接口映射**，
一调用就抛 `NotImplementedException`。改 `InterfaceIsDual` 也一样。

而且 `ClearTextPassword` **不在 `GetIDsOfNames` 的分发表里**（返回 `0x80020006`），
所以 `ocx.ClearTextPassword = ...`（dynamic 绑定）同样失败。
本机再逐个试 `IMsTscSecuredSettings` / `IMsRdpClientNonScriptable` 等 IID，
要么 QI 失败（`0x80004002`），要么也没有这个名字。

**★ 但真正重要的是——实战里根本不需要手动设密码：**

| 场景 | 结果 |
| --- | --- |
| 用户**已登录**（Active 或 Disc） | RDP 连过去直接进桌面，**不需要密码** |
| 用户**已注销**（需要密码） | `ocx.UserName = "A2"` + `Connect()` **也能自动登录成功** ✅ |

实测（a2 已 `logoff`，会话不存在）：
`Connect()` 后 **615 ms** 自动建出**新会话 #10 且 Active**，画面直接是 a2 的桌面。

> 结论：**别费劲找 ClearTextPassword 的 DISPID。**
> 设好 `Server` / `UserName` / `Domain` / `RDPPort` 后直接 `Connect()` 就够了。
> 只有在目标机器策略禁止保存凭据、或凭据被拒时，才需要考虑别的办法。

#### 10.2.1 ⚠️ 踩过的坑：猜 DISPID 会写坏 `Server`

我一开始对控件主 `IDispatch` 穷举 PROPERTYPUT dispid 1~10，看到 **1~5 全部返回 S_OK** 就以为成功了。
结果回读属性发现：

```
Server=12345678.     ← 密码被写进了 Server！
```

**主分发表上的 dispid 1 是 `Server`，不是 `ClearTextPassword`。**
"HRESULT=0" 完全不等于"设对了属性"。教训：**改完必须回读校验**，别信返回值。


### 10.3 ★ RDP「新建空会话」问题：已解决

上文 3.4 的担心只在**「目标会话已 Active」**时才成立。实测规律：

| 目标会话状态 | RDP 连过去的结果 |
| --- | --- |
| **Active**（有人正连着） | ❌ 新建一个空会话（原会话不动，进程都在） |
| **Disc**（已断开挂机中） | ✅ **复用原会话**，Disc→Active，登录时间不变，进程原样保留 |

实测证据：`tsdiscon 8` 后连接 → 会话 **#8 被复用**（ID 不变），**没有**新建会话。

**所以正确做法**：连接前先 `tsdiscon <会话ID>` 把目标踢成 Disc，再 RDP 连过去，
就能拿到**真实的挂机桌面**，而不是空会话。

> 权限：`tsdiscon` 需要 SYSTEM 或对该会话有权限。本机管理员令牌直接 `tsdiscon` 实测**可以**。

### 10.4 WPF 空域（Airspace）：★ 分层窗口里 RDP 画面**永远全黑**

这是本轮最费时间的一个坑，务必记住结论：

| 窗口类型 | RDP 控件表现 |
| --- | --- |
| **普通窗口**（`AllowsTransparency=False`） | ✅ 画面正常 |
| **分层窗口**（`AllowsTransparency=True`，哪怕 `WindowStyle=None`） | ❌ 能连上、会话也是 Active、HWND 链和坐标全正常，**但画面全黑** |

对照组（同一份代码、同一个 CLSID、同样的配置，**只改 `AllowsTransparency`**）：

```
实验 9: 分层窗口(AllowsTransparency)=False   → CONNECTED after 410 ms，画面正常
实验 9: 分层窗口(AllowsTransparency)=True    → CONNECTED after 431 ms，画面全黑
```

**所以 SoulLink 主窗口必须是非分层窗口。**
`MainWindow.ApplyOpaqueMode()` 负责这件事，并用「深色底 + 圆角 + 自绘边框」
替代原来的透明发光层来保持外观。

另外仍是老规矩：WPF 元素**画不到** `WindowsFormsHost` 上面（空域）。
要覆盖在画面上方的工具条、提示层，必须放在 `WindowsFormsHost` 外部。


### 10.6 ★ 环回地址不能按会话号推算

`WTSClientAddress` 查下来，本机**所有**环回 RDP 客户端在服务端看来都是同一个 `127.0.0.1`。
所以「会话 8 → 127.0.0.8」是错的：

| 地址 | 实测 |
| --- | --- |
| `127.0.0.3` | ✅ 一直可用（a2 最稳的地址） |
| `127.0.0.8` | ❌ 能建立 TCP，但连接会**卡住并弹凭据框** |
| `127.0.0.1`~`.9` | 端口都通（RDP 在 0.0.0.0 监听），**但只有部分能落到目标会话** |

**做法**：地址写进账号配置（`Account.RdpHost`），不要推算。
新账号默认给 `127.0.0.3`，连不上再让用户在「设置 → 账号 → 地址」里改。

### 10.7 ★★ 最大的坑：对**已断开**的会话执行 `tsdiscon` 会直接注销它

设计初衷是对的：目标 Active 时 RDP 会新建空会话，所以要先把目标踢成 Disc。
但把它写成「连接前无条件 tsdiscon」就出事了：

- 目标本来就是 Disc（挂机中的常态）
- `tsdiscon <id>` 再踢一次 → **会话被注销**，进程全丢
- 随后 RDP 连过去，服务端回：**「你已从此远程计算机注销」**，画面永远黑

**正确做法**：

```csharp
var s = find(sessionId);
if (s != null && s.IsDisconnected)  return;              // 已经是 Disc，什么都别做
if (s != null && s.IsActive)        tsdiscon(sessionId); // 只有 Active 才踢
```

判断依据是 `query user` 的状态列（中文系统是「运行中 / 断开」）。

### 10.8 ★ 同一会话重复连接会把前一个客户端踢掉

对同一个会话再发一次 `Connect()`，服务端会让旧连接失效，并给出瞬态错误：
`connected=1` 一闪而过，紧接着 `connected=2` + 「会话已结束」。

这会造成两个误判：
1. 把瞬态的 `connected==1` 当成功；
2. 切标签页时自动重连 → 把好好的画面踢黑。

**对策**：
- 连续 **2 次**采样到 `Connected==1` 才算连接成功；
- 切标签页**绝不重连**，只有「从未连接过」的标签才发起连接；
- `ConnectAsync` 入口先判 `IsConnected`，已连接直接返回。
### 10.9 调试副作用记录（务必知悉）

调试期间 a2 被反复重建：`#2 → 注销 → #7 → #8 → 注销 → #10`。
**原会话 #2 的挂机进程（auto-bgi / mojang / BetterGI 等）在注销时已经全部丢失**，
需要重新登录 a2 并重新拉起挂机程序。

教训：调试 RDP 时，**能不动会话就别动**。tsdiscon 和 logoff 都会真的杀掉挂机。

---

## 十一、2026-11-14 第三轮：输入注入与去 PsExec（★ 已完成）

> 本轮把「能看见画面」推进到「能操作画面」，同时彻底移除了 PsExec。

### 11.1 ★ 弃用 PsExec，改走 SYSTEM 计划任务通道（免安装）

原因：PsExec 要写服务、杀软敏感、密码还得进命令行。新方案完全不碰这些。

**链路**（实测 626~727ms 完成一次投送）：

```
管理员进程
  ├─ schtasks /create /ru SYSTEM /sc once /st <10分钟后> /tr "SoulLink.exe --system-run <job.json>"
  ├─ schtasks /run   ← 手动触发（/st 只是占位，不必等）
  │   └─ 引导进程 SoulLink.exe --system-run（此时是 SYSTEM）
  │       ├─ WTSQueryUserToken(sessionId)       ← 只有真 SYSTEM 才不报 1314
  │       ├─ DuplicateTokenEx → 主令牌
  │       ├─ CreateEnvironmentBlock            ← 不建会继承 SYSTEM 的 PATH/TEMP
  │       └─ CreateProcessAsUser(winsta0\default) ← 落到目标用户桌面
  └─ schtasks /delete /f   ← 用完即删，无残留
```

**关键点**：
- 引导进程就是 **SoulLink.exe 自己**（`--system-run`），不需要额外打包任何东西
- `--system-run` / `--relay` 都必须在 `OnStartup` **最前面**分流，不能先拉起任何 UI
- `/st` 必须给**还没到**的时间（取 10 分钟后），否则 schtasks 拒绝排程；实际用 `/run` 触发
- 结果经临时 job 文件的 `ResultPath` 回传，约定 `Arguments = "OK|<pid>|说明"`

**踩过的坑**：`BuildBootstrapCmd` 用 `Environment.ProcessPath` 取引导 exe，
在被测试程序引用的场景下会指向那个测试程序，引导进程根本不是 `--system-run` 实现。
必须显式传入或回退到本体（见 `SystemLauncher.ResolveSelfExe`）。

### 11.2 ★ 注入代理：复用 SoulLink.exe 本体

代理不需要单独 exe —— `SoulLink.exe --relay <管道名>` 就是代理：
- 自包含单文件，目标会话里**不需要装任何运行时**
- 纯后台循环，不初始化 WPF（不会在别人桌面上闪窗口）
- 由 SYSTEM 通道投送，连回宿主命名管道收 JSON 指令

实测：`pid=56112 session=4`，代理确实跑在目标会话里。

### 11.3 实测通过的注入能力

| 能力 | 结果 |
| --- | --- |
| 跨会话窗口枚举 | ✅ 能列出目标桌面窗口（标题 / PID / 矩形） |
| 前台窗口 | ✅ 能拿到标题与 PID |
| **绝对定位** `SetCursorPos` | ✅ 300,200 精确命中，**误差 0** |
| **点击** `mouse_event` | ✅ 左键命中 |
| **相对位移** `mouse_event(MOVE)` | ✅ 300,200 → 589,56，增量精确 |
| **按住右键转视角** | ✅ down → moverel → up 完整手势可用 |
| 滚轮 | ✅ |

### 11.4 ★★ 最大的限制：**Disc 会话没有可交互桌面**

会话处于 `Disc` 时，`SetCursorPos` 失败、光标锁死在 `0,0`、前台窗口为空。
必须先让 RDP 连上（会话变 `Active`）才有桌面可注入。

```
Disc   状态: 可用性 False · 光标被锁在 0,0 · 绝对定位 ok=False
Active 状态: 可用性 True  · 屏幕 1280x720 · 绝对定位精确
```

**所以中控台的正确工作流是**：先接入画面（RDP 接管 → 会话变 Active）→ 注入才生效。
`SessionInput.CheckUsableAsync()` 会主动探测并把原因回报给界面。

### 11.5 光标贴边的处理（⚠️ 已被 11.10 的光标捕获方案取代，保留作演进记录）

相对位移的固有缺陷：光标撞到屏幕边缘后，继续发 dx 不累积，转视角就「转不动了」。
代理的解法：每次移动后检测是否贴边，贴边就把光标挪回屏幕中心再继续，
响应里带 `· 光标贴边已回中`，上层记一笔，避免用户以为画面卡了。

### 11.6 坐标换算

`CoordinateMapper` 负责 UI 像素 ↔ 远端像素：

```
远端 1280x720 · 视口 900x600 · 缩放 0.703x0.833 · 偏移 0,47
UI(450,300) → 远端(640,360)   ← 保持宽高比 + 黑边居中
```

- 绝对定位按缩放换算（含黑边偏移）
- 相对位移**不**按缩放缩小（否则窗口一小转视角就变慢），只做轻度补偿 + 上限保护


### 11.10 ★★ 鼠标接管方案（第二版，取代 11.5/11.9 的做法）

第一版用「按住右键拖动」转视角，实测反馈有三个硬伤，全部推翻重做：

| 问题 | 原因 | 现在怎么解决 |
| --- | --- | --- |
| 要按住右键，反人类 | 设计成手势 | 改成**光标捕获**：移动即转视角，不用按键 |
| 干扰别的前台程序 | 钩子全局生效，没判前台 | 只在 `窗口.IsActive` 时接管，失焦立刻放行 |
| 切页面回来失效 | 钩子挂在 `Unloaded`，切页不触发 | 用 `IsVisibleChanged` + 窗口 `Activated` 重装 |
| 只能转一点点 | 光标撞屏幕边缘就没位移了 | **光标锁在画面中心**，永远撞不到边 → 可无限转 |

**核心机制**（`RdpConsole.OnGlobalMouse`）：

```
捕获中：
  dx = 光标X - 画面中心X
  SetCursorPos(画面中心)      ← 立刻拉回，下次还是纯相对量
  注入 moverel(dx, dy)
```

这样光标永远停在中心，**不存在撞边问题**，可以一直转下去。
实测连续 30 次、每次 +100，光标纹丝不动停在中心。

### 11.11 ★ 指针加速必须幂等恢复（踩过的坑）

`PointerAcceleration` 是引用计数式的，但第一版在 `ReleaseCapture` 和 `UninstallMouseHook`
两条路径上都调了 `PopRestore()`，计数被多减一次 → **系统加速停在 `MouseSpeed=0` 没恢复**。

表现：用户退出程序后鼠标手感变了（无加速）。这是污染系统设置，属于严重 bug。

修法：控件自己记 `_accelHeld` 标志，`ReleaseAccel()` 幂等，持有才释放。

验证：`speed=10` → 捕获中 `speed=0` → 失焦后 `speed=10` ✅

### 11.12 两种输入模式

| 模式 | 行为 | 适用 |
| --- | --- | --- |
| **自由视角**（默认） | 光标隐藏锁中心，移动转视角，点击打准星位置 | 挂机游戏转视角 |
| **指针模式** | 绝对定位，可精确点击画面任意位置 | 点按钮、操作桌面 |

顶部按钮切换，**ESC 也可退回指针模式**。


### 11.13 ★★ 自由视角的安全底线（第三版，踩了一串血的教训）

第一版把自由视角做成**自动进入**，结果用户一进中控台就被困住，只能 Win+D 逃命。
这一版把所有逃生路径补齐，规则如下，**任何改动都不能破坏**：

| 规则 | 为什么 |
| --- | --- |
| **默认指针模式，绝不自动捕获** | 用户不被困住是第一优先级 |
| **Ctrl+Alt+Q 全局热键** | 唯一可靠的逃生出口，窗口失焦也有效 |
| 失焦 / 最小化 / 不可见 / 页签切走 → 立刻释放 | 绝不干扰别的前台程序 |
| 钩子单例（`_mouseHook != null` 直接 return） | 第一版泄漏了 8 个同类钩子 |
| 计算画面矩形时校验坐标 | 最小化时 `PointToScreen` 返回 `-31172,-31494`，不校验会把光标丢到屏幕外 |
| 回调整体 try/catch，异常就 ReleaseCapture | 宁可不管鼠标，也不能困住用户 |

### 11.14 ★ 三个具体的坑

**① 切换页面后光标进不了画面**
`NavigateTo` 当初会走一遍 `_pages` 重置，而 `ConsolePage` 的 `Activate` 没有重新确认可见性，
`RdpConsole` 的钩子也早被 `Unloaded` 摘掉了。
修法：`IsVisibleChanged` 重装钩子 + `OnShown()` 兜底 + `NavigateTo` 只改 `Visibility` 不重建。

**② 指针加速残留（污染系统设置）**
`ReleaseCapture` 和 `UninstallMouseHook` 两条路径都调 `PopRestore()`，计数被多减 →
`MouseSpeed` 停在 0，用户退出后鼠标手感变了。
修法：控件自己记 `_accelHeld`，`ReleaseAccel()` 幂等。

**③ XAML 用了不存在的资源键，整窗崩溃**
写了 `{StaticResource TextCyan}`，实际键名是 `CyanBrush`。
结算：**整个窗口加载失败**，表现为「界面启动失败」。
以后加样式先跑一遍「校验所有 StaticResource 都有定义」的脚本。

### 11.15 ★ 转视角灵敏度

高 DPI 鼠标（1600+）用 1.0 倍率明显偏慢。现在：
- 默认 `4.0x`，界面右上角有滑块可实时调（1.0 ~ 12.0x）
- 值保存在 `Settings.LookSensitivity`，下次打开还在
- 注入用 `LookRawAsync`：**直接下发像素位移，不做画面缩放折算**
  （捕获模式下位移本来就是原始物理量，再折算会让高 DPI 越转越慢）


---

## 十二、1Remote 源码研读笔记（2026-11-14）

> 源码快照已存到 `docs/1remote-AxMsRdpClient09Host.xaml.cs.txt`（52KB）和 `docs/1remote-RDP.cs.txt`（40KB），
> 断网也能对照。下面只记**对 SoulLink 有用的部分**。

### 12.1 ★ 两个我们漏掉的 WndProc 处理（很重要）

1Remote 继承 `AxMsRdpClient9NotSafeForScripting` 重写了 `WndProc`，处理两个消息：

```csharp
protected override void WndProc(ref Message m)
{
    // ① 修 UIA/读屏软件导致的崩溃（issue #1053）
    if (m.Msg == WM_GETOBJECT)   // 0x003D
    {
        m.Result = (IntPtr)(-1);  // 设 0 没用，-1 才有效
        return;
    }
    // ② 修「点了控件但拿不到焦点」的问题 ★ 我们正缺这个
    if (m.Msg == WM_MOUSEACTIVATE)  // 0x0021
    {
        if (!this.ContainsFocus) this.Focus();
    }
    base.WndProc(ref m);
}
```

**第 ② 条解释了我们的键盘为什么不生效**：RDP 控件没有焦点，键盘事件进不去。

### 12.2 ★ 分辨率自适应（我们完全没做）

1Remote 的 `ReSizeRdpToControlSize` + `SetRdpResolution`：

```csharp
// 连接后才允许改
if (!_flagHasConnected || _rdpClient.FullScreen != false
    || _rdpSettings.RdpWindowResizeMode != AutoResize) return;

// ★ 拖窗口时鼠标按住会疯狂触发 resize → 闪屏。必须等松开再改：
while (Mouse.LeftButton == MouseButtonState.Pressed) Thread.Sleep(300);

// 尺寸真的变了才调（控件默认宽是 288，别用这个值）
if (_rdpClient.DesktopWidth != nw || _rdpClient.DesktopHeight != nh)
    SetRdpResolution(nw, nh, false);

// 热更新：
_rdpClient.UpdateSessionDisplaySettings(w, h, w, h, 0, newScaleFactor, 100);
```

还有一个关键细节：**`SmartSizing` 只在 Stretch 模式开**，AutoResize 模式必须关掉，
否则远端分辨率跟着控件变、又叠加拉伸，画面会糊。

### 12.3 五种缩放模式（完整定义）

```csharp
enum ERdpWindowResizeMode {
    AutoResize = 0,          // 跟随窗口（要动态改远端分辨率）
    Stretch = 1,             // 固定分辨率 + 拉伸
    Fixed = 2,               // 固定分辨率 + 原大小
    StretchFullScreen = 3,
    FixedFullScreen = 4,
}
```

我们在 `AppSettings.RdpScaleMode` 里只做了 0/1/2，缺全屏两种。

### 12.4 DPI 处理（1Remote 的做法，⚠️ 实测在本机不可用，见 12.9）

```csharp
_rdpClient.SetExtendedProperty("DesktopScaleFactor", scaleFactor);  // 系统 DPI，如 125/150
_rdpClient.SetExtendedProperty("DeviceScaleFactor", (uint)100);
```

**注意**：`DesktopWidth/Height` 设的是**物理像素**，缩放由 `DesktopScaleFactor` 单独表达。
我们之前把两者混在一起算，导致坐标换算和画面比例都不准。

### 12.5 性能标志（`AdvancedSettings8.NetworkConnectionType`）

```
1 = Modem              2 = 低速宽带     3 = 卫星
4 = 高速宽带           5 = WAN          6 = LAN
```

配合 `PerformanceFlags` 位标志可关壁纸/动画/字体平滑。挂机中控台用 4~6 比较合适。

### 12.6 连接栏 / 全屏

```csharp
AdvancedSettings6.DisplayConnectionBar
AdvancedSettings6.ConnectionBarShowPinButton
AdvancedSettings6.ContainerHandledFullScreen = 1;   // 让宿主窗口处理全屏
```

回调事件：`OnRequestGoFullScreen` / `OnRequestLeaveFullScreen` / `OnRequestContainerMinimize` /
`OnConnected` / `OnDisconnected` / `OnLoginComplete` / `OnConfirmClose`。

用事件回调比我们轮询 `Connected` 属性准确得多——**这是我们该改的**。

### 12.7 落地情况

| 项 | 状态 |
| --- | --- |
| WndProc 子类化：`WM_MOUSEACTIVATE` 抢焦点 | ✅ 已做（`RdpAxHost.WndProc`） |
| WndProc 子类化：`WM_GETOBJECT` 防崩 | ✅ 已做 |
| `GrabFocusOnConnect = true` + `GiveKeyboardFocus()` | ✅ 已做，键盘已实测可用 |
| `DesktopScaleFactor` / `DeviceScaleFactor` | ❌ **本机不支持**，见 12.9 |
| Resize 防抖 + `UpdateSessionDisplaySettings` | ✅ 已做（`RequestResize`） |
| 缩放模式 5 种 | ⚠️ 目前用 0/1/2，全屏两种待补 |
| 用 `OnConnected` 事件替代轮询 | ⚠️ 手写 AxHost 无事件接口，保留加固轮询 |


### 12.8 ★ 键盘：RDP 原生就有，别做注入

**我一开始想错了**：打算用 relay 代理做键盘注入，这和 PsExec 一样属于重复造轮子。
RDP 协议本身完整转发键盘，前提只有一个——**控件必须拿到焦点**。

排查发现三处把焦点搞丢了：

| 问题 | 后果 |
| --- | --- |
| `AdvancedSettings2.GrabFocusOnConnect = false` | **主因**：连接后控件不抢焦点，按键进不去 |
| 用全局钩子拦截鼠标后不再 `ax.Focus()` | 鼠标事件被吞，控件拿不到激活 |
| 切页 / 重连后没重新设焦点 | 回来键盘就失效 |

**修法**：
```csharp
adv2.GrabFocusOnConnect = true;    // 连上就让控件拿焦点

// 连接成功后 + 页面重新显示时都补一次
public void GiveKeyboardFocus()
{
    if (!IsVisible || _ax == null || !_ax.IsHandleCreated) return;
    _ax.Focus();
}
```

实测：按 `M` 键 → a2 里的原神**打开了地图**。键盘完全正常。

### 12.9 ★ DPI 属性：Web 上说的写法和实际都不可用

1Remote 用：
```csharp
_rdpClient.SetExtendedProperty("DesktopScaleFactor", scaleFactor);
_rdpClient.SetExtendedProperty("DeviceScaleFactor", 100);
```

我按它的思路做了完整尝试，**全部失败**：

| 尝试 | 结果 |
| --- | --- |
| `[ComImport] IMsRdpExtendedSettings` 强转 | `E_UNEXPECTED (0x8000FFFF)` |
| 原生 `IDispatch::Invoke` 按名字调 `set_Property` | 分发表里没有（`0x80020006`） |
| QI 到 `{302D8188-...}` 后按 vtable slot 8 直调 | QI 成功，但 `set_Property` 恒返回 `E_UNEXPECTED` |
| 连接前 / 连接后分别试 | 都一样 |
| `Stdcall` / `Winapi` 约定 | 都一样 |

vtable 探测结果（连接后）：
```
slot 7 GET  -> 0x45F45987（明显是栈错乱，说明不是 get_Property）
slot 8 GET  -> 0x8000FFFF
slot 9 GET  -> 0x8000FFFF
slot 10 GET -> 0x80004002 (E_NOINTERFACE)
```

**结论：本机的 RDP 控件不接受这两个属性。**
替代方案：远端固定按 100% 渲染，宿主侧用 `CoordinateMapper` 消化本机 150% 缩放。
我们不做远端 DPI 设置，画面比例和坐标都是对的。

### 12.10 UI 整理（反馈驱动）

| 反馈 | 改动 |
| --- | --- |
| 不应再有 PsExec 字样 | **全源码清零**，设置页改为「跨会话投送」状态卡 |
| 工具栏太挤 | 拆成两行：标签页一行、操作与状态一行 |
| 灵敏度应默认收起 | 改成齿轮按钮，点开才显示滑块 |
| 主操作不突出 | 「进入自由视角」改成主按钮（`PrimaryButton`） |


### 12.11 ★★ 点击偏移：DIP 与物理像素混用（150% DPI 下必现）

反馈：点地图某处，游戏光标瞬移到右下角另一个位置。

**根因**：`_host.ActualWidth` 返回的是 **DIP**，而 `PointToScreen` 返回的是**物理像素**。
本机 DPI 是 **150%**，DIP 只有物理像素的 2/3，视口宽度被严重低估：

```
错误（用 DIP）：视口 904x595  → 缩放 0.706x0.826  ← 比例错了
正确（物理像素）：视口 1357x892 → 缩放 1.060x1.239  ← 实际就是 150% DPI
```

比例偏小 → 换算出的远端坐标整体偏大 → 点击落到右下角。

**修法**：视口尺寸一律用 `PointToScreen` 差值算，不用 `ActualWidth`：

```csharp
var tl = _host.PointToScreen(new Point(0, 0));
var br = _host.PointToScreen(new Point(_host.ActualWidth, _host.ActualHeight));
int width  = (int)Math.Round(br.X - tl.X);   // 物理像素
int height = (int)Math.Round(br.Y - tl.Y);
```

实测校验（点屏幕中心）：
```
注入点击 UI(678,457) → 远端(640,370)
  远端光标实际 = 640,370      ← 误差 0
```

### 12.12 ★★ ESC 关不掉游戏地图：WPF 抢走了键盘

反馈：游戏里按 ESC 想关地图，结果页面跳回启动台，地图关不掉。

排查过程：
1. 先怀疑 `ContainerHandledFullScreen = 1` —— 控件会自己处理「退出全屏」并把 ESC 当自己的命令。
   已改成 **0**（我们是窗口内嵌，不需要控件管全屏）。
2. 但改完仍不行。用 `GetGUIThreadInfo` 查 Win32 焦点：
   ```
   focusHwnd=... class=IHWindowClass title=[Input Capture Window]
   ```
   **焦点在 WPF 的 Input Capture Window 上，不是 RDP 控件。**
   按键被 WPF 截走后不会转给 ActiveX 控件。

**修法**：
```csharp
// 1. 构造时就让 WPF 别争键盘
Focusable = false;
KeyboardNavigation.SetIsTabStop(_host, false);
_host.Focusable = false;

// 2. 焦点用 WinForms + 原生 SetFocus 双重保证
_ax.Focus();
SetFocus(ocxHwnd);        // user32!SetFocus
```

实测：按 ESC → **地图关闭、回到游戏场景，页面仍停在中控台**。

> 注意：`IHWindowClass` 是 WPF 的输入捕获窗口，用 `GetGUIThreadInfo` 查会看到它，
> 但 `ax.ContainsFocus` 才是准确的判断依据。两者不一致时以 AxHost 为准。


### 12.13 ★★ 改成「鼠标锁定」二元模型（用户提的设计，更清晰）

之前的「指针模式 / 自由视角」双模式语义混乱，反馈里连出三个问题。
改成**二元开关**，一下就清楚了：

| 状态 | 行为 |
| --- | --- |
| **未锁定**（默认） | 鼠标事件**完全放行**给 RDP 原生通道，行为和直接开游戏窗口一模一样。点击、拖拽、右键全都正常 |
| **锁定** | 光标钉在画面中心并隐藏，移动即转视角（可无限转），点击在当前位置生效 |

**关键代码**（`OnGlobalMouse` 开头一句就决定了行为）：
```csharp
if (!_captured) return false;   // 没锁 → 一律放行，什么都不做
```

### 12.14 ★ 自定义热键（双向）

热键**常驻注册**，不是只在锁定时才有：
- 未锁定 → 按下上锁
- 锁定中 → 按下解锁

组合键可在界面里改（点齿轮 → 「锁定热键」输入框），保存到 `Settings.LockHotkey`。
支持 `Ctrl+Alt+Q`、`Ctrl+Shift+F9` 这类写法，必须带修饰键。

### 12.15 ★★ 指针加速：改用「代次令牌」，别再数数

第二版用引用计数（`_depth`），结果：**用户退出后系统加速停在 `speed=0`**，鼠标手感变了。
原因：多实例各 Push 一次 + `ForceRestore` 强行把计数清零，两边状态对不上。

改成**代次（generation）机制**：
```csharp
int token = PushDisabled();   // 返回当前代次
PopRestore(token);            // 代次不符就忽略
ForceRestore();               // 强行恢复 + 代次+1，旧持有者全部失效
```

这样无论走哪条路径、有多少实例，**系统设置最终一定回到用户的原始值**。
实测：`speed=10` → 锁定 `speed=0` → 解锁 `speed=10` ✅

### 12.16 键盘泄漏到导航栏

反馈：一开始键盘失灵，而且没按 ESC 也触发了页面切换。

根因：**ListBox 的 type-ahead**。导航栏是 `ListBox`，按 `W`/`A`/`S`/`D` 会跳到首字母匹配的菜单项
（「在线会话」「启动台」…），于是玩游戏的 WASD 把页面切走了。

修法：
```xml
IsTextSearchEnabled="False"
Focusable="False"
KeyboardNavigation.TabNavigation="None"
```
外加 `PreviewKeyDown` 吞掉方向键/空格/Home/End/翻页。


### 12.17 ★★★ 最终形态：锁定只加「转视角」，其余全交给 RDP

用户提的关键洞察，彻底解决了之前一连串问题：

> **「我们可以直接用 rdp 输入，只增加转动的效果吗？」**

之前的错误做法：锁定后把所有鼠标按键都拦下来自己注入 →
点击、拖拽、右键全部失效，键盘也因焦点问题失灵。

**现在的做法**（`OnGlobalMouse` 只需要看这两段）：

```csharp
if (!_captured) return false;      // 没锁 → 一律放行

if (ev.IsMove) { ...转视角...; return true; }   // 锁了也只处理移动
return false;                       // 其余全部放行给 RDP
```

效果：
- 未锁定：和直接开游戏窗口完全一致
- 锁定后：**只有鼠标移动被用来转视角**，点击/拖拽/右键/滚轮/键盘全部走 RDP 原生通道

这也解释了为什么之前「锁定状态键盘失灵」——把按键吞掉后 RDP 收不到，自然没反应。

### 12.18 界面重构：用户列表 + 右侧画面

用户反馈「点中控台应该是用户列表，增减配置用户，不应该直接连接」。已改：

| 旧 | 新 |
| --- | --- |
| 打开就自动连接所有账号 | **不自动连接**，点用户才接画面 |
| 顶部一排标签 | 左侧**用户列表**，每行带「配置 / 删除」 |
| RDP 参数散在设置页 | 每行就地打开「用户与 RDP 设置」窗口 |

新增的每用户 RDP 参数（`Account` 上）：
```
RdpWidth / RdpHeight       画面分辨率
RdpScaleMode               0=跟随窗口 1=拉伸 2=原大小
RdpColorDepth              32 / 16 / 8
RdpAutoDisconnect          连接前是否先踢下线
RdpLowBandwidth            低带宽模式
```

### 12.19 热键改为「录制」

用户反馈「输入框应该是按什么键就能记住是什么键，支持鼠标按键，不要用户输入」。

新控件 `HotkeyRecorder`（继承 `Button`）：
- 点一下进入录制态，显示「请按组合键…」
- 按键盘组合 → 记录（按 Esc 取消）
- **按鼠标键 → 记录**（左/右/中键、侧键 X1/X2）
- 鼠标热键通过 `WH_MOUSE_LL` 实现，因为 `RegisterHotKey` 不支持鼠标

`HotkeySpec` 负责描述与显示，例如 `Ctrl+Alt+Q`、`鼠标中键`、`Ctrl+鼠标侧键4`。


### 12.20 布局定稿（按用户标注的四区结构）

用户画图标注了四个区域，最终按此实现：

```
┌─────────┬──────────────────────────────────────┐
│ 红框     │ [配置] [A2] [A3] …   ← 顶部标签卡      │
│ 导航栏   ├────────┬─────────────────────────────┤
│ (可折叠) │ 绿框    │ 蓝框                         │
│         │ 用户列表 │ 该用户的配置项 / 或画面       │
└─────────┴────────┴─────────────────────────────┘
```

**实现要点**：

| 区域 | 行为 |
| --- | --- |
| 红框 导航栏 | 标题栏左侧有折叠按钮，收起后左上角浮出展开按钮；状态存 `UiState.NavCollapsed` |
| 绿框 用户列表 | 单击选中并显示配置，双击直接打开画面 |
| 蓝框 配置页 | 显示名称 / 地址 / 端口 / 登录名 / 分辨率 / 色深 / 缩放模式 / 连接行为 |
| 顶部标签卡 | 「配置」+ 每个用户一个页签，用户页签带连接状态点 |

### 12.21 ★ 又一个 XAML 崩溃：样式 TargetType 不匹配

给 `ComboBox` 套了 `{StaticResource ComboToggle}`，而这个样式的 `TargetType="ToggleButton"`，
结果 **整个主窗口加载失败**，报错还是被包装过的：

```
XamlParseException: 设置属性 FrameworkElement.Style 时引发了异常
  ---> InvalidOperationException: "ToggleButton" TargetType 与元素"ComboBox"的类型不匹配
```

**教训**：`XamlParseException` 的真实原因藏在 InnerException 里，
一定要把 `--->` 后面那段打出来才能定位。只看外层消息会完全找错方向。

排查命令（以后直接抄）：
```powershell
$c = Get-Content $env:LOCALAPPDATA\SoulLink\error.log -Raw
$i = $c.IndexOf("XamlParseException"); $c.Substring($i, 800)
```


### 12.22 ★ 账号级参数根本没生效（白屏元凶之一）

用户把配置改成 **1920x1080 / 16 位**，但日志显示实际连的是 **1280x720 / 32 位**：

```
配置:  RdpWidth=1920 RdpHeight=1080 RdpColorDepth=16
日志:  Desktop = 1280x720  ColorDepth=32   ← 用的是全局默认
```

原因：`RdpConsole.Configure()` 里只读了 `AppSettings`（全局），
我新加的 `Account.RdpWidth/RdpHeight/RdpColorDepth/RdpScaleMode/RdpAutoDisconnect` **一个都没用上**。

改成账号级优先：
```csharp
int w = Clamp(account.RdpWidth  > 0 ? account.RdpWidth  : settings.RdpWidth,  200, 8192);
int d = account.RdpColorDepth  > 0 ? account.RdpColorDepth : settings.RdpColorDepth;
bool stretch = (account.RdpScaleMode == 1 || account.RdpScaleMode == 3);
```

### 12.23 ★ 注销用户后连不上 / 白屏

用户在任务管理器里注销了 a2，再点连接 → 画面全白、没有任何提示。

根因：会话已经不存在，但代码拿着**旧的会话号**去连，RDP 连上了但对面没有桌面，于是白屏。
而且失败时遮罩层只显示一行小字，用户根本不知道发生了什么。

**修法**：
1. 连接前先 `query user` 校验会话是否真的存在；不存在就直接提示「用户当前没有登录会话」
2. 失败时遮罩进入**错误态**：显示明确原因 + 一个「重新连接」按钮
3. 会话号 <= 0 也直接拒绝

### 12.24 ★ 新增「注销并关闭用户」

用户要求：「顶上应该加个注销并关闭用户」。

画面工具栏新增红色**「注销」**按钮，和「断开」明确区分：

| 按钮 | 行为 |
| --- | --- |
| 断开 | 只断画面，**会话继续挂机**（常用） |
| **注销** | **关掉该用户所有程序**，不可恢复 |

注销前二次确认，文案明确写出「会话 #N 里的所有程序都会被关闭」并提示「只是暂时不看请用断开」。
实现走 `logoff.exe <sessionId>`，执行后回读 `query user` 确认会话真的没了。

### 12.25 配置页改成两栏

用户反馈「设置项离得太远，看不见连接信息」。原来是单栏纵向排列，滚动后看不到上面的地址。

改成左右两栏：
```
┌─────────────────┬─────────────────┐
│ 连接信息         │ 画面与行为       │
│ 显示名称         │ 宽 / 高          │
│ RDP 地址         │ 色深             │
│ 端口             │ 缩放模式         │
│ RDP 登录名       │ 连接行为         │
└─────────────────┴─────────────────┘
```
两栏并排，一屏看全，不用滚。


### 12.26 ★★ 会话不存在 ≠ 错误，RDP 会自己登录

上一版为了修白屏，加了「没有会话就不让连」的拦截 —— **把能用的功能堵死了**。
用户直接指出：「我点击不久应该登录吗？」

对。HANDOFF 10.2 早就实测过：用户注销时，`Connect()` 会用保存的凭据
**自动登录并建出新会话**。不该拦。

**正确逻辑**：
```csharp
var s = SessionService.Find(sessions, user);
int sessionId = s?.Id ?? 0;      // 没有就是 0，不拦截
if (s == null) {
    BusyText.Text = "用户未登录，正在尝试用保存的凭据登录…";
}
await row.Console.ConnectAsync(account, settings, sessionId);
```

`sessionId = 0` 时：
- 跳过 `tsdiscon`（本来也没有会话可踢）
- RDP 直接按 `Server + UserName + Password` 走登录流程
- 成功后会建出新会话，下次 `query user` 就能看到

实测（a2 已注销、密码存在）：
```
[Console] 会话不存在，走 RDP 自动登录流程
Desktop = 1920x1080 色深=16 Mode=1    ← 账号级参数也生效了
→ query user: a2  rdp-tcp#2  5  Active  2026/11/15 0:54
```

## 12.27 一个反复出现的教训

这一轮里同样的错误犯了两次：

| 场景 | 错误做法 | 正确做法 |
| --- | --- | --- |
| 键盘 | 以为 RDP 不转发，自己注入 | RDP 原生转发，只需给焦点 |
| 会话 | 以为没会话不能连，加拦截 | RDP 会自己登录，别拦 |

**共同点：低估了 RDP 自己的能力，用额外机制去「补充」，结果反而破坏功能。**
下次遇到「某某不工作」时，先确认协议本身是否已支持，再考虑要不要自己实现。


### 12.28 ★★★ 非分层窗口的圆角：只能靠 DWM

用户反馈：「整个应用圆角没实现，四个角的圆角外还有一层阴影尖角」。

**根因**：主窗口为了 RDP 画面必须是非分层窗口（`AllowsTransparency=false`），
而**非分层窗口下 `Border.CornerRadius` 不会真的裁掉窗口四角**——
WPF 只是把内容画成圆角，窗口本身仍是矩形。
更糟的是 `DropShadowEffect` 会沿着**矩形边界**画阴影，于是圆角外面露出一圈直角阴影。

**修复分三步**：

```csharp
// 1. 让 DWM 做真正的窗口圆角（Win11 原生）
DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

// 2. 摘掉 WindowChrome —— 它的 CornerRadius=0 会把角做成直角，覆盖 DWM 效果
WindowChrome.SetWindowChrome(this, null);

// 3. 去掉外层 DropShadowEffect（它在直角区域会显形）+ 关掉 Shell 的 CornerRadius
RootLayer.Effect = null;
Shell.CornerRadius = new CornerRadius(0);
```

**副作用处理**：摘掉 WindowChrome 后窗口就不能拖动了，自己补 `WM_NCHITTEST`：

```csharp
if (msg == WM_NCHITTEST) {
    // 四边 6px → 缩放边框；顶部 64px 且避开按钮 → HTCAPTION 可拖动
    if (top && left) return HTTOPLEFT;
    ...
    if (pt.Y <= 64 && pt.X > 120 && pt.X < w - 260) return HTCAPTION;
}
```

实测截图放大后：四角是真圆弧，没有尖角阴影。

### 12.29 侧栏改成「图标栏 + 浮层」

用户要求：「收起后应该显示一栏图标，而不是一条线；展开后应该浮在上层，不要让画面闪来闪去」。

**布局**：
```
┌──────┬────────────────────────┐
│ 56px │                        │
│ 图标 │      画面（宽度恒定）    │
│ 栏   │                        │
└──────┴────────────────────────┘
       ↑ 展开时浮层盖在画面上，不改列宽
```

**关键点**：
- 列宽**恒定 56px**，绝不因展开/收起而变
- 浮层用 `Panel.ZIndex=50` + `Visibility` 切换，画面尺寸永不变化 → **不闪**
- 浮层从图标栏右侧（`Margin=66`）浮出，不遮住图标，避免鼠标在两者间跳来跳去

### 12.30 图标重复 & 对齐

用户反馈：「折叠图标与展开图标重复，项目没有对齐」。

原来图标栏里有展开按钮（E700），浮层里又有收起按钮（E76B），位置不同看着像重复。
**现在只有一个汉堡按钮**放在图标栏顶部，图标跟随状态切换 E700 ⇄ E76B 并高亮。

浮层里不再放任何切换按钮，标题只保留「控制台」文字。

### 12.31 侧栏透明度

原来用 `GlassCardStrong`（`GlassFillStrong`，很透），底下的画面会透上来干扰阅读。
新增 `NavSurface` 样式：`Background=#F20A1020`（95% 不透明），专门给图标栏和浮层用。


### 12.32 ★★★ RDP 挡住左侧菜单：必须用 Popup（空域无解）

用户反馈：「RDP 挡住左侧菜单了」。

**根因**：RDP 控件是 `WindowsFormsHost` 里的 ActiveX，属于 **WPF 空域（Airspace）**。
空域内容永远绘制在 WPF 内容**之上**，`Panel.ZIndex` 对它**完全无效**——
ZIndex 只影响同一视觉树内的 WPF 元素排序，跨不过 HWND 边界。

**唯一解**：把浮层做成独立 HWND，也就是 `Popup`。

```xml
<Popup x:Name="NavPopup" Placement="Relative" PlacementTarget="{Binding ElementName=NavRail}"
       AllowsTransparency="True" StaysOpen="True">
    <Border x:Name="NavPanel" ...>...</Border>
</Popup>
```

实测：浮层现在能完整盖住 RDP 画面。

### 12.33 外框叠层 / 只保留内部圆角

用户反馈：「叠了一层框，很难看。仔细看内部有圆角，只需要内部那个」。

原因：`Shell`（CornerRadius=24 + 边框）+ 内层玻璃 Border（CornerRadius=23 + 边框）
**各画了一道框**，看上去就是双层。

改成：外层只铺底色不画框，只有内层玻璃层保留圆角和边框。

### 12.34 标签卡只在打开后出现

用户要求：「打开之后才出现用户标签卡，没打开不需要」。

加了 `_openedIds` 集合，只有真正打开过画面的用户才进 TabBar：
```csharp
foreach (var row in _rows)
{
    if (!_openedIds.Contains(row.Account.Id)) continue;   // 没打开过 → 不显示
    TabBar.Children.Add(MakeChip(row.Account.Caption, row));
}
```

### 12.35 悬停 2 秒才展开

用户反馈：「没对齐，设置一个悬浮 2 秒再打开，按钮本来就可以点击」。

图标栏的悬停改成 **2 秒延迟**：鼠标只是划过不会弹出来，
要停住 2 秒才展开；而图标本身随时可以直接点，不用等。

### 12.36 添加用户 / 通用设置的位置

| 项 | 改到哪 |
| --- | --- |
| 添加用户 | 用户列表**上方**（原来在顶部工具栏） |
| 通用设置 | 侧栏**标题行右侧**齿轮图标（原来混在导航列表里） |

另外明确：**「添加用户」实际对应的是从「在线会话」里挑一个已登录用户**，
不是新建账号，tooltip 和空列表提示都写清楚了。

### 12.37 ★ 用户列表多选

用户要求：「用户列表需要可以多选，可以同时启动多个用户」。

- 每行左侧加复选框
- 也支持 **Ctrl / Shift + 点击**切换勾选
- 有勾选时，列表底部出现「打开勾选的 N 个画面」按钮
- 点击后逐个接入，标签卡依次出现，最后显示第一个

```csharp
foreach (var row in targets) {
    _openedIds.Add(row.Account.Id);
    if (row.Console == null) InitializeConsole(row);
    if (!row.Console.IsConnected) await ConnectAsync(row);
}
```


### 12.38 ★ 研读 1Remote 后的修正（12.32~12.37 的返工）

把 `MainWindowView.xaml`（48KB）拉下来对照后，发现我之前几个地方都做复杂了：

| 1Remote 的做法 | 我之前的做法 | 修正 |
| --- | --- | --- |
| `Popup` + `PlacementTarget` + `StaysOpen=False` | 自己算偏移、手动管开关 | 用 `Placement="Right"`，宽度随内容 |
| `MenuItem` 列表 | 自己拼 ListBox | 保持 ListBox（够用），但去掉重复入口 |
| 设置菜单只在 Popup 里 | 图标栏 + 浮层各一个 | **图标栏去掉**，只留浮层标题行齿轮 |

### 12.39 「粉框里还有方框」的真正原因

`GlassLayer` 这个 Border 有 `Margin="8"`，在圆角窗口内缩出一圈**矩形**边，
和 DWM 的弧形边不重合，看起来就是方形框套在圆形角里。

**直接删掉这层**，圆角完全交给 DWM：
```xml
<!-- 不再画内层边框，它会形成一圈矩形 -->
```

### 12.40 注销用户要同时关掉窗口

之前只执行 `logoff.exe`，但该用户的画面页签还挂着。现在注销成功后：
```csharp
_openedIds.Remove(row.Account.Id);   // 标签卡消失
row.Console?.Dispose();             // 释放 RDP 控件
row.Console = null;
if (ReferenceEquals(_viewing, row)) { ShowConfigPage(); }   // 回到配置页
```

### 12.41 「添加用户」= 新增配置，不是挑在线会话

用户明确纠正过两次。现在直接建一个空账号配置并进入编辑态：
```csharp
var account = new Account { DisplayName = "新用户", RdpHost = "127.0.0.3", RdpPort = 3389 };
App.Config.Accounts.Add(account);
_editing = row;   // 直接进入编辑
NameBox.Focus();
```

### 12.42 勾选框重做

之前用 `CheckBox` 默认样式，是个小方块，且和文字基线对不齐。改成：
- **圆形**（`CornerRadius=7`，14x14），未选是空心圆，选中填充紫色
- 用两列 `Grid`（固定 22px + 星号列）代替横排 `StackPanel`，**保证基线对齐**
- 点击只切换勾选，不触发整行选中


### 12.43 12 项问题集中修复记录

| # | 问题 | 修法 |
| --- | --- | --- |
| 1 | 顶部圆角失效 | 内容区顶部留 10px，Shell 背景改透明，底色交给 Window 由 DWM 裁切 |
| 2 | 浮层与图标栏错位 | 两边使用**逐行等高的同一结构**（汉堡36 + 分隔21 + 导航38×5 + 分隔21） |
| 3 | 添加用户 | 建空账号 + 直接进编辑态 + 焦点到名称框 |
| 4 | 右键菜单 | `ContextMenu`：打开画面 / 编辑配置 / 复制一份 / 勾选 / 删除 |
| 5 | 虚线焦点框 | 定义 `NoFocusVisual`，应用到所有按钮样式 |
| 6 | 注销确认框 | ✅ 已改自绘玻璃窗口，见 12.45 |
| 7 | 注销白屏 | ✅ 独立深色过场 + 淡入淡出，见 12.45 |
| 8 | 设置入口 | 移到用户列表第一条卡片，点右侧显示通用设置 |
| 9 | 锁定后键盘失效 | **`BeginCapture` 补 `GiveKeyboardFocus()`** —— SetCursorPos/Cursor.Hide 会抢走焦点 |
| 10 | 取消勾选误开用户 | 勾选框 `PreviewMouseLeftButtonDown` 就吃掉事件；尺寸 14 → 18 |
| 11 | 卡片两个 IP / 状态灯错位 | 删除重复的地址行；改三列 Grid：勾选框 / 文字 / 状态灯 |
| 12 | RDP 设置项不全 | ✅ 已补音频/录音/性能/组合键/设备重定向，见 12.46 |

### 12.44 ★ 踩坑：全局隐式样式冲突导致启动崩溃

给 `Button`/`ListBoxItem`/`CheckBox`/`ComboBox` 加了**无 Key 的隐式样式**，结果：

```
ArgumentException: Item has already been added.
Key in dictionary: 'System.Windows.Controls.ComboBox'
```

`Styles.xaml` 里已经有同类型的隐式样式，同一个字典里不能有两个同 TargetType 的无 Key 样式。
**退出码 -532462766（0xE0434352）**就是 .NET 未捕获异常，且发生在 `OnStartup` 之前，
boot.log 是空的 —— 说明是 XAML 加载阶段就崩了。

修法：删掉全局隐式样式，改用**带 Key 的显式样式**（`NoFocusVisual`）应用到各个具名样式上。

### 12.45 2026-11-15 注销确认框与过场（已实测） ✅

- 注销不再使用 `MessageBox.Show`，改用 `Views/LogoffConfirmWindow.xaml` 自绘玻璃窗口。
  默认聚焦「取消」，危险操作按钮为「注销并关闭」，保留明确的会话号与数据丢失提示。
- 新增 `Views/LogoffTransitionWindow.xaml`：**独立 HWND** 覆盖整个 RDP 视口。
  先淡入深色「正在注销…」，再断开/销毁 RDP；成功后返回配置页、显示完成态并淡出。
- 过场窗口用 `Owner` 跟随主窗口，坐标按 `PointToScreen + TransformFromDevice` 从物理像素换算为 DIP。
- ★ 实测坑：非分层窗口上 `Window.Opacity` 动画不可靠，关闭时画面残留。
  过场本身**不含 RDP 子控件**，所以允许 `AllowsTransparency=True`；改后实测淡出/关闭正常。
  这不违反 10.4：RDP 所在主窗口仍必须是非分层窗口。
- 注销入口带防重入状态，取消时恢复按钮；异常也会走淡出，不会留下永久遮罩。
- 截图：`docs/screenshot-logoff-confirm.png` / `screenshot-logoff-transition.png` / `screenshot-logoff-complete.png`。

### 12.46 RDP 设置项补齐（已实测保存、回读、连接） ✅

`Account` 新增账号级参数：

| 配置 | 控件属性 / 值 |
| --- | --- |
| 音频输出 | `SecuredSettings3.AudioRedirectionMode`：0=本机 / 1=远端 / 2=禁用 |
| 录音 | `AdvancedSettings8.AudioCaptureRedirectionMode` |
| 网络等级 | `AdvancedSettings8.NetworkConnectionType`：0=自动，1~6 与文档一致 |
| 性能预设 | `AdvancedSettings9.PerformanceFlags`：自动=0，省带宽=0x6F，均衡=0x18F，高画质=0x180 |
| 组合键 | `SecuredSettings3.KeyboardHookMode`：0=本机 / 1=远端 / 2=仅全屏 |
| 设备 | 打印机 / 剪贴板 / 智能卡 / 端口 / 驱动器重定向，默认全关 |

- ★ 本机 `AdvancedSettings2` **没有 `RedirectComPorts`**，写它会抛 `RuntimeBinderException`；
  实测应使用 `RedirectPorts`。不能照抄 .rdp 文件的 `RedirectComPorts` 名字。
- 所有新参数在连接前写入，并逐项回读到 `rdp-debug.log`。
  全开测试回读：`Capture=True / Clipboard=True / Printers=True / Drives=True / SmartCards=True / Ports=True`，连接成功。
- 性能预设不覆盖用户色深；旧 `RdpLowBandwidth=true` 仍兼容并优先使用省带宽标志。
- 配置保存/复制/重载均包含新增字段；测试后 a2 已恢复 1920×1080、16 位、拉伸、设备全关。
- 连接中/错误遮罩由普通 WPF Border 改为 **Popup**，否则仍会被 RDP 空域盖住，看不到错误。

### 12.47 自动登录后注入会话号 / 自适应分辨率修正 ✅

- RDP 自动登录成功后，按账号重新解析**真实会话 ID**，再投送代理；不再拿初始的 0 投送。
  实测日志：`连接后真实会话号=13` → `输入探测：就绪 · 会话可交互 · 屏幕 1920x1080`。
- `RequestResize` 改为读取**账号的** `RdpScaleMode`，不再只看全局默认；
  尺寸用物理视口而非 DIP，避免 150% 缩放下远端分辨率变小。
- 实测：`Mode=0 SmartSizing=False`，1920×1080 桌面正常内嵌（窗口内滚动查看），输入注入就绪。
- 内嵌全屏功能仍未做，保留在待办。

### 12.48 待排查：用户多选框无法使用（2026-11-15 用户反馈）

**现象**：中控台 → 配置 → 用户列表，A2 用户卡片左侧的圆形多选框无法勾选。

**实际结果**：无法通过勾选框选择用户，因此无法进行多用户批量操作。

**期望结果**：点击圆形多选框可以切换选中状态；支持一次勾选多个用户，并显示「打开勾选的 N 个画面」批量操作按钮。

**状态**：待排查，尚未修改代码。截图中勾选框位于 A2 卡片最左侧、用户名称的左下方。

### 12.49 待排查：窗口顶部边框与底部圆角不一致（2026-11-15 用户反馈）

**现象**：SoulLink 主窗口顶部外框偏粗，顶部内侧边角呈直角；与底部的圆润边角效果不一致，视觉上不统一。

**期望结果**：顶部采用与底部相同的圆角与边框效果，避免粗边框和直角，保持整个窗口轮廓一致。

**状态**：待排查，尚未修改代码。用户截图中可见主窗口顶部及左右上角的差异。

### 12.50 待排查：侧栏展开对齐、间距及高亮同步（2026-11-15 用户反馈）

**现象与期望**：

1. 展开侧栏浮层没有做到与左侧图标栏**等大展开**、逐行对齐；希望两者尺寸及对应菜单项对齐。
2. 图标栏与展开卡片之间留有一条明显间隙；希望两个卡片之间**不要留缝**。
3. 图标高亮时，对应的文字没有同步高亮；希望当前导航项的图标和文字保持相同的高亮状态。

**状态**：待排查，尚未修改代码。用户截图红框标出了图标栏与展开侧栏的整体区域及中间缝隙。

### 12.51 待排查：应用设置按钮消失（2026-11-15 用户反馈）

**现象**：左侧图标栏底部的「应用设置」按钮消失，截图红框标出的底部区域为空白。

**实际结果**：用户无法从原入口打开应用设置。

**期望结果**：恢复图标栏底部的应用设置按钮，图标、提示及点击入口正常，并与其他导航项保持对齐。

**状态**：待排查，尚未修改代码。

**补充反馈**：应用设置按钮即使出现也打不开；点击应跳转到「通用设置」页面。

### 12.52 待排查：通用设置误显示为 RDP 配置（2026-11-15 用户反馈）

**现象**：「通用设置」卡片右侧显示分辨率、色深、缩放、性能、音频及设备重定向等 RDP 参数，内容与应用级设置不符。

**期望结果**：通用设置只放所有用户共用的应用级选项（如外观、侧栏行为、锁定热键/灵敏度等），不显示某个用户的 RDP 连接配置。

**状态**：待排查，尚未修改代码。该问题与 12.51 的设置入口失效属于同一组导航/内容展示问题。

### 12.53 待排查：添加用户后配置区空白，无法新增（2026-11-15 用户反馈）

**现象**：点击中控台用户列表上方「添加」后，右侧仍显示「从左侧选一个用户进行配置」的空白提示，没有新增用户的设置表单。

**实际结果**：无法填写用户名、地址等信息，新增用户流程无法继续。

**期望结果**：点击添加后立即进入新用户编辑表单，可填写并保存配置；不能停留在空白提示页。

**状态**：待排查，尚未修改代码。

### 12.54 待排查：用户卡片交互出现虚线框（2026-11-15 用户反馈）

**现象**：添加用户、点击用户卡片等交互过程中，卡片外出现虚线焦点框；A2、A3 等用户行均可见。

**期望结果**：任何交互过程都不能出现该虚线框，焦点提示应融入现有玻璃主题，不能破坏卡片外观。

**状态**：待排查，尚未修改代码。

### 12.55 待排查：用户右键菜单与主题不协调（2026-11-15 用户反馈）

**现象**：用户卡片右键菜单的配色、边框、圆角及交互样式与当前深色玻璃主题不统一。

**期望结果**：右键菜单采用与整体一致的深色玻璃风格，文字/图标、悬停和选中态协调，并与现有控件视觉规范统一。

**状态**：待排查，尚未修改代码。

### 12.56 待排查：用户配置标题及保存/删除操作不应随内容滚动（2026-11-15 用户反馈）

**现象**：用户配置页向下滚动后，顶部的用户名称、用户信息及「打开画面」按钮会离开视野；「保存配置」「移除用户」仍位于内容底部，需要滚到底部才能操作。

**期望结果**：顶部用户标题/主要操作区固定显示，不能随内容滚走；「保存配置」「移除用户」也放到顶部固定操作区，配置内容单独滚动。

**状态**：待排查，尚未修改代码。

### 12.57 待排查：应用内弹窗风格不统一（2026-11-15 用户反馈）

**现象**：移除用户等确认操作仍使用粉色标题栏、白色内容区的系统 `MessageBox`，与深色玻璃主题明显不协调。虽然注销确认框已自绘，但其他弹窗尚未统一。

**期望结果**：全面检查应用内所有页面和弹窗，不只处理注销框；确认、警告、错误及输入弹窗都应使用统一的深色玻璃自绘风格，按钮、文案层级和默认安全操作保持一致。

**状态**：待排查，尚未修改代码。截图为「移除用户」的系统确认框，需同时覆盖其他页面残留的 `MessageBox.Show`。

### 12.58 待排查：注销过场置顶遮挡其他应用（2026-11-15 用户反馈）

**现象**：注销过程中，「正在注销…」过场画面会挡住一切；即使切换到其他应用，过场仍置顶显示在最前方。

**实际结果**：过场影响其他应用的正常查看与操作，而且可能覆盖 SoulLink 中不属于当前注销对象的内容。

**期望结果**：过场只固定在 SoulLink **原用户画面标签页**的视口内；切换应用时不应继续置顶遮挡，切换用户标签时应留在原标签页，不覆盖其他标签。主窗口最小化/隐藏时也应同步隐藏，完成或失败后必须关闭。

**状态**：待排查，尚未修改代码。重点检查独立过场窗口的 `Topmost`、所属窗口及页面可见性联动。

### 12.59 待排查：动作编辑器未显示用户且重复要求密码（2026-11-15 用户反馈）

**现象**：启动台动作编辑器中，「① 以哪个用户身份运行」的账号下拉框没有显示当前用户；下方仍展示「② 该用户的密码」输入框、显示密码选项及密码提示。

**用户预期**：动作已绑定并保存好账号凭据时，执行命令**不应再重复要求输入密码**，应直接复用已加密保存的凭据。

**期望结果**：
1. 正确显示当前动作绑定的用户/账号名称，不允许出现空白账号选择状态。
2. 移除动作编辑器里重复的密码输入及「显示密码」等密码相关区域；已有账号配置与 DPAPI 凭据继续复用。
3. 若账号确实没有可用凭据，应引导用户到账号配置中处理，而不是在执行动作时重复收集密码。

**状态**：待排查，尚未修改代码。本次仅记录 UI/交互需求，未验证任何投送、登录或命令执行。

### 12.60 待实现：RDP 键鼠按点击捕获 + 通用设置开关（2026-11-15 用户反馈）

**期望的 RDP 默认交互**：
1. 默认**不捕获鼠标和键盘**，不抢宿主桌面输入。
2. 用户主动点击一次 RDP 画面后，才捕获并转发键鼠。
3. 鼠标移出画面范围后立即释放捕获，停止向远端发送输入；再次回到画面时需要**重新点击**才能捕获。
4. 捕获状态应有清晰视觉提示，切页、失焦、最小化、断开等场景也要安全释放。

**通用设置要求**：
- 将上述「点击捕获 / 移出释放」行为做成可配置的开关，放入**通用设置**。
- 原画面齿轮按钮里的内容也要并入通用设置，包括**锁定灵敏度、锁定热键**等，不再只藏在 RDP 工具栏中。
- 保留现有「鼠标锁定转视角」功能语义；本项调整的是**何时开始接收键鼠**，不能误改成重复注入键盘或破坏已修复的锁定键盘问题。

**状态**：待实现，尚未修改代码。该需求应与 12.51/12.52 的设置入口和通用设置页面重构一起实现。

### 12.61 待实现：用户拖动排序及按列表顺序批量执行（2026-11-15 用户反馈）

**期望行为**：
1. 中控台的用户列表支持拖动卡片调整顺序，排序状态持久化，重启应用后保持。
2. 多选多个用户并启动/打开画面时，严格按照列表从上到下（即用户自定义顺序）执行，而不是按勾选先后、字典顺序或随机顺序。
3. 排序不应误触打开画面、勾选、右键菜单或编辑操作；拖放过程需要提供清晰的插入位置/目标提示。
4. 多用户执行仍是逐个、可观察的操作；单个用户失败时记录明确日志，不应把顺序打乱。

**状态**：待实现，尚未修改代码。与 12.48 的多选失效、12.53 的新增用户表单缺失属于同一组用户列表交互需求。

### 12.62 待实现：梦幻炫酷、少女心的整体视觉升级（2026-11-15 用户反馈）

**期望风格**：在现有深色玻璃/霓虹基础上进一步美化，营造**梦幻、炫酷、少女感**的整体观感，而不是仅给某个按钮换色。

**设计方向与边界**：
- 可采用柔和的樱花粉、薰衣草紫、星光青作为点缀，搭配克制的渐变、微光、细腻描边与轻量动效。
- 保持文字、状态信息和危险操作的清晰度/对比度；不要大面积过亮或持续闪烁，避免影响挂机查看。
- 统一窗口圆角、导航、用户卡片、输入框、按钮、菜单和弹窗，避免局部风格割裂。
- RDP 画面区域以清晰显示为优先，不让装饰覆盖空域或干扰键鼠； overlays 仍须遵循 Popup/独立 HWND 的规则。
- 美化不能牺牲性能、输入焦点和注销/断开等操作的可辨识性，需实机截图确认整体效果。

**状态**：待实现，尚未修改代码。可与 12.49~12.57 的边框、侧栏、焦点及弹窗风格问题统一设计。

### 12.63 待实现：深色/浅色双模式及侧栏主题切换（2026-11-15 用户反馈）

**期望行为**：
1. 应用支持完整的深色与浅色两套主题，覆盖窗口、导航、卡片、表单、菜单、弹窗及状态色，不能只改背景。
2. 在侧栏**设置按钮正上方**放置主题切换按钮，图标随当前模式变化，并有明确提示。
3. 保存用户选择的模式，重启后保持；切换过程应平滑，不需重启应用。
4. 浅色模式下同样保持文字对比度、边界辨识和危险操作可辨识；RDP 画面本身不重着色。
5. 与 12.62 的梦幻少女感风格统一设计，切换主题不能破坏窗口圆角、侧栏对齐和输入焦点。

**补充（12.63 三种主题模式）**：侧栏主题按钮不是二元切换，而是支持 **深色 / 浅色 / 跟随系统** 三种模式，点击循环切换；按钮图标与提示明确区分当前模式。「跟随系统」需读取 Windows 应用主题，并在系统主题改变时及时更新。保存的是三态选择，而非仅保存深/浅布尔值。

**状态**：待实现，尚未修改代码。

### 12.64 待设计：多用户自动化管理参考 autoBGI（2026-11-15 用户反馈）

**核心定位**：RDP 画面与输入是核心功能；多用户的自动化管理同样是核心功能，不能只做「能连画面」的中控台。

**参考对象**：[autoBGI 本机页面](http://127.0.0.1:10086/)（用户明确要求**不要执行操作**）。已只读获取首页 HTML，确认它是 myorg 的 Web 前端；未启动、保存、删除或触发任何任务。

**可参考并融入的功能**：
- 配置组管理：将多个动作/脚本组织成可复用、可编辑的配置组。
- 一条龙管理：将启动、运行、停止等步骤组织成明确的顺序流程，支持查看与编辑。
- 一条龙启动：从统一入口启动完整流程，展示当前用户、步骤及结果，避免黑盒执行。
- 定时任务：按时间/周期触发配置组或流程，具备启用状态、下次执行时间和执行记录。

**融入 SoulLink 的方向**：围绕用户列表、拖动顺序及 RDP 会话状态组织多用户自动化；能直观看到每个用户的配置组、流程、运行状态和日志。可参考 BGI 日志文档（`docs/bgi-realtime-log-reader.md`）做只读状态展示，但不把日志成功文本误当成任务终态。

**边界**：当前仅记录需求并只读查看入口，未调用 autoBGI 的操作接口，未执行任何自动化任务。

**状态**：待设计/实现，尚未修改代码。

### 12.65 2026-11-15 用户列表 / 导航 / 主题 / 自动化收尾（已实测） ✅

- **用户列表**：圆形多选框改为真实可访问的 `CheckBox`，Ctrl/Shift 和多用户批量入口继续可用；新增用户立即进入配置表单，空用户名会被提示且不会保存。卡片支持拖动排序，顺序写入 `Account.SortOrder` 并持久化。
- **固定操作区**：用户标题、「打开画面」「保存配置」「移除用户」固定在顶部，RDP 表单在下方滚动，不会把关键操作滚走。
- **导航**：侧栏图标与文字按钮同步高亮；设置按钮恢复在主题按钮下方，点击直接进入真正的通用设置。侧栏与浮层之间不留 10px 缝。
- **通用设置**：不再复用某用户的 RDP 表单；加入「点击画面才捕获键鼠，移出立即释放」开关、锁定视角灵敏度与锁定热键。
- **三态主题**：`ThemeMode` 为 0=跟随系统、1=浅色、2=深色；侧栏主题按钮循环切换并保存。样式画刷在启动时转换为 `DynamicResource`，切换时同步更新文字、导航、窗口与极光背景；浅色模式不再被固定深空底色覆盖。
- **弹窗**：新增统一自绘 `AppDialog`，注销、移除用户/账号/动作/配置组、结束进程等确认默认取消，替代粉色/白色系统 `MessageBox`。右键菜单使用同一套深色玻璃资源。
- **RDP 捕获**：`RdpCaptureOnClick=true` 时，画面默认不接收远端键鼠；点击一次后捕获并给 RDP 焦点，鼠标移出、切页、失焦、断开立即释放。普通指针模式及「锁定鼠标转视角」语义保持，键盘只走 RDP 原生通道，不重复注入。
- **自动化管理**：新增 `AutomationPage` 与 `AutomationProfile`，支持配置组、用户范围、动作流程、每天定时启动；执行使用现有 `ActionService.ExecuteAsync`，按中控台用户顺序与配置组动作顺序逐个执行。默认不启用定时任务，测试未启动用户程序。
- **注销过场**：独立窗口不再全局置顶，跟随主窗口与原 RDP 视口；切页、隐藏、最小化或完成后会隐藏/关闭，不遮挡其他应用。
- **实测**：编译发布通过；用户多选、新增表单、自动化创建/保存、通用设置入口和主题切换均有截图。测试产生的重复「新用户」配置已通过配置清理移除，仅保留 A2/A3 配置；未删除任何用户文件。临时验证工具已清理，测试实例已关闭。

### 12.66 待排查：侧栏主题与设置按钮需固定到底部（2026-11-15 用户反馈）

**现象**：当前主题切换按钮与应用设置按钮紧挨在上方的导航项之后，未利用侧栏底部的空白区域。

**期望结果**：主题切换按钮与应用设置按钮一起移到图标栏**底部**，保持与底部边距一致；展开侧栏中的对应入口也应与底部对齐，避免随导航项数量变化而上下跳动。

**状态**：待排查，尚未修改代码。截图红框标出了当前按钮位置和希望使用的底部区域。

### 12.67 待排查：RDP 账号密码配置位置缺失（2026-11-15 用户反馈）

**现象**：通用设置中的「已保存的账号」仍提供地址、改密码、移除入口；中控台各用户的配置表单只剩 RDP 登录名，无法在该用户处维护密码。

**实际结果**：账号凭据与应用级设置混杂，用户无法按 RDP 账号查看/修改对应密码，配置职责不清晰。

**期望结果**：
1. RDP 的用户名与密码配置统一放在**对应用户的配置卡片**中，与地址、端口、分辨率等参数一起维护。
2. 通用设置不再保存/展示 RDP 账号密码相关配置，只保留应用级设置。
3. 密码继续以 DPAPI 加密保存，不在界面、日志或命令行明文展示；已有加密凭据必须兼容迁移，不能被清空或覆盖。

**状态**：待排查，尚未修改代码。截图标出了通用设置的账号区和中控台用户配置中的 RDP 登录名位置。

### 12.68 待排查：RDP 色深下拉框与输入框宽度不一致（2026-11-15 用户反馈）

**现象**：用户配置页的「色深」下拉框比同行的宽、高输入框明显更宽，视觉上不对齐；同时它与上方「端口 / RDP 登录名」的输入区域也未形成统一宽度。

**期望结果**：用户配置表单中同一行的控件宽度保持一致，宽、高、色深等输入框/下拉框按统一网格对齐，不能出现色深控件单独突出或挤压布局。

**状态**：待排查，尚未修改代码。截图红框标出了色深下拉框的宽度差异。

### 12.69 待排查：窗口顶部内缩形成粗边框且圆角不一致（2026-11-15 用户反馈）

**现象**：SoulLink 窗口顶部的内部深色区域没有贴齐窗口边界，四周留下明显粗边框；顶部内侧仍呈直角，与底部及外层圆角不一致。

**期望结果**：顶部内部深色背景与窗口轮廓一致，避免额外粗边框和直角；上、下圆角及左右边缘使用统一的圆角和留白，不再出现「外框包住内框」的割裂效果。

**状态**：待排查，尚未修改代码。截图红框标出了顶部内缩与粗边框问题。此项与 12.49 的顶部圆角反馈属于同一类问题，应一并验证，不能只调整局部颜色。

### 12.70 待排查：侧栏整体联动、展开高度与提示遮挡（2026-11-15 用户反馈）

**现象与期望**：

1. **文字和图标应视为同一个按钮**：悬停图标时，对应文字也要同步高亮/执行动画；当前只有图标按钮有动画，文字无反馈。当前页面高亮可以保留，但不能代替悬停联动。
2. **展开卡片应是图标栏的延伸**：两者应在视觉上像一个折叠整体，中间不能有缝；背景、圆角、边框和过渡应连续。
3. **展开菜单应与左侧导航条等大**：不仅覆盖菜单内容区域，还要与左侧图标栏顶部、底部及高度一致，避免底部露出另一层卡片边缘。
4. **悬浮提示被盖住**：侧栏图标/文字的悬浮提示被其他窗口或浮层遮住；用户表示该提示可以删除，应移除被遮挡的提示，不再干扰操作。

**状态**：待排查，尚未修改代码。此项与 12.50、12.66 属于同一组侧栏结构/交互问题，应整体设计并验证深色、浅色及 RDP 空域场景。

### 12.71 待排查：浅色模式文字对比度不足（2026-11-15 用户反馈）

**现象**：浅色模式下，正文、标签、表单提示、侧栏及卡片标题仍偏淡、发白或带有浅色光晕，叠加极光/玻璃背景后难以阅读，整体观感与配色不协调。

**实际结果**：用户无法清楚辨认配置项、导航文字和状态内容，浅色模式不具备正常可用性。

**期望结果**：浅色模式下，主要文字、次级文字、输入框文字、按钮及禁用态均有足够对比度；背景与玻璃/极光效果降低干扰，不使用浅色发光文字。深色模式继续保持原有视觉层次，切换后不需要重启。

**状态**：待排查，尚未修改代码。需检查全应用而非只修用户配置页，尤其关注旧的固定浅色文字、低透明度文字和渐变/阴影对可读性的影响。

### 12.72 待排查：断开画面后未同时关闭用户标签页（2026-11-15 用户反馈）

**现象**：在 RDP 画面工具栏点击「断开」后，用户画面标签页仍然保留，画面处于断开/失效状态。

**期望结果**：点击「断开」时，同时关闭该用户的画面标签页并释放其 RDP 控件/输入资源，返回配置页或切换到其他仍打开的用户标签；**不要注销用户会话，也不要关闭目标用户正在运行的程序**。

**状态**：待排查，尚未修改代码。必须保持「断开画面」和「注销用户」语义严格分离。

### 12.73 待排查：RDP 连接/错误遮罩遮挡其他应用（2026-11-15 用户反馈）

**现象**：用户未登录或正在连接时，RDP 视口出现整块白色/深色遮罩，并显示「当前未登录，正在尝试用保存的凭据登录…」等提示；该遮罩会跑到其他应用上层，切换应用后仍可能覆盖其他窗口。

**期望结果**：连接中、失败和错误提示只固定在 SoulLink 对应用户标签页的视口内，不遮挡其他应用；切页、主窗口失焦/最小化时同步隐藏，结束连接或关闭标签页后立即释放。仍需遵循 RDP 空域规则，不能用普通 WPF Border 充当覆盖层。

**状态**：待排查，尚未修改代码。重点检查 `BusyPopup` 的可见性、所属窗口、切页和应用激活状态联动。

### 12.74 待排查：未登录/未连接时注销应立即关闭画面（2026-11-15 用户反馈）

**现象**：用户未登录、连接失败或 RDP 尚未建立连接时，点击「注销」仍可能进入会话注销流程，不能及时关闭画面。

**期望结果**：没有可确认的有效登录会话时，「注销」按**关闭当前用户画面/标签页**处理，立即释放画面与输入资源并返回配置页；不应执行 `logoff`、重复登录尝试或留下遮罩。存在有效登录会话时才进入注销确认与过场流程。

**状态**：待排查，尚未修改代码。必须避免因关闭无效画面而误建或误操作用户会话。

### 12.75 待排查：通用设置应内嵌在中控台右侧而非跳转页面（2026-11-15 用户反馈）

**现象**：点击用户列表中的「通用设置」后，跳转到独立的应用设置页面，未在中控台右侧的用户配置区域展示设置项。

**期望结果**：点击「通用设置」后，在中控台右侧**绿色标注的用户配置区域**内显示通用设置表单，保持左侧用户列表和页面上下文；不需要跳转到独立的应用设置页面。通用设置仍只包含应用级选项，不能显示某个用户的 RDP 参数。

**状态**：待排查，尚未修改代码。此项与 12.52 的内容归属、12.51 的按钮跳转属于同一组设置导航问题。

### 12.76 待排查：RDP 通用配置混入应用设置（2026-11-15 用户反馈）

**现象**：应用设置页仍显示「点击画面才捕获键鼠，移出立即释放」「锁定视角灵敏度」「退出鼠标锁定的热键」等 RDP 通用配置区块。

**实际结果**：RDP 专属交互设置与应用级设置混杂，与此前要求的「通用设置不显示 RDP 配置」不一致。

**期望结果**：将 RDP 通用交互配置整体移到中控台右侧的**通用设置**区域（12.75 所述内嵌位置）；独立应用设置页只保留应用行为、引擎/系统状态和配置管理等通用应用项，不显示 RDP 捕获、灵敏度或锁定热键。

**状态**：待排查，尚未修改代码。需与 12.75 一并调整归属，不应只删除控件而丢失设置入口。

### 12.77 待排查：RDP 工具栏齿轮应打开 RDP 通用配置且点击无响应（2026-11-15 用户反馈）

**现象**：RDP 画面工具栏中，位于「锁定鼠标」右侧的齿轮按钮点击后没有任何反应。

**期望结果**：该齿轮应进入 **RDP 通用配置**（即 12.75/12.76 所述中控台右侧内嵌设置页），显示点击捕获、灵敏度、锁定热键等 RDP 通用交互选项；不应进入独立应用设置页，也不能点击无响应。

**状态**：待排查，尚未修改代码。应与 12.75 的内嵌通用设置页面共用入口，保留当前用户画面上下文。

### 12.78 补充：RDP 通用交互配置必须作用于全部用户（2026-11-15 用户反馈）

**要求**：点击捕获、视角灵敏度、锁定热键等 RDP 通用配置必须作为**所有用户的统一设置**生效，而不是只对当前选中的 A2/A3 或某一个画面生效。

**预期行为**：
- 修改后保存到全局应用配置，所有已打开和后续打开的用户画面按同一规则执行。
- 不应要求逐个用户重复设置；用户专属 RDP 参数（地址、登录名、分辨率等）仍留在各用户配置中。
- 当前已打开的画面应及时同步最新通用配置，不能只对新连接生效。

**状态**：与 12.75~12.77 一并待实现，尚未修改代码。

### 12.79 待排查：已连接画面的注销按钮疑似失效（2026-11-15 用户反馈）

**现象**：当前已显示用户桌面画面的状态下，点击红色「注销」按钮似乎没有响应，没有出现二次确认弹窗，也没有注销过场动画。

**期望结果**：
1. 已建立有效 RDP 画面且能确认登录会话时，点击「注销」应先显示深色自绘二次确认框。
2. 用户明确确认后，显示「正在注销…」过场并执行注销，完成后关闭对应画面标签页并恢复配置页。
3. 注销过程中不遮挡其他应用，失败时有明确提示并恢复可操作状态。

**状态**：待排查，尚未修改代码。需要与 12.74 区分：本项针对**已连接、有效登录会话**，不能把有会话的注销错误地走成仅关闭标签页。

### 12.80 待排查：当前版本用户卡片仍无法拖动排序（2026-11-15 用户反馈）

**现象**：用户希望在左侧用户列表中直接拖动 A2/A3 卡片调整顺序；当前版本未能实现预期的拖动排序交互。

**期望结果**：用户卡片可拖放到目标位置，拖动过程显示清晰的插入位置，不误触勾选、打开画面或右键菜单；松手后保存顺序，重启后保持，多用户启动/打开画面严格按列表顺序执行。

**状态**：待排查，尚未修改代码。此项与 12.61 的拖动排序需求属于同一问题，后续修复后需实机验证，不能只以代码中存在拖放逻辑判定完成。

### 12.81 2026-11-15 用户反馈修复批次（12.66~12.80，已实测） ✅

- **侧栏**：主题与应用设置按钮固定到底部，展开菜单与图标栏等高且中间不留缝；图标与对应文字悬停同步高亮，不再出现只有图标有动画的状态。
- **RDP 通用设置**：新增 `Controls/RdpGeneralSettings`，内嵌于中控台右侧，齿轮和「通用设置」卡片共用入口。点击捕获、灵敏度、锁定热键作用于全部用户，保存时遍历已打开的 `RdpConsole.ApplyGlobalSettings()` 实时同步。
- **配置归属**：独立应用设置移除 RDP 捕获/灵敏度/热键及账号密码维护区；用户密码在对应用户配置卡片「修改密码…」打开 DPAPI 密码窗口，不再混入应用设置。
- **断开/注销**：断开关闭当前用户标签并释放 RDP，但不注销用户；无有效会话时注销仅关闭画面，不执行 logoff；有效会话保留二次确认与独立过场。实测 a2 的确认框、过场、完成恢复及断开关闭均通过。
- **遮罩**：`BusyPopup` 与主窗口激活、可见性和当前用户页联动，切换应用时收起，不再覆盖其他应用。
- **用户排序**：用户卡片右侧有明确拖动手柄，使用专用 `SoulLinkAccount` 拖放数据格式，避免普通字符串拖放无法命中。实测 A3 拖到 A2 前，`SortOrder` 保存为 1/2，重启读取顺序正确。
- **主题**：画刷更新改到真正的合并样式字典，浅色背景与深色文本正确切换；次级文字及低透明度文字提高对比度，极光背景在浅色下不再压暗页面。
- **布局**：色深与宽/高控件统一网格，密码按钮放在 RDP 登录名标题右侧，避免重叠；顶部内缩减小，保留窗口圆角边界。
- **收尾**：测试后恢复 A2/A3 原顺序与深色主题，未删除用户文件。测试实例与临时验证工具已清理。截图见 `docs/verify-logoff-confirm.png`、`verify-logoff-transition.png`、`verify-logoff-complete.png`、`verify-sort.png`、`verify-general-settings.png`。

### 12.82 待实现：端口、用户名和密码内联编辑布局（2026-11-15 用户反馈）

**现象**：当前端口与 RDP 登录名共占一行，端口输入框过宽；密码只能通过「修改密码…」弹窗维护，操作路径不够直接。

**期望结果**：
1. 缩小端口输入框宽度，将主要空间留给用户名和密码。
2. **用户名、密码、端口号直接填写在同一用户配置表单中**，不再通过弹窗修改密码。
3. 密码输入框默认掩码显示，沿用现有自绘输入框与深色/浅色主题；保存时继续用 DPAPI 加密，不在日志、界面回显或命令行暴露明文。
4. 保留已有加密凭据，进入编辑页面时不将真实密码明文展示；未修改密码时不能覆盖或清空已有密码。

**状态**：待实现，尚未修改代码。截图绿框标出了需要调整的端口/登录名/密码区域。

### 12.83 待排查：应用内多个区域出现虚线焦点框（2026-11-15 用户反馈）

**现象**：用户列表卡片、通用设置入口等位置在点击、聚焦或交互后出现虚线矩形框，破坏玻璃卡片的整体观感。用户表示应用内还有多处类似情况。

**期望结果**：统一排查整个应用的所有页面和交互状态，任何按钮、卡片、列表项、复选框、菜单或输入控件都不能出现此类虚线框；焦点反馈应融入现有主题，不使用系统默认虚线样式。

**状态**：待排查，尚未修改代码。不能只隐藏某个用户卡片，需要检查模板中的 `FocusVisualStyle`、键盘焦点、自定义 Border 以及局部覆盖，确保深浅主题下均不会出现。

### 12.84 待排查：窗口外框及表单控件尺寸/密度不统一（2026-11-15 用户反馈）

**现象**：
1. 应用外框问题仍然存在，窗口顶部与边缘的内外框、圆角和留白不够统一。
2. 普通输入框与下拉框高度不一致，同行控件上下边缘不齐。
3. 表单标签、控件与区块之间留白偏大，整体不够紧凑合理。

**期望结果**：
- 统一窗口外框、圆角和边缘留白，消除此前反复出现的粗边框/内外框不一致。
- 所有输入框与下拉框使用统一的高度、内边距和圆角，同一行控件视觉尺寸一致。
- 重新整理用户配置页间距，保持信息清晰的同时减少无效空白，让常用配置更紧凑易读。

**状态**：待排查，尚未修改代码。此项与 12.49、12.68、12.69、12.82 应一并处理，不能只修局部边距。

### 12.85 待排查：A2 注销过场覆盖 A3 画面（2026-11-15 用户反馈）

**现象**：注销 A2 时，注销动画没有独立留在 A2 的标签页，切换到 A3 后仍会覆盖 A3 的画面。

**期望结果**：每个注销过场必须只属于发起注销的用户视口；切换到其他用户标签、页面或应用时，不得覆盖其他用户画面。过场与原用户画面销毁、切换和关闭必须严格同步，不能通过全局置顶遮盖解决。

**状态**：待排查，尚未修改代码。需重点验证多用户同时打开时的过场归属，而不能只验证单用户注销。

### 12.86 待排查：A3 已保存密码仍无法自动登录（2026-11-15 用户反馈）

**现象**：已为 A3 填写并保存密码，但连接时仍无法自动登录，会弹出系统密码输入框。

**实际结果**：保存的凭据没有按预期用于 RDP 自动登录，用户需要再次输入密码。

**期望结果**：核查对应用户的用户名、DPAPI 凭据保存/读取和 RDP 自动登录链路，确保保存的密码真实生效；不得在日志或界面泄露密码明文。若自动登录依赖 Windows 已保存凭据，应明确其限制，不能把「只保存到应用 JSON」误当成 RDP 控件已经使用密码。

**状态**：待排查，尚未修改代码，也未对 A3 做登录操作。后续实测只允许 A2/A3，并保留现有用户文件。

### 12.87 待实现：侧栏底部布局、整体展开与悬停联动（2026-11-15 用户反馈）

**需求**：
1. 绿框中的「鼠标悬停展开」和当前会话信息区域应移动到侧栏**底部**，与底部的主题/设置按钮统一布局。
2. 侧栏按钮左右边缘被遮挡，需要修正宽度、裁剪和边距。
3. 展开时不应看起来像两个独立卡片，图标栏与展开区域中间**无缝隙、无中间圆角**，视觉上为一个整体。
4. 当前按钮的高亮框要延伸到对应文字区域，不能只有图标部分高亮。
5. 鼠标悬停展开的延迟由 2 秒改为 **1.2 秒**。
6. 之前记录的被遮挡悬浮提示可以删除，不能再次盖住导航文字。

**状态**：待实现，尚未修改代码。与 12.50、12.66、12.70 同属侧栏整体交互问题，需一起实测。

### 12.88 待排查：浅色模式下展开侧栏及多个页面文字对比度仍不足（2026-11-15 用户反馈）

**现象**：浅色模式中，展开的深色侧栏与整体浅色背景不协调；启动台、在线会话、会话详情等页面的文字、状态信息和按钮内容仍偏淡，难以阅读。

**实际结果**：浅色系只改变了背景和部分控件，仍有固定深色面板、低透明度文字、渐变/发光色干扰，多个页面不具备正常可读性。

**期望结果**：
- 展开侧栏的背景、边框、文字、当前会话卡片及底部区域与浅色模式协调，不保留不协调的纯深色面板。
- 全面检查启动台、在线会话、会话详情、中控台及设置页的固定浅色/低透明度文字，提升对比度。
- 浅色模式的玻璃与极光效果不能覆盖文字；按钮文字、状态色及禁用态也要清晰可辨。

**状态**：待排查，尚未修改代码。此项与 12.71 属于同类问题，需跨页面统一处理，不能只修单个输入框或背景。

### 12.89 待排查：RDP 通用设置样式不一致、重叠及页面无法返回用户配置（2026-11-15 用户反馈）

**现象**：
1. RDP 通用设置的标题、卡片、滑块、热键录制框与用户配置页风格不统一，且出现文字重叠、进度条样式突兀、热键按钮边框不美观等问题。
2. 点击「通用设置」后界面被锁在该页面，无法返回用户配置。

**期望结果**：
- 通用设置复用用户配置页相同的标题、输入框、按钮、热键框和间距规范，消除重叠，形成一致的视觉风格。
- 能随时通过用户卡片或配置标签返回用户配置，不被通用设置页困住；RDP 齿轮也应能进入该内嵌设置页。
- 热键录制按钮应使用与用户配置输入框一致的尺寸、边框和主题样式。

**状态**：待排查，尚未修改代码。

### 12.90 待排查：点击捕获逻辑阻止鼠标进入 RDP 画面（2026-11-15 用户反馈）

**现象**：勾选「点击画面才捕获鼠标」后，鼠标无法移动到 RDP 窗口上层/画面区域，与预期交互相反。

**期望的正确语义**：
- 鼠标可以自由移动到 RDP 画面上方，默认不把远端键鼠交给 RDP。
- 用户主动**点击 RDP 画面一次后**，才由 RDP 接管鼠标和键盘。
- 鼠标移出 RDP 画面后立即释放；重新进入时需要再次点击才能接管。
- 不能把“尚未捕获”实现成拦截宿主移动，也不能阻止 RDP 控件收到用于激活的点击。

**状态**：待排查，尚未修改代码，也未进行 RDP 输入测试。重点检查低级鼠标钩子的矩形坐标、吞事件条件与焦点处理。

### 12.91 待排查：自由视角切换失效及按钮命名（2026-11-15 用户反馈）

**现象**：「锁定鼠标」按钮及用户自定义快捷键均无法切换进入自由视角状态。

**期望结果**：
- 「锁定鼠标」改名为**自由视角**，与功能语义一致。
- 按钮和用户自定义热键均能可靠切换自由视角/指针模式，切换状态有清晰提示。
- 修复失效原因时保留已修复的 RDP 键盘焦点、Escape/热键退出和失焦安全释放，不重复注入键盘。

**状态**：待排查，尚未修改代码，也未进行自由视角操作。需要核查 `CaptureOnClick`、`_pointerCaptured`、热键注册和 `BeginCapture` 之间的关系。

### 12.92 当前修复进展与凭据方案纠正（2026-11-15，待实测）

**已完成的代码修改**：
- 撤销 `RdpCredentialStore` 凭据管理器方案；1Remote 实际使用的是 `IMsTscNonScriptable.ClearTextPassword`。
- 新增 `Services/RdpNonScriptable.cs`，用 .NET 标准 COM `InterfaceIsDual` 调用密码接口；不再回读密码（微软明确它是**只写属性**）。
- 鼠标激活点击不再吞掉，「锁定鼠标」更名为「自由视角」，按钮切换已在前一轮实测通过。
- 用户密码改为配置表单内联 PasswordBox，端口固定 100px，用户名/密码分列；留空不覆盖已有密码。
- 通用设置可返回用户配置；注销过场按当前用户归属隐藏；侧栏悬停改为 1.2 秒。
- **重要配置事实**：A3 显示名为 A3、RDP 地址为 127.0.0.4，但其 `UserName` 当前仍为 a2；`RdpUserName` 才是 A3。连接后真实会话解析仍按 `UserName`，不能把两者混用。

**验证限制**：本阶段执行工具暂时不可用（`pwsh.exe ENOENT`），所以标准 COM 密码接口、A3 自动登录、浅色主题、侧栏整体外观、表单内联密码与注销过场独立性均**尚未完成本轮实机验收**，不能视为已完成。不要再用凭据管理器作为自动登录的替代结论。

### 12.93 1Remote 密码接口兼容修复（2026-11-15，A3 已实测通过） ✅

**实测结论**：A3 使用保存的密码可自动登录到桌面，不再出现 Windows 凭据输入框。

**根因**：1Remote 的 `IMsTscNonScriptable.ClearTextPassword` 是 **InterfaceIsIUnknown** COM 接口，不是 `InterfaceIsDual`。`Dual` 在 .NET 8 抛 `NotImplementedException`；旧 IDispatch 路径也无法找到该属性。

**正确实现**：
```csharp
[ComImport, Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsTscNonScriptable
{
    [DispId(1)]
    string ClearTextPassword { set; }
}
((IMsTscNonScriptable)ocx).ClearTextPassword = password;
```
- 参考 1Remote 的 `lib/MSTSCLib.dll` 只读反射后确认；密码只写，不能回读验证。
- 不使用凭据管理器作为该问题的替代路径，`RdpCredentialStore` 方案已撤销。
- A3 自动登录实测日志：`ClearTextPassword ok=True · IMsTscNonScriptable.ClearTextPassword 已设置`。
- 该问题与 `UserName`（跨会话代理）/ `RdpUserName`（RDP 登录）分离无关，不能混用字段。

**本轮其余修改**：内联密码三列布局、自由视角按钮/首次点击、通用设置返回、过场归属、侧栏底部/1.2 秒展开和浅色主题资源调整。后续验收应优先检查这些页面，而非再重复密码接口试错。

**待继续验收**：浅色模式下侧栏仍有固定深色/文字偏暗的表现，当前目标是继续统一这些样式与整体外观；密码接口已验收不代表全部界面任务完成。测试应用已关闭。

### 12.94 浅色侧栏与内联密码验收（2026-11-15，已实测） ✅

- **浅色侧栏**：`MainWindow.Loaded` 后重新应用主题，`NavRail` 与 `NavPanel` 使用 `NavBackground` 动态引用，浅色侧栏实测变为浅色面板；中间不再留缝，中间圆角已统一。
- **导航文字**：浅色下改用 `TextSecondary`，不再使用固定暗色，悬停/当前页同步高亮到文字区域；悬停展开时间为 1.2 秒。
- **内联密码**：端口固定 100px，RDP 登录名与密码分别占剩余列，`PasswordBox` 掩码输入；不再弹窗，留空保存不会覆盖已有 DPAPI 密码。
- **通用设置返回**：用户卡片点击可正常从 RDP 通用设置返回用户配置，RDP 齿轮与卡片共用入口。
- **密码接口**：按 12.93 的 IUnknown 方式，A3 已实测自动登录到桌面。
- **过场独立性已补测**：双用户画面打开后，A2 注销过场中切换到 A3，A3 画面正常显示，未被 A2 过场覆盖；完成后 A3 仍正常。过场窗口禁止鼠标命中、禁止完成态重新显示，切页隐藏后自动销毁。截图：`docs/transition-other-user.png`、`docs/transition-complete.png`。
- **截图**：`docs/fix-light-sidebar.png`（浅色侧栏）、`docs/fix-inline-password.png`（内联密码）、`docs/fix-general-settings.png`（通用设置）。测试后恢复深色主题，关闭 SoulLink 进程并清理验证工具。

### 11.7 测试红线（务必遵守）

**A1、A4 是绝对红线，任何情况下不得进行任何操作**（不投送、不注入、不 tsdiscon、不 logoff）。
只能操作 **a2 / a3**。调试时也不要删除这两个账号下的任何用户文件。

### 11.8 ★★ 为什么不能转视角：RDP 控件会吞掉所有鼠标输入

第一版把鼠标事件挂在 `WindowsFormsHost` 的 WPF `PreviewMouseXxx` 上，**完全收不到事件**。
实测排查过程：

| 做法 | 结果 |
| --- | --- |
| `ax.MouseDown`（WinForms 层） | ❌ 抛 `NotSupportedException: MouseDown 事件在此 ActiveX 控件上无效` |
| `wfh.PreviewMouseLeftButtonDown`（WPF 层） | ❌ 根本不触发 |
| `grid.PreviewMouseLeftButtonDown`（父容器） | ❌ 也不触发 |
| **`WH_MOUSE_LL` 全局钩子** | ✅ 能拿到全部鼠标事件 |

**结论：想在画面区域拦截鼠标，只能挂系统级低级钩子。**
实现见 `Services/LowLevelMouseHook.cs`，注意：
- 委托必须用字段保住引用，否则被 GC 回收后回调直接崩
- 返回 `(IntPtr)1` 才是「吞掉」，让画面和宿主桌面都不响应
- 画面矩形用 `_host.PointToScreen(...)` 实时算，窗口移动/缩放都跟着变

### 11.9 转视角手感：必须临时关闭指针加速（⚠️ 结论仍有效，但恢复逻辑见 11.11 的幂等修正）

光有钩子还不够。系统「指针加速」会把位移非线性放大——**实测推 70 像素被算成 154**，
转视角就会越转越快、完全不可控。

解决方法（`Services/PointerAcceleration.cs`）：
- 开始转视角时 `SystemParametersInfo(SPI_SETMOUSE)` 把 `MouseSpeed` 置 0
- 结束时恢复用户原设置
- **引用计数**：多个控制台同时转视角不会互相踩
- 程序退出/卸载时兜底恢复，绝不把用户系统设置留在关闭状态

实测：`speed=10` → 转视角中 `speed=0` → 结束后 `speed=10` ✅
位移也从 154 变成 60（线性、可控）。
