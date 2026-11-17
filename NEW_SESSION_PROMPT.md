# 新会话开场白（直接复制）

如果你是接手这个项目的新会话，请先完整阅读：

1. E:\Documents\deepseek-harness\SoulLink\HANDOVER.md
2. E:\Documents\deepseek-harness\SoulLink\releases\RDP-WINDOWING-RESEARCH-SUMMARY-20261117.md
3. E:\Documents\deepseek-harness\SoulLink\releases\RDP-SPLIT-INCIDENT-20261117.md

如需多窗口背景，再读：
- releases/1REMOTE-WINDOWING-RESEARCH-20261117.md
- releases/SIMPLEREMOTE-TABBED-RDP-RESEARCH-20261117.md
- releases/MOBAXTERM-RDP-ARCHITECTURE-RESEARCH-20261117.md

## 当前基线

- 当前正式开发版本：v1.13.2，另有提交 0d12968（连接/注销动画不遮展开导航）。
- dist/ 是开发发布目录，不包含分屏实验；当前 exe 还含 0d12968 动画层级小修复，与 v1.13.2 标签归档哈希不同。
- dist/stable/ 是生产挂机版，正在运行，绝对不要修改或回退。
- 分屏、Canvas 内部窗口、独立顶级窗口、1:1 分屏实验源码均已撤回。
- 撤回后的主工程 Release 构建通过；未因此启动应用或 RDP。

## 已验证 / 未验证

已验证或有用户可用反馈：
- BGI 更新：4 账号 exe 哈希一致。
- BGI 脚本：A1 单人模式跑通 5 条路线。
- RDP 侧栏原生浮层基础交互。
- RDP 首次点击 Down/Up 消费、后台重连保护曾实测可用。

未完整验收：
- RDP 注销后重开保护层的最后修复。
- 连接/注销动画与导航层级修复的完整归档验收。
- BGI 2/3/4 人组队和拾取效果。

没有可交付实现：
- 同屏分屏、内部可拖动窗口、分屏 1:1、布局记忆。
这些实验代码已撤回，不要从旧测试目录恢复。

## 严重事故与禁令

2026-11-17 分屏验证后发生 Windows Bugcheck 0x119
VIDEO_SCHEDULER_INTERNAL_ERROR，电脑死机重启。
NVIDIA nvlddmkm Event 153；转储：
C:\Windows\Minidump\111726-18203-01.dmp

尚未完成内核栈分析，只能确认时间相关和高风险触发组合，不能声称某一行代码是直接根因。

绝对禁止：
1. 修改/回退 dist/stable/，除非用户重新明确授权。
2. 操作 A1/A4；测试只用 A2/A3，真实 RDP 验证必须得到用户授权。
3. 不按 SessionId 过滤就操作任何进程；relay 与主窗口严格区分。
4. 启动 tools/rdp-layout-validation、tools/rdp-process-windowing 中的旧验证程序。
5. 在当前机器恢复同进程多 ActiveX 分屏、Canvas 内部窗口或独立顶级窗口原型。
6. 为验证“是否已经好了”再次复现 0x119。

## 多窗口研究结论

- 1Remote：同进程 Host + 顶级 Dragablz 标签窗口，可拆出/全屏；不是主窗口内多视口分屏。
- SimpleRemote：单窗口多标签，标签切换不断连、不重建 ActiveX；不支持同屏自由分屏。
- MobaXterm：主控 + MobaRTE 子进程，证据指向 RDP 子窗口嵌入主控原生面板；既有进程隔离，也受主应用约束。

如果继续做功能：
- 优先稳定单窗口多标签保活（SimpleRemote 模型）。
- 若未来仍研究分屏，先分析 0x119 转储，再单独评估 MobaXterm 式“子进程内嵌子窗口”。
- 不要直接照搬第三方项目的多 ActiveX 实现，也不要把“别人能用”当作当前驱动环境安全证明。

## 建议下一步

先让我确认你的目标：
A. 只读分析 0x119 转储和驱动风险；
B. 稳定现有单窗口多标签与保护层；
C. 回到 BGI 组队/拾取验证；
D. 其他你明确指定的任务。

未经我明确授权，不启动任何 RDP 验证、不操作账号、不修改 dist/ 或 dist/stable/。
