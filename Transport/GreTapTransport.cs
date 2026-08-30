using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading;
using WGL2Bridge.Config;
using WGL2Bridge.Logging;

namespace WGL2Bridge.Transport;

/// <summary>
/// GRETAP encapsulation over a raw IP socket (protocol 47). The kernel adds the outer IP header;
/// we prepend a constant GRE header carrying the transparent Ethernet bridging (TEB) protocol
/// 0x6558, with an optional GRE key. Compatible with a Linux kernel <c>gretap</c> interface.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GreTapTransport : IBridgeTransport
{
    private const ushort GreProtocolTeb = 0x6558;
    private const int IpHeaderLength = 20;

    private readonly uint? _key;
    private readonly bool _validatePeerSource;
    private readonly byte[] _header;
    private readonly IPEndPoint _remote = new(IPAddress.Any, 0);
    private Socket? _socket;
    private IPEndPoint? _peer;
    private long _droppedNonPeer;
    private int _nonPeerLogged;

    public GreTapTransport(BridgeConfig config)
    {
        _key = config.GreTapKey;
        _validatePeerSource = config.PeerSourceValidation;

        if (_key is null)
        {
            _header = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(_header.AsSpan(2, 2), GreProtocolTeb);
        }
        else
        {
            _header = new byte[8];
            _header[0] = 0x20; // K flag (key present)
            BinaryPrimitives.WriteUInt16BigEndian(_header.AsSpan(2, 2), GreProtocolTeb);
            BinaryPrimitives.WriteUInt32BigEndian(_header.AsSpan(4, 4), _key.Value);
        }
    }

    public TransportMode Mode => TransportMode.GreTap;

    public int HeaderLength => _header.Length;

    public int EncapsulationOverhead => IpHeaderLength + _header.Length;

    public string Description =>
        _key is null ? "GRETAP (GRE/TEB)" : $"GRETAP (GRE/TEB, key 0x{_key:X8})";

    public bool IsOpen => _socket is not null;

    public long DroppedNonPeer => Interlocked.Read(ref _droppedNonPeer);

    /// <summary>Opens a raw socket for GRE (protocol 47) and binds it to the tunnel IP.</summary>
    public void Open(IPAddress localTunnelIp, IPAddress peerIp)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, (ProtocolType)47);
        _socket.Bind(new IPEndPoint(localTunnelIp, 0));
        _peer = new IPEndPoint(peerIp, 0);
        Interlocked.Exchange(ref _nonPeerLogged, 0);
    }

    public void WriteHeader(Span<byte> headroom) => _header.CopyTo(headroom);

    public int ValidateIncoming(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < IpHeaderLength + 4)
        {
            return -1;
        }

        if ((packet[0] >> 4) != 4)
        {
            return -1; // not IPv4
        }

        int ihl = (packet[0] & 0x0F) * 4;
        if (ihl < IpHeaderLength || packet.Length < ihl + 4)
        {
            return -1;
        }

        if (packet[9] != 47)
        {
            return -1; // not GRE
        }

        int greOffset = ihl;
        int flags = packet[greOffset];
        int protocol = BinaryPrimitives.ReadUInt16BigEndian(packet[(greOffset + 2)..(greOffset + 4)]);
        if (protocol != GreProtocolTeb)
        {
            return -1; // not transparent Ethernet bridging
        }

        int frameOffset = greOffset + 4;
        if ((flags & 0x20) != 0)
        {
            if (packet.Length < frameOffset + 4)
            {
                return -1;
            }

            if (_key is not null)
            {
                uint rxKey = BinaryPrimitives.ReadUInt32BigEndian(packet[frameOffset..(frameOffset + 4)]);
                if (rxKey != _key.Value)
                {
                    return -1; // key mismatch
                }
            }

            frameOffset += 4;
        }

        return frameOffset;
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
