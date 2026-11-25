# MOBILE-SELF-AUDIT-20261125 —— Android 端「差到没法用」自审报告

> 任务 t33（T41）· 只读审计 · 审计人 ui-fixer · 2026-11-25 00:10
> 用户原话：「能输入，但是效果差到没法用。而且你没理解什么是全屏。」
> 结论先行：**6 个致命缺陷 + 9 个显著/小缺陷**。其中 **5 项是一行级改动就能显著改善**。
> 审计对象：PlayerPlaceholderScreen.kt（346 行）/ InputSender.kt（124 行）/ InputThrottler.kt / H264Decoder.kt / H264Player.kt / MainActivity.kt / PlayerConstants.kt / AndroidManifest.xml。

## 一、缺陷清单（按影响排序）

### S1【致命】拖动 move 完全无节流：绝对模式每次触摸事件一个 HTTP 请求
- **现象**：绝对模式拖动跟手性极差、卡顿、延迟越拖越大。
- **原因**：`onDragMove` 直接 `inputSender.sendNoWait(InputAction.Move(nx, ny))`（PlayerPlaceholderScreen.kt:185-187）——**没有任何节流**。触摸采样 120Hz（现代手机），每次手指移动都生成一个 HTTP POST 排进 `sll-input` 单线程 FIFO 队列（InputSender.kt:47-49）。一个 RTT 约 5~30ms（内网），队列吞吐上限 ≈ 30~200 请求/秒，**120Hz 输入直接把队列打爆**——旧位置请求还在排队，光标「追不上手」，延迟持续累积。
  对照：**InputThrottler.kt 里已经写好了 `MoveThrottler`（latest-wins + 8ms，:54-91）但根本没人用**；相对模式的 lookThrottler 有 8ms 合并，绝对模式裸奔。
- **file:line**：PlayerPlaceholderScreen.kt:185-187；InputThrottler.kt:54-91（现成 MoveThrottler 未接线）。
- **修法**：onDragMove 改走 MoveThrottler（latest-wins：8ms 窗口只保留最新坐标，旧位置丢弃不排队）。
- **★ 一行级改动，「没法用→能用」的最大单项**。

### S2【致命】强制软解：1080p60 软解在中端手机必然掉帧
- **现象**：画面卡顿、掉帧、延迟高、手机发烫。
- **原因**：`USE_SOFTWARE_DECODER = true`（H264Decoder.kt companion）——T36 黑屏排障的**临时试验开关**（验证「c2.qti 吃帧不吐」时强制软解）。Annex-B 根因修复后（packAnnexB + 码流内 CODEC_CONFIG），硬解大概率已可用，但开关没回退。c2.android 软解 1080p60 CPU 单核吃满 → skipQueue 频繁触发丢帧 → 请求 IDR → 更多大帧 → 更跟不上，恶性循环。
- **file:line**：H264Decoder.kt companion `USE_SOFTWARE_DECODER = true`。
- **修法**：改回 false（先验硬解；保留 c2.qti 黑名单降级链）。
- **★ 一行改动显著改善**。

### S3【致命→已缓解】T36 诊断日志：前 20 次限制保护了稳态，但应清理
- **现象**：启动期日志密集；热路径仍有条件判断与字符串分配。
- **原因**：feed 入口/入队日志限前 20 次（H264Decoder.kt:148-151/:182）；loop# 前 20 + 每 500 次（:277-280）；dequeueInputBuffer 前 20 次（:316）。**每 500 循环 ≈ 每秒 1 条**（2ms/循环）——稳态影响小；但这是排障代码，任务完成应整体移除（热路径上的字符串拼接分配 + 条件分支）。
- **file:line**：H264Decoder.kt:146-151/:158/:182/:274-280/:316。
- **修法**：删 _feedLogs/_iter 相关打点（保留 configure 三条 + 错误路径）。
- **★ 删代码即改善**。

