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
    private IPAddress _peerAddress;
    private readonly IPeerProvider _peerProvider;
    private readonly TapAdapter _tap;
    private readonly TapNetworkConfigurator _networkConfigurator;
    private readonly IBridgeTransport _transport;
    private readonly IndustrialFilter _filter;
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

    private CancellationTokenSource? _sessionCts;
    private volatile bool _stopRequested;

    /// <summary>Builds the bridge from fully-resolved runtime values. The tunnel interface and the
    /// peer are re-resolved on every session so the bridge survives the adapter being
    /// destroyed/recreated or the peer address changing.</summary>
    public Bridge(
        BridgeConfig config,
        IPAddress peerAddress,
        IPeerProvider peerProvider,
        string tapDevicePath,
        string tapAdapterId)
    {
        _config = config;
        _peerAddress = peerAddress;
        _peerProvider = peerProvider;
        _tap = new TapAdapter(tapDevicePath);
        _networkConfigurator = new TapNetworkConfigurator(config, tapAdapterId);
        _transport = TransportFactory.Create(config);
        _filter = new IndustrialFilter(config.DropUdpPorts, config.AllowVlans);
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

            try
            {
                OpenSession();
                BridgeLog.Info("Bridge running.");
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

                // Renew DHCP only after the forwarding pumps are up; otherwise the DHCPDISCOVER has
                // no path to the remote server and ipconfig /renew blocks through its discovery timeout.
                if (recovered && _config.RenewDhcpOnReconnect && string.IsNullOrWhiteSpace(_config.TapIpAddress))
                {
                    _ = RenewDhcpAfterForwardingStartedAsync(_sessionCts.Token);
                }

                await AwaitFirstFaultAsync(pumps).ConfigureAwait(false);
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
                BridgeLog.Debug($"Reconnecting in {_config.ReconnectDelaySeconds}s (attempt {reconnectAttempt}).");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.ReconnectDelaySeconds), cancellationToken).ConfigureAwait(false);
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

    private void OpenSession()
    {
        _tunnel.Resolve(_config);
        RefreshPeer();

        IPAddress localIp = _tunnel.LocalAddress!;
        int tunnelMtu = _tunnel.Mtu;
        _bufferSize = Math.Max(2048, tunnelMtu + 128 + _transport.HeaderLength);

        _tap.Open();
        BridgeLog.Info($"TAP adapter '{_config.TapName}' opened ({_tap.DevicePath}), media state forced to connected.");
        BridgeLog.Debug($"TAP MAC '{FormatMac(_tap.MacAddress)}'.");

        LogTapOperationalStatus();

        int tapMtu = _networkConfigurator.DeriveTapMtu(tunnelMtu, _transport.EncapsulationOverhead);
        _networkConfigurator.ApplyMtu(tapMtu);
        int innerHeader = _config.AssumeVlanTagged ? 18 : 14;
        BridgeLog.Info($"TAP MTU {tapMtu} (tunnel MTU {tunnelMtu} - {_transport.EncapsulationOverhead} encapsulation - {innerHeader} Ethernet header).");

        _transport.Open(localIp, _peerAddress);
        BridgeLog.Info($"Encapsulation active: {_transport.Description}; egress pinned to tunnel address '{localIp}'.");
    }

    private void RefreshPeer()
    {
        PeerInfo? fresh = _peerProvider.Resolve();
        if (fresh is null)
        {
            BridgeLog.Debug($"Peer re-resolution failed; keeping cached peer '{_peerAddress}'.");
            return;
        }

        if (!fresh.Address.Equals(_peerAddress))
        {
            BridgeLog.Info($"Peer address changed: '{_peerAddress}' -> '{fresh.Address}'.");
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
        _tap.Dispose();
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
        else
        {
            BridgeLog.Info(
                $"TAP adapter '{_config.TapName}' operational status '{nic.OperationalStatus}'. " +
                $"IPv4: {(ipv4.Length == 0 ? "none" : string.Join(", ", ipv4.Select(a => a.ToString())))}.");
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
                if (frame.Length >= Ethernet.HeaderLength)
                {if (!frame[6..12].SequenceEqual(_tap.MacAddress))
                    {
                        _macTable.Learn(frame[6..12], MacTable.Port.Tunnel, nowMs);
                    }
                    else if (Interlocked.Exchange(ref _tapMacReflectedLogged, 1) == 0)
                    {
                        BridgeLog.Warning("Own TAP MAC observed arriving from the tunnel (reflection/loop); not learning it as remote.");
                    }

                    _macTable.Learn(frame[6..12], MacTable.Port.Tunnel, nowMs);
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

            BridgeLog.Info(
                $"Stats: tap→tunnel read={Interlocked.Read(ref _tapFramesRead)} forwarded={Interlocked.Read(ref _tapForwarded)} " +
                $"filter-drop={Interlocked.Read(ref _tapFilterDropped)} mac-suppress={Interlocked.Read(ref _tapMacSuppressed)}; " +
                $"tunnel→tap received={Interlocked.Read(ref _tunnelPacketsReceived)} written={Interlocked.Read(ref _tunnelWritten)} " +
                $"invalid={Interlocked.Read(ref _tunnelInvalid)} filter-drop={Interlocked.Read(ref _tunnelFilterDropped)} " +
                $"mac-suppress={Interlocked.Read(ref _tunnelMacSuppressed)}; " +
                $"storm-drop={Interlocked.Read(ref _broadcastDropped)} non-peer-drop={_transport.DroppedNonPeer}.");
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

            BridgeLog.Debug($"InterfaceWatch: localIp='{localIp}' present={present}.");

            if (!present)
            {
                throw new IOException($"Tunnel interface '{_config.TunnelInterfaceName}' is down or missing.");
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

            PeerInfo? peer = await ResolvePeerBoundedAsync(ct).ConfigureAwait(false);

            BridgeLog.Debug(
                $"HealthCheck: peer={(peer is null ? "null" : peer.Address.ToString())} connected={peer?.Connected}.");

            if (peer is { Connected: false })
            {
                string peerLabel = string.IsNullOrWhiteSpace(_config.PeerName) ? peer.Address.ToString() : _config.PeerName;
                throw new IOException($"NetBird reports peer '{peerLabel}' is not connected.");
            }

            long nowMs = Environment.TickCount64;
            long lastSent = Interlocked.Read(ref _lastTapForwardMs);
            long lastReceived = Interlocked.Read(ref _lastTunnelReceiveMs);

            if (lastSent > 0 && nowMs - lastSent <= intervalMs && nowMs - lastReceived > intervalMs)
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
        $"peer {_peerAddress}\n" +
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
            BridgeLog.Warning($"Tunnel lost: {message}. Reconnecting in {_config.ReconnectDelaySeconds}s.");
            lastFailure = message;
        }
    }

    private static string FormatMac(ReadOnlySpan<byte> mac) =>
        string.Join(':', mac[0].ToString("X2"), mac[1].ToString("X2"), mac[2].ToString("X2"),
            mac[3].ToString("X2"), mac[4].ToString("X2"), mac[5].ToString("X2"));

    public ValueTask DisposeAsync()
    {
        _stopRequested = true;
        _sessionCts?.Cancel();
        CloseSession();
        _transport.Dispose();
        return ValueTask.CompletedTask;
    }
}
