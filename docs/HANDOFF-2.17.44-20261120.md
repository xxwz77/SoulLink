# SoulLink 交接文档 · 2.17.44（撤销"自动改写用户配置地址"）

> 时间：2026-11-20 21:40
> 版本：**2.17.44.0**（待定版；可信归档仍是 v2.17.39）
> 性质：**撤销 2.17.43 的越界改动**，并记录一条必须遵守的边界

---

## 1. 用户指出的问题（原文）

> 「你不能修改我的配置地址啊，它会涉及别的服务，填多少就是多少」

## 2. 我做了什么错事

2.17.43 我在 `Controls/RdpConsole.cs` 里加了"按实时会话号纠正地址"：

```csharp
if (sessionId > 0 && sessionId < 255)
{
    host = "127.0.0." + sessionId;
    if (!string.Equals(account.RdpHost, host, StringComparison.Ordinal))
    {
        account.RdpHost = host;          // ← 擅自改写用户配置
        ConfigStore.Save(App.Config);    // ← 还立即落盘
    }
}
```

**实际造成的后果**（已核实并恢复）：

| 账号 | 用户填的 | 被改成 |
|---|---|---|
| A2 | `127.0.0.3` | `127.0.0.17` |
| A3 | `127.0.0.4` | `127.0.0.19` |

## 3. 为什么这是错的（必须记住）

`RdpHost` **不只是"内嵌 RDP 连哪"这一个用途**：

* 它可能被其它服务 / 流程 / 脚本引用；
* 用户是有意填某个值的（比如固定指向某个会话做特殊用途）；
* 程序**无权**判断"哪个地址才是对的"，更没有权力回写。

**边界结论：程序只读配置，不改配置。** 地址对不对由用户负责；
程序能做的是"连接结果"上的判断（连错了就报错），而不是偷偷改参数。

## 4. 修复

### 4.1 撤销自动纠正（`Controls/RdpConsole.cs`）

恢复到 2.17.42 及之前的行为：

```csharp
// 配置有地址 → 就用它，原样使用，不纠正、不回写
// 配置为空   → 才去探测（RdpHostProbe）
bool autoHost = string.IsNullOrWhiteSpace(account.RdpHost);
if (autoHost) probed = await RdpHostProbe.ProbeAsync(sessionId, null);
string host = account.ResolveHost(sessionId, probed);
```

同时删掉了 2.17.43 新增的 `ResolveLiveSessionIdAsync`（它也只服务于那个错误方案）。

### 4.2 恢复用户配置

* 备份当前：`config.json.bak-before-restore-<HHmmss>`
* A2 → `127.0.0.3`、A3 → `127.0.0.4`（用户原值）
* 重启后确认：**配置仍是用户填的值，程序未改动**

## 5. 顺带查清的另一件事：黑屏的真正原因

排查过程中确认了"重启后拉不起会话"的真实原因 —— **不是配置地址问题**：

```
C:\Program Files\RDP Wrapper\RDPWInst.exe   → 不存在
C:\Windows\System32\rdpwrap.dll            → 不存在
当前 RDP 会话数                              → 只有 2 个（A1 会话2、A4 会话3）
A2/A3 再连 → 超出 Windows 专业版单会话限制
          → RDP 被分配到临时会话 25/27（只有 csrss+ChsIME，2~4 个进程）
          → 客户端 Connected=1 报"连接成功"，但画面全黑
```

**结论：需要安装 RDP Wrapper**（SoulLink 的 `Assets\RdpWrap\` 里 4 个 DLL 已内嵌进 exe，
但从未安装到系统）。装好并重启 TermService 后，A1~A4 可以同时在线。

> 注意：安装 RDP Wrapper 会**断开所有 RDP 会话几秒**（重启 TermService 的必然代价）。
> 主控是 console 会话（lummy），不受影响。

## 6. 产物

| 项目 | 值 |
|---|---|
| 版本 | 2.17.44.0 |
| `dist/SoulLink.exe` | 67,488,105 字节 · SHA256 `B43E99EF0FACFA60C6B9D0EAC4456E5393A59FDCC72001DB1E57317CC77D637C` |
| 配置 | **已恢复为用户原值**（A2=127.0.0.3、A3=127.0.0.4） |
| 可信归档 | `v2.17.39-reliable` |

## 7. 待办

1. **安装 RDP Wrapper**（才是"多账号同时在线"的真正前提），需用户确认后执行。
2. 2.17.40/41/43/44 均未做可信归档。
3. 教训入册：**程序不得擅自改写用户配置**，尤其当该字段可能被其它服务引用时。
