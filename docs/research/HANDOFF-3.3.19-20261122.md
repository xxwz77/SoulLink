# 交接文档 · 3.3.19（网页直连代理 + 按需取数 + 性能修复）

> 日期：2026-11-22
> 版本：**3.3.19**（`SoulLink.csproj`）
> 提交：`81a8f89`（已推送 origin/main）
> 上一版：3.3.4（`9a04b0c`）

---

## 0. 一句话总结

网页的画面/按键**直连代理**，主程序只出数据不出流量；所有两端交互改成
**按需触发**（无任何周期轮询）；顺带修掉三个真实卡顿根因和一个光标缺失问题。

---

## 1. 这一版解决了什么

### 1.1 网页直连代理（核心）

用户要求：「网页端尽量隔离主客户端」「模拟按键也有独立出来」。

```
改前：
  浏览器 → 主程序 → 命名管道 → 代理（截帧）→ 写盘 → 主程序读回 → 浏览器
         ↑ 每一帧都要主程序搬运，视频 Range 请求也全是主程序在搬字节

改后：
  浏览器 → 代理 :1080N  /live        直取内存缓冲（零落盘、零轮询）
                      /api/input    本会话内注入键鼠
                      /api/info     身份回显
  主程序 → /api/sessions 只返回「账号 → 端口」映射表
```

**端口规则**（`Services/WebDirect.cs`）：

```
端口 = WebPort + 账号在 App.Config.Accounts 里的序号 + 1
```

★ 按**账号**分配而不是会话号：会话号会变（注销重登就换，实测 A2 从 17 变过），
   按它分配会导致网页刷新后端口乱跳。

### 1.2 按需取数（用户硬性要求）

> 「两端交互需要 api 按需触发，不要持续不断，否则拖垮性能」

| 数据 | 改前 | 改后 |
|---|---|---|
| 实时画面 | `setInterval` 固定节奏轮询 | `setTimeout` 串行调度 |
| 录像列表 | 打开画面就扫盘 | 点了「录像回放」才取，30 秒缓存 |
| 设置 | — | 只在用户改动时 POST |

实时画面的四条保护：

1. **串行**：上一帧没回来，绝不发下一帧（请求永不堆积）
2. **失败退避**：1s → 2s → 4s → 8s
3. **静止降频**：连续 6 帧字节数不变 → 降到 1fps，一有变化立刻恢复
4. **停止条件**：暂停 / 页面隐藏 / 换账号（`frameGen` 丢弃在途）

### 1.3 鼠标光标

`BitBlt` 抓的是**屏幕像素**，而光标由 DWM 作为独立图层合成，**不在结果里**，
所以录像和实时画面永远看不到鼠标。RDP 客户端能看到是因为它走另一条通道。

修法（`Relay/SessionRecorder.cs`）：`BitBlt` 之后、JPEG 编码之前，
用 `GetCursorInfo` + `DrawIconEx` 叠加。三个必须处理的细节：

| 细节 | 不处理的后果 |
|---|---|
| `GetIconInfo` 的 `xHotspot/yHotspot` | 光标整体偏移，箭头尖端不在真正位置 |
| 释放 `hbmMask`/`hbmColor` | 每帧泄漏 2 个 GDI 句柄，几分钟耗尽 |
| DPI 换算 | 进程已 `SetProcessDPIAware`，坐标与 `BitBlt` 一致，**再算就错** |

前端同时修了「必须按住才发 move」：`pointermove` 首行 `if (!drag) return;`
把悬停移动全丢弃了。现在悬停即跟随。

### 1.4 网页设置双向同步

录制三项（帧率/画质/保留）进浏览器顶栏，改后：

```
写 config.json → 立即下发给代理（agentsUpdated）→ App.SettingsChanged
                                                    → 界面刷新（不用切页）
```

★ 另设独立的「本机播放·取图」：只存 `localStorage`，**不写配置**。
  否则「我看得慢一点」会让所有观看者一起变慢。

### 1.5 性能修复

实测基线（详见 `docs/research/PERF-3.3.4-20261122.md`）：
空闲挂机态 **约 16% 单核**，其中约 12% 是 UI 线程 + WPF 渲染线程。

| 修复 | 位置 |
|---|---|
| `Kick()` 移出 UI 线程（四个调用点全在 UI 线程 → 连接/投送时冻结） | `Services/AccountStateCenter.cs` |
| 日志改增量追加，内容没变零开销（原来每秒整窗文本重排） | `Views/LaunchPage.xaml.cs` + `Services/LogTailAppend.cs` |
| `Discover` TTL 1s→3s + 枚举双检锁串行化 | `Services/BgiLogWatcher.cs` |
| 鼠标钩子复用事件对象（原来每次 new） | `Services/LowLevelMouseHook.cs` |
| 不再往 error.log 塞假的 `InvalidOperationException` | `App.xaml.cs` |

