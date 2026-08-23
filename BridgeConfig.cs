using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace WGL2Bridge;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    GenerationMode = JsonSourceGenerationMode.Metadata)]

/// <summary>
/// Bridge configuration, loadable from a JSON document on disk.
/// Values that can be discovered at runtime (tunnel adapter, port defaults, MTU) are optional.
/// </summary>
public sealed class BridgeConfig
{
    /// <summary>Windows connection name of the virtual TAP adapter that supplies raw Layer 2 frames.</summary>
    public string TapAdapterName { get; set; } = "Industrial-TAP";

    /// <summary>Source of the local and remote tunnel IPs: None (literal values) or Netbird.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PeerDiscoveryMode>))]
    public PeerDiscoveryMode PeerDiscovery { get; set; } = PeerDiscoveryMode.Netbird;

    /// <summary>Remote bridge peer: NetBird FQDN, short hostname, or tunnel IP. Empty prompts for interactive selection.</summary>
    public string RemotePeer { get; set; } = string.Empty;

    /// <summary>
    /// Tunnel adapter connection name. Optional under NetBird discovery, where the adapter is
    /// located by its tunnel IP; required when PeerDiscovery is None.
    /// </summary>
    public string? WireGuardAdapterName { get; set; }

    /// <summary>Wire format used inside the tunnel: Raw, Vxlan or Gretap.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<EncapsulationMode>))]
    public EncapsulationMode EncapsulationMode { get; set; } = EncapsulationMode.Raw;

    /// <summary>UDP port for Raw/VXLAN. Null selects the mode default (Raw 55555, VXLAN 4789).</summary>
    public int? EncapsulationPort { get; set; }

    /// <summary>24-bit VXLAN network identifier; must match the VNI on the Linux peer.</summary>
    public int VxlanVni { get; set; } = 4096;

    /// <summary>Optional 32-bit GRE key (RFC 2890). 0 disables the key field.</summary>
    public uint GreKey { get; set; }

    /// <summary>Enables the broad industrial pass / consumer-noise drop filter.</summary>
    public bool EnableBroadIndustrialFilter { get; set; } = true;

    /// <summary>Forces a TAP MTU instead of deriving it from the tunnel MTU and encapsulation overhead.</summary>
    public int? TapMtuOverride { get; set; }

    /// <summary>Applies the derived/overridden MTU to the TAP adapter at startup.</summary>
    public bool EnforceTapMtu { get; set; } = true;

    /// <summary>How the TAP adapter's IP configuration is managed: Manual, Dhcp or Static.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TapAddressMode>))]
    public TapAddressMode TapAddressMode { get; set; } = TapAddressMode.Manual;

    /// <summary>Static address applied when <see cref="TapAddressMode"/> is Static.</summary>
    public string? TapStaticAddress { get; set; }

    public int TapStaticPrefixLength { get; set; } = 24;

    /// <summary>Ignores default routes on the TAP adapter so a DHCP gateway cannot hijack host traffic.</summary>
    public bool IsolateTapRouting { get; set; } = true;

    /// <summary>Unbinds IPv6 and link-layer discovery from the TAP adapter to cut broadcast noise.</summary>
    public bool DisableIpv6OnTap { get; set; }

    /// <summary>Learns MAC addresses so known-local unicast frames are not flooded across the tunnel.</summary>
    public bool EnableMacLearning { get; set; } = true;

    public int MacAgingSeconds { get; set; } = 300;

    /// <summary>Emits periodic probe frames and reports when one returns, indicating a Layer 2 loop.</summary>
    public bool EnableLoopDetection { get; set; } = true;

    public int LoopProbeSeconds { get; set; } = 5;

    /// <summary>Stops the bridge when a loop is detected, instead of only logging it.</summary>
    public bool StopOnLoopDetected { get; set; } = true;

    /// <summary>When true, a missing TAP adapter is provisioned via tapctl.exe (requires an elevated process).</summary>
    public bool AutoCreateTapAdapter { get; set; }

