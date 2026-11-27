using System;
using System.Collections.Generic;
using SoulLink.Services;

namespace SoulLink.Views;

internal sealed record StartupItemRow(StartupEntry Entry, string Title, string Purpose, string Impact)
{
    private sealed record Description(string Title, string Purpose, string Impact);

    private static readonly Dictionary<string, Description> KnownItems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SoulLink-StableAutoStart"] = new("SoulLink 挂机版自启", "按计划任务启动 SoulLink 挂机版。",
            "关闭该任务后，依赖它启动的挂机版需手动开启。"),
        ["Fences"] = new("Stardock Fences", "给桌面图标分组，也能隐藏或整理图标。",
            "关闭登录自启后，桌面图标分组可能要等 Fences 手动启动才会加载。"),
        ["LGHUB"] = new("罗技 G HUB", "管理罗技外设的灯光、宏和应用配置。",
            "依赖 G HUB 的灯光、宏或应用配置切换可能要等它手动启动才会生效。"),
        ["OneDrive"] = new("Microsoft OneDrive", "同步云盘文件，也可能备份桌面和文档。",
            "关闭登录自启后，自动同步通常要等 OneDrive 手动启动才会恢复。"),
        ["Clash Verge (Admin)"] = new("Clash Verge", "用于管理网络代理，当前启动名称标记为管理员模式。",
            "关闭自启后，代理连接可能要等 Clash Verge 手动启动才会建立。"),
        ["OmenSuperHub_Logon"] = new("HP OMEN Gaming Hub", "管理 OMEN 设备的游戏性能、灯光和其他设置。",
            "关闭后，依赖 Gaming Hub 启动的灯光或性能配置可能要等应用启动才会生效。"),
        ["WallpaperEngine"] = new("Wallpaper Engine", "加载动态壁纸。",
            "关闭登录自启后，动态壁纸通常要等 Wallpaper Engine 手动启动才会加载。"),
    };

    private static readonly Description EdgeStartupBoost = new("Microsoft Edge 预启动",
        "提前在后台启动 Edge，缩短首次打开浏览器的等待时间。",
        "关闭后仍可手动打开 Edge，首次打开可能稍慢。");

    private static readonly Description QuarkUpdater = new("夸克更新助手（按名称判断）",
        "从名称判断，这一项用于检查或安装夸克客户端更新。",
        "若确认是更新助手，关闭自启可能延迟自动更新。");

    internal static StartupItemRow Describe(StartupEntry entry)
    {
        string name = entry.Name.StartsWith("OneDrive Startup Task-S-1-", StringComparison.OrdinalIgnoreCase)
            ? "OneDrive" : entry.Name;
        if (!KnownItems.TryGetValue(name, out var description))
        {
            if (entry.Name.StartsWith("MicrosoftEdgeAutoLaunch_", StringComparison.OrdinalIgnoreCase))
                description = EdgeStartupBoost;
            else if (entry.Name.StartsWith("QuarkUpdaterTaskUser", StringComparison.OrdinalIgnoreCase))
                description = QuarkUpdater;
            else if (entry.Program is { } program)
                KnownItems.TryGetValue(System.IO.Path.GetFileNameWithoutExtension(program.FileName), out description);
        }

        if (description != null) return new(entry, description.Title, description.Purpose, description.Impact);
        if (entry.Program is not { } identity)
            return new(entry, entry.Name, DescribeTrigger(entry.Source), "可展开查看启动命令和来源。");

        string title = ReadMetadata(identity.ProductName) ?? entry.Name;
        string? publisher = ReadMetadata(identity.Publisher);
        string purpose = "登录时启动 " + identity.FileName + (publisher == null ? "。" : " · 文件标注：" + publisher);
        return new(entry, title, purpose, "此操作只影响这条启动记录。可展开查看程序路径并核对用途。");
    }

    private static string? ReadMetadata(string? text)
    {
        string? value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 160) return null;
        foreach (char character in value)
            if (char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)
                return null;
        return value;
    }

    internal static string DescribeTrigger(string source)
    {
        if (source.StartsWith("注册表 RunOnce", StringComparison.Ordinal))
            return "Windows 按登录条件执行的一次性任务。";
        if (source.StartsWith("注册表 Run", StringComparison.Ordinal))
            return "Windows 在对应用户登录时运行。";
        return source switch
        {
            "启动文件夹" => "Windows 在对应用户登录时打开其中的文件或快捷方式。",
            "计划任务" => "任务计划程序按开机或登录条件运行。",
            _ => source
        };
    }
}
