# MobaXterm RDP 嵌入实现机制：反汇编级确认（第二轮研究）

> 研究日期：2026-11-17（会话 2）
> 对象：`D:\AAA挂机专区\远程本地多用户\MobaXterm\` 下的 MobaXterm.exe 26.5.0.5542 与 MobaRTE.exe 1.8.80.0
> 方法：**静态反汇编**（PE 解析 + capstone x86-32），未启动 MobaXterm、未连 RDP、未修改任何产物。
> 本文把上一轮 `MOBAXTERM-RDP-ARCHITECTURE-RESEARCH-20261117.md` 的"证据指向"结论**升级为机制级确认**。

---

## 〇、一句话结论

MobaXterm 的 RDP 不是"把 ActiveX 塞进主窗口"，而是：

> **主程序把宿主面板 HWND 通过命令行 `-mobahwnd <十进制HWND>` 交给 MobaRTE 子进程；
> 子进程内唯一的 MSTSCLib ActiveX 窗体用 `SetParent` 挂成该面板的子窗口；
> 之后主程序只用 SetWindowPos/ShowWindow 改矩形和可见性，子进程只负责一个 RDP 连接。**

这是"进程隔离 + 子窗口嵌入"，不是独立顶级窗口，也不是同进程多 ActiveX。

---

## 一、进程与命令行的完整链路

### 1.1 主程序构造 RDP 命令行

MobaXterm.exe 里有一组连续的 Delphi 字符串对象（`ff ff ff ff` + 长度 + 内容），
在 0x4D1C40 附近被顺序 push 拼成命令行：

```text
mobardp
 -host "<host>"
 -port <port>
 -user "<user>"
 -displayres "<W>x<H>"
 -zoomlevel "<n>"
 -authlevel <n>
 -mobahwnd "<HWND>"
 -remcmd "<cmd>"
 -redirdrives / -redirectwebauthn / -redirprinters / -redirports
 -audiomode <n> / -smartcard / -comp / -RedirectClipboard
 -slow / -forwardkeys / -autoscale / -autoAdaptResolution
 -console / -nocredssp / -debugmode / -noreconnectmsg
```

关键代码（0x4D1F4B）：

```asm
004d1f4b  call 0x69cd20            ; if 已嵌入(全局[0xbe64b0]<>0) 则跳过
004d1f50  test al,al
004d1f52  jne  0x4d1f8f           ;   → 不追加 -mobahwnd
004d1f54  push [ebp-0x10]          ;   cmdline 累加器
004d1f57  push 0x8d89d0            ;   " -mobahwnd \""
004d1f5c  mov  eax,[0xbe59e4]
004d1f63  call 0x81774             ;   ← 取"宿主面板"的 Win32 HWND
004d1f6c  call 0xc164              ;   整数 → 字符串（参数已 push，结果写入 [ebp-0x3a8]）
004d1f7d  push 0x8d828c            ;   "\""
004d1f8a  call 0x5484              ;   拼接到命令行
```

- `0x81774` 是 Delphi 的 `TWinControl.GetHandle`：确保句柄已创建，返回 `[Self+0x180]`。
  → **传给子进程的是主程序自己进程内的一个可控面板窗口句柄，不是子进程的窗口。**
- `0x69cd20` 读全局 `[0xbe64b0]`，非 0 说明当前已经是嵌入模式，不再重复传 `-mobahwnd`
  （避免"嵌入的子进程再启动子进程"时无限传递）。

### 1.2 子进程由 CreateProcessA 启动

MobaXterm.exe 全文件只有 **1 处** `CreateProcessA` 调用（thunk 0x790c），
位于函数 `0x373400`（唯一调用者 `0x37416c`）。即：MobaRTE.exe 由 MobaXterm.exe 作为普通子进程创建，
**没有** `CreateProcessAsUser` / `WTSQueryUserToken` / `OpenInputDesktop` / `SetThreadDesktop` 这类跨会话 API。
→ 与 SoulLink 的"RDP 注入到其它会话"是两条完全不同的路。

配套证据：两进程都是 32 位 Delphi/VCL 原生程序（imagebase 0x400000，CODE 段在第 1 页），
导入表里 **没有** `mstscax.dll`、**没有** `wfreerdp.dll`，只有 `ole32/oleaut32` 的 COM 入口。

---

## 二、RDP 客户端本体：MSTSCLib 的"不可脚本化"版本

MobaRTE.exe 里完整存在 `MSTSCLib_TLB` 的 Delphi 类型库导入单元（约 0x2F99DE 起）：

```text
MSTSCLib_TLB / IMsTscAx / IMsRdpClient / IMsRdpClient2..9
IMsTscSecuredSettings / IMsTscAdvancedSettings / IMsRdpClientAdvancedSettings(2..7)
IMsRdpClientSecuredSettings / IMsRdpClientTransportSettings
TMsRdpClientNotSafeForScripting  +  TMsRdpClient6NotSafeForScripting
TMsRdpClientNotSafeForScripting.OnDisconnected / OnRemoteDesktopSizeChange /
  OnAutoReconnecting / OnFocusReleased / OnMouseInputModeChanged /
  OnNetworkStatusChanged / OnRemoteWindowDisplayed / ...
