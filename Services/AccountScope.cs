using System;
using System.Collections.Generic;
using System.Linq;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>全局“仅管理挂机账号”策略：隐藏主用户，同时阻止后台把它当执行目标。</summary>
    public static class AccountScope
    {
        public static bool OnlyAutomationAccounts(AppConfig? cfg = null)
            => (cfg ?? App.Config).Settings.OnlyAutomationAccounts;

        public static bool IsPrimaryUser(string userName, AppConfig? cfg = null)
        {
            if (string.IsNullOrWhiteSpace(userName)) return false;
            return string.Equals(SessionService.BareName(userName), SessionService.BareName(SessionService.CurrentUser), StringComparison.OrdinalIgnoreCase);
        }

        public static bool Allows(string userName, AppConfig? cfg = null)
            => !OnlyAutomationAccounts(cfg) || !IsPrimaryUser(userName, cfg);

        public static List<Account> Accounts(AppConfig? cfg = null)
        {
            var source = (cfg ?? App.Config).Accounts ?? new List<Account>();
            return source.Where(a => a != null && Allows(a.UserName,cfg)).ToList();
        }

        public static List<SessionInfo> Sessions(IEnumerable<SessionInfo> sessions, AppConfig? cfg = null)
            => (sessions ?? Enumerable.Empty<SessionInfo>()).Where(s => s != null && Allows(s.UserName,cfg)).ToList();

        public static bool IsAllowed(Account account, AppConfig? cfg = null) => account != null && Allows(account.UserName,cfg);
    }
}