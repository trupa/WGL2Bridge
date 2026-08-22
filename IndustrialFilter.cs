namespace WGL2Bridge;

/// <summary>
/// Zero-allocation Layer 2 / Layer 4 frame classifier implementing the "Broad-Industrial-Pass" policy.
/// All inspection is performed over <see cref="ReadOnlySpan{T}"/> slices; nothing is copied or allocated.
/// </summary>
public static class IndustrialFilter
{
    // ---- EtherType constants (frame bytes 12..13, big endian) ----
    private const ushort EtherTypeIpv4 = 0x0800;
    private const ushort EtherTypeArp = 0x0806;
    private const ushort EtherTypeVlan = 0x8100; // 802.1Q tag
    private const ushort EtherTypeQinQ = 0x88A8; // 802.1ad provider tag
    private const ushort EtherTypeIpv6 = 0x86DD;

    // ---- Non-IP industrial Layer 2 protocols (always allowed) ----
    private const ushort EtherTypeProfinetRt = 0x8892;
    private const ushort EtherTypePowerlink = 0x88AB;
    private const ushort EtherTypeEtherCat = 0x88E1;
    private const ushort EtherTypeGoose = 0x891D;

    private const byte IpProtoUdp = 17;
    private const byte IpProtoTcp = 6;

    private const int EthHeaderLength = 14;   // 6 dst + 6 src + 2 ethertype
    private const int VlanTagLength = 4;      // TPID already counted in ethertype field + 2 TCI, shifts payload by 4

    /// <summary>
    /// Consumer/Windows link-local discovery UDP ports that are aggressively dropped
    /// even when carried over plain IPv4/IPv6, to keep the tunnel free of broadcast noise.
    /// </summary>
    private static ReadOnlySpan<ushort> BlockedUdpPorts =>
    [
        137,   // NetBIOS Name Service
        138,   // NetBIOS Datagram Service
        1900,  // SSDP / UPnP
        3702,  // WS-Discovery
        5353,  // mDNS / Bonjour
        5355,  // LLMNR
        17500, // Dropbox LAN sync discovery
        27036  // Steam in-home streaming discovery
    ];

    /// <summary>
    /// Industrial ports that are explicitly prioritised (documented fast-path, always forwarded).
    /// </summary>
    private static ReadOnlySpan<ushort> IndustrialTcpPorts =>
    [
        502,   // Modbus TCP
        44818  // EtherNet/IP explicit messaging (CIP)
    ];

    private static ReadOnlySpan<ushort> IndustrialUdpPorts =>
    [
        2222,  // EtherNet/IP implicit (I/O) messaging
        44818  // EtherNet/IP UDP explicit messaging
    ];

    /// <summary>
    /// Returns <c>true</c> when the frame may cross the bridge.
    /// </summary>
    /// <param name="frame">A complete Ethernet II frame starting at the destination MAC.</param>
    public static bool ShouldForward(ReadOnlySpan<byte> frame)
    {
        // Runt frame: not even a full Ethernet header.
        if (frame.Length < EthHeaderLength)
        {
            return false;
        }

        // Bytes 12..13 = EtherType (big endian).
        ushort etherType = (ushort)((frame[12] << 8) | frame[13]);
        int payloadOffset = EthHeaderLength;

        // 802.1Q / 802.1ad: the real EtherType sits 4 bytes further in (bytes 16..17).
        // Industrial networks routinely use VLAN priority tagging (PROFINET, GOOSE), so unwrap up to two tags.
        for (int tag = 0; tag < 2 && (etherType is EtherTypeVlan or EtherTypeQinQ); tag++)
        {
            if (frame.Length < payloadOffset + VlanTagLength + 2)
            {
                return false;
            }

            // +2 TCI bytes, then the next EtherType field.
            etherType = (ushort)((frame[payloadOffset + 2] << 8) | frame[payloadOffset + 3]);
            payloadOffset += VlanTagLength;
        }

        switch (etherType)
        {
            // ---- (A) Infrastructure baseline & non-IP industrial protocols: unconditional pass ----
            case EtherTypeArp:          // 0x0806 - hardware discovery must cross the bridge
            case EtherTypeProfinetRt:   // 0x8892 - PROFINET Real-Time
            case EtherTypePowerlink:    // 0x88AB - Ethernet POWERLINK
            case EtherTypeEtherCat:     // 0x88E1 - EtherCAT master/slave
            case EtherTypeGoose:        // 0x891D - IEC 61850 GOOSE
                return true;

            // ---- (B) IP traffic: broadly allowed, minus consumer discovery ports ----
            case EtherTypeIpv4:
                return InspectIpv4(frame, payloadOffset);

            case EtherTypeIpv6:
                return InspectIpv6(frame, payloadOffset);

            // ---- Broad pass: anything else (LLDP, PTP, vendor L2 protocols, ...) is industrial-plausible ----
            default:
                return true;
        }
    }

