# SoulLink 2.11.0 变更

日期：2026-11-19
主线：新应用图标

## 图标

粉紫渐变圆角方块 + 居中显示器线条 + 云朵与气泡点缀。
源图 1024×1024。

### 设计稿位置

原图在桌面：`E:\Desktop\SoulLink-computer-clouds-bubbles-spread.png`
生成的 ico 在仓库：`Assets\soullink.ico`

### 生成方式

新增 `tools/layout-check/IconBuilder.cs`，用 System.Drawing 逐尺寸
高质量重采样（HighQualityBicubic + 预乘透明），每个尺寸以 PNG 内嵌：

```
16 / 20 / 24 / 32 / 40 / 48 / 64 / 96 / 128 / 256
```

选 PNG 内嵌而不是 BMP：体积小、透明通道无损，Vista 以上原生支持。

## 统一三处图标

| 位置 | 原来 | 现在 |
|------|------|------|
| exe 文件图标 | 旧 ico | 新 ico |
| 任务栏 / 标题栏 | 继承 exe | 继承 exe |
| 托盘图标 | **代码手绘紫青圆环** | 从 `Assets/soullink.ico` 加载 |

托盘那处是个长期不一致：`TrayIcon.BuildIcon()` 用 GDI+ 画了一个
紫青渐变圆环，和 exe 图标完全不是一个图案，同一程序在任务栏和托盘
显示两种样子。现在统一到同一个 ico；资源读不到时才回退到旧手绘，
保证不会因为缺资源而崩。

## 版本号

2.10.2 → 2.11.0。改了外观属于可感知变化，按语义化版本升次版本号。

## 验证

- ICO 结构：10 个尺寸齐全
- exe 嵌入：`ExtractAssociatedIcon` 可读出
- 更新包内 exe：SHA256 匹配、版本 2.11.0.0、图标可读
- 四处一致：`dist` / `dist\stable` / Broker 服务目录 / publish 产物
- 挂机版身份隔离：配置目录、互斥体、管道、任务前缀均独立

## 挂机版

- `dist\stable\SoulLink-Stable.exe` 已同步为 2.11.0（同哈希）。
- 旧版保留：`SoulLink-Stable-2.10.2.exe`。
- 更新包：`releases\SoulLink-v2.11.0-Stable-Update.zip`（115.8 MB）。
- 包内主程序名为 `SoulLink.exe`，部署时须改名为 `SoulLink-Stable.exe`。

## 未解决

- 本版只改图标与版本号，未动业务逻辑。
- A3（会话 7）侧键仍未复验。
- UAC 开启的 linked token 分支、冷启动时间窗口、锁屏与会话切换未回归。