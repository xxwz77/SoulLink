using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace SoulLink.Services
{
    public static class EdgeStartupPolicy
    {
        private const string EdgeKey = @"SOFTWARE\Policies\Microsoft\Edge";
        private const string Ownership = @"SOFTWARE\SoulLink\SessionSlim\Edge";
        private static readonly (string Name, int Value)[] Values = { ("StartupBoostEnabled", 0), ("BackgroundModeEnabled", 0) };

        public static void ApplyUserPolicy(bool enable)
        {
            try { ApplyForUser(Registry.CurrentUser, enable); }
            catch (Exception ex) { SoulLink.Relay.RelayAgent.LogQuiet("Edge 启动项策略失败：" + ex.Message); }
        }

        internal static int ApplyForUser(RegistryKey hive, bool enable) => ApplyOwned(hive, EdgeKey, Ownership, Values, enable);

        // Never replace an existing policy. A rollback deletes only values this app owns and that still match.
        internal static int ApplyOwned(RegistryKey hive, string policyPath, string ownershipPath,
            IReadOnlyList<(string Name, int Value)> values, bool enable)
        {
            using var policy = enable ? hive.CreateSubKey(policyPath) : hive.OpenSubKey(policyPath, writable: true);
            using var ownership = enable ? hive.CreateSubKey(ownershipPath) : hive.OpenSubKey(ownershipPath, writable: true);
            if (policy == null || ownership == null) return 0;

            int changed = 0;
            foreach (var (name, value) in values)
            {
                object? current = policy.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (enable)
                {
                    if (current != null) continue;
                    ownership.SetValue(name, value, RegistryValueKind.DWord);
                    try { policy.SetValue(name, value, RegistryValueKind.DWord); }
                    catch { ownership.DeleteValue(name, false); throw; }
                    changed++;
                }
                else if (ownership.GetValue(name) is int written && written == value)
                {
                    if (current is int actual && actual == written)
                    {
                        policy.DeleteValue(name, false);
                        changed++;
                    }
                    ownership.DeleteValue(name, false);
                }
            }
            return changed;
        }
    }
}
