using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SoulLink.Services;

namespace SoulLink.Views
{
    public partial class CliPage : UserControl
    {
        public CliPage()
        {
            InitializeComponent();
        }

        public void Activate()
        {
            string exe = CommandBuilder.ExePath;
            SoulLink.Models.ActionItem? first = App.Config.Actions.FirstOrDefault();
            string comma = " ";

            SampleOne.Text = first != null
                ? CommandBuilder.ForAction(first)
                : CommandBuilder.Quote(exe) + " open --user A2 --exe " +
                  CommandBuilder.Quote(@"D:\AAA挂机专区\A2\BetterGI\BetterGI.exe") + " --quiet";

            SampleOpen.Text = CommandBuilder.Quote(exe) + comma + "open  --user A2 --exe " +
                              CommandBuilder.Quote(@"D:\AAA挂机专区\A2\BetterGI\BetterGI.exe");
            SampleClose.Text = CommandBuilder.Quote(exe) + comma + "close --user A2 --exe " +
                               CommandBuilder.Quote(@"D:\AAA挂机专区\A2\BetterGI\BetterGI.exe");
            SampleRestart.Text = CommandBuilder.Quote(exe) + comma + "restart --user A2 --exe " +
                                 CommandBuilder.Quote(@"D:\AAA挂机专区\A2\BetterGI\BetterGI.exe");
        }

        private void CopySampleOne_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(SampleOne.Text); OutputText.Text = "已复制到剪贴板。"; } catch { }
        }

        private void CopyFull_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(CommandBuilder.Quote(CommandBuilder.ExePath) + " " + CmdBox.Text.Trim());
                OutputText.Text = "已复制完整命令行。";
            }
            catch { }
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            string raw = (CmdBox.Text ?? "").Trim();
            if (raw.Length == 0) { OutputText.Text = "请输入参数。"; return; }

            OutputText.Text = "⟳ 正在执行…";
            var sw = Stopwatch.StartNew();

            // 拆参数（支持引号）
            var parts = Regex.Matches(raw, @"""([^""]*)""|\S+")
                             .Cast<Match>()
                             .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Value)
                             .ToArray();

            var parsed = CommandLine.Parse(parts);
            var req = CommandLine.ToRequest(parsed);

            var resp = await Task.Run(() => RequestHandler.HandleAsync(req, App.Config));

            sw.Stop();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine((resp.Ok ? "✓ 成功" : "✕ 失败") + "   耗时 " + sw.ElapsedMilliseconds + " ms   退出码 " + resp.ExitCode);
            sb.AppendLine();
            sb.AppendLine("消息：" + resp.Message);
            if (!string.IsNullOrWhiteSpace(resp.Command))
            {
                sb.AppendLine();
                sb.AppendLine("实际命令：");
                sb.AppendLine(ProcessService.FirstLine(resp.Command));
            }
            foreach (var d in resp.Detail)
                sb.AppendLine("· " + d);

            OutputText.Text = sb.ToString().TrimEnd();
            OutputText.Foreground = resp.Ok
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9D, 0xFF, 0xE0))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB4, 0xC4));
        }
    }
}