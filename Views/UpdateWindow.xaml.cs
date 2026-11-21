using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 更新对话框。展示版本对比与更新说明，用户确认后下载并交棒给独立更新器。
    ///
    /// ★ 只能通过 ShowFor(owner, manifest) 弹出；外部不应直接 new。
    /// </summary>
    public partial class UpdateWindow : Window
    {
        private readonly CloudUpdater.Manifest _manifest;
        private CancellationTokenSource? _cts;
        private bool _busy;

        private UpdateWindow(CloudUpdater.Manifest manifest)
        {
            InitializeComponent();
            _manifest = manifest;

            LocalVersionText.Text = "v" + CloudUpdater.CurrentVersion;
            RemoteVersionText.Text = "v" + manifest.Version;
            NotesText.Text = string.IsNullOrWhiteSpace(manifest.Notes)
                ? "本次更新没有提供说明。"
                : manifest.Notes.Replace("\r\n", "\n").Trim();

            Loaded += (_, _) => BtnConfirm.Focus();
        }

        /// <summary>弹出更新对话框；用户选择更新时返回 true。</summary>
        public static bool ShowFor(Window? owner, CloudUpdater.Manifest manifest)
        {
            var w = new UpdateWindow(manifest);
            if (owner != null && owner.IsVisible) w.Owner = owner;
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return w.ShowDialog() == true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                // 下载中：先中止下载，不关窗口，让用户看到已取消
                try { _cts?.Cancel(); } catch { }
                return;
            }
            DialogResult = false;
            Close();
        }

        private async void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            BtnConfirm.IsEnabled = false;
            BtnCancel.Content = "取消";
            ErrorText.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressBar.Value = 0;
            ProgressText.Text = "正在连接…";

            _cts = new CancellationTokenSource();
            var progress = new Progress<double>(p =>
            {
                if (p < 0) { ProgressText.Text = "正在下载…"; return; }
                ProgressBar.Value = p * 100;
                ProgressText.Text = "正在下载 " + (p * 100).ToString("0") + "%";
            });

            try
            {
                string file = await CloudUpdater.DownloadAsync(_manifest, progress, _cts.Token)
                                                .ConfigureAwait(true);
                ProgressText.Text = "下载完成，正在准备更新…";
                LaunchUpdater(file);
            }
            catch (OperationCanceledException)
            {
                ProgressText.Text = "已取消";
                ResetBusy();
            }
            catch (Exception ex)
            {
                // ★ 不把内部地址/技术细节暴露给用户，只给可读结论
                ErrorText.Text = FriendlyError(ex);
                ErrorText.Visibility = Visibility.Visible;
                ProgressPanel.Visibility = Visibility.Collapsed;
                ResetBusy();
            }
        }

        private void ResetBusy()
        {
            _busy = false;
            BtnConfirm.IsEnabled = true;
            BtnConfirm.Content = "重试";
            BtnCancel.Content = "取消";
        }

        /// <summary>把内部异常翻译成用户能看懂的说法（不泄露发布地址）。</summary>
        private static string FriendlyError(Exception ex)
        {
            if (ex is InvalidOperationException &&
                ex.Message.Contains("校验", StringComparison.Ordinal))
                return "下载的文件校验不通过，请重试。";
            if (ex is HttpRequestException || ex is TaskCanceledException)
                return "网络连接失败，请检查网络后重试。";
            if (ex is IOException || ex is UnauthorizedAccessException)
                return "无法写入临时文件，请检查磁盘空间或安全软件拦截。";
            return "更新失败，请稍后重试。";
        }

        private void LaunchUpdater(string downloadedPackage)
        {
            try
            {
                string self = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(self))
                {
                    ErrorText.Text = "无法定位当前程序，请手动下载更新。";
                    ErrorText.Visibility = Visibility.Visible;
                    ResetBusy();
                    return;
                }

                string dir = Path.GetDirectoryName(self) ?? "";
                string updater = Path.Combine(dir, "SoulLink.Updater.exe");
                if (!File.Exists(updater))
                {
                    ErrorText.Text = "更新组件缺失，请重新安装完整版本。";
                    ErrorText.Visibility = Visibility.Visible;
                    ResetBusy();
                    return;
                }

                var psi = new ProcessStartInfo(updater)
                {
                    UseShellExecute = true,
                    WorkingDirectory = dir,
                    Arguments = string.Join(" ",
                        "--pid", Environment.ProcessId.ToString(),
                        "--root", Quote(dir),
                        "--package", Quote(downloadedPackage))
                };
                Process.Start(psi);

                DialogResult = true;
                Close();
                Application.Current?.Shutdown();
            }
            catch (Exception)
            {
                ErrorText.Text = "无法启动更新程序，请稍后重试。";
                ErrorText.Visibility = Visibility.Visible;
                ResetBusy();
            }
        }

        private static string Quote(string s) => "\"" + s + "\"";
    }
}
