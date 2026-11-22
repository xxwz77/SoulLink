# media-probe · SoulLink 媒体能力探测（只读 Spike）

DXGI Desktop Duplication 与 Media Foundation H.264 硬件编码的**可行性探测**。
回答一个问题：`STREAM-PERF-AUDIT` §4.5(e) 提的那条「DXGI + 硬编」路线，
在这台机器上到底能不能用、能不能撑 60fps。

配套报告：[`docs/research/MEDIA-CAPABILITY-20261122.md`](../../docs/research/MEDIA-CAPABILITY-20261122.md)

---

## 它只有一个用途

**测量，不改任何东西。** 具体：

- 只创建/销毁自己的 D3D 设备、duplication 对象、GDI 位图和临时 MP4
- 不写配置、不改注册表、不启停服务、不结束任何进程、不碰 `dist/`
- 临时 MP4 写在 `%TEMP%`，测完立即删除
- 唯一可选落盘是 `-Out` 指定的 Markdown 报告

可以放心在**有账号挂机时**跑：它不接触 SoulLink 的进程，只读本会话的桌面。

---

## 怎么跑

```powershell
cd tools\media-probe

# 直接编译运行
dotnet run -c Release

# 或先编再跑（更快复跑）
dotnet build -c Release
.\bin\Release\net11.0-windows\media-probe.exe

# 顺带把结果写成 Markdown
.\bin\Release\net11.0-windows\media-probe.exe -Out ..\..\artifacts\media-probe.md
```

退出码：**0 = 关键项全部 OK**，**1 = 有 FAIL**（便于脚本判断）。

---

## 探测项一览

| # | 项 | 判定依据 |
|---|---|---|
| 1 | 会话环境 | `SM_REMOTESESSION` → **先确认是不是 RDP**，它决定其余结论的适用范围 |
| 2 | DXGI 适配器 / 输出 | 能枚举到几个适配器、每个有几个可见输出 |
| 3 | D3D11 设备 | 硬件 / WARP 分别试，报 featureLevel 与 HRESULT |
| 4 | **Desktop Duplication** | 逐适配器逐输出尝试 `DuplicateOutput`，失败时报**每个输出的精确 HR** |
| 5 | 采集耗时 | 桌面复制 vs BitBlt，各 60 帧，含 p95 / 最大 |
| 6 | H.264 编码器枚举 | 用 `MFT_ENUM_FLAG_HARDWARE` **单独**枚举硬件 MFT |
| 7 | 编码实测 | GDI+ JPEG q75 基线 + MF H.264，含体积自校验 |
| 8 | 60fps 判定 | 采集 + 编码相加，对 16.7ms 预算 |

---

## ★ RDP 复测（必读）

本程序**第一次运行是在本地控制台会话**（`SM_REMOTESESSION=0`），
所以它的结论**不能**代表「代理跑在 RDP 会话里」这个真实场景。必须复测。

### 方法 A：在 RDP 会话里手动跑（最直接）

```powershell
# 1) 用目标账号（例如 A2）RDP 登入这台机器
# 2) 在该 RDP 会话的桌面里打开 PowerShell，cd 到 tools\media-probe
# 3) 跑
.\bin\Release\net11.0-windows\media-probe.exe

# 4) 确认输出第一行是：
#    [RDP     ] SM_REMOTESESSION   运行在 RDP 会话内 → 结论可直接用于目标场景
#    如果不是 RDP，说明你还在控制台会话，结论仍然不算数
```

### 方法 B：通过计划任务投进目标会话（无人值守）

> ⚠ 以下命令会创建计划任务。它**不在本 Spike 的只读范围内**，是给实施阶段的命令备份，
> 执行前请自行确认。本程序自身不需要也不应被这样调度。

```powershell
# 假设目标会话是 18（先把 media-probe 整个目录复制到该会话可见的路径）
schtasks /Create /TN "misprobe" /TR "C:\path\media-probe.exe -Out C:\Temp\media-probe-rdp.md" `
         /SC ONCE /ST 23:59 /RU <账号> /IT /F
schtasks /Run /TN "misprobe"
```

### 复测时要盯的四个点

| 项 | 期望（若桌面复制在 RDP 下可用） | 若不可用会看到 |
|---|---|---|
| `SM_REMOTESESSION` | `[RDP]` | 还是 `LOCAL` → 你跑错会话了 |
| 适配器输出数量 | ≥1 | `无可见输出` |
| `DuplicateOutput` | `OK ... succeeded` | `hr=0x887A0004 DXGI_ERROR_UNSUPPORTED` |
| 桌面复制单帧耗时 | 个位数 ms 以内 | `FAIL 一次新帧都没拿到` |

**如果 RDP 下返回 `0x887A0004`**，结论就是：这条路线在目标场景**不可用**，
`STREAM-PERF-AUDIT` §4.5(e) 的 P2 方案要降级或放弃（改为继续优化 BitBlt 路径）。
这正是当初标注「需实测」的原因。

---

## 这个程序踩过的坑（改代码前必读）

都是**真实发生并修正**过的，记录下来省下一次重踩：

### 1. `MFT_ENUM_FLAG_SYNCMFT|ASYNCMFT` 看不到硬件编码器

第一版这样枚举，结果是「0 个硬件 MFT」——而本机明明有 RTX 4060。
**硬件 MFT 必须用 `MFT_ENUM_FLAG_HARDWARE` 单独枚举。**
修正后正确列出 `NVIDIA H.264 Encoder MFT`。

### 2. `IMFSample` 的 vtable 前面有 33 个 `IMFAttributes` 槽位

只声明 `IMFSample` 自己的方法 → `SetSampleTime` 打到错误槽位 → **`0xC0000005` 崩溃**。
写成 `interface IMFSample : IMFAttributes` **仍然崩**。
最终方案：把 `IMFAttributes` 的 33 个方法在 `IMFSample` 里**显式重复声明一遍**。

### 3. 异步 MFT：`WriteSample` 耗时 ≠ 编码耗时

NVIDIA 的 MFT 是异步的，`WriteSample` 只入队就返回。
第一版据此算出「**0.020ms/帧、比 JPEG 快 236 倍**」——纯假数据。
现在用「写入 + `Finalize` 排空」的端到端口径，并明确标注它是**CPU 侧下界**。

### 4. 编码体积自校验

40 帧 1080p H.264 的合理下限约 **300KB**。第一版实测只有 150KB，原因是
NV12 填充过于平坦（纯渐变），编码器把整帧压成极少字节 —— 于是「编码很快」。
现在填入拟真桌面内容（色带 + 文本纹理 + 确定性噪声），并把 <300KB 判为
**不可信**、拒绝输出排名数字。

### 5. `SM_CXSCREEN` 不等于真实桌面像素尺寸

本机 DPI 150%，`SM_CXSCREEN` 返回 **1707x960**（逻辑尺寸），
而实际桌面是 **2560x1440**。用错尺寸测 BitBlt 会少拷一半像素，
耗时被系统性低估（15.99ms → 实际 18.5~19.8ms）。改用 `EnumDisplaySettings`。

### 6. `GetFrameDirtyRects` 的 size 参数是「缓冲区字节数」

不是「元数据总大小」。传错会算出恒 **100%** 的脏矩形占比。
正确写法：`dirtyRectsBufferSize = rects.Length * 16`。

### 7. `GetExtendedTcpTable` 式的 122 —— 本例中是 Vortice 的行为差异

`DuplicateOutput` 在 Vortice 里**成功返回接口、失败抛 `SharpGenException`**，
真实 HRESULT 在 `ex.ResultCode.Code` 里。要拿它必须 catch，否则看不到 `0x887A0004`。

---

## 依赖

- .NET 11 SDK（与主项目 `net11.0-windows` 一致）
- NuGet：`Vortice.DXGI` / `Vortice.Direct3D11` **3.6.2**

> 为什么用 Vortice 而不是手写 COM vtable：
> DXGI 的 COM 接口层次深（`IDXGIFactory1`/`IDXGIAdapter`/`IDXGIOutput`/`IDXGIOutput1`/
> `IDXGIOutputDuplication`），手写 vtable 极容易错位，而错位的表现是**崩溃或假阴性**。
> 上面第 2 条坑就是活例子。用成熟互操作库能拿到精确 HRESULT，代价只是两个包引用。

## 清理

```powershell
# 用完清掉构建产物（AGENTS.md 工作区整洁规则第 1、6 条）
Remove-Item -Recurse -Force .\bin, .\obj
```
