using WGL2Bridge.Config;

namespace WGL2Bridge.Network;

/// <summary>Which frame field rejected a frame. Drives counter and metric names.</summary>
public enum FilterDropReason : byte
{
    None = 0,
    LinkLocal,
    Vlan,
    EtherType,
    IpProtocol,
    UdpPort,
    TcpPort,
    Icmpv4Type,
    Icmpv6Type,
}

/// <summary>A dropped frame's cause: the field that matched plus the exact value that matched it.</summary>
public readonly record struct FilterDrop(FilterDropReason Reason, ulong Value)
{
    /// <summary>Nothing was dropped.</summary>
    public static readonly FilterDrop None = new(FilterDropReason.None, 0);
}

/// <summary>
/// Effective, pre-computed filter policy. Configuration is grouped by the <em>field</em> that is
/// matched — EtherType, destination MAC, IP protocol, UDP port, TCP port, ICMP type — so a value can
/// never be filed under the wrong concept: a UDP port in a "consumer discovery" list and one in a
/// "gateway redundancy" list are the same list here.
/// <para>
/// Each list is <c>null</c> for the built-in default set, an explicit array replaces it, and an empty
/// array means "drop nothing". One entry is not configurable: 802.3x PAUSE
/// (<c>01:80:C2:00:00:01</c>) is always dropped, because 802.3 does not let bridges forward MAC
/// Control frames.
/// </para>
/// The configured values are also kept as arrays so the bridge can keep one counter per configured
/// entry and report exactly which value is being dropped.
/// </summary>
public sealed class FilterPolicy
{
    /// <summary>
    /// IEEE 802.1 reserved group (STP/RSTP/MSTP, LACP, 802.1X/EAPOL, 802.3x PAUSE, slow protocols)
    /// plus Cisco CDP/VTP/DTP/UDLD (01:00:0C:CC:CC:CC) and PVST+/PAgP (01:00:0C:CC:CC:CD).
    /// </summary>
    public static readonly string[] DefaultLinkLocalMacs =
    [
        "01:80:C2:00:00:00-0F",
        "01:00:0C:CC:CC:CC",
        "01:00:0C:CC:CC:CD",
    ];

    /// <summary>IGMP 2, EIGRP 88, OSPF 89, PIM 103, VRRP/CARP 112.</summary>
    public static readonly int[] DefaultIpProtocols = [2, 88, 89, 103, 112];

    /// <summary>
    /// Consumer discovery (mDNS 5353, LLMNR 5355, SSDP 1900, WS-Discovery 3702, NetBIOS 137/138,
    /// Dropbox 17500, Steam 27036) plus gateway redundancy (HSRP 1985, GLBP 3222).
    /// </summary>
    public static readonly int[] DefaultUdpPorts =
        [5353, 5355, 1900, 3702, 137, 138, 17500, 27036, 1985, 3222];

    /// <summary>MAC Control PAUSE (<c>01:80:C2:00:00:01</c>) — never forwarded, not configurable.</summary>
    private const ulong MacControlPauseMac = 0x0180_C200_0001UL;

    /// <summary>STP/RSTP/MSTP BPDUs (<c>01:80:C2:00:00:00</c>); used to warn when they are forwarded.</summary>
    private const ulong SpanningTreeMac = 0x0180_C200_0000UL;

    private FilterPolicy(
        HashSet<ulong> linkLocalMacs,
        ulong[] linkLocalMacValues,
        bool[] etherTypes,
        ushort[] etherTypeValues,
        bool[] ipProtocols,
        int[] ipProtocolValues,
        bool[] udpPorts,
        int[] udpPortValues,
        bool[] tcpPorts,
        int[] tcpPortValues,
        bool[] icmpv4Types,
        int[] icmpv4Values,
        bool[] icmpv6Types,
        int[] icmpv6Values,
        bool[]? vlanAllowSet,
        string description)
    {
        LinkLocalMacs = linkLocalMacs;
        LinkLocalMacValues = linkLocalMacValues;
        EtherTypes = etherTypes;
        EtherTypeValues = etherTypeValues;
        IpProtocols = ipProtocols;
        IpProtocolValues = ipProtocolValues;
        UdpPorts = udpPorts;
        UdpPortValues = udpPortValues;
        TcpPorts = tcpPorts;
        TcpPortValues = tcpPortValues;
        Icmpv4Types = icmpv4Types;
        Icmpv4Values = icmpv4Values;
        Icmpv6Types = icmpv6Types;
        Icmpv6Values = icmpv6Values;
        VlanAllowSet = vlanAllowSet;
        Description = description;
    }

