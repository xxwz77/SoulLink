using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SoulLink.Services;

internal sealed record StartupAccount(string Sid, string Name, bool IsCurrent);
internal sealed record StartupScanResult(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Notes, bool IsElevated);
internal sealed record StartupMigrationResult(bool Success, string Message);
internal sealed record StartupProgramInfo(string FileName, string? ProductName, string? Publisher);
internal enum StartupAccountAction { None, RemoveUserRun }

internal sealed class StartupEntry(
    string name, string command, string source, string scope, string location, bool isMachineWide,
    string migrationBlockReason, StartupInventory.RunSnapshot? snapshot = null,
    StartupInventory.UserRunSnapshot? userRunSnapshot = null, string? accountActionBlockReason = null,
    StartupProgramInfo? program = null)
{
    public string Name { get; } = name;
    public string Command { get; } = command;
    public string Source { get; } = source;
    public string Scope { get; } = scope;
    public string Location { get; } = location;
    public bool IsMachineWide { get; } = isMachineWide;
    public bool CanMigrate => Snapshot != null && MigrationBlockReason.Length == 0;
    public string MigrationBlockReason { get; } = migrationBlockReason;
    public StartupAccountAction AccountAction => UserRunSnapshot == null ? StartupAccountAction.None : StartupAccountAction.RemoveUserRun;
    public bool CanManage => CanMigrate || AccountAction != StartupAccountAction.None;
    public StartupProgramInfo? Program { get; } = program;
    public string AccountActionBlockReason { get; } = accountActionBlockReason ??
        (isMachineWide ? "这是共享启动项，无法只从所选账号移除。" : migrationBlockReason);
    internal StartupInventory.RunSnapshot? Snapshot { get; } = snapshot;
    internal StartupInventory.UserRunSnapshot? UserRunSnapshot { get; } = userRunSnapshot;
}

