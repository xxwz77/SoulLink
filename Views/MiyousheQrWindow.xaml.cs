using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.IO;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 米游社扫码登录二维码窗口（主程序数据中心入口，需求 t33）。
    /// 只做展示与轮询；所有网络/IO 均异步，不阻塞 UI 线程。
    /// </summary>
    public partial class MiyousheQrWindow : Window
    {
        private readonly string _account;
        private readonly string _region;
        private readonly CancellationTokenSource _cts = new();
        private string _session = "";
        private bool _closed;

        public MiyousheQrWindow(string account, string region = "cn")
        {
            InitializeComponent();
            _account = account;
            _region = region == "os" ? "os" : "cn";
            AccountText.Text = account + " · " + (_region == "os" ? "国际服" : "国服");
            Loaded += async (_, __) => await BeginAsync();
            Closed += (_, __) => { _closed = true; try { _cts.Cancel(); } catch { } };
        }

        private async Task BeginAsync()
        {
            try
            {
                StatusText.Text = "正在生成二维码…";
                var qr = await MiyousheService.BeginQrAsync(_account, _region, _cts.Token).ConfigureAwait(true);
                _session = qr.Session;
                ShowQr(qr.QrPngBase64);
                StatusText.Text = "请用米游社 App 扫码，并在手机端确认";
                _ = PollLoopAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = "二维码生成失败：" + ex.Message;
            }
        }

        private void ShowQr(string pngBase64)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(pngBase64);
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                QrImage.Source = bmp;
            }
            catch { }
        }

        private async Task PollLoopAsync()
        {
            while (!_closed && !_cts.IsCancellationRequested)
            {
                try
                {
                    var r = await MiyousheService.PollQrAsync(_session, _cts.Token).ConfigureAwait(true);
                    switch (r.Status)
                    {
                        case "Confirmed":
                            StatusText.Text = "扫码成功，凭据已加密保存";
                            Close();
                            return;
                        case "Scanned":
                            StatusText.Text = "已扫码，请在手机上确认";
                            break;
                        default:
                            StatusText.Text = "等待扫码…";
                            break;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // 过期 / 取消 / 会话不存在 → 显示重新生成按钮
                    StatusText.Text = ex.Message;
                    BtnRegen.Visibility = Visibility.Visible;
                    return;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(true); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async void BtnRegen_Click(object sender, RoutedEventArgs e)
        {
            BtnRegen.Visibility = Visibility.Collapsed;
            await BeginAsync();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
