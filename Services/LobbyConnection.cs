using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

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
    /// 联机大厅连接管理：心跳 + 自动重连 + 状态通知。
    ///
    /// ★ 行为：
    ///   · 启动时立即探测一次，然后每 10 秒心跳
    ///   · 连续失败后进入"断开"状态，按 2→4→8→16→30 秒退避重试
    ///   · 恢复后立即退出退避，回到 10 秒心跳
    ///   · 状态变化通过 StateChanged 事件通知界面（指示灯）
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

        private DispatcherTimer? _timer;
        private bool _running;
        private bool _probing;
        private int _failCount;

        private LobbyConnection() { }

        /// <summary>开始监控（幂等）。</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _timer.Tick += async (_, __) => await TickAsync().ConfigureAwait(true);
            _timer.Start();

            // 立即探一次
            _ = TickAsync();
        }

        /// <summary>停止监控。</summary>
        public void Stop()
        {
            _running = false;
            _timer?.Stop();
            _timer = null;
        }

        /// <summary>手动触发一次检测（用户点刷新时调）。</summary>
        public async Task<bool> CheckNowAsync()
        {
            return await ProbeAsync().ConfigureAwait(true);
        }

        /// <summary>一次心跳 / 探测。</summary>
        private async Task TickAsync()
        {
            if (!_running || _probing) return;
            _probing = true;
            try
            {
                await ProbeAsync().ConfigureAwait(true);
            }
            finally { _probing = false; }
        }

        private async Task<bool> ProbeAsync()
        {
            try
            {
                EnsureConnecting();

                var (ok, msg) = await LobbyApi.PingAsync().ConfigureAwait(true);

                if (ok)
                {
                    // 心跳：告诉服务器"我还在"
                    try { await LobbyApi.HeartbeatAsync().ConfigureAwait(true); } catch { }

                    _failCount = 0;
                    LastOkAt = DateTime.Now;
                    SetState(ConnState.Connected, "");
                    return true;
                }

                _failCount++;
                SetState(ConnState.Disconnected, msg);
                ApplyBackoff();
                return false;
            }
            catch (Exception ex)
            {
                _failCount++;
                SetState(ConnState.Disconnected, ex.Message);
                ApplyBackoff();
                return false;
            }
        }

        private void EnsureConnecting()
        {
            if (State == ConnState.Unknown)
                SetState(ConnState.Connecting, "");
        }

        /// <summary>失败后调整下次检查间隔（指数退避）。</summary>
        private void ApplyBackoff()
        {
            if (_timer == null) return;

            int sec = _failCount switch
            {
                <= 1 => 5,
                2 => 10,
                3 => 20,
                _ => 30
            };

            _timer.Interval = TimeSpan.FromSeconds(sec);
        }

        /// <summary>成功后恢复常规间隔。</summary>
        private void ResetInterval()
        {
            if (_timer != null && _timer.Interval.TotalSeconds > 10)
                _timer.Interval = TimeSpan.FromSeconds(10);
        }

        private void SetState(ConnState s, string err)
        {
            bool changed = s != State || err != LastError;
            State = s;
            LastError = err;

            if (s == ConnState.Connected) ResetInterval();

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