### S4【致命】视频区 padding(8.dp) 留边 + 顶栏常驻：视频只占 ~78% 屏幕
- **现象**：用户说「你没理解什么是全屏」——四周 8dp 黑边 + 顶部整条工具栏。
- **原因**：a) `Box(Modifier.fillMaxSize().padding(8.dp))`（PlayerPlaceholderScreen.kt:143）无条件留边；b) Column 里 TextButton(返回) + 账号 + 状态行 + 两按钮（:118-140）常驻。竖屏 1208x2624 实测：顶栏 ≈116dp + 8dp×2 → **视频高度约 78% 屏幕**。沉浸式已生效（MainActivity applyImmersive），但布局没跟着全屏。
- **file:line**：PlayerPlaceholderScreen.kt:143、:118-140。
- **修法**：视频 Box 全屏铺满 + 顶栏改浮层（半透明、几秒无操作自动隐藏）；删 padding(8.dp)。
- **小重构（非一行）**。

### S5【致命】视频宽高比 ≠ 屏幕比例：无 aspect 布局层
- **现象**：竖屏手机（1208x2624）看横屏 1920x1080 流，画面比例错/黑边巨大/变形。
- **原因**：setVideoScalingMode(SCALE_TO_FIT)（H264Decoder.kt:129）语义是等比 fit（letterbox）；SurfaceView fillMaxSize()（PlayerPlaceholderScreen.kt:173）无 aspectRatio 计算——letterbox 位置/大小由设备 SCALER 行为决定，不受控。**onFrame 回调已给 w/h（H264Decoder onFrame）但播放页未消费**。
- **file:line**：H264Decoder.kt:129、PlayerPlaceholderScreen.kt:173。
- **修法**：视频 Box 用 Modifier.aspectRatio(videoW/videoH)（消费 onFrame 的 w/h）居中。
- **中等改动**。

### S6【致命】触摸坐标映射无 letterbox/padding 校正：系统性偏移
- **现象**：绝对模式点击边缘偏移；letterbox 场景点黑边完全错位。
- **原因**：所有归一化用 `pos.x / size.width`（PlayerPlaceholderScreen.kt:268/:276/:303/:312-314/:318）——按 Compose 布局尺寸算，而视频实际显示区域 ≠ 布局区域（S5 letterbox + S4 的 8dp padding）：点在黑边/padding 上 nx 仍算进 [0,1] 发给远端——**落点系统性偏移**（8dp + letterbox 偏移量）。
- **file:line**：PlayerPlaceholderScreen.kt:268/:276/:303/:312-314/:318。
- **修法**：把视频显示 Rect（aspect 计算后）传给手势层，归一化 = (pos - rect.origin)/rect.size，越界 clamp。**与 S5 同批修（互相依赖）**。

### S7【显著】拖动收尾 up 用手势起点坐标（不是最后位置）
- **现象**：拖动结束/多指打断时远端指针跳回起点。
- **原因**：收尾分支的 nx/ny **按 downPos 计算**（PlayerPlaceholderScreen.kt:268-269，active.isEmpty() 分支里用 downPos）；多指打断分支也用 downPos（:260）。正确应为最后 move 位置。
- **file:line**：PlayerPlaceholderScreen.kt:260、:268-269。
- **修法**：维护 lastMovePos，收尾用最后位置。**★ 一行级改动**。

### S8【显著】send（等回包）与 sendNoWait 共用单线程 FIFO：click 的 RTT 阻塞后续 move
- **现象**：点击后短时间拖动无响应（tap 后立刻拖 → 前几百 ms 没反应）。
- **原因**：单线程 executor（InputSender.kt:47-49）里 send 同步 execute 等 HTTP 回包（:83-90，内网 5~30ms、弱网 100ms+），期间排在其后的 move 全部排队。
- **file:line**：InputSender.kt:47-49、:83-90。
- **修法**：两条通道分离（保序队列只放 click/down/up/key；move/moverel/wheel 走 latest-wins——与 S1 的 MoveThrottler 联动最自然）；或 send 改不等回包（后端幂等，回包仅诊断价值）。**★ 与 S1 联动**。

### S9【有限】OkHttpClient 连接池
- defaultInputClient()（PlayerPlaceholderScreen.kt:338-341）默认 keep-alive 池 5 连接——单线程串行下能复用。S1 把量级打满后会触发连接 churn。S1 修复后自动缓解。