---

## 2. 踩过的坑（**改之前务必读**）

### 2.1 端口不能共享 —— 实测

两个进程绑同一端口时，第二个**绑定"成功"但一个连接都收不到**：

```
job1 (先绑)  → served=12
job2 (后绑)  → served=0      ← 最危险的失败模式：日志无异常，表现为"时好时坏"
```

反证（端口确实独占）：

```
PLAIN BIND FAILED: 通常每个套接字地址(协议/网络地址/端口)只允许使用一次
```

所以架构上放弃「多代理共享端口」，改成一账号一端口。

### 2.2 历史回放**不该**走代理

我最初把 `/file/` 也做到代理里，是错的（已删除）。

判断依据只有一句：**数据是不是只有代理能拿到？**

| 数据 | 在哪 | 谁服务 |
|---|---|---|
| 实时帧 | 只有代理内存里 | 代理 `/live` |
| 键鼠注入 | 必须在该会话执行 | 代理 `/api/input` |
| 历史 MP4 / 截图 | 磁盘文件 | **主程序** `/file/` |

代理和主程序在同一台机器、读同一块硬盘，让代理服务这些文件是纯多余的一跳，
只给提权进程增加攻击面。**不需要防护的最好办法是不要那个功能。**

### 2.3 POST 白名单会挡住新接口

```csharp
if (method == "POST") {
    if (path == "/api/input") { ... }
    await WriteText(ctx, 404, "404");   // ← 其它 POST 全在这里被挡
}
```

加 `/api/settings` 时排查了很久，一度怀疑「跑的是旧二进制」。
加新 POST 接口一定要同步改这里。

### 2.4 `<img>` 无法携带自定义请求头

代理只认 `X-SoulLink-Token` 时，网页的实时画面会一直 401（页面有框无图）。
现在 `CheckToken` **两种都接受**：请求头（fetch 用）+ `?token=`（img 用）。

★ 用 `Invoke-WebRequest -Headers` 测试时不会暴露这个问题 —— 那测的是 API 能力，
  不是浏览器行为。

### 2.5 一行重复 `const` 让整页失效

```js
const vc = ...;
const vc = ...;   // SyntaxError → 整个 <script> 一行都不执行
```

表现：账号列表空、主题不跟随、按钮全点不动，**只有静态 HTML 骨架可见**。
这和 `HANDOFF-3.3.3` §6.3 记录的是同一类事故。

**现在的做法**：`scripts/sync-webpages.ps1` 同步前会做元素引用检查，
改页面后至少跑一次 headless 浏览器验证。

### 2.6 `_loading` 标志会把设置锁死

```csharp
_loading = true;
...            // 中途抛异常
_loading = false;   // ← 永远执行不到
```

卡在 `true` 之后，用户改任何设置都被 `if (_loading) return;` **静默忽略**。
现在用 `try/finally` 保证复位。

### 2.7 `artifacts/` 没排除编译

NativeAOT 试验留下的 `artifacts/aot-test/obj/*.AssemblyInfo.cs` 被主项目
通配符扫进去 → `CS0579 特性重复`。已在 `DefaultItemExcludes` 补 `artifacts\**`，
与 `backups\**` 是同一类坑。

---

## 3. 当前状态

| 项 | 值 |
|---|---|
| 版本 | **3.3.19** |
| 提交 | `81a8f89`（已推送） |
| `dist\SoulLink.exe` | v3.3.19 |
| `C:\Program Files\SoulLinkDev\SoulLink.exe` | v3.3.19（Broker 服务） |
| `C:\Program Files\SoulLinkDev\SoulLink.Agent.exe` | v3.3.19 |
| 网页端口 | 18080（主程序）/ 18081+（各账号代理） |
| 工作区 | 472 MB（健康标准 < 1 GB） |

**当前录制配置**：30fps / 质量 75 / 保留 10 秒，录制账号 `acc_a2`。

> ⚠ 这三个值在我测试期间被误改成过 `5/75/10`、`15/60/60`。
> 原值备份：`dist\data\config.json.bak-before-recordfix`。

---

## 4. 已知问题与未完成

### 4.1 「太卡」未根治

用户反馈过卡顿。当前配置 30fps + 质量 75 ≈ **每秒 4.8 MB**，
浏览器持续解码这个量会吃满一个核。

已经给了用户两个开关（都会立即生效，不用重启）：
- 网页顶栏「录制」组调低帧率/画质
- 网页顶栏「本机播放·取图」单独降频（只影响自己）

**没有做**：WebP 替代 JPEG、WebRTC、帧差编码。

### 4.2 光标跟随受录制帧率限制

光标画在画面帧里，所以更新频率 = `RecordFps`：

```
30fps → 光标每秒动 30 次（流畅）
5fps  → 光标每秒只动 5 次（明显一顿一顿）
```

