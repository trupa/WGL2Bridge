using System.Net;
using System.Net.Sockets;

namespace WGL2Bridge;

public enum EncapsulationMode
{
    /// <summary>Bare Ethernet frame inside UDP. Only interoperable with another instance of this bridge.</summary>
    Raw,

    /// <summary>RFC 7348 VXLAN: 8-byte header over UDP/4789. Interoperable with Linux `ip link add type vxlan`.</summary>
    Vxlan,

    /// <summary>RFC 2784/2890 GRE with protocol type 0x6558 (Transparent Ethernet Bridging), IP protocol 47.</summary>
    Gretap
}

/// <summary>
/// Encapsulation-agnostic tunnel transport. The header is constant for the lifetime of the bridge,
/// so it is written once into the send buffer's headroom and never rebuilt per packet.
/// </summary>
public interface IBridgeTransport : IDisposable
{
    /// <summary>Bytes of headroom reserved in front of the inner Ethernet frame when sending.</summary>
    int HeaderSize { get; }

    /// <summary>Fills <paramref name="header"/> (exactly <see cref="HeaderSize"/> bytes) with the constant tunnel header.</summary>
    void WriteHeader(Span<byte> header);

    ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken);

    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Returns the offset of the inner Ethernet frame in a received datagram, or -1 to drop it.</summary>
    int GetInnerFrameOffset(ReadOnlySpan<byte> datagram);

    string Describe();
}

public static class BridgeTransportFactory
{
    public static IBridgeTransport Create(BridgeConfig config, IPAddress localTunnelIp, IPAddress remote)
    {
        return config.EncapsulationMode switch
        {
            EncapsulationMode.Raw => new UdpTransport(
                new IPEndPoint(localTunnelIp, config.EffectivePort),
                new IPEndPoint(remote, config.EffectivePort),
                new RawEncapsulation()),

            EncapsulationMode.Vxlan => new UdpTransport(
                new IPEndPoint(localTunnelIp, config.EffectivePort),
                new IPEndPoint(remote, config.EffectivePort),
                new VxlanEncapsulation(config.VxlanVni)),

            EncapsulationMode.Gretap => new GreTransport(localTunnelIp, remote, config.GreKey),

            _ => throw new InvalidDataException($"Unsupported encapsulation mode '{config.EncapsulationMode}'.")
        };
    }
}

/// <summary>Header shaping for the UDP-based encapsulations.</summary>
public interface IUdpEncapsulation
{
    int HeaderSize { get; }

    void WriteHeader(Span<byte> header);

    /// <summary>Validates the received header; returns the inner frame offset or -1 to drop.</summary>
    int GetInnerFrameOffset(ReadOnlySpan<byte> datagram);

    string Describe();
}

/// <summary>No header at all: the UDP payload is the Ethernet frame.</summary>
public sealed class RawEncapsulation : IUdpEncapsulation
{
    public int HeaderSize => 0;

    public void WriteHeader(Span<byte> header)
    {
    }

    public int GetInnerFrameOffset(ReadOnlySpan<byte> datagram) => datagram.Length >= 14 ? 0 : -1;

    public string Describe() => "RAW (bare Ethernet frame in UDP)";
}

/// <summary>
/// RFC 7348 VXLAN header (8 bytes):
///   byte 0     : flags, bit 3 (0x08) = valid VNI
///   bytes 1..3 : reserved
///   bytes 4..6 : 24-bit VNI
///   byte 7     : reserved
/// </summary>
public sealed class VxlanEncapsulation(int vni) : IUdpEncapsulation
{
    private const byte ValidVniFlag = 0x08;
    private readonly int _vni = vni;

    public int HeaderSize => 8;

    public void WriteHeader(Span<byte> header)
    {
        header[0] = ValidVniFlag;
        header[1] = 0;
        header[2] = 0;
        header[3] = 0;
        header[4] = (byte)((_vni >> 16) & 0xFF);
        header[5] = (byte)((_vni >> 8) & 0xFF);
        header[6] = (byte)(_vni & 0xFF);
        header[7] = 0;
    }

    public int GetInnerFrameOffset(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < HeaderSize + 14 || (datagram[0] & ValidVniFlag) == 0)
        {
            return -1;
        }

        int vni = (datagram[4] << 16) | (datagram[5] << 8) | datagram[6];
        return vni == _vni ? HeaderSize : -1; // Reject frames from other VXLAN segments.
    }

    public string Describe() => $"VXLAN (RFC 7348), VNI {_vni}";
}

/// <summary>UDP tunnel transport used by both the raw and VXLAN encapsulations.</summary>
public sealed class UdpTransport : IBridgeTransport
{
    private readonly Socket _socket;
    private readonly IPEndPoint _remote;
    private readonly IUdpEncapsulation _encapsulation;
    private readonly SocketAddress _receivedFrom;

    public UdpTransport(IPEndPoint local, IPEndPoint remote, IUdpEncapsulation encapsulation)
    {
        _remote = remote;
        _encapsulation = encapsulation;
        _receivedFrom = new SocketAddress(local.AddressFamily);

        _socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(local); // Binding to the WireGuard IP forces egress through the tunnel.
        _socket.DontFragment = true;
        _socket.SendBufferSize = 1 << 20;
        _socket.ReceiveBufferSize = 1 << 20;
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
    }

