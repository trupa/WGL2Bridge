using System.Buffers.Binary;

namespace WGL2Bridge.Network;

/// <summary>
/// Span-based Ethernet frame helpers. Everything here operates on borrowed buffers; nothing allocates.
/// </summary>
public static class Ethernet
{
    /// <summary>Length of an untagged Ethernet header in bytes.</summary>
    public const int HeaderLength = 14;

    /// <summary>Length of a MAC address in bytes.</summary>
    public const int MacLength = 6;

    /// <summary>Broadcast MAC address as a 48-bit <see cref="ulong"/>.</summary>
    public const ulong BroadcastMac = 0xFFFF_FFFF_FFFF;

    public const ushort Vlan = 0x8100;
    public const ushort QinQ = 0x88A8;
    public const ushort Ipv4 = 0x0800;
    public const ushort Ipv6 = 0x86DD;
    public const ushort Arp = 0x0806;
    public const ushort Profinet = 0x8892;
    public const ushort EtherCat = 0x88A4;
    public const ushort Goose = 0x88B8;
    public const ushort Sv = 0x88BA;
    public const ushort Lldp = 0x88CC;

    /// <summary>EtherType used by our loop-detection probes.</summary>
    public const ushort LoopProbe = 0x88B5;

    /// <summary>Reads the 6-byte destination MAC at the start of a frame as a 48-bit value.</summary>
    public static ulong ReadMac(ReadOnlySpan<byte> frame) =>
        ((ulong)frame[0] << 40) | ((ulong)frame[1] << 32) | ((ulong)frame[2] << 24) |
        ((ulong)frame[3] << 16) | ((ulong)frame[4] << 8) | (ulong)frame[5];

    /// <summary>Reads a big-endian 16-bit value at the given byte offset.</summary>
    public static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> frame, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset, 2));

    /// <summary>True if the MAC has its individual/group (I/G) bit set.</summary>
    public static bool IsMulticast(ulong mac) => (mac & 0x0100_0000_0000UL) != 0;

    /// <summary>True if the first byte's I/G bit is set (multicast or broadcast).</summary>
    public static bool IsMulticast(ReadOnlySpan<byte> frame) => (frame[0] & 0x01) != 0;

    /// <summary>
    /// Walks VLAN/QinQ tags and returns the inner EtherType plus the offset of the L3 header.
    /// Callers must have verified at least a 14-byte frame beforehand.
    /// </summary>
    public static ushort ReadEtherType(ReadOnlySpan<byte> frame, out int l3Offset)
    {
        int offset = 12;
        ushort type = ReadU16(frame, offset);

        while ((type == Vlan || type == QinQ) && offset + 4 <= frame.Length)
        {
            offset += 4;
            type = ReadU16(frame, offset);
        }

        l3Offset = offset + 2;
        return type;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> frame, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset, 2));
}
