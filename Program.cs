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

namespace WGL2Bridge;

/// <summary>
/// Entry point. Supports console mode (default), '--service' (run under the SCM) and '--check'
/// (validate configuration and resolution without opening devices). Misconfiguration exits with
/// code 1 — it never retries forever.
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
            PeerInfo? peer = peerProvider.Resolve();
            if (peer is null)
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

            int overhead = config.TransportMode switch
            {
                TransportMode.Vxlan => 36,
                TransportMode.GreTap => 24,
                _ => 20,
            };
            int tapMtu = Math.Clamp(tunnel.Mtu - overhead - (config.AssumeVlanTagged ? 18 : 14), 576, 9000);

            BridgeLog.Info($"Check OK: peer={peer.Address}, tunnel='{config.TunnelInterfaceName}' {tunnel.LocalAddress} MTU {tunnel.Mtu}, tap='{config.TapName}' MTU {tapMtu}.");
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

        var netbird = new NetbirdStatus(config.NetbirdCliPath, config.PeerName);
        if (!netbird.CliAvailable)
        {
            BridgeLog.Warning("netbird.exe not found; peer discovery and status checks unavailable.");
        }
        else if (!netbird.IsConnected())
        {
            BridgeLog.Warning("NetBird daemon is not reporting a connected state.");
        }

        IPeerProvider peerProvider = new PeerResolver(config, new NetbirdPeerProvider(netbird));

        PeerInfo? peer = peerProvider.Resolve();
        if (peer is null)
        {
            BridgeLog.Error("Unknown peer: no 'PeerAddress'/'PeerName' resolved and overlay discovery failed.");
            throw new InvalidOperationException("Unknown peer.");
        }

        BridgeLog.Debug($"Peer resolved to '{peer.Address}' (connected={peer.Connected}).");

        if (!config.PeerSourceValidation)
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
        networkConfigurator.ApplyRouteIsolation();
        networkConfigurator.ConfigureAddress();

        await using var bridge = new Bridge(config, peer.Address, peerProvider, tapInfo.DevicePath, tapInfo.AdapterId);

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
