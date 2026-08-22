using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace WGL2Bridge;

/// <summary>
/// Preflight provisioning for the TAP-Windows6 adapter: detects a missing adapter and either
/// creates it with tapctl.exe (when auto-provisioning is enabled and the process is elevated)
/// or prints the exact commands the operator must run.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TapProvisioner
{
    private static readonly string[] TapCtlSearchPaths =
    [
        @"C:\Program Files\OpenVPN\bin\tapctl.exe",
        @"C:\Program Files (x86)\OpenVPN\bin\tapctl.exe",
        @"C:\Program Files\TAP-Windows\bin\tapctl.exe",
        @"C:\Program Files (x86)\TAP-Windows\bin\tapctl.exe"
    ];

    public static bool IsElevated
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Ensures the configured TAP adapter exists, provisioning it when allowed.
    /// </summary>
    public static async Task EnsureAdapterExistsAsync(BridgeConfig config, Action<string> log, CancellationToken cancellationToken)
    {
        if (NativeMethods.TryResolveAdapterGuidByName(config.TapAdapterName) is not null)
        {
            return;
        }

        log($"TAP adapter '{config.TapAdapterName}' does not exist.");

        string? tapCtl = FindTapCtl();

        if (!config.AutoCreateTapAdapter || tapCtl is null || !IsElevated)
        {
            PrintManualInstructions(config, tapCtl, log);
            throw new InvalidOperationException(
                $"TAP adapter '{config.TapAdapterName}' is missing and could not be created automatically.");
        }

        log($"Creating TAP adapter with {tapCtl} ...");
        await RunAsync(tapCtl, ["create", "--name", config.TapAdapterName], log, cancellationToken).ConfigureAwait(false);

        // The device node takes a moment to register before its registry entries are readable.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (NativeMethods.TryResolveAdapterGuidByName(config.TapAdapterName) is not null)
            {
                log($"TAP adapter '{config.TapAdapterName}' created.");
                await ConfigureAdapterAsync(config, log, cancellationToken).ConfigureAwait(false);
                return;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"tapctl reported success but adapter '{config.TapAdapterName}' did not appear in the registry.");
    }

    private static async Task ConfigureAdapterAsync(BridgeConfig config, Action<string> log, CancellationToken cancellationToken)
    {
        await RunAsync("netsh",
            ["interface", "set", "interface", $"name={config.TapAdapterName}", "admin=enabled"],
            log, cancellationToken).ConfigureAwait(false);

        await RunAsync("netsh",
            ["interface", "ipv4", "set", "subinterface", config.TapAdapterName, $"mtu={config.EffectiveTapMtu}", "store=persistent"],
            log, cancellationToken).ConfigureAwait(false);

        log($"Adapter enabled and MTU set to {config.EffectiveTapMtu}.");
    }

    private static void PrintManualInstructions(BridgeConfig config, string? tapCtl, Action<string> log)
    {
        log("");
        log("Run the following in an ELEVATED PowerShell to provision the adapter:");

        if (tapCtl is null)
        {
            log("  1. Install the OpenVPN 'TAP Virtual Ethernet Adapter' component (tap-windows6 driver).");
            log("     Note: WireGuard's Wintun adapter is Layer 3 only and cannot be used as the source TAP.");
            log($"  2. & \"C:\\Program Files\\OpenVPN\\bin\\tapctl.exe\" create --name \"{config.TapAdapterName}\"");
        }
        else
        {
            log($"  & \"{tapCtl}\" create --name \"{config.TapAdapterName}\"");
        }

        log($"  netsh interface ipv4 set subinterface \"{config.TapAdapterName}\" mtu={config.EffectiveTapMtu} store=persistent");
        log($"  Enable-NetAdapter -Name \"{config.TapAdapterName}\"");
        log("");
        log($"Alternatively set \"AutoCreateTapAdapter\": true in the configuration and run this bridge elevated.");

        if (!IsElevated)
        {
            log("This process is NOT running elevated; raw TAP access requires administrator rights.");
        }
    }

    private static string? FindTapCtl()
    {
        foreach (string candidate in TapCtlSearchPaths)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim(), "tapctl.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        return null;
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

        if (output.Length > 0)
        {
            log($"  {fileName}: {output}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(fileName)} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}. {error}");
        }
    }
}
