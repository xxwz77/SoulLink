using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SoulLink.Services;

// One gate for discovery, the collector, and consumers holding a dated path across midnight.
internal sealed class BgiDailyLogProbe
{
    internal const int RetryMs = 30_000;
    private const string Prefix = "better-genshin-impact";
    private readonly Dictionary<string, (long Until, bool Missing)> _retry = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, FileInfo?> _stat;
    private readonly string? _historyFile;
    private bool _loaded;

    internal BgiDailyLogProbe(Func<string, FileInfo?>? stat = null, string? historyFile = null)
    {
        _historyFile = historyFile;
        _stat = stat ?? (path => {
            var file = new FileInfo(path);
            // Exists silently maps access errors to false. Length retains the OS error.
            _ = file.Length;
            return file;
        });
    }

    internal static string ExpectedPath(string exe, DateTime day)
    {
        string? dir = Path.GetDirectoryName(exe);
        return string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, "log", Prefix
            + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
    }

    internal static bool IsHistory(string path, DateTime today)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase)
            && DateTime.TryParseExact(name.AsSpan(Prefix.Length), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day) && day < today.Date;
    }

    internal List<FileInfo> RecentHistory(string exe, int days, long now, DateTime today)
    {
        var files = new List<FileInfo>();
        // The window includes today, but the real-time collector exclusively owns it.
        for (int i = days - 1; i >= 1; i--)
        {
            string path = ExpectedPath(exe, today.AddDays(-i));
            if (Find(path, now, today, out _) is FileInfo file && file.Length > 0) files.Add(file);
        }
        return files;
    }

    internal FileInfo? Find(string path, long now, DateTime today, out bool missing)
    {
        missing = false;
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Path.GetFullPath(path);
        bool history = IsHistory(path, today);
        lock (_retry)
        {
            if (history)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    try { if (_historyFile != null) _history.UnionWith(File.ReadAllLines(_historyFile)); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                if (_history.Contains(path)) { missing = true; return null; }
            }
            if (_retry.TryGetValue(path, out var wait) && now < wait.Until)
            {
                missing = wait.Missing;
                return null;
            }
            try
            {
                var file = _stat(path);
                if (file != null) { _retry.Remove(path); return file; }
                missing = Directory.Exists(Path.GetDirectoryName(path));
            }
            catch (FileNotFoundException) { missing = Directory.Exists(Path.GetDirectoryName(path)); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }

            if (history && missing)
            {
                _history.Add(path);
                _retry.Remove(path);
                try { if (_historyFile != null) File.AppendAllLines(_historyFile, new[] { path }); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            else
            {
                if (_retry.Count >= 128)
                    foreach (var item in _retry)
                        if (item.Value.Until <= now) _retry.Remove(item.Key);
                _retry[path] = (now + RetryMs, missing);
            }
            return null;
        }
    }
}