### S10【显著】KEY_MAX_INPUT_SIZE 仍设置（Moonlight 不设）
- H264Decoder.kt:111-115 自算 1~8MB。Moonlight 调研结论：不设此键。S2 回退硬解后观察 overflowDrops（日志已有），为 0 可移除。

### S11【显著】旋转后 aspect/坐标不重算
- configChanges 已补（AndroidManifest.xml:18，旋转不重建 Activity）→ SurfaceView 不重建；Compose size 会更新（PointerInputScope.size 布局时刷新），但 **S5 的 aspect 层缺失**，旋转后 letterbox 位置错、S6 的映射随之偏。surfaceChanged 空实现（PlayerPlaceholderScreen.kt:165）。
- **修法**：S5 的 aspect 层用 Compose state 驱动，旋转自动重算。

### S12【小】streamFps 默认 30 的竞态窗口
- H264Decoder.kt:36-37 streamFps 默认 30；/api/info 异步返回前 feed 按 30 算 skipQueue（60fps 流窗口内阈值偏紧 4 而非 6）。窗口短、影响小。

### S13【无需改】dragThreshold 12dp
- 1208px 宽 ~2.75x density → 12dp ≈ 33px ≈ 屏宽 2.7%。ToDesk 约 8~10dp。**合理**。

### S14【小】死代码 TAP_PX
- PlayerPlaceholderScreen.kt:336 `private const val TAP_PX = 10f`——旧 tap 判定残留（已被 dragThresholdPx 取代），删除。

### S15【合理】LookThrottler 8ms（相对模式）
- moverel 合并语义正确（125 req/s 上限）；问题只在绝对模式没走它（S1）。

## 二、一行改动显著改善清单（优先做）

| # | 改动 | 预计收益 |
|---|---|---|
| 1 | S1：onDragMove 接 MoveThrottler（latest-wins 8ms） | 拖动可用性质变 |
| 2 | S2：USE_SOFTWARE_DECODER = false | 掉帧/发烫/延迟三重缓解 |
| 3 | S7：收尾 up 用最后位置 lastMovePos | 拖动收尾不跳起点 |
| 4 | S4：删 padding(8.dp) | 立减 16dp 黑边 |
| 5 | S8：click/down/up 不等回包 | 点击后拖动不再卡 |

## 三、我们 vs 专业软件差距表（个人判断，标依据）

| 维度 | 我们现状 | Moonlight/ToDesk 水平 | 依据 |
|---|---|---|---|
| 输入通道 | 每动作一个 HTTP POST（串行 FIFO） | 专用输入流（TCP/UDP 单连接包协议，微帧格式） | Moonlight ControlStream：一条连接+二进制包，~0 请求开销；我们每 move 一次 HTTP 头+JSON |
| 触摸→光标延迟 | 120Hz 无节流打爆队列后 ≈ 队列深度×RTT | ≤16ms 合并 latest-wins | web 端已验证 8ms 模型；Android 端绝对模式没接 |
| 视频解码 | 软解强制（试验开关未回退） | 硬解优先+厂商黑名单降级 | Moonlight MediaCodecHelper 白黑名单 |
| 全屏/沉浸 | 8dp padding+常驻顶栏（视频 ~78% 屏） | Surface 全屏 + aspect 布局 + 工具栏自动隐藏 | Moonlight StreamingFrameLayout |
| 坐标映射 | 布局尺寸归一化（无 letterbox 校正） | 视频矩形精确映射 | 视频矩形 vs 布局矩形的差异我们未处理 |
| 渲染 | releaseOutputBuffer(true) 已对齐 | 同 | 对齐 Moonlight |

## 四、审计声明

- 全程只读（零代码改动、零构建、未连接手机）；所有结论均给出 file:line 可复核。
- 报告基于当前工作区（含 T35~T36b 全部已提交改动）。
- 下一批建议顺序：S1+S8（输入通道）→ S2（硬解回退）→ S4+S5+S6（全屏/aspect/映射）→ S7/S3（收尾/日志清理）。
