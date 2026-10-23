using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Win32;
using WGL2Bridge.Config;
using WGL2Bridge.Logging;
using WGL2Bridge.Platform;

namespace WGL2Bridge.Tap;

/// <summary>
/// Applies Windows networking quirks around the bridged TAP adapter: route isolation (metric 9999 +
/// DisableDefaultRoutes) before DHCP can install a default route, and MTU derivation. Changes are
/// made only when the current setting differs, so repeated starts do not generate network-change
/// events that can make NetBird bounce the tunnel.
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
        // Only change the metric when it differs. Re-applying it on every start is a Windows network
        // change that can make NetBird's monitor bounce the tunnel repeatedly.
        if (QueryInterfaceMetric() != IsolationMetric)
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
        }
        else
        {
            BridgeLog.Debug($"'{_config.TapName}' already has metric {IsolationMetric}; skipping.");
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{_adapterId}",
                writable: true);

            if (key is null)
            {
                BridgeLog.Debug($"Adapter registry key for '{_adapterId}' not found; skipping DisableDefaultRoutes.");
                return;
            }

            if (key.GetValue("DisableDefaultRoutes") is int existing && existing == 1)
            {
                BridgeLog.Debug($"'DisableDefaultRoutes=1' already set for '{_adapterId}'.");
                return;
            }

            key.SetValue("DisableDefaultRoutes", 1, RegistryValueKind.DWord);
            BridgeLog.Debug($"Set 'DisableDefaultRoutes=1' for adapter '{_adapterId}'.");
        }
        catch (Exception ex)
        {
            BridgeLog.Warning($"Failed to write DisableDefaultRoutes for '{_adapterId}': {ex.Message}");
        }
    }

    /// <summary>Reads the IPv4 interface metric for the TAP, or null if it cannot be determined.</summary>
    private int? QueryInterfaceMetric()
    {
        var (exit, output) = ProcessRunner.Run("netsh.exe", "interface ipv4 show interfaces");
        if (exit != 0)
        {
            return null;
        }

        foreach (string line in output.Split('\n'))
        {
            string[] parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 &&
                string.Equals(parts[^1], _config.TapName, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(parts[1], out int metric))
            {
                return metric;
            }
        }

        return null;
    }

    /// <summary>
    /// Configures the TAP adapter address. If 'TapIpAddress' is set it is applied statically with an
    /// explicitly empty default gateway; otherwise the adapter falls back to DHCP.
    /// </summary>
    public void ConfigureAddress()
    {
        if (string.IsNullOrWhiteSpace(_config.TapIpAddress))
        {
            // Avoid a redundant 'netsh ... source=dhcp' (a network change) when DHCP is already on.
            if (QueryDhcpEnabled() == true)
            {
                BridgeLog.Debug($"TAP adapter '{_config.TapName}' is already using DHCP; skipping.");
                return;
            }

            SetDhcp();
            return;
        }

        if (!Ipv4Cidr.TryParse(_config.TapIpAddress, out IPAddress address, out int prefixLength))
        {
            BridgeLog.Error($"Invalid 'TapIpAddress' '{_config.TapIpAddress}': expected IPv4/CIDR such as '192.168.45.158/24'.");
            return;
        }

        if (HasAddress(address, prefixLength))
        {
            BridgeLog.Debug($"TAP adapter '{_config.TapName}' already has {address}/{prefixLength}; skipping.");
            return;
        }

        SetStatic(address, prefixLength);
    }

    /// <summary>Reads the DHCP state from netsh, or null if it cannot be determined.</summary>
    private bool? QueryDhcpEnabled()
    {
        var (exit, output) = ProcessRunner.Run(
            "netsh.exe",
            $"interface ipv4 show config name=\"{_config.TapName}\"");

        if (exit != 0)
        {
            return null;
        }

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("DHCP enabled:", StringComparison.OrdinalIgnoreCase))
            {
                string value = trimmed["DHCP enabled:".Length..].Trim();
                if (value.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (value.Equals("No", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return null;
    }

    /// <summary>True when the TAP already has the specified IPv4 address and prefix.</summary>
    private bool HasAddress(IPAddress address, int prefixLength)
    {
        NetworkInterface? nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Name, _config.TapName, StringComparison.OrdinalIgnoreCase));
        if (nic is null)
        {
            return false;
        }

        return nic.GetIPProperties().UnicastAddresses.Any(u =>
            u.Address.AddressFamily == AddressFamily.InterNetwork &&
            u.Address.Equals(address) &&
            u.PrefixLength == prefixLength);
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

    /// <summary>Persists the given MTU on the TAP adapter via netsh. Returns false on failure.</summary>
    public bool ApplyMtu(int mtu)
    {
        var (exit, output) = ProcessRunner.Run(
            "netsh.exe",
            $"interface ipv4 set subinterface \"{_config.TapName}\" mtu={mtu} store=persistent");

        if (exit != 0)
        {
            BridgeLog.Warning($"Failed to set MTU {mtu} on '{_config.TapName}': {output.Trim()}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies the MTU only when the adapter does not already report it. Avoids a network
    /// configuration change (and a possible NetBird reconnect) on every start.
    /// </summary>
    public bool ApplyMtuIfNeeded(int mtu)
    {
        if (QueryIpv4Mtu() == mtu)
        {
            BridgeLog.Debug($"'{_config.TapName}' already has MTU {mtu}; skipping.");
            return true;
        }

        return ApplyMtu(mtu);
    }

    private int? QueryIpv4Mtu()
    {
        try
        {
            NetworkInterface? nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Name, _config.TapName, StringComparison.OrdinalIgnoreCase));
            return nic?.GetIPProperties().GetIPv4Properties()?.Mtu;
        }
        catch
        {
            return null; // best effort; fall back to applying the MTU
        }
    }
}
