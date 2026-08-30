using System.Net;
using System.Net.Sockets;
using WGL2Bridge.Config;

namespace WGL2Bridge.Peer;

/// <summary>
/// Composes overlay discovery with the configured pinned 'PeerAddress' fallback. Precedence (identical
/// to the original inline resolution): name-based discovery first, then the pinned address, then
/// any-connected discovery. A future NetMaker provider slots in by replacing the overlay provider;
/// the pinned-address fallback stays provider-agnostic.
/// </summary>
public sealed class PeerResolver(BridgeConfig config, IPeerProvider overlay) : IPeerProvider
{
    public string Name => overlay.Name;

    public PeerInfo? Resolve()
    {
        if (!string.IsNullOrWhiteSpace(config.PeerName))
        {
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

            IPAddress? resolved = Dns.GetHostAddresses(config.PeerAddress)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (resolved is not null)
            {
                return new PeerInfo(resolved, Connected: true);
            }
        }

        return overlay.Resolve();
    }
}
