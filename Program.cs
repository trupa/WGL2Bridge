using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace WGL2Bridge;

[SupportedOSPlatform("windows")]
public static class Program
{
    /// <summary>Ethernet II frame with 802.1Q headroom; rounded up for the pooled buffer.</summary>
    private const int MaxFrameSize = 1600;

    private static long _framesToTunnel;
    private static long _framesToTap;
    private static long _framesDropped;
    private static ILoggerFactory? _loggerFactory;
    private static ILogger? _logger;
    private static BridgeLog? _log;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string configPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "bridge.config.json");

        var cts = new CancellationTokenSource();

        // ProcessExit can race with normal teardown, so cancellation must tolerate a disposed source.
        void RequestShutdown()
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        ConsoleCancelEventHandler cancelKeyHandler = (_, e) =>
        {
            e.Cancel = true; // Prevent an abrupt kill so the TAP handle is released cleanly.
            LogInfo("Shutdown requested, draining I/O...");
            RequestShutdown();
        };
        EventHandler processExitHandler = (_, _) => RequestShutdown();

        Console.CancelKeyPress += cancelKeyHandler;
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        TapAdapter? tap = null;

        try
        {
            BridgeConfig config = BridgeConfig.Load(configPath);
            InitializeLogging(config);
            LogInfo($"Configuration loaded from {configPath}");
            LogDebug($"Configuration: adapter '{config.TapAdapterName}', discovery {config.PeerDiscovery}, encapsulation {config.EncapsulationMode}, port {config.EffectivePort}, VNI {config.VxlanVni}, GRE key {config.GreKey}.");

            if (!TapProvisioner.IsElevated)
            {
                LogWarning("not running elevated. Opening the raw TAP device requires administrator rights.");
            }

            // ---- 1. TAP adapter preflight and raw Layer 2 attachment ----
            await TapProvisioner.EnsureAdapterExistsAsync(config, _log!, cts.Token).ConfigureAwait(false);
            tap = TapAdapter.Open(config.TapAdapterName);
            LogInfo($"TAP adapter '{tap.AdapterName}' opened ({tap.DeviceGuid}), media state forced to connected.");

            LogInfo(config.EnableBroadIndustrialFilter
                ? "Broad-Industrial-Pass filter ENABLED (ARP/PROFINET/POWERLINK/EtherCAT/GOOSE/IP allowed; mDNS, LLMNR, SSDP, NetBIOS, WS-Discovery dropped)."
                : "Filter DISABLED - all Layer 2 traffic is bridged verbatim.");

            MacTable? macTable = config.EnableMacLearning
                ? new MacTable(TimeSpan.FromSeconds(config.MacAgingSeconds))
                : null;
            LogInfo(macTable is not null
                ? $"MAC learning ENABLED (aging {config.MacAgingSeconds}s); known-local unicast is not flooded."
                : "MAC learning DISABLED - every admitted frame floods both ways.");

            if (config.EnableLoopDetection)
            {
                LogInfo($"Loop detection ENABLED (probe every {config.LoopProbeSeconds}s).");
            }

            // ---- 2. Bridge sessions with tunnel-loss recovery ----
            // The TAP and MAC table survive reconnects; the transport socket and loop detector are
            // rebuilt per session because they are bound to the (possibly changing) tunnel address.
            Task stats = ReportStatsAsync(cts.Token);
            var sessionState = new TunnelSessionState();
            bool firstSession = true;

            // Retry-loop logging state: the failure is logged once, then only the recovery.
            string? lastFailure = null;
            int retryCount = 0;
            var waitingSince = Stopwatch.StartNew();

            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await RunBridgeSessionAsync(config, tap, macTable, sessionState, firstSession, cts).ConfigureAwait(false);

                    if (lastFailure is not null)
                    {
                        LogInfo($"Tunnel recovered after {waitingSince.Elapsed:mm\\:ss} ({retryCount} attempts).");
                        lastFailure = null;
                        retryCount = 0;
                    }

                    break; // Session ended because shutdown was requested.
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (IsRecoverableTunnelFailure(ex))
                {
                    string failure = ex.Message;

                    // Log the first failure, and any change in the failure mode; identical
                    // failures repeat silently until recovery is announced.
                    if (!string.Equals(failure, lastFailure, StringComparison.Ordinal))
                    {
                        if (lastFailure is null)
                        {
                            waitingSince.Restart();
                            LogWarning($"Tunnel lost: {failure}");
                        }
                        else
                        {
                            LogWarning($"Tunnel still down, failure changed: {failure}");
                        }

                        lastFailure = failure;
                    }

                    retryCount++;
                    firstSession = false;
                    LogDebug($"Reconnect attempt {retryCount}: waiting {config.ReconnectDelaySeconds}s before re-binding the tunnel.");

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(config.ReconnectDelaySeconds), cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            await SafeAwait(stats).ConfigureAwait(false);
            LogInfo("Bridge stopped.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            LogInfo("Bridge stopped.");
            return 0;
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("TAP adapter", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("NetBird peer", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("remote bridge", StringComparison.OrdinalIgnoreCase))
        {
            LogError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            LogError(ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelKeyHandler;
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            tap?.Dispose();
            cts.Dispose();
            _loggerFactory?.Dispose();
            _loggerFactory = null;
            _logger = null;
            _log = null;
        }
    }

    /// <summary>Tunnel state carried across reconnect sessions.</summary>
    private sealed class TunnelSessionState
    {
        /// <summary>Resolved once via NetBird discovery (or config), then reused on every reconnect.</summary>
        public IPAddress? RemoteAddress { get; set; }

        /// <summary>Tunnel adapter name learned from NetBird status on the first connect.</summary>
        public string? AdapterName { get; set; }
    }

    /// <summary>
    /// Runs one bridge session: resolves the tunnel endpoints, binds the transport socket and pumps
    /// frames until shutdown is requested or the tunnel fails. Recoverable tunnel failures surface
    /// as exceptions so the caller can wait for NetBird and start a new session.
    /// </summary>
    private static async Task RunBridgeSessionAsync(
        BridgeConfig config,
        TapAdapter tap,
        MacTable? macTable,
        TunnelSessionState state,
        bool firstSession,
        CancellationTokenSource shutdown)
    {
        CancellationToken cancellationToken = shutdown.Token;

        // ---- Tunnel endpoint discovery and socket binding ----
        IPAddress? localTunnelIp = null;
        IPAddress remoteTunnelIp;

        if (config.PeerDiscovery == PeerDiscoveryMode.Netbird)
        {
            if (state.RemoteAddress is null)
            {
                // Full NetBird discovery runs once; the resolved peer is cached for reconnects so a
                // tunnel flap never re-prompts the operator or depends on the CLI mid-reconnect.
                NetbirdStatus netbird = await NetbirdStatus.QueryAsync(config.NetbirdCliPath, cancellationToken).ConfigureAwait(false);
                NetbirdPeer peer;

                if (string.IsNullOrWhiteSpace(config.RemotePeer))
                {
                    peer = netbird.SelectPeerInteractively();
                }
                else if (netbird.HasPeer(config.RemotePeer))
                {
                    peer = netbird.ResolvePeer(config.RemotePeer);
                }
                else if (!Console.IsInputRedirected)
                {
                    // The configured peer is not in the mesh right now (offline or renamed); let the
                    // operator pick from what NetBird currently reports instead of failing.
                    LogInfo($"Configured peer '{config.RemotePeer}' is not in the NetBird mesh.");
                    peer = netbird.SelectPeerInteractively();
                }
                else
                {
                    peer = netbird.ResolvePeer(config.RemotePeer); // throws with the known-peers list
                }

                localTunnelIp = netbird.LocalIp;
                state.RemoteAddress = peer.Address;
                state.AdapterName ??= netbird.InterfaceName;

                LogInfo($"NetBird: local {netbird.LocalFqdn} [{localTunnelIp}] -> peer {peer.Fqdn} [{peer.Address}] ({peer.Status})");
                if (!string.Equals(peer.Status, "Connected", StringComparison.OrdinalIgnoreCase))
                {
                    LogWarning($"NetBird peer '{peer.Fqdn}' is {peer.Status}; frames will be dropped until it connects.");
                }
            }

            else
            {
                LogDebug($"Reusing cached NetBird peer {state.RemoteAddress} on adapter '{state.AdapterName}'.");
            }

            remoteTunnelIp = state.RemoteAddress;
        }
        else
        {
            remoteTunnelIp = IPAddress.Parse(config.RemotePeer);
        }

        NetworkInterface tunnel = WireGuardInterface.Find(
            state.AdapterName ?? config.WireGuardAdapterName ?? "wg0", localTunnelIp);
        if (tunnel.OperationalStatus != OperationalStatus.Up)
        {
            throw new InvalidOperationException(
                $"WireGuard adapter '{tunnel.Name}' is {tunnel.OperationalStatus}; waiting for it to come up.");
        }

        localTunnelIp ??= WireGuardInterface.GetLocalTunnelAddress(tunnel);

        using IBridgeTransport transport = BridgeTransportFactory.Create(config, localTunnelIp, remoteTunnelIp);
        LogInfo($"{(firstSession ? "Encapsulation" : "Tunnel re-established")}: {transport.Describe()} (bound to '{tunnel.Name}')");

        // A reconnect can change the tunnel MTU (different relay path), so the TAP MTU is
        // re-derived and re-applied every session, not only at process start.
        int tunnelMtu = WireGuardInterface.GetMtu(tunnel);
        config.EffectiveTapMtu = config.TapMtuOverride ?? BridgeConfig.FallbackTapMtu;
        ApplyDerivedTapMtu(config, transport, tunnelMtu);

        await TapNetworkConfigurator.ApplyAsync(config, _log!, cancellationToken).ConfigureAwait(false);
        await CheckMtuAsync(config, tap, tunnelMtu, cancellationToken).ConfigureAwait(false);

        // Session-scoped cancellation: a tunnel failure stops this session's pumps without
        // touching the process-wide shutdown token.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // The loop detector holds the transport, so it is rebuilt per session.
        LoopDetector? loopDetector = config.EnableLoopDetection
            ? new LoopDetector(transport, tap, TimeSpan.FromSeconds(config.LoopProbeSeconds),
                config.StopOnLoopDetected, _log!, shutdown)
            : null;

        if (firstSession)
        {
            LogInfo("Bridge running. Press Ctrl+C to stop.");
        }

        // ---- Bidirectional pump ----
        Task toTunnel = PumpTapToTunnelAsync(tap, transport, config, macTable, loopDetector, sessionCts.Token);
        Task toTap = PumpTunnelToTapAsync(tap, transport, config, macTable, loopDetector, sessionCts.Token);
        Task probes = loopDetector?.RunAsync(sessionCts.Token) ?? Task.CompletedTask;

        Task finished = await Task.WhenAny(toTunnel, toTap).ConfigureAwait(false);
        Exception? failure = finished.Exception?.GetBaseException();

        // Stop the surviving pump and the probe loop, then wait for a clean drain.
        await sessionCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(SafeAwait(toTunnel), SafeAwait(toTap), SafeAwait(probes)).ConfigureAwait(false);

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>
    /// True when the failure means the tunnel went away (adapter down, address removed, peer
    /// unreachable) and the bridge should wait and rebind instead of exiting. Configuration
    /// mistakes (unknown peer, missing netbird.exe) are not recoverable and stay fatal.
    /// </summary>
    private static bool IsRecoverableTunnelFailure(Exception ex)
    {
        if (ex is SocketException socket)
        {
            return socket.SocketErrorCode is SocketError.AddressNotAvailable
                or SocketError.NetworkUnreachable
                or SocketError.HostUnreachable
                or SocketError.NetworkDown
                or SocketError.ConnectionReset
                or SocketError.HostDown
                or SocketError.HostNotFound
                or SocketError.TimedOut;
        }

        if (ex is InvalidOperationException invalid)
        {
            // Tunnel adapter missing/down/unaddressed, the NetBird daemon mid-restart, or the
            // mesh not synced yet. All of these clear on their own once NetBird connects.
            return invalid.Message.Contains("WireGuard adapter", StringComparison.OrdinalIgnoreCase)
                || invalid.Message.Contains("has no IPv4 address", StringComparison.OrdinalIgnoreCase)
                || invalid.Message.Contains("netbirdIp", StringComparison.OrdinalIgnoreCase)
                || invalid.Message.Contains("netbird status", StringComparison.OrdinalIgnoreCase)
                || invalid.Message.Contains("still connecting", StringComparison.OrdinalIgnoreCase)
                || invalid.Message.Contains("no peers", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>TAP -> tunnel: reads raw Ethernet frames and encapsulates them into the tunnel.</summary>
    private static async Task PumpTapToTunnelAsync(
        TapAdapter tap,
        IBridgeTransport transport,
        BridgeConfig config,
        MacTable? macTable,
        LoopDetector? loopDetector,
        CancellationToken cancellationToken)
    {
        int headerSize = transport.HeaderSize;

        // Single pooled buffer for the lifetime of the loop: header headroom first, frame after it.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(headerSize + MaxFrameSize);
        try
        {
            // The tunnel header is constant, so it is written once and reused for every frame.
            transport.WriteHeader(buffer.AsSpan(0, headerSize));
            Memory<byte> framePayload = buffer.AsMemory(headerSize, MaxFrameSize);

            while (!cancellationToken.IsCancellationRequested)
            {
                int length = await tap.ReadFrameAsync(framePayload, cancellationToken).ConfigureAwait(false);
                if (length <= 0)
                {
                    continue;
                }

                if (loopDetector is not null &&
                    loopDetector.TryHandleProbe(buffer.AsSpan(headerSize, length), BridgePort.Tap))
                {
                    continue; // Probe frames are consumed, never bridged.
                }

                if (macTable is not null &&
                    !macTable.ShouldForward(buffer.AsSpan(headerSize, length), BridgePort.Tap))
                {
                    Interlocked.Increment(ref _framesDropped);
                    continue;
                }

                if (config.EnableBroadIndustrialFilter &&
                    !IndustrialFilter.ShouldForward(buffer.AsSpan(headerSize, length)))
                {
                    Interlocked.Increment(ref _framesDropped);
                    continue;
                }

                try
                {
                    await transport.SendAsync(buffer.AsMemory(0, headerSize + length), cancellationToken)
                                   .ConfigureAwait(false);
                    Interlocked.Increment(ref _framesToTunnel);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize)
                {
                    LogWarning($"Frame of {length} bytes exceeds the tunnel MTU. Lower the TAP MTU to {config.EffectiveTapMtu}.");
                }
                catch (SocketException ex) when (IsTransient(ex))
                {
                    // Tunnel flapping / peer temporarily unreachable: drop and keep pumping.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Tunnel -> TAP: de-encapsulates tunnel datagrams and injects them onto the local segment.</summary>
    private static async Task PumpTunnelToTapAsync(
        TapAdapter tap,
        IBridgeTransport transport,
        BridgeConfig config,
        MacTable? macTable,
        LoopDetector? loopDetector,
        CancellationToken cancellationToken)
    {
        // Receive headroom also covers the IPv4 header that raw (GRE) sockets prepend.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxFrameSize + 128);
        try
        {
            Memory<byte> memory = buffer.AsMemory(0, MaxFrameSize + 128);

            while (!cancellationToken.IsCancellationRequested)
            {
                int length;
                try
                {
                    length = await transport.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException ex) when (IsTransient(ex))
                {
                    continue; // ICMP port-unreachable from a peer that is not listening yet.
                }

                if (length <= 0)
                {
                    continue;
                }

                int offset = transport.GetInnerFrameOffset(buffer.AsSpan(0, length));
                if (offset < 0)
                {
                    Interlocked.Increment(ref _framesDropped);
                    continue; // Foreign VNI/GRE key, or not a GRETAP payload.
                }

                int frameLength = length - offset;
                if (loopDetector is not null &&
                    loopDetector.TryHandleProbe(buffer.AsSpan(offset, frameLength), BridgePort.Tunnel))
                {
                    continue;
                }

                if (macTable is not null &&
                    !macTable.ShouldForward(buffer.AsSpan(offset, frameLength), BridgePort.Tunnel))
                {
                    Interlocked.Increment(ref _framesDropped);
                    continue;
                }

                if (config.EnableBroadIndustrialFilter &&
                    !IndustrialFilter.ShouldForward(buffer.AsSpan(offset, frameLength)))
                {
                    Interlocked.Increment(ref _framesDropped);
                    continue;
                }

                await tap.WriteFrameAsync(buffer.AsMemory(offset, frameLength), cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _framesToTap);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Derives the largest TAP MTU that still fits inside the tunnel:
    /// tunnel MTU - outer IP/UDP (or GRE) header - tunnel header - inner Ethernet header.
    /// The WireGuard/NetBird overhead is already reflected in the tunnel adapter's own MTU.
    /// </summary>
    private static void ApplyDerivedTapMtu(BridgeConfig config, IBridgeTransport transport, int tunnelMtu)
    {
        if (config.TapMtuOverride is not null)
        {
            return;
        }

        if (tunnelMtu <= 0)
        {
            LogWarning($"Tunnel MTU is unknown; falling back to a TAP MTU of {config.EffectiveTapMtu}.");
            return;
        }

        const int OuterIpv4Header = 20;
        const int UdpHeader = 8;
        const int InnerEthernetHeader = 14;

        int outerOverhead = config.EncapsulationMode == EncapsulationMode.Gretap
            ? OuterIpv4Header + transport.HeaderSize
            : OuterIpv4Header + UdpHeader + transport.HeaderSize;

        int derived = tunnelMtu - outerOverhead - InnerEthernetHeader;
        if (derived < 576)
        {
            LogWarning($"tunnel MTU {tunnelMtu} leaves only {derived} bytes for the TAP adapter; keeping {config.EffectiveTapMtu}.");
            return;
        }

        config.EffectiveTapMtu = derived;
        LogInfo($"Derived TAP MTU {derived} (tunnel {tunnelMtu} - {outerOverhead} encapsulation - {InnerEthernetHeader} Ethernet).");
    }

    private static async Task CheckMtuAsync(BridgeConfig config, TapAdapter tap, int tunnelMtu, CancellationToken cancellationToken)
    {
        // The netsh MTU write returns before the IPv4 stack has applied it, so the first read can
        // still show the old value. Give it a moment and retry once before reporting.
        int? tapMtu = await GetAdapterMtuAsync(config.TapAdapterName, cancellationToken).ConfigureAwait(false);

        if (config.EnforceTapMtu && tapMtu is not null && tapMtu != config.EffectiveTapMtu)
        {
            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
            tapMtu = await GetAdapterMtuAsync(config.TapAdapterName, cancellationToken).ConfigureAwait(false);
        }

        tapMtu ??= tap.TryGetDriverMtu();

        LogInfo($"MTU report: TAP={(tapMtu is > 0 ? tapMtu.ToString() : "unknown")}, tunnel={(tunnelMtu > 0 ? tunnelMtu.ToString() : "unknown")}");

        if (tapMtu is null or <= 0 || tapMtu > config.EffectiveTapMtu)
        {
            LogWarning($"set the '{config.TapAdapterName}' adapter MTU to {config.EffectiveTapMtu} to avoid fragmentation inside the tunnel.");
            LogWarning($"netsh interface ipv4 set subinterface \"{config.TapAdapterName}\" mtu={config.EffectiveTapMtu} store=persistent");
        }
    }

    /// <summary>
    /// Reads the IPv4 MTU from the Netio store via netsh — the same store the configurator writes
    /// with "set subinterface". NetworkInterface.GetIPv4Properties().Mtu reports the driver link
    /// MTU for TAP adapters and never reflects the netsh write, so it cannot verify it.
    /// </summary>
    private static async Task<int?> GetAdapterMtuAsync(string adapterName, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("netsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("interface");
        startInfo.ArgumentList.Add("ipv4");
        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add("subinterface");
        startInfo.ArgumentList.Add(adapterName);

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            foreach (string line in (await stdout.ConfigureAwait(false)).Split('\n'))
            {
                // Data rows look like: "      1230                1     18477      133391  Industrial-TAP"
                string trimmed = line.Trim();
                int firstSpace = trimmed.IndexOf(' ');
                if (firstSpace > 0 && int.TryParse(trimmed[..firstSpace], out int mtu) && mtu > 0)
                {
                    return mtu;
                }
            }

            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task ReportStatsAsync(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var uptime = Stopwatch.StartNew();
        long lastOut = -1, lastIn = -1, lastDropped = -1;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                long framesOut = Interlocked.Read(ref _framesToTunnel);
                long framesIn = Interlocked.Read(ref _framesToTap);
                long framesDropped = Interlocked.Read(ref _framesDropped);

                // Quiet when idle: only the ticks where something actually moved are interesting.
                if (framesOut != lastOut || framesIn != lastIn || framesDropped != lastDropped)
                {
                    LogInfo($"[{uptime.Elapsed:hh\\:mm\\:ss}] out={framesOut} in={framesIn} dropped={framesDropped}");
                    lastOut = framesOut;
                    lastIn = framesIn;
                    lastDropped = framesDropped;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            timer.Dispose();
        }
    }

    private static bool IsTransient(SocketException ex) =>
        ex.SocketErrorCode is SocketError.ConnectionReset
            or SocketError.NetworkUnreachable
            or SocketError.HostUnreachable
            or SocketError.NetworkDown;

    private static async Task SafeAwait(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void InitializeLogging(BridgeConfig config)
    {
        if (_loggerFactory is not null)
        {
            return;
        }

        string filePath = string.IsNullOrWhiteSpace(config.LogFilePath)
            ? "wgl2bridge.log.txt"
            : config.LogFilePath;

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Trace);

            builder.AddSimpleConsole(options =>
            {
                options.TimestampFormat = "HH:mm:ss.fff  ";
                options.SingleLine = true;
                options.ColorBehavior = LoggerColorBehavior.Disabled;
            });

            builder.AddProvider(new PlainTextFileLoggerProvider(filePath));
            builder.AddFilter<ConsoleLoggerProvider>(category: null, config.ConsoleLogLevel);
            builder.AddFilter<PlainTextFileLoggerProvider>(category: null, config.FileLogLevel);
        });

        _logger = _loggerFactory.CreateLogger("WGL2Bridge");
        _log = new BridgeLog(_logger);
    }

    private static void LogInfo(string message) => Write(LogLevel.Information, message);

    private static void LogDebug(string message) => Write(LogLevel.Debug, message);

    private static void LogWarning(string message) => Write(LogLevel.Warning, message);

    private static void LogError(string message) => Write(LogLevel.Error, message);

    private static void Write(LogLevel level, string message)
    {
        if (_logger is null)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");
            return;
        }

        _logger.Log(level, "{Message}", message);
    }
}
