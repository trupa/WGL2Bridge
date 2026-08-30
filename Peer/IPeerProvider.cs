using System.Net;

namespace WGL2Bridge.Peer;

/// <summary>
/// A resolved bridge peer: its tunnel IP plus whether the overlay currently reports it as connected.
/// Provider-agnostic — shared by the NetBird implementation and any future NetMaker implementation.
/// </summary>
public sealed record PeerInfo(IPAddress Address, bool Connected);

/// <summary>
/// Resolves the bridge peer (IP + connection status) for an overlay network. Implementations hide how
/// discovery is performed (local CLI, REST API, pinned address, ...).
/// </summary>
public interface IPeerProvider
{
    /// <summary>Short provider name used in logs, e.g. "NetBird" or "NetMaker".</summary>
    string Name { get; }

    /// <summary>Resolves the peer, or returns null when it cannot be determined.</summary>
    PeerInfo? Resolve();
}
