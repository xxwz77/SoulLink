using System;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 自由视角的有界输入队列：钩子只入队，不执行 IPC/IO/等待。
    /// 位移合并为最新累积值；按键按顺序发送，避免丢失 down/up 状态。
    /// </summary>
    public sealed class LookInputQueue : IDisposable
    {
        private readonly Func<SessionInput> _input;
        private readonly Func<double> _sensitivity;
        private readonly Action<string>? _diagnostic;
        private readonly Lock _sync = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly Task _worker;
        private int _dx, _dy;
        private bool _dirty;
        private bool _disposed;
        private bool _accepting = true;
        private long _buttonsSent;
        private long _movesSent;

        public LookInputQueue(Func<SessionInput> input, Func<double> sensitivity, Action<string>? diagnostic = null)
        {
            _input = input;
            _sensitivity = sensitivity;
            _diagnostic = diagnostic;
            _worker = Task.Run(WorkAsync);
        }

        public void EnqueueMove(int dx, int dy)
        {
            lock (_sync)
            {
                if (!_accepting) return;
                _dx += dx; _dy += dy;
                _dirty = true;
                _wake.Set();
            }
        }

        public void EnqueueButton(string button, bool down) => Enqueue(new Command(button, down));
        public void EnqueueWheel(int delta) => Enqueue(new Command(delta));

        private void Enqueue(Command command)
        {
            lock (_sync)
            {
                if (!_accepting) return;
                // down/up 是状态，不能像位移一样任意丢弃。满时合并只保留最新释放，
                // 队列清空由后台按序完成，不会变成“每次点击一个新任务”。
                if (_buttons.Count >= 64) return;
                _buttons.Enqueue(command);
                _wake.Set();
            }
        }

        private readonly System.Collections.Generic.Queue<Command> _buttons = new();
        private readonly struct Command
        {
            public Command(string button, bool down) { Button = button; Down = down; Wheel = 0; }
            public Command(int wheel) { Button = ""; Wheel = wheel; Down = false; }
            public string Button { get; }
            public bool Down { get; }
            public int Wheel { get; }
        }

        private async Task WorkAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested || HasPending)
                {
                    _wake.WaitOne(20);
                    while (true)
                    {
                        Command? command = null;
                        int dx = 0, dy = 0;
                        lock (_sync)
                        {
                            if (_buttons.Count > 0) command = _buttons.Dequeue();
                            else if (_dirty)
                            {
                                dx = _dx; dy = _dy;
                                _dx = _dy = 0; _dirty = false;
                            }
                            else break;
                        }

                        try
                        {
                            var input = _input();
                            if (!input.Relay.IsConnected) continue;
                            if (command is { } c)
                            {
                                if (c.Wheel != 0) await input.Relay.WheelAsync(c.Wheel).ConfigureAwait(false);
                                else if (c.Down) await input.Relay.DownAsync(c.Button).ConfigureAwait(false);
                                else await input.Relay.UpAsync(c.Button).ConfigureAwait(false);
                                Interlocked.Increment(ref _buttonsSent);
                            }
                            else
                            {
                                double sensitivity = _sensitivity();
                                int sx = (int)Math.Round(dx * sensitivity);
                                int sy = (int)Math.Round(dy * sensitivity);
                                await input.LookRawAsync(sx, sy).ConfigureAwait(false);
                                Interlocked.Increment(ref _movesSent);
                            }
                        }
                        catch (Exception ex) { _diagnostic?.Invoke("视角队列发送失败：" + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { _diagnostic?.Invoke("视角队列退出：" + ex.Message); }
        }

        private bool HasPending { get { lock (_sync) return _dirty || _buttons.Count > 0; } }

        public (long buttons, long moves, int queued) Statistics
        {
            get { lock (_sync) return (_buttonsSent, _movesSent, _buttons.Count + (_dirty ? 1 : 0)); }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                // 退出自由视角不再追赶旧位移；按键释放由 releaseall 独立保证。
                _dx = _dy = 0;
                _dirty = false;
                _buttons.Clear();
            }
            _cts.Cancel();
            _wake.Set();
            // 不阻塞 UI 等待网络或消息循环；资源由后台发送循环结束时回收。
            _ = _worker.ContinueWith(_ =>
            {
                try { _wake.Dispose(); } catch { }
                try { _cts.Dispose(); } catch { }
            }, TaskScheduler.Default);
        }
    }
}
