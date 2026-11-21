# SoulLink 2.10.2 变更

日期：2026-11-19
主线：手动代理侧键终于可用

## 实测证据（A2 · 会话 6）

```
代理 00:47:06.160  目标会话鼠标热键命中=鼠标侧键4
代理 00:47:06.170  热键已写入事件管道 value=toggle
宿主 00:47:06.168  事件管道收到热键 · 会话 6 · toggle · 订阅者=1
宿主 00:47:06.168  收到目标会话热键事件 · connected=True input=True captured=False
宿主 00:47:06.169  执行热键切换 · guard=0
宿主 00:47:06.169  热键触发: target=FreeLook
宿主 00:47:06.181  进入自由视角 中心=1337,768 焦点=True
宿主 00:47:06.181  热键切换结果 ok=True captured=True mode=FreeLook
```

## 三个叠加的成因

### 1. 手动通道从不挂接（2.10.1 已修）

ListenAsync 只建管道，没有把 RelayClient 挂到 RdpConsole，
热键配置从未下发，代理侧缺「目标会话鼠标热键安装=True」。

### 2. 热键判据要求整个窗口激活（本版修）

```csharp
// 旧：只要焦点在主窗口其它区域就丢弃
if (_ownerWindow == null || !_ownerWindow.IsActive) return false;

// 新：以 RDP 画面控件焦点为准
return RdpSurfaceHasFocus;
```

RdpSurfaceHasFocus 同时用 AxHost.ContainsFocus 和 Win32
GetFocus() / IsChild() 判断——后者是必需的，
因为 RDP ActiveX 的焦点常落在它创建的 IHWindowClass 子窗口上。

### 3. 日志白名单关键词与文案不一致（本版修，最隐蔽）

判据文案从「丢弃非当前控制台热键事件」改成「丢弃热键事件 · 原因=…」后，
IsImportant 白名单还在匹配旧字符串，于是这条唯一能说明原因的
日志被静默丢弃。表现就是日志里什么都没有，
无法区分事件没到还是被拦了。

修复：白名单改用稳定前缀「丢弃热键事件」，并新增 LogFilterCheck2
逐条断言热键链路日志都能落盘。

## 其他

- 主用户不再建管道：StartForOnlineAccountsAsync 改走 AccountScope，
  StartWaitingAsync 入口二次校验。实测会话 1 已无 SoulLink_Relay_1_*。
- 事件管道全链路可观测：入队、写入、收到、读结束、异常、挂接。

## 新增回归工具

| 工具 | 作用 |
|------|------|
| ManualHotkeyCheck | 手动通道挂接 + 热键配置下发 + 钩子安装 |
| HotkeyFocusCheck | 判据改为控件焦点、Win32 兜底、退出无条件放行 |
| EventFlowCheck | 模拟代理写事件，验证宿主能收到（协议层隔离） |
| LogFilterCheck2 | 热键链路每条日志都能落盘 |

## 未解决

- A3（会话 7）尚未复验。本版只在 A2 实测成功。
- 使用前需先点一下 RDP 画面（有意设计：控件有焦点才响应侧键）。
- UAC 开启的 linked token 分支、冷启动时间窗口、锁屏与会话切换未回归。