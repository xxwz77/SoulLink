using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 一个会话的输入控制器：把「界面上的鼠标动作」翻译成远端会话的注入指令。
    ///
    /// 双模式（HANDOFF 3.1 的结论）：
    ///   · 绝对定位 SetCursorPos —— 点击、悬停，实测误差 0
    ///   · 相对位移 mouse_event(MOVE, dx, dy) —— 转游戏视角，**只有这个对游戏有效**
    ///     （游戏读 Raw Input，SetCursorPos 那种跳变它不认）
    ///
    /// 转视角的完整手势是：按住右键 → 相对移动 → 松开右键。
    /// </summary>
    public sealed class SessionInput : IDisposable
    {
        public RelayClient Relay { get; }
        public CoordinateMapper Mapper { get; } = new CoordinateMapper();

        /// <summary>相对位移灵敏度（1.0 = 直接换算）</summary>
        public double Sensitivity { get; set; } = 1.0;

        /// <summary>是否处于「按住右键转视角」状态</summary>
        public bool IsLooking { get; private set; }

        private readonly Stopwatch _clickGuard = Stopwatch.StartNew();

        public event Action<SessionInput, string>? Activity;

        public SessionInput(RelayClient relay)
        {
            Relay = relay;
        }

        private void Note(string text) => Activity?.Invoke(this, text);

        // ── 绝对定位 ─────────────────────────────────────────────────────────

        /// <summary>把远端光标移到 UI 坐标对应的位置。</summary>
        public async Task<bool> MoveToUiAsync(double uiX, double uiY)
        {
            if (!Relay.IsConnected) return false;
            var (rx, ry) = Mapper.ToRemote(uiX, uiY);
            var r = await Relay.MoveAsync(rx, ry).ConfigureAwait(false);
            return r?.Ok == true;
        }

        /// <summary>在 UI 坐标处点击。</summary>
        public async Task<bool> ClickUiAsync(double uiX, double uiY, string button = "left", int count = 1)
        {
            if (!Relay.IsConnected) return false;

            // 绝对定位 + 点击之间留一点时间，否则某些程序会按旧位置响应
            bool moved = await MoveToUiAsync(uiX, uiY).ConfigureAwait(false);
            if (!moved) return false;
            await Task.Delay(12).ConfigureAwait(false);

            var r = await Relay.ClickAsync(0, 0, button, count).ConfigureAwait(false);
            Note(button + " 点击" + (count > 1 ? " x" + count : "") + " @ " + (int)uiX + "," + (int)uiY);
            return r?.Ok == true;
        }

        // ── 转视角（相对位移）────────────────────────────────────────────────

        /// <summary>开始转视角：按住右键。</summary>
        public async Task<bool> BeginLookAsync()
        {
            if (!Relay.IsConnected || IsLooking) return IsLooking;
            var r = await Relay.DownAsync("right").ConfigureAwait(false);
            IsLooking = r?.Ok == true;
            if (IsLooking) Note("开始转视角（右键按住）");
            return IsLooking;
        }

        /// <summary>
        /// 转视角：**直接下发像素位移，不做坐标缩放**。
        ///
        /// ★ 为什么不能用 ScaleRelative：捕获模式下光标是锁死的，
        ///   位移已经是原始物理量，再按画面缩放折算会让高 DPI 鼠标越转越慢
        ///   （第一版实测：DPI 1600 的鼠标转起来比前台慢好几倍）。
        /// </summary>
        public async Task<bool> LookRawAsync(int dx, int dy)
        {
            if (!Relay.IsConnected) return false;
            if (dx == 0 && dy == 0) return true;
            var r = await Relay.MoveRelAsync(dx, dy).ConfigureAwait(false);
            return r?.Ok == true;
        }

        /// <summary>按 UI 位移转视角（会按画面缩放补偿，用于非捕获场景）。</summary>
        public async Task<bool> LookAsync(double deltaUiX, double deltaUiY)
        {
            if (!Relay.IsConnected) return false;
            var (dx, dy) = Mapper.ScaleRelative(deltaUiX, deltaUiY, Sensitivity);
            if (dx == 0 && dy == 0) return true;
            var r = await Relay.MoveRelAsync(dx, dy).ConfigureAwait(false);
            if (r?.Ok == true && r.Message?.Contains("回中") == true)
                Note("光标贴边，已回中继续");
            return r?.Ok == true;
        }

        /// <summary>结束转视角：松开右键。</summary>
        public async Task<bool> EndLookAsync()
        {
            if (!Relay.IsConnected) { IsLooking = false; return false; }
            bool was = IsLooking;
            IsLooking = false;
            var r = await Relay.UpAsync("right").ConfigureAwait(false);
            if (was) Note("结束转视角");
            return r?.Ok == true;
        }

        // ── 滚轮 ─────────────────────────────────────────────────────────────

        public async Task<bool> WheelAsync(int delta)
        {
            if (!Relay.IsConnected) return false;
            var r = await Relay.WheelAsync(delta).ConfigureAwait(false);
            Note("滚轮 " + delta);
            return r?.Ok == true;
        }

        // ── 状态 ─────────────────────────────────────────────────────────────

        public async Task<string> ProbeAsync()
        {
            var cur = await Relay.CursorAsync().ConfigureAwait(false);
            if (cur == null) return "无响应";
            Mapper.Update(cur.ScreenWidth, cur.ScreenHeight, Mapper.ViewWidth, Mapper.ViewHeight);

            var w = await Relay.ForegroundAsync().ConfigureAwait(false);
            string fg = w == null || string.IsNullOrEmpty(w.Message) ? "(无前台窗口)" : w.Message;
            return "光标 " + cur.X + "," + cur.Y + " · 屏幕 " + cur.ScreenWidth + "x" + cur.ScreenHeight
                   + " · 前台：" + fg;
        }

        /// <summary>关键诊断：Disc 的会话没有可交互桌面，SetCursorPos 会被拒绝。</summary>
        public async Task<(bool usable, string message)> CheckUsableAsync()
        {
            if (!Relay.IsConnected) return (false, "注入代理未连接");

            var cur = await Relay.CursorAsync().ConfigureAwait(false);
            if (cur == null) return (false, "代理无响应");

            int w = cur.ScreenWidth, h = cur.ScreenHeight;
            if (w <= 0 || h <= 0) return (false, "代理报告的分辨率异常：" + w + "x" + h);

            // 探测：能不能真的移动光标
            var before = await Relay.CursorAsync().ConfigureAwait(false);
            var mv = await Relay.MoveAsync(w / 2, h / 2).ConfigureAwait(false);
            var after = await Relay.CursorAsync().ConfigureAwait(false);

            bool moved = mv?.Ok == true && after != null && Math.Abs(after.X - w / 2) <= 2 && Math.Abs(after.Y - h / 2) <= 2;
            if (moved) return (true, "会话可交互 · 屏幕 " + w + "x" + h);

            return (false,
                "会话当前不可交互（光标被锁在 " + (after?.X ?? -1) + "," + (after?.Y ?? -1)
                + "）。断开状态的会话没有活动桌面，必须先让画面连上（RDP 接管）才能注入。");
        }

        public void Dispose()
        {
            // 释放由 RelayClient.Dispose 统一 releaseall，禁止在 UI 上等待按键请求。
            try { Relay.Dispose(); } catch { }
        }
    }
}
