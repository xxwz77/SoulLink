using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SoulLink.Services
{
    internal static class EngineComponentVersions
    {
        internal const string AgentFile = "SoulLink.Agent.exe";
        internal const string BrokerFile = "SoulLink.Broker.exe";
        internal const string BrokerServiceName = "SoulLink.DevBroker";

        internal static Version? Read(string path, string component)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.Equals(info.OriginalFilename, Path.ChangeExtension(component, ".dll"), StringComparison.OrdinalIgnoreCase))
                    return null;
                return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
            }
            catch { return null; }
        }

        internal static bool NeedsUpdate(string target, string component, Version offered)
            => Read(target, component) is not { } current || current < offered;

        internal static bool IsRegisteredBroker(string servicePath)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + BrokerServiceName);
                return key?.GetValue("ImagePath") is string command
                    && string.Equals(command.Trim(), "\"" + servicePath + "\" --broker", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // 旧服务使用主程序的 PE 标识。仅接受已注册在固定安装路径的 Broker。
        internal static Version? ReadInstalledBroker(string servicePath)
            => Read(servicePath, BrokerFile)
                ?? (IsRegisteredBroker(servicePath) ? Read(servicePath, "SoulLink.exe") : null);

        internal static Version? ReadMarker(string path)
        {
            try
            {
                return Version.TryParse(File.ReadAllText(path).Trim(), out var version) && version.Build >= 0
                    ? new Version(version.Major, version.Minor, version.Build) : null;
            }
            catch { return null; }
        }
    }
}