    public IPEndPoint LocalEndPoint { get; }

    public int HeaderSize => _encapsulation.HeaderSize;

    public void WriteHeader(Span<byte> header) => _encapsulation.WriteHeader(header);

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        => _socket.SendToAsync(datagram, SocketFlags.None, _remote, cancellationToken);

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _socket.ReceiveFromAsync(buffer, SocketFlags.None, _receivedFrom, cancellationToken);

    public int GetInnerFrameOffset(ReadOnlySpan<byte> datagram) => _encapsulation.GetInnerFrameOffset(datagram);

    public string Describe() => $"{_encapsulation.Describe()} :: {LocalEndPoint} -> {_remote}";

    public void Dispose() => _socket.Dispose();
}

/// <summary>
/// GRETAP transport over a raw IPv4 socket (IP protocol 47). Requires an elevated process.
/// GRE header (RFC 2784/2890):
///   bytes 0..1 : flags + version (0x0000, or 0x2000 when a key is present)
///   bytes 2..3 : protocol type, 0x6558 = Transparent Ethernet Bridging
///   bytes 4..7 : optional 32-bit key
/// On receive, Windows raw sockets deliver the full IPv4 header, so the inner frame offset is IHL + GRE.
/// </summary>
public sealed class GreTransport : IBridgeTransport
{
    private const ushort ProtocolTransparentEthernetBridging = 0x6558;
    private const ushort KeyPresentFlag = 0x2000;
    private const int GreProtocolNumber = 47;

    private readonly Socket _socket;
    private readonly IPEndPoint _remote;
    private readonly SocketAddress _receivedFrom;
    private readonly uint _key;
    private readonly bool _hasKey;

    public GreTransport(IPAddress localTunnelIp, IPAddress remote, uint key)
    {
        _key = key;
        _hasKey = key != 0;
        _remote = new IPEndPoint(remote, 0); // Port is meaningless for a raw IP protocol.
        _receivedFrom = new SocketAddress(AddressFamily.InterNetwork);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, (ProtocolType)GreProtocolNumber);
        _socket.Bind(new IPEndPoint(localTunnelIp, 0));
        _socket.SendBufferSize = 1 << 20;
        _socket.ReceiveBufferSize = 1 << 20;
        LocalAddress = localTunnelIp;
    }

    public IPAddress LocalAddress { get; }

    public int HeaderSize => _hasKey ? 8 : 4;

    public void WriteHeader(Span<byte> header)
    {
        ushort flags = _hasKey ? KeyPresentFlag : (ushort)0;
        header[0] = (byte)(flags >> 8);
        header[1] = (byte)(flags & 0xFF);
        header[2] = (byte)(ProtocolTransparentEthernetBridging >> 8);
        header[3] = (byte)(ProtocolTransparentEthernetBridging & 0xFF);

        if (_hasKey)
        {
            header[4] = (byte)(_key >> 24);
            header[5] = (byte)(_key >> 16);
            header[6] = (byte)(_key >> 8);
            header[7] = (byte)_key;
        }
    }

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        => _socket.SendToAsync(datagram, SocketFlags.None, _remote, cancellationToken);

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _socket.ReceiveFromAsync(buffer, SocketFlags.None, _receivedFrom, cancellationToken);

    public int GetInnerFrameOffset(ReadOnlySpan<byte> datagram)
    {
        // IPv4 byte 0 low nibble = IHL in 32-bit words.
        if (datagram.Length < 20)
        {
            return -1;
        }

        int ipHeaderLength = (datagram[0] & 0x0F) * 4;
        if (ipHeaderLength < 20 || datagram.Length < ipHeaderLength + 4)
        {
            return -1;
        }

        ReadOnlySpan<byte> gre = datagram[ipHeaderLength..];
        ushort flags = (ushort)((gre[0] << 8) | gre[1]);
        ushort protocol = (ushort)((gre[2] << 8) | gre[3]);

        if (protocol != ProtocolTransparentEthernetBridging)
        {
            return -1; // Not GRETAP (e.g. plain GRE carrying IP).
        }

        int greLength = 4;
        if ((flags & 0x8000) != 0) greLength += 4; // Checksum present (checksum + reserved1).
        if ((flags & KeyPresentFlag) != 0)
        {
            if (gre.Length < greLength + 4)
            {
                return -1;
            }

            uint receivedKey = (uint)((gre[greLength] << 24) | (gre[greLength + 1] << 16) |
                                      (gre[greLength + 2] << 8) | gre[greLength + 3]);
            if (_hasKey && receivedKey != _key)
            {
                return -1;
            }

            greLength += 4;
        }
        else if (_hasKey)
        {
            return -1; // A key is configured but the peer sent none.
        }

        if ((flags & 0x1000) != 0) greLength += 4; // Sequence number present.

        int offset = ipHeaderLength + greLength;
        return datagram.Length >= offset + 14 ? offset : -1;
    }

    public string Describe() =>
        $"GRETAP (IP protocol 47, 0x6558){(_hasKey ? $", key {_key}" : string.Empty)} :: {LocalAddress} -> {_remote.Address}";

    public void Dispose() => _socket.Dispose();
}
