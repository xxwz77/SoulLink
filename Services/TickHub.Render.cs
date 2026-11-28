using System;
using System.Windows.Media;

namespace SoulLink.Services;

public static partial class TickHub
{
    private static Action<TimeSpan>? RenderSubscribers;

    // Visual-only work shares the compositor clock and stops when the last visible scene detaches.
    internal static IDisposable RegisterRenderFrames(int framesPerSecond, Action<TimeSpan> callback)
    {
        var clock = new RenderSubscription(Math.Clamp(framesPerSecond, 1, 30), callback);
        if (RenderSubscribers == null) CompositionTarget.Rendering += DispatchRender;
        RenderSubscribers += clock.Tick;
        return clock;
    }

    private static void DispatchRender(object? sender, EventArgs args)
    {
        if (args is RenderingEventArgs frame) RenderSubscribers?.Invoke(frame.RenderingTime);
    }

    private sealed class RenderSubscription(int framesPerSecond, Action<TimeSpan> callback) : IDisposable
    {
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(1.0 / framesPerSecond);
        private TimeSpan? _lastFrame;
        private TimeSpan _nextFrame;

        public void Tick(TimeSpan now)
        {
            if (now < _nextFrame || _lastFrame == now) return;
            if (_lastFrame is TimeSpan last) callback(now - last);
            _lastFrame = now;
            _nextFrame += _interval;
            if (_nextFrame <= now) _nextFrame = now + _interval;
        }

        public void Dispose()
        {
            RenderSubscribers -= Tick;
            if (RenderSubscribers == null) CompositionTarget.Rendering -= DispatchRender;
        }
    }
}
