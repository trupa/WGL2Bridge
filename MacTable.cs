using System.Collections.Concurrent;
using System.Diagnostics;

namespace WGL2Bridge;

public enum BridgePort : byte
{
    /// <summary>The local TAP adapter.</summary>
    Tap,

    /// <summary>The encapsulated tunnel to the remote peer.</summary>
    Tunnel
}

/// <summary>
/// Learning bridge forwarding database. Unicast frames whose destination was last seen on the
/// ingress port are dropped instead of being flooded across the tunnel; broadcast, multicast and
/// unknown destinations still flood, which is standard 802.1D behaviour.
/// </summary>
public sealed class MacTable(TimeSpan agingTime, int capacity = 4096)
{
    private readonly ConcurrentDictionary<ulong, Entry> _entries = new();
    private readonly long _agingTicks = agingTime.Ticks;
    private readonly int _capacity = capacity;

    private readonly struct Entry(BridgePort port, long timestamp)
    {
        public readonly BridgePort Port = port;
        public readonly long Timestamp = timestamp;
    }

    /// <summary>
    /// Learns the source address and decides whether the frame may cross to the other port.
    /// </summary>
    /// <param name="frame">Ethernet frame: bytes 0-5 destination MAC, bytes 6-11 source MAC.</param>
    /// <param name="ingress">Port the frame arrived on.</param>
    public bool ShouldForward(ReadOnlySpan<byte> frame, BridgePort ingress)
    {
        if (frame.Length < 12)
        {
            return false;
        }

        long now = Stopwatch.GetTimestamp();
        Learn(frame[6..12], ingress, now);

        // Bit 0 of the first destination byte marks broadcast/multicast: always flooded.
        if ((frame[0] & 0x01) != 0)
        {
            return true;
        }

        return !IsKnownOn(frame[..6], ingress, now);
    }

    private void Learn(ReadOnlySpan<byte> sourceMac, BridgePort port, long now)
    {
        // Never learn from a multicast source address; it is invalid and would poison the table.
        if ((sourceMac[0] & 0x01) != 0)
        {
            return;
        }

        ulong key = ToKey(sourceMac);
        var entry = new Entry(port, now);

        if (_entries.TryGetValue(key, out Entry existing))
        {
            // Refresh timestamps lazily: only rewrite when the port moved or the entry is half-aged.
            if (existing.Port != port || now - existing.Timestamp > _agingTicks / 2)
            {
                _entries[key] = entry;
            }

            return;
        }

        if (_entries.Count >= _capacity)
        {
            Prune(now);
            if (_entries.Count >= _capacity)
            {
                return;
            }
        }

        _entries.TryAdd(key, entry);
    }

    private bool IsKnownOn(ReadOnlySpan<byte> mac, BridgePort port, long now)
    {
        if (!_entries.TryGetValue(ToKey(mac), out Entry entry))
        {
            return false;
        }

        if (now - entry.Timestamp > _agingTicks)
        {
            _entries.TryRemove(ToKey(mac), out _);
            return false;
        }

        return entry.Port == port;
    }

    private void Prune(long now)
    {
        foreach (KeyValuePair<ulong, Entry> pair in _entries)
        {
            if (now - pair.Value.Timestamp > _agingTicks)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private static ulong ToKey(ReadOnlySpan<byte> mac) =>
        ((ulong)mac[0] << 40) | ((ulong)mac[1] << 32) | ((ulong)mac[2] << 24) |
        ((ulong)mac[3] << 16) | ((ulong)mac[4] << 8) | mac[5];
}