    /// <summary>Optional explicit path to netbird.exe; discovered automatically when empty.</summary>
    public string? NetbirdCliPath { get; set; }

    /// <summary>Seconds to wait before re-binding the tunnel socket after the tunnel drops.</summary>
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>Optional path for the text log file. Relative paths resolve under the executable directory.</summary>
    public string LogFilePath { get; set; } = "wgl2bridge.log.txt";

    /// <summary>Minimum level written to the console sink.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))]
    public LogLevel ConsoleLogLevel { get; set; } = LogLevel.Information;

    /// <summary>Minimum level written to the file sink.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))]
    public LogLevel FileLogLevel { get; set; } = LogLevel.Debug;

    /// <summary>TAP MTU used before a tunnel MTU has been measured.</summary>
    public const int FallbackTapMtu = 1350;

    /// <summary>TAP MTU actually in force: the override, or the value derived at startup.</summary>
    [JsonIgnore]
    public int EffectiveTapMtu { get; set; } = FallbackTapMtu;

    /// <summary>UDP port in force for the selected encapsulation mode.</summary>
    [JsonIgnore]
    public int EffectivePort => EncapsulationPort ?? (EncapsulationMode == EncapsulationMode.Vxlan ? 4789 : 55555);

    public static BridgeConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Configuration file not found: {path}", path);
        }

        using FileStream stream = File.OpenRead(path);
        BridgeConfig config = JsonSerializer.Deserialize(
                                  stream,
                                  BridgeConfigJsonContext.Default.BridgeConfig)
                              ?? throw new InvalidDataException($"Configuration file '{path}' is empty or invalid.");
        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TapAdapterName))
        {
            throw new InvalidDataException($"{nameof(TapAdapterName)} must be set.");
        }

        if (PeerDiscovery == PeerDiscoveryMode.None)
        {
            if (string.IsNullOrWhiteSpace(WireGuardAdapterName))
            {
                throw new InvalidDataException(
                    $"{nameof(WireGuardAdapterName)} must be set when {nameof(PeerDiscovery)} is None.");
            }

            if (string.IsNullOrWhiteSpace(RemotePeer))
            {
                throw new InvalidDataException(
                    $"{nameof(RemotePeer)} must be the tunnel IP of the remote bridge when {nameof(PeerDiscovery)} is None.");
            }
        }

        if (EffectivePort is < 1 or > 65535)
        {
            throw new InvalidDataException($"{nameof(EncapsulationPort)} must be between 1 and 65535.");
        }

        if (VxlanVni is < 0 or > 0xFFFFFF)
        {
            throw new InvalidDataException($"{nameof(VxlanVni)} must be a 24-bit value (0..16777215).");
        }

        if (TapMtuOverride is < 576 or > 9000)
        {
            throw new InvalidDataException($"{nameof(TapMtuOverride)} must be between 576 and 9000.");
        }

        if (TapAddressMode == TapAddressMode.Static)
        {
            if (!IPAddress.TryParse(TapStaticAddress, out _))
            {
                throw new InvalidDataException(
                    $"{nameof(TapStaticAddress)} must be a valid IPv4 address when {nameof(TapAddressMode)} is Static.");
            }

            if (TapStaticPrefixLength is < 1 or > 32)
            {
                throw new InvalidDataException($"{nameof(TapStaticPrefixLength)} must be between 1 and 32.");
            }
        }

        if (MacAgingSeconds < 1)
        {
            throw new InvalidDataException($"{nameof(MacAgingSeconds)} must be at least 1.");
        }

        if (LoopProbeSeconds < 1)
        {
            throw new InvalidDataException($"{nameof(LoopProbeSeconds)} must be at least 1.");
        }

        if (ReconnectDelaySeconds < 1)
        {
            throw new InvalidDataException($"{nameof(ReconnectDelaySeconds)} must be at least 1.");
        }

        EffectiveTapMtu = TapMtuOverride ?? EffectiveTapMtu;
    }
}
