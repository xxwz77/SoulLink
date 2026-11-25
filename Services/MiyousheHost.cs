using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>米游社周期宿主。唯一调度源 TickHub，不新增 DispatcherTimer。</summary>
    public static class MiyousheHost
    {
        private static IDisposable? _tick;
        private static int _started;
        private static int _warned;
        internal static Func<CancellationToken,Task<IReadOnlyList<string>>>? AccountProviderForTest;
        internal static int TickCount { get; private set; }
        internal static int RunCount { get; private set; }
        internal static int LedgerRunCount { get; private set; }

        /// <summary>签到「当天已执行」标记：key = yyyy-MM-dd（本地日），value = 已触发过的账号集合。</summary>
        private static readonly Dictionary<string, HashSet<string>> SignDoneByDay = new(StringComparer.Ordinal);
        private static readonly object SignDoneLock = new();

        /// <summary>签到当日失败重试计数：key = account|day，value = 已重试次数。</summary>
        private static readonly Dictionary<string, int> SignRetryCount = new(StringComparer.Ordinal);
        private static readonly object SignRetryLock = new();

        /// <summary>每账号上次摩拉拉取时刻（本地时间），用于每小时一次的节流。</summary>
        private static readonly Dictionary<string, DateTime> LastLedgerPull = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object LedgerLock = new();

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started,1)!=0) return;
            try { _tick=TickHub.RegisterBackground("MiyousheHost.Daily",60,() => _=TickAsync()); }
            catch { App.AppendErrorLog("MiyousheHost.Start",new InvalidOperationException("米游社调度注册失败")); }
        }

        internal static async Task TickAsync(CancellationToken ct=default)
        {
            TickCount++;
            AppConfig? config=App.Config;
            if (config?.Settings == null) return;

            // ── 需求3：签到，每天 00:10 触发一次 ──
            if (config.Settings.MiyousheSignEnabled)
            {
                if (SignTimeReached(config.Settings))
                {
                    await RunSignOnceAsync(config, ct).ConfigureAwait(false);
                }
            }

            // ── 需求2：摩拉每小时拉取（与签到解耦）──
            if (config.Settings.MiyousheLedgerEnabled)
            {
                await RunLedgerIfDueAsync(config, ct).ConfigureAwait(false);
            }
        }

        /// <summary>解析签到时刻（HH:mm）；MiyousheSignTime 为空时回退 MiyousheSignHour。</summary>
        private static TimeSpan SignTimeOfDay(AppSettings s)
        {
            string t = (s.MiyousheSignTime ?? "").Trim();
            if (t.Length > 0
                && TimeSpan.TryParse(t, out var parsed)
                && parsed >= TimeSpan.Zero && parsed < TimeSpan.FromHours(24))
                return parsed;
            return TimeSpan.FromHours(Math.Clamp(s.MiyousheSignHour,0,23));
        }

        /// <summary>当前时间是否已过今天的目标签到时刻。</summary>
        private static bool SignTimeReached(AppSettings s)
            => DateTime.Now.TimeOfDay >= SignTimeOfDay(s);

        /// <summary>「今天尚未签到过该账号」判定（本地日期 + 已执行标记）。</summary>
        private static bool SignNotDoneToday(string account)
        {
            string day = DateTime.Now.ToString("yyyy-MM-dd");
            lock (SignDoneLock)
            {
                if (!SignDoneByDay.TryGetValue(day, out var set)) return true;
                return !set.Contains(account);
            }
        }

        private static void MarkSignDone(string account)
        {
            string day = DateTime.Now.ToString("yyyy-MM-dd");
            lock (SignDoneLock)
            {
                if (!SignDoneByDay.TryGetValue(day, out var set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); SignDoneByDay[day]=set; }
                set.Add(account);
            }
            // 只保留最近 3 天，防字典无限增长
            lock (SignDoneLock)
            {
                var stale = new List<string>();
                foreach (var k in SignDoneByDay.Keys)
                    if (string.CompareOrdinal(k, day) < -2) stale.Add(k);
                foreach (var k in stale) SignDoneByDay.Remove(k);
            }
        }

        private static async Task RunSignOnceAsync(AppConfig config, CancellationToken ct)
        {
            if (MiyousheService.Running!=0) return;
            try
            {
                var accounts = AccountProviderForTest != null
                    ? await AccountProviderForTest(ct).ConfigureAwait(false)
                    : await MiyousheStore.Shared.ListAccountsAsync(ct).ConfigureAwait(false);
                foreach (string account in accounts)
                {
                    if (!SignNotDoneToday(account)) continue;                 // ★ 当天已触发，不再试
                    if (config.Settings.MiyousheSignAccounts.TryGetValue(account,out bool enabled) && !enabled) continue;
                    if (!CanRetrySign(account)) continue;                     // ★ 重试限次
                    string region = config.Settings.MiyousheAccountRegions.TryGetValue(account,out var regionName)&&regionName=="os"?"os":"cn";
                    var credential = await MiyousheCredentialStore.LoadAsync(account,region,ct).ConfigureAwait(false);
                    if (credential==null) { MarkSignDone(account); continue; }  // 未登录也算「已处理」，当天不再打扰
                    RunCount++;
                    bool ok = false;
                    try
                    {
                        var result = await MiyousheService.RunForAccountAsync(account,true,false,ct).ConfigureAwait(false);
                        ok = result != null;
                    }
                    catch { ok = false; }
                    if (ok) MarkSignDone(account);
                    else RecordSignRetry(account);   // 失败：记一次重试，按 MiyousheRetryMinutes 退避
                    if (MiyousheService.DelayForTest==null) await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(2,6)),ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (Interlocked.Exchange(ref _warned,1)==0)
                    App.AppendErrorLog("MiyousheHost.Sign",new InvalidOperationException("米游社签到周期任务失败；未记录请求内容或凭据"));
            }
        }

        private static bool CanRetrySign(string account)
        {
            string key = account + "|" + DateTime.Now.ToString("yyyy-MM-dd");
            lock (SignRetryLock)
            {
                if (!SignRetryCount.TryGetValue(key, out int n)) { SignRetryCount[key]=0; return true; }
                return n < 5;   // ★ 最多 5 次重试，避免整天空转
            }
        }

        private static void RecordSignRetry(string account)
        {
            string key = account + "|" + DateTime.Now.ToString("yyyy-MM-dd");
            lock (SignRetryLock)
            {
                SignRetryCount.TryGetValue(key, out int n);
                SignRetryCount[key] = n + 1;
            }
        }

        // ── 摩拉每小时拉取（需求2）──────────────────────────────────

        private static async Task RunLedgerIfDueAsync(AppConfig config, CancellationToken ct)
        {
            if (MiyousheService.Running!=0) return;
            int interval = Math.Max(1, config.Settings.MiyousheLedgerIntervalMinutes);
            try
            {
                var accounts = AccountProviderForTest != null
                    ? await AccountProviderForTest(ct).ConfigureAwait(false)
                    : await MiyousheStore.Shared.ListAccountsAsync(ct).ConfigureAwait(false);
                foreach (string account in accounts)
                {
                    DateTime due;
                    lock (LedgerLock)
                    {
                        if (LastLedgerPull.TryGetValue(account, out var last))
                        {
                            if ((DateTime.Now - last).TotalMinutes < interval) continue;   // ★ 未到间隔
                        }
                        due = DateTime.Now;
                    }
                    string region = config.Settings.MiyousheAccountRegions.TryGetValue(account,out var rn)&&rn=="os"?"os":"cn";
                    var credential = await MiyousheCredentialStore.LoadAsync(account,region,ct).ConfigureAwait(false);
                    if (credential==null) continue;
                    LedgerRunCount++;
                    try
                    {
                        await MiyousheService.RunForAccountAsync(account,false,true,ct).ConfigureAwait(false);
                        lock (LedgerLock) { LastLedgerPull[account] = due; }   // 成功才推进
                    }
                    catch
                    {
                        // 失败不推进 LastLedgerPull → 下一拍仍「已到间隔」，但受 RunForAccountAsync
                        // 内部 PausedUntil（MiyousheRetryMinutes 退避）限制，不会紧循环。
                    }
                    if (MiyousheService.DelayForTest==null) await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(2,6)),ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (Interlocked.Exchange(ref _warned,1)==0)
                    App.AppendErrorLog("MiyousheHost.Ledger",new InvalidOperationException("米游社摩拉拉取周期任务失败；未记录请求内容或凭据"));
            }
        }

        internal static void StopForTest() { try { _tick?.Dispose(); } catch { } _tick=null; _started=0; }
        internal static void ResetTestState()
        {
            lock (SignDoneLock) SignDoneByDay.Clear();
            lock (SignRetryLock) SignRetryCount.Clear();
            lock (LedgerLock) LastLedgerPull.Clear();
        }
    }
}