    /// <param name="ipOffset">Offset of the first byte of the IPv4 header inside <paramref name="frame"/>.</param>
    private static bool InspectIpv4(ReadOnlySpan<byte> frame, int ipOffset)
    {
        // Minimum IPv4 header is 20 bytes.
        if (frame.Length < ipOffset + 20)
        {
            return true; // Malformed/truncated: fail open, the endpoint will discard it.
        }

        // IPv4 byte 0: bits 7..4 version, bits 3..0 IHL in 32-bit words.
        int ihl = (frame[ipOffset] & 0x0F) * 4;
        if (ihl < 20 || frame.Length < ipOffset + ihl)
        {
            return true;
        }

        // IPv4 bytes 6..7: flags + fragment offset. Non-zero fragment offset => no L4 header present.
        int fragmentOffset = ((frame[ipOffset + 6] & 0x1F) << 8) | frame[ipOffset + 7];
        if (fragmentOffset != 0)
        {
            return true; // Later fragments of an already-admitted datagram.
        }

        // IPv4 byte 9: protocol.
        byte protocol = frame[ipOffset + 9];
        int l4Offset = ipOffset + ihl;

        return InspectTransport(frame, protocol, l4Offset);
    }

    /// <param name="ipOffset">Offset of the first byte of the IPv6 header inside <paramref name="frame"/>.</param>
    private static bool InspectIpv6(ReadOnlySpan<byte> frame, int ipOffset)
    {
        // Fixed IPv6 header is 40 bytes; byte 6 is the Next Header field.
        if (frame.Length < ipOffset + 40)
        {
            return true;
        }

        byte nextHeader = frame[ipOffset + 6];
        int l4Offset = ipOffset + 40;

        // Extension headers are not walked: only bare TCP/UDP is port-inspected, everything else passes.
        return InspectTransport(frame, nextHeader, l4Offset);
    }

    private static bool InspectTransport(ReadOnlySpan<byte> frame, byte protocol, int l4Offset)
    {
        // TCP/UDP both carry source port at bytes 0..1 and destination port at bytes 2..3.
        if (protocol is not (IpProtoUdp or IpProtoTcp) || frame.Length < l4Offset + 4)
        {
            return true; // ICMP, IGMP, GRE, truncated headers, ... : broad pass.
        }

        ushort sourcePort = (ushort)((frame[l4Offset] << 8) | frame[l4Offset + 1]);
        ushort destPort = (ushort)((frame[l4Offset + 2] << 8) | frame[l4Offset + 3]);

        if (protocol == IpProtoTcp)
        {
            // Modbus TCP (502) and EtherNet/IP explicit (44818) are the prioritised flows;
            // no consumer noise rides on TCP link-local discovery, so all TCP passes.
            return true;
        }

        // Industrial UDP (EtherNet/IP implicit 2222, explicit 44818) always wins over the drop list.
        if (Contains(IndustrialUdpPorts, sourcePort) || Contains(IndustrialUdpPorts, destPort))
        {
            return true;
        }

        // Consumer Windows link-local discovery noise: dropped in both directions.
        return !Contains(BlockedUdpPorts, sourcePort) && !Contains(BlockedUdpPorts, destPort);
    }

    private static bool Contains(ReadOnlySpan<ushort> ports, ushort port)
    {
        for (int i = 0; i < ports.Length; i++)
        {
            if (ports[i] == port)
            {
                return true;
            }
        }

        return false;
    }
}
