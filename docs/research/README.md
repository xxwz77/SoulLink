# 研究文档索引

> 归档时间：2026-11-21
> 原分散在 `releases/`、项目根目录、`research/` 三处，现统一收在此目录。

## RDP / 远程桌面架构研究

- [1REMOTE-WINDOWING-RESEARCH-20261117.md](1REMOTE-WINDOWING-RESEARCH-20261117.md)
- [FULLSCREEN-RDP-RESEARCH-20261121.md](FULLSCREEN-RDP-RESEARCH-20261121.md)
- [MOBAXTERM-RDP-ARCHITECTURE-RESEARCH-20261117.md](MOBAXTERM-RDP-ARCHITECTURE-RESEARCH-20261117.md)
- [MOBAXTERM-RDP-EMBEDDING-DEEP-DIVE-20261117.md](MOBAXTERM-RDP-EMBEDDING-DEEP-DIVE-20261117.md)
- [MULTITAB-MULTIVIEW-COEXISTENCE-DESIGN-20261117.md](MULTITAB-MULTIVIEW-COEXISTENCE-DESIGN-20261117.md)
- [MULTITAB-MULTIVIEW-INTEGRATION-20261117.md](MULTITAB-MULTIVIEW-INTEGRATION-20261117.md)
- [RDP-NATIVE-VISIBILITY-FIX-20261117.md](RDP-NATIVE-VISIBILITY-FIX-20261117.md)
- [RDP-SPLIT-INCIDENT-20261117.md](RDP-SPLIT-INCIDENT-20261117.md)
- [RDP-WINDOWING-RESEARCH-SUMMARY-20261117.md](RDP-WINDOWING-RESEARCH-SUMMARY-20261117.md)
- [SIMPLEREMOTE-TABBED-RDP-RESEARCH-20261117.md](SIMPLEREMOTE-TABBED-RDP-RESEARCH-20261117.md)
- [RDP-CONFIG-FIX-20261122.md](RDP-CONFIG-FIX-20261122.md) —— **RDP 配置页两处缺陷修复**（删除用户「回来」根因 = 远程管理页删 Windows 账户与中控台连接配置不联动，非重建非缓存；默认页改为选中「通用设置」；附用户验证步骤与「文案区分」待决策项）

## 会话瘦身 / 会话隔离

- [SESSION-SLIM-DEEP-20261122.md](SESSION-SLIM-DEEP-20261122.md) —— 会话瘦身两层拆分（分账号杀进程 / 机器级 HKLM 策略）
- [SESSION-SLIM-DEEP-V3-20261122.md](SESSION-SLIM-DEEP-V3-20261122.md) —— **SessionSlimmer 深度优化补充**（三层设计 / 会话43实测 54进程2168MB / SearchHost块392MB+Widgets块99MB精确归因 / 注册表禁用+恢复命令 / 是否影响操作逐项判断 / 会话隔离架构结论 CreateDesktop不可替代RDP / INJECTED对原神=功能边界非封号风险）
- [SESSION-POLICY-FIX-20261122.md](SESSION-POLICY-FIX-20261122.md) —— **注册表策略修复**（小组件/搜索是 class=Machine，旧代码写用户分支从未生效；新增 `ApplyMachinePolicy` 写 HKLM + 代理启动写 HKCU Edge 策略（路径A绕开已登录无法挂载），附准确键名/恢复方法/路径B建议）
- [SESSION-SLIM-SWITCH-WIRING-20261122.md](SESSION-SLIM-SWITCH-WIRING-20261122.md) —— **会话瘦身三开关接线 + 两页面统一**（`SessionSlimKillEnabled` 接入 `Slim()` 生效；说明页改只读+跳转，操作页三开关齐全；接线对照表）

## 自动化 / 流程引擎

- [AUTOMATION-BUG-20261122.md](AUTOMATION-BUG-20261122.md) —— **自动化「无 END + 日志触点只触发一次」根因审计**（数据层时间窗语义，非引擎锁存卡死；含最小复现测试 `tools/layout-check/LogRetriggerCheck`）
- [LOG-LATCH-DESIGN-20261122.md](LOG-LATCH-DESIGN-20261122.md) —— **日志触点「事件锁存 + 消费」实现**（TrigLogMatch 从「最近 N 秒电平」改为「出现即锁存、动作消费后清空」，替代时间窗方案；含 `tools/layout-check/LogLatchCheck`）

## 视频流 / 远程画面（2026-11-22 主线）

