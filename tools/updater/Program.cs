using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Forms;

namespace SoulLink.Updater
{
    /// <summary>
    /// 独立更新器：等主程序退出 → 解压更新包覆盖安装目录 → 重新启动。
    ///
    /// 之所以要独立进程：Windows 不允许覆盖正在运行的可执行文件。
    /// 主程序下载完新版本后启动本程序、随即退出，由本程序完成替换。
    ///
    /// ★ 支持更新**任意文件**（主程序、Agent、Broker 安装器、脚本等），
    ///   因为更新包是一个 zip，按包内相对路径覆盖到安装目录。
    ///
    /// ★ 安全措施：
    ///   1. 绝不允许包内路径跳出安装目录（zip-slip 防护）
    ///   2. 重命名正在使用的 exe 而非删除，失败可回滚
    ///   3. 全部替换成功后才清理备份
    ///
    /// 用法：
    ///   SoulLink.Updater.exe --pid 1234 --root "C:\...\SoulLink" --package "%TEMP%\pkg.zip"
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string? pidText = null, rootDir = null, package = null;
            string exeName = "SoulLink.exe";
            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--pid": pidText = args[++i]; break;
                    case "--root": rootDir = args[++i]; break;
                    case "--package": package = args[++i]; break;
                    case "--exe": exeName = args[++i]; break;
                }
            }

            if (string.IsNullOrWhiteSpace(rootDir) || string.IsNullOrWhiteSpace(package))
                return ShowError("更新参数不完整，无法继续。");
            if (!File.Exists(package))
                return ShowError("找不到已下载的更新包。");

            rootDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDir));
            if (!Directory.Exists(rootDir))
                return ShowError("找不到程序安装目录。");
            if (Path.GetFileName(exeName) != exeName || exeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return ShowError("无法识别要更新的程序文件。");

            if (!int.TryParse(pidText, out int pid) || pid <= 0 || !WaitForExit(pid, TimeSpan.FromSeconds(30)))
                return ShowError("SoulLink 尚未退出，请关闭主程序后重新检查更新。");

            try
            {
                ApplyPackage(package, rootDir, exeName);
            }
            catch (Exception ex)
            {
                return ShowError("更新失败，已尝试回滚。\n\n" + ex.Message);
            }

            // 4. 清理备份与临时文件
            try
            {
                File.Delete(package);
                string pkgDir = Path.GetDirectoryName(package) ?? "";
                if (Path.GetFileName(pkgDir).StartsWith("SoulLinkUpdate-", StringComparison.Ordinal)
                    && Directory.Exists(pkgDir) && !Directory.EnumerateFileSystemEntries(pkgDir).Any())
                    Directory.Delete(pkgDir);
            }
            catch { }

            // 5. 重启
            try
            {
                string mainExe = Path.Combine(rootDir, exeName);
                Process.Start(new ProcessStartInfo(mainExe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = rootDir
                });
            }
            catch (Exception ex)
            {
                return ShowError("更新已完成，但无法自动启动程序：\n" + ex.Message);
            }

            return 0;
        }

        private static void ApplyPackage(string package, string rootDir, string exeName)
        {
            var work = Directory.CreateTempSubdirectory("SoulLinkUpdateStage-");
            var changed = new List<(string Destination, string? Backup)>();
            string suffix = ".old-" + Guid.NewGuid().ToString("N");
            try
            {
                using var installLock = new FileStream(Path.Combine(rootDir, ".soullink-update.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                ZipFile.ExtractToDirectory(package, work.FullName);
                var main = new FileInfo(Path.Combine(work.FullName, "SoulLink.exe"));
                if (!main.Exists || main.Length == 0) throw new IOException("更新包缺少主程序。");
                try
                {
                    foreach (var source in Directory.EnumerateFiles(work.FullName, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(work.FullName, source);
                        string destination = Path.Combine(rootDir, relative.Equals("SoulLink.exe", StringComparison.OrdinalIgnoreCase)
                            ? exeName : relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        string? backup = File.Exists(destination) ? destination + suffix : null;
                        if (backup != null) File.Move(destination, backup);
                        changed.Add((destination, backup));
                        File.Copy(source, destination);
                    }
                }
                catch
                {
                    for (int i = changed.Count - 1; i >= 0; i--)
                    {
                        var (destination, backup) = changed[i];
                        try
                        {
                            File.Delete(destination);
                            if (backup != null) File.Move(backup, destination);
                        }
                        catch { }
                    }
                    throw;
                }
                foreach (var (_, backup) in changed)
                    if (backup != null) try { File.Delete(backup); } catch { }
            }
            finally { try { work.Delete(recursive: true); } catch { } }
        }

        /// <summary>等待指定进程退出。返回 true = 已退出（或本就不存在）。</summary>
        private static bool WaitForExit(int pid, TimeSpan timeout)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.WaitForExit((int)timeout.TotalMilliseconds);
            }
            catch (ArgumentException) { return true; }   // 进程不存在 = 已退出
            catch { return false; }
        }

        private static int ShowError(string message)
        {
            MessageBox.Show(message, "SoulLink 更新",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }
}
