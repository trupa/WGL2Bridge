using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading;
using WGL2Bridge.Config;
using WGL2Bridge.Logging;

namespace WGL2Bridge.Transport;

/// <summary>
/// Raw encapsulation: the bare Ethernet frame is the payload of a raw IP packet with a configurable
/// protocol number. The kernel adds the outer IP header, so there is no constant headroom. This mode
/// has no kernel-native Linux peer equivalent; the peer must run matching software. Prefer VXLAN for
/// production.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RawTransport : IBridgeTransport
{
    private readonly int _protocol;
    private readonly bool _validatePeerSource;
    private readonly IPEndPoint _remote = new(IPAddress.Any, 0);
    private Socket? _socket;
    private IPEndPoint? _peer;
    private long _droppedNonPeer;
    private int _nonPeerLogged;

    public RawTransport(BridgeConfig config)
    {
        _protocol = config.RawIpProtocol;
        _validatePeerSource = config.PeerSourceValidation;
    }

    public TransportMode Mode => TransportMode.Raw;

    public int HeaderLength => 0;

    public int EncapsulationOverhead => 20; // IP header only

    public string Description => $"Raw IP (protocol {_protocol})";

    public bool IsOpen => _socket is not null;
    public IPEndPoint? LastReceiveSource { get; private set; }

    public long DroppedNonPeer => Interlocked.Read(ref _droppedNonPeer);

    /// <summary>Opens a raw socket for the configured IP protocol and binds it to the tunnel IP.</summary>
    public void Open(IPAddress localTunnelIp, IPAddress? peerIp)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, (ProtocolType)_protocol);
        _socket.Bind(new IPEndPoint(localTunnelIp, 0));
        _peer = peerIp is null ? null : new IPEndPoint(peerIp, 0);
        LastReceiveSource = null;
        Interlocked.Exchange(ref _nonPeerLogged, 0);
    }

    public void WriteHeader(Span<byte> headroom)
    {
        // No headroom in Raw mode.
    }

    public int ValidateIncoming(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 20)
        {
            return -1;
        }

        if ((packet[0] >> 4) != 4)
        {
            return -1;
        }

        int ihl = (packet[0] & 0x0F) * 4;
        if (ihl < 20 || packet.Length < ihl)
        {
            return -1;
        }

        return packet[9] == _protocol ? ihl : -1;
    }

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, int frameOffset, int frameLength, CancellationToken ct) =>
        _socket!.SendToAsync(
            buffer[frameOffset..(frameOffset + frameLength)],
            SocketFlags.None,
            _peer ?? throw new InvalidOperationException("No peer configured (server mode)."),
            ct);

    public ValueTask<int> SendToAsync(
        ReadOnlyMemory<byte> buffer, int frameOffset, int frameLength, IPEndPoint destination, CancellationToken ct) =>
        _socket!.SendToAsync(buffer[frameOffset..(frameOffset + frameLength)], SocketFlags.None, destination, ct);

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        SocketReceiveFromResult result = await _socket!
            .ReceiveFromAsync(buffer, SocketFlags.None, _remote, ct)
            .ConfigureAwait(false);

        IPEndPoint? source = result.RemoteEndPoint as IPEndPoint;
        LastReceiveSource = source;

        // In server mode (_peer is null) the bridge accepts multiple clients and applies the
        // configured allow-list itself.
        if (_peer is not null && _validatePeerSource && source is not null && !source.Address.Equals(_peer.Address))
        {
            Interlocked.Increment(ref _droppedNonPeer);
            if (Interlocked.Exchange(ref _nonPeerLogged, 1) == 0)
            {
                BridgeLog.Warning($"Dropped tunnel packet from non-peer source {source.Address} (expected peer {_peer.Address}).");
            }
            return 0;
        }

        return result.ReceivedBytes;
    }

    public void Close()
    {
        _socket?.Dispose();
        _socket = null;
    }

    public void Dispose() => Close();
}