- [HANDOFF-VIDEO-20261122.md](HANDOFF-VIDEO-20261122.md) ← **最新交接**（服务端视频流全链路跑通：WGC 2~8ms + NVENC 6~8ms + /stream，端到端 636~742ms 其中 500ms 是 ffplay 缓冲）
- [STREAM-LATENCY-TEST-20261122.md](STREAM-LATENCY-TEST-20261122.md) —— **延迟实测**（软编 1384ms vs 硬编 636~742ms，各阶段耗时分解）
- [WGC-SNAPSHOT-20261122.md](WGC-SNAPSHOT-20261122.md) —— **WGC 非黑帧实证**（A1 抓原神 1920x1080 非黑 100%，含截图）
- [WGC-TFM-VERIFY-20261122.md](WGC-TFM-VERIFY-20261122.md) —— **TFM 多目标方案**（55 个 tools 零改动，publish 加 -f）
- [WGC-A1-DYNAMIC-20261122.md](WGC-A1-DYNAMIC-20261122.md) —— WGC 帧率实测（31.7fps = 原神锁帧，抓窗口比抓屏快 2.6 倍）
- [WGC-VERIFY-20261122.md](WGC-VERIFY-20261122.md) —— WGC 在 RDP 可用（身份是关键）
- [VIDEO-STREAM-RESEARCH-20261122.md](VIDEO-STREAM-RESEARCH-20261122.md) —— 视频流方案对比（MF 软编 mfh264enc 系统自带 Win7+）
- [NVENC-DESIGN-20261122.md](NVENC-DESIGN-20261122.md) —— 硬件编码设计（多厂商抽象层）
- [STREAM-GAP-20261122.md](STREAM-GAP-20261122.md) —— **为什么串流软件能 60fps**（BitBlt 是 2006 年的 GDI）
- [ANDROID-APP-DESIGN-20261122.md](ANDROID-APP-DESIGN-20261122.md) —— **安卓 APP 设计**（Kotlin + SurfaceView + MediaCodec）

## 性能与架构