internal static class StartupInventory
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    private const string RunKey = CurrentVersion + @"\Run";
    private const string RunOnceKey = CurrentVersion + @"\RunOnce";
    private const string WowUserRunKey = @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalKey = CurrentVersion + @"\Explorer\StartupApproved";
    private const string UserFoldersKey = CurrentVersion + @"\Explorer\User Shell Folders";
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const string StartupSuffix = @"Microsoft\Windows\Start Menu\Programs\Startup";
    private const int KeyQueryValue = 0x0001;
    private const int KeySetValue = 0x0002;
    private const int KeyCreateSubKey = 0x0004;
    private const int ErrorFileNotFound = 2;
    private const int ErrorMoreData = 234;
    private const int KeyNameInformation = 3;
    private const uint RegSz = 1;
    private const uint RegExpandSz = 2;
    private const uint RegBinary = 3;
    private const string AllUsers = "所有用户";
    private const string FolderReadOnly = "启动文件夹条目只读，快捷方式可能使用相对路径或特定的账号设置。";
    private const string UserFolderAction = "请登录所选账号，运行 shell:startup，确认文件夹归属后再管理对应快捷方式。";
    private const string SharedFolderAction = "这是共享启动文件夹（shell:common startup），修改其中的项目会影响所有账号。";
    private const string SourceChanged = "项目在扫描后发生了变化，请刷新后再试。";

    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);
    private static readonly RegistryView NativeView = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
    private static readonly HashSet<string> BlockedPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "powershell_ise", "pwsh", "wscript", "cscript", "mshta", "rundll32",
        "regsvr32", "reg", "msiexec", "installutil", "msbuild", "forfiles", "schtasks", "wmic",
        "python", "pythonw", "node", "ruby", "java", "javaw", "bash", "wsl", "dotnet",
        "securityhealthsystray", "msmpeng", "msascuil", "mssense", "nissrv", "hpisystemeventutilityhost",
        "igfxtray", "hkcmd", "igfxpers", "rtkauduservice64", "ctfmon", "userinit", "winlogon"
    };
    private static readonly string[] ProtectedComponents =
    [
        "security", "antivirus", "antimalware", "anti-malware", "endpoint", "defender", "firewall",
        "crowdstrike", "sentinelone", "sophos", "symantec", "mcafee", "trellix", "bitdefender", "kaspersky",
        "eset", "avast", "avira", "norton", "trend micro", "malwarebytes", "360safe", "huorong", "火绒",
        "安全中心", "杀毒", "防火墙", "windowsapps", "common files", "system32",
        "installer", "setup", "device driver", "audio driver", "realtek", "intel graphics", "nvidia driver",
        "安装程序", "驱动程序"
    ];

    internal sealed record RunSnapshot(RegistryView View, uint Kind, byte[] Data);
    internal sealed record UserRunSnapshot(string Sid, string Name, uint Kind, byte[] Data);

    internal static IReadOnlyList<StartupAccount> GetAccounts()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string? currentSid = identity.User?.Value;
        var accounts = new List<StartupAccount>();
        if (identity.User is { } current && IsInteractiveSid(current))
            accounts.Add(new(currentSid!, identity.Name, true));

        try
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, NativeView);
            foreach (string candidate in users.GetSubKeyNames())
            {
                if (!candidate.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) ||
                    candidate.Equals(currentSid, StringComparison.OrdinalIgnoreCase) ||
                    candidate.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var sid = new SecurityIdentifier(candidate);
                    if (!IsInteractiveSid(sid)) continue;
                    string name = sid.Translate(typeof(NTAccount)).Value;
                    if (name.StartsWith(Environment.MachineName + "\\", StringComparison.OrdinalIgnoreCase))
                        accounts.Add(new(sid.Value, name, false));
                }
                catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException or Win32Exception)
                {
                    // An orphaned, unloaded or unresolvable SID cannot be selected.
                }
            }
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            // The current account can still be listed when HKU enumeration is restricted.
        }

        return accounts.OrderByDescending(account => account.IsCurrent)
            .ThenBy(account => account.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static StartupScanResult Scan(StartupAccount? account = null, bool manageableOnly = false)
    {
        bool elevated = SystemLauncher.IsElevated;
        var entries = new List<StartupEntry>();
        var notes = new List<string>();
        if (!elevated)
            notes.Add("管理员权限可读取更多账号的启动项，迁移也需要管理员权限。");

        ReadMachineRegistry(NativeView, entries, notes, manageableOnly);
        if (Environment.Is64BitOperatingSystem)
            ReadMachineRegistry(RegistryView.Registry32, entries, notes, manageableOnly);

        if (!manageableOnly)
            ReadKnownStartupFolder(Environment.SpecialFolder.CommonStartup, AllUsers, true, entries, notes);

        var available = GetAccounts();
        StartupAccount? selected = account == null
            ? available.FirstOrDefault(candidate => candidate.IsCurrent)
            : available.FirstOrDefault(candidate => candidate.Sid == account.Sid);
        if (selected == null)
        {
            notes.Add("所选账号的用户配置单元不可用。登录该账号后可重新扫描。");
        }
        else
        {
            ReadUserRegistry(selected, entries, notes, manageableOnly);
            if (!manageableOnly)
            {
                if (selected.IsCurrent)
                    ReadKnownStartupFolder(Environment.SpecialFolder.Startup, selected.Name, false, entries, notes);
                else
                    ReadOtherStartupFolder(selected, entries, notes);
            }
        }

        if (!manageableOnly) ReadStartupTasks(selected, entries, notes);
        return new(entries.Where(entry => !manageableOnly || entry.CanManage)
            .OrderByDescending(entry => entry.IsMachineWide)
            .ThenBy(entry => entry.Source, StringComparer.CurrentCulture)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), notes, elevated);
    }

    internal static StartupMigrationResult Migrate(StartupEntry entry, StartupAccount target)
    {
        if (!SystemLauncher.IsElevated) return Failed("请以管理员身份运行 SoulLink 后重试。");
        if (entry.Snapshot is not { } snapshot || !entry.CanMigrate)
            return Failed(entry.MigrationBlockReason.Length == 0 ? "该项目无法迁移。" : entry.MigrationBlockReason);

        try
        {
            StartupAccount? liveAccount = GetAccounts().FirstOrDefault(account => account.Sid == target.Sid);
            if (liveAccount == null)
                return Failed("目标账号的用户配置单元已不可用，请登录该账号后刷新。");

            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, snapshot.View);
            using var nativeMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, NativeView);
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, NativeView);
            using var committedSource = machine.OpenSubKey(RunKey);
            if (committedSource == null) return Failed(SourceChanged);
            EnsureKeyName(committedSource.Handle, MachineKeyName(RunKey, snapshot.View));

            string userVersion = liveAccount.Sid + "\\" + CurrentVersion;
            string userRun = userVersion + @"\Run";
            using var committedDestination = users.OpenSubKey(userRun);
            if (committedDestination != null) EnsureKeyName(committedDestination.Handle, UserKeyName(userRun));

            using var transaction = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 10000, null);
            if (transaction.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

            using var source = OpenTransacted(machine.Handle, RunKey, KeyQueryValue | KeySetValue, snapshot.View, transaction);
            if (source == null) return Failed(SourceChanged);
            EnsureKeyName(source, MachineKeyName(RunKey, snapshot.View));
            var actual = ReadValue(source, entry.Name);
            if (!SameValue(actual, snapshot.Kind, snapshot.Data)) return Failed(SourceChanged);
            string block = GetRunBlockReason(entry.Name, actual!, out _);
            if (block.Length != 0) return Failed(block);

            using var userParent = OpenTransacted(users.Handle, userVersion, KeyQueryValue | KeyCreateSubKey, NativeView, transaction);
            if (userParent == null) return Failed("目标账号的启动配置尚未初始化，请登录后重试。");
            EnsureKeyName(userParent, UserKeyName(userVersion));

            using var destination = CreateRunKey(userParent, transaction, out bool newDestinationKey);
            EnsureKeyName(destination, UserKeyName(userRun));
            if (!newDestinationKey && committedDestination == null)
                return Failed("目标启动配置在迁移期间创建，请刷新后再试。");
            if (ReadValue(destination, entry.Name) != null)
                return Failed("目标账号已有同名启动项，未进行迁移。");

            string? conflict = CheckRelatedValues(nativeMachine.Handle, users.Handle, liveAccount.Sid,
                entry.Name, snapshot.View, transaction);
            if (conflict != null) return Failed(conflict);

            // Copy and verify the exact registry type and bytes before removing the source within the same transaction.
            CheckError(RegSetValueEx(destination, entry.Name, 0, snapshot.Kind, snapshot.Data, (uint)snapshot.Data.Length));
            if (!SameValue(ReadValue(destination, entry.Name), snapshot.Kind, snapshot.Data))
                return Failed("目标启动项校验失败，已取消迁移。");
            if (!SameValue(ReadValue(source, entry.Name), snapshot.Kind, snapshot.Data))
                return Failed(SourceChanged);

            CheckError(RegDeleteValue(source, entry.Name));
            // TxR reads are read-committed. Check the pre-transaction values once both keys have staged writes.
            if (!SameValue(ReadValue(committedSource.Handle, entry.Name), snapshot.Kind, snapshot.Data))
                return Failed(SourceChanged);
            if (committedDestination != null && ReadValue(committedDestination.Handle, entry.Name) != null)
                return Failed("目标账号在迁移期间新增了同名启动项，请刷新后再试。");

            if (!CommitTransaction(transaction)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(true, $"已将“{entry.Name}”从所有用户迁移到 {liveAccount.Name}，下次登录时生效。");
            // Closing an uncommitted transaction rolls back all registry changes.
        }
        catch (Exception ex) when (IsReadError(ex) || ex is Win32Exception or EntryPointNotFoundException or DllNotFoundException)
        {
            return Failed("Windows 未能提交启动项迁移，已取消操作。 " + ex.Message);
        }
    }

    internal static StartupMigrationResult ChangeAccountStartup(StartupEntry entry, StartupAccount account)
    {
        if (entry.UserRunSnapshot is not { } snapshot)
            return Failed(entry.AccountActionBlockReason);
        if (snapshot.Sid != account.Sid)
            return Failed("该项目不属于所选账号，请刷新后再试。");

        try
        {
            StartupAccount? liveAccount = GetAccounts().FirstOrDefault(candidate => candidate.Sid == snapshot.Sid);
            if (liveAccount == null)
                return Failed("所选账号的用户配置单元已不可用，请登录该账号后刷新。");
            if (!liveAccount.IsCurrent && !SystemLauncher.IsElevated)
                return Failed("请以管理员身份运行 SoulLink 后移除其他账号的启动项。");

            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, NativeView);
            string userRun = liveAccount.Sid + "\\" + RunKey;
            using var committed = users.OpenSubKey(userRun);
            if (committed == null) return Failed(SourceChanged);
            EnsureKeyName(committed.Handle, UserKeyName(userRun));

            using var transaction = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 10000, null);
            if (transaction.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

            using var source = OpenTransacted(users.Handle, userRun, KeyQueryValue | KeySetValue, NativeView, transaction);
            if (source == null) return Failed(SourceChanged);
            EnsureKeyName(source, UserKeyName(userRun));
            var actual = ReadValue(source, snapshot.Name);
            if (!SameValue(actual, snapshot.Kind, snapshot.Data)) return Failed(SourceChanged);
            string reason = GetUserApprovalBlockReason(users, liveAccount.Sid, snapshot.Name)
                ?? GetRunBlockReason(snapshot.Name, actual!, out _, isAccountRemoval: true);
            if (reason.Length != 0) return Failed(reason);

            CheckError(RegDeleteValue(source, snapshot.Name));
            // Check the committed value after staging the deletion because TxR reads are read-committed.
            if (!SameValue(ReadValue(committed.Handle, snapshot.Name), snapshot.Kind, snapshot.Data))
                return Failed(SourceChanged);

            if (!CommitTransaction(transaction)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(true, $"已移除 {liveAccount.Name} 的“{snapshot.Name}”启动项，下次登录时生效。");
        }
        catch (Exception ex) when (IsReadError(ex) || ex is Win32Exception or EntryPointNotFoundException or DllNotFoundException)
        {
            return Failed("Windows 未能移除启动项，操作已取消。 " + ex.Message);
        }
    }

    private static bool IsInteractiveSid(SecurityIdentifier sid)
    {
        string value = sid.Value;
        if (value.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase)) return true;
        if (!sid.IsAccountSid() || !value.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(value.AsSpan(value.LastIndexOf('-') + 1), out int rid) && (rid == 500 || rid >= 1000);
    }

    private static void ReadMachineRegistry(RegistryView view, List<StartupEntry> entries, List<string> notes, bool manageableOnly)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var nativeHive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, NativeView);
        string node = view == RegistryView.Registry32 ? "32 位" : Environment.Is64BitOperatingSystem ? "64 位" : "本机";
        ReadRunKey(hive, RunKey, @"HKLM\" + MachineRelativePath(RunKey, view), $"注册表 Run（{node}）",
            AllUsers, true, view, nativeHive, entries, notes);
        if (!manageableOnly)
            ReadRunKey(hive, RunOnceKey, @"HKLM\" + MachineRelativePath(RunOnceKey, view), $"注册表 RunOnce（{node}）",
                AllUsers, true, null, null, entries, notes);
    }

    private static void ReadUserRegistry(StartupAccount account, List<StartupEntry> entries, List<string> notes, bool manageableOnly)
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, NativeView);
        string prefix = account.Sid + "\\";
        ReadRunKey(users, prefix + RunKey, @"HKU\" + prefix + RunKey, "注册表 Run（用户）",
            account.Name, false, null, null, entries, notes, removableAccount: account);
        if (manageableOnly) return;
        ReadRunKey(users, prefix + RunOnceKey, @"HKU\" + prefix + RunOnceKey, "注册表 RunOnce（用户）",
            account.Name, false, null, null, entries, notes);
        // HKCU\Software is shared across views. List manually created WOW6432Node keys without treating them as the shared Run key.
        if (Environment.Is64BitOperatingSystem)
        {
            ReadRunKey(users, prefix + WowUserRunKey, @"HKU\" + prefix + WowUserRunKey, "注册表 Run（用户 Wow6432Node）",
                account.Name, false, null, null, entries, notes);
            ReadRunKey(users, prefix + WowUserRunKey + "Once", @"HKU\" + prefix + WowUserRunKey + "Once",
                "注册表 RunOnce（用户 Wow6432Node）", account.Name, false, null, null, entries, notes);
        }
    }

    private static void ReadRunKey(RegistryKey hive, string path, string location, string source, string scope, bool machine,
        RegistryView? migratableView, RegistryKey? nativeMachine, List<StartupEntry> entries, List<string> notes,
        StartupAccount? removableAccount = null)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key == null) return;
            string physicalPath = machine ? @"\REGISTRY\MACHINE\" + location[5..] : UserKeyName(path);
            EnsureKeyName(key.Handle, physicalPath);
            foreach (string name in key.GetValueNames())
            {
                try
                {
                    var value = ReadValue(key.Handle, name);
                    if (value == null) continue;
                    string command = value.Command ?? $"注册表值类型 {value.Kind}，{value.Data.Length} 字节";
                    RunSnapshot? snapshot = null;
                    UserRunSnapshot? userRunSnapshot = null;
                    StartupProgramInfo? program = null;
                    bool runOnce = path.EndsWith(@"\RunOnce", StringComparison.OrdinalIgnoreCase);
                    string reason = runOnce
                        ? "RunOnce 用于一次性任务，只读。"
                        : "这是现有的单账号启动项。";
                    string? accountActionReason = null;
                    if (migratableView is { } view)
                    {
                        reason = GetRunBlockReason(name, value, out program);
                        if (reason.Length == 0)
                            reason = GetMachineApprovalBlockReason(nativeMachine!, name, view) ?? "";
                        if (reason.Length == 0) snapshot = new(view, value.Kind, value.Data);
                    }
                    if (removableAccount != null)
                    {
                        accountActionReason = GetUserApprovalBlockReason(hive, removableAccount.Sid, name)
                            ?? GetRunBlockReason(name, value, out program, isAccountRemoval: true);
                        if (accountActionReason.Length == 0) userRunSnapshot = new(removableAccount.Sid, name, value.Kind, value.Data);
                    }
                    else if (!machine && !runOnce)
                    {
                        accountActionReason = "这是用户 Wow6432Node 条目，需通过创建它的应用管理。";
                    }
                    entries.Add(new(name.Length == 0 ? "（默认）" : name, command, source, scope,
                        location, machine, reason, snapshot, userRunSnapshot, accountActionReason, program));
                }
                catch (Exception ex) when (IsReadError(ex) || ex is Win32Exception)
                {
                    string reason = "无法验证启动项。 " + ex.Message;
                    entries.Add(new(name.Length == 0 ? "（默认）" : name, "", source, scope, location, machine, reason,
                        accountActionBlockReason: machine ? null : reason));
                }
            }
        }
        catch (Exception ex) when (IsReadError(ex) || ex is Win32Exception)
        {
            notes.Add($"无法完整读取 {location}。 {ex.Message}");
        }
    }

    private static string GetRunBlockReason(string name, RegistryValue value, out StartupProgramInfo? program, bool isAccountRemoval = false)
    {
        program = null;
        if (value.Command is not { } command || command.Length == 0 ||
            value.Data.Length != (command.Length + 1) * sizeof(char) ||
            command.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0 ||
            name.Length == 0 || name[0] is '!' or '*')
            return "该项不是受支持的普通登录启动命令。";

        // User environment variables can resolve differently in the selected account.
        string? filename = GetExecutablePath(command, !isAccountRemoval && value.Kind == RegExpandSz);
        if (filename == null)
            return isAccountRemoval ? "仅支持移除直接指向本地程序绝对路径的启动项。"
                : "仅迁移直接指向本地程序文件的启动命令。";

        string executable = Path.GetFileNameWithoutExtension(filename);
        if (BlockedPrograms.Contains(executable) || LooksProtected(name) || LooksProtected(filename))
            return "系统、安全、驱动或安装组件受保护。";

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows, Environment.SpecialFolderOption.DoNotVerify);
        if (IsUnderDirectory(filename, windows))
            return "Windows 目录中的系统组件受保护。";

        try
        {
            if (!HasLocalPathWithoutLinks(filename, out var attrs) || attrs.HasFlag(FileAttributes.Directory))
                return "程序文件不可验证，或路径经过重解析点。";

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(filename);
            if (LooksProtected(version.ProductName) || LooksProtected(version.FileDescription)
                || LooksProtected(version.CompanyName) || BlockedPrograms.Contains(Path.GetFileNameWithoutExtension(version.OriginalFilename ?? "")))
                return "系统、安全、驱动或安装组件受保护。";
            program = new(Path.GetFileName(filename), version.ProductName, version.CompanyName);
        }
        catch (Exception ex) when (isAccountRemoval && ex is (FileNotFoundException or DirectoryNotFoundException))
        {
            // An otherwise eligible Run value may remain after its application is uninstalled.
            program = new(Path.GetFileName(filename), null, null);
        }
        catch (Exception ex) when (isAccountRemoval && (IsReadError(ex) || ex is Win32Exception))
        {
            return "无法读取程序文件以确认它是否受保护。";
        }

        return "";
    }

    private static bool LooksProtected(string? value) =>
        value != null && ProtectedComponents.Any(component => value.Contains(component, StringComparison.OrdinalIgnoreCase));

    private static string? GetExecutablePath(string command, bool expand)
    {
        string text = command.TrimStart();
        if (text.Length == 0) return null;
        string token;
        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            if (close < 0 || (close + 1 < text.Length && !char.IsWhiteSpace(text[close + 1]))) return null;
            token = text[1..close];
        }
        else
        {
            int end = text.AsSpan().IndexOfAny(' ', '\t');
            token = end < 0 ? text : text[..end];
        }

        if (expand && token.StartsWith('%'))
        {
            int end = token.IndexOf('%', 1);
            if (end <= 1) return null;
            string variable = token[1..end];
            string? root = GetMachineVariable(variable);
            if (root == null) return null;
            token = root + token[(end + 1)..];
        }

        if (token.Contains('%') || !token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return NormalizeLocalPath(token);
    }

    private static string? GetMachineApprovalBlockReason(RegistryKey machine, string name, RegistryView view)
    {
        string path = GetMachineApprovalPath(view);
        using var key = machine.OpenSubKey(path);
        if (key == null) return null;
        EnsureKeyName(key.Handle, MachineKeyName(path, NativeView));
        return ApprovalBlockReason(ReadValue(key.Handle, name));
    }

    private static string? GetUserApprovalBlockReason(RegistryKey users, string sid, string name)
    {
        string path = sid + "\\" + ApprovalKey + @"\Run";
        using var key = users.OpenSubKey(path);
        if (key == null) return null;
        EnsureKeyName(key.Handle, UserKeyName(path));
        return ApprovalBlockReason(ReadValue(key.Handle, name));
    }

    private static string? ApprovalBlockReason(RegistryValue? value)
    {
        if (value == null) return null;
        // Only recognize the Explorer enabled forms with a zero disable timestamp. Unknown formats stay read-only.
        if (value.Kind == RegBinary && value.Data.Length == 12 &&
            BinaryPrimitives.ReadUInt32LittleEndian(value.Data) is 2 or 6 &&
            BinaryPrimitives.ReadUInt64LittleEndian(value.Data.AsSpan(4)) == 0) return null;
        return "该启动项已禁用，或其启用状态无法确认。";
    }

    private static string? CheckRelatedValues(SafeRegistryHandle machine, SafeRegistryHandle users, string targetSid,
        string name, RegistryView sourceView, SafeFileHandle transaction)
    {
        string approval = GetMachineApprovalPath(sourceView);
        using var machineApproval = OpenTransacted(machine, approval, KeyQueryValue, NativeView, transaction);
        if (machineApproval != null)
        {
            EnsureKeyName(machineApproval, MachineKeyName(approval, NativeView));
            string? reason = ApprovalBlockReason(ReadValue(machineApproval, name));
            if (reason != null) return reason;
        }

        if (Environment.Is64BitOperatingSystem)
        {
            RegistryView otherView = sourceView == RegistryView.Registry32 ? RegistryView.Registry64 : RegistryView.Registry32;
            using var otherMachineRun = OpenTransacted(machine, RunKey, KeyQueryValue, otherView, transaction);
            if (otherMachineRun != null)
            {
                EnsureKeyName(otherMachineRun, MachineKeyName(RunKey, otherView));
                if (ReadValue(otherMachineRun, name) != null)
                    return "另一个注册表视图已有同名的所有用户启动项，请先检查它。";
            }
        }

        foreach (string relative in new[] { WowUserRunKey, ApprovalKey + @"\Run", ApprovalKey + @"\Run32" })
        {
            string path = targetSid + "\\" + relative;
            using var key = OpenTransacted(users, path, KeyQueryValue, NativeView, transaction);
            if (key == null) continue;
            EnsureKeyName(key, UserKeyName(path));
            if (ReadValue(key, name) != null)
                return "目标账号已有同名启动项或启用状态记录，未进行迁移。";
        }
        return null;
    }

    private static string GetMachineApprovalPath(RegistryView view) =>
        ApprovalKey + (view == RegistryView.Registry32 ? @"\Run32" : @"\Run");

    private static void ReadKnownStartupFolder(Environment.SpecialFolder folder, string scope, bool machine,
        List<StartupEntry> entries, List<string> notes)
    {
        try
        {
            string path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (path.Length == 0)
                notes.Add($"无法定位 {scope} 的启动文件夹。");
            else
                ReadStartupFolder(path, scope, machine, entries, notes);
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            notes.Add($"无法读取 {scope} 的启动文件夹。 {ex.Message}");
        }
    }

    private static void ReadOtherStartupFolder(StartupAccount account, List<StartupEntry> entries, List<string> notes)
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, NativeView);
            using var profile = machine.OpenSubKey(ProfileListKey + "\\" + account.Sid);
            string? profilePath = profile?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            string? profileRoot = ExpandFolderPath(profilePath, null, null);
            if (profileRoot == null)
            {
                notes.Add($"无法定位 {account.Name} 的本地用户配置目录。");
                return;
            }

            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, NativeView);
            string path = account.Sid + "\\" + UserFoldersKey;
            using var folders = users.OpenSubKey(path);
            if (folders != null) EnsureKeyName(folders.Handle, UserKeyName(path));
            string? startup = folders?.GetValue("Startup", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            string? appDataValue = folders?.GetValue("AppData", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            string? appData = ExpandFolderPath(appDataValue ?? @"%USERPROFILE%\AppData\Roaming", profileRoot, null);
            string? resolved = ExpandFolderPath(startup ?? @"%APPDATA%\" + StartupSuffix, profileRoot, appData);
            if (resolved == null || !IsUnderDirectory(resolved, profileRoot))
            {
                notes.Add($"{account.Name} 的启动文件夹已重定向或无法安全解析。");
                return;
            }
            ReadStartupFolder(resolved, account.Name, false, entries, notes);
        }
        catch (Exception ex) when (IsReadError(ex) || ex is Win32Exception)
        {
            notes.Add($"无法读取 {account.Name} 的启动文件夹。 {ex.Message}");
        }
    }

    private static void ReadStartupFolder(string path, string scope, bool machine, List<StartupEntry> entries, List<string> notes)
    {
        string? folder = NormalizeLocalPath(path);
        if (folder == null)
        {
            notes.Add($"{scope} 的启动文件夹不在本地固定磁盘上。");
            return;
        }

        FileAttributes attrs;
        try
        {
            if (!HasLocalPathWithoutLinks(folder, out attrs))
            {
                notes.Add($"{scope} 的启动文件夹经过重解析点。");
                return;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        if (!attrs.HasFlag(FileAttributes.Directory)) return;

        foreach (string file in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            entries.Add(new(name, file, "启动文件夹", scope, folder, machine, FolderReadOnly,
                accountActionBlockReason: machine ? SharedFolderAction : UserFolderAction));
        }
    }

    private static void ReadStartupTasks(StartupAccount? selected, List<StartupEntry> entries, List<string> notes)
    {
        object? scheduler = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null || (scheduler = Activator.CreateInstance(type)) == null)
            {
                notes.Add("Windows 任务计划程序不可用。");
                return;
            }

            dynamic service = scheduler;
            service.Connect();
            var pending = new Stack<string>();
            pending.Push(@"\");
            int inaccessible = 0;
            while (pending.TryPop(out string? path))
            {
                object? folder = null;
                object? taskCollection = null;
                object? folderCollection = null;
                try
                {
                    folder = service.GetFolder(path);
                    dynamic taskFolder = folder;
                    taskCollection = taskFolder.GetTasks(1); // TASK_ENUM_HIDDEN
                    dynamic tasks = taskCollection;
                    int taskCount = tasks.Count;
                    for (int index = 1; index <= taskCount; index++)
                    {
                        object? task = null;
                        try
                        {
                            task = tasks[index];
                            AddStartupTask(task, selected, entries);
                        }
                        catch (Exception ex) when (IsTaskReadError(ex)) { inaccessible++; }
                        finally { ReleaseCom(task); }
                    }
                }
                catch (Exception ex) when (IsTaskReadError(ex)) { inaccessible++; }
                finally { ReleaseCom(taskCollection); }

                try
                {
                    if (folder == null) continue;
                    dynamic taskFolder = folder;
                    folderCollection = taskFolder.GetFolders(0);
                    dynamic folders = folderCollection;
                    int count = folders.Count;
                    for (int index = 1; index <= count; index++)
                    {
                        object? child = null;
                        try
                        {
                            child = folders[index];
                            pending.Push((string)((dynamic)child).Path);
                        }
                        catch (Exception ex) when (IsTaskReadError(ex)) { inaccessible++; }
                        finally { ReleaseCom(child); }
                    }
                }
                catch (Exception ex) when (IsTaskReadError(ex)) { inaccessible++; }
                finally
                {
                    ReleaseCom(folderCollection);
                    ReleaseCom(folder);
                }
            }

            if (inaccessible > 0) notes.Add($"有 {inaccessible} 个计划任务或任务文件夹无法读取。");
        }
        catch (Exception ex) when (IsTaskReadError(ex))
        {
            notes.Add("无法读取 Windows 计划启动任务。 " + ex.Message);
        }
        finally { ReleaseCom(scheduler); }
    }

    private static void AddStartupTask(dynamic task, StartupAccount? selected, List<StartupEntry> entries)
    {
        XElement? definition = XDocument.Parse((string)task.Xml).Root;
        if (definition == null) return;
        XNamespace ns = definition.Name.Namespace;
        XElement? triggers = definition.Element(ns + "Triggers");
        if (triggers == null) return;

        bool boot = triggers.Elements(ns + "BootTrigger").Any();
        var logons = triggers.Elements(ns + "LogonTrigger").ToArray();
        bool anyLogon = logons.Any(trigger => string.IsNullOrWhiteSpace((string?)trigger.Element(ns + "UserId")));
        bool selectedLogon = selected != null && logons.Any(trigger => MatchesAccount((string?)trigger.Element(ns + "UserId"), selected));
        if (!boot && !anyLogon && !selectedLogon) return;

        bool enabled = (bool)task.Enabled;
        bool activeTrigger = (boot && triggers.Elements(ns + "BootTrigger").Any(trigger => TriggerIsEnabled(trigger, ns))) ||
            logons.Any(trigger => TriggerIsEnabled(trigger, ns) &&
                (string.IsNullOrWhiteSpace((string?)trigger.Element(ns + "UserId")) ||
                 (selected != null && MatchesAccount((string?)trigger.Element(ns + "UserId"), selected))));
        string reason = enabled && activeTrigger
            ? "计划任务含触发条件、运行身份和权限设置，只读。"
            : "该启动计划任务或相应触发器已禁用，只读。";
        string scope = boot ? "整机开机" : anyLogon ? AllUsers : selected!.Name;
        string accountActionReason = !enabled || !activeTrigger
            ? "该计划任务或相应触发器已禁用。可在 Windows 任务计划程序中查看。"
            : "请在 Windows 任务计划程序中检查此任务的触发条件和运行身份后管理。";
        string? group = (string?)definition.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "GroupId");
        if (!boot && anyLogon && !string.IsNullOrWhiteSpace(group))
            scope = "登录组 " + group;
        string[] actions = definition.Element(ns + "Actions")?.Elements().Select(action =>
            action.Name.LocalName switch
            {
                "Exec" => string.Join(" ", new[] { (string?)action.Element(ns + "Command"), (string?)action.Element(ns + "Arguments") }
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
                "ComHandler" => "COM 处理程序 " + (string?)action.Element(ns + "ClassId"),
                _ => action.Name.LocalName
            }).ToArray() ?? [];
        entries.Add(new((string)task.Name, string.Join(Environment.NewLine, actions), "计划任务",
            scope, (string)task.Path, boot || anyLogon, reason, accountActionBlockReason: accountActionReason));
    }

    private static bool TriggerIsEnabled(XElement trigger, XNamespace ns) =>
        (string?)trigger.Element(ns + "Enabled") is not ("false" or "0");

    private static bool MatchesAccount(string? value, StartupAccount account)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        if (value.Equals(account.Sid, StringComparison.OrdinalIgnoreCase) ||
            value.Equals(account.Name, StringComparison.OrdinalIgnoreCase)) return true;
        string localPrefix = Environment.MachineName + "\\";
        if (!account.Name.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string shortName = account.Name[localPrefix.Length..];
        return value.Equals(shortName, StringComparison.OrdinalIgnoreCase) ||
            value.Equals(@".\" + shortName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTaskReadError(Exception ex) =>
        IsReadError(ex) || ex is COMException or XmlException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    private static void ReleaseCom(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private static string? ExpandFolderPath(string? path, string? profile, string? appData)
    {
        if (path == null) return null;
        var result = new StringBuilder();
        int cursor = 0;
        while (cursor < path.Length)
        {
            int open = path.IndexOf('%', cursor);
            if (open < 0)
            {
                result.Append(path.AsSpan(cursor));
                break;
            }
            result.Append(path.AsSpan(cursor, open - cursor));
            int close = path.IndexOf('%', open + 1);
            if (close < 0) return null;
            string variable = path[(open + 1)..close];
            string? replacement = variable.ToUpperInvariant() switch
            {
                "USERPROFILE" => profile,
                "APPDATA" => appData,
                _ => GetMachineVariable(variable)
            };
            if (replacement == null) return null;
            result.Append(replacement);
            cursor = close + 1;
        }
        return NormalizeLocalPath(result.ToString());
    }

    private static string? GetMachineVariable(string variable)
    {
        const Environment.SpecialFolderOption noVerify = Environment.SpecialFolderOption.DoNotVerify;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows, noVerify);
        return variable.ToUpperInvariant() switch
        {
            "SYSTEMDRIVE" => Path.GetPathRoot(windows)?.TrimEnd('\\'),
            "SYSTEMROOT" or "WINDIR" => windows,
            "PROGRAMDATA" or "ALLUSERSPROFILE" => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, noVerify),
            "PROGRAMFILES" or "PROGRAMW6432" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles, noVerify),
            "PROGRAMFILES(X86)" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86, noVerify),
            _ => null
        };
    }

    private static string? NormalizeLocalPath(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/') ||
            path.AsSpan(2).IndexOfAny(':', '\0') >= 0)
            return null;
        string fullPath = Path.GetFullPath(path);
        var drive = new DriveInfo(fullPath[..3]);
        return drive.DriveType == DriveType.Fixed ? fullPath : null;
    }

    private static bool IsUnderDirectory(string path, string directory) =>
        directory.Length > 0 && path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool HasLocalPathWithoutLinks(string path, out FileAttributes attrs)
    {
        // Inspect each ancestor before opening the next one so a reparse point cannot redirect routine inventory.
        attrs = 0;
        string root = Path.GetPathRoot(path)!;
        string current = root;
        foreach (string component in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            attrs = File.GetAttributes(current);
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        return true;
    }

    private sealed record RegistryValue(uint Kind, byte[] Data)
    {
        internal string? Command
        {
            get
            {
                if (Kind is not (RegSz or RegExpandSz) || Data.Length % sizeof(char) != 0) return null;
                try { return StrictUtf16.GetString(Data).TrimEnd('\0'); }
                catch (DecoderFallbackException) { return null; }
            }
        }
    }

    private static RegistryValue? ReadValue(SafeRegistryHandle key, string name)
    {
        uint size = 0;
        int status = RegQueryValueEx(key, name, IntPtr.Zero, out uint kind, null, ref size);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (status == ErrorFileNotFound) return null;
            if (status != ErrorMoreData) CheckError(status);
            if (size > 65536) throw new IOException("注册表值超过可读取的大小。");
            var data = new byte[size];
            status = RegQueryValueEx(key, name, IntPtr.Zero, out kind, data, ref size);
            if (status == 0)
                return new(kind, data.AsSpan(0, (int)size).ToArray());
        }
        if (status == ErrorFileNotFound) return null;
        CheckError(status);
        throw new IOException("注册表值正在变化。");
    }

    private static bool SameValue(RegistryValue? actual, uint kind, byte[] expected) =>
        actual != null && actual.Kind == kind && actual.Data.AsSpan().SequenceEqual(expected);

    private static SafeRegistryHandle? OpenTransacted(SafeRegistryHandle root, string path, int access, RegistryView view,
        SafeFileHandle transaction)
    {
        int status = RegOpenKeyTransacted(root, path, 0, access | (int)view, out var key, transaction, IntPtr.Zero);
        if (status == 0) return key;
        key.Dispose();
        if (status != ErrorFileNotFound) CheckError(status);
        return null;
    }

    private static SafeRegistryHandle CreateRunKey(SafeRegistryHandle parent, SafeFileHandle transaction, out bool newKey)
    {
        int status = RegCreateKeyTransacted(parent, "Run", 0, null, 0, KeyQueryValue | KeySetValue | (int)NativeView,
            IntPtr.Zero, out var key, out uint disposition, transaction, IntPtr.Zero);
        newKey = disposition == 1; // REG_CREATED_NEW_KEY
        if (status == 0) return key;
        key.Dispose();
        throw new Win32Exception(status);
    }

    private static string MachineRelativePath(string path, RegistryView view) =>
        view == RegistryView.Registry32 ? @"SOFTWARE\Wow6432Node\" + path[@"SOFTWARE\".Length..] : path;

    private static string MachineKeyName(string path, RegistryView view) => @"\REGISTRY\MACHINE\" + MachineRelativePath(path, view);
    private static string UserKeyName(string path) => @"\REGISTRY\USER\" + path;

    private static void EnsureKeyName(SafeRegistryHandle key, string expected)
    {
        // A loaded user hive can include symbolic keys. Validate the opened handle itself before reading or writing it.
        var name = new byte[sizeof(uint) + expected.Length * sizeof(char) + 32];
        int status = NtQueryKey(key, KeyNameInformation, name, (uint)name.Length, out uint returned);
        if (status != 0 || returned < sizeof(uint))
            throw new IOException("无法确认打开的注册表路径。");
        uint bytes = BinaryPrimitives.ReadUInt32LittleEndian(name);
        if (bytes != expected.Length * sizeof(char) || bytes > returned - sizeof(uint) ||
            !Encoding.Unicode.GetString(name, sizeof(uint), (int)bytes).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("注册表路径已重定向，无法安全地继续。");
    }

    private static bool IsReadError(Exception ex) =>
        ex is UnauthorizedAccessException or SecurityException or IOException or ArgumentException or InvalidOperationException or NotSupportedException;

    private static StartupMigrationResult Failed(string message) => new(false, message);

    private static void CheckError(int status)
    {
        if (status != 0) throw new Win32Exception(status);
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryKey(SafeRegistryHandle key, int informationClass, [Out] byte[] information,
        uint length, out uint resultLength);

    [DllImport("ktmw32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateTransaction(IntPtr attributes, IntPtr unitOfWork, uint options,
        uint isolationLevel, uint isolationFlags, uint timeout, string? description);

    [DllImport("ktmw32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CommitTransaction(SafeFileHandle transaction);

    [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyTransactedW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegOpenKeyTransacted(SafeRegistryHandle root, string subkey, uint options,
        int access, out SafeRegistryHandle key, SafeFileHandle transaction, IntPtr extended);

    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyTransactedW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegCreateKeyTransacted(SafeRegistryHandle root, string subkey, uint reserved, string? keyClass,
        uint options, int access, IntPtr security, out SafeRegistryHandle key, out uint disposition,
        SafeFileHandle transaction, IntPtr extended);

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out uint kind,
        [Out] byte[]? data, ref uint size);

    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string name, uint reserved, uint kind, byte[] data, uint size);

    [DllImport("advapi32.dll", EntryPoint = "RegDeleteValueW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegDeleteValue(SafeRegistryHandle key, string name);
}
