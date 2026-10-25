using System.Buffers.Binary;

namespace WGL2Bridge.Network;

/// <summary>
/// Frame policy engine: evaluates a pre-resolved <see cref="FilterPolicy"/> against a frame and
/// reports which field rejected it, together with the exact value that matched. Truncated headers,
/// unlisted ICMP and later fragments are forwarded. Every lookup is a pre-built table or set — O(1)
/// and allocation free on the hot path.
/// </summary>
public sealed class FrameFilter
{
    private readonly FilterPolicy _policy;

    /// <summary>Builds the filter from an effective policy (see <see cref="FilterPolicy.Resolve"/>).</summary>
    public FrameFilter(FilterPolicy policy) => _policy = policy;

    /// <summary>Returns true if the frame should be forwarded across the bridge.</summary>
    public bool Allow(ReadOnlySpan<byte> frame) => Allow(frame, out _);

    /// <summary>
    /// Returns true if the frame should be forwarded across the bridge; otherwise
    /// <paramref name="drop"/> names the field that rejected it and the value that matched.
    /// </summary>
    public bool Allow(ReadOnlySpan<byte> frame, out FilterDrop drop)
    {
        drop = FilterDrop.None;

        if (frame.Length < Ethernet.HeaderLength)
        {
            return true; // truncated header → fail open
        }

        if (IsLinkLocal(frame, _policy.LinkLocalMacs, out ulong destinationMac))
        {
            drop = new FilterDrop(FilterDropReason.LinkLocal, destinationMac);
            return false; // STP/LACP/EAPOL/PAUSE and vendor discovery never cross a WAN link
        }

        if (!VlanAllowed(frame))
        {
            drop = new FilterDrop(FilterDropReason.Vlan, 0);
            return false; // VLAN not in the allow list
        }

        ushort etherType = Ethernet.ReadEtherType(frame, out int l3Offset);
        if (_policy.EtherTypes[etherType])
        {
            drop = new FilterDrop(FilterDropReason.EtherType, etherType);
            return false; // EtherType in the configured block list
        }

        switch (etherType)
        {
            case Ethernet.Ipv4:
                return AllowIpv4(frame[l3Offset..], out drop);

            case Ethernet.Ipv6:
                return AllowIpv6(frame[l3Offset..], out drop);

            default:
                return true; // forward everything else by default
        }
    }

    /// <summary>
    /// True for destination MACs that only ever carry link-local control traffic: the IEEE 802.1
    /// reserved group 01:80:C2:00:00:00-0F (STP/RSTP/MSTP, LACP, 802.1X/EAPOL, 802.3x PAUSE, slow
    /// protocols) and Cisco's 01:00:0C:CC:CC:CC / 01:00:0C:CC:CC:CD (CDP, VTP, DTP, UDLD, PVST+,
    /// PAgP). These are per-link protocols; bridging them merges unrelated control domains.
    /// </summary>
    private static bool IsLinkLocal(ReadOnlySpan<byte> frame, HashSet<ulong> macs, out ulong mac)
    {
        mac = 0;

        // Every frame passes through here, so test the multicast bit first: only group destinations
        // can be one of these protocols.
        if ((frame[0] & 0x01) == 0)
        {
            return false;
        }

        mac = ((ulong)frame[0] << 40) | ((ulong)frame[1] << 32) | ((ulong)frame[2] << 24) |
              ((ulong)frame[3] << 16) | ((ulong)frame[4] << 8) | frame[5];
        return macs.Contains(mac);
    }

    /// <summary>
    /// True when VLAN filtering is disabled, or when the frame carries an 802.1Q tag whose VLAN ID
    /// is in the allow list. Untagged frames are dropped only when a VLAN allow list is configured.
    /// </summary>
    private bool VlanAllowed(ReadOnlySpan<byte> frame)
    {
        bool[]? allowSet = _policy.VlanAllowSet;
        if (allowSet is null)
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
            if (allowSet[vlanId])
            {
                return true;
            }

            offset += 4;
        }

