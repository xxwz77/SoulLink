using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SoulLink.Services;

internal static class BgiLocalPackages
{
    private static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    private const uint DoNotVerifyKnownFolder = 0x4000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributes = 0x80;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint OpenExisting = 3;
    private const int FileDispositionInfo = 4;
    private const int FileIdInfo = 18;

    internal static string DefaultDirectory { get; } = ResolveDefaultDirectory();

    internal static IReadOnlyList<string> Scan(string directory, IEnumerable<string> pinned, CancellationToken ct = default)
    {
        var files = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(directory))
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
            foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (IsPackageName(file.Name) && file.Length > 0)
                    files.TryAdd(file.FullName, file.LastWriteTimeUtc);
            }
        }

        foreach (string path in pinned)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path) || System.IO.Path.GetExtension(path).ToLowerInvariant() is not
                (".zip" or ".7z" or ".rar" or ".tar" or ".gz")) continue;
            var file = new FileInfo(path);
            if (file.Length > 0)
                files.TryAdd(file.FullName, file.LastWriteTimeUtc);
        }

        return files.OrderByDescending(file => file.Value)
            .ThenBy(file => file.Key, StringComparer.OrdinalIgnoreCase).Select(file => file.Key).ToArray();
    }

    internal static async Task<DownloadedPackage> SaveDownloadedAsync(string stagedArchive, string destinationDirectory,
        string requestedFileName, CancellationToken ct = default)
    {
        if (!IsPackageName(requestedFileName) || requestedFileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("下载包文件名无效。", nameof(requestedFileName));
        ct.ThrowIfCancellationRequested();
        string directory = System.IO.Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory, ".soullink-bgi-" + Guid.NewGuid().ToString("N") + ".partial");
        FileId? createdId = null;
        try
        {
            await using (var input = new FileStream(stagedArchive, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (input.Length == 0) throw new InvalidDataException("下载的安装包为空。");
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                createdId = ReadId(output.SafeFileHandle);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            string stem = System.IO.Path.GetFileNameWithoutExtension(requestedFileName);
            string extension = System.IO.Path.GetExtension(requestedFileName);
            for (int index = 1; ; index++)
            {
                ct.ThrowIfCancellationRequested();
                string name = index == 1 ? requestedFileName : $"{stem} ({index}){extension}";
                string destination = System.IO.Path.Combine(directory, name);
                try
                {
                    File.Move(temporary, destination, overwrite: false);
                    using var file = OpenForIdentity(destination, delete: false);
                    var identity = createdId.HasValue && ReadId(file) == createdId ? ReadIdentity(file) : null;
                    return new DownloadedPackage(destination, identity);
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 80 or 183)
                {
                    // Another file already owns the name, including one created during this attempt.
                }
            }
        }
        finally
        {
            if (createdId.HasValue)
            {
                using var partial = OpenForIdentity(temporary, delete: true);
                if (ReadId(partial) == createdId)
                    TryMarkForDeletion(partial);
            }
        }
    }

    internal sealed class DownloadedPackage(string path, FileIdentity? identity)
    {
        public string Path { get; } = path;

        internal bool DeleteIfUnchanged()
        {
            if (!identity.HasValue) return false;
            using var file = OpenForIdentity(Path, delete: true);
            return ReadIdentity(file) == identity && TryMarkForDeletion(file);
        }
    }

    private static bool IsPackageName(string name)
    {
        string extension = System.IO.Path.GetExtension(name);
        if (!extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return false;
        string stem = name[..^extension.Length];
        foreach (string product in new[] { "BetterGI", "NexusBGI" })
        {
            if (!stem.StartsWith(product, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = stem.AsSpan(product.Length);
            if (suffix.IsEmpty || suffix[0] is '_' or '-' or '.' or ' ' or '(' || char.IsAsciiDigit(suffix[0]) ||
                (suffix.Length > 1 && suffix[0] is 'v' or 'V' && char.IsAsciiDigit(suffix[1])))
                return true;
        }
        return false;
    }

    private static string ResolveDefaultDirectory()
    {
        IntPtr path = IntPtr.Zero;
        try
        {
            Guid folder = Downloads;
            if (SHGetKnownFolderPath(in folder, DoNotVerifyKnownFolder, IntPtr.Zero, out path) >= 0 &&
                Marshal.PtrToStringUni(path) is { Length: > 0 } knownPath)
                return knownPath;
        }
        finally { Marshal.FreeCoTaskMem(path); }

        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    private static SafeFileHandle OpenForIdentity(string path, bool delete) =>
        CreateFileW(path, ReadAttributes | (delete ? DeleteAccess : 0), (uint)FileShare.Read,
            IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);

    private static FileId? ReadId(SafeFileHandle file)
    {
        if (file.IsInvalid || !GetFileInformationByHandleEx(file, FileIdInfo, out var info, (uint)Marshal.SizeOf<FileId>()))
            return null;
        return info;
    }

    private static FileIdentity? ReadIdentity(SafeFileHandle file)
    {
        if (ReadId(file) is not { } id || !GetFileInformationByHandle(file, out var info) ||
            (info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return null;
        return new FileIdentity(id, ((ulong)info.SizeHigh << 32) | info.SizeLow,
            info.CreationHigh, info.CreationLow, info.WriteHigh, info.WriteLow);
    }

    private static bool TryMarkForDeletion(SafeFileHandle file)
    {
        byte delete = 1;
        return !file.IsInvalid && SetFileInformationByHandle(file, FileDispositionInfo, ref delete, sizeof(byte));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct FileId(ulong Volume, ulong IdLow, ulong IdHigh);

    internal readonly record struct FileIdentity(FileId Id, ulong Size, uint CreationHigh, uint CreationLow,
        uint WriteHigh, uint WriteLow);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, IntPtr token, out IntPtr path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileId info, uint bytes);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte info, uint bytes);
}