TForm / TPanel / TFrameRDP / TMobaRDPForm / THdpiRdpForm
```

要点：

1. 用的是 **`TMsRdpClientNotSafeForScripting`**（不可脚本化的 ActiveX），
   这正是"嵌入到宿主窗口里用"的标准选择；`TMsRdpClient`（可脚本化）会被浏览器类宿主限制。
2. 事件回调齐全（`OnRemoteDesktopSizeChange`、`OnAutoReconnecting`、`OnFocusReleased`），
   说明断开/自动重连/尺寸变化是**在子进程内闭环处理**的，主程序不参与重连时序。
3. 没有 FreeRDP。

---

## 三、嵌入实现：主程序的 3 步与子进程的 2 步

### 3.1 子进程侧：解析 `-mobahwnd`

```asm
; TFormMobaRDP 的 Create/参数解析里
00308db7  mov  eax,0x7090e4        ; "-mobahwnd"
00308dbc  call 0x1374fc            ; PosEx / AnsiPos
00308dc7  call 0x1376ac            ; 取参数值字符串
00308dcc  cmp  [ebp-0x1c],0
00308dd0  je   0x308e49            ; 没有该参数 → 跳过
00308e11  call 0xbb40              ; StrToInt
00308e1c  call 0xc010              ; Val / 转整数
00308e24  mov  [edx+0x358], eax    ; <<< 宿主 HWND 存进窗体字段 +0x358
...
00308e3e  mov  [eax+0x358], 0      ; 解析失败则清零
```

成功解析后 `-mobahwnd` 的值就是 **`TFormMobaRDP.ParentWindow`**（`+0x190`）的来源。

### 3.2 子进程侧：SetParent 把 RDP 窗体挂成子窗口

MobaRTE.exe 里 `SetParent` 一共只有 4 个调用点（0x820F4 / 0x2D5F83 / 0x2D6B6A / 0x2DBBEC）。
其中 0x820F4 是通用嵌入核心（与 MobaXterm.exe 中 0x7ED7C 是**同一段 VCL 代码**）：

```asm
000820bc  push ebx / push esi
000820be  mov  esi,edx              ; esi = 新的父窗口
000820c0  mov  ebx,eax
000820c2  cmp  [ebx+0x30],0         ; 已有父控件？→ 不处理
000820c6  jne  0x82140
000820c8  cmp  esi,[ebx+0x190]      ; 和当前 ParentWindow 相同？
000820ce  je   0x82140
000820d0  cmp  [ebx+0x180],0        ; 自己已创建句柄？
000820d7  je   0x8212c
000820d9  cmp  [ebx+0x190],0        ; 旧父已存在？
000820e0  je   0x8212c
000820e2  test esi,esi              ; 新父非空？
000820e4  je   0x8212c
000820e6  mov  [ebx+0x190],esi      ; 记录 ParentWindow
000820ec  push esi
000820ed  mov  eax,[ebx+0x180]      ; 自己的 HWND
000820f3  push eax
000820f4  call 0x88d4               ; >>> user32!SetParent(child, host)
000820f9  cmp  [0x76f620],5         ; 之后是调试日志分支
0008212c  call 0x81ee0              ; 无旧父时走"重新 CreateHandle"路径
00082133  mov  [ebx+0x190],esi
```

要点：

- 嵌入前先保证自己句柄已创建（`[+0x180]`），**旧父、新父都必须非空**，
  否则走 `0x81ee0` 的"销毁重建句柄再设父"路径 → 这正是 SoulLink 之前踩过的坑。
- 只改父，不改样式：没有找到 `or eax,WS_CHILD(0x40000000)` 之类的写入
  （MobaRTE 里唯一一处 `push 0x40000000` 在 0x2D6C2A，属于窗口挑选器的"排除子窗口"判断）。
  即 **依赖 `SetParent` 让窗口变成子窗口**，而不是手工改 GWL_STYLE。

### 3.3 主程序侧：SetParent 之后立刻按客户区矩形重排

MobaXterm.exe 的 4 个 `SetParent` 调用点（0x55FF68 / 0x56D833 / 0x583367 / 0x7ED7C）
**步骤完全一致**，是同一套"把子窗体贴进面板"的模板：

```asm
; 0x583366 处（典型）
00583366  push eax
00583367  call 0x879c               ; SetParent(child, host)
0058336c  cmp  [ebp-9],0
00583370  je   0x5833a2
; --- [ebp-9] == 0 的分支 ---
005833a2  push 0x4244               ; SWP_NOZORDER|SWP_SHOWWINDOW|SWP_NOOWNERZORDER|SWP_ASYNCWINDOWPOS
005833a7  mov  eax,[ebp-8]
005833aa  call 0x7a848              ; Width
005833b0  mov  eax,[ebp-8]
005833b3  call 0x7a804              ; Height
005833bd  push [0xbe4540]           ; 目标位置基准
005833cb  call 0x87f4               ; SetWindowPos
; --- [ebp-9] != 0 的分支 ---
00583372  push 0x4284               ; SWP_NOZORDER|SWP_HIDEWINDOW|SWP_NOOWNERZORDER|SWP_ASYNCWINDOWPOS
0058339b  call 0x87f4               ; SetWindowPos
```

- 两个标志值是**一对 SHOW/HIDE**，不是两套尺寸：
  `0x4244` = `SWP_NOZORDER|SWP_SHOWWINDOW|SWP_NOOWNERZORDER|SWP_ASYNCWINDOWPOS`（9 处）
  `0x4284` = `SWP_NOZORDER|SWP_HIDEWINDOW|SWP_NOOWNERZORDER|SWP_ASYNCWINDOWPOS`（4 处）
- 注意 `SWP_ASYNCWINDOWPOS`：**改的是别的进程的窗口**，必须走异步，
  否则会因跨进程 SendMessage 同步等待而死锁。这是跨进程嵌入的关键细节。
- 结论：**主程序只做"改父 + 改矩形 + 改可见性"，不碰 ActiveX 内部任何状态。**
- 另有 `MoveWindow` 2 处（0x5814AD / 0x701020）、`SetWindowRgn` 37 处（裁剪圆角/异形面板）。

---

## 四、子 → 主的"raise"信号：FindWindowA + 窗口类名

这是整套机制里最值得 SoulLink 借鉴的一段：**跨进程但不走任何 IPC 通道**。

### 4.1 子进程如何"抬起"主窗口

`TFormMobaRDP2/3` 的 `RaiseRootWindow`（0x30A784）与 `WMMouseActivate`（0x30A7F0）：

```asm
0030a784  push ebp / mov ebp,esp / push ebx/esi/edi
0030a78a  push 0
0030a78c  push 0x70a7dc           ; 窗口类名
0030a791  call 0x84dc             ; >>> user32!FindWindowA(nil, "UIMainClass")
0030a796  mov  ebx,eax
0030a798  test ebx,ebx
0030a79a  jbe  0x30a7d6           ; 没找到 → 什么都不做
0030a7aa  push 0x13               ; SWP_NOSIZE|SWP_NOMOVE|SWP_NOACTIVATE
0030a7ac  push 0 / 0 / 0 / 0
0030a7b4  push -1                 ; HWND_TOPMOST
0030a7b6  push ebx
0030a7b7  call 0x892c             ; >>> SetWindowPos(host, TOPMOST, ...)
0030a7bc  push ebx
0030a7bd  call 0x88ac             ; >>> SetForegroundWindow(host)
```

`RaiseRootWindow`（0x30A7F0 起）还额外对自己的客户窗口 `[ebx+0x2f8]` 做同样的 `SetWindowPos + SetForegroundWindow`，
前置一个全局开关 `[0x76fb94]` 作为"是否允许抢前台"的总闸。

配套的**可读字符串**（上一轮已发现，这里是它的实现）：

```text
BEGIN TFormMobaRDP2.RaiseRootWindow
  ... Trying to raise MobaXterm window as it seems that another window masks the RDP session...
  ... NOT trying to raise MobaXterm window as it seems that no other window masks the RDP session.
