using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using WGL2Bridge.Config;
using WGL2Bridge.Logging;
using WGL2Bridge.Platform;

namespace WGL2Bridge.Tap;

/// <summary>
/// Creates a TAP-Windows6 adapter when none exists. It prefers OpenVPN's <c>tapctl.exe</c>
/// (which creates and names the adapter in one step and needs no INF, since the driver is already
/// installed), and falls back to the older devcon-style <c>tapinstall.exe</c> / <c>devcon.exe</c>
/// with the OemVista.inf driver package. Creating a root-enumerated virtual adapter requires
/// elevation and the tap0901 driver package to be present.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TapCreator
{
    private static readonly string[] TapCtlDefaults =
    [
        @"C:\Program Files\OpenVPN\bin\tapctl.exe",
        @"C:\Program Files (x86)\OpenVPN\bin\tapctl.exe",
    ];

    private static readonly string[] TapInstallDefaults =
    [
        @"C:\Program Files\TAP-Windows\bin\tapinstall.exe",
        @"C:\Program Files (x86)\TAP-Windows\bin\tapinstall.exe",
        @"C:\Program Files\TAP-Windows\bin\devcon.exe",
    ];

    private static readonly string[] InfDefaults =
    [
        @"C:\Program Files\TAP-Windows\driver\OemVista.inf",
        @"C:\Program Files (x86)\TAP-Windows\driver\OemVista.inf",
    ];

    /// <summary>
    /// Ensures a TAP adapter named <paramref name="tapName"/> exists, creating and naming one if
    /// necessary. Returns false with a human-readable <paramref name="error"/> on failure.
    /// </summary>
    public bool TryCreate(string tapName, BridgeConfig config, out string? error)
    {
        error = null;

        if (NetworkInterface.GetAllNetworkInterfaces()
            .Any(n => string.Equals(n.Name, tapName, StringComparison.OrdinalIgnoreCase)))
        {
            return true; // already exists
        }

        string? tool = ResolveTool(config.TapInstallToolPath, out bool isTapCtl);
        if (tool is null)
        {
            error = "no TAP creation tool found (tapctl.exe / tapinstall.exe / devcon.exe); set 'TapInstallToolPath'.";
            return false;
        }

        return isTapCtl
            ? TryCreateWithTapCtl(tool, tapName, config, out error)
            : TryCreateWithTapInstall(tool, tapName, config, out error);
    }

    private static bool TryCreateWithTapCtl(string tool, string tapName, BridgeConfig config, out string? error)
    {
        error = null;
        BridgeLog.Info($"Creating TAP adapter '{tapName}' via '{tool}' (hwid '{config.TapHardwareId}').");

        var (exit, output) = ProcessRunner.Run(tool, $"create --name \"{tapName}\" --hwid {config.TapHardwareId}");
        if (exit == 0)
        {
            BridgeLog.Info($"TAP adapter '{tapName}' created and ready.");
            return true;
        }

        error = output.Trim();
        return false;
    }

    private static bool TryCreateWithTapInstall(string tool, string tapName, BridgeConfig config, out string? error)
    {
        error = null;

        string? inf = LocateInf(config.TapDriverInfPath);
        if (inf is null)
        {
            error = "TAP driver INF (OemVista.inf) not found; set 'TapDriverInfPath'.";
            return false;
        }

        var before = new HashSet<string>(
            NetworkInterface.GetAllNetworkInterfaces().Select(n => n.Name),
            StringComparer.OrdinalIgnoreCase);

        BridgeLog.Info($"Creating TAP adapter '{tapName}' via '{tool}' (hwid '{config.TapHardwareId}').");
        var (exit, output) = ProcessRunner.Run(tool, $"install \"{inf}\" {config.TapHardwareId}");
        if (exit != 0)
        {
            error = output.Trim();
            return false;
        }

        string? created = WaitForNewAdapter(before);
        if (created is null)
        {
            error = "driver reported success but no new adapter appeared.";
            return false;
        }

        if (!string.Equals(created, tapName, StringComparison.OrdinalIgnoreCase))
        {
            var (renameExit, renameOutput) = ProcessRunner.Run(
                "netsh.exe",
                $"interface set interface name=\"{created}\" newname=\"{tapName}\"");

            if (renameExit != 0)
            {
                error = $"created '{created}' but failed to rename it to '{tapName}': {renameOutput.Trim()}";
                return false;
            }
        }

        BridgeLog.Info($"TAP adapter '{tapName}' created and ready.");
        return true;
    }

    private static string? ResolveTool(string? configuredPath, out bool isTapCtl)
    {
        isTapCtl = false;

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!File.Exists(configuredPath))
            {
                return null;
            }

            isTapCtl = Path.GetFileName(configuredPath).StartsWith("tapctl", StringComparison.OrdinalIgnoreCase);
            return configuredPath;
        }

        if (TryFindInPath("tapctl.exe", out string? tapCtl))
        {
            isTapCtl = true;
            return tapCtl;
        }

        foreach (string candidate in TapCtlDefaults)
        {
            if (File.Exists(candidate))
            {
                isTapCtl = true;
                return candidate;
            }
        }

        if (TryFindInPath("tapinstall.exe", out string? tapInstall))
        {
            return tapInstall;
        }

        if (TryFindInPath("devcon.exe", out string? devcon))
        {
            return devcon;
        }

        return TapInstallDefaults.FirstOrDefault(File.Exists);
    }

    private static string? WaitForNewAdapter(HashSet<string> before)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            Thread.Sleep(500);

            string? created = NetworkInterface.GetAllNetworkInterfaces()
                .Select(n => n.Name)
                .FirstOrDefault(n => !before.Contains(n));

            if (created is not null)
            {
                return created;
            }
        }

        return null;
    }

    private static string? LocateInf(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        return InfDefaults.FirstOrDefault(File.Exists);
    }

    private static bool TryFindInPath(string name, out string? path)
    {
        path = null;
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return false;
        }

        foreach (string dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim(), name);
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        return false;
    }
}