- [PERF-3.3.4-20261122.md](PERF-3.3.4-20261122.md) —— **性能基线**（16% 单核的构成分解 + 三个卡顿根因）
- [LOOK-OPTIMIZE-20261122.md](LOOK-OPTIMIZE-20261122.md) —— **转视角链路优化 + 手机端贯通**（去 Thread.Sleep(6) + moverel 单向 NoReply + 8ms 节流 + 触屏滑动手势状态机）
- [WEB-DIRECT-3.3.5-20261122.md](WEB-DIRECT-3.3.5-20261122.md) —— **网页直连代理**（端口按账号分配 / 端口独占实测 / 路径穿越防护）
- [REVIEW-20261122.md](REVIEW-20261122.md) —— **三方案对抗性评审**（唯一 P0/P1/P2 顺序、量化复算、风险与遗漏项）
- [WEB-EXTENSION-PLAN-20261122.md](WEB-EXTENSION-PLAN-20261122.md) —— 网页 BGI 数据与自动化流程扩展（按需 API / 双端乐观锁 / 安全前置）
- [STREAM-PERF-AUDIT-20261122.md](STREAM-PERF-AUDIT-20261122.md) —— **画面链路性能根因审计 + 最小成本远程控制内核**（逐跳数据流 / CPU 五分解 / 7 方案量化 / 推荐架构 + 分阶段路线）
- [DASHBOARD-DESIGN-20261122.md](DASHBOARD-DESIGN-20261122.md) —— **主页/控制面板信息架构 + myorg 模块取舍**（模块取舍表 / 账号卡片字段排序 / 快捷按钮 / CPU内存开销结论 / 网页与APP分工 / 应砍清单）
- [SESSION-SLIM-PAGE-DESIGN-20261122.md](SESSION-SLIM-PAGE-DESIGN-20261122.md) —— **会话瘦身说明页设计**（两区块拆分 / 每个优化项的大白话四行说明 / 三个独立开关 / 全机设置警告色默认关）
- [DUAL-RATE-DESIGN-20261122.md](DUAL-RATE-DESIGN-20261122.md) —— **录制/直播双速率解耦设计**（2fps 录像 + 60fps 直播，共享单次抓屏，file:line 改动清单 + 分步施工）
- [STREAM-PLAN-V2-20261122.md](STREAM-PLAN-V2-20261122.md) —— **RDP 实测后的远程画面方案**（帧率上界 / 交互解耦 / 外网码率 / RDP 服务端能力边界）
- [NVENC-DESIGN-20261122.md](NVENC-DESIGN-20261122.md) —— **NVENC 硬编替换 JPEG**（RDP 内可用性实测：NVENC 可用但抓屏 31ms 才是瓶颈；多厂商 IVideoEncoder 抽象层 + 选型 MF MFT 不选 SDK + 4060 Laptop 12 路并发查证 + 回退链 + AMD/Intel 备用记录）
- [MOBILE-REMOTE-PLAN-20261122.md](MOBILE-REMOTE-PLAN-20261122.md) —— **手机端远程控制方案**（PWA→壳套 APP 路线 / 手势映射 / 外网穿透与安全边界 / 里程碑）
- [MOBILE-CONTROLS-DESIGN-20261122.md](MOBILE-CONTROLS-DESIGN-20261122.md) —— **手机端控制层设计**（虚拟摇杆数学与事件序列 / 可编辑键位布局 JSON / 中文输入上屏时序 / 断线释放协议）
- [ANDROID-APP-DESIGN-20261122.md](ANDROID-APP-DESIGN-20261122.md) —— **安卓 APP 协议设计与工程脚手架**（Kotlin + 传统 View/SurfaceView + Compose 混合 / ExoPlayer vs MediaCodec 取舍 / 协议时序 / 目录树 / 类骨架 / 分阶段）
- [VIRTUAL-CONTROLS-RESEARCH-20261122.md](VIRTUAL-CONTROLS-RESEARCH-20261122.md) —— **虚拟按键/摇杆/键位映射调研**（UU 远程公开资料 / Moonlight OSC 详细拆解 / 开源项目对比表 / 手机端转视角方案对比 / 键位映射 JSON Schema v2 / 许可风险）
- [MIYOUSHE-RESEARCH-20261122.md](MIYOUSHE-RESEARCH-20261122.md) —— **米游社扫码/短信登录与收益数据获取调研**（开源项目清单 / 扫码+短信登录完整流程含极验 / 收益接口清单 / C# 集成方案含 DS 签名 / 风险合规）
- [SYSTEM-OPTIMIZE-20261122.md](SYSTEM-OPTIMIZE-20261122.md) —— **系统优化：可关服务白名单 + Defender 目录排除**（6 项服务白名单 / 仅本次停止不改启动类型 / Defender 排除增删 / 风险边界）
- [OSS-REMOTE-RESEARCH-20261122.md](OSS-REMOTE-RESEARCH-20261122.md) —— **开源远程串流项目调研**（Sunshine/Moonlight/Parsec 替代/候选项目对比 / 转视角实现方案对比 / WGC vs DXGI vs BitBlt 在 RDP 下的可用性 / 可借鉴代码 file:line / 许可风险 / 给 SoulLink 的 3 条建议）
- [GITHUB-SWEEP-20261122.md](GITHUB-SWEEP-20261122.md) —— **GitHub 地毯式搜索 60 项目清单**（远程/串流/抓屏/虚拟显示器/输入注入 / WGC 与虚拟显示器在 RDP 会话的 issue 级证据 / 最值得看的 5 个项目 / 已证实·传闻·未验证标注）
- [VDD-RESEARCH-20261122.md](VDD-RESEARCH-20261122.md) —— **虚拟显示器（IddCx）方案深度研究**（parsec-vdd 原理/源码/许可/安装 / 同类方案对比表 / RDP 会话用 RDPDD 而非 IddCx 的关键修正 / 副作用 / 换本地会话方向评估 / 给 SoulLink 的 3 条建议）
- [VIDEO-STREAM-RESEARCH-20261122.md](VIDEO-STREAM-RESEARCH-20261122.md) —— **视频流方案调研**（MF vs 小型库 vs 精简 ffmpeg 对比 / MF 可用性矩阵与检测逻辑 / 原生安卓 APP 的 MediaCodec 拉流方案 / MSE vs WebCodecs 支持矩阵 / 最终推荐与分阶段路线）
- [CN-CLOUD-GAMING-20261122.md](CN-CLOUD-GAMING-20261122.md) —— **国内云游戏/云电脑调研**（UU/顺网/达龙/START/云原神/ToDesk·向日葵·RayLink/华为·阿里 对比表 / 多开路线 A(云厂商GPU虚拟化) vs B(远程串流) 架构分析 / 与 SoulLink RDP 差异 / 可借鉴做法 / UU 手机虚拟按键产品级设计参考）
- [CITATION-AUDIT-20261122.md](CITATION-AUDIT-20261122.md) —— 上述三份文档的 file:line 引用全量核对（107 条，0 WRONG / 0 MISSING）
- [PERF-MEASURE-20261122.md](PERF-MEASURE-20261122.md) —— **性能实测方法**（`scripts/measure-soullink-perf.ps1` 用法 + 三个关键数字的验证步骤）
- [ROUTE-LEDGER-DESIGN-20261122.md](ROUTE-LEDGER-DESIGN-20261122.md) —— **配置组/路线级收益与报错台账**（归因规则 / JSONL 落盘 / 读写契约 / 查询能力 / 磁盘估算，最小可用版已实现）
- [SESSION-ARCH-20261122.md](SESSION-ARCH-20261122.md) —— **会话架构路线评估**（RDP / 虚拟显示器 / 本地多开 / 虚拟机 四条路线对比 + 本机实测环境 + 本地多开否决 + VM 真实开销「NVIDIA 笔记本 GPU 被 GPU-PV 排除」+ 推荐路径 A + 给用户的直白解释）
- [LOBBY-OPTIMIZE-20261122.md](LOBBY-OPTIMIZE-20261122.md) —— **联机大厅按需访问 + 发车人数 2-4 可配**（改前/改后流量量化对比 / 轮询 vs 长连接取舍 / SQLite schema 迁移 / 改动清单）

