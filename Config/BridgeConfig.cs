using WGL2Bridge.Logging;
using WGL2Bridge.Platform;

namespace WGL2Bridge.Config;

/// <summary>Encapsulation performed on the tunnel before an inner Ethernet frame.</summary>
public enum TransportMode
{
    /// <summary>Bare Ethernet frame carried as the payload of a raw IP packet (peer needs this software).</summary>
    Raw,

    /// <summary>VXLAN (UDP/4789) encapsulation; compatible with Linux kernel vxlan interfaces.</summary>
    Vxlan,

    /// <summary>GRETAP (GRE with protocol 0x6558) encapsulation; compatible with Linux gretap interfaces.</summary>
    GreTap,
}

/// <summary>
/// Single source of truth for WGL2Bridge configuration. Every field has a sensible default;
/// <see cref="Validate"/> fails fast on misconfiguration so a bad config exits with code 1 instead
/// of retrying forever.
/// </summary>
public sealed record BridgeConfig
{
    /// <summary>Friendly name of the TAP-Windows6 adapter to bridge.</summary>
    public string TapName { get; init; } = "Industrial-TAP";

    /// <summary>Friendly name of the WireGuard/NetBird tunnel interface.</summary>
    public string TunnelInterfaceName { get; init; } = "NetBird";

    /// <summary>Encapsulation mode for frames sent across the tunnel.</summary>
    public TransportMode TransportMode { get; init; } = TransportMode.Vxlan;

    /// <summary>Peer tunnel IP or hostname. Required unless NetBird CLI discovery is available.</summary>
    public string? PeerAddress { get; init; }

    /// <summary>Optional NetBird peer FQDN used to select the right peer during CLI discovery.</summary>
    public string? PeerName { get; init; }

    /// <summary>Optional override for the local tunnel IP; otherwise discovered from the interface.</summary>
    public string? TunnelLocalAddress { get; init; }

    /// <summary>VXLAN Network Identifier (0..16777215).</summary>
    public int VxlanVni { get; init; } = 100;

    /// <summary>VXLAN UDP destination port (IANA default 4789).</summary>
    public int VxlanDestinationPort { get; init; } = 4789;

    /// <summary>Optional GRE key (K bit) for GRETAP; null emits a plain 4-byte GRE header.</summary>
    public uint? GreTapKey { get; init; }

    /// <summary>IP protocol number used by Raw mode (1..255).</summary>
    public int RawIpProtocol { get; init; } = 99;

    /// <summary>Drop inbound tunnel packets whose source IP is not the peer. Disable only for diagnostics or multi-peer topologies.</summary>
    public bool PeerSourceValidation { get; init; } = true;

    /// <summary>Seconds to wait before rebinding after a tunnel or adapter failure.</summary>
    public int ReconnectDelaySeconds { get; init; } = 5;

    /// <summary>Seconds between tunnel health checks; 0 disables. Detects a down/missing interface or a dead tunnel (sending but no inbound) and reconnects.</summary>
    public int TunnelHealthCheckSeconds { get; init; } = 30;

    /// <summary>Stop the bridge (exit) when our own loop probe returns, preventing a broadcast storm.</summary>
    public bool StopOnLoopDetected { get; init; } = true;

    /// <summary>Seconds between loop-detection probe injections.</summary>
    public int LoopProbeIntervalSeconds { get; init; } = 10;

    /// <summary>Seconds between per-direction packet counter log lines; 0 disables stats logging.</summary>
    public int StatsIntervalSeconds { get; init; } = 15;

    /// <summary>Max broadcast/multicast frames forwarded per second per direction; 0 = unlimited.</summary>
    public int MaxBroadcastPps { get; init; } = 0;

    /// <summary>Loopback TCP port for the metrics endpoint; 0 disables it.</summary>
    public int MetricsPort { get; init; } = 0;

    /// <summary>Seconds a learned MAC entry is retained before it is considered unknown again.</summary>
    public int MacAgingSeconds { get; init; } = 300;

    /// <summary>Enable 0x88B5 loop detection probes.</summary>
    public bool EnableLoopDetection { get; init; } = true;

    /// <summary>Assume VLAN-tagged (802.1Q) frames when deriving the TAP MTU (18-byte inner header).</summary>
    public bool AssumeVlanTagged { get; init; } = true;

    /// <summary>
    /// Static IPv4 address for the TAP adapter in CIDR form (e.g. "192.168.45.158/24"). When set,
    /// the adapter is configured statically with no default gateway; when null/empty it falls back
    /// to DHCP. A bare address defaults to a /24 prefix.
    /// </summary>
    public string? TapIpAddress { get; init; }

