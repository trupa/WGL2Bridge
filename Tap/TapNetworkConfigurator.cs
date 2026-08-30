using System.Net;
using System.Runtime.Versioning;
using Microsoft.Win32;
using WGL2Bridge.Config;
using WGL2Bridge.Logging;
using WGL2Bridge.Platform;

namespace WGL2Bridge.Tap;

/// <summary>
/// Applies Windows networking quirks around the bridged TAP adapter: route isolation (metric 9999 +
/// DisableDefaultRoutes) before DHCP can install a default route, and MTU derivation. MTU is
/// re-derived and re-applied every session; route isolation is a one-time, persistent change.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TapNetworkConfigurator
{
    private const int IsolationMetric = 9999;

    private readonly BridgeConfig _config;
    private readonly string _adapterId;

    public TapNetworkConfigurator(BridgeConfig config, string adapterId)
    {
        _config = config;
        _adapterId = adapterId;
    }

    /// <summary>Forces metric 9999 and suppresses default-route installation on the TAP adapter.</summary>
    public void ApplyRouteIsolation()
    {
        var (exit, output) = ProcessRunner.Run(
            "netsh.exe",
            $"interface ipv4 set interface \"{_config.TapName}\" metric={IsolationMetric}");

        if (exit != 0)
        {
            BridgeLog.Warning($"Failed to set metric {IsolationMetric} on '{_config.TapName}': {output.Trim()}");
        }
        else
        {
            BridgeLog.Info($"Route isolation applied: '{_config.TapName}' metric {IsolationMetric}.");
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{_adapterId}",
                writable: true);

            key?.SetValue("DisableDefaultRoutes", 1, RegistryValueKind.DWord);
            BridgeLog.Debug($"Set 'DisableDefaultRoutes=1' for adapter '{_adapterId}'.");
        }
        catch (Exception ex)
        {
            BridgeLog.Warning($"Failed to write DisableDefaultRoutes for '{_adapterId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Configures the TAP adapter address. If 'TapIpAddress' is set it is applied statically with an
    /// explicitly empty default gateway; otherwise the adapter falls back to DHCP.
    /// </summary>
    public void ConfigureAddress()
    {
        if (string.IsNullOrWhiteSpace(_config.TapIpAddress))
        {
            SetDhcp();
            return;
        }

        if (!Ipv4Cidr.TryParse(_config.TapIpAddress, out IPAddress address, out int prefixLength))
        {
            BridgeLog.Error($"Invalid 'TapIpAddress' '{_config.TapIpAddress}': expected IPv4/CIDR such as '192.168.45.158/24'.");
            return;
        }

        SetStatic(address, prefixLength);
    }

    private void SetStatic(IPAddress address, int prefixLength)
    {
        string mask = Ipv4Cidr.PrefixToSubnetMask(prefixLength);

        // 'none' as the gateway argument leaves the default gateway empty.
        var (exit, output) = ProcessRunner.Run(
            "netsh.exe",
            $"interface ipv4 set address name=\"{_config.TapName}\" static {address} {mask} none");

        if (exit != 0)
        {
            BridgeLog.Warning($"Failed to set '{_config.TapName}' to {address}/{prefixLength}: {output.Trim()}");
        }
        else
        {
            BridgeLog.Info($"TAP adapter '{_config.TapName}' set to static {address}/{prefixLength} (no default gateway).");
        }
    }

    private void SetDhcp()
    {
        var (exit, output) = ProcessRunner.Run(
            "netsh.exe",
            $"interface ipv4 set address name=\"{_config.TapName}\" source=dhcp");

        if (exit == 0)
        {
            BridgeLog.Info($"TAP adapter '{_config.TapName}' set to DHCP.");
        }
        else if (output.Contains("already enabled", StringComparison.OrdinalIgnoreCase))
        {
            BridgeLog.Debug($"TAP adapter '{_config.TapName}' is already in DHCP mode.");
        }
        else
        {
            BridgeLog.Warning($"Failed to set '{_config.TapName}' to DHCP: {output.Trim()}");
        }
    }

    /// <summary>
    /// Forces a DHCP renewal on the TAP adapter. Used after a reconnect to clear any stale lease or
    /// neighbor/ARP state so the bridged segment's addressing is re-established.
    /// </summary>
    public void RenewDhcp()
    {
        var (exit, output) = ProcessRunner.Run("ipconfig.exe", $"/renew \"{_config.TapName}\"");

        if (exit != 0)
        {
            BridgeLog.Warning($"Failed to renew DHCP on '{_config.TapName}': {output.Trim()}");
        }
        else
        {
            BridgeLog.Info($"DHCP renewed on '{_config.TapName}'.");
        }
    }

    /// <summary>
    /// Derives the TAP MTU so a full-size frame plus encapsulation still fits the tunnel MTU:
    /// tunnel MTU - outer header - inner Ethernet header. Clamped to a sane range.
    /// </summary>
    public int DeriveTapMtu(int tunnelMtu, int encapsulationOverhead)
    {
        int innerHeader = _config.AssumeVlanTagged ? 18 : 14;
        int mtu = tunnelMtu - encapsulationOverhead - innerHeader;
        return Math.Clamp(mtu, 576, 9000);
    }

    /// <summary>Persists the given MTU on the TAP adapter via netsh (best-effort, logged).</summary>
    public void ApplyMtu(int mtu)
    {
        var (exit, output) = ProcessRunner.Run(
            "netsh.exe",
            $"interface ipv4 set subinterface \"{_config.TapName}\" mtu={mtu} store=persistent");

        if (exit != 0)
        {
            BridgeLog.Warning($"Failed to set MTU {mtu} on '{_config.TapName}': {output.Trim()}");
        }
    }
}
