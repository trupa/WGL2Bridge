using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WGL2Bridge;

/// <summary>
/// Locates the named WireGuard tunnel adapter and exposes its local tunnel IP so that the
/// encapsulation socket can be bound to it, guaranteeing egress through the encrypted tunnel only.
/// </summary>
public static class WireGuardInterface
{
    public static NetworkInterface Find(string adapterName, IPAddress? fallbackAddress = null)
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (string.Equals(nic.Name, adapterName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nic.Description, adapterName, StringComparison.OrdinalIgnoreCase))
            {
                return nic;
            }
        }

        // NetBird/WireGuard may name the interface differently than configured; match on the tunnel IP instead.
        if (fallbackAddress is not null)
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.Equals(fallbackAddress))
                    {
                        return nic;
                    }
                }
            }
        }

        throw new InvalidOperationException(
            $"WireGuard adapter '{adapterName}' was not found. Start the tunnel before launching the bridge.");
    }

    /// <summary>Returns the IPv4 address assigned to the tunnel adapter.</summary>
    public static IPAddress GetLocalTunnelAddress(NetworkInterface tunnel)
    {
        foreach (UnicastIPAddressInformation address in tunnel.GetIPProperties().UnicastAddresses)
        {
            if (address.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                return address.Address;
            }
        }

        throw new InvalidOperationException(
            $"WireGuard adapter '{tunnel.Name}' has no IPv4 address assigned.");
    }

    public static int GetMtu(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GetIPv4Properties().Mtu;
        }
        catch (NetworkInformationException)
        {
            return -1;
        }
    }
}
