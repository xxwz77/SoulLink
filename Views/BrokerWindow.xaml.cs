using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
namespace SoulLink.Views
{
    public partial class BrokerWindow : Window
    {
        private bool _busy;
        public BrokerWindow()
        {
            InitializeComponent(); InstallPath.Text = Services.BrokerInstall.Root;
            Diagnostics.Text = "等待检测结果。";
            Loaded += async (_, _) => await DetectAsync();
        }
        private void DragArea(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            SoulLink.Services.InputTrace.Write("BROKER_WIN_CLOSE", "弹窗按 X 关闭 _busy=" + _busy);
            Close();
        }
        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(StatusTitle.Text + "\n" + StatusText.Text + "\n" + InstallPath.Text + "\n" + Diagnostics.Text); } catch { }
        }
        private async void Detect_Click(object sender, RoutedEventArgs e) => await DetectAsync();
        private async void Install_Click(object sender, RoutedEventArgs e) => await RunAsync("install");
        private async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (!ThemedMessage.Confirm(this, "卸载测试服务", "仅卸载测试版 Broker，不修改挂机版。确定继续吗？", true)) return;
            await RunAsync("uninstall");
        }
        private void Show(string title, string detail, string diagnostics, bool ok)
        {
            StatusTitle.Text = title; StatusText.Text = detail; Diagnostics.Text = diagnostics;
            StatusDot.Fill = new SolidColorBrush(ok ? Color.FromRgb(0x41, 0xF0, 0xC0) : Color.FromRgb(0xFF, 0xB1, 0x4D));
        }
        private async Task DetectAsync()
        {
            if (_busy) return; _busy = true;
            try
            {
                Show("正在检测服务…", "查询测试版服务与本机通道。", "等待响应。", false);
                var result = await Services.BrokerProtocol.SendAsync(new Services.BrokerProtocol.Request { Command = "health" }, 2500);
                if (result.Ok) Show("服务已就绪", result.Message, "服务：" + Services.BrokerProtocol.ServiceName, true);
                else if (result.Code == "NOT_INSTALLED") Show("尚未安装服务", "请点击下方「安装 / 修复服务」。", "当前没有检测到已运行的 Broker。不会自动修改系统。", false);
                else if (result.Code == "NOT_RUNNING") Show("服务已停止", "Broker 已安装，但当前没有运行。请点击「安装 / 修复服务」重新启动。", "错误代码：NOT_RUNNING", false);
                else Show("服务不可用", result.Message, "错误代码：" + result.Code, false);
            }
            catch (Exception ex) { Show("检测失败", ex.Message, ex.ToString(), false); }
            finally { _busy = false; }
        }
        private async Task RunAsync(string action)
        {
            if (_busy) return; _busy = true;
            try
            {
                string exe = Services.SystemLauncher.ResolveSelfExe();
                if (action != "install")
                {
                    string setupPath = Path.Combine(Path.GetDirectoryName(exe) ?? "", "tools", "SoulLink.BrokerSetup.exe");
                    var old = Process.Start(new ProcessStartInfo(setupPath, "--uninstall") { UseShellExecute = true, Verb = "runas" });
                    if (old == null) throw new InvalidOperationException("卸载工具未启动");
                    await old.WaitForExitAsync();
                    if (old.ExitCode != 0) throw new InvalidOperationException("卸载退出码：" + old.ExitCode);
                    Show("测试服务已卸载", "挂机版未修改。", "可在需要时重新安装。", true); return;
                }
                string setup = Path.Combine(Path.GetDirectoryName(exe) ?? "", "tools", "SoulLink.BrokerSetup.exe");
                if (!File.Exists(setup))
                {
                    // ★ 发布包里没带独立安装器时，给出可执行的说明，而不是一句"找不到"。
                    //   用户实测反馈：只看到「找不到独立安装器 C:\Program Files\SoulLinkDev」，
                    //   完全不知道该怎么办。
                    Show("缺少独立安装器",
                        "当前这份程序里没有 tools\\SoulLink.BrokerSetup.exe。",
                        "需要它才能安装后台服务（它负责写服务、配置 ACL，必须单独分发）。\n\n"
                        + "解决办法：\n"
                        + "  · 使用带 tools 子目录的完整安装包；或\n"
                        + "  · 把 SoulLink.BrokerSetup.exe 放到：" + Path.GetDirectoryName(setup)
                        + "\n\n"
                        + "不装后台服务也能正常使用本程序，只是「跨会话执行」需要管理员手动启动。",
                        false);
                    return;
                }
                Show("正在安装服务", "停止旧服务、更新文件并启动。", "只操作测试版目录，不修改挂机版或 UAC。", false);
                var process = Process.Start(new ProcessStartInfo(setup, "--install \"" + exe + "\"") { UseShellExecute = true, Verb = "runas" });
                if (process == null) throw new InvalidOperationException("独立安装器未启动");
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new InvalidOperationException("安装器退出码：" + process.ExitCode + "（0x" + unchecked((uint)process.ExitCode).ToString("X8") + "）");
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    var r = await Services.BrokerProtocol.SendAsync(new Services.BrokerProtocol.Request { Command = "health" }, 1200);
                    if (r.Ok)
                    {
                        // ★ 服务已更新：清掉版本闸门缓存，否则 30 秒内仍按旧版本拒绝投送。
                        Services.BrokerProtocol.InvalidateServiceVersionCache();

                        // ★ 再通知中控台重新投送。
                        //   原来这里只弹个框就返回，中控台完全不知道服务换了 ——
                        //   那些账号仍被记在"已处理"里，于是永远不再重试，
                        //   表现就是「安装服务后没有自动更新并启用投送」（用户实测）。
                        Services.BrokerProtocol.NotifyServiceUpdated();

                        Show("服务已就绪", r.Message, "安装目录：" + Services.BrokerInstall.Root
                            + "\n\n通道已刷新，正在重新投送各账号。", true);
                        return;
                    }
                    await Task.Delay(350);
                }
                var last = await Services.BrokerProtocol.SendAsync(new Services.BrokerProtocol.Request { Command = "health" }, 1500);
                throw new InvalidOperationException("健康检查失败：" + last.Code + " · " + last.Message);
            }
            catch (Exception ex)
            {
                string log = Path.Combine(Path.GetTempPath(), "SoulLink-Broker-install-current.log");
                string text = File.Exists(log) ? File.ReadAllText(log) : "没有本次安装日志。";
                Show("操作未完成", ex.Message, "日志：" + log + "\n" + text, false);
            }
            finally { _busy = false; }
        }
    }
}