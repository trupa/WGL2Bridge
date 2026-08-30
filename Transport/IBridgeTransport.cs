using System.Net;
using System.Runtime.Versioning;
using WGL2Bridge.Config;

namespace WGL2Bridge.Transport;

/// <summary>
/// Encapsulation strategy for frames crossing the tunnel. Implementations own their socket, precompute
/// a constant tunnel header into buffer headroom, and validate inbound packets before the inner frame
/// is handed to the bridge. Egress is pinned to the tunnel IP by binding the socket to it.
/// </summary>
[SupportedOSPlatform("windows")]
public interface IBridgeTransport : IDisposable
{
    /// <summary>The encapsulation mode this transport implements.</summary>
    TransportMode Mode { get; }

    /// <summary>Bytes of constant headroom reserved before the inner frame (0 for Raw).</summary>
    int HeaderLength { get; }

    /// <summary>Total outer encapsulation size (IP + tunnel headers) used for MTU derivation.</summary>
    int EncapsulationOverhead { get; }

    /// <summary>Human-readable summary used in lifecycle logs.</summary>
    string Description { get; }

    /// <summary>True while the underlying socket is open.</summary>
    bool IsOpen { get; }

    /// <summary>Number of inbound tunnel packets dropped because they came from a non-peer source.</summary>
    long DroppedNonPeer { get; }

    /// <summary>Opens and binds the socket to the tunnel IP, targeting the peer.</summary>
    void Open(IPAddress localTunnelIp, IPAddress peerIp);

    /// <summary>Writes the constant tunnel header into the reserved headroom (called once per buffer).</summary>
    void WriteHeader(Span<byte> headroom);

    /// <summary>
    /// Validates an inbound packet and returns the offset of the inner Ethernet frame, or -1 if the
    /// packet is not ours (wrong VNI / GRE key / protocol).
    /// </summary>
    int ValidateIncoming(ReadOnlySpan<byte> packet);

    /// <summary>Sends the header + frame slice of the pooled buffer across the tunnel.</summary>
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, int frameOffset, int frameLength, CancellationToken ct);

    /// <summary>Receives one packet into the pooled buffer and returns its total length.</summary>
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>Closes the socket.</summary>
    void Close();
}
