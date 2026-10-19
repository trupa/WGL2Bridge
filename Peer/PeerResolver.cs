using System.Net;
using System.Net.Sockets;
using WGL2Bridge.Config;

namespace WGL2Bridge.Peer;

/// <summary>
/// Resolves the configured peer without guessing. Precedence:
/// <list type="number">
/// <item>When 'PeerName' is set, only an exact FQDN match is accepted.</item>
/// <item>When 'PeerAddress' is set (or the named peer is absent), the explicit address is used.</item>
/// <item>If neither yields a target, returns null so the caller logs and waits.</item>
/// </list>
/// It never falls back to an arbitrary connected peer, so a typo or a missing peer can never
/// silently bridge to the wrong machine.
/// </summary>
public sealed class PeerResolver(BridgeConfig config, IPeerProvider overlay) : IPeerProvider
{
    public string Name => overlay.Name;

    public PeerInfo? Resolve()
    {
        if (!string.IsNullOrWhiteSpace(config.PeerName))
        {
            // Exact FQDN match only; the provider must not substitute another peer.
            PeerInfo? discovered = overlay.Resolve();
            if (discovered is not null)
            {
                return discovered;
            }
        }

        if (!string.IsNullOrWhiteSpace(config.PeerAddress))
        {
            if (IPAddress.TryParse(config.PeerAddress, out IPAddress? ip))
            {
                return new PeerInfo(ip, Connected: true);
            }

            try
            {
                IPAddress? resolved = Dns.GetHostAddresses(config.PeerAddress)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (resolved is not null)
                {
                    return new PeerInfo(resolved, Connected: true);
                }
            }
            catch (SocketException)
            {
                // Host did not resolve: treat as "not found" so the caller waits and retries.
            }
            catch (ArgumentException)
            {
                // Malformed host name: same.
            }
        }

        // No safe target. Never fall back to an arbitrary peer.
        return null;
    }
}
