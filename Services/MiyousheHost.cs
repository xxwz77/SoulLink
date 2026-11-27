using System;
using System.Collections.Generic;
using System.Linq;
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
        private static int _ticking;
        internal static Func<CancellationToken,Task<IReadOnlyList<string>>>? AccountProviderForTest;
        internal static int TickCount { get; private set; }
        internal static int RunCount { get; private set; }
        internal static int LedgerRunCount { get; private set; }

        /// <summary>签到「当天已执行」标记：key = yyyy-MM-dd（本地日），value = 已触发过的账号集合。</summary>
        private static readonly Dictionary<string, HashSet<string>> SignDoneByDay = new(StringComparer.Ordinal);
        private static readonly object SignDoneLock = new();

        private static readonly Dictionary<string, (DateOnly Day, int Attempts, DateTimeOffset RetryAt)> SignRetryCount
            = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object SignRetryLock = new();

        /// <summary>每账号上次摩拉拉取时刻（本地时间），用于每小时一次的节流。</summary>
        private static readonly Dictionary<(string Account, string Uid), DateTimeOffset> LastLedgerPull = new();
        private static readonly object LedgerLock = new();

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started,1)!=0) return;
            try { _tick=TickHub.RegisterBackground("MiyousheHost.Daily",60,() => _=TickAsync()); }
            catch { App.AppendErrorLog("MiyousheHost.Start",new InvalidOperationException("米游社调度注册失败")); }
        }

        internal static async Task TickAsync(CancellationToken ct=default)
        {
            if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
            try { await TickCoreAsync(ct).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _ticking, 0); }
        }

        private static async Task TickCoreAsync(CancellationToken ct)
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
        private static TimeSpan SignTimeOfDay(AppSettings s) => MiyousheSignPreferences.TimeOfDay(s);

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
                string cutoff = DateTime.Now.AddDays(-2).ToString("yyyy-MM-dd");
                foreach (var k in SignDoneByDay.Keys)
                    if (string.CompareOrdinal(k, cutoff) < 0) stale.Add(k);
                foreach (var k in stale) SignDoneByDay.Remove(k);
            }
        }

        private static async Task RunSignOnceAsync(AppConfig config, CancellationToken ct)
        {
            if (MiyousheService.Running != 0 || MiyousheBatchService.Running != 0) return;
            try
            {
                var accounts = AccountProviderForTest != null
                    ? await AccountProviderForTest(ct).ConfigureAwait(false)
                    : AccountScope.Accounts(config).Select(item => item.UserName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (string account in accounts)
                {
                    if (!SignNotDoneToday(account)) continue;                 // ★ 当天已触发，不再试
                    if (!MiyousheSignPreferences.AccountEnabled(config.Settings, account)) continue;
                    if (!MiyousheSignPreferences.Projects(config.Settings, account).Any(game =>
                        MiyousheSignPreferences.ProjectEnabled(config.Settings, account, MiyousheSignPreferences.ProjectId(game)))) continue;
                    if (!CanRetrySign(account)) continue;                     // ★ 重试限次
                    string region = MiyousheSignPreferences.RegionFor(config.Settings, account);
                    var credential = await MiyousheCredentialStore.LoadAsync(account,region,ct).ConfigureAwait(false);
                    if (credential == null) continue;
                    RunCount++;
                    bool ok = false;
                    try
                    {
                        var results = await MiyousheBatchService.RunAsync(account, scheduled: true, ct).ConfigureAwait(false);
                        ok = !results.Any(result => result.State is "failed" or "risk" or "needs_login" or "queued");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { ok = false; }
                    if (ok) MarkSignDone(account);
                    else RecordSignRetry(account, config.Settings.MiyousheRetryMinutes);
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
            lock (SignRetryLock)
                return !SignRetryCount.TryGetValue(account, out var retry) || retry.Day != DateOnly.FromDateTime(DateTime.Today)
                    || (retry.Attempts < 5 && DateTimeOffset.UtcNow >= retry.RetryAt);
        }

        private static void RecordSignRetry(string account, int retryMinutes)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            lock (SignRetryLock)
            {
                int attempts = SignRetryCount.TryGetValue(account, out var retry) && retry.Day == today ? retry.Attempts : 0;
                SignRetryCount[account] = (today, attempts + 1,
                    DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(retryMinutes, 1, 1440)));
            }
        }

        internal static int LedgerIntervalHours(int minutes)
            => minutes >= 60 && minutes <= 360 && minutes % 60 == 0 ? minutes / 60 : 1;

        // ── 摩拉定时拉取 ──────────────────────────────────

        private static async Task RunLedgerIfDueAsync(AppConfig config, CancellationToken ct)
        {
            if (MiyousheService.Running!=0) return;
            var interval = TimeSpan.FromHours(LedgerIntervalHours(config.Settings.MiyousheLedgerIntervalMinutes));
            try
            {
                var accounts = AccountProviderForTest != null
                    ? await AccountProviderForTest(ct).ConfigureAwait(false)
                    : config.Settings.MiyousheDefaultRole.Keys.ToArray();
                foreach (string account in accounts)
                {
                    string uid = MiyousheService.GetDefaultRole(account);
                    string region = MiyousheSignPreferences.RegionFor(config.Settings, account);
                    if (region != "cn" || uid.Length == 0) continue;
                    var due = DateTimeOffset.UtcNow;
                    var last = await LastLedgerAttemptAsync(account, uid, ct).ConfigureAwait(false);
                    if (due - last < interval) continue;

                    var credential = await MiyousheCredentialStore.LoadAsync(account, region, ct).ConfigureAwait(false);
                    if (credential == null) continue;
                    LedgerRunCount++;
                    var result = await MiyousheService.RunForAccountAsync(account, false, true, ct).ConfigureAwait(false);
                    // A busy gate did not send a request. Real attempts use the configured interval.
                    if (result.state != "queued")
                        NoteLedgerAttempt(account, uid, due);
                    if (MiyousheService.DelayForTest==null) await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(2,6)),ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (Interlocked.Exchange(ref _warned,1)==0)
                    App.AppendErrorLog("MiyousheHost.Ledger",new InvalidOperationException("米游社摩拉拉取周期任务失败；未记录请求内容或凭据"));
            }
        }

        private static async Task<DateTimeOffset> LastLedgerAttemptAsync(string account, string uid, CancellationToken ct)
        {
            var key = (account, uid);
            lock (LedgerLock)
            {
                if (LastLedgerPull.TryGetValue(key, out var cached)) return cached;
            }

            long? stored = await MiyousheStore.Shared.LatestPullStartAsync(account, uid, ct).ConfigureAwait(false);
            lock (LedgerLock)
            {
                if (LastLedgerPull.TryGetValue(key, out var cached)) return cached;
                var last = stored.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(stored.Value) : DateTimeOffset.MinValue;
                LastLedgerPull[key] = last;
                return last;
            }
        }

        internal static void NoteLedgerAttempt(string account, string uid, DateTimeOffset start)
        {
            var key = (account, uid);
            lock (LedgerLock)
            {
                if (!LastLedgerPull.TryGetValue(key, out var last) || start > last)
                    LastLedgerPull[key] = start;
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
