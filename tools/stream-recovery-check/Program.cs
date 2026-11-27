using SoulLink.Relay;

static void Check(bool result, string label)
{
    if (!result) throw new InvalidOperationException(label);
    Console.WriteLine("PASS " + label);
}
var q = new ViewerFrameQueue<int>(2, false);
Check(!q.Offer(1, false, out _, out bool request) && request, "new viewer waits for an IDR");
Check(q.Offer(2, true, out _, out _) && q.Offer(3, false, out _, out _), "queue contains a complete reference chain");
Check(!q.Offer(4, false, out int dropped, out _) && dropped == 1 && q.Count == 2, "bounded even when the first queued frame is an IDR");
Check(q.Dequeue() == 2 && !q.Offer(5, false, out _, out _) && q.Dequeue() == 3, "drains valid frames without appending broken deltas");
Check(!q.Offer(6, false, out _, out request) && request, "drained viewer requests resynchronisation");
Check(q.Offer(7, true, out _, out _) && q.Offer(8, false, out _, out _) && q.Dequeue() == 7 && q.Dequeue() == 8, "fresh IDR recovers the viewer");
var other = new ViewerFrameQueue<int>(2, true);
Check(other.Offer(9, false, out _, out _) && other.Offer(10, true, out dropped, out _) && dropped == 1 && other.Dequeue() == 10, "new IDR supersedes any pending older frames");
