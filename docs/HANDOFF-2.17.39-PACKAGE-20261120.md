# SoulLink 交接文档 · 2.17.39 更新包发布

> 时间：2026-11-20 20:20
> 版本：**2.17.39.0**（用户已认可"信任此版本"）
> 产出：`releases/SoulLink-v2.17.39-Update.zip`（117 MB）+ 解压目录

---

## 1. 用户要求

> 「信任此版本，做更新包，打包别忘了附带资源」

## 2. 更新包内容（★ 重点是资源）

```
releases/SoulLink-v2.17.39-Update/
├─ SoulLink.exe                    67,486,930 字节（2.17.39.0）
├─ 7za.exe                          1,292,288  ← BGI 更新包解压要用
├─ sl-rescue.exe                   35,105,268  ← 自救工具
├─ broker-install.ps1                   3,127  ← 根目录兼容路径
├─ broker-uninstall.ps1                   479
├─ tools/
│   ├─ SoulLink.BrokerSetup.exe    35,232,057  ← Broker 服务安装器
│   ├─ broker-install.ps1               3,127  ← BrokerInstall.FindScript 首选路径
│   └─ broker-uninstall.ps1               479
├─ 更新说明.txt
└─ SHA256.txt                     （9 条）
```

### 2.1 为什么必须带这些文件

代码里运行时真的会去这些位置找文件（逐个确认过）：

| 文件 | 谁在用 | 缺失后果 |
|---|---|---|
| `7za.exe` | `BgiUpdater`、`RouteUpdater` | 抛"找不到 7za.exe（应随程序一起分发）"，BGI/路线更新全废 |
| `tools\SoulLink.BrokerSetup.exe` | `BrokerWindow` | 弹窗"当前这份程序里没有 tools\SoulLink.BrokerSetup.exe"，Broker 装不了 |
| `tools\broker-install.ps1` | `BrokerInstall.FindScript` | 优先找 `tools\broker-*.ps1`，找不到回退根目录 |
| 根目录 `broker-*.ps1` | 同上（回退路径） | 两处都缺才失败，所以**两份都放** |
| `sl-rescue.exe` | 用户自救 | 程序起不来时没有救援工具 |

### 2.2 无需外部文件的资源（已内嵌进 exe）

```
SoulLink.Assets.RdpWrap.TermWrap.dll
SoulLink.Assets.RdpWrap.Zydis.dll
SoulLink.Assets.RdpWrap.UmWrap.dll
SoulLink.Assets.RdpWrap.EndpWrap.dll
SoulLink.g.resources   （含 Assets/soullink.ico）
```

实际用 `Assembly.GetManifestResourceNames()` 核对过：**4 个 RdpWrap DLL + 图标资源都在程序集里**，
所以包内**不需要**再附 `Assets\` 目录。这一点写进了更新说明，避免用户到处找 DLL。

## 3. 验证

### 3.1 完整性

```
解压后按 SHA256.txt 校验：ok=9  bad=0
与 dist 源文件比对：
  SoulLink.exe                      ✓ 一致
  7za.exe                           ✓ 一致
  sl-rescue.exe                     ✓ 一致
  tools\SoulLink.BrokerSetup.exe    ✓ 一致
```

### 3.2 端到端启动（关键）

把 zip 解压到**临时目录**，直接启动里面的 `SoulLink.exe`（就是用户更新后的真实使用姿势）：

```
进程存活=True  pid=196420
MainWindowTitle=SoulLink · 跨会话执行器
```

说明包内文件自包含，**不依赖仓库目录**也能正常起主窗口。

> 中途踩了一个坑：第一次用 `--identity=测试名` 启动，进程在 `tray created` 后以 exit 1 退出。
> 原因不是包坏了，而是新身份**没有配置文件**，走到了 `_startupRequest` 分支直接 Shutdown。
> 换成"解压到临时目录 + 正常身份启动"后立刻通过。

## 4. 产物

| 项目 | 值 |
|---|---|
| 解压目录 | `E:\Documents\deepseek-harness\SoulLink\releases\SoulLink-v2.17.39-Update\` |
| 压缩包 | `releases\SoulLink-v2.17.39-Update.zip` |
| 大小 | 122,692,976 字节（117 MB） |
| SHA256 | `6898CC7CD636571937769D04B0D92F5E30872A96381806B36642595A93CF78DC` |
| 主程序 | 67,486,930 字节 · `C381242CA223594D102CE968DF04B2D11FF7C292A0456BF6063700E6733FC6C2` |

## 5. 更新说明里特别强调的三点

1. **必须保留 `tools\` 目录结构**，不能只拷 `SoulLink.exe`；
2. **注入代理要单独更新**（`C:\Program Files\SoulLinkDev\`，只换主程序不生效），
   给了完整步骤 + 指向界面上「安装/修复服务」按钮；
3. RdpWrap 的 4 个 DLL **已内嵌**，不用额外找文件。

## 6. 待办

1. 用户实际更新一次，确认主界面布局恢复正常。
2. 注入代理投送偶发不稳（2.17.38 归档时出现过），更新说明里已列为已知问题。
3. 2.17.39 尚未做源码归档（可信归档仍是 `v2.17.38-reliable`）。
