using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WGL2Bridge.Config;
using WGL2Bridge.Platform;

namespace WGL2Bridge.Network;

/// <summary>
/// Resolves the WireGuard overlay tunnel interface: its IPv4 address (the address the encapsulation
/// socket binds to, pinning egress) and its MTU (used to derive the TAP MTU). MTU is read from
/// <c>netsh interface ipv4 show interfaces</c> because the managed stack does not expose it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WireGuardInterface
{
    /// <summary>Friendly name of the tunnel interface.</summary>
    public required string Name { get; init; }

    /// <summary>Local IPv4 address of the tunnel interface.</summary>
    public IPAddress? LocalAddress { get; private set; }

    /// <summary>Tunnel interface MTU, or 1500 if it cannot be determined.</summary>
    public int Mtu { get; private set; } = 1500;

    /// <summary>Discovers the local address and MTU of the tunnel interface.</summary>
    public void Resolve(BridgeConfig config)
    {
        NetworkInterface nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Name, Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Tunnel interface '{Name}' not found.");

        if (!string.IsNullOrWhiteSpace(config.TunnelLocalAddress))
        {
            if (!IPAddress.TryParse(config.TunnelLocalAddress, out IPAddress? overrideIp) ||
                overrideIp.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new InvalidOperationException($"Invalid 'TunnelLocalAddress' '{config.TunnelLocalAddress}'.");
            }

            LocalAddress = overrideIp;
        }
        else
        {
            IPAddress[] ipv4 = nic.GetIPProperties().UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => u.Address)
                .ToArray();

            // Prefer a real overlay address over a 169.254.x.x APIPA, which Windows may assign
            // while the interface is up but not yet configured.
            LocalAddress = ipv4.FirstOrDefault(a => !IsLinkLocal(a)) ?? ipv4.FirstOrDefault()
                ?? throw new InvalidOperationException($"Tunnel interface '{Name}' has no IPv4 address.");
        }

        Mtu = QueryMtu();
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length >= 2 && bytes[0] == 169 && bytes[1] == 254;
    }

    private int QueryMtu()
    {
        var (exit, output) = ProcessRunner.Run("netsh.exe", "interface ipv4 show interfaces");
        if (exit != 0)
        {
            return 1500;
        }

        foreach (string line in output.Split('\n'))
        {
            string[] parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 &&
                string.Equals(parts[^1], Name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(parts[2], out int mtu))
            {
                return mtu;
            }
        }

        return 1500;
    }
}
