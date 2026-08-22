using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;

namespace WGL2Bridge;

public enum TapAddressMode
{
    /// <summary>Leave the adapter's IP configuration untouched.</summary>
    Manual,

    /// <summary>Request an address from a DHCP server on the bridged segment.</summary>
    Dhcp,

    /// <summary>Apply the static address from the configuration.</summary>
    Static
}

/// <summary>
/// Applies the Windows-side IP configuration of the TAP adapter at startup. The bridge already
/// runs elevated, so every step is executed directly instead of being printed for the operator.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TapNetworkConfigurator
{
    public static async Task ApplyAsync(BridgeConfig config, Action<string> log, CancellationToken cancellationToken)
    {
        string adapter = config.TapAdapterName;

        if (config.EnforceTapMtu)
        {
            await RunAsync("netsh",
                ["interface", "ipv4", "set", "subinterface", adapter, $"mtu={config.EffectiveTapMtu}", "store=persistent"],
                log, cancellationToken).ConfigureAwait(false);
            log($"TAP MTU set to {config.EffectiveTapMtu}.");
        }

        // Routing isolation must be set BEFORE addressing: once a DHCP lease lands, its default
        // route is installed immediately, and NetBird's network monitor reacts to any new default
        // route by restarting its engine (taking the tunnel down with it). With
        // ignoredefaultroutes already enabled, the lease's gateway is never installed, so no
        // route-add event ever reaches NetBird.
        if (config.IsolateTapRouting)
        {
            await RunAsync("netsh",
                ["interface", "ipv4", "set", "interface", adapter, "ignoredefaultroutes=enabled", "metric=9999"],
                log, cancellationToken).ConfigureAwait(false);
            log("TAP routing isolated: default routes ignored, interface metric 9999.");
        }

        switch (config.TapAddressMode)
        {
            case TapAddressMode.Dhcp:
                await RunAsync("netsh",
                    ["interface", "ipv4", "set", "address", adapter, "source=dhcp"],
                    log, cancellationToken).ConfigureAwait(false);
                log("TAP address mode: DHCP (leases from the remote segment).");
                break;

            case TapAddressMode.Static:
                string mask = PrefixToMask(config.TapStaticPrefixLength);
                // No gateway argument: the industrial segment must never become the default route.
                await RunAsync("netsh",
                    ["interface", "ipv4", "set", "address", adapter, "static", config.TapStaticAddress!, mask],
                    log, cancellationToken).ConfigureAwait(false);
                log($"TAP address mode: static {config.TapStaticAddress}/{config.TapStaticPrefixLength} (no gateway).");
                break;
        }

        if (config.DisableIpv6OnTap)
        {
            // NDIS binding changes can briefly bounce other adapters (including the NetBird
            // userspace tunnel), so only touch the bindings when they are not already disabled.
            string escaped = adapter.Replace("'", "''");
            var check = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            check.ArgumentList.Add("-NoProfile");
            check.ArgumentList.Add("-NonInteractive");
            check.ArgumentList.Add("-Command");
            check.ArgumentList.Add(
                $"(Get-NetAdapterBinding -Name '{escaped}' -ComponentID ms_tcpip6,ms_lltdio,ms_rspndr " +
                "-ErrorAction SilentlyContinue | Where-Object Enabled | Measure-Object).Count");

            int enabledCount = 1; // Assume enabled when the query fails: disabling is the safe default.
            try
            {
                using Process? probe = Process.Start(check);
                if (probe is not null)
                {
                    Task<string> probeOut = probe.StandardOutput.ReadToEndAsync(cancellationToken);
                    await probe.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    await probe.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                    if (int.TryParse((await probeOut.ConfigureAwait(false)).Trim(), out int count))
                    {
                        enabledCount = count;
                    }
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }

            if (enabledCount == 0)
            {
                log("IPv6 and link-layer discovery bindings already disabled on the TAP adapter.");
            }
            else
            {
                await RunAsync("powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        $"Disable-NetAdapterBinding -Name '{escaped}' " +
                        "-ComponentID ms_tcpip6,ms_lltdio,ms_rspndr -ErrorAction SilentlyContinue"
                    ],
                    log, cancellationToken).ConfigureAwait(false);
                log("IPv6 and link-layer discovery bindings disabled on the TAP adapter.");
            }
        }
    }

    private static string PrefixToMask(int prefixLength)
    {
        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        return new IPAddress([(byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask]).ToString();
    }

    private static async Task RunAsync(string fileName, string[] arguments, Action<string> log, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        string output = (await stdout.ConfigureAwait(false)).Trim();
        string error = (await stderr.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
        {
            // Adapter configuration is best-effort: a failure here should not stop the bridge.
            log($"WARNING: '{Path.GetFileName(fileName)} {string.Join(' ', arguments)}' returned {process.ExitCode}. {error} {output}".TrimEnd());
        }
    }
}
