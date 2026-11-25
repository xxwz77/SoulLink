# t31 交付：米游社并入数据中心网页体系（#/miyoushe/:id + 摩拉收益分区）

> 日期：2026-11-25（t31，attempt 94889fd4）。按用户裁定「数据中心才是拿数据的地方」——米游社不是独立顶级页，而是并入现有 hash 路由体系；网页是唯一详情入口（主程序只放摘要）。
> 改动：docs/web-index.html（真源，WebServer 优先读取）+ Services/WebPages.cs（内置兜底常量，同步一致）。

## 1. 结构（与现有三按钮完全同款）

- 账号卡片第 4 个按钮「米游社」：data-act=miyoushe → #/miyoushe/:id（复用 :1008 的通用分发，无需改分发逻辑）；
- 页面容器 #miyoushePage：与 ledgerPage/earningsPage 同构（.page-back 返回总览 + .sect-t 标题 + buildUserSwitch 用户切换器 + body）——只放账号运维：凭据状态三指标（已登录/UID/掩码账号）、扫码登录（弹窗自动轮询 2s 直到 Confirmed/过期）、一键导入（@bound，不回显 cookie，只提示来源与份数）、立即签到、自动签到开关+时点、签到批次历史表（日期/批次号前 8 位/任务/状态/详情+retcode，近 20 条）；
- route() 分支 page === 'miyoushe' + document.title = '米游社 · <账号>' + back 按钮回 #/；
- 页面互斥：hideFuncPages/showLedger/showEarnings/showRemote 全部加 miyoushePage 隐藏，无残留。

## 2. 摩拉数据并入「摩拉收益」页（#/earnings/:id）——分区不混算

BGI 日志侧（原有 metrics：今日经验/摩拉/拾取）与米游社账目侧（新增）分区展示：
- 新增「米游社札记（账目侧 · 击杀四类归类 · 近 7 天）」区块：四类汇总指标（传奇精英/普通精英/传奇小怪/小怪，summary 接口记录条数口径）+ 明细表（rows 接口：日期/类别/单次摩拉/次数/摩拉/路线组）；
- 筛选：类别下拉（全部/四类/unknown）；分页：每页 50 条 + 上一页/下一页 + 共 N 条·第 x/y 页；
- 表尾固定口径说明：米游社札记账目流水（击杀归类，记录条数口径，kill_count 恒 1 不乘算），与 BGI 日志侧挂机时段收益口径不同，分区展示不混算；
- 米游社区加载失败不影响 BGI 侧展示（独立 try/catch）。

## 3. 数据来源（全部现有 API，鉴权后）

登录/运维：/api/miyoushe/login/status、login/begin + login/poll（QR base64 PNG 直出，2s 轮询）、import（POST @bound）、settings（开关/时点）、sign/run、sign/logs（MiyousheTaskExecution：BatchId/TaskId/State/Retcode/Detail/TsMs）。摩拉：mora/summary（四类汇总）+ mora/rows（明细）。

## 4. 验证

- dotnet build -c Release 双 TFM 0 error；
- docs/web-index.html 与 Services/WebPages.cs 同步一致（function showMiyoushe / renderMoraTable / loadEarningsMiyoushe 三标志双 True；WebServer 优先读文件、常量兜底，两源都新）；
- JS 大括号平衡 630/630；全部静态元素 id 存在（动态创建的 modal/wrap 除外）；route 分支/卡片按钮/四处页面互斥隐藏全就位；
- 零 CDN 引用（外部资源 0 处，QR 为 base64 内联）；API 全部鉴权后（mora 端点位于 IsAuthorized 之后）；
- 无业务 C# 改动（仅 WebPages.cs 内嵌 HTML 常量同步）。

## 5. 边界说明（Lead 要求的判断点）

两套摩拉口径确实不同：BGI 日志侧=挂机时段实际拾取（当日零点重置）；米游社侧=札记账目流水（月账目、约 1 小时延迟、分钟精度）。处理=分区展示 + 固定口径说明文字，任何位置都没有把两源数字相加或对比成单一数字。
