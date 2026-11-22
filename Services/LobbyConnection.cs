using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>连接状态。</summary>
    public enum ConnState
    {
        /// <summary>还没试过</summary>
        Unknown = 0,
        /// <summary>正在连接</summary>
        Connecting = 1,
        /// <summary>已连接</summary>
        Connected = 2,
        /// <summary>断开</summary>
        Disconnected = 3
    }

    /// <summary>
    /// 联机大厅连接管理：按需心跳 + 状态通知。
    ///
    /// ★★ 按需使用（用户被常驻流量搞欠费后的核心改造）：★★
    ///   旧实现是「启动即挂一个 10 秒 DispatcherTimer，永久 Ping + Heartbeat」，
    ///   哪怕用户从不打开联机页、没有房间也照样每分钟打服务器。
    ///
    ///   现在改成**活动引用计数**：
    ///   · 没有任何「活动」时：不注册任何节拍，不发任何请求（完全静默）。
    ///   · 有活动时（正在看联机页 / 在某个房间里）：走 TickHub 每 10 秒只发一次
    ///     轻量心跳（POST /api/heartbeat，一请求），用它同时充当「我还活着」和
    ///     「座位保活」，不再单独发 Ping。
    ///
    /// 活动来源：
    ///   · 联机页可见（LobbyPage.OnPageActivated/Deactivated 调用 Acquire/Release）
    ///   · 本机在某个房间里（SetInRoom，由 LobbyPage 拉完列表后判定并常驻）
    ///   两者任一存在就保持心跳，全部消失才彻底停。
    /// </summary>
    public sealed class LobbyConnection
    {
        private static readonly Lazy<LobbyConnection> _inst =
            new(() => new LobbyConnection());
        public static LobbyConnection Instance => _inst.Value;

        /// <summary>状态变化（在 UI 线程触发）。</summary>
        public event Action<ConnState, string>? StateChanged;

        public ConnState State { get; private set; } = ConnState.Unknown;
        public string LastError { get; private set; } = "";

        /// <summary>上次成功心跳的时间。</summary>
        public DateTime LastOkAt { get; private set; } = DateTime.MinValue;

        /// <summary>心跳间隔（秒）。服务端掉线阈值 60s，10s 一跳留足余量。</summary>
        public const int HeartbeatIntervalSeconds = 10;

        private readonly object _gate = new();
        private readonly HashSet<object> _watchers = new();
        private static readonly object InRoomKey = new();
        private IDisposable? _heartbeatSub;
        private bool _running;
        private bool _probing;

        private LobbyConnection() { }

        /// <summary>开始（幂等）。仅置运行标记，不再自动轮询。</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
        }

        /// <summary>停止监控（退出时可选调用）。</summary>
        public void Stop()
        {
            _running = false;
            StopHeartbeat();
        }

        /// <summary>声明一个活动（幂等）。任一个活动在，就保持心跳。</summary>
        public void Acquire(object key)
        {
            if (key == null) return;
            lock (_gate) _watchers.Add(key);
            EnsureHeartbeat();
        }

        /// <summary>释放一个活动。全部释放后心跳停止。</summary>
        public void Release(object key)
        {
            if (key == null) return;
            lock (_gate) _watchers.Remove(key);
            if (!IsActive) StopHeartbeat();
        }

        /// <summary>
        /// 本机是否在某个房间里（常驻活动）。在房间时即使离开联机页也保持心跳，
        /// 否则服务端 60s 内把座位判掉线。
        /// </summary>
        public void SetInRoom(bool inRoom)
        {
            lock (_gate)
            {
                if (inRoom) _watchers.Add(InRoomKey);
                else _watchers.Remove(InRoomKey);
            }
            if (inRoom) EnsureHeartbeat();
            else if (!IsActive) StopHeartbeat();
        }

        private bool IsActive { get { lock (_gate) return _watchers.Count > 0; } }

        private void EnsureHeartbeat()
        {
            if (_heartbeatSub != null) return;
            if (!_running) return;

            // ★ 周期任务走 TickHub（AGENTS.md），不新建 DispatcherTimer。
            _heartbeatSub = TickHub.Register("LobbyHeartbeat", HeartbeatIntervalSeconds, async () =>
            {
                if (!IsActive) return;
                await TickAsync().ConfigureAwait(true);
            });
        }

        private void StopHeartbeat()
        {
            _heartbeatSub?.Dispose();
            _heartbeatSub = null;
        }

        /// <summary>手动触发一次检测（用户点刷新 / 改地址后调）。含版本探测。</summary>
        public async Task<bool> CheckNowAsync()
        {
            return await ProbeAsync().ConfigureAwait(true);
        }

        /// <summary>一次心跳（活动期间的轻量保活）。</summary>
        private async Task TickAsync()
        {
            if (!_running || _probing) return;
            _probing = true;
            try
            {
                await HeartbeatProbeAsync().ConfigureAwait(true);
            }
            finally { _probing = false; }
        }

        /// <summary>只发心跳（1 请求），成功即视为在线。</summary>
        private async Task HeartbeatProbeAsync()
        {
            try
            {
                EnsureConnecting();
                bool ok = await LobbyApi.HeartbeatAsync().ConfigureAwait(true);

                if (ok)
                {
                    _failCount = 0;
                    LastOkAt = DateTime.Now;
                    SetState(ConnState.Connected, "");
                }
                else
                {
                    _failCount++;
                    SetState(ConnState.Disconnected, "心跳失败");
                }
            }
            catch (Exception ex)
            {
                _failCount++;
                SetState(ConnState.Disconnected, ex.Message);
            }
        }

        private int _failCount;

        /// <summary>完整探测：Ping（带版本）+ 心跳。用于手动检测。</summary>
        private async Task<bool> ProbeAsync()
        {
            try
            {
                EnsureConnecting();

                var (ok, msg) = await LobbyApi.PingAsync().ConfigureAwait(true);

                if (ok)
                {
                    try { await LobbyApi.HeartbeatAsync().ConfigureAwait(true); } catch { }

                    _failCount = 0;
                    LastOkAt = DateTime.Now;
                    SetState(ConnState.Connected, "");
                    return true;
                }

                _failCount++;
                SetState(ConnState.Disconnected, msg);
                return false;
            }
            catch (Exception ex)
            {
                _failCount++;
                SetState(ConnState.Disconnected, ex.Message);
                return false;
            }
        }

        private void EnsureConnecting()
        {
            if (State == ConnState.Unknown)
                SetState(ConnState.Connecting, "");
        }

        private void SetState(ConnState s, string err)
        {
            bool changed = s != State || err != LastError;
            State = s;
            LastError = err;

            if (changed) StateChanged?.Invoke(s, err);
        }

        /// <summary>状态的中文描述。</summary>
        public string StateText => State switch
        {
            ConnState.Connected => "已连接",
            ConnState.Connecting => "连接中",
            ConnState.Disconnected => "未连接",
            _ => "未知"
        };
    }
}
