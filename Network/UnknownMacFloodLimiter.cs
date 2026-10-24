namespace WGL2Bridge.Network;

/// <summary>
/// Per-destination-MAC flood budget for unknown unicast, used in Lan/server mode only.
/// <para>
/// When the server receives a frame from the LAN addressed to a MAC it cannot attribute to a
/// connected client, the only correct action is to copy it to every client — which multiplies the
/// LAN ingress rate by the client count. This limiter caps how many copies a <em>single</em>
/// destination MAC may generate per second, so one misbehaving or one-way sender cannot occupy the
/// tunnel, while other MACs are unaffected.
/// </para>
/// <para>
/// State is a fixed-size open-addressed table (no per-MAC dictionary), so a flood of distinct
/// destination MACs cannot grow memory. When the table is full the oldest window is recycled.
/// Locking is a short critical section: unknown unicast is not the hot path.
/// </para>
/// </summary>
public sealed class UnknownMacFloodLimiter
{
    /// <summary>Fixed number of destination MACs tracked concurrently.</summary>
    public const int Capacity = 256;

    private struct Slot
    {
        public bool Used;
        public bool Warned;
        public ulong Mac;
        public long WindowStartMs;
        public int Count;
    }

    private readonly Slot[] _slots = new Slot[Capacity];
    private readonly int _maxPerSecond;
    private readonly object _lock = new();

    public UnknownMacFloodLimiter(int maxPerSecond) => _maxPerSecond = maxPerSecond;

    /// <summary>Frames per second allowed per destination MAC; 0 means unlimited.</summary>
    public int MaxPerSecond => _maxPerSecond;

    /// <summary>Returns true if a copy of the frame may be flooded now.</summary>
    public bool Allow(ulong mac, long nowMs) => Allow(mac, nowMs, out _);

    /// <summary>
    /// Returns true if a copy of the frame may be flooded now. <paramref name="firstClip"/> is true
    /// exactly once per tracked MAC (until its slot is recycled), so the caller can log the first
    /// drop without flooding the log.
    /// </summary>
    public bool Allow(ulong mac, long nowMs, out bool firstClip)
    {
        firstClip = false;
        if (_maxPerSecond <= 0)
        {
            return true;
        }

        int start = Hash(mac);

        lock (_lock)
        {
            int empty = -1;
            int oldest = -1;
            long oldestMs = long.MaxValue;

            for (int probe = 0; probe < Capacity; probe++)
            {
                int index = (start + probe) % Capacity;
                ref Slot slot = ref _slots[index];

                if (!slot.Used)
                {
                    if (empty < 0)
                    {
                        empty = index;
                    }

                    continue;
                }

                if (slot.Mac == mac)
                {
                    return AllowOnSlot(ref slot, nowMs, out firstClip);
                }

                if (slot.WindowStartMs < oldestMs)
                {
                    oldestMs = slot.WindowStartMs;
                    oldest = index;
                }
            }

            int target = empty >= 0 ? empty : (oldest >= 0 ? oldest : start);
            _slots[target] = new Slot { Used = true, Mac = mac, WindowStartMs = nowMs, Count = 1 };
            return true;
        }
    }

    private bool AllowOnSlot(ref Slot slot, long nowMs, out bool firstClip)
    {
        firstClip = false;

        if (nowMs - slot.WindowStartMs >= 1000)
        {
            slot.WindowStartMs = nowMs;
            slot.Count = 0;
        }

        if (slot.Count >= _maxPerSecond)
        {
            firstClip = !slot.Warned;
            slot.Warned = true;
            return false;
        }

        slot.Count++;
        return true;
    }

    private static int Hash(ulong mac)
    {
        ulong x = mac * 0x9E3779B97F4A7C15UL;
        x ^= x >> 29;
        return (int)(x % Capacity);
    }
}
