namespace WGL2Bridge.Network;

/// <summary>
/// Learning bridge MAC table (IEEE 802.1D). Source MACs are learned per ingress port; known-local
/// unicast is suppressed while broadcast, multicast and unknown destinations still flood. The table
/// is a fixed-size, open-addressed hash map keyed on a 48-bit MAC so steady-state operation performs
/// no allocations.
/// </summary>
public sealed class MacTable
{
    /// <summary>Ingress port a MAC was last seen on.</summary>
    public enum Port : byte
    {
        None = 0,
        Tap = 1,
        Tunnel = 2,
    }

    private struct Entry
    {
        public ulong Mac;
        public Port Port;
        public long LastSeenMs;
    }

    private readonly Entry[] _buckets;
    private readonly int _mask;
    private readonly long _agingMs;
    private readonly object _lock = new();

    /// <summary>Creates a MAC table rounded up to a power-of-two capacity.</summary>
    public MacTable(int capacity = 2048, TimeSpan? aging = null)
    {
        int size = 1;
        while (size < capacity)
        {
            size <<= 1;
        }

        _buckets = new Entry[size];
        _mask = size - 1;
        _agingMs = (long)(aging ?? TimeSpan.FromSeconds(300)).TotalMilliseconds;
    }

    /// <summary>Learns (or refreshes) the mapping of a source MAC to an ingress port.</summary>
    public void Learn(ReadOnlySpan<byte> mac, Port port, long nowMs)
    {
        if (mac.Length < Ethernet.MacLength)
        {
            return;
        }

        ulong key = Ethernet.ReadMac(mac);
        if (key == 0 || Ethernet.IsMulticast(key))
        {
            return;
        }

        int index = (int)(Hash(key) & (uint)_mask);

        lock (_lock)
        {
            for (int probe = 0; probe < _buckets.Length; probe++)
            {
                ref Entry entry = ref _buckets[index];

                if (entry.Mac == key)
                {
                    entry.Port = port;
                    entry.LastSeenMs = nowMs;
                    return;
                }

                if (entry.Mac == 0)
                {
                    entry = new Entry { Mac = key, Port = port, LastSeenMs = nowMs };
                    return;
                }

                index = (index + 1) & _mask;
            }

            // Table full: evict the probed slot (best-effort).
            _buckets[index] = new Entry { Mac = key, Port = port, LastSeenMs = nowMs };
        }
    }

    /// <summary>Resolves the port a destination MAC was last seen on, or <see cref="Port.None"/>.</summary>
    public Port Resolve(ReadOnlySpan<byte> mac, long nowMs)
    {
        if (mac.Length < Ethernet.MacLength)
        {
            return Port.None;
        }

        ulong key = Ethernet.ReadMac(mac);
        int index = (int)(Hash(key) & (uint)_mask);

        lock (_lock)
        {
            for (int probe = 0; probe < _buckets.Length; probe++)
            {
                ref Entry entry = ref _buckets[index];

                if (entry.Mac == 0)
                {
                    return Port.None;
                }

                if (entry.Mac == key)
                {
                    if (nowMs - entry.LastSeenMs > _agingMs)
                    {
                        entry = default; // expired
                        return Port.None;
                    }

                    return entry.Port;
                }

                index = (index + 1) & _mask;
            }
        }

        return Port.None;
    }

    /// <summary>
    /// True if a frame arriving on <paramref name="ingressPort"/> should be forwarded to the other
    /// port: broadcast/multicast/unknown destinations flood, known-same-port unicast is suppressed.
    /// </summary>
    public bool ShouldForward(ReadOnlySpan<byte> destMac, Port ingressPort, long nowMs)
    {
        ulong key = Ethernet.ReadMac(destMac);
        if (key == Ethernet.BroadcastMac || Ethernet.IsMulticast(key))
        {
            return true;
        }

        return Resolve(destMac, nowMs) != ingressPort;
    }

    private static ulong Hash(ulong key)
    {
        key ^= key >> 33;
        key *= 0xFF51AFD7ED558CCD;
        key ^= key >> 33;
        return key;
    }
}
