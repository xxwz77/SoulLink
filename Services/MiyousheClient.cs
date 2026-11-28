using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    public sealed record MiyousheQrSession(string Session, string Account, string Status,
        string QrPngBase64, string Detail, string Uid = "", string AidMasked = "");
    internal sealed record MiyousheReply(int Retcode, string Message, JsonElement Data, string[] Cookies);
    internal sealed class MiyousheRequestException : Exception
    {
        internal int Retcode { get; }
        internal bool RequiresLogin => IsLoginError(Retcode);
        internal static bool IsLoginError(int code) => code is -100 or -10001;
        internal MiyousheRequestException(int code, string message) : base(message) { Retcode = code; }
    }

    /// <summary>纯 HTTP 扫码与旅行者札记客户端；永不记录请求 Cookie 或响应 Set-Cookie。</summary>
    internal sealed class MiyousheClient
    {
        internal const string CreateQrUrl = "https://passport-api.miyoushe.com/account/ma-cn-passport/web/createQRLogin";
        internal const string QueryQrUrl = "https://passport-api.miyoushe.com/account/ma-cn-passport/web/queryQRLoginStatus";
        internal const string RolesUrl = "https://api-takumi.mihoyo.com/binding/api/getUserGameRolesByCookie?game_biz=hk4e_cn";
        internal const string SignInfoUrl = "https://api-takumi.mihoyo.com/event/luna/hk4e/info";
        internal const string SignUrl = "https://api-takumi.mihoyo.com/event/luna/hk4e/sign";
        internal const string MonthInfoUrl = "https://hk4e-api.mihoyo.com/event/ys_ledger/monthInfo";
        internal const string MonthDetailUrl = "https://hk4e-api.mihoyo.com/event/ys_ledger/monthDetail";
        private static readonly HttpClient DefaultHttp = CreateHttp();
        private static int _pageDelayMs = 2000;
        private readonly HttpClient _http;
        private readonly string _salt;
        private readonly string _region;
        internal MiyousheClient(string salt, HttpClient? http = null, string region="cn")
        { _salt = salt ?? ""; _http = http ?? DefaultHttp; _region=region=="os"?"os":"cn"; }
        private MiyousheRegionProfile Profile => MiyousheCatalog.Regions[_region];

        private static HttpClient CreateHttp()
        {
            var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        }

        /// <summary>t40：device 由调用方传入（按账号持久化，见 CredentialStore.GetOrCreateDeviceIdAsync）。</summary>
        internal async Task<(string Ticket,string Url)> CreateQrAsync(CancellationToken ct, string? device = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Profile.PassportCreate) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            // ★ t37：x-rpc-app_id 由 AddCommon 统一添加（此前这里重复加两遍 → 服务端 -3005）
            // ★ t40：device_id 不再每次 NewGuid（风控特征），由持久化层提供
            AddCommon(request, device ?? Guid.NewGuid().ToString("D"));
            var reply = await SendAsync(request, ct).ConfigureAwait(false);
            var data = Data(reply);
            string ticket = String(data,"ticket"), url = String(data,"url");
            if (ticket.Length == 0 || url.Length == 0) throw new InvalidDataException("二维码接口缺少 ticket/url");
            return (ticket,url);
        }

        internal async Task<(string Status,MiyousheCredential? Credential)> PollQrAsync(string ticket, string account, CancellationToken ct)
        {
            var prior = await MiyousheCredentialStore.LoadAsync(account, _region, ct).ConfigureAwait(false)
                ?? new MiyousheCredential { DeviceId = await MiyousheCredentialStore.GetOrCreateDeviceIdAsync(account, ct).ConfigureAwait(false) };
            string body = JsonSerializer.Serialize(new { ticket });
            using var request = new HttpRequestMessage(HttpMethod.Post, Profile.PassportQuery) { Content = JsonContent(body) };
            // ★ t37：x-rpc-app_id 由 AddCommon 统一添加（原此处用硬编码 bll8iq97cem8，改为 Profile.AppId 一处收口）
            AddCommon(request, prior.DeviceId);
            var reply = await SendAsync(request, ct).ConfigureAwait(false);
            var data = Data(reply); string status = String(data,"status");
            if (!status.Equals("Confirmed",StringComparison.OrdinalIgnoreCase)) return (status,null);
            var cookies = ParseCookies(reply);
            var credential = new MiyousheCredential
            {
                LToken = Get(cookies,Profile.LTokenName), LtUid = Get(cookies,Profile.LtUidName),
                LtMid = Get(cookies,"ltmid_v2"), CookieToken = Get(cookies,Profile.CookieTokenName),
                AccountId = Get(cookies,Profile.AccountIdName), AccountMid = Get(cookies,"account_mid_v2"), SToken = Get(cookies,Profile.StokenName),
                StUid = Get(cookies,"stuid"), Mid = Get(cookies,"mid"), Region=_region,
                DeviceId = prior.DeviceId, LastOkAt = DateTimeOffset.Now
            };
            if (data.TryGetProperty("user_info",out var user))
            { credential.Aid = String(user,"aid"); if (credential.Mid.Length == 0) credential.Mid = String(user,"mid"); }
            if (credential.AccountId.Length == 0) credential.AccountId = credential.LtUid;
            if (credential.CookieToken.Length == 0 || credential.AccountId.Length == 0)
                throw new InvalidDataException("扫码确认响应缺少必要凭据");
            await MiyousheCredentialStore.SaveAsync(account,_region,credential,ct).ConfigureAwait(false);
            return (status,credential);
        }

        internal async Task<List<MiyousheRole>> GetRolesAsync(string account, MiyousheCredential credential, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,RolesUrl);
            AddAuthorized(request,credential,"game_biz=hk4e_cn",null);
            var reply = await SendAsync(request,ct).ConfigureAwait(false);
            var data = Data(reply);
            if (!data.TryGetProperty("list",out var list) || list.ValueKind != JsonValueKind.Array)
                return new List<MiyousheRole>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var roles = new List<MiyousheRole>();
            foreach (var item in list.EnumerateArray())
            {
                string uid = String(item,"game_uid"); if (uid.Length == 0) continue;
                var role = new MiyousheRole(account,uid,String(item,"region"),String(item,"nickname"),Int(item,"level") ?? 0);
                roles.Add(role); await MiyousheStore.Shared.SaveRoleAsync(role,item.GetRawText(),now,ct).ConfigureAwait(false);
            }
            return roles;
        }
        internal async Task<(bool IsSign,string Raw)> GetSignInfoAsync(MiyousheCredential credential, MiyousheRole role, CancellationToken ct)
        {
            string query = Query(new[]{("act_id","e202311201442471"),("uid",role.GameUid),("region",role.Region)});
            using var request = new HttpRequestMessage(HttpMethod.Get,SignInfoUrl+"?"+query);
            AddAuthorized(request,credential,query,null);
            var reply = await SendAsync(request,ct).ConfigureAwait(false);
            var data = Data(reply);
            return (Bool(data,"is_sign") == true,data.GetRawText());
        }

        internal async Task<MiyousheSignResult> SignAsync(string account, MiyousheCredential credential, MiyousheRole role,
            string date, CancellationToken ct)
        {
            string body = JsonSerializer.Serialize(new { act_id = "e202311201442471", region = role.Region, uid = role.GameUid });
            using var request = new HttpRequestMessage(HttpMethod.Post,SignUrl) { Content = JsonContent(body) };
            AddAuthorized(request,credential,null,body);
            try
            {
                var reply = await SendAsync(request,ct).ConfigureAwait(false);
                var data = Data(reply);
                string reward = "", count = "";
                if (data.TryGetProperty("award",out var award)) { reward = String(award,"name"); count = String(award,"cnt"); }
                return new(account,role.GameUid,date,"success",0,"签到成功",reward,int.TryParse(count,out var n)?n:0,NowMs());
            }
            catch (MiyousheRequestException ex) when (ex.Retcode == -5003)
            { return new(account,role.GameUid,date,"already",-5003,"今日已签到","",0,NowMs()); }
        }

        internal async Task<bool> PullLedgerMonthInfoAsync(string account, MiyousheCredential credential,
            MiyousheRole role, DateTime requestedMonth, DateTime? diaryDay, CancellationToken ct,
            DateTimeOffset? fetchedAtOverride = null)
        {
            int month = requestedMonth.Month;
            string ym = requestedMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            string infoQuery = Query(new[]{("bind_uid",role.GameUid),("bind_region",role.Region),("month",month.ToString())});
            using var request = new HttpRequestMessage(HttpMethod.Get,MonthInfoUrl+"?"+infoQuery);
            AddAuthorized(request,credential,infoQuery,null);
            var reply = await SendAsync(request,ct).ConfigureAwait(false);
            var fetchedAt = fetchedAtOverride ?? DateTimeOffset.Now;
            var data = Data(reply);
            if (data.TryGetProperty("month_data",out var m) && m.ValueKind == JsonValueKind.Object)
            {
                await MiyousheStore.Shared.SaveLedgerMonthAsync(new(account,role.GameUid,ym,
                    Long(m,"current_primogems"),Long(m,"current_mora"),Long(m,"last_primogems"),Long(m,"last_mora"),
                    Int(m,"primogem_rate"),Int(m,"mora_rate"),m.GetRawText(),"ys_ledger/monthInfo",NowMs()),ct).ConfigureAwait(false);
            }
            var received = fetchedAt.LocalDateTime;
            if (diaryDay is not DateTime snapshotDay || received.Year != requestedMonth.Year || received.Month != month ||
                DogfoodRecordReader.CurrentBusinessDay(received) != snapshotDay ||
                !data.TryGetProperty("day_data", out var dayData) || dayData.ValueKind != JsonValueKind.Object)
                return false;

            long? currentMora = Long(dayData, "current_mora");
            long? currentPrimogems = Long(dayData, "current_primogems");
            if (!currentMora.HasValue && !currentPrimogems.HasValue) return false;
            string day = snapshotDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await MiyousheStore.Shared.SaveLedgerFactsAsync(Array.Empty<MiyousheLedgerEntry>(),
                new[] { new MiyousheLedgerDay(account, role.GameUid, day, 0, currentPrimogems, currentMora,
                    dayData.GetRawText(), "ys_ledger/monthInfo", fetchedAt.ToUnixTimeMilliseconds()) }, ct).ConfigureAwait(false);
            if (!MiyousheStore.Shared.IsHealthy) throw new IOException("米游社札记汇总写入失败");
            return currentMora.HasValue;
        }

        internal async Task PullLedgerAsync(string account, MiyousheCredential credential, MiyousheRole role,
            DateTime requestedMonth, DateTime? diaryDay, Func<int,int,CancellationToken,Task> progress,
            CancellationToken ct, DateTime? detailsSince = null, Action? onDailySummarySaved = null,
            bool skipMonthInfo = false, DateTimeOffset? fetchedAtOverride = null)
        {
            if (!skipMonthInfo &&
                await PullLedgerMonthInfoAsync(account, credential, role, requestedMonth, diaryDay, ct, fetchedAtOverride)
                    .ConfigureAwait(false))
                onDailySummarySaved?.Invoke();
            string ym = requestedMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            int allPages=0,allInserted=0;
            for (int kind = 1; kind <= 2; kind++)
            {
                await PullDetailPagesAsync(account,credential,role,ym,requestedMonth,kind,async (pages,inserted,token) =>
                {
                    if (kind==1) { allPages=pages; allInserted=inserted; }
                    await progress(allPages+(kind==2?pages:0),allInserted+(kind==2?inserted:0),token).ConfigureAwait(false);
                },ct, detailsSince).ConfigureAwait(false);
                if (kind == 1) await DelayBetweenRequestsAsync(ct).ConfigureAwait(false);
            }
        }

        internal void ConfigureTestDelay(int milliseconds) { _pageDelayMs=milliseconds; }

        private sealed record DetailPage(JsonElement Items, int? Number, string Fingerprint)
        {
            internal bool Matches(int page) => Items.GetArrayLength() > 0 && (!Number.HasValue || Number.Value == page);
        }
        private sealed record BoundaryOccurrence(int ActionId, string Time, string Raw, int Count);
        private sealed record DetailCursorState(string[] Fingerprints, long BoundaryMs,
            BoundaryOccurrence[] BoundaryOccurrences, Dictionary<string, long> Daily);

        private async Task PullDetailPagesAsync(string account, MiyousheCredential credential, MiyousheRole role,
            string ym, DateTime month, int kind, Func<int,int,CancellationToken,Task> progress, CancellationToken ct,
            DateTime? since)
        {
            int? replay = await PullDetailPassAsync(account, credential, role, ym, month, kind, progress, ct, since,
                allowResume: true, totalInserted: 0).ConfigureAwait(false);
            if (replay.HasValue)
            {
                await DelayBetweenRequestsAsync(ct).ConfigureAwait(false);
                await PullDetailPassAsync(account, credential, role, ym, month, kind, progress, ct, since,
                    allowResume: false, totalInserted: replay.Value).ConfigureAwait(false);
            }
        }

        // Returns inserted count only when a resumed stream has changed and must be replayed once.
        private async Task<int?> PullDetailPassAsync(string account, MiyousheCredential credential, MiyousheRole role,
            string ym, DateTime month, int kind, Func<int,int,CancellationToken,Task> progress, CancellationToken ct,
            DateTime? since, bool allowResume, int totalInserted)
        {
            // Dense game days can contain more than 2,000 receipts. The time boundary stops normal pulls.
            const int maxPagesPerPass = 512;
            int firstPage = 1;
            var occurrences = new Dictionary<(int ActionId, string Time, string Raw), int>();
            var boundaryOccurrences = new Dictionary<(int ActionId, string Time, string Raw), int>();
            var daily = new Dictionary<string, long>();
            var fingerprints = new List<string>();
            var seenPages = new HashSet<string>(StringComparer.Ordinal);
            DateTimeOffset? cutoff = since.HasValue ? new DateTimeOffset(since.Value) : null;
            DateTimeOffset? previousTime = null;
            bool descending = cutoff.HasValue;
            MiyousheLedgerScope? scope = since is DateTime startDay && cutoff is DateTimeOffset begins
                ? new(account, role.GameUid, role.Region, ym, kind, begins.ToUnixTimeMilliseconds(),
                    new DateTimeOffset(startDay.AddDays(1)).ToUnixTimeMilliseconds()) : null;
            bool resumed = false;
            bool cursorExists = false;
            DetailPage? firstResponse = null;
            if (allowResume && scope != null &&
                await MiyousheStore.Shared.LoadLedgerCursorAsync(scope, ct).ConfigureAwait(false) is { } saved)
            {
                var state = ReadDetailCursor(saved, maxPagesPerPass);
                if (state != null)
                {
                    firstResponse = await ReadDetailPageAsync(credential, role, month, kind, 1, ct).ConfigureAwait(false);
                    if (firstResponse.Matches(1) && firstResponse.Fingerprint == state.Fingerprints[0])
                    {
                        var tail = firstResponse;
                        if (saved.Page > 1)
                        {
                            await DelayBetweenRequestsAsync(ct).ConfigureAwait(false);
                            tail = await ReadDetailPageAsync(credential, role, month, kind, saved.Page, ct).ConfigureAwait(false);
                        }
                        if (tail.Matches(saved.Page) && tail.Fingerprint == state.Fingerprints[^1] &&
                            ParseMinute(String(tail.Items[tail.Items.GetArrayLength() - 1], "time"), month.Year)
                                ?.ToUnixTimeMilliseconds() == state.BoundaryMs)
                        {
                            resumed = cursorExists = true;
                            firstPage = saved.Page + 1;
                            previousTime = DateTimeOffset.FromUnixTimeMilliseconds(state.BoundaryMs);
                            daily = state.Daily;
                            fingerprints.AddRange(state.Fingerprints);
                            seenPages.UnionWith(fingerprints);
                            foreach (var occurrence in state.BoundaryOccurrences)
                            {
                                var key = (occurrence.ActionId, occurrence.Time, occurrence.Raw);
                                occurrences[key] = boundaryOccurrences[key] = occurrence.Count;
                            }
                            await progress(saved.Page, totalInserted, ct).ConfigureAwait(false);
                        }
                        if (saved.Page > 1 || resumed) await DelayBetweenRequestsAsync(ct).ConfigureAwait(false);
                    }
                }
                if (!resumed) await ClearDetailCursorAsync(scope, ct).ConfigureAwait(false);
            }

            for (int page = firstPage; !(descending && previousTime < cutoff); page++)
            {
                DetailPage response = page == 1 && firstResponse != null ? firstResponse :
                    await ReadDetailPageAsync(credential, role, month, kind, page, ct).ConfigureAwait(false);
                bool empty = response.Items.GetArrayLength() == 0;
                bool invalidPage = empty ? response.Number is > 0 && response.Number != page
                    : !response.Matches(page) || !seenPages.Add(response.Fingerprint);
                if (invalidPage)
                {
                    if (resumed)
                    {
                        await ClearDetailCursorAsync(scope!, ct).ConfigureAwait(false);
                        return totalInserted;
                    }
                    throw new InvalidDataException("米游社返回了重复或不匹配的流水分页");
                }
                // 列表可能少于请求数量，在收到空页前继续拉取。
                if (empty)
                {
                    await progress(page, totalInserted, ct).ConfigureAwait(false);
                    break;
                }
                fingerprints.Add(response.Fingerprint);

                var entries = new List<MiyousheLedgerEntry>();
                foreach (var item in response.Items.EnumerateArray())
                {
                    string time = String(item,"time");
                    var occurredAt = ParseMinute(time, month.Year);
                    if (descending)
                    {
                        descending = occurredAt.HasValue && (!previousTime.HasValue || occurredAt <= previousTime);
                        if (occurredAt != previousTime) boundaryOccurrences.Clear();
                        previousTime = occurredAt;
                    }
                    long? num = Long(item,"num"); int actionId = Int(item,"action_id") ?? 0;
                    // Count identical facts across the pull. An inserted row on page one must not renumber other facts.
                    string raw = item.GetRawText();
                    var identity = (actionId, time, raw);
                    occurrences.TryGetValue(identity, out int ordinal);
                    occurrences[identity] = ordinal + 1;
                    if (descending) boundaryOccurrences[identity] = ordinal + 1;
                    entries.Add(new(account,role.GameUid,role.Region,ym,kind,actionId,String(item,"action"),num,
                        Long(item,"single_morale") ?? Long(item,"single_mora"),Int(item,"count"),
                        time,occurredAt?.ToUnixTimeMilliseconds(),raw,"ys_ledger/monthDetail",NowMs(),ordinal));
                    if (num.HasValue && occurredAt is DateTimeOffset at)
                    {
                        string day = at.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        daily.TryGetValue(day, out long sum);
                        daily[day] = checked(sum + num.Value);
                    }
                }
                if (resumed && !descending)
                {
                    await ClearDetailCursorAsync(scope!, ct).ConfigureAwait(false);
                    return totalInserted;
                }
                MiyousheLedgerCursor? nextCursor = null;
                if (descending && scope != null && previousTime is DateTimeOffset boundary)
                {
                    var state = new DetailCursorState(fingerprints.ToArray(), boundary.ToUnixTimeMilliseconds(),
                        boundaryOccurrences.Select(pair => new BoundaryOccurrence(
                            pair.Key.ActionId, pair.Key.Time, pair.Key.Raw, pair.Value)).ToArray(), daily);
                    nextCursor = new(scope, page, JsonSerializer.Serialize(state));
                }
                int inserted = await MiyousheStore.Shared.SaveLedgerFactsAsync(entries, Array.Empty<MiyousheLedgerDay>(),
                    ct, stableOccurrences: true, cursor: nextCursor,
                    clearCursor: nextCursor == null && cursorExists ? scope : null).ConfigureAwait(false);
                EnsureDetailCommit(inserted);
                cursorExists = nextCursor != null;
                totalInserted += inserted;
                await progress(page, totalInserted, ct).ConfigureAwait(false);
                if (descending && previousTime < cutoff) break;
                if (page - firstPage + 1 == maxPagesPerPass)
                    throw new InvalidDataException("米游社流水达到分页上限，尚未读完整");
                await DelayBetweenRequestsAsync(ct).ConfigureAwait(false);
            }

            // A date-window pull can finish partway through an older calendar day.
            string? firstCompleteDay = null;
            if (descending && previousTime < cutoff && since is DateTime start)
            {
                DateTime firstCompleteDate = start.TimeOfDay == TimeSpan.Zero ? start.Date : start.Date.AddDays(1);
                firstCompleteDay = firstCompleteDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            var days = daily.Where(pair => firstCompleteDay == null || string.CompareOrdinal(pair.Key, firstCompleteDay) >= 0)
                .Select(pair => new MiyousheLedgerDay(account, role.GameUid, pair.Key, kind,
                    kind == 1 ? pair.Value : null, kind == 2 ? pair.Value : null,
                    JsonSerializer.Serialize(new { sum = pair.Value }), "ys_ledger/monthDetail", NowMs())).ToArray();
            if (days.Length > 0 || cursorExists)
                EnsureDetailCommit(await MiyousheStore.Shared.SaveLedgerFactsAsync(Array.Empty<MiyousheLedgerEntry>(),
                    days, ct, clearCursor: cursorExists ? scope : null).ConfigureAwait(false));
            return null;
        }

        private async Task<DetailPage> ReadDetailPageAsync(MiyousheCredential credential, MiyousheRole role,
            DateTime month, int kind, int page, CancellationToken ct)
        {
            // 国服札记用 page/limit。传 current_page/page_size 时接口仍返回第一页。
            string query = Query(new[] { ("bind_uid", role.GameUid), ("bind_region", role.Region),
                ("month", month.Month.ToString(CultureInfo.InvariantCulture)), ("type", kind.ToString(CultureInfo.InvariantCulture)),
                ("page", page.ToString(CultureInfo.InvariantCulture)), ("limit", "20") });
            using var request = new HttpRequestMessage(HttpMethod.Get, MonthDetailUrl + "?" + query);
            AddAuthorized(request, credential, query, null);
            var data = Data(await SendAsync(request, ct).ConfigureAwait(false));
            if (!data.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("米游社流水分页格式无效");
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(list.GetRawText())));
            return new(list, Int(data, "page") ?? Int(data, "current_page"), fingerprint);
        }

        private static DetailCursorState? ReadDetailCursor(MiyousheLedgerCursor saved, int maxPagesPerPass)
        {
            if (saved.Page < 1 || saved.Page > int.MaxValue - maxPagesPerPass - 1) return null;
            try
            {
                var state = JsonSerializer.Deserialize<DetailCursorState>(saved.StateJson);
                return state?.Fingerprints?.Length == saved.Page && state.Daily != null &&
                    state.BoundaryOccurrences != null && state.BoundaryOccurrences.All(item => item is { Count: > 0 })
                    ? state : null;
            }
            catch (JsonException) { return null; }
        }

        private static async Task ClearDetailCursorAsync(MiyousheLedgerScope scope, CancellationToken ct)
            => EnsureDetailCommit(await MiyousheStore.Shared.SaveLedgerFactsAsync(Array.Empty<MiyousheLedgerEntry>(),
                Array.Empty<MiyousheLedgerDay>(), ct, clearCursor: scope).ConfigureAwait(false));

        private static void EnsureDetailCommit(int result)
        {
            if (result < 0 || !MiyousheStore.Shared.IsHealthy) throw new IOException("米游社流水写入失败");
        }
        private async Task<MiyousheReply> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var response = await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);
            string text;
            try { text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
            catch { throw new IOException("米游社响应不可读"); }
            if (text.Length > 4_000_000) throw new InvalidDataException("米游社响应过大");
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new MiyousheRequestException(-10001,"登录已失效，请重新扫码");
            if (!response.IsSuccessStatusCode)
                throw new IOException("米游社接口暂不可用（HTTP "+(int)response.StatusCode+"）");
            try
            {
                using var doc = JsonDocument.Parse(text);
                int code = Int(doc.RootElement,"retcode") ?? 0;
                if (code != 0) throw new MiyousheRequestException(code,SafeServerMessage(String(doc.RootElement,"message"),code));
                if (!doc.RootElement.TryGetProperty("data",out var data))
                    throw new InvalidDataException("米游社响应缺少 data");
                var cookies = response.Headers.TryGetValues("Set-Cookie",out var values) ? values.ToArray() : Array.Empty<string>();
                return new MiyousheReply(code,"",data.Clone(),cookies);
            }
            catch (JsonException) { throw new InvalidDataException("米游社响应格式无效"); }
        }

        private static JsonElement Data(MiyousheReply response) => response.Data;

        /// <summary>
        /// 公共请求头。★ t37：x-rpc-app_id **统一在此处添加一次**（此前 CreateQrAsync 加了两遍
        /// 导致服务端 -3005「参数不合法」、PollQrAsync 用硬编码 bll8iq97cem8 又加一遍——
        /// 三处收拢成一处，任何路径（含 AddAuthorized→AddCommon）都只会发一个）。
        /// </summary>
        private void AddCommon(HttpRequestMessage request, string device)
        {
            request.Headers.TryAddWithoutValidation("User-Agent","Mozilla/5.0 (Windows NT 10.0; Win64; x64) SoulLink/3.3");
            request.Headers.TryAddWithoutValidation("Accept","application/json, text/plain, */*");
            request.Headers.TryAddWithoutValidation("x-rpc-device_id",device);
            request.Headers.TryAddWithoutValidation("x-rpc-client_type","4");
            request.Headers.TryAddWithoutValidation("x-rpc-app_version","2.70.1");
            request.Headers.TryAddWithoutValidation("x-rpc-app_id", Profile.AppId);
            request.Headers.TryAddWithoutValidation("Referer","https://www.miyoushe.com/");
        }

        private void AddAuthorized(HttpRequestMessage request, MiyousheCredential credential, string? query, string? body)
        {
            AddCommon(request,credential.DeviceId);
            request.Headers.Remove("Referer"); request.Headers.TryAddWithoutValidation("Referer",Profile.Referer);
            request.Headers.TryAddWithoutValidation("Cookie",CookieHeader(credential));
            request.Headers.TryAddWithoutValidation("x-rpc-signgame","hk4e");
            if (_salt.Length == 0 && _region != "os")
                throw new InvalidOperationException("米游社 DS salt 为空：请到设置页填写 MiyousheDsSalt（国服 x4 salt），然后重试。");
            // 国际服参考实现只要求 Referer/Cookie，未证实的 x4 salt 不伪造；配置后可显式覆盖发送。
            if (_salt.Length > 0)
                request.Headers.TryAddWithoutValidation("DS",CreateDs(_salt,body,query));
        }

        internal static string CreateDs(string salt, string? body, string? query, long? unixSeconds = null, string? random = null)
        {
            string t = (unixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
            string r = random ?? RandomNumberGenerator.GetInt32(100000,1000000).ToString();
            string q = "";
            if (!string.IsNullOrEmpty(query))
                q = string.Join("&",query.Split('&').Select(p => p.Split('=',2)).OrderBy(p => p[0],StringComparer.Ordinal)
                    .Select(p => p[0]+"="+(p.Length>1?p[1]:"")));
            string raw = "salt="+salt+"&t="+t+"&r="+r+"&b="+(body??"")+"&q="+q;
            return t+","+r+","+Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        }

        /// <summary>
        /// 组装请求用 Cookie 头。
        ///
        /// ★★ 旧键名优先（导入来源的真实情况）★★
        ///   从 BetterGI / ABGI 导入的凭据是**旧键名** cookie
        ///   （ltoken / ltuid / cookie_token / account_id / mid），
        ///   而 cn 档案的结构化字段名是 v2。把 v1 的值挂在 v2 名下，服务端会判为无效，
        ///   所以只要 <see cref="MiyousheCredential.RawCookie"/> 非空就**原样回放**，
        ///   再补上结构化字段里独有的项（例如 stoken_v2 / stuid / aid）。
        ///   扫码登录拿到的凭据没有 RawCookie，走原来的结构化拼装。
        /// </summary>
        private string CookieHeader(MiyousheCredential c)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Put(string name,string value)
            { if (!string.IsNullOrEmpty(value) && seen.Add(name)) list.Add(name+"="+value); }

            bool raw = !string.IsNullOrEmpty(c.RawCookie);
            if (raw)
                foreach (string part in c.RawCookie.Split(';'))
                {
                    string item = part.Trim(); if (item.Length == 0) continue;
                    int eq = item.IndexOf('=');
                    if (eq <= 0) continue;
                    Put(item[..eq].Trim(), item[(eq+1)..].Trim());
                }

            // 原文已带旧键名（ltoken/ltuid/cookie_token/account_id/mid）时，
            // **绝不**再把同一批值挂到 v2 名下 —— 那正是「服务端判无效」的根因。
            // 原文缺失的项（例如 stoken_v2 / stuid / aid）才用结构化字段补。
            if (!raw || !HasRaw(c, "ltoken_v2", "ltoken")) Put(Profile.LTokenName, c.LToken);
            if (!raw || !HasRaw(c, "ltuid_v2", "ltuid")) Put(Profile.LtUidName, c.LtUid);
            if (!raw || !HasRaw(c, "cookie_token_v2", "cookie_token")) Put(Profile.CookieTokenName, c.CookieToken);
            if (!raw || !HasRaw(c, "account_id_v2", "account_id", "ltuid_v2", "ltuid")) Put(Profile.AccountIdName, c.AccountId);
            if (!raw && _region == "cn")
                Put("account_mid_v2", !string.IsNullOrEmpty(c.AccountMid) ? c.AccountMid :
                    !string.IsNullOrEmpty(c.Mid) ? c.Mid : c.LtMid);
            if (!raw || !HasRaw(c, "ltmid_v2", "mid")) Put("ltmid_v2", c.LtMid);
            if (!raw || !HasRaw(c, "stoken_v2", "stoken")) Put(Profile.StokenName, c.SToken);
            Put("stuid", c.StUid);
            Put("mid", c.Mid);
            return string.Join("; ",list);
        }

        /// <summary>原文里是否已有这些键之一（忽略大小写）。</summary>
        private static bool HasRaw(MiyousheCredential c, params string[] names)
        {
            foreach (string part in c.RawCookie.Split(';'))
            {
                string item = part.Trim(); if (item.Length == 0) continue;
                int eq = item.IndexOf('=');
                if (eq <= 0) continue;
                string key = item[..eq].Trim();
                foreach (string name in names)
                    if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static Dictionary<string,string> ParseCookies(MiyousheReply reply)
        {
            var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in reply.Cookies)
            {
                string pair = line.Split(';',2)[0]; int split = pair.IndexOf('=');
                if (split > 0) result[pair[..split].Trim()] = pair[(split+1)..].Trim();
            }
            return result;
        }
        internal static string CreateQrPng(string url)
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(url,QRCoder.QRCodeGenerator.ECCLevel.M);
            using var qr = new QRCoder.PngByteQRCode(data);
            // ★ t42 复查：真实米游社 URL 约 132 字符 → QR 57 模块。GetGraphic(8) 无静默区
            //   → 456px 图片顶满 460 窗口被裁、且缺二维码规范要求的 4 模块白边。
            //   改 GetGraphic(6, drawQuietZones:true)：57×6=342px + 4×2×6=48px 静默区 = 390px，
            //   带静默区、尺寸匹配窗口、6px/模块在高 DPI（150%/200%）下仍清晰可扫。
            return Convert.ToBase64String(qr.GetGraphic(6, drawQuietZones: true));
        }

        private static StringContent JsonContent(string json) => new(json,Encoding.UTF8,"application/json");
        private static string Query(IEnumerable<(string Key,string Value)> values)
            => string.Join("&",values.Select(v => Uri.EscapeDataString(v.Key)+"="+Uri.EscapeDataString(v.Value)));
        private static string Get(Dictionary<string,string> cookies,string name) => cookies.TryGetValue(name,out var value) ? value : "";
        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static Task DelayBetweenRequestsAsync(CancellationToken ct)
            => Task.Delay(TimeSpan.FromMilliseconds(_pageDelayMs == 2000 ? RandomNumberGenerator.GetInt32(2000,5001) : _pageDelayMs),ct);

        private static string String(JsonElement element,string name)
        {
            if (!element.TryGetProperty(name,out var value)) return "";
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true", JsonValueKind.False => "false", _ => ""
            };
        }
        private static long? Long(JsonElement element,string name)
            => long.TryParse(String(element,name),System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,out var value) ? value : null;
        private static int? Int(JsonElement element,string name)
            => int.TryParse(String(element,name),System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,out var value) ? value : null;
        private static bool? Bool(JsonElement element,string name)
        {
            if (!element.TryGetProperty(name,out var value)) return null;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            return String(element,name) == "1";
        }
        private static DateTimeOffset? ParseMinute(string value, int year)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(value,@"^\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?$"))
                value=year.ToString("0000", CultureInfo.InvariantCulture)+"-"+value.Replace('/','-');
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at)
                ? at : null;
        }
        private static string SafeServerMessage(string message,int code)
        {
            if (code == -5003) return "今日已签到";
            if (MiyousheRequestException.IsLoginError(code)) return "米游社拒绝了当前登录凭据，请重新扫码";
            if (code is 1034 or 10035 or 5003 or 10041) return "触发服务端风控，请人工处理并重新扫码";
            // 不回显可能包含票据/URL 的服务端文本，只保留有限长度的通用失败描述。
            return "米游社请求失败（retcode="+code+"）";
        }
    }
}
