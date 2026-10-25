using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WGL2Bridge.Config;
using WGL2Bridge.Logging;
using WGL2Bridge.Network;
using WGL2Bridge.Peer;
using WGL2Bridge.Tap;
using WGL2Bridge.Transport;

namespace WGL2Bridge.Core;

/// <summary>
/// The symmetric two-pump pipeline:
/// <list type="bullet">
/// <item>TAP → tunnel: overlapped read → filter/MAC/loop → constant header already in headroom → sendto.</item>
/// <item>Tunnel → TAP: recvfrom → header validation + same filters → overlapped write to the TAP device.</item>
/// </list>
/// Tunnel loss tears down the session, waits <c>ReconnectDelaySeconds</c> and rebinds, reusing the
/// cached peer address so a flap never re-prompts.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Bridge : IAsyncDisposable
{
    private readonly BridgeConfig _config;
    private IPAddress? _peerAddress;
    private readonly IPeerProvider _peerProvider;
    private readonly TapAdapter _tap;
    private readonly TapNetworkConfigurator _networkConfigurator;
    private readonly IBridgeTransport _transport;
    private readonly FrameFilter _filter;
    private readonly FilterPolicy _filterPolicy;
    private readonly MacTable _macTable;
    private readonly LoopDetector _loopDetector;
    private readonly WireGuardInterface _tunnel;
    private int _bufferSize = 2048;

    // Largest frame that fits the tunnel payload (tunnel MTU - encapsulation overhead). Frames above
    // it cannot cross without IP fragmentation, which almost always means an MTU mismatch.
    private int _maxInnerFrame = 1500;
    private int _tapMtu;
    private long _oversizedOutbound;
    private long _oversizedInbound;
    private long _oversizedDropped;
    private long _ipFragments;
    private long _lastOversizeWarnMs;

    private long _tapFramesRead;
    private long _tapFilterDropped;
    private long _tapMacSuppressed;
    private long _tapForwarded;
    private long _tunnelPacketsReceived;
    private long _tunnelInvalid;
    private long _tunnelFilterDropped;
    private long _tunnelMacSuppressed;
    private long _tunnelWritten;
    private long _broadcastDropped;
    private long _lastTapForwardMs;
    private long _lastTunnelReceiveMs;

    private readonly BroadcastLimiter _broadcastTap;
    private readonly BroadcastLimiter _broadcastTunnel;

    // One counter per configured filter value, generated from the effective policy, so the metrics
    // endpoint can name exactly which port/protocol/MAC is being dropped.
    private readonly long[] _linkLocalDrops;
    private readonly long[] _etherTypeDrops;
    private readonly long[] _ipProtocolDrops;
    private readonly long[] _udpPortDrops;
    private readonly long[] _tcpPortDrops;
    private readonly long[] _icmpv4Drops;
    private readonly long[] _icmpv6Drops;
    private long _vlanDropped;

    private int _firstTapFrameLogged;
    private int _firstTunnelPacketLogged;
    private int _tapMacReflectedLogged;
    private bool _tunnelUnavailableLogged;
    private bool _firstSession = true;
    private int _lastAppliedMtu = -1;

    // Server (Lan) mode: clients are learned from inbound tunnel packets.
    private readonly bool _serverMode;
    private readonly bool _allowClientToClient;
    private readonly int _maxClients;
    private readonly long _clientAgingMs;
    private readonly long _remoteMacAgingMs;
    private readonly int _maxRemoteMacs;
    private readonly HashSet<IPAddress>? _allowedClientPeers;
    private readonly object _clientLock = new();
    private readonly Dictionary<IPEndPoint, long> _clients = new();
    private readonly Dictionary<IPAddress, IPEndPoint> _clientEndpointByAddress = new();
    private readonly Dictionary<ulong, RemoteMac> _remoteMacs = new();
    private readonly UnknownMacFloodLimiter _unknownMacFlood;
    private long _lastClientPruneMs;
    private long _clientForwarded;
    private long _clientDropped;
    private long _clientRejected;
    private long _flooded;
    private long _unknownMacFlooded;
    private long _unknownMacDropped;
    private long _remoteMacOwnerGone;
    private long _remoteMacsEvicted;

    /// <summary>
    /// A MAC learned from a client, plus the endpoint it was last seen on. The binding is kept after
    /// the owning client goes idle so one-way traffic to it is not fanned out to every client.
    /// </summary>
    private struct RemoteMac
    {
        public IPEndPoint Endpoint;
        public long LastSeenMs;
    }

    /// <summary>Outcome of resolving a destination MAC to an owning client.</summary>
    private enum RemoteMacState : byte
    {
        /// <summary>Never learned from any client.</summary>
        Unknown,

        /// <summary>Owned by a client that is currently connected.</summary>
        Connected,

        /// <summary>Owner is remembered but that client is not connected right now.</summary>
        OwnerDisconnected,
    }

    private CancellationTokenSource? _sessionCts;
    private volatile bool _stopRequested;

    /// <summary>Builds the bridge from fully-resolved runtime values. The tunnel interface and the
    /// peer are re-resolved on every session so the bridge survives the adapter being
    /// destroyed/recreated or the peer address changing.</summary>
    public Bridge(
        BridgeConfig config,
        IPAddress? peerAddress,
        IPeerProvider peerProvider,
        string tapDevicePath,
        string tapAdapterId)
    {
        _config = config;
        _peerAddress = peerAddress;
        _peerProvider = peerProvider;
        _serverMode = config.BridgeSide == BridgeSide.Lan;
        _allowClientToClient = config.AllowClientToClient;
        _maxClients = config.MaxClients;
        _clientAgingMs = config.ClientAgingSeconds * 1000L;
        _remoteMacAgingMs = config.RemoteMacAgingSeconds * 1000L;
        _maxRemoteMacs = config.MaxRemoteMacs;
        if (config.AllowedClientPeers.Length > 0)
        {
            _allowedClientPeers = new HashSet<IPAddress>(
                config.AllowedClientPeers.Select(IPAddress.Parse));
        }

        _tap = new TapAdapter(tapDevicePath);
        _networkConfigurator = new TapNetworkConfigurator(config, tapAdapterId);
        _transport = TransportFactory.Create(config);
        _filterPolicy = FilterPolicy.Resolve(config);
        _filter = new FrameFilter(_filterPolicy);
        _linkLocalDrops = new long[_filterPolicy.LinkLocalMacValues.Length];
        _etherTypeDrops = new long[_filterPolicy.EtherTypeValues.Length];
        _ipProtocolDrops = new long[_filterPolicy.IpProtocolValues.Length];
        _udpPortDrops = new long[_filterPolicy.UdpPortValues.Length];
        _tcpPortDrops = new long[_filterPolicy.TcpPortValues.Length];
        _icmpv4Drops = new long[_filterPolicy.Icmpv4Values.Length];
        _icmpv6Drops = new long[_filterPolicy.Icmpv6Values.Length];
        _macTable = new MacTable(2048, TimeSpan.FromSeconds(config.MacAgingSeconds));
        _loopDetector = new LoopDetector();
        _broadcastTap = new BroadcastLimiter(config.MaxBroadcastPps);
        _broadcastTunnel = new BroadcastLimiter(config.MaxBroadcastPps);
        _unknownMacFlood = new UnknownMacFloodLimiter(config.MaxUnknownMacFloodPps);
        _tunnel = new WireGuardInterface { Name = config.TunnelInterfaceName };
    }

    /// <summary>Runs the bridge until cancellation, reconnecting across recoverable failures.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string? lastFailure = null;
        int reconnectAttempt = 0;

        while (!cancellationToken.IsCancellationRequested && !_stopRequested)
        {
            Task[]? pumps = null;
            bool recovered = reconnectAttempt > 0;
            bool tunnelUnavailable = false;

            try
            {
                OpenSession();

                if (_tunnelUnavailableLogged)
                {
                    _tunnelUnavailableLogged = false;
                    BridgeLog.Info($"Tunnel interface '{_config.TunnelInterfaceName}' is available again.");
                }

                LogSessionInit("Bridge running.");
                LogSessionInit(_filterPolicy.Description);
                _firstSession = false;
                if (recovered)
                {
                    BridgeLog.Info($"Session recovered after {reconnectAttempt} failure(s).");
                }

                reconnectAttempt = 0;
                _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                pumps =
                [
                    PumpTapToTunnel(_sessionCts.Token),
                    PumpTunnelToTap(_sessionCts.Token),
                    PumpProbes(_sessionCts.Token),
                    PumpStats(_sessionCts.Token),
                    PumpInterfaceWatch(_sessionCts.Token),
                    PumpHealthCheck(_sessionCts.Token),
                ];

                // Request/renew DHCP only after the forwarding pumps are up; otherwise the
                // DHCPDISCOVER has no path to the remote server and ipconfig /renew blocks through
                // its discovery timeout. Do this on every client session, not only after a
                // recovery: the Windows DHCP client may have fallen back to APIPA (169.254.x.x)
                // before the tunnel was ready, and needs a nudge once forwarding works.
                // Only the client side has an address on the TAP; the LAN side is a bridge port.
                if (_config.BridgeSide == BridgeSide.Client && _config.RenewDhcpOnReconnect &&
                    string.IsNullOrWhiteSpace(_config.TapIpAddress))
                {
                    _ = RenewDhcpAfterForwardingStartedAsync(_sessionCts.Token);
                }

                await AwaitFirstFaultAsync(pumps).ConfigureAwait(false);
            }
            catch (TunnelUnavailableException ex)
            {
                // NetBird may be reconnecting after a network change. This is not a session
                // failure: wait briefly and retry as soon as the interface is back.
                tunnelUnavailable = true;
                if (!_tunnelUnavailableLogged)
                {
                    _tunnelUnavailableLogged = true;
                    BridgeLog.Info(
                        $"{ex.Message} Waiting for it to return (if NetBird is reconnecting after a network change, this is expected).");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _stopRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                reconnectAttempt++;
                LogSessionFailure(ex, ref lastFailure, reconnectAttempt);
            }
            finally
            {
                _sessionCts?.Cancel();

                // Drain every pump before touching the TAP handle; disposing a handle with overlapped
                // I/O still in flight is undefined behavior and was the source of the shutdown hang.
                if (pumps is not null)
                {
                    try
                    {
                        await Task.WhenAll(pumps).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Pumps fault or cancel during teardown — expected.
                    }
                }

                CloseSession();
                _sessionCts?.Dispose();
                _sessionCts = null;
            }

            if (!cancellationToken.IsCancellationRequested && !_stopRequested)
            {
                // While the tunnel interface is missing, poll quickly so the session resumes as
                // soon as NetBird finishes reconnecting; otherwise use the configured backoff.
                TimeSpan delay = tunnelUnavailable
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromSeconds(_config.ReconnectDelaySeconds);

                if (!tunnelUnavailable)
                {
                    BridgeLog.Debug($"Reconnecting in {_config.ReconnectDelaySeconds}s (attempt {reconnectAttempt}).");
                }

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        BridgeLog.Info("Bridge stopped.");
    }

    /// <summary>
    /// Awaits the pumps until the first one faults or is canceled, then rethrows that exception so the
    /// session tears down. Pumps that complete normally (e.g. disabled via config) are ignored.
    /// Task.WhenAll is NOT used here: it waits for every pump, but the forwarding pumps are infinite
    /// loops, so a single failing watcher would never be observed.
    /// </summary>
    private static async Task AwaitFirstFaultAsync(Task[] pumps)
    {
        var pending = new List<Task>(pumps);
        while (pending.Count > 0)
        {
            Task done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);

            if (done.IsFaulted || done.IsCanceled)
            {
                await done.ConfigureAwait(false); // throws the pump's exception
            }
        }
    }

    private async Task RenewDhcpAfterForwardingStartedAsync(CancellationToken ct)
    {
        try
        {
            // Give the pumps a moment to establish the tunnel before broadcasting DHCPDISCOVER.
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

            if (!TapNeedsDhcpLease())
            {
                BridgeLog.Debug($"TAP adapter '{_config.TapName}' already has a usable IPv4 address; skipping DHCP renew.");
                return;
            }

            _networkConfigurator.RenewDhcp();
        }
        catch (OperationCanceledException)
        {
            // Session ended before the renew; the next recovery will renew again.
        }
        catch (Exception ex)
        {
            BridgeLog.Warning($"DHCP renew failed: {ex.Message}");
        }
    }

    /// <summary>
    /// True when the TAP has no IPv4 address, or only an APIPA (169.254.x.x) address — i.e. when a
    /// DHCP renew is actually needed. Avoids a network configuration change on every reconnect.
    /// </summary>
    private bool TapNeedsDhcpLease()
    {
        NetworkInterface? nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Name, _config.TapName, StringComparison.OrdinalIgnoreCase));
        if (nic is null)
        {
            return false; // adapter gone; the session loop will retry
        }

        IPAddress[] ipv4 = nic.GetIPProperties().UnicastAddresses
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(u => u.Address)
            .ToArray();

        return ipv4.Length == 0 || ipv4.All(IsLinkLocal);
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length >= 2 && bytes[0] == 169 && bytes[1] == 254;
    }

    private void OpenSession()
    {
        _tunnel.Resolve(_config);

        // Client mode requires a resolved peer. Lan/server mode learns clients from inbound
        // traffic, so a peer is optional (it is only used as a seed client).
        if (!_serverMode)
        {
            // Resolved per session so an overlay that is not up yet is retried rather than fatal.
            RefreshPeer();
        }

        IPAddress? peerAddress = _peerAddress;
        if (!_serverMode && peerAddress is null)
        {
            throw new IOException("Peer is not resolved yet; waiting for the overlay to become available.");
        }

        IPAddress localIp = _tunnel.LocalAddress!;
        int tunnelMtu = _tunnel.Mtu;

        // Size the receive buffer for the worst case a peer can send (jumbo frame) so an oversized
        // datagram is received intact and can be diagnosed instead of being silently truncated.
        _bufferSize = Math.Max(2048, Math.Max(tunnelMtu, 9000) + 128 + _transport.HeaderLength);

        bool openedNow = _tap.Open();
        if (openedNow)
        {
            BridgeLog.Info($"TAP adapter '{_config.TapName}' opened ({_tap.DevicePath}), media state forced to connected.");
        }
        else
        {
            BridgeLog.Debug($"TAP adapter '{_config.TapName}' already open; reusing handle.");
        }

        BridgeLog.Debug($"TAP MAC '{FormatMac(_tap.MacAddress)}'.");

        LogTapOperationalStatus();

        int tapMtu = _networkConfigurator.DeriveTapMtu(tunnelMtu, _transport.EncapsulationOverhead);
        int innerHeader = _config.AssumeVlanTagged ? 18 : 14;
        _tapMtu = tapMtu;
        _maxInnerFrame = Math.Max(576, tunnelMtu - _transport.EncapsulationOverhead);

        // The client side owns the TAP's IP stack, so its MTU is applied here. In Lan mode the TAP
        // is a network-bridge port with no IPv4 subinterface of its own: the bridge/NIC owns the
        // MTU, and netsh would fail with "Element not found".
        if (_config.BridgeSide == BridgeSide.Client)
        {
            // Avoid re-running netsh when the MTU is already set: a config change can make
            // NetBird bounce the tunnel again. The in-memory cache avoids even querying per
            // session; ApplyMtuIfNeeded also covers process restarts.
            if (tapMtu != _lastAppliedMtu && _networkConfigurator.ApplyMtuIfNeeded(tapMtu))
            {
                _lastAppliedMtu = tapMtu;
            }
        }
        else
        {
            BridgeLog.Debug("Bridge side is 'Lan'; not applying the TAP MTU (the network bridge owns the MTU).");
        }

        LogSessionInit($"TAP MTU {tapMtu} (tunnel MTU {tunnelMtu} - {_transport.EncapsulationOverhead} encapsulation - {innerHeader} Ethernet header).");
        LogSessionInit(
            $"Largest frame that fits the tunnel: {_maxInnerFrame} bytes; frames above it are " +
            (_config.AllowFragmentation ? "IP-fragmented." : "dropped (AllowFragmentation is false)."));

        if (_serverMode && peerAddress is not null)
        {
            SeedClient(peerAddress);
        }

        _transport.Open(localIp, _serverMode ? null : peerAddress);
        LogSessionInit($"Encapsulation active: {_transport.Description}; egress pinned to tunnel address '{localIp}'.");
    }

    private void RefreshPeer()
    {
        PeerInfo? fresh = _peerProvider.Resolve();
        if (fresh is null)
        {
            if (_peerAddress is null)
            {
                string target = !string.IsNullOrWhiteSpace(_config.PeerName)
                    ? $"peer '{_config.PeerName}'"
                    : !string.IsNullOrWhiteSpace(_config.PeerAddress)
                        ? $"peer address '{_config.PeerAddress}'"
                        : "peer (no 'PeerName' or 'PeerAddress' configured)";

                throw new IOException(
                    $"Configured {target} could not be resolved; waiting (no fallback to another peer).");
            }

            BridgeLog.Debug($"Peer re-resolution failed; keeping cached peer '{_peerAddress}'.");
            return;
        }

        if (!fresh.Address.Equals(_peerAddress))
        {
            BridgeLog.Info($"Peer address changed: '{_peerAddress?.ToString() ?? "(none)"}' -> '{fresh.Address}'.");
            _peerAddress = fresh.Address;
        }

        if (!fresh.Connected)
        {
            throw new IOException($"{_peerProvider.Name} peer is not connected yet; waiting before starting the session.");
        }
    }

    private void CloseSession()
    {
        _transport.Close();

        // The TAP handle is intentionally kept open across sessions: disposing/reopening it toggles
        // the adapter's media state, which is a Windows network change that can make NetBird bounce
        // wt0 in a loop. It is disposed once in DisposeAsync.
    }

    private void LogTapOperationalStatus()
    {
        NetworkInterface? nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Name, _config.TapName, StringComparison.OrdinalIgnoreCase));

        if (nic is null)
        {
            BridgeLog.Warning($"TAP adapter '{_config.TapName}' no longer enumerates in the network stack.");
            return;
        }

        var ipv4 = nic.GetIPProperties().UnicastAddresses
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(u => u.Address)
            .ToArray();

        if (nic.OperationalStatus != OperationalStatus.Up)
        {
            BridgeLog.Warning(
                $"TAP adapter '{_config.TapName}' reports '{nic.OperationalStatus}' (not Up) — the host stack will not transmit into it. " +
                $"IPv4: {(ipv4.Length == 0 ? "none" : string.Join(", ", ipv4.Select(a => a.ToString())))}.");
        }
        else if (_firstSession)
        {
            BridgeLog.Info(
                $"TAP adapter '{_config.TapName}' operational status '{nic.OperationalStatus}'. " +
                $"IPv4: {(ipv4.Length == 0 ? "none" : string.Join(", ", ipv4.Select(a => a.ToString())))}.");
        }
        else
        {
            BridgeLog.Debug(
                $"TAP adapter '{_config.TapName}' operational status '{nic.OperationalStatus}'. " +
                $"IPv4: {(ipv4.Length == 0 ? "none" : string.Join(", ", ipv4.Select(a => a.ToString())))}.");
        }
    }

    /// <summary>Logs session setup detail at Info for the first session, then Debug for reconnects.</summary>
    private void LogSessionInit(string message)
    {
        if (_firstSession)
        {
            BridgeLog.Info(message);
        }
        else
        {
            BridgeLog.Debug(message);
        }
    }

    private async Task PumpTapToTunnel(CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        try
        {
            int headroom = _transport.HeaderLength;
            _transport.WriteHeader(buffer.AsSpan(0, headroom));

            while (!ct.IsCancellationRequested)
            {
                int frameLength = await _tap.ReadAsync(buffer, headroom, _bufferSize - headroom, ct).ConfigureAwait(false);
                if (frameLength <= 0)
                {
                    continue;
                }

                Interlocked.Increment(ref _tapFramesRead);
                if (Interlocked.Exchange(ref _firstTapFrameLogged, 1) == 0)
                {
                    string etherType = frameLength >= Ethernet.HeaderLength
                        ? $"0x{Ethernet.ReadEtherType(buffer.AsSpan(headroom, frameLength), out _):X4}"
                        : "truncated";
                    BridgeLog.Info($"TAP→tunnel: first frame read ({frameLength} bytes, EtherType {etherType}).");
                }

                ReadOnlySpan<byte> frame = buffer.AsSpan(headroom, frameLength);
                if (!_filter.Allow(frame, out FilterDrop tapDrop))
                {
                    Interlocked.Increment(ref _tapFilterDropped);
                    RecordFilterDrop(tapDrop);
                    continue;
                }

                if (frameLength > _maxInnerFrame)
                {
                    Interlocked.Increment(ref _oversizedOutbound);
                    WarnOversized(frameLength, outbound: true);

                    if (!_config.AllowFragmentation)
                    {
                        // Dropping is the only alternative to fragmentation: the send would fail with
                        // WSAEMSGSIZE and tear the session down.
                        Interlocked.Increment(ref _oversizedDropped);
                        continue;
                    }
                }

                long nowMs = Environment.TickCount64;
                if (frameLength >= Ethernet.HeaderLength)
                {
                    _macTable.Learn(frame[6..12], MacTable.Port.Tap, nowMs);
                    if (_loopDetector.Observe(frame))
                    {
                        OnLoopDetected();
                    }

                    if (_serverMode)
                    {
                        // Multi-client forwarding: known local unicast stays on the LAN, known
                        // remote unicast goes to its owning client, everything else floods.
                        if (Ethernet.IsMulticast(frame))
                        {
                            if (!_broadcastTap.Allow(nowMs))
                            {
                                Interlocked.Increment(ref _broadcastDropped);
                                continue;
                            }

                            await FloodToClientsAsync(buffer, headroom, frameLength, except: null, ct).ConfigureAwait(false);
                            Interlocked.Increment(ref _tapForwarded);
                            Interlocked.Exchange(ref _lastTapForwardMs, nowMs);
                            continue;
                        }

                        if (_macTable.Resolve(frame[..6], nowMs) == MacTable.Port.Tap)
                        {
                            // Destination is local to the LAN; the Windows bridge handles it.
                            Interlocked.Increment(ref _tapMacSuppressed);
                            continue;
                        }

                        ulong destinationMac = Ethernet.ReadMac(frame[..6]);
                        RemoteMacState state = ResolveRemote(destinationMac, nowMs, out IPEndPoint? client);
                        if (state == RemoteMacState.Connected && client is not null)
                        {
                            // Known unicast whose owner is connected: exactly one copy.
                            await _transport.SendToAsync(buffer, headroom, frameLength, client, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            // Unknown MAC, or a MAC whose remembered owner is offline (it may have
                            // moved to another client). Flooding is the only option, but it costs one
                            // copy per client, so each destination MAC gets a bounded budget.
                            if (state == RemoteMacState.OwnerDisconnected)
                            {
                                Interlocked.Increment(ref _remoteMacOwnerGone);
                            }

                            if (!_unknownMacFlood.Allow(destinationMac, nowMs, out bool firstClip))
                            {
                                Interlocked.Increment(ref _unknownMacDropped);
                                if (firstClip)
                                {
                                    BridgeLog.Warning(
                                        $"Unknown-unicast flood budget exceeded for {FormatMac(frame[..6])} " +
                                        $"({_unknownMacFlood.MaxPerSecond}/s); dropping further copies " +
                                        $"(owner {(state == RemoteMacState.OwnerDisconnected ? "disconnected" : "never seen")}).");
                                }

                                continue;
                            }

                            await FloodToClientsAsync(buffer, headroom, frameLength, except: null, ct).ConfigureAwait(false);
                            Interlocked.Increment(ref _unknownMacFlooded);
                        }

                        Interlocked.Increment(ref _tapForwarded);
                        Interlocked.Exchange(ref _lastTapForwardMs, nowMs);
                        continue;
                    }

                    if (!_macTable.ShouldForward(frame[..6], MacTable.Port.Tap, nowMs))
                    {
                        Interlocked.Increment(ref _tapMacSuppressed);
                        continue;
                    }
                }

                if (frameLength >= Ethernet.HeaderLength && Ethernet.IsMulticast(frame) && !_broadcastTap.Allow(nowMs))
                {
                    Interlocked.Increment(ref _broadcastDropped);
                    continue;
                }

                await _transport.SendAsync(buffer, headroom, frameLength, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _tapForwarded);
                Interlocked.Exchange(ref _lastTapForwardMs, Environment.TickCount64);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task PumpTunnelToTap(CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int packetLength = await _transport.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (packetLength <= 0)
                {
                    continue;
                }

                Interlocked.Increment(ref _tunnelPacketsReceived);
                Interlocked.Exchange(ref _lastTunnelReceiveMs, Environment.TickCount64);
                if (Interlocked.Exchange(ref _firstTunnelPacketLogged, 1) == 0)
                {
                    BridgeLog.Info($"tunnel→TAP: first packet received ({packetLength} bytes).");
                }

                int frameOffset = _transport.ValidateIncoming(buffer.AsSpan(0, packetLength));
                if (frameOffset < 0)
                {
                    Interlocked.Increment(ref _tunnelInvalid);
                    continue;
                }

                ReadOnlySpan<byte> frame = buffer.AsSpan(frameOffset, packetLength - frameOffset);
                if (!_filter.Allow(frame, out FilterDrop tunnelDrop))
                {
                    Interlocked.Increment(ref _tunnelFilterDropped);
                    RecordFilterDrop(tunnelDrop);
                    continue;
                }

                int innerLength = packetLength - frameOffset;
                if (innerLength > _maxInnerFrame)
                {
                    // Forward it anyway: it may be perfectly deliverable on the LAN side. This is a
                    // signal that the peer's MTU is too high, not a reason to drop.
                    Interlocked.Increment(ref _oversizedInbound);
                    WarnOversized(innerLength, outbound: false);
                }

                if (IsIpFragment(frame))
                {
                    Interlocked.Increment(ref _ipFragments);
                }

                long nowMs = Environment.TickCount64;

                if (_serverMode)
                {
                    IPEndPoint? source = _transport.LastReceiveSource;
                    if (source is null || !IsClientAllowed(source.Address))
                    {
                        Interlocked.Increment(ref _clientRejected);
                        continue;
                    }

                    TouchClient(source, nowMs);

                    if (frame.Length >= Ethernet.HeaderLength)
                    {
                        ulong srcMac = Ethernet.ReadMac(frame[6..12]);
                        if (!frame[6..12].SequenceEqual(_tap.MacAddress))
                        {
                            _macTable.Learn(frame[6..12], MacTable.Port.Tunnel, nowMs);
                            if (!Ethernet.IsMulticast(srcMac))
                            {
                                LearnRemoteMac(srcMac, source, nowMs);
                            }
                        }
                        else if (Interlocked.Exchange(ref _tapMacReflectedLogged, 1) == 0)
                        {
                            BridgeLog.Warning(
                                "Own TAP MAC observed arriving from the tunnel (reflection/loop or duplicate MAC on a client); not learning it as remote.");
                        }

                        if (_loopDetector.Observe(frame))
                        {
                            OnLoopDetected();
                        }

                        if (Ethernet.IsMulticast(frame))
                        {
                            if (!_broadcastTunnel.Allow(nowMs))
                            {
                                Interlocked.Increment(ref _broadcastDropped);
                                continue;
                            }

                            // The LAN always sees the broadcast; other clients see it only when
                            // client-to-client forwarding is enabled.
                            await _tap.WriteAsync(buffer, frameOffset, packetLength - frameOffset, ct).ConfigureAwait(false);
                            Interlocked.Increment(ref _tunnelWritten);

                            if (_allowClientToClient)
                            {
                                await FloodToClientsAsync(buffer, frameOffset, packetLength - frameOffset, except: source, ct).ConfigureAwait(false);
                            }

                            continue;
                        }

                        ulong destMac = Ethernet.ReadMac(frame[..6]);
                        if (_macTable.Resolve(frame[..6], nowMs) == MacTable.Port.Tap)
                        {
                            // Destination is on the LAN (or the server itself).
                            await _tap.WriteAsync(buffer, frameOffset, packetLength - frameOffset, ct).ConfigureAwait(false);
                            Interlocked.Increment(ref _tunnelWritten);
                            continue;
                        }

                        RemoteMacState destState = ResolveRemote(destMac, nowMs, out IPEndPoint? destClient);
                        if (destState == RemoteMacState.Connected && destClient is not null && !destClient.Equals(source))
                        {
                            if (_allowClientToClient)
                            {
                                EnsureClientHeader(buffer, frameOffset);
                                await _transport.SendToAsync(buffer, frameOffset, packetLength - frameOffset, destClient, ct).ConfigureAwait(false);
                                Interlocked.Increment(ref _clientForwarded);
                            }
                            else
                            {
                                Interlocked.Increment(ref _clientDropped);
                            }

                            continue;
                        }

                        // Unknown destination (or a remembered owner that is offline): let the LAN see
                        // it — it may answer — and flood the remaining clients so a newly seen MAC can
                        // be reached. The client fan-out is budgeted per destination MAC; the single
                        // copy written to the LAN is not.
                        await _tap.WriteAsync(buffer, frameOffset, packetLength - frameOffset, ct).ConfigureAwait(false);
                        Interlocked.Increment(ref _tunnelWritten);

                        if (_allowClientToClient)
                        {
                            if (_unknownMacFlood.Allow(destMac, nowMs))
                            {
                                await FloodToClientsAsync(buffer, frameOffset, packetLength - frameOffset, except: source, ct).ConfigureAwait(false);
                                Interlocked.Increment(ref _unknownMacFlooded);
                            }
                            else
                            {
                                Interlocked.Increment(ref _unknownMacDropped);
                            }
                        }

                        continue;
                    }

                    // Truncated frame (filter fails open): write it to the LAN.
                    await _tap.WriteAsync(buffer, frameOffset, packetLength - frameOffset, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _tunnelWritten);
                    continue;
                }

                if (frame.Length >= Ethernet.HeaderLength)
                {
                    if (!frame[6..12].SequenceEqual(_tap.MacAddress))
                    {
                        _macTable.Learn(frame[6..12], MacTable.Port.Tunnel, nowMs);
                    }
                    else if (Interlocked.Exchange(ref _tapMacReflectedLogged, 1) == 0)
                    {
                        BridgeLog.Warning(
                            "Own TAP MAC observed arriving from the tunnel (reflection/loop or duplicate MAC on the peer); not learning it as remote.");
                    }

                    if (_loopDetector.Observe(frame))
                    {
                        OnLoopDetected();
                    }

                    if (!_macTable.ShouldForward(frame[..6], MacTable.Port.Tunnel, nowMs))
                    {
                        Interlocked.Increment(ref _tunnelMacSuppressed);
                        continue;
                    }
                }

                if (frame.Length >= Ethernet.HeaderLength && Ethernet.IsMulticast(frame) && !_broadcastTunnel.Allow(nowMs))
                {
                    Interlocked.Increment(ref _broadcastDropped);
                    continue;
                }

                await _tap.WriteAsync(buffer, frameOffset, packetLength - frameOffset, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _tunnelWritten);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task PumpStats(CancellationToken ct)
    {
        int intervalSeconds = _config.StatsIntervalSeconds;
        if (intervalSeconds <= 0)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(intervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);

            BridgeLog.Debug(
                $"Stats: tap→tunnel read={Interlocked.Read(ref _tapFramesRead)} forwarded={Interlocked.Read(ref _tapForwarded)} " +
                $"filter-drop={Interlocked.Read(ref _tapFilterDropped)} mac-suppress={Interlocked.Read(ref _tapMacSuppressed)}; " +
                $"tunnel→tap received={Interlocked.Read(ref _tunnelPacketsReceived)} written={Interlocked.Read(ref _tunnelWritten)} " +
                $"invalid={Interlocked.Read(ref _tunnelInvalid)} filter-drop={Interlocked.Read(ref _tunnelFilterDropped)} " +
                $"mac-suppress={Interlocked.Read(ref _tunnelMacSuppressed)}; " +
                $"storm-drop={Interlocked.Read(ref _broadcastDropped)} non-peer-drop={_transport.DroppedNonPeer}" +
                $" oversized-out={Interlocked.Read(ref _oversizedOutbound)} oversized-in={Interlocked.Read(ref _oversizedInbound)} " +
                $"oversized-dropped={Interlocked.Read(ref _oversizedDropped)} fragments={Interlocked.Read(ref _ipFragments)}" +
                (_serverMode
                    ? $" clients={SnapshotClients().Length} client-forward={Interlocked.Read(ref _clientForwarded)} " +
                      $"client-drop={Interlocked.Read(ref _clientDropped)} rejected={Interlocked.Read(ref _clientRejected)} " +
                      $"flooded={Interlocked.Read(ref _flooded)} unknown-flood={Interlocked.Read(ref _unknownMacFlooded)} " +
                      $"unknown-drop={Interlocked.Read(ref _unknownMacDropped)} mac-evicted={Interlocked.Read(ref _remoteMacsEvicted)}."
                    : "."));

            if (_serverMode)
            {
                foreach (string clientLine in BuildClientReport().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    BridgeLog.Debug($"Server {clientLine}");
                }
            }
        }
    }

    /// <summary>
    /// Watches only that the tunnel adapter exists and is Up. This is the primary liveness signal and
    /// must stay free of any CLI calls so a hung netbird/netsh process can never stall it.
    /// </summary>
    private async Task PumpInterfaceWatch(CancellationToken ct)
    {
        int intervalSeconds = _config.TunnelHealthCheckSeconds;
        if (intervalSeconds <= 0)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(intervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);

            // The managed NetworkInterface enumeration caches adapter data inside a long-running
            // process, and a UDP send does not reliably fail when the adapter is gone. The reliable
            // fresh check: the tunnel's local IP becomes unassigned the moment wt0 is removed, so a
            // bind to it fails with WSAEADDRNOTAVAIL.
            IPAddress? localIp = _tunnel.LocalAddress;
            bool present = localIp is not null && IsLocalAddressPresent(localIp);

            BridgeLog.Trace($"InterfaceWatch: localIp='{localIp}' present={present}.");

            if (!present)
            {
                throw new TunnelUnavailableException(
                    $"Tunnel interface '{_config.TunnelInterfaceName}' is down or missing.");
            }
        }
    }

    /// <summary>True while <paramref name="address"/> is still assigned to a local interface.</summary>
    private static bool IsLocalAddressPresent(IPAddress address)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(address, 0));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Secondary liveness checks: NetBird's view of the peer plus a no-inbound-traffic heuristic.
    /// These run on a slower, independent cadence and can never block the interface watcher.
    /// </summary>
    private async Task PumpHealthCheck(CancellationToken ct)
    {
        int intervalSeconds = _config.TunnelHealthCheckSeconds;
        if (intervalSeconds <= 0)
        {
            return;
        }

        // Peer probing spawns the NetBird CLI, so run it at a fixed slower cadence than the interface watch.
        const int peerCheckSeconds = 30;
        long intervalMs = peerCheckSeconds * 1000L;
        var interval = TimeSpan.FromSeconds(peerCheckSeconds);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);

            if (!_serverMode)
            {
                PeerInfo? peer = await ResolvePeerBoundedAsync(ct).ConfigureAwait(false);

                BridgeLog.Trace(
                    $"HealthCheck: peer={(peer is null ? "null" : peer.Address.ToString())} connected={peer?.Connected}.");

                if (peer is { Connected: false })
                {
                    string peerLabel = string.IsNullOrWhiteSpace(_config.PeerName) ? peer.Address.ToString() : _config.PeerName;
                    throw new IOException($"NetBird reports peer '{peerLabel}' is not connected.");
                }
            }

            long nowMs = Environment.TickCount64;
            long lastSent = Interlocked.Read(ref _lastTapForwardMs);
            long lastReceived = Interlocked.Read(ref _lastTunnelReceiveMs);

            // In server mode there may legitimately be no clients (and therefore no inbound
            // traffic) while LAN broadcasts are still being forwarded, so this heuristic does
            // not apply.
            if (!_serverMode && lastSent > 0 && nowMs - lastSent <= intervalMs && nowMs - lastReceived > intervalMs)
            {
                throw new IOException($"Tunnel health check failed: no inbound traffic for {peerCheckSeconds}s while frames were being forwarded.");
            }
        }
    }

    /// <summary>
    /// Runs peer resolution off-thread with a hard timeout so a hung overlay CLI/API can never stall the
    /// health-check loop indefinitely.
    /// </summary>
    private async Task<PeerInfo?> ResolvePeerBoundedAsync(CancellationToken ct)
    {
        try
        {
            return await Task.Run(_peerProvider.Resolve, ct)
                .WaitAsync(TimeSpan.FromSeconds(10), ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            BridgeLog.Debug("Peer resolution timed out; skipping the peer liveness check this cycle.");
            return null;
        }
    }

    private async Task PumpProbes(CancellationToken ct)
    {
        if (!_config.EnableLoopDetection)
        {
            return;
        }

        int headerLength = _transport.HeaderLength;
        byte[] probe = ArrayPool<byte>.Shared.Rent(headerLength + LoopDetector.ProbeLength);
        try
        {
            _transport.WriteHeader(probe.AsSpan(0, headerLength));

            var interval = TimeSpan.FromSeconds(_config.LoopProbeIntervalSeconds);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);

                int length = _loopDetector.BuildProbe(
                    probe.AsSpan(headerLength, LoopDetector.ProbeLength), _tap.MacAddress);

                // Inject into the local segment and also send it over the tunnel. Without the
                // tunnel copy a loop formed through the tunnel could never be observed: the local
                // probe never returns on the tunnel path. If it loops, it comes back on either
                // path and LoopDetector.Observe matches this instance's random ID.
                await _tap.WriteAsync(probe, headerLength, length, ct).ConfigureAwait(false);

                if (_serverMode)
                {
                    await FloodToClientsAsync(probe, headerLength, length, except: null, ct).ConfigureAwait(false);
                }
                else
                {
                    await _transport.SendAsync(probe, headerLength, length, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probe);
        }
    }

    /// <summary>Returns a plain-text snapshot of the bridge counters for the metrics endpoint.</summary>
    public string GetMetricsSnapshot() =>
        $"peer {(_serverMode ? $"server ({SnapshotClients().Length} clients)" : (_peerAddress?.ToString() ?? "(unresolved)"))}\n" +
        $"transport {_transport.Mode}\n" +
        $"tap_read {Interlocked.Read(ref _tapFramesRead)}\n" +
        $"tap_forwarded {Interlocked.Read(ref _tapForwarded)}\n" +
        $"tap_filter_dropped {Interlocked.Read(ref _tapFilterDropped)}\n" +
        $"tap_mac_suppressed {Interlocked.Read(ref _tapMacSuppressed)}\n" +
        $"tap_broadcast_dropped {Interlocked.Read(ref _broadcastDropped)}\n" +
        $"tunnel_received {Interlocked.Read(ref _tunnelPacketsReceived)}\n" +
        $"tunnel_written {Interlocked.Read(ref _tunnelWritten)}\n" +
        $"tunnel_invalid {Interlocked.Read(ref _tunnelInvalid)}\n" +
        $"tunnel_filter_dropped {Interlocked.Read(ref _tunnelFilterDropped)}\n" +
        $"tunnel_mac_suppressed {Interlocked.Read(ref _tunnelMacSuppressed)}\n" +
        $"tunnel_non_peer_dropped {_transport.DroppedNonPeer}\n" +
        $"clients {(_serverMode ? SnapshotClients().Length : 0)}\n" +
        $"client_forwarded {Interlocked.Read(ref _clientForwarded)}\n" +
        $"client_dropped {Interlocked.Read(ref _clientDropped)}\n" +
        $"client_rejected {Interlocked.Read(ref _clientRejected)}\n" +
        $"flooded {Interlocked.Read(ref _flooded)}\n" +
        $"unknown_mac_flooded {Interlocked.Read(ref _unknownMacFlooded)}\n" +
        $"unknown_mac_dropped {Interlocked.Read(ref _unknownMacDropped)}\n" +
        $"remote_mac_owner_gone {Interlocked.Read(ref _remoteMacOwnerGone)}\n" +
        $"remote_macs_evicted {Interlocked.Read(ref _remoteMacsEvicted)}\n" +
        $"oversized_outbound {Interlocked.Read(ref _oversizedOutbound)}\n" +
        $"oversized_inbound {Interlocked.Read(ref _oversizedInbound)}\n" +
        $"oversized_dropped {Interlocked.Read(ref _oversizedDropped)}\n" +
        $"ip_fragments {Interlocked.Read(ref _ipFragments)}\n" +
        BuildFilterReport() +
        (_serverMode ? BuildClientReport() : string.Empty) +
        $"loop_detected {(_loopDetector.LoopDetected ? 1 : 0)}\n";

    /// <summary>Per-configured-value filter drops, omitted while zero.</summary>
    private string BuildFilterReport()
    {
        var lines = new List<string>();

        for (int i = 0; i < _linkLocalDrops.Length; i++)
        {
            long total = Interlocked.Read(ref _linkLocalDrops[i]);
            if (total > 0)
            {
                lines.Add($"filter_mac_{_filterPolicy.LinkLocalMacValues[i]:x12} {total}\n");
            }
        }

        for (int i = 0; i < _etherTypeDrops.Length; i++)
        {
            long total = Interlocked.Read(ref _etherTypeDrops[i]);
            if (total > 0)
            {
                lines.Add($"filter_ethertype_{_filterPolicy.EtherTypeValues[i]:x4} {total}\n");
            }
        }

        AppendFilterLines(lines, "filter_ip_", _filterPolicy.IpProtocolValues, _ipProtocolDrops);
        AppendFilterLines(lines, "filter_udp_", _filterPolicy.UdpPortValues, _udpPortDrops);
        AppendFilterLines(lines, "filter_tcp_", _filterPolicy.TcpPortValues, _tcpPortDrops);
        AppendFilterLines(lines, "filter_icmpv4_", _filterPolicy.Icmpv4Values, _icmpv4Drops);
        AppendFilterLines(lines, "filter_icmpv6_", _filterPolicy.Icmpv6Values, _icmpv6Drops);

        long vlanDropped = Interlocked.Read(ref _vlanDropped);
        if (vlanDropped > 0)
        {
            lines.Add($"filter_vlan {vlanDropped}\n");
        }

        return lines.Count == 0 ? string.Empty : string.Concat(lines);
    }

    private static void AppendFilterLines(List<string> lines, string prefix, int[] values, long[] counters)
    {
        for (int i = 0; i < counters.Length; i++)
        {
            long total = Interlocked.Read(ref counters[i]);
            if (total > 0)
            {
                lines.Add($"{prefix}{values[i]} {total}\n");
            }
        }
    }

    /// <summary>
    /// Charges a drop to the counter of the exact configured value that matched. Safe to scan: this
    /// only runs for frames that were actually dropped, and the arrays hold only configured entries.
    /// </summary>
    private void RecordFilterDrop(FilterDrop drop)
    {
        switch (drop.Reason)
        {
            case FilterDropReason.LinkLocal:
                IncrementCounter(_linkLocalDrops, Array.IndexOf(_filterPolicy.LinkLocalMacValues, drop.Value));
                break;

            case FilterDropReason.EtherType:
                IncrementCounter(_etherTypeDrops, Array.IndexOf(_filterPolicy.EtherTypeValues, (ushort)drop.Value));
                break;

            case FilterDropReason.IpProtocol:
                IncrementCounter(_ipProtocolDrops, Array.IndexOf(_filterPolicy.IpProtocolValues, (int)drop.Value));
                break;

            case FilterDropReason.UdpPort:
                IncrementCounter(_udpPortDrops, Array.IndexOf(_filterPolicy.UdpPortValues, (int)drop.Value));
                break;

            case FilterDropReason.TcpPort:
                IncrementCounter(_tcpPortDrops, Array.IndexOf(_filterPolicy.TcpPortValues, (int)drop.Value));
                break;

            case FilterDropReason.Icmpv4Type:
                IncrementCounter(_icmpv4Drops, Array.IndexOf(_filterPolicy.Icmpv4Values, (int)drop.Value));
                break;

            case FilterDropReason.Icmpv6Type:
                IncrementCounter(_icmpv6Drops, Array.IndexOf(_filterPolicy.Icmpv6Values, (int)drop.Value));
                break;

            case FilterDropReason.Vlan:
                Interlocked.Increment(ref _vlanDropped);
                break;
        }
    }

    private static void IncrementCounter(long[] counters, int index)
    {
        if (index >= 0 && index < counters.Length)
        {
            Interlocked.Increment(ref counters[index]);
        }
    }

    /// <summary>
    /// Logs at most one MTU warning per minute: an undersized tunnel would otherwise produce one line
    /// per full-size frame.
    /// </summary>
    private void WarnOversized(int frameLength, bool outbound)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastOversizeWarnMs);
        if (now - last < 60_000)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastOversizeWarnMs, now, last) != last)
        {
            return; // another pump already warned
        }

        if (outbound)
        {
            string action = _config.AllowFragmentation
                ? "Fragmented by the IP stack (allowFragmentation=true)."
                : "Dropped (allowFragmentation=false).";
            BridgeLog.Warning(
                $"TAP→tunnel: frame of {frameLength} bytes exceeds the tunnel payload limit of {_maxInnerFrame} bytes " +
                $"(tunnel MTU {_tunnel.Mtu} - {_transport.EncapsulationOverhead} encapsulation). {action} " +
                $"Lower the bridged segment's MTU to {_tapMtu} bytes (DHCP option 26 / RA MTU) to avoid this.");
        }
        else
        {
            BridgeLog.Warning(
                $"tunnel→TAP: received a {frameLength}-byte frame, above the tunnel payload limit of {_maxInnerFrame} bytes — " +
                $"the peer is sending frames larger than the tunnel carries. Set the peer's vxlan/bridge MTU to {_tapMtu} bytes. Forwarding it.");
        }
    }

    /// <summary>True when the frame is an IP fragment (IPv4 MF/offset, or an IPv6 fragment header).</summary>
    private static bool IsIpFragment(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < Ethernet.HeaderLength)
        {
            return false;
        }

        ushort etherType = Ethernet.ReadEtherType(frame, out int l3);

        if (etherType == Ethernet.Ipv4)
        {
            if (frame.Length < l3 + 8)
            {
                return false;
            }

            int fragmentField = (frame[l3 + 6] << 8) | frame[l3 + 7];
            return (fragmentField & 0x3FFF) != 0; // MF or non-zero offset
        }

        if (etherType != Ethernet.Ipv6 || frame.Length < l3 + 40)
        {
            return false;
        }

        int nextHeader = frame[l3 + 6];
        int offset = l3 + 40;

        for (int hop = 0; hop < 4; hop++)
        {
            if (nextHeader == 44)
            {
                return true; // fragment header
            }

            if (nextHeader is not (0 or 43 or 60) || offset + 2 > frame.Length)
            {
                return false;
            }

            nextHeader = frame[offset];
            offset += (frame[offset + 1] + 1) * 8;
            if (offset > frame.Length)
            {
                return false;
            }
        }

        return false;
    }

    private void OnLoopDetected()
    {
        BridgeLog.Error("Layer 2 loop detected: own 0x88B5 probe returned to the bridge.");
        if (_config.StopOnLoopDetected)
        {
            BridgeLog.Info("StopOnLoopDetected is enabled; shutting down to prevent a broadcast storm.");
            _stopRequested = true;
            _sessionCts?.Cancel();
        }
        else
        {
            BridgeLog.Warning("StopOnLoopDetected is disabled; continuing to forward frames.");
        }
    }

    private void LogSessionFailure(Exception ex, ref string? lastFailure, int attempt)
    {
        string message = ex.Message;
        if (message == lastFailure)
        {
            BridgeLog.Debug($"Session failure repeats: {message} (retry {attempt}).");
        }
        else
        {
            BridgeLog.Warning($"Session failed: {message} Reconnecting in {_config.ReconnectDelaySeconds}s.");
            lastFailure = message;
        }
    }

    private static string FormatMac(ReadOnlySpan<byte> mac) =>
        string.Join(':', mac[0].ToString("X2"), mac[1].ToString("X2"), mac[2].ToString("X2"),
            mac[3].ToString("X2"), mac[4].ToString("X2"), mac[5].ToString("X2"));

    // ---- Server (Lan) mode: learned clients and per-client MAC forwarding ----

    private void SeedClient(IPAddress address)
    {
        int port = _transport.Mode == TransportMode.Vxlan ? _config.VxlanDestinationPort : 0;
        TouchClient(new IPEndPoint(address, port), Environment.TickCount64);
        BridgeLog.Debug($"Server mode: seeded client '{address}'.");
    }

    private bool IsClientAllowed(IPAddress address) =>
        _allowedClientPeers is null || _allowedClientPeers.Contains(address);

    private void TouchClient(IPEndPoint endpoint, long nowMs)
    {
        lock (_clientLock)
        {
            _clients[endpoint] = nowMs;
            _clientEndpointByAddress[endpoint.Address] = endpoint;

            if (_clients.Count > _maxClients)
            {
                IPEndPoint oldest = _clients.OrderBy(kv => kv.Value).First().Key;
                _clients.Remove(oldest);
                ForgetClientAddressLocked(oldest);

                // Its MAC bindings are kept on purpose: they still point at the (now disconnected)
                // owner, so LAN traffic for those MACs is not multiplied across every client, and
                // they are re-learned the moment that client sends again.
                BridgeLog.Warning($"Client limit {_maxClients} reached; evicted '{oldest}'.");
            }

            if (nowMs - _lastClientPruneMs >= 30_000)
            {
                PruneClientsLocked(nowMs);
                _lastClientPruneMs = nowMs;
            }
        }
    }

    private void LearnRemoteMac(ulong mac, IPEndPoint endpoint, long nowMs)
    {
        lock (_clientLock)
        {
            if (_remoteMacs.TryGetValue(mac, out RemoteMac existing))
            {
                existing.Endpoint = endpoint;
                existing.LastSeenMs = nowMs;
                _remoteMacs[mac] = existing;
                return;
            }

            if (_remoteMacs.Count >= _maxRemoteMacs)
            {
                EvictOldestRemoteMacLocked();
            }

            _remoteMacs[mac] = new RemoteMac { Endpoint = endpoint, LastSeenMs = nowMs };
        }
    }

    /// <summary>Removes the least recently seen binding; caller holds <see cref="_clientLock"/>.</summary>
    private void EvictOldestRemoteMacLocked()
    {
        ulong oldestMac = 0;
        long oldestMs = long.MaxValue;

        foreach (KeyValuePair<ulong, RemoteMac> entry in _remoteMacs)
        {
            if (entry.Value.LastSeenMs < oldestMs)
            {
                oldestMs = entry.Value.LastSeenMs;
                oldestMac = entry.Key;
            }
        }

        _remoteMacs.Remove(oldestMac);
        Interlocked.Increment(ref _remoteMacsEvicted);
    }

    /// <summary>
    /// Resolves a destination MAC to the client that owns it. The binding survives the owner going
    /// idle (<see cref="_remoteMacAgingMs"/>), so one-way traffic to a quiet host is sent to that one
    /// client rather than copied to every client. Only a live endpoint is returned for sending.
    /// </summary>
    private RemoteMacState ResolveRemote(ulong mac, long nowMs, out IPEndPoint? endpoint)
    {
        lock (_clientLock)
        {
            endpoint = null;
            if (!_remoteMacs.TryGetValue(mac, out RemoteMac entry))
            {
                return RemoteMacState.Unknown;
            }

            if (nowMs - entry.LastSeenMs > _remoteMacAgingMs)
            {
                _remoteMacs.Remove(mac);
                return RemoteMacState.Unknown;
            }

            // Resolve through the address so a client whose UDP source port changed is still matched.
            if (_clientEndpointByAddress.TryGetValue(entry.Endpoint.Address, out IPEndPoint? live))
            {
                endpoint = live;
                return RemoteMacState.Connected;
            }

            endpoint = entry.Endpoint;
            return RemoteMacState.OwnerDisconnected;
        }
    }

    private IPEndPoint[] SnapshotClients()
    {
        lock (_clientLock)
        {
            PruneClientsLocked(Environment.TickCount64);
            return _clients.Keys.ToArray();
        }
    }

    /// <summary>Per-client report (endpoint, idle seconds, learned MAC count) for metrics/debugging.</summary>
    private string BuildClientReport()
    {
        long nowMs = Environment.TickCount64;
        var lines = new List<string>();

        lock (_clientLock)
        {
            PruneClientsLocked(nowMs);

            foreach (KeyValuePair<IPEndPoint, long> client in _clients.OrderBy(kv => kv.Key.ToString()))
            {
                int macs = _remoteMacs.Count(kv => kv.Value.Endpoint.Address.Equals(client.Key.Address));
                long idleSeconds = Math.Max(0, (nowMs - client.Value) / 1000);
                lines.Add($"client {client.Key} idle_s {idleSeconds} macs {macs}");
            }
        }

        return lines.Count == 0 ? "client none\n" : string.Join('\n', lines) + "\n";
    }

    private void PruneClientsLocked(long nowMs)
    {
        List<IPEndPoint> expired = _clients
            .Where(kv => nowMs - kv.Value > _clientAgingMs)
            .Select(kv => kv.Key)
            .ToList();

        foreach (IPEndPoint endpoint in expired)
        {
            _clients.Remove(endpoint);
            ForgetClientAddressLocked(endpoint);
        }

        PruneRemoteMacsLocked(nowMs);
    }

    /// <summary>Drops the address→endpoint mapping of a client that is no longer tracked.</summary>
    private void ForgetClientAddressLocked(IPEndPoint endpoint)
    {
        if (_clientEndpointByAddress.TryGetValue(endpoint.Address, out IPEndPoint? current) &&
            current.Equals(endpoint))
        {
            _clientEndpointByAddress.Remove(endpoint.Address);
        }
    }

    /// <summary>
    /// Expires idle MAC bindings and enforces <see cref="_maxRemoteMacs"/>. Expired bindings are
    /// forgotten only after <c>remoteMacAgingSeconds</c>, not when their client disconnects.
    /// </summary>
    private void PruneRemoteMacsLocked(long nowMs)
    {
        if (_remoteMacs.Count == 0)
        {
            return;
        }

        List<ulong>? expired = null;
        foreach (KeyValuePair<ulong, RemoteMac> entry in _remoteMacs)
        {
            if (nowMs - entry.Value.LastSeenMs > _remoteMacAgingMs)
            {
                expired ??= new List<ulong>();
                expired.Add(entry.Key);
            }
        }

        if (expired is not null)
        {
            foreach (ulong mac in expired)
            {
                _remoteMacs.Remove(mac);
            }
        }

        while (_remoteMacs.Count > _maxRemoteMacs)
        {
            EvictOldestRemoteMacLocked();
        }
    }

    private async Task FloodToClientsAsync(byte[] buffer, int frameOffset, int frameLength, IPEndPoint? except, CancellationToken ct)
    {
        EnsureClientHeader(buffer, frameOffset);

        foreach (IPEndPoint client in SnapshotClients())
        {
            if (except is not null && client.Equals(except))
            {
                continue;
            }

            await _transport.SendToAsync(buffer, frameOffset, frameLength, client, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _flooded);
        }
    }

    private void EnsureClientHeader(byte[] buffer, int frameOffset)
    {
        int headerLength = _transport.HeaderLength;
        if (headerLength > 0)
        {
            _transport.WriteHeader(buffer.AsSpan(frameOffset - headerLength, headerLength));
        }
    }

    public ValueTask DisposeAsync()
    {
        _stopRequested = true;
        _sessionCts?.Cancel();
        CloseSession();
        _transport.Dispose();
        _tap.Dispose();
        return ValueTask.CompletedTask;
    }
}