END TFormMobaRDP2.RaiseRootWindow
BEGIN TFormMobaRDP2.WMMouseActivate
  ... Mouse is over RDP frame...
  ... Sending MobaXterm the "raise" signal.
END TFormMobaRDP2.WMMouseActivate
```

### 4.2 为什么这点对 SoulLink 重要

| 问题 | SoulLink 过去的做法 | MobaXterm 的做法 |
|---|---|---|
| 子窗口被主窗口遮挡 | 反复 Show/Hide 覆盖层，WPF IsVisible 与 Win32 脱节 | 子进程自己检测 `WM_MOUSEACTIVATE`，用 `SetWindowPos`+`SetForegroundWindow` 抬主窗口 |
| 跨进程通知 | 需要自建 IPC | `FindWindowA(nil, "UIMainClass")` 按类名找主窗口，零通道 |
| 谁负责 Z 序 | 主程序 + 覆盖层 + Canvas 抢 | **子进程主动让位**，主程序不参与 |

**关键设计取向：把"抢焦点/Z 序"的责任下放到子进程，主程序只管矩形。**

---

## 五、隐藏成"面板里的一个控件"所需的两个细节

### 5.1 `-numtab`：从任务栏/Alt+Tab 里抹掉

```asm
00308ec7  mov  eax,0x709140        ; "-numtab"
00308f05  mov  eax,[ebp-0xc]       ; 解析出的 tab 序号
00308f0d  mov  [edx+0x354],eax     ; 存到 +0x354
00308f16  push -0x14               ; GWL_EXSTYLE
00308f23  call 0x8674              ; GetWindowLongA(Application.MainForm.Handle, -20)
00308f28  or   eax,0x80            ; WS_EX_TOOLWINDOW
00308f2e  push -0x14
00308f36  call 0x890c              ; SetWindowLongA(...)
00308f3b  ...                      ; 顺便把两个子控件设为可见
```

→ 子进程把自己标成 `WS_EX_TOOLWINDOW`：**不出现在任务栏、不出现在 Alt+Tab**，
   在用户看来它就是主窗口面板里的一个控件，而不是"另一个程序"。

### 5.2 `-disabletopbar`：无边框无标题栏

```asm
00308e5e  mov  eax,0x7090f8        ; "-disabletopbar"
00308e63  call 0x137414            ; 参数存在性判断
00308e6c  mov  eax,[ebp-4]
00308e6f  mov  eax,[eax+0x2fc]     ; RDP 窗体的顶栏控件
00308e77  call 0x7e098             ; Visible := False
```

→ 去掉子窗体自己的标题栏和按钮条，只留 RDP 画面，符合"嵌进面板"的观感。

---

## 六、缩放/分辨率：在子进程内闭环

`-displayres "WxH"`（0x308FB9 起）：

```asm
00308fdf  mov  edx,[ebp-8]         ; "WxH" 字符串
00308fe2  mov  eax,0x7091cc        ; "x"
00308fe7  call 0x11248             ; Pos('x')
0030900c  call 0x13729c            ; Copy(1..pos-1) → 宽
0030902a  call 0x13729c            ; Copy(pos+1..) → 高
00309019  mov  [ebp-0x10],eax      ; -> DesktopWidth
00309037  mov  [ebp-0x14],eax      ; -> DesktopHeight
```

配合可见字符串（0x308A10 区）：

```text
  BEGIN DesktopResize...
      RDPDisconnecting...
      DesktopWidth:
      DesktopHeight:
      Fullscreen:
      RDPReconnecting...
  END DesktopResize.
