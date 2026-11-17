# RDP 分屏实验事故记录（2026-11-17）

## 结论

2026-11-17 15:09～15:10，运行独立分屏验证版后 Windows 死机并自动重启。
系统证据确认这是内核级显卡调度故障，不是普通 WPF 异常：

- 意外关机：2026-11-17 15:09:29
- 重启：2026-11-17 15:10:35
- NVIDIA nvlddmkm Event 153：15:09:17，GPUID 100
- Bugcheck：0x00000119 VIDEO_SCHEDULER_INTERNAL_ERROR
- 参数：0xA000, 0xFFFF94DC15551000, 0x8F1, 0x8F2
- 转储：C:\Windows\Minidump\111726-18203-01.dmp
- 转储 SHA256：D0DDE9953F8727B94EEF1115CB7F194CBBDC2797A3F70ED6AFEE428DC4C75913

用户态代码不能直接产生内核 Bugcheck，但实验中的多 ActiveX 重挂、原生覆盖 HWND 和远端分辨率重协商可能触发了显示驱动缺陷。
时间与分屏验证版操作吻合，因此该实现不得继续在此机器复现。

## 显示驱动环境

- NVIDIA GeForce RTX 4060 Laptop GPU，驱动 32.0.16.2101（2026-10-19）
- GameViewer Virtual Display Adapter，驱动 15.6.5.199
- OrayIddDriver Device，驱动 16.54.44.444
- Windows 多用户 RDP ActiveX 环境

## 已采取的安全措施

- 停止所有分屏实验，不启动验证版、不重新连接账号。
- 撤回未提交的分屏/内部窗口/1:1 源码，恢复提交 0d12968 的内容。
- 仅执行 dotnet build 验证安全基线，0 错误；没有启动应用。
- 没有修改 dist/stable/；生产挂机实例保持运行。
- dist/ 仍为 v1.13.2，不包含本次分屏实验。

## 禁止事项

在转储完成专业分析、驱动风险得到控制之前，禁止在这台机器上继续验证：

1. 同时挂载多个 RDP ActiveX 并进行容器间重挂。
2. 拖动/缩放时反复 UpdateSessionDisplaySettings。
3. 每个 RDP 视口高频创建/隐藏/重定位独立 HWND 浮层。
4. 原生标题栏与 ActiveX 内容高频相互重排。
5. 以“再试一次”方式复现 0x119。

## 后续原则

先分析转储和驱动兼容性。若继续设计分屏，应先做不含真实 ActiveX 的静态原型，
再考虑单 ActiveX 重投影/截图式预览方案；不得直接恢复已撤回的多 ActiveX 实现。
