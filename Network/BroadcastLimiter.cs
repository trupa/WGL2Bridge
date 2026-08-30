namespace WGL2Bridge.Network;

/// <summary>
/// Simple per-second rate limiter for broadcast/multicast frames, used as storm control so a
/// misbehaving segment cannot flood the WAN link. Zero allocations; guarded by a tiny critical
/// section (broadcast frames are not the hot path).
/// </summary>
public sealed class BroadcastLimiter
{
    private readonly int _maxPerSecond;
    private readonly object _lock = new();
    private long _windowStartMs;
    private int _count;

    public BroadcastLimiter(int maxPerSecond) => _maxPerSecond = maxPerSecond;

    /// <summary>Returns true if the frame may be forwarded now, false if the rate limit is exceeded.</summary>
    public bool Allow(long nowMs)
    {
        if (_maxPerSecond <= 0)
        {
            return true;
        }

        lock (_lock)
        {
            if (nowMs - _windowStartMs >= 1000)
            {
                _windowStartMs = nowMs;
                _count = 0;
            }

            if (_count >= _maxPerSecond)
            {
                return false;
            }

            _count++;
            return true;
        }
    }
}