```

→ **改远端分辨率必须先主动断开再重连**，不是在线协商。
   另有 `-autoscale` → `SmartSizing: True`（本地缩放，不改远端分辨率）。

这正是 SoulLink 事故报告里"拖动/缩放反复 `UpdateSessionDisplaySettings`"的高风险点——
MobaXterm 的做法是**断开重连**，而不是在线重协商。

---

## 七、另一条线：普通外部程序窗口的嵌入（与 RDP 无关，但同源）

MobaRTE.exe 里还有一套"把任意外部程序窗口吸进来"的机制（0x2D5B28 / 0x2D6590），
它和 RDP 嵌入**共用同一个 VCL 嵌入内核**（0x820BC 的 ParentWindow setter），但挑选规则完全不同：

```text
排除类名: shell_traywnd / progman / tooltips_class32 / sysshadow
必须样式: WS_CAPTION | WS_SIZEBOX | WS_BORDER | WS_DLGFRAME | WS_MINIMIZEBOX | WS_SYSMENU
          （0x2D64CC..0x2D6560 的调试字符串："    WS_CAPTION" ... "    --> OK !"）
尺寸下限: 宽 > 0x78(120) 且 高 > 0x3C(60)   （另一分支是 >0xF0 / >0x78）
其它条件: IsWindowVisible(TRUE)、GetWindowLong(GWL_STYLE) 检查、
          同进程 & 非 Owned & 不是宿主自己的子窗口（GetParent 链比对）
