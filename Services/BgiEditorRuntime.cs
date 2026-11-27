using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace SoulLink.Services;

internal sealed class EditorFault(int status, string message) : Exception(message)
{
    internal int Status => status;
}

internal static class BgiEditorRuntime
{
    internal static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static EditorFault Unknown() => new(503, "无法确认账号、会话、路径或进程归属，请刷新后重试");

    internal static Target Resolve(string user)
    {
        if (string.IsNullOrWhiteSpace(user) || user != user.Trim() || user.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new EditorFault(400, "须提供有效账号");
        var cfg = App.Config;
        var accounts = AccountScope.Accounts(cfg).Where(a => Equal(a.UserName, user)).ToArray();
        if (accounts.Length != 1) throw new EditorFault(404, "账号未配置或不在管理范围内");
        var bindings = cfg.Settings.BgiPaths.ToArray();
        var match = bindings.Where(p => Equal(p.Key, user)).ToArray();
        if (match.Length != 1 || !Path.IsPathFullyQualified(match[0].Value)) throw Unknown();
        string exe = Path.GetFullPath(match[0].Value);
        if (exe.StartsWith(@"\\") || !Equal(Path.GetFileName(exe), "BetterGI.exe") || !File.Exists(exe)) throw Unknown();
        if (bindings.Any(p => !Equal(p.Key, user) && Path.IsPathFullyQualified(p.Value) && Equal(Path.GetFullPath(p.Value), exe))) throw Unknown();
        CheckLocalPath(exe);
        var sessions = (WtsSessionQuery.Enumerate(includeDomain: true) ?? throw Unknown()).Where(s => Equal(s.UserName, user)).ToArray();
        if (sessions.Length > 1 || (sessions.Length == 1 && !Equal(sessions[0].Domain, Environment.MachineName))) throw Unknown();
        var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, user).Translate(typeof(SecurityIdentifier));
        return new Target(accounts[0].UserName, exe, sessions.Length == 0 ? -1 : sessions[0].Id, sid.Value);
    }

    private static void CheckLocalPath(string path)
    {
        for (string? item = path; item != null; item = Path.GetDirectoryName(item))
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw Unknown();
    }

    internal sealed record Target(string User, string Exe, int SessionId, string OwnerSid)
    {
        private string UserDir => Path.Combine(Path.GetDirectoryName(Exe)!, "User");
        internal void RequireUnchanged()
        {
            if (Resolve(User) != this) throw Unknown();
        }

        // Hold a kernel handle through the action. Windows cannot recycle the PID until it closes.
        internal Process? Inspect()
        {
            var processes = Process.GetProcessesByName("BetterGI");
            Process? found = null;
            try
            {
                foreach (var p in processes)
                {
                    _ = p.SafeHandle;
                    var owner = BrokerSecurity.InspectProcess(p.Id);
                    var image = p.MainModule?.FileName ?? throw Unknown();
                    bool pathMatches = Equal(Path.GetFullPath(image), Exe);
                    if (owner.SessionId != SessionId && owner.Sid != OwnerSid && !pathMatches) continue;
                    if (owner.SessionId != SessionId || owner.Sid != OwnerSid || !pathMatches || p.HasExited || found != null) throw Unknown();
                    found = p;
                }
                return found;
            }
            catch { found = null; throw; }
            finally { foreach (var p in processes) if (p != found) p.Dispose(); }
        }

        internal FileStream Lock()
        {
            string path = Path.Combine(UserDir, ".soullink-editor.lock");
            CheckLocalPath(UserDir);
            if (File.Exists(path)) CheckLocalPath(path);
            try { return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new EditorFault(409, "账号正忙或无法取得编辑锁"); }
        }

        internal FileStream OpenDragon(string name)
        {
            if (!SafeName(name)) throw new EditorFault(400, "一条龙名称无效");
            string path = Path.Combine(UserDir, "OneDragon", name + ".json");
            if (!File.Exists(path)) throw new EditorFault(404, "该账号没有此一条龙配置");
            CheckLocalPath(path);
            return File.OpenRead(path);
        }

        internal async Task Stop(Process target)
        {
            CheckLocalPath(Path.Combine(UserDir, "config.json"));
            var data = BgiDataCenter.ReadFiles(User, Exe);
            string key = data.CancelTaskHotkey;
            if (data.Error.Length != 0 || HotkeySpec.Parse(key) is not { IsMouse: false, Key: > 0 })
                throw new EditorFault(409, "无法读取有效的停止当前脚本快捷键");
            string path = BgiLogWatcher.FindTodayLog(Exe);
            if (!File.Exists(path)) throw new EditorFault(409, "缺少今日日志，无法确认停止快捷键");
            CheckLocalPath(path);
            using var log = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long offset = log.Seek(0, SeekOrigin.End);
            RequireExact(target);
            var baseline = DateTime.Now;
            var sent = await BgiTaskPreparer.SendStopHotkeyAsync(SessionId, target.Id, key, default);
            if (!sent.Sent) throw new EditorFault(409, "停止快捷键投递失败");
            var timer = Stopwatch.StartNew();
            while (!target.HasExited && timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                long count = log.Length - offset;
                if (count is < 0 or > 262144) throw Unknown();
                if (count > 0)
                {
                    log.Position = offset;
                    byte[] bytes = new byte[(int)count];
                    await log.ReadExactlyAsync(bytes);
                    string text = Encoding.UTF8.GetString(bytes);
                    if (BgiTaskPreparer.HasHotkeyPressEvidence(text, baseline, key) || BgiTaskPreparer.HasScriptStopEvidence(text, baseline))
                    {
                        RequireExact(target);
                        target.Kill(entireProcessTree: false);
                        await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                        break;
                    }
                }
                await Task.Delay(250);
            }
            if (!target.HasExited) throw new EditorFault(409, "未收到停止快捷键确认，已保留 BGI 进程");
            RequireUnchanged();
            using var remaining = Inspect();
            if (remaining != null) throw Unknown();
        }

        private void RequireExact(Process held)
        {
            RequireUnchanged();
            using var current = Inspect();
            if (held.HasExited || current?.Id != held.Id) throw Unknown();
        }
    }

    internal static bool SafeName(string name) => name.Length is > 0 and <= 128 && name == name.Trim()
        && name[0] != '-' && !name.EndsWith('.') && name is not ("." or "..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Any(char.IsControl);
}
