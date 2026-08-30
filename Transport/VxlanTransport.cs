using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading;
using WGL2Bridge.Config;
using WGL2Bridge.Logging;

namespace WGL2Bridge.Transport;

/// <summary>
/// VXLAN encapsulation over a UDP socket bound to the VXLAN port (4789). The kernel builds the outer
/// IP/UDP header and computes the UDP checksum; we prepend only the constant 8-byte VXLAN header.
/// Compatible with a Linux kernel <c>vxlan</c> interface (dstport 4789) on the peer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VxlanTransport : IBridgeTransport
{
    private readonly int _vni;
    private readonly int _destinationPort;
    private readonly bool _validatePeerSource;
    private readonly byte[] _header = new byte[8];
    private readonly IPEndPoint _remote = new(IPAddress.Any, 0);
    private Socket? _socket;
    private long _droppedNonPeer;
    private int _nonPeerLogged;
    private IPEndPoint? _peer;

    public VxlanTransport(BridgeConfig config)
    {
        _vni = config.VxlanVni;
        _destinationPort = config.VxlanDestinationPort;
        _validatePeerSource = config.PeerSourceValidation;
    }

    public TransportMode Mode => TransportMode.Vxlan;

    public int HeaderLength => 8;

    public int EncapsulationOverhead => 36; // IP(20) + UDP(8) + VXLAN(8)

    public string Description => $"VXLAN (VNI {_vni}, UDP/{_destinationPort})";


    public long DroppedNonPeer => Interlocked.Read(ref _droppedNonPeer);
    public bool IsOpen => _socket is not null;

    /// <summary>
    /// Binds a UDP socket to the tunnel IP on the VXLAN port. The socket is intentionally NOT
    /// connected: Linux vxlan transmits from a hashed source port (not 4789), which a connected
    /// socket would filter out. Binding pins egress to the tunnel IP and demuxes inbound VXLAN.
    /// </summary>
    public void Open(IPAddress localTunnelIp, IPAddress peerIp)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(localTunnelIp, _destinationPort));
        _peer = new IPEndPoint(peerIp, _destinationPort);
        Interlocked.Exchange(ref _nonPeerLogged, 0);
    }

    public void WriteHeader(Span<byte> headroom)
    {
        _header[0] = 0x08; // VXLAN "I" flag
        _header[4] = (byte)(_vni >> 16);
        _header[5] = (byte)(_vni >> 8);
        _header[6] = (byte)_vni;
        _header.CopyTo(headroom);
    }

    public int ValidateIncoming(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8)
        {
            return -1;
        }

        if ((packet[0] & 0x08) == 0)
        {
            return -1;
        }

        int vni = (packet[4] << 16) | (packet[5] << 8) | packet[6];
        return vni == _vni ? 8 : -1;
    }

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, int frameOffset, int frameLength, CancellationToken ct) =>
        _socket!.SendToAsync(buffer[..(frameOffset + frameLength)], SocketFlags.None, _peer!, ct);

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        SocketReceiveFromResult result = await _socket!
            .ReceiveFromAsync(buffer, SocketFlags.None, _remote, ct)
            .ConfigureAwait(false);

        IPAddress source = (result.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.Any;
        if (_validatePeerSource && !source.Equals(_peer!.Address))
        {
            Interlocked.Increment(ref _droppedNonPeer);
            if (Interlocked.Exchange(ref _nonPeerLogged, 1) == 0)
            {
                BridgeLog.Warning($"Dropped tunnel packet from non-peer source {source} (expected peer {_peer.Address}).");
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
