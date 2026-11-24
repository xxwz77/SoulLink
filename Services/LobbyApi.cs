using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 联机大厅服务端客户端。
    ///
    /// 所有请求都带 X-Device-Code 头（设备码即身份）。
    /// 服务端地址可配置（配置为空则联机功能静默，不发任何请求）。
    /// </summary>
    public static class LobbyApi
    {
        private static readonly HttpClient _http;
        /// <summary>
        /// 内置默认地址：**空**。服务器地址来自单一设置（ServerEndpoints），
        /// 代码不再写死任何服务器地址。留空 = 联机功能未配置 → 不发起任何请求（彻底静默）。
        /// 用户在「服务器」设置里填新地址后才会联网。
        /// </summary>
        public const string DefaultBaseUrl = "";

        private static string _baseUrl = DefaultBaseUrl;

        /// <summary>
        /// 实际请求使用的地址（配置为空时为空串）。
        /// ★ 仅供内部 HTTP 调用使用，不要绑定到界面输入框 —— 那会把默认地址暴露给用户。
        /// </summary>
        public static string BaseUrl
        {
            get => _baseUrl;
            set => _baseUrl = (value ?? "").Trim().TrimEnd('/');
        }

        /// <summary>是否已配置服务器地址。未配置时所有接口直接返回失败、不发请求。</summary>
        public static bool HasServer => !string.IsNullOrWhiteSpace(_baseUrl);

        /// <summary>
        /// 用户在配置里显式填写的地址（未填则为空串）。
        /// ★ 界面应当只显示这个：留空代表"未配置服务器地址"，而不是把默认地址印出来。
        /// </summary>
        public static string ConfiguredUrl
        {
            get
            {
                try
                {
                    var s = App.Config?.Settings?.ServerBaseUrl;
                    return ServerEndpoints.NormalizeBase(s);
                }
                catch { return ""; }
            }
        }

        /// <summary>当前是否在使用内置默认地址（用户没填 = 未配置服务器）。</summary>
        public static bool UsingDefaultUrl => !HasServer;

        static LobbyApi()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        /// <summary>从配置读取服务器地址。</summary>
        public static void LoadFromConfig()
        {
            try
            {
                BaseUrl = ServerEndpoints.Base;
            }
            catch { }
        }

        /// <summary>保存服务器地址到配置。</summary>
        public static void SaveToConfig(string url)
        {
            try
            {
                BaseUrl = url;
                App.Config.Settings.ServerBaseUrl = _baseUrl;
                ConfigStore.Save(App.Config);
            }
            catch { }
        }

        // ══════════ 底层请求 ══════════

        private static HttpRequestMessage Req(HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, _baseUrl + path);
            req.Headers.Add("X-Device-Code", DeviceIdentity.Code);
            return req;
        }        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static async Task<T?> SendAsync<T>(HttpRequestMessage req, object? body,
                                                   CancellationToken ct)
        {
            if (body != null)
            {
                req.Content = JsonContent.Create(body, options: _json);
            }

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text)) return default;

            return JsonSerializer.Deserialize<T>(text, _json);
        }

        // ══════════ 健康检查 ══════════

        public sealed class PingResult
        {
            public bool Ok { get; set; }
            public string Version { get; set; } = "";
            public DateTime Time { get; set; }
        }

        /// <summary>测试服务器是否可达。</summary>
        public static async Task<(bool ok, string message)> PingAsync(
            CancellationToken ct = default)
        {
            if (!HasServer) return (false, "未配置服务器地址");
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/api/ping");
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return (false, "服务器返回 " + (int)resp.StatusCode);

                var p = await resp.Content.ReadFromJsonAsync<PingResult>(_json, ct)
                        .ConfigureAwait(false);
                return (true, "服务器在线 · 版本 " + (p?.Version ?? "?"));
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>心跳：告诉服务器本机还在线。返回是否成功。</summary>
        public static async Task<bool> HeartbeatAsync(CancellationToken ct = default)
        {
            if (!HasServer) return false;
            try
            {
                using var req = Req(HttpMethod.Post, "/api/heartbeat");
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ══════════ 房间 ══════════

        public sealed class RoomDto
        {
            public string Id { get; set; } = "";
            public string Code { get; set; } = "";
            public string Title { get; set; } = "";
            public int Kind { get; set; }
            public string OwnerDevice { get; set; } = "";
            public string PairId { get; set; } = "";
            public string PairRole { get; set; } = "";
            public bool Departed { get; set; }
            public int DepartSize { get; set; }
            public DateTime LastActiveAt { get; set; }
            public List<SeatDto> Seats { get; set; } = new();
        }

        public sealed class SeatDto
        {
            public int Index { get; set; }
            public string DeviceCode { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public bool Online { get; set; }
            public string RoleName { get; set; } = "";
        }

        /// <summary>列出所有房间（房间码只对创建者可见）。</summary>
        public static async Task<(bool ok, string message, List<RoomDto> rooms)> ListRoomsAsync(
            CancellationToken ct = default)
        {
            if (!HasServer) return (false, "未配置服务器地址", new List<RoomDto>());
            try
            {
                using var req = Req(HttpMethod.Get, "/api/rooms");
                var list = await SendAsync<List<RoomDto>>(req, null, ct).ConfigureAwait(false);
                return (true, "", list ?? new List<RoomDto>());
            }
            catch (Exception ex) { return (false, ex.Message, new List<RoomDto>()); }
        }

        public sealed class CreateResult
        {
            public bool Ok { get; set; }
            public string Message { get; set; } = "";
            public RoomDto? Room { get; set; }
            public RoomDto? Big { get; set; }
            public RoomDto? Small { get; set; }
        }

        /// <summary>建私人房（发车人数 2-4 可选）。</summary>
        public static async Task<CreateResult> CreateFourAsync(
            string title, string code, int departSize = 0, CancellationToken ct = default)
        {
            if (!HasServer) return new CreateResult { Ok = false, Message = "未配置服务器地址" };
            try
            {
                using var req = Req(HttpMethod.Post, "/api/rooms/create");
                var r = await SendAsync<CreateResult>(req,
                    new { title, code, kind = 0, departSize }, ct).ConfigureAwait(false);
                return r ?? new CreateResult { Ok = false, Message = "服务器无响应" };
            }
            catch (Exception ex) { return new CreateResult { Ok = false, Message = ex.Message }; }
        }

        /// <summary>建私人·大小号房（成对，每边发车人数 2-4 可选）。</summary>
        public static async Task<CreateResult> CreatePairAsync(
            string title, string bigCode, string smallCode,
            int bigSize = 0, int smallSize = 0, CancellationToken ct = default)
        {
            if (!HasServer) return new CreateResult { Ok = false, Message = "未配置服务器地址" };
            try
            {
                using var req = Req(HttpMethod.Post, "/api/rooms/create-pair");
                var r = await SendAsync<CreateResult>(req,
                    new { title, bigCode, smallCode, bigSize, smallSize }, ct).ConfigureAwait(false);
                return r ?? new CreateResult { Ok = false, Message = "服务器无响应" };
            }
            catch (Exception ex) { return new CreateResult { Ok = false, Message = ex.Message }; }
        }

        /// <summary>用房间码加入。</summary>
        public static async Task<CreateResult> JoinAsync(
            string code, string displayName, string roleName,
            CancellationToken ct = default)
        {
            if (!HasServer) return new CreateResult { Ok = false, Message = "未配置服务器地址" };
            try
            {
                using var req = Req(HttpMethod.Post, "/api/rooms/join");
                var r = await SendAsync<CreateResult>(req,
                    new { code, displayName, roleName }, ct).ConfigureAwait(false);
                return r ?? new CreateResult { Ok = false, Message = "服务器无响应" };
            }
            catch (Exception ex) { return new CreateResult { Ok = false, Message = ex.Message }; }
        }

        /// <summary>离开房间。</summary>
        public static async Task<bool> LeaveAsync(string roomId, CancellationToken ct = default)
        {
            if (!HasServer) return false;
            try
            {
                using var req = Req(HttpMethod.Post, "/api/rooms/" + roomId + "/leave");
                var r = await SendAsync<SimpleResult>(req, null, ct).ConfigureAwait(false);
                return r?.Ok == true;
            }
            catch { return false; }
        }

        public sealed class DepartResult
        {
            public bool Ok { get; set; }
            public string Message { get; set; } = "";
            public List<DepartTarget> Targets { get; set; } = new();
        }

        public sealed class DepartTarget
        {
            public string DeviceCode { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public DepartConfig Config { get; set; } = new();
        }

        public sealed class DepartConfig
        {
            public string Mode { get; set; } = "";
            public string ActionId { get; set; } = "";
            public string DragonName { get; set; } = "";
            public string Groups { get; set; } = "";
            public string Summary { get; set; } = "";

            /// <summary>发车前先停止该账号的 BGI 脚本（默认开，一条龙必需）。</summary>
            public bool StopBgiFirst { get; set; } = true;

            /// <summary>发车前关闭该账号的原神。</summary>
            public bool CloseGenshin { get; set; } = false;
        }

        /// <summary>兼容旧名称（窗口里用它拼装配置）。</summary>
        public sealed class UserDepartDto
        {
            public string Mode { get; set; } = "";
            public string ActionId { get; set; } = "";
            public string DragonName { get; set; } = "";
            public string Groups { get; set; } = "";
            public string Summary { get; set; } = "";

            /// <summary>发车前是否先停止该账号正在跑的 BGI 脚本（一条龙要求空闲）。</summary>
            public bool StopBgiFirst { get; set; } = true;

            /// <summary>发车前是否关闭该账号的原神。</summary>
            public bool CloseGenshin { get; set; } = false;
        }

        /// <summary>发车。</summary>
        public static async Task<DepartResult> DepartAsync(string roomId, CancellationToken ct = default)
        {
            if (!HasServer) return new DepartResult { Ok = false, Message = "未配置服务器地址" };
            try
            {
                using var req = Req(HttpMethod.Post, "/api/rooms/depart");
                var r = await SendAsync<DepartResult>(req,
                    new { roomId }, ct).ConfigureAwait(false);
                return r ?? new DepartResult { Ok = false, Message = "服务器无响应" };
            }
            catch (Exception ex) { return new DepartResult { Ok = false, Message = ex.Message }; }
        }

        /// <summary>改房间码。</summary>
        public static async Task<SimpleResult> ChangeCodeAsync(
            string roomId, string newCode, CancellationToken ct = default)
        {
            if (!HasServer) return new SimpleResult { Ok = false, Message = "未配置服务器地址" };
            try
            {
                using var req = Req(HttpMethod.Post, "/api/rooms/change-code");
                var r = await SendAsync<SimpleResult>(req,
                    new { roomId, newCode }, ct).ConfigureAwait(false);
                return r ?? new SimpleResult { Ok = false, Message = "服务器无响应" };
            }
            catch (Exception ex) { return new SimpleResult { Ok = false, Message = ex.Message }; }
        }

        /// <summary>关闭房间。</summary>
        public static async Task<SimpleResult> DeleteAsync(string roomId, CancellationToken ct = default)
        {
            if (!HasServer) return new SimpleResult { Ok = false, Message = "未配置服务器地址" };
            try
            {
                using var req = Req(HttpMethod.Delete, "/api/rooms/" + roomId);
                var r = await SendAsync<SimpleResult>(req, null, ct).ConfigureAwait(false);
                return r ?? new SimpleResult { Ok = false, Message = "服务器无响应" };
            }
            catch (Exception ex) { return new SimpleResult { Ok = false, Message = ex.Message }; }
        }

        // ══════════ 发车动作配置 ══════════

        /// <summary>读本设备的发车动作。</summary>
        public static async Task<DepartConfig> GetDepartConfigAsync(CancellationToken ct = default)
        {
            if (!HasServer) return new DepartConfig();
            try
            {
                using var req = Req(HttpMethod.Get, "/api/depart-config");
                var r = await SendAsync<DepartConfig>(req, null, ct).ConfigureAwait(false);
                return r ?? new DepartConfig();
            }
            catch { return new DepartConfig(); }
        }

        /// <summary>保存本设备的发车动作。</summary>
        public static async Task<bool> SetDepartConfigAsync(
            DepartConfig cfg, CancellationToken ct = default)
        {
            if (!HasServer) return false;
            try
            {
                using var req = Req(HttpMethod.Post, "/api/depart-config");
                var r = await SendAsync<SimpleResult>(req, cfg, ct).ConfigureAwait(false);
                return r?.Ok == true;
            }
            catch { return false; }
        }

        public sealed class SimpleResult
        {
            public bool Ok { get; set; }
            public string Message { get; set; } = "";
        }
    }
}
