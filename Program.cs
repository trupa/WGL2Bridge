using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WGL2Bridge.Config;
using WGL2Bridge.Core;
using WGL2Bridge.Logging;
using WGL2Bridge.Metrics;
using WGL2Bridge.Netbird;
using WGL2Bridge.Network;
using WGL2Bridge.Peer;
using WGL2Bridge.Service;
using WGL2Bridge.Tap;
using WGL2Bridge.Transport;

namespace WGL2Bridge;

/// <summary>
/// Entry point. Supports console mode (default), '--service' (run under the SCM) and '--check'
/// (validate configuration and resolution without opening devices). Static misconfiguration exits
/// with code 1; runtime conditions such as a missing tunnel adapter or an unresolved peer are
/// treated as recoverable and retried by the bridge session loop.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Parse command-line arguments
        bool checkMode = args.Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase));
        bool serviceMode = args.Any(a => a.Equals("--service", StringComparison.OrdinalIgnoreCase));
        string configPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "appsettings.json";

        try
        {
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"Config file '{configPath}' not found.");
                return 1;
            }

            BridgeConfig config = LoadConfig(configPath);
            IReadOnlyList<string> errors = config.Validate();
            if (errors.Count > 0)
            {
                foreach (string error in errors)
                {
                    Console.Error.WriteLine($"Config error: {error}");
                }

                return 1;
            }

            BridgeLog.Initialize(config);
            BridgeLog.Info("WGL2Bridge starting.");

            // Check mode validates configuration and resolves the peer/tunnel/TAP without opening any device.
            if (checkMode)
            {
                return RunCheck(config) ? 0 : 1;
            }

            if (serviceMode)
            {
                WindowsService.Run("WGL2Bridge", ct => RunBridge(config, ct));
                return 0;
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await RunBridge(config, cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            if (BridgeLog.IsInitialized)
            {
                BridgeLog.Error($"Fatal: {ex.Message}");
            }
            else
            {
                Console.Error.WriteLine($"Fatal: {ex.Message}");
            }

            return 1;
        }
    }

    /// <summary>Validates configuration and resolves peer/tunnel/TAP without opening any device.</summary>
    private static bool RunCheck(BridgeConfig config)
    {
        try
        {
            var netbird = new NetbirdStatus(config.NetbirdCliPath, config.PeerName);
            IPeerProvider peerProvider = new PeerResolver(config, new NetbirdPeerProvider(netbird));
            bool serverMode = config.BridgeSide == BridgeSide.Lan;
            PeerInfo? peer = peerProvider.Resolve();
            if (peer is null && !serverMode)
            {
                BridgeLog.Error("Unknown peer: no 'PeerAddress'/'PeerName' resolved and overlay discovery failed.");
                return false;
            }

            var tunnel = new WireGuardInterface { Name = config.TunnelInterfaceName };
            tunnel.Resolve(config);

            var provisioner = new TapProvisioner();
            if (!provisioner.TryResolve(config.TapName, out TapAdapterInfo tapInfo))
            {
                if (config.CreateTapIfMissing)
                {
                    BridgeLog.Info($"TAP adapter '{config.TapName}' not found; it would be created by tapctl at runtime.");
                }
                else
                {
                    BridgeLog.Error($"TAP adapter '{config.TapName}' not found ('CreateTapIfMissing' is disabled).");
                    return false;
                }
            }
            else
            {
                BridgeLog.Debug($"TAP '{tapInfo.Name}' -> '{tapInfo.DevicePath}'.");
            }

            // Use the real transport so the reported MTU matches the runtime session exactly.
            // Constructing a transport opens no sockets, so this stays a dry run.
            using IBridgeTransport transport = TransportFactory.Create(config);
            int tapMtu = Math.Clamp(
                tunnel.Mtu - transport.EncapsulationOverhead - (config.AssumeVlanTagged ? 18 : 14),
                576,
                9000);

            BridgeLog.Info($"Check OK: peer={(peer is null ? "(server: learns clients)" : peer.Address.ToString())}, tunnel='{config.TunnelInterfaceName}' {tunnel.LocalAddress} MTU {tunnel.Mtu}, tap='{config.TapName}' MTU {tapMtu}, encap='{transport.Description}'.");
            return true;
        }
        catch (Exception ex)
        {
            BridgeLog.Error($"Check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Resolves runtime state, configures the adapter, and runs the bridge (plus metrics).</summary>
    private static async Task RunBridge(BridgeConfig config, CancellationToken cancellationToken)
    {
        BridgeLog.Debug(
            $"Resolved config: mode={config.TransportMode}, tap='{config.TapName}', " +
            $"tunnel='{config.TunnelInterfaceName}', peer={(config.PeerAddress ?? "(discover)")}.");

        bool serverMode = config.BridgeSide == BridgeSide.Lan;
        var netbird = new NetbirdStatus(config.NetbirdCliPath, config.PeerName);

        // The NetBird CLI is only consulted when discovery is actually required: a 'PeerName'
        // target, or no 'PeerAddress' to pin. With a pinned peer the CLI is never used, so don't
        // probe it (avoids nuisance warnings and a needless 'netbird status' subprocess).
        bool overlayDiscoveryRequired = !serverMode &&
            (!string.IsNullOrWhiteSpace(config.PeerName) || string.IsNullOrWhiteSpace(config.PeerAddress));

        if (serverMode)
        {
            BridgeLog.Info(
                "Bridge side is 'Lan': accepting multiple clients on the tunnel" +
                (string.IsNullOrWhiteSpace(config.PeerAddress) && string.IsNullOrWhiteSpace(config.PeerName)
                    ? " (learned from inbound traffic)."
                    : $"; '{config.PeerAddress ?? config.PeerName}' is used as the initial client."));
        }
        else if (overlayDiscoveryRequired)
        {
            if (!netbird.CliAvailable)
            {
                BridgeLog.Warning("netbird.exe not found; NetBird peer discovery is unavailable.");
            }
            else if (!netbird.IsConnected())
            {
                BridgeLog.Warning("NetBird daemon is not reporting a connected state.");
            }
        }
        else
        {
            BridgeLog.Debug("Peer pinned via 'peerAddress'; NetBird CLI not required.");
        }

        IPeerProvider peerProvider = new PeerResolver(config, new NetbirdPeerProvider(netbird));

        // Peer resolution is best-effort at startup: the overlay or tunnel adapter may simply not
        // be up yet (for example when NetBird starts after this service). The bridge session loop
        // re-resolves the peer and retries, so a missing peer/tunnel must not abort the process.
        PeerInfo? peer = null;
        try
        {
            peer = peerProvider.Resolve();
        }
        catch (Exception ex)
        {
            BridgeLog.Warning($"Peer resolution failed: {ex.Message}");
        }

        if (peer is null)
        {
            if (serverMode)
            {
                BridgeLog.Info("LAN/server mode: no peer configured; waiting for clients to appear.");
            }
            else
            {
                string target = !string.IsNullOrWhiteSpace(config.PeerName)
                    ? $"peer '{config.PeerName}'"
                    : !string.IsNullOrWhiteSpace(config.PeerAddress)
                        ? $"peer address '{config.PeerAddress}'"
                        : "peer (no 'PeerName' or 'PeerAddress' configured)";

                BridgeLog.Warning($"Configured {target} is not available; waiting and retrying (no fallback to another peer).");
            }
        }
        else
        {
            BridgeLog.Debug($"Peer resolved to '{peer.Address}' (connected={peer.Connected}).");
        }

        if (serverMode)
        {
            if (config.AllowedClientPeers.Length == 0)
            {
                BridgeLog.Warning("Lan/server mode: 'allowedClientPeers' is empty; any peer that can reach the tunnel may join.");
            }

            if (!config.AllowClientToClient)
            {
                BridgeLog.Info("Lan/server mode: client-to-client forwarding is disabled.");
            }
        }
        else if (!config.PeerSourceValidation)
        {
            BridgeLog.Warning("Peer source validation is DISABLED: inbound tunnel packets are accepted from any source.");
        }

        var provisioner = new TapProvisioner();
        if (!provisioner.TryResolve(config.TapName, out TapAdapterInfo tapInfo))
        {
            if (!config.CreateTapIfMissing)
            {
                BridgeLog.Error($"TAP adapter '{config.TapName}' not found ('CreateTapIfMissing' is disabled).");
                throw new InvalidOperationException($"TAP adapter '{config.TapName}' not found.");
            }

            BridgeLog.Info($"TAP adapter '{config.TapName}' not found; creating it.");
            var creator = new TapCreator();
            if (!creator.TryCreate(config.TapName, config, out string? createError))
            {
                BridgeLog.Error($"Failed to create TAP adapter '{config.TapName}': {createError}");
                throw new InvalidOperationException($"Failed to create TAP adapter '{config.TapName}'.");
            }

            tapInfo = provisioner.Resolve(config.TapName);
        }

        BridgeLog.Debug($"TAP '{tapInfo.Name}' -> device '{tapInfo.DevicePath}' (GUID '{tapInfo.AdapterId}').");

        var networkConfigurator = new TapNetworkConfigurator(config, tapInfo.AdapterId);

        if (config.BridgeSide == BridgeSide.Client)
        {
            networkConfigurator.ApplyRouteIsolation();
            networkConfigurator.ConfigureAddress();
        }
        else
        {
            BridgeLog.Info(
                $"Bridge side is 'Lan': '{config.TapName}' is a bridge port, so TAP address configuration " +
                "and route isolation are skipped (the network bridge owns the LAN address)." +
                (string.IsNullOrWhiteSpace(config.TapIpAddress) ? string.Empty : " 'tapIpAddress' is ignored in this mode."));
        }

        await using var bridge = new Bridge(config, peer?.Address, peerProvider, tapInfo.DevicePath, tapInfo.AdapterId);

        using var metricsCts = new CancellationTokenSource();
        Task metricsTask = config.MetricsPort > 0
            ? MetricsEndpoint.RunAsync(config.MetricsPort, bridge.GetMetricsSnapshot, metricsCts.Token)
            : Task.CompletedTask;

        try
        {
            await bridge.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            metricsCts.Cancel();
        }

        if (config.MetricsPort > 0)
        {
            await metricsTask.ConfigureAwait(false);
        }
    }

    private static BridgeConfig LoadConfig(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.BridgeConfig) ?? new BridgeConfig();
    }
}
