using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WGL2Bridge.Network;

/// <summary>
/// Loop detector using 0x88B5 probes tagged with a per-instance random ID. Only our own probe
/// returning raises the alarm, so foreign probe traffic on the segment is ignored.
/// </summary>
public sealed class LoopDetector
{
    /// <summary>Total probe frame length: 14-byte header + 8-byte payload.</summary>
    public const int ProbeLength = 22;

    private readonly uint _instanceId;
    private long _lastProbeMs;

    /// <summary>Generates a fresh per-instance probe ID.</summary>
    public LoopDetector()
    {
        _instanceId = unchecked((uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue));
    }

    /// <summary>True once our own probe has been observed returning to the bridge.</summary>
    public bool LoopDetected { get; private set; }

    /// <summary>
    /// Builds a broadcast 0x88B5 probe carrying the magic + instance ID into <paramref name="destination"/>.
    /// Returns the number of bytes written.
    /// </summary>
    public int BuildProbe(Span<byte> destination, ReadOnlySpan<byte> sourceMac)
    {
        if (destination.Length < ProbeLength)
        {
            throw new ArgumentException($"Probe buffer too small ({destination.Length} < {ProbeLength}).", nameof(destination));
        }

        destination[..6].Fill(0xFF);
        sourceMac.CopyTo(destination[6..12]);
        BinaryPrimitives.WriteUInt16BigEndian(destination[12..14], Ethernet.LoopProbe);
        "WGL2"u8.CopyTo(destination[14..18]);
        BinaryPrimitives.WriteUInt32BigEndian(destination[18..22], _instanceId);

        return ProbeLength;
    }

    /// <summary>
    /// Inspects an incoming frame and returns true exactly once, when our own probe has returned.
    /// </summary>
    public bool Observe(ReadOnlySpan<byte> frame)
    {
        if (LoopDetected || frame.Length < ProbeLength)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(frame[12..14]) != Ethernet.LoopProbe)
        {
            return false;
        }

        if (!frame[14..18].SequenceEqual("WGL2"u8))
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(frame[18..22]) != _instanceId)
        {
            return false;
        }

        LoopDetected = true;
        return true;
    }

    /// <summary>True when at least <paramref name="interval"/> has elapsed since the last probe.</summary>
    public bool TimeToSend(long nowMs, TimeSpan interval)
    {
        if (nowMs - _lastProbeMs < interval.TotalMilliseconds)
        {
            return false;
        }

        _lastProbeMs = nowMs;
        return true;
    }
}
