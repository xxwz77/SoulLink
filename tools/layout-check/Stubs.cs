using System;
using System.Collections.Generic;
using SoulLink.Models;

// FlowValidator 只用到 BgiPathResolver.GetPath 和 cfg.Settings.BgiPaths。
// 这里给最小替身，避免把整个 WPF 应用拖进测试。
namespace SoulLink.Services
{
    public static class BgiPathResolver
    {
        public static string GetPath(string userName) => "";
    }
}

namespace SoulLink.Models
{
    public sealed class AppSettings
    {
        public Dictionary<string, string> BgiPaths { get; set; } = new();
    }

    public sealed class AppConfig
    {
        public AppSettings Settings { get; set; } = new();
    }
}
