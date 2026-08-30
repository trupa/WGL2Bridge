using System.Buffers.Binary;

namespace WGL2Bridge.Network;

/// <summary>
/// "Broad-Industrial-Pass" policy: allow ARP, industrial L2 EtherTypes and all TCP; drop only the
/// consumer discovery UDP ports. Truncated headers, ICMP and later fragments fail open (forwarded).
/// A 64 KiB bitfield gives O(1), zero-allocation UDP port lookups on the hot path.
/// </summary>
public sealed class IndustrialFilter
{
    private readonly bool[] _dropUdpPorts = new bool[65536];
    private readonly bool[]? _vlanAllowSet;

    /// <summary>Builds the filter from the configured drop and VLAN allow lists.</summary>
    public IndustrialFilter(IReadOnlyCollection<int> dropUdpPorts, IReadOnlyCollection<int>? allowVlans)
    {
        foreach (int port in dropUdpPorts)
        {
            if (port is >= 0 and <= 65535)
            {
                _dropUdpPorts[port] = true;
            }
        }

        if (allowVlans is not null)
        {
            var set = new bool[4096];
            bool any = false;
            foreach (int vlan in allowVlans)
            {
                if (vlan is >= 0 and <= 4094)
                {
                    set[vlan] = true;
                    any = true;
                }
            }

            if (any)
            {
                _vlanAllowSet = set;
            }
        }
    }

    /// <summary>Returns true if the frame should be forwarded across the bridge.</summary>
    public bool Allow(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < Ethernet.HeaderLength)
        {
            return true; // truncated header → fail open
        }

        if (!VlanAllowed(frame))
        {
            return false; // VLAN not in the allow list
        }

        ushort etherType = Ethernet.ReadEtherType(frame, out int l3Offset);

        switch (etherType)
        {
            case Ethernet.Arp:
            case Ethernet.Profinet:
            case Ethernet.EtherCat:
            case Ethernet.Goose:
            case Ethernet.Sv:
            case Ethernet.Lldp:
            case Ethernet.LoopProbe:
                return true;

            case Ethernet.Ipv4:
                return AllowIpv4(frame[l3Offset..]);

            case Ethernet.Ipv6:
                return AllowIpv6(frame[l3Offset..]);

            default:
                return true; // unknown L2 EtherType → pass
        }
    }

    /// <summary>
    /// True when VLAN filtering is disabled, or when the frame carries an 802.1Q tag whose VLAN ID
    /// is in the allow list. Untagged frames are dropped only when a VLAN allow list is configured.
    /// </summary>
    private bool VlanAllowed(ReadOnlySpan<byte> frame)
    {
        if (_vlanAllowSet is null)
        {
            return true;
        }

        int offset = 12;
        while (offset + 4 <= frame.Length)
        {
            ushort tpid = Ethernet.ReadUInt16BigEndian(frame, offset);
            if (tpid != Ethernet.Vlan && tpid != Ethernet.QinQ)
            {
                break; // no more tags
            }

            int vlanId = Ethernet.ReadUInt16BigEndian(frame, offset + 2) & 0x0FFF;
            if (_vlanAllowSet[vlanId])
            {
                return true;
            }

            offset += 4;
        }

        return false; // untagged, or no allowed VLAN tag found
    }

    private bool AllowIpv4(ReadOnlySpan<byte> ip)
    {
        if (ip.Length < 20)
        {
            return true;
        }

        int ihl = (ip[0] & 0x0F) * 4;
        if (ip.Length < ihl)
        {
            return true; // truncated IP header → pass
        }

        int protocol = ip[9];
        if (protocol == 6)
        {
            return true; // all TCP passes
        }

        if (protocol == 17)
        {
            int fragField = BinaryPrimitives.ReadUInt16BigEndian(ip[6..8]);
            bool laterFragment = (fragField & 0x1FFF) != 0;
            if (laterFragment)
            {
                return true; // later fragment carries no UDP header → pass
            }

            if (ip.Length < ihl + 4)
            {
                return true; // truncated UDP header → pass
            }

            int dstPort = BinaryPrimitives.ReadUInt16BigEndian(ip[(ihl + 2)..]);
            return !_dropUdpPorts[dstPort];
        }

        return true; // ICMP, GRE, etc. → pass
    }

    private bool AllowIpv6(ReadOnlySpan<byte> ip)
    {
        if (ip.Length < 40)
        {
            return true;
        }

        int nextHeader = ip[6];
        int offset = 40;

        // Walk past simple extension headers; anything unusual fails open.
        while (nextHeader is 0 or 43 or 60)
        {
            if (offset + 2 > ip.Length)
            {
                return true;
            }

            int headerLength = (ip[offset + 1] + 1) * 8;
            if (offset + headerLength > ip.Length)
            {
                return true;
            }

            nextHeader = ip[offset];
            offset += headerLength;
        }

        if (nextHeader == 44)
        {
            return true; // fragmentation header → pass
        }

        if (nextHeader == 6)
        {
            return true; // all TCP passes
        }

        if (nextHeader == 17)
        {
            if (offset + 4 > ip.Length)
            {
                return true; // truncated UDP header → pass
            }

            int dstPort = BinaryPrimitives.ReadUInt16BigEndian(ip[(offset + 2)..]);
            return !_dropUdpPorts[dstPort];
        }

        return true;
    }
}
