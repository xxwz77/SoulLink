namespace SoulLink.Services;

internal enum RdpEscapeAction { None, SendRemote, ExitLocal }

// One physical Esc press owns both edges, even if its original input context disappears.
internal sealed class RdpEscapeGesture
{
    internal const int HoldMilliseconds = 650;
    private enum State { Idle, PassingThrough, Waiting, Suppressing }
    private State _state;
    private long _started;

    internal bool IsIdle => _state == State.Idle;

    internal bool Down(bool canCapture, long now, out bool started)
    {
        started = false;
        if (_state != State.Idle) return _state is State.Waiting or State.Suppressing;
        _state = canCapture ? State.Waiting : State.PassingThrough;
        if (!canCapture) return false;
        _started = now;
        started = true;
        return true;
    }

    internal bool Up(long now, out RdpEscapeAction action)
    {
        action = _state == State.Waiting
            ? now - _started >= HoldMilliseconds ? RdpEscapeAction.ExitLocal : RdpEscapeAction.SendRemote
            : RdpEscapeAction.None;
        bool swallow = _state is State.Waiting or State.Suppressing;
        _state = State.Idle;
        return swallow;
    }

    internal bool Hold()
    {
        if (_state != State.Waiting) return false;
        _state = State.Suppressing;
        return true;
    }

    internal void Cancel() => Hold();
}
