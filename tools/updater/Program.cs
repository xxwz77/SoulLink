using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
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
            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--pid": pidText = args[++i]; break;
                    case "--root": rootDir = args[++i]; break;
                    case "--package": package = args[++i]; break;
                }
            }

            if (string.IsNullOrWhiteSpace(rootDir) || string.IsNullOrWhiteSpace(package))
                return ShowError("更新参数不完整，无法继续。");
            if (!File.Exists(package))
                return ShowError("找不到已下载的更新包。");

            rootDir = Path.GetFullPath(rootDir);
            if (!Directory.Exists(rootDir))
                return ShowError("找不到程序安装目录。");

            // 等主程序退出
            if (int.TryParse(pidText, out int pid))
                WaitForExit(pid, TimeSpan.FromSeconds(30));

            // 文件锁释放有延迟（实测约 1~2 秒）
            Thread.Sleep(1500);

            string work = Path.Combine(Path.GetTempPath(), "SoulLinkUpdate", "stage");
            var replaced = new List<string>();
            var renamed = new List<(string Old, string New)>();

            try
            {
                // 1. 解压到暂存目录
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
                Directory.CreateDirectory(work);
                ZipFile.ExtractToDirectory(package, work, overwriteFiles: true);

                // 2. 逐个覆盖
                var files = Directory.GetFiles(work, "*", SearchOption.AllDirectories);
                if (files.Length == 0)
                    throw new IOException("更新包是空的");

                foreach (var src in files)
                {
                    string rel = Path.GetRelativePath(work, src);
                    string dst = Path.GetFullPath(Path.Combine(rootDir, rel));

                    // ★ zip-slip 防护：解析后的绝对路径必须仍在安装目录内
                    if (!dst.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(dst, rootDir, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("更新包含有非法路径：" + rel);
                    }

                    string? dir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    // 目标若是正在使用的文件，先改名（Windows 允许重命名运行中的 exe）
                    if (File.Exists(dst))
                    {
                        try
                        {
                            File.Copy(src, dst, overwrite: true);
                        }
                        catch (IOException)
                        {
                            string bak = dst + ".old";
                            try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                            File.Move(dst, bak);
                            renamed.Add((dst, bak));
                            File.Copy(src, dst, overwrite: true);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            string bak = dst + ".old";
                            try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                            File.Move(dst, bak);
                            renamed.Add((dst, bak));
                            File.Copy(src, dst, overwrite: true);
                        }
                    }
                    else
                    {
                        File.Copy(src, dst, overwrite: true);
                    }
                    replaced.Add(rel);
                }

                // 3. 校验：主程序必须存在且非空
                string mainExe = Path.Combine(rootDir, "SoulLink.exe");
                var fi = new FileInfo(mainExe);
                if (!fi.Exists || fi.Length == 0)
                    throw new IOException("更新后主程序无效");
            }
            catch (Exception ex)
            {
                Rollback(replaced, renamed, rootDir);
                return ShowError("更新失败，已尝试回滚。\n\n" + ex.Message);
            }

            // 4. 清理备份与临时文件
            try
            {
                foreach (var (dst, bak) in renamed)
                {
                    try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                }
                Directory.Delete(work, recursive: true);
                File.Delete(package);
                string pkgDir = Path.GetDirectoryName(package) ?? "";
                if (pkgDir.EndsWith("SoulLinkUpdate", StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(pkgDir) && !Directory.EnumerateFileSystemEntries(pkgDir).Any())
                    Directory.Delete(pkgDir);
            }
            catch { }

            // 5. 重启
            try
            {
                string mainExe = Path.Combine(rootDir, "SoulLink.exe");
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

        /// <summary>尽量把已覆盖的文件恢复原状。</summary>
        private static void Rollback(List<string> replaced, List<(string Old, string New)> renamed, string rootDir)
        {
            foreach (var (dst, bak) in renamed)
            {
                try
                {
                    if (File.Exists(bak))
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(bak, dst);
                    }
                }
                catch { }
            }
        }

        private static void WaitForExit(int pid, TimeSpan timeout)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                p.WaitForExit((int)timeout.TotalMilliseconds);
            }
            catch (ArgumentException) { }
            catch { }
        }

        private static int ShowError(string message)
        {
            MessageBox.Show(message, "SoulLink 更新",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }
}
