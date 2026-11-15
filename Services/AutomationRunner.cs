using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using SoulLink.Models;

namespace SoulLink.Services
{
 public static class AutomationRunner
 {
  public static async Task RunAsync(AppConfig config, AutomationProfile profile, TextBlock? status = null)
  {
   var accountIds = config.Accounts.OrderBy(a => a.SortOrder).Select(a => a.Id).ToHashSet();
   var actions = profile.ActionIds.Select(id => config.Actions.FirstOrDefault(a => a.Id == id)).Where(a => a != null).ToList();
   int total = 0, success = 0;
   foreach (string accountId in config.Accounts.OrderBy(a => a.SortOrder).Where(a => profile.AccountIds.Contains(a.Id)).Select(a => a.Id))
   {
    foreach (var action in actions)
    {
     total++;
     var result = await Task.Run(() => ActionService.ExecuteAsync(action!, config)).ConfigureAwait(true);
     if (result.Ok) success++;
     LogBus.Write(result.Ok ? LogLevel.Ok : LogLevel.Error, "自动化 · " + action!.Name, result.Message);
     if (status != null) status.Text = action.Name + "：" + (result.Ok ? "成功" : "失败") + " · " + result.Message;
    }
   }
   profile.LastRun = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); ConfigStore.Save(config);
   if (status != null) status.Text = "流程完成：" + success + "/" + total + " 成功；失败已记录日志，后续步骤继续执行。";
  }
 }

 public sealed class AutomationScheduler : IDisposable
 {
  private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
  private readonly System.Collections.Generic.HashSet<string> _running = new(StringComparer.Ordinal);
  public AutomationScheduler() { _timer.Tick += async (_, __) => await TickAsync(); _timer.Start(); }
  private async Task TickAsync()
  {
   foreach (var profile in App.Config.AutomationProfiles.Where(p => p.Enabled))
   {
    if (!TimeSpan.TryParseExact(profile.DailyTime, @"hh\:mm", null, out var time)) continue;
    var now = DateTime.Now; string due = now.ToString("yyyy-MM-dd") + " " + profile.DailyTime;
    if (now.Hour != time.Hours || now.Minute != time.Minutes || profile.LastRun.StartsWith(due, StringComparison.Ordinal)) continue;
    if (!_running.Add(profile.Id)) continue;
    try { await AutomationRunner.RunAsync(App.Config, profile); } catch (Exception ex) { LogBus.Error("定时自动化失败", ex.Message); }
    finally { _running.Remove(profile.Id); }
   }
  }
  public void Dispose() => _timer.Stop();
 }
}