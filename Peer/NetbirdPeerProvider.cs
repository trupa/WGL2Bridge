using WGL2Bridge.Netbird;

namespace WGL2Bridge.Peer;

/// <summary>Peer provider backed by the local NetBird daemon CLI (<c>netbird status --json</c>).</summary>
public sealed class NetbirdPeerProvider(NetbirdStatus netbird) : IPeerProvider
{
    public string Name => "NetBird";

    public PeerInfo? Resolve() => netbird.TryDiscoverPeer();
}