    /// <summary>Trigger 'ipconfig /renew' on the TAP after a reconnect (DHCP mode only).</summary>
    public bool RenewDhcpOnReconnect { get; init; } = true;

    /// <summary>Create the TAP adapter if it does not already exist (requires the tap0901 driver package).</summary>
    public bool CreateTapIfMissing { get; init; } = true;

    /// <summary>Optional path to the TAP creation tool (tapctl.exe / tapinstall.exe / devcon.exe); otherwise auto-searched.</summary>
    public string? TapInstallToolPath { get; init; }

    /// <summary>Optional path to the TAP driver INF (OemVista.inf); otherwise searched from standard install dirs.</summary>
    public string? TapDriverInfPath { get; init; }

    /// <summary>Hardware ID passed to the installer when creating the adapter.</summary>
    public string TapHardwareId { get; init; } = "tap0901";

    /// <summary>Minimum level written to the console.</summary>
    public LogLevel ConsoleLogLevel { get; init; } = LogLevel.Information;

    /// <summary>Minimum level written to the log file.</summary>
    public LogLevel FileLogLevel { get; init; } = LogLevel.Debug;

    /// <summary>Path of the plain-text log file.</summary>
    public string LogFilePath { get; init; } = "wgl2bridge.log";

    /// <summary>Maximum log file size in bytes before it is rotated to '.1'.</summary>
    public long LogMaxBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>Consumer discovery UDP destination ports to drop (mDNS, LLMNR, SSDP, WS-Discovery, NetBIOS, ...).</summary>
    public int[] DropUdpPorts { get; init; } = [5353, 5355, 1900, 3702, 137, 138, 17500, 27036];

    /// <summary>VLAN IDs to bridge (802.1Q). When null/empty, all VLANs (and untagged) are allowed.</summary>
    public int[]? AllowVlans { get; init; }

    /// <summary>Optional explicit path to netbird.exe; otherwise resolved from PATH or standard install dirs.</summary>
    public string? NetbirdCliPath { get; init; }

    /// <summary>Returns a list of configuration problems; empty means the configuration is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(TapName))
            errors.Add("'TapName' must not be empty.");
        if (string.IsNullOrWhiteSpace(TunnelInterfaceName))
            errors.Add("'TunnelInterfaceName' must not be empty.");
        if (!Enum.IsDefined(TransportMode))
            errors.Add($"'TransportMode' value '{TransportMode}' is not a valid mode.");
        if (VxlanVni is < 0 or > 0xFFFFFF)
            errors.Add("'VxlanVni' must be between 0 and 16777215.");
        if (VxlanDestinationPort is < 1 or > 65535)
            errors.Add("'VxlanDestinationPort' must be between 1 and 65535.");
        if (RawIpProtocol is < 1 or > 255)
            errors.Add("'RawIpProtocol' must be between 1 and 255.");
        if (ReconnectDelaySeconds < 0)
            errors.Add("'ReconnectDelaySeconds' must be >= 0.");
        if (TunnelHealthCheckSeconds < 0)
            errors.Add("'TunnelHealthCheckSeconds' must be >= 0.");
        if (MacAgingSeconds < 1)
            errors.Add("'MacAgingSeconds' must be >= 1.");
        if (LoopProbeIntervalSeconds < 1)
            errors.Add("'LoopProbeIntervalSeconds' must be >= 1.");
        if (StatsIntervalSeconds < 0)
            errors.Add("'StatsIntervalSeconds' must be >= 0.");
        if (MaxBroadcastPps < 0)
            errors.Add("'MaxBroadcastPps' must be >= 0.");
        if (MetricsPort is < 0 or > 65535)
            errors.Add("'MetricsPort' must be between 0 and 65535.");
        foreach (int port in DropUdpPorts)
        {
            if (port is < 0 or > 65535)
            {
                errors.Add($"'DropUdpPorts' contains invalid port {port}.");
                break;
            }
        }
        if (AllowVlans is not null)
        {
            foreach (int vlan in AllowVlans)
            {
                if (vlan is < 0 or > 4094)
                {
                    errors.Add($"'AllowVlans' contains invalid VLAN {vlan} (0..4094).");
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(LogFilePath))
            errors.Add("'LogFilePath' must not be empty.");
        if (LogMaxBytes < 1024)
            errors.Add("'LogMaxBytes' must be at least 1024.");
        if (!string.IsNullOrWhiteSpace(TapIpAddress) &&
            !Ipv4Cidr.TryParse(TapIpAddress, out _, out _))
            errors.Add($"'TapIpAddress' value '{TapIpAddress}' is not a valid IPv4 address or CIDR.");
        if (string.IsNullOrWhiteSpace(TapHardwareId))
            errors.Add("'TapHardwareId' must not be empty.");

        return errors;
    }
}
