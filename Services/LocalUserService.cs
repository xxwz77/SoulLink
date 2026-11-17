using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>本机一个 Windows 本地账户的当前状态。</summary>
    public sealed class LocalUserInfo
    {
        public string Name = "";
        /// <summary>账户是否启用。</summary>
        public bool Enabled = true;
        /// <summary>是否被隐藏（Winlogon SpecialAccounts\UserList 里配了 0）。</summary>
        public bool Hidden;
        /// <summary>密码是否永不过期。</summary>
        public bool PasswordNeverExpires;
        /// <summary>是否在 administrators 组。</summary>
        public bool IsAdmin;
        /// <summary>是否在 "Remote Desktop Users" 组。</summary>
        public bool IsRemoteUser;
        /// <summary>是否为本机内置账户（Administrator/Guest/DefaultAccount/...），默认不允许删。</summary>
        public bool IsBuiltIn;
    }

    /// <summary>
    /// Windows 本地账户管理。
    ///
    /// ★ 全部通过 net.exe / PowerShell 完成，不 P/Invoke NetUserAdd：
    ///   这些命令在中文/英文系统下行为一致，而且能直接复用系统自带的错误信息。
    /// ★ 所有动作都要求管理员权限；非管理员会返回明确原因而不是抛异常。
    /// </summary>
    public static class LocalUserService
    {
        private const string UserListKey =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

        /// <summary>Windows 内置账户，默认不允许删除。</summary>
        private static readonly HashSet<string> BuiltInNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Administrator", "Guest", "DefaultAccount", "WDAGUtilityAccount", "defaultuser0",
            "All Users", "Default"
        };

        // ── 执行 ─────────────────────────────────────────────────────────

        public sealed class Result
        {
            public bool Ok;
            public string Output = "";
            public static Result Fail(string msg) => new Result { Ok = false, Output = msg };
            public static Result Success(string msg = "") => new Result { Ok = true, Output = msg };
        }

        private static async Task<Result> RunAsync(string file, string args, int timeoutMs = 30000)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    // 系统命令输出多为 OEM/GBK，用 UTF8 可能乱码，但状态判断只依赖退出码与关键词
                };
                using var p = Process.Start(psi);
                if (p == null) return Result.Fail("进程启动失败");

                var sb = new StringBuilder();
                sb.Append(await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false));
                sb.Append(await p.StandardError.ReadToEndAsync().ConfigureAwait(false));

                using var cts = new CancellationTokenSource(timeoutMs);
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    return Result.Fail("命令超时：" + file + " " + args);
                }

                string output = sb.ToString().Trim();
                return new Result { Ok = p.ExitCode == 0, Output = output };
            }
            catch (Exception ex)
            {
                return Result.Fail(ex.Message);
            }
        }

        private static Task<Result> NetAsync(string args, int timeoutMs = 30000)
            => RunAsync("net.exe", args, timeoutMs);

        // ── 枚举 ─────────────────────────────────────────────────────────

        /// <summary>列出本机全部本地账户及其状态。</summary>
        public static async Task<List<LocalUserInfo>> ListAsync()
        {
            var list = new List<LocalUserInfo>();

            // ★ 用 Get-LocalUser 而不是 net user：
            //   1) 输出结构化（CSV），不会因为系统语言不同而解析失败；
            //   2) PasswordExpires 为空 = 永不过期，语义明确；
            //   3) win11 24H2 起 wmic 已被移除，不能依赖它。
            var ps = await RunPowerShellAsync(
                "Get-LocalUser | Select-Object Name,Enabled,PasswordExpires | ConvertTo-Csv -NoTypeInformation");

            if (!ps.Ok || string.IsNullOrWhiteSpace(ps.Output))
            {
                // 退化路径：至少把用户名列出来，别让页面空着
                var fallback = await NetAsync("user");
                foreach (var n in ParseUserNames(fallback.Output))
                    list.Add(new LocalUserInfo { Name = n, IsBuiltIn = BuiltInNames.Contains(n) });
                return list;
            }

            var admins = await GetGroupMembersAsync("administrators");
            var remote = await GetGroupMembersAsync("Remote Desktop Users");
            var hidden = ReadHiddenUsers();

            var lines = ps.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines.Skip(1))   // 跳过 CSV 表头
            {
                var cols = SplitCsv(line);
                if (cols.Count < 3) continue;
                string name = cols[0];
                if (string.IsNullOrWhiteSpace(name)) continue;

                bool enabled = cols[1].Equals("True", StringComparison.OrdinalIgnoreCase);
                // PasswordExpires 为空 → 永不过期
                bool neverExpire = string.IsNullOrWhiteSpace(cols[2]);

                list.Add(new LocalUserInfo
                {
                    Name = name,
                    Enabled = enabled,
                    PasswordNeverExpires = neverExpire,
                    IsAdmin = admins.Contains(name),
                    IsRemoteUser = remote.Contains(name),
                    Hidden = hidden.Contains(name),
                    IsBuiltIn = BuiltInNames.Contains(name)
                });
            }

            return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> SplitCsv(string line)
        {
            var result = new List<string>();
            var cur = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQuotes = !inQuotes;
                }
                else if (ch == ',' && !inQuotes) { result.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            result.Add(cur.ToString());
            return result;
        }

        /// <summary>执行 PowerShell 脚本并取回 stdout。</summary>
        private static async Task<Result> RunPowerShellAsync(string script, int timeoutMs = 60000)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script);

                using var p = Process.Start(psi);
                if (p == null) return Result.Fail("PowerShell 启动失败");

                string o = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string e = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);

                using var cts = new CancellationTokenSource(timeoutMs);
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    return Result.Fail("PowerShell 超时");
                }
                return new Result { Ok = p.ExitCode == 0, Output = (o + e).Trim() };
            }
            catch (Exception ex) { return Result.Fail(ex.Message); }
        }

        private static List<string> ParseUserNames(string output)
        {
            var names = new List<string>();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool inList = false;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (!inList)
                {
                    if (line.Contains("----")) inList = true;
                    continue;
                }
                if (line.IndexOf("The command completed", StringComparison.OrdinalIgnoreCase) >= 0) break;
                if (line.IndexOf("命令成功完成", StringComparison.Ordinal) >= 0) break;
                if (line.Trim().Length == 0) continue;

                // 一行可能有多个用户名，按空白切分
                foreach (var token in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Length == 0) continue;
                    if (!names.Contains(token, StringComparer.OrdinalIgnoreCase)) names.Add(token);
                }
            }
            return names;
        }

        private static async Task<HashSet<string>> GetGroupMembersAsync(string group)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var r = await NetAsync("localgroup \"" + group + "\"");
            if (!r.Ok) return set;

            var lines = r.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            bool inMembers = false;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (!inMembers)
                {
                    if (line.Contains("----")) inMembers = true;
                    continue;
                }
                if (line.Length == 0) { if (set.Count > 0) break; continue; }
                if (line.IndexOf("The command completed", StringComparison.OrdinalIgnoreCase) >= 0) break;
                if (line.IndexOf("命令成功完成", StringComparison.Ordinal) >= 0) break;
                set.Add(line);
            }
            return set;
        }

        /// <summary>读取被隐藏的账户（UserList 下值为 0 的项）。</summary>
        public static HashSet<string> ReadHiddenUsers()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UserListKey);
                if (baseKey == null) return set;
                foreach (var n in baseKey.GetValueNames())
                {
                    try
                    {
                        var v = baseKey.GetValue(n);
                        if (v is int i && i == 0) set.Add(n);
                    }
                    catch { }
                }
            }
            catch { }
            return set;
        }

        // ── 校验 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 按 Windows 本地账户规则校验用户名。
        /// 规则：1~20 字符；不能含 " / \ [ ] : ; | = , + * ? &lt; &gt; @；
        /// 不能以句点结尾；不能是纯空白；不能与保留名冲突。
        /// </summary>
        public static string? ValidateUserName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "用户名不能为空";
            if (name.Length > 20) return "用户名最多 20 个字符";
            if (name.Trim().Length != name.Length) return "用户名首尾不能有空格";
            if (name.EndsWith(".")) return "用户名不能以句点结尾";

            const string bad = "\"/\\[]:;|=,+*?<>@";
            foreach (var c in name)
                if (bad.IndexOf(c) >= 0) return "用户名不能包含字符：" + c;
            if (name.Any(char.IsControl)) return "用户名不能包含控制字符";

            if (BuiltInNames.Contains(name)) return "该名称是系统保留名，不能使用";
            return null;
        }

        /// <summary>
        /// 按 Windows 本地账户规则校验密码。
        /// 默认策略下：不得为空、最长 127；这里额外要求至少 6 位，
        /// 避免创建出空密码账户（空密码账户 RDP 无法登录）。
        /// </summary>
        public static string? ValidatePassword(string password, string userName)
        {
            if (string.IsNullOrEmpty(password)) return "密码不能为空（空密码账户无法远程登录）";
            if (password.Length < 6) return "密码至少 6 位";
            if (password.Length > 127) return "密码最多 127 个字符";
            if (password.Any(char.IsControl)) return "密码不能包含控制字符";
            if (!string.IsNullOrEmpty(userName)
                && password.IndexOf(userName, StringComparison.OrdinalIgnoreCase) >= 0)
                return "密码不能包含用户名";
            return null;
        }

        // ── 新建 / 删除 / 改状态 ──────────────────────────────────────────

        public sealed class CreateSpec
        {
            public string UserName = "";
            public string Password = "";
            public bool AddToAdministrators;
            public bool AddToRemoteDesktopUsers = true;
            public bool Hide;
            public bool PasswordNeverExpires = true;
        }

        /// <summary>创建本地账户并按需加入组。</summary>
        public static async Task<Result> CreateAsync(CreateSpec spec)
        {
            if (!IsElevated()) return Result.Fail("需要管理员权限");

            var vn = ValidateUserName(spec.UserName);
            if (vn != null) return Result.Fail(vn);
            var vp = ValidatePassword(spec.Password, spec.UserName);
            if (vp != null) return Result.Fail(vp);

            // net user <name> <pwd> /add /expires:never
            var r = await NetAsync("user \"" + spec.UserName + "\" \"" + spec.Password + "\" /add /expires:never");
            if (!r.Ok) return Result.Fail("创建用户失败：" + Clean(r.Output));

            var notes = new List<string>();

            if (spec.AddToAdministrators)
            {
                var a = await NetAsync("localgroup administrators \"" + spec.UserName + "\" /add");
                notes.Add(a.Ok ? "已加入 administrators" : "加入 administrators 失败");
            }
            if (spec.AddToRemoteDesktopUsers)
            {
                var d = await NetAsync("localgroup \"Remote Desktop Users\" \"" + spec.UserName + "\" /add");
                notes.Add(d.Ok ? "已加入 Remote Desktop Users" : "加入 Remote Desktop Users 失败");
            }
            if (spec.PasswordNeverExpires)
            {
                var w = await RunPowerShellAsync(
                    "Set-LocalUser -Name '" + Escape(spec.UserName) + "' -PasswordNeverExpires $true");
                notes.Add(w.Ok ? "密码永不过期" : "设置密码永不过期失败");
            }
            if (spec.Hide)
            {
                SetHidden(spec.UserName, true);
                notes.Add("已在登录界面隐藏");
            }

            return Result.Success(string.Join("；", notes));
        }

        /// <summary>删除账户（不删除用户目录以外的系统数据）。</summary>
        public static async Task<Result> DeleteAsync(string userName)
        {
            if (!IsElevated()) return Result.Fail("需要管理员权限");
            if (BuiltInNames.Contains(userName)) return Result.Fail("不能删除系统内置账户");

            var r = await NetAsync("user \"" + userName + "\" /delete");
            return r.Ok ? Result.Success("已删除") : Result.Fail("删除失败：" + Clean(r.Output));
        }

        /// <summary>显示/隐藏账户（登录界面）。</summary>
        public static Result SetHidden(string userName, bool hidden)
        {
            if (!IsElevated()) return Result.Fail("需要管理员权限");
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(UserListKey, true);
                if (key == null) return Result.Fail("无法打开注册表项");
                if (hidden)
                    key.SetValue(userName, 0, Microsoft.Win32.RegistryValueKind.DWord);
                else
                    key.DeleteValue(userName, throwOnMissingValue: false);
                return Result.Success(hidden ? "已隐藏" : "已显示");
            }
            catch (Exception ex) { return Result.Fail(ex.Message); }
        }

        /// <summary>设置密码永不过期。</summary>
        public static async Task<Result> SetPasswordNeverExpiresAsync(string userName, bool neverExpire)
        {
            if (!IsElevated()) return Result.Fail("需要管理员权限");
            // ★ 用 Set-LocalUser：wmic 在 Win11 24H2+ 已被移除，不能依赖
            var r = await RunPowerShellAsync("Set-LocalUser -Name '" + Escape(userName)
                + "' -PasswordNeverExpires $" + (neverExpire ? "true" : "false"));
            return r.Ok ? Result.Success(neverExpire ? "已设为永不过期" : "已恢复过期策略")
                        : Result.Fail(Clean(r.Output));
        }

        /// <summary>转义单引号，避免用户名破坏 PowerShell 字符串。</summary>
        private static string Escape(string s) => (s ?? "").Replace("'", "''");

        // ── 辅助 ─────────────────────────────────────────────────────────

        public static bool IsElevated()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "（无输出）";
            var lines = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(x => x.Trim())
                         .Where(x => x.Length > 0)
                         .Take(6);
            return string.Join(" / ", lines);
        }
    }
}
