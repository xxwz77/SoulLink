using System;
using System.Collections.Generic;

namespace SoulLink.Relay;

/// <summary>One viewer's bounded backlog. The caller serializes reads and writes.</summary>
internal sealed class ViewerFrameQueue<T>
{
    private readonly int _capacity;
    private readonly Queue<T> _pending = new();
    private bool _waitingForIdr;
    internal int Count => _pending.Count;

    internal ViewerFrameQueue(int capacity, bool hasInitialIdr)
    {
        _capacity = Math.Max(1, capacity);
        _waitingForIdr = !hasInitialIdr;
    }

    internal bool Offer(T frame, bool idr, out int dropped, out bool requestIdr)
    {
        dropped = 0;
        requestIdr = false;
        if (idr)
        {
            dropped = _pending.Count;
            _pending.Clear();
            _waitingForIdr = false;
        }
        else if (_waitingForIdr || _pending.Count == _capacity)
        {
            // Queued frames still form a valid chain. The frame we missed breaks
            // the *next* chain. Resume on a fresh IDR once the viewer can keep up.
            _waitingForIdr = true;
            dropped = 1;
            requestIdr = _pending.Count == 0;
            return false;
        }
        _pending.Enqueue(frame);
        return true;
    }

    internal T Dequeue() => _pending.Dequeue();
}