        return false; // untagged, or no allowed VLAN tag found
    }

    private bool AllowIpv4(ReadOnlySpan<byte> ip, out FilterDrop drop)
    {
        drop = FilterDrop.None;

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
        if (_policy.IpProtocols[protocol])
        {
            drop = new FilterDrop(FilterDropReason.IpProtocol, (ulong)protocol);
            return false; // IGMP/EIGRP/OSPF/PIM/VRRP must not span the tunnel
        }

        if (protocol == 6)
        {
            if (!_policy.HasTcpPorts)
            {
                return true; // common case: no TCP port policy → skip the lookup
            }

            return AllowTransport(ip, ihl, isUdp: false, out drop);
        }

        if (protocol == 17)
        {
            return AllowTransport(ip, ihl, isUdp: true, out drop);
        }

        if (protocol == 1 && _policy.HasIcmpv4Types)
        {
            int fragField = BinaryPrimitives.ReadUInt16BigEndian(ip[6..8]);
            if ((fragField & 0x1FFF) != 0)
            {
                return true; // later fragment carries no ICMP header → pass
            }

            if (ip.Length < ihl + 1)
            {
                return true; // truncated ICMP header → pass
            }

            int icmpType = ip[ihl];
            if (_policy.Icmpv4Types[icmpType])
            {
                drop = new FilterDrop(FilterDropReason.Icmpv4Type, (ulong)icmpType);
                return false;
            }
        }

        return true; // ICMP, GRE, etc. → pass
    }

    /// <summary>
    /// Applies the UDP/TCP destination-port table. Later fragments and truncated transport headers
    /// pass, since the port is not present in them.
    /// </summary>
    private bool AllowTransport(ReadOnlySpan<byte> ip, int ihl, bool isUdp, out FilterDrop drop)
    {
        drop = FilterDrop.None;

        int fragField = BinaryPrimitives.ReadUInt16BigEndian(ip[6..8]);
        if ((fragField & 0x1FFF) != 0)
        {
            return true; // later fragment carries no transport header → pass
        }

        if (ip.Length < ihl + 4)
        {
            return true; // truncated transport header → pass
        }

        int dstPort = BinaryPrimitives.ReadUInt16BigEndian(ip[(ihl + 2)..]);
        if (isUdp)
        {
            if (_policy.UdpPorts[dstPort])
            {
                drop = new FilterDrop(FilterDropReason.UdpPort, (ulong)dstPort);
                return false;
            }
        }
        else if (_policy.TcpPorts[dstPort])
        {
            drop = new FilterDrop(FilterDropReason.TcpPort, (ulong)dstPort);
            return false;
        }

        return true;
    }

    private bool AllowIpv6(ReadOnlySpan<byte> ip, out FilterDrop drop)
    {
        drop = FilterDrop.None;

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
            // Only later fragments lack the transport header; the first fragment (offset 0) carries
            // it, so inspect it — otherwise an IPv6 port/type policy could be bypassed by fragmenting.
            if (offset + 8 > ip.Length)
            {
                return true;
            }

            int fragmentOffset = (BinaryPrimitives.ReadUInt16BigEndian(ip[(offset + 2)..]) >> 3) & 0x1FFF;
            if (fragmentOffset != 0)
            {
                return true; // later fragment carries no transport header → pass
            }

            nextHeader = ip[offset];
            offset += 8;
        }

        if (_policy.IpProtocols[nextHeader])
        {
            drop = new FilterDrop(FilterDropReason.IpProtocol, (ulong)nextHeader);
            return false; // IGMP/EIGRP/OSPF/PIM/VRRP must not span the tunnel
        }

        if (nextHeader == 58)
        {
            if (!_policy.HasIcmpv6Types)
            {
                return true; // common case: no ICMPv6 type policy → skip the lookup
            }

            if (offset + 1 > ip.Length)
            {
                return true; // truncated ICMPv6 header → pass
            }

            int icmpType = ip[offset];
            if (_policy.Icmpv6Types[icmpType])
            {
                drop = new FilterDrop(FilterDropReason.Icmpv6Type, (ulong)icmpType);
                return false;
            }

            return true;
        }

        if (nextHeader == 6)
        {
            if (!_policy.HasTcpPorts)
            {
                return true; // common case: no TCP port policy → skip the lookup
            }

            return AllowTransport6(ip, offset, isUdp: false, out drop);
        }

        if (nextHeader == 17)
        {
            return AllowTransport6(ip, offset, isUdp: true, out drop);
        }

        return true;
    }

    /// <summary>Applies the UDP/TCP port table to an IPv6 transport header at <paramref name="offset"/>.</summary>
    private bool AllowTransport6(ReadOnlySpan<byte> ip, int offset, bool isUdp, out FilterDrop drop)
    {
        drop = FilterDrop.None;

        if (offset + 4 > ip.Length)
        {
            return true; // truncated transport header → pass
        }

        int dstPort = BinaryPrimitives.ReadUInt16BigEndian(ip[(offset + 2)..]);
        if (isUdp)
        {
            if (_policy.UdpPorts[dstPort])
            {
                drop = new FilterDrop(FilterDropReason.UdpPort, (ulong)dstPort);
                return false;
            }
        }
        else if (_policy.TcpPorts[dstPort])
        {
            drop = new FilterDrop(FilterDropReason.TcpPort, (ulong)dstPort);
            return false;
        }

        return true;
    }
}