    /// <summary>Destination MACs to drop.</summary>
    public HashSet<ulong> LinkLocalMacs { get; }

    /// <summary>Same set as <see cref="LinkLocalMacs"/>, ordered, for per-value counters.</summary>
    public ulong[] LinkLocalMacValues { get; }

    public bool[] EtherTypes { get; }

    public ushort[] EtherTypeValues { get; }

    public bool[] IpProtocols { get; }

    public int[] IpProtocolValues { get; }

    public bool[] UdpPorts { get; }

    public int[] UdpPortValues { get; }

    public bool[] TcpPorts { get; }

    public int[] TcpPortValues { get; }

    /// <summary>False when no TCP port is configured, so the TCP path skips the lookup entirely.</summary>
    public bool HasTcpPorts => TcpPortValues.Length > 0;

    public bool[] Icmpv4Types { get; }

    public int[] Icmpv4Values { get; }

    public bool HasIcmpv4Types => Icmpv4Values.Length > 0;

    public bool[] Icmpv6Types { get; }

    public int[] Icmpv6Values { get; }

    public bool HasIcmpv6Types => Icmpv6Values.Length > 0;

    /// <summary>VLAN allow set; null means all VLANs (and untagged) pass.</summary>
    public bool[]? VlanAllowSet { get; }

    /// <summary>One-line summary of the effective policy, logged once at session start.</summary>
    public string Description { get; }

    /// <summary>Resolves a configuration into an effective policy, collecting problems into <paramref name="errors"/>.</summary>
    public static FilterPolicy Resolve(BridgeConfig config, List<string>? errors = null)
    {
        errors ??= [];

        // 802.3x PAUSE is the one entry that is not configurable: bridges do not forward MAC Control
        // frames, and a forwarded PAUSE would stall the far segment's transmit queue.
        HashSet<ulong> linkLocalMacs = [MacControlPauseMac];
        foreach (string text in config.DropLinkLocalMacs ?? DefaultLinkLocalMacs)
        {
            if (!TryParseMacList(text, linkLocalMacs))
            {
                errors.Add($"'DropLinkLocalMacs' contains invalid MAC or range '{text}' (expected AA:BB:CC:DD:EE:FF or AA:BB:CC:DD:EE:00-0F).");
            }
        }

        ulong[] linkLocalMacValues = [.. linkLocalMacs.Order()];

        bool[] etherTypes = new bool[65536];
        var etherTypeValues = new List<ushort>();
        foreach (string text in config.DropEtherTypes ?? [])
        {
            if (Ethernet.TryParseEtherType(text, out ushort etherType))
            {
                if (!etherTypes[etherType])
                {
                    etherTypes[etherType] = true;
                    etherTypeValues.Add(etherType);
                }
            }
            else
            {
                errors.Add($"'DropEtherTypes' contains invalid EtherType '{text}' (expected hex such as '0x88CC').");
            }
        }

        (bool[] ipProtocols, int[] ipProtocolValues) = BuildByteTable(
            config.DropIpProtocols ?? DefaultIpProtocols, "DropIpProtocols", "IP protocol", errors);

        (bool[] udpPorts, int[] udpPortValues) = BuildPortTable(
            config.DropUdpPorts ?? DefaultUdpPorts, "DropUdpPorts", errors);

        (bool[] tcpPorts, int[] tcpPortValues) = BuildPortTable(
            config.DropTcpPorts ?? [], "DropTcpPorts", errors);

        (bool[] icmpv4Types, int[] icmpv4Values) = BuildByteTable(
            config.DropIcmpv4Types ?? [], "DropIcmpv4Types", "ICMPv4 type", errors);

        (bool[] icmpv6Types, int[] icmpv6Values) = BuildByteTable(
            config.DropIcmpv6Types ?? [], "DropIcmpv6Types", "ICMPv6 type", errors);

        bool[]? vlanAllowSet = null;
        if (config.AllowVlans is not null)
        {
            var set = new bool[4096];
            bool any = false;
            foreach (int vlan in config.AllowVlans)
            {
                if (vlan is >= 0 and <= 4094)
                {
                    set[vlan] = true;
                    any = true;
                }
                else
                {
                    errors.Add($"'AllowVlans' contains invalid VLAN {vlan} (0..4094).");
                }
            }

            if (any)
            {
                vlanAllowSet = set;
            }
        }

        string description =
            "Filter: " +
            $"link-local={FormatDrop(linkLocalMacValues.Length, "mac", "macs")} " +
            $"ip={FormatDrop(ipProtocolValues.Length, "protocol", "protocols")} " +
            $"udp={FormatDrop(udpPortValues.Length, "port", "ports")} " +
            $"tcp={FormatDrop(tcpPortValues.Length, "port", "ports")} " +
            $"icmpv4={FormatDrop(icmpv4Values.Length, "type", "types")} " +
            $"icmpv6={FormatDrop(icmpv6Values.Length, "type", "types")} " +
            $"ethertype={FormatDrop(etherTypeValues.Count, "type", "types")} " +
            $"vlans={(vlanAllowSet is null ? "all" : "allow-list")}.";

        return new FilterPolicy(
            linkLocalMacs,
            linkLocalMacValues,
            etherTypes,
            [.. etherTypeValues],
            ipProtocols,
            ipProtocolValues,
            udpPorts,
            udpPortValues,
            tcpPorts,
            tcpPortValues,
            icmpv4Types,
            icmpv4Values,
            icmpv6Types,
            icmpv6Values,
            vlanAllowSet,
            description);
    }

