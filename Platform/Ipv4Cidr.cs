using System.Net;
using System.Net.Sockets;

namespace WGL2Bridge.Platform;

/// <summary>
/// Parsing helpers for IPv4 CIDR notation (e.g. "192.168.45.158/24"). A bare address defaults to a
/// /24 prefix. Used only during configuration, not on the packet hot path.
/// </summary>
public static class Ipv4Cidr
{
    /// <summary>
    /// Parses an IPv4 address with an optional /prefix into <paramref name="address"/> and
    /// <paramref name="prefixLength"/>. Returns false on any malformed input.
    /// </summary>
    public static bool TryParse(string value, out IPAddress address, out int prefixLength)
    {
        address = IPAddress.None;
        prefixLength = 24;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int slash = value.IndexOf('/');
        string ipPart = slash >= 0 ? value[..slash] : value;

        if (!IPAddress.TryParse(ipPart, out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        address = parsed;

        if (slash >= 0)
        {
            string prefixPart = value[(slash + 1)..];
            if (!int.TryParse(prefixPart, out prefixLength) || prefixLength is < 1 or > 32)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Converts a prefix length to a dotted-quad subnet mask (e.g. 24 → "255.255.255.0").</summary>
    public static string PrefixToSubnetMask(int prefixLength)
    {
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return $"{(byte)(mask >> 24)}.{(byte)(mask >> 16)}.{(byte)(mask >> 8)}.{(byte)mask}";
    }
}