重试方式: EnumWindows 循环 + 0x5 次 / 与 -timeout 参数比较的超时重试
失败提示: "Sorry, the external application window has not been detected.
          Please select it from the windows list below:"
调试输出: "Trying to determine the best candidate window from the entire windows list"
          "Candidate found: "
```

→ 说明 MobaXterm 的嵌入能力是**通用 VCL 嵌入**，RDP 只是其中一种被嵌入对象。
   RDP 走的是**确定性的句柄传递**（`-mobahwnd`），不走启发式挑选。

---

## 八、与上一轮结论的差异修正

| 上一轮说法 | 本轮证据 | 修正 |
|---|---|---|
| "主控 + MobaRTE 子进程" | 唯一 CreateProcessA 调用点 0x3734E6 | **确认** |
| "RDP 使用 MSTSCLib，不是 FreeRDP" | MSTSCLib_TLB 全类型库 + TMsRdpClientNotSafeForScripting | **确认**，并补充"不可脚本化"变体 |
| "-mobahwnd / ParentWindow 是嵌入线索" | `-mobahwnd "<HWND>"` 由主程序 GetHandle 填入，子进程 StrToInt 存 +0x358 | **升级为确定机制** |
| "主程序只改矩形和可见性" | SetParent 后 SWP 0x4244/0x4284 | **确认**，并给出具体标志值 |
| "通过私有协议协调" | 无管道、无 socket、无 ChangeWindowMessageFilter | **修正**：协调是 FindWindowA(类名) + SetForegroundWindow |
| "官方 Split 文案只写 terminals" | 未发现 RDP 分屏代码 | **维持**：仍不能证明 4 个 RDP 可同屏 |

---

## 九、对 SoulLink 的可用结论

### 9.1 可以直接借鉴（无需真实 RDP 即可验证）

1. **"一进程一个 ActiveX"是进程隔离的核心价值。**
   MobaXterm 从不把两个 ActiveX 放进同一个进程；SoulLink 的 0x119 事故恰恰来自
   "同进程多 ActiveX + 反复跨容器重挂"。
2. **宿主面板 HWND 由主程序显式创建并通过命令行交付**，子进程不猜、不启发式挑选。
   → SoulLink 若做子进程方案，宿主面板必须是主程序 `HwndHost` 下的一个真实 Win32 子窗口，
   并把它的 HWND 传给子进程。
3. **主程序职责收窄**：只做 `SetParent` → `SetWindowPos(SWP 0x4244/0x4284)` → `ShowWindow`。
   不参与重连、不参与分辨率协商、不碰 ActiveX 属性。
4. **`WS_EX_TOOLWINDOW` + 隐藏子窗体顶栏** 是让子进程"看起来像面板控件"的必要条件。
5. **子进程主动让出焦点**（`WM_MOUSEACTIVATE` → `SetWindowPos(TOPMOST)` + `SetForegroundWindow`），
   比在主程序里叠加覆盖层去抢 Z 序稳定得多——这正是 SoulLink 保护层反复失效的根因同类问题。
6. **分辨率变化走"断开→改 DesktopWidth/Height→重连"**，不要在在线会话里反复
   `UpdateSessionDisplaySettings`。

### 9.2 不能直接照搬

- MobaRTE 是 **VCL 原生窗体**，嵌入的是原生 HWND；SoulLink 是 **WPF**，
  必须用 `HwndHost`/`WindowsFormsHost` 提供真实 HWND，WPF 的视觉树和这套机制无关。
- MobaRTE 内部有 Delphi VCL 的 `ParentWindow` 生命周期管理（`[+0x180]`/`[+0x190]`），
  在 .NET 里对应的是 `Control.Parent` + `CreateControl`，语义不完全等价。
- 不能据此认为"当前机器的显示驱动环境对该方案安全"——转储仍未做内核栈分析。

### 9.3 建议的最小验证路径（不触发 0x119）

按事故记录的约束，下一步只做**不含真实 RDP**的纯 Win32 原型：

1. WPF 主窗口放一个 `HwndHost`，其子窗口（面板）创建为真实 Win32 窗口。
2. 启动一个**空的** WPF 子进程，把它唯一的窗口当作"RDP 窗体"。
3. 子进程读 `-mobahwnd <HWND>`，`SetParent` 到面板，主程序 `SetWindowPos` 铺满。
4. 子在 `WM_MOUSEACTIVATE` 里 `SetForegroundWindow` 主窗口。
5. 反复切换标签 / 最小化恢复 / DPI 变化，全部不接 RDP、不接 ActiveX。

只有这一步稳定通过，才谈"把 ActiveX 放进子进程"。

---

## 十、方法论备忘（下轮研究省时间）

分析这两个二进制时踩过的坑，写进脚本可避免重犯：

1. **Delphi 字符串对象布局**：`ff ff ff ff` + `u32 长度` + 字节数据 + 结尾对齐。
   代码里的立即数指向**数据首字节**，不是头部。
2. **操作数高 16 位是 0**：`push 0x8d89d0` 对应的绝对地址是 `0x008d89d0`；
   我的第一版脚本误加了 imagebase，导致全部字符串解错（表现为"标签返回 None"）。
   正确做法：`VA = 立即数`；`RVA = VA - imagebase`。
3. **导入调用是两级**：CODE 段开头是 `jmp dword ptr [IAT绝对地址]` 的 thunk 桩，
   业务代码 `call <thunk RVA>`。必须先把 thunk → IAT 槽 → 导入名 建表，否则
   `SetParent` 之类的调用一个都找不到。
4. **函数边界不能靠"找 55 8B EC"**：Delphi 函数常无栈帧或前面是数据，
   要正向线性反汇编验证能否精确落在目标地址上。
5. capstone 5.0.9 可从 PyPI 直接装（`python -m pip install capstone`）。

---

## 十一、仍未确认 / 未做的事

- 未运行 MobaXterm，无动态轨迹；所有结论来自静态代码。
- MobaRTE 里 `+0x358`（宿主 HWND）与 `ParentWindow(+0x190)` 的**赋值桥接点**没能精确定位：
  搜索 `[reg+0x358]` 只命中 0x304CE8（读取）与 0x308E24（写入），
  但 `ParentWindow` setter 的调用方需要完整 VMT 反解，本轮未完成。
- `UIMainClass` 在 MobaRTE 里只以字符串常量出现（0x30A91C），未找到可读的引用指令
  （位于寄存器间接寻址区域）。
- 未分析 RDP 分屏：没有找到任何"多个 RDP 同屏"的代码路径，维持上一轮结论。
- 未做 0x119 转储的内核栈分析——这是任何新方案的前置条件。

---

## 十二、复现脚本

本轮所有分析脚本留在 `research/mobaxterm/`：

| 文件 | 用途 |
|---|---|
| `pe.py` | 最小 PE 解析器（无第三方依赖），被其余脚本 import |
| `imp.py` / `imp2.py` | 导入表、资源表转储 |
| `strings.py` | ASCII / UTF-16 字符串提取（生成 `out/*.txt`，已被 .gitignore 忽略） |
| `stubmap.py` / `imports.json` | **thunk → IAT 槽 → API 名** 映射表（关键，其余脚本依赖） |
| `ann2.py`–`ann15.py` | 带注释反汇编（自动标注导入调用和 Delphi 字符串） |
| `final1.py`–`final10.py` | 关键站点追踪：`-mobahwnd` 生成/解析、SetParent、RaiseRootWindow |
| `xref4.py` / `xref5.py` | 字符串交叉引用（**注意绝对 VA 约定**） |
| `verify.py` | 样式/标志常量统计 |

运行前置：

```powershell
& <python> -m pip install capstone      # 反汇编用，5.0.9 可用
& <python> research\mobaxterm\stubmap.py   # 先建 imports.json
& <python> research\mobaxterm\ann6.py      # 再看 SetParent 站点
```

最关键的一条复现命令（能一次看到"宿主 HWND 被写进命令行"）：

```powershell
& <python> research\mobaxterm\final6.py
```