    /// <summary>
    /// Reports policy consequences worth surfacing but not fatal: currently a link-local set that no
    /// longer drops STP BPDUs, which leaves cross-site loop protection to the bridge's own loop
    /// detector.
    /// </summary>
    public static void CollectWarnings(BridgeConfig config, List<string> warnings)
    {
        HashSet<ulong> macs = [MacControlPauseMac];
        foreach (string text in config.DropLinkLocalMacs ?? DefaultLinkLocalMacs)
        {
            TryParseMacList(text, macs);
        }

        if (!macs.Contains(SpanningTreeMac))
        {
            warnings.Add(
                "'DropLinkLocalMacs' no longer drops STP/RSTP/MSTP BPDUs (01:80:C2:00:00:00): they will cross the tunnel, so cross-site loops are guarded only by the bridge's own loop detection.");
        }
    }

    private static string FormatDrop(int count, string singular, string plural) =>
        count > 0 ? $"drop({count} {(count == 1 ? singular : plural)})" : "none";

    private static (bool[] Table, int[] Values) BuildByteTable(
        IReadOnlyCollection<int> values, string key, string what, List<string> errors)
    {
        var table = new bool[256];
        var configured = new List<int>();
        foreach (int value in values)
        {
            if (value is >= 0 and <= 255)
            {
                if (!table[value])
                {
                    table[value] = true;
                    configured.Add(value);
                }
            }
            else
            {
                errors.Add($"'{key}' contains invalid {what} {value} (0..255).");
            }
        }

        return (table, [.. configured]);
    }

    private static (bool[] Table, int[] Values) BuildPortTable(
        IReadOnlyCollection<int> values, string key, List<string> errors)
    {
        var table = new bool[65536];
        var configured = new List<int>();
        foreach (int value in values)
        {
            if (value is >= 0 and <= 65535)
            {
                if (!table[value])
                {
                    table[value] = true;
                    configured.Add(value);
                }
            }
            else
            {
                errors.Add($"'{key}' contains invalid port {value} (0..65535).");
            }
        }

        return (table, [.. configured]);
    }

    /// <summary>Parses "AA:BB:CC:DD:EE:FF" or a last-octet range "AA:BB:CC:DD:EE:00-0F" into the set.</summary>
    private static bool TryParseMacList(string text, HashSet<ulong> macs)
    {
        string macPart = text;
        int rangeEnd = -1;

        int dash = text.LastIndexOf('-');
        if (dash > 0)
        {
            string tail = text[(dash + 1)..];
            if (!byte.TryParse(tail, System.Globalization.NumberStyles.HexNumber, null, out byte parsedEnd))
            {
                return false;
            }

            rangeEnd = parsedEnd;
            macPart = text[..dash];
        }

        string[] octets = macPart.Split(':');
        if (octets.Length != 6)
        {
            return false;
        }

        ulong mac = 0;
        for (int i = 0; i < 6; i++)
        {
            if (!byte.TryParse(octets[i], System.Globalization.NumberStyles.HexNumber, null, out byte value))
            {
                return false;
            }

            mac = (mac << 8) | value;
        }

        if (rangeEnd < 0)
        {
            macs.Add(mac);
            return true;
        }

        // A range applies to the last octet only: "01:80:C2:00:00:00-0F" covers ...:00 through ...:0F.
        int rangeStart = (int)(mac & 0xFF);
        if (rangeEnd < rangeStart)
        {
            return false;
        }

        ulong prefix = mac & 0xFFFF_FFFF_FF00UL;
        for (int last = rangeStart; last <= rangeEnd; last++)
        {
            macs.Add(prefix | (ulong)(uint)last);
        }

        return true;
    }
}
