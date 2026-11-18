using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SoulLink.Services
{
    public sealed record ProcessOwnerRecord(int Pid, string Name, int SessionId, string UserName);

    /// <summary>每轮只执行一次 tasklist，所有流程共享只读进程/所有者快照。</summary>
    public static class ProcessOwnerSnapshot
    {
        private static readonly object Sync = new();
        private static ProcessOwnerRecord[] _items = Array.Empty<ProcessOwnerRecord>();
        public static bool Available { get; private set; }
        public static DateTime CapturedAt { get; private set; } = DateTime.MinValue;
        public static long RefreshCount { get; private set; }
        public static IReadOnlyList<ProcessOwnerRecord> Items { get { lock (Sync) return _items; } }
        public static void Refresh()
        {
            string? text = null;
            try
            {
                using var p = Process.Start(new ProcessStartInfo("tasklist.exe", "/V /FO CSV /NH")
                { UseShellExecute=false, RedirectStandardOutput=true, CreateNoWindow=true, StandardOutputEncoding=SystemLauncher.ConsoleEncoding() });
                text = p?.StandardOutput.ReadToEnd();
                if (p != null && !p.WaitForExit(5000)) { try { p.Kill(); } catch { } }
            }
            catch { }
            if (text == null) { lock (Sync) Available = false; return; }
            Set(text, DateTime.Now);
        }
        public static void Set(string csv, DateTime? now = null)
        {
            var items = Parse(csv);
            lock (Sync)
            {
                _items = items; Available = true; CapturedAt = now ?? DateTime.Now; RefreshCount++;
            }
        }
        public static bool Exists(string processName, string userName, int? sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(processName) || (!sessionId.HasValue && string.IsNullOrWhiteSpace(userName))) return false;
            if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) processName = processName[..^4];
            string user = SessionService.BareName(userName);
            lock (Sync) return _items.Any(p => p.Name.Equals(processName, StringComparison.OrdinalIgnoreCase)
                && (!sessionId.HasValue || p.SessionId == sessionId.Value)
                && (sessionId.HasValue || string.Equals(SessionService.BareName(p.UserName), user, StringComparison.OrdinalIgnoreCase)));
        }
        internal static ProcessOwnerRecord[] Parse(string csv)
        {
            var result = new List<ProcessOwnerRecord>();
            using var reader = new StringReader(csv ?? "");
            while (reader.ReadLine() is string line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var f = Csv(line);
                if (f.Count < 7 || !int.TryParse(f[1], out int pid)) continue;
                int.TryParse(f[3], out int sid);
                result.Add(new ProcessOwnerRecord(pid, Path.GetFileNameWithoutExtension(f[0]), sid, f[6]));
            }
            return result.ToArray();
        }
        private static List<string> Csv(string line)
        {
            var values = new List<string>(); var value = new StringBuilder(); bool quoted = false;
            for (int i=0;i<line.Length;i++)
            {
                char c=line[i];
                if (c=='"') { if (quoted && i+1<line.Length && line[i+1]=='"') { value.Append('"'); i++; } else quoted=!quoted; }
                else if (c==',' && !quoted) { values.Add(value.ToString()); value.Clear(); }
                else value.Append(c);
            }
            values.Add(value.ToString()); return values;
        }
    }
}
