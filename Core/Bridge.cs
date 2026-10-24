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
    private readonly MacTable _macTable;
    private readonly LoopDetector _loopDetector;
    private readonly WireGuardInterface _tunnel;
    private int _bufferSize = 2048;

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
    private readonly long _macAgingMs;
    private readonly HashSet<IPAddress>? _allowedClientPeers;
    private readonly object _clientLock = new();
    private readonly Dictionary<IPEndPoint, long> _clients = new();
    private readonly Dictionary<ulong, RemoteMac> _remoteMacs = new();
    private long _lastClientPruneMs;
    private long _clientForwarded;
    private long _clientDropped;
    private long _clientRejected;
    private long _flooded;

    private struct RemoteMac
    {
        public IPEndPoint Endpoint;
        public long LastSeenMs;
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
        _macAgingMs = config.MacAgingSeconds * 1000L;
        if (config.AllowedClientPeers.Length > 0)
        {
            _allowedClientPeers = new HashSet<IPAddress>(
                config.AllowedClientPeers.Select(IPAddress.Parse));
        }

        _tap = new TapAdapter(tapDevicePath);
        _networkConfigurator = new TapNetworkConfigurator(config, tapAdapterId);
        _transport = TransportFactory.Create(config);
        _filter = new FrameFilter(config.DropEtherTypes, config.DropUdpPorts, config.AllowVlans);
        _macTable = new MacTable(2048, TimeSpan.FromSeconds(config.MacAgingSeconds));
        _loopDetector = new LoopDetector();
        _broadcastTap = new BroadcastLimiter(config.MaxBroadcastPps);
        _broadcastTunnel = new BroadcastLimiter(config.MaxBroadcastPps);
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
        _bufferSize = Math.Max(2048, tunnelMtu + 128 + _transport.HeaderLength);

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
                if (!_filter.Allow(frame))
                {
                    Interlocked.Increment(ref _tapFilterDropped);
                    continue;
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

                        IPEndPoint? client = ResolveRemote(Ethernet.ReadMac(frame[..6]), nowMs);
                        if (client is not null)
                        {
                            await _transport.SendToAsync(buffer, headroom, frameLength, client, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await FloodToClientsAsync(buffer, headroom, frameLength, except: null, ct).ConfigureAwait(false);
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
                if (!_filter.Allow(frame))
                {
                    Interlocked.Increment(ref _tunnelFilterDropped);
                    continue;
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

                        IPEndPoint? destClient = ResolveRemote(destMac, nowMs);
                        if (destClient is not null && !destClient.Equals(source))
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

                        // Unknown destination: let the LAN see it (it may answer) and flood the
                        // remaining clients so a newly seen MAC can be reached.
                        await _tap.WriteAsync(buffer, frameOffset, packetLength - frameOffset, ct).ConfigureAwait(false);
                        Interlocked.Increment(ref _tunnelWritten);

                        if (_allowClientToClient)
                        {
                            await FloodToClientsAsync(buffer, frameOffset, packetLength - frameOffset, except: source, ct).ConfigureAwait(false);
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
                (_serverMode
                    ? $" clients={SnapshotClients().Length} client-forward={Interlocked.Read(ref _clientForwarded)} " +
                      $"client-drop={Interlocked.Read(ref _clientDropped)} rejected={Interlocked.Read(ref _clientRejected)} " +
                      $"flooded={Interlocked.Read(ref _flooded)}."
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

        byte[] probe = ArrayPool<byte>.Shared.Rent(LoopDetector.ProbeLength);
        try
        {
            var interval = TimeSpan.FromSeconds(_config.LoopProbeIntervalSeconds);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                int length = _loopDetector.BuildProbe(probe.AsSpan(0, LoopDetector.ProbeLength), _tap.MacAddress);
                await _tap.WriteAsync(probe, 0, length, ct).ConfigureAwait(false);
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
        (_serverMode ? BuildClientReport() : string.Empty) +
        $"loop_detected {(_loopDetector.LoopDetected ? 1 : 0)}\n";

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

            if (_clients.Count > _maxClients)
            {
                IPEndPoint oldest = _clients.OrderBy(kv => kv.Value).First().Key;
                _clients.Remove(oldest);
                RemoveMacsFor(oldest);
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
            _remoteMacs[mac] = new RemoteMac { Endpoint = endpoint, LastSeenMs = nowMs };
        }
    }

    private IPEndPoint? ResolveRemote(ulong mac, long nowMs)
    {
        lock (_clientLock)
        {
            if (!_remoteMacs.TryGetValue(mac, out RemoteMac entry))
            {
                return null;
            }

            if (nowMs - entry.LastSeenMs > _macAgingMs)
            {
                _remoteMacs.Remove(mac);
                return null;
            }

            return entry.Endpoint;
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
                int macs = _remoteMacs.Count(kv => kv.Value.Endpoint.Equals(client.Key));
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
            RemoveMacsFor(endpoint);
        }
    }

    private void RemoveMacsFor(IPEndPoint endpoint)
    {
        List<ulong> macs = _remoteMacs
            .Where(kv => kv.Value.Endpoint.Equals(endpoint))
            .Select(kv => kv.Key)
            .ToList();

        foreach (ulong mac in macs)
        {
            _remoteMacs.Remove(mac);
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