## 交接与状态文档

- [HANDOFF-3.3.19-20261122.md](HANDOFF-3.3.19-20261122.md) ← **最新**（网页直连代理 + 按需取数 + 性能修复）
- [HANDOFF-3.3.3-20261122.md](HANDOFF-3.3.3-20261122.md)（屏幕录制 + 网页远程 + 性能修复）
- [HANDOFF-2.17.45-20261121.md](HANDOFF-2.17.45-20261121.md)
- [HANDOFF-FAILURE-2.13-20261119.md](HANDOFF-FAILURE-2.13-20261119.md)
- [HANDOFF.archive-2026-11-15.md](HANDOFF.archive-2026-11-15.md)
- [HANDOFF.md](HANDOFF.md)
- [HANDOVER-AUTOMATION.md](HANDOVER-AUTOMATION.md)
- [HANDOVER-BROKER.md](HANDOVER-BROKER.md)
- [HANDOVER-GUIDE.md](HANDOVER-GUIDE.md)
- [HANDOVER.md](HANDOVER.md)
- [NEW_SESSION_PROMPT.md](NEW_SESSION_PROMPT.md)

## 变更记录与版本规范

- [CHANGELOG-2.10.0.md](CHANGELOG-2.10.0.md)
- [CHANGELOG-2.10.1.md](CHANGELOG-2.10.1.md)
- [CHANGELOG-2.10.2.md](CHANGELOG-2.10.2.md)
- [CHANGELOG-2.11.0.md](CHANGELOG-2.11.0.md)
- [CHANGELOG-2.17.45.md](CHANGELOG-2.17.45.md)
- [CHANGELOG-2.3.0.md](CHANGELOG-2.3.0.md)
- [CHANGELOG-2.6.2.md](CHANGELOG-2.6.2.md)
- [CHANGELOG-2.8.8.md](CHANGELOG-2.8.8.md)
- [CHANGELOG-2.8.9.md](CHANGELOG-2.8.9.md)
- [CHANGELOG-2.9.0.md](CHANGELOG-2.9.0.md)
- [CHANGELOG-2.9.1.md](CHANGELOG-2.9.1.md)
- [VERSIONING.md](VERSIONING.md)

## 服务端部署与运维

- [LOBBY-DEPLOY-20261122.md](LOBBY-DEPLOY-20261122.md) —— **新服务器（42.192.114.250）部署方案与流量监控**（自包含交叉编译可行性 / 部署步骤 / 腾讯云安全组 / 流量监控命令 / 回滚 / HTTPS 三选项）
- [LOBBY-DEPLOY-2.1.0-20261122.md](LOBBY-DEPLOY-2.1.0-20261122.md) —— **服务端 2.1.0 部署记录**（实际部署步骤 / schema 迁移验证 / 发车人数验证 / 回滚方法）

## 其它

- [BROKER-TESTING.md](BROKER-TESTING.md)
- [PERF-BRANCH-PLAN.md](PERF-BRANCH-PLAN.md)

---

## 说明

这些是开发过程中的研究记录、故障复盘与交接文档，**不是当前代码的说明**。
当前有效的规则见项目根的 `AGENTS.md` 与 `docs/RELEASE-RULES.md`。