想低帧率下也顺滑，得让网页自己渲染光标（用 move 事件位置画 CSS 光标）。

### 4.3 应用界面同步依赖事件

`App.SettingsChanged` 在构造函数里订阅、`Unloaded` 时退订。
如果发现「网页改了应用里不变」，先确认面板是不是 `RdpGeneralSettings`
（另一个可能是 `ConsolePage` 里还有别处显示这些值）。

### 4.4 CPU 的 WPF 渲染部分

`RenderRecursive` / `PrecomputeRecursive` 占大头，根因仍未解决。
`docs/research/PERF-3.3.4-20261122.md` 有完整的采样方法，接着查就行。

---

## 5. 网页开发流程（重要）

**页面现在是运行时读磁盘**，改样式不用重编译、不用重启：

```
1. 改 docs\web-index.html
2. 跑 scripts\sync-webpages.ps1   （生成 WebPages.cs + 复制到 dist\）
3. 刷新浏览器
```

主程序优先读 `exe 同目录\web-index.html`，读不到才用内置常量兜底。

> 为什么这么做：原来页面编译进 exe，改一个 CSS 都要重编译 + 重启，
> 而重启会**断开所有 RDP 会话**。用户明确抱怨过这一点。

**改完必须验证**，别只看代码：

```powershell
# 元素引用检查（防止 $('id') 指向不存在的元素）
# 用 headless 浏览器真渲染一遍，看 console 有没有报错
$edge --headless=new --dump-dom "http://127.0.0.1:18080/"
```

---

## 6. 给继任者的提醒

### 6.1 绝对不要做的事

1. **不要用 `write` 覆盖大文件** —— 用 `edit` 增量修改。
2. **不要动 `dist/stable/`**，除非用户明确授权。
3. **不要用 A1 / A4 账号**，除非用户明确授权。
4. **不要拿用户的生产 `config.json` 当测试环境** —— 我做这件事时把用户的
   录制设置改成了测试值，是明确的错误。要测写入请用 `SOULLINK_DATA_DIR`
   指向独立目录。
5. **不要在 UI 线程上做文件 IO / 进程枚举**。

### 6.2 布局调试的正确顺序

这次为了验证一个 CSS 布局，我试了 Python stub（3 次失败）、
PowerShell HttpListener（2 次）、注入错误捕获（失败）、嵌入 base64（失败），
最后才用最简单的静态服务器跑通。**效率很低。**

正确做法：先用「把测试图直接塞进页面」这种最简单的办法，不要一开始就
模拟整个 API。

### 6.3 看到两边数据不一致时，先问「这个值是谁写的」

「应用显示 30fps、网页显示 5fps」那次，我第一反应是查前端渲染逻辑，
绕了很久。**正确做法是查 `config.json` 的 mtime**，一看就知道是
我刚调过的测试值。

### 6.4 测量 CPU 必须交叉验证

我第一次采样得到「279% 单核」是**假数据**（`Get-Process` 缺 `$p.Refresh()`，
两个采样点落在同一时刻）。正确写法：

```powershell
function Sample([int]$procId, $ms) {
  $p = Get-Process -Id $procId
  $p.Refresh(); $a = $p.TotalProcessorTime.TotalMilliseconds
  Start-Sleep -Milliseconds $ms
  $p.Refresh(); $b = $p.TotalProcessorTime.TotalMilliseconds
  return ($b - $a) / $ms * 100
}
```

**必须**拿「进程总 CPU / 存活时长」当平均基线交叉验证。

---

## 7. 下一步建议

**优先级从高到低**：

1. **发版** —— 版本已 3.3.19，但没走 `publish-release.ps1 -Full -Push`。
   注意 `dist/stable/` 仍是 3.3.3。
2. **实测卡顿是否解决** —— 用户配 30fps，让他用网页开关降到 15fps 对比。
3. **WPF 渲染那部分 CPU** —— `docs/research/PERF-3.3.4` 有采样方法。
4. 低帧率下的光标平滑（网页自绘光标）。
5. WebP 替代 JPEG（同画质体积约减 30%）。

---

## 8. 相关文档

- `docs/research/PERF-3.3.4-20261122.md` —— 性能基线、采样方法、三个卡顿根因
- `docs/research/WEB-DIRECT-3.3.5-20261122.md` —— 直连架构、端口独占实测、路径穿越防护
- `docs/RELEASE-RULES.md` —— 发布流程
- `AGENTS.md` —— 性能约束与工作区整洁规则

## 9. 回归测试

```powershell
dotnet run --project tools\layout-check\LogTailAppendCheck.csproj -c Release   # 12 用例
dotnet run --project tools\layout-check\WebDirectCheck.csproj -c Release       # 17 用例
```

两者都应输出 `ALL PASSED`。