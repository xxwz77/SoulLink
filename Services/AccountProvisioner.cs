using System;
using System.Collections.Generic;
using System.Linq;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 新建用户时自动生成连接配置。
    ///
    /// ★ 地址分配规则（按用户要求）：
    ///   从 127.0.0.2 起，向上顺延，跳过已被占用的环回地址。
    ///   本机多用户 RDP 每个会话对应一个 127.0.0.x，新账号拿下一个空位即可。
    /// </summary>
    public static class AccountProvisioner
    {
        /// <summary>环回地址起始值。</summary>
        public const int LoopbackStart = 2;
        /// <summary>环回地址上限（保留到 127.0.0.254）。</summary>
        public const int LoopbackEnd = 254;

        /// <summary>找出下一个未被占用的 127.0.0.x 地址。</summary>
        public static string NextLoopbackHost(IEnumerable<Account> existing)
        {
            var used = new HashSet<int>();
            foreach (var a in existing ?? Enumerable.Empty<Account>())
            {
                var host = a?.RdpHost ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(host, @"^127\.0\.0\.(\d{1,3})$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int octet))
                    used.Add(octet);
            }

            for (int i = LoopbackStart; i <= LoopbackEnd; i++)
                if (!used.Contains(i)) return "127.0.0." + i;

            // 用尽了就退回 127.0.0.1（用户几乎不可能遇到）
            return "127.0.0.1";
        }

        /// <summary>
        /// 为新账号生成一份连接配置。
        /// </summary>
        /// <param name="userName">Windows 登录名（用于 RDP 认证）</param>
        /// <param name="password">明文密码，会经 DPAPI 加密后保存</param>
        /// <param name="nickname">昵称（显示名），与用户名分开</param>
        /// <param name="existing">现有账号，用于挑一个空闲的环回地址</param>
        public static Account Create(string userName, string password, string nickname,
            IEnumerable<Account> existing)
        {
            var acc = new Account
            {
                UserName = userName.Trim(),
                RdpUserName = userName.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(nickname) ? userName.Trim() : nickname.Trim(),
                RdpHost = NextLoopbackHost(existing),
                RdpPort = 3389,
                Note = "由远程管理自动创建"
            };
            if (!string.IsNullOrEmpty(password))
                ConfigStore.SetPassword(acc, password);
            return acc;
        }

        /// <summary>把新账号并入配置并持久化。</summary>
        public static void Persist(Account account)
        {
            App.Config.Accounts.Add(account);
            ConfigStore.Save(App.Config);
        }
    }
}
