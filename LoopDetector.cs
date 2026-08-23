using System.Runtime.Versioning;

namespace WGL2Bridge;

/// <summary>
/// Detects Layer 2 loops by periodically emitting a tagged probe frame on both ports.
/// Receiving a probe carrying this instance's own identifier means the frame travelled out of one
/// port and came back in through another, which can only happen if a second path exists.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LoopDetector
{
    // IEEE 802a local experimental EtherType; safe for site-local use.
    private const ushort ProbeEtherType = 0x88B5;
    private const int ProbeFrameSize = 60; // Minimum Ethernet frame without FCS.
    private const int MagicOffset = 14;
    private const int InstanceOffset = 22;
    private const int PortOffset = 38;

    private static ReadOnlySpan<byte> Magic => "WGL2LOOP"u8;

    private readonly IBridgeTransport _transport;
    private readonly TapAdapter _tap;
    private readonly TimeSpan _interval;
    private readonly bool _stopOnDetection;
    private readonly BridgeLog _log;
    private readonly CancellationTokenSource _shutdown;
    private readonly byte[] _instanceId = Guid.NewGuid().ToByteArray();
    private readonly byte[] _tunnelBuffer;
    private readonly byte[] _tapBuffer;

    private bool LoopDetected { get; set; }

    public LoopDetector(
        IBridgeTransport transport,
        TapAdapter tap,
        TimeSpan interval,
        bool stopOnDetection,
        BridgeLog log,
        CancellationTokenSource shutdown)
    {
        _transport = transport;
        _tap = tap;
        _interval = interval;
        _stopOnDetection = stopOnDetection;
        _log = log;
        _shutdown = shutdown;

        _tunnelBuffer = new byte[transport.HeaderSize + ProbeFrameSize];
        transport.WriteHeader(_tunnelBuffer.AsSpan(0, transport.HeaderSize));
        BuildProbe(_tunnelBuffer.AsSpan(transport.HeaderSize, ProbeFrameSize), BridgePort.Tunnel);

        _tapBuffer = new byte[ProbeFrameSize];
        BuildProbe(_tapBuffer, BridgePort.Tap);
    }

    private void BuildProbe(Span<byte> frame, BridgePort origin)
    {
        frame.Clear();

        // Destination: locally administered multicast so bridges flood it (bit 0 = multicast, bit 1 = local).
        frame[0] = 0x03;
        frame[1] = 0x57;
        frame[2] = 0x47;
        frame[3] = 0x4C;
        frame[4] = 0x32;
        frame[5] = 0x42;

        // Source: locally administered unicast derived from the instance id.
        frame[6] = 0x02;
        _instanceId.AsSpan(0, 5).CopyTo(frame[7..12]);

        frame[12] = (byte)(ProbeEtherType >> 8);
        frame[13] = (byte)(ProbeEtherType & 0xFF);

        Magic.CopyTo(frame[MagicOffset..]);
        _instanceId.CopyTo(frame[InstanceOffset..]);
        frame[PortOffset] = (byte)origin;
    }

    /// <summary>
    /// Consumes loop probes. Returns true when the frame was a probe and must not be forwarded.
    /// </summary>
    public bool TryHandleProbe(ReadOnlySpan<byte> frame, BridgePort ingress)
    {
        if (frame.Length < PortOffset + 1)
        {
            return false;
        }

        ushort etherType = (ushort)((frame[12] << 8) | frame[13]);
        if (etherType != ProbeEtherType || !frame.Slice(MagicOffset, Magic.Length).SequenceEqual(Magic))
        {
            return false;
        }

        // A probe from another bridge instance is simply swallowed; only our own indicates a loop.
        if (frame.Slice(InstanceOffset, _instanceId.Length).SequenceEqual(_instanceId) && !LoopDetected)
        {
            LoopDetected = true;
            var origin = (BridgePort)frame[PortOffset];
            _log.Warning($"LOOP DETECTED: probe sent on {origin} returned on {ingress}. A second Layer 2 path exists between the bridged segments.");

            if (_stopOnDetection)
            {
                _log.Warning("Stopping the bridge to prevent a broadcast storm. Remove the redundant path, then restart.");
                _shutdown.Cancel();
            }
        }

        return true;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (LoopDetected)
                {
                    return;
                }

                await _transport.SendAsync(_tunnelBuffer, cancellationToken).ConfigureAwait(false);
                await _tap.WriteFrameAsync(_tapBuffer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warning($"Loop detector stopped: {ex.Message}");
        }
        finally
        {
            timer.Dispose();
        }
    }
}
