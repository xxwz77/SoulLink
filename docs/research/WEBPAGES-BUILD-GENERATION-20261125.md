# WebPages 构建期单源生成（t34）

> 日期：2026-11-25。目标：消灭 WebPages.cs 与 docs/web-index.html 的手工双源同步。网页内容和 WebServer 读取逻辑未改。

## 生成机制

- `Build/WebPages.targets` 在 CoreCompile 前执行 `GenerateWebPagesIndex`。
- `scripts/generate-webpages-index.ps1` 读取 `docs/web-index.html`（UTF-8），生成 raw string literal。
- 输出：`obj/<Configuration>/<TargetFramework>/WebPages.Index.g.cs`，两个 TFM 独立目录，避免并发写冲突。
- `Services/WebPages.cs` 只剩 partial class 声明与注释，无手写大段 HTML。
- Inputs 包含 HTML、生成脚本、targets；Outputs 为 TFM 专属生成文件。输入未变时 Target 被跳过。
- 缺 HTML 明确构建失败；连续三个双引号明确生成失败；脚本保留 UTF-8 BOM 兼容 PowerShell 5.1。

## 精简后的 WebPages.cs

```csharp
namespace SoulLink.Services
{
    /// <summary>
    /// 网页兜底常量：构建期由 docs/web-index.html 生成到 obj/<TFM>/WebPages.Index.g.cs。
    /// 不要在本文件手写 HTML。WebServer 的外部文件优先读取逻辑保持不变。
    /// </summary>
    internal static partial class WebPages
    {
    }
}
```

## 实测证据

- 双 TFM Release 0 error。
- HTML 加临时标记后 Target 重新执行，生成的 WebPages.Index.g.cs 含标记。
- 单文件发布成功（PublishSingleFile + IncludeNativeLibrariesForSelfExtract），日志明确生成路径；HostModel Bundler.IsBundle=True。
- 不改 HTML 再构建，日志明确“正在跳过目标 GenerateWebPagesIndex，因为所有输出文件相对于输入文件而言都是最新的”，时间戳未变。
- 反射读取 WebPages.Index + 私有 LoadIndexHtml：`FALLBACK_MATCHES_INDEX=True`，常量兜底路径可执行。
- 缺 HTML：`MISSING_BUILD_EXIT=1`，错误“找不到 docs/web-index.html，网页兜底内容无法生成”。
- 恢复原文后 SOURCE_MARKER=False、GENERATED_MARKER=False，再构建 0 error。

单文件验证边界：已验证发布时 Target 执行与合法 bundle；反射回退使用同一发布源编译的参考程序集。未把失败的自制 bundle 解包尝试算作成功结果。

## 踩坑修正

1) WriteLinesToFile 会把 HTML 换行当 item 分隔，改专用生成脚本。
2) PowerShell 5.1 中文脚本需 BOM。
3) raw string 开始/结束独立换行。
4) 生成路径使用 BaseIntermediateOutputPath + Configuration + TargetFramework，避免并发冲突。
5) 生成脚本/targets 纳入 Inputs，防止修改生成器后被增量跳过。
