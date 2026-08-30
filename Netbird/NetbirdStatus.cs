using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using WGL2Bridge.Peer;
using WGL2Bridge.Platform;

namespace WGL2Bridge.Netbird;

/// <summary>
/// Wraps the NetBird CLI for runtime discovery. The authoritative peer address is the configured
/// 'PeerAddress'; the CLI is used to (1) verify daemon connectivity and (2) discover a peer IP when
/// none is configured. Discovery parsing is tolerant (JsonDocument, not a fixed schema) because the
/// NetBird CLI JSON shape is not version-stable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetbirdStatus
{
    private static readonly string[] DefaultInstallDirs =
    [
        @"C:\Program Files\NetBird",
        @"C:\Program Files (x86)\NetBird",
    ];

    private readonly string? _cliPath;
    private readonly string? _peerName;

    public NetbirdStatus(string? configuredPath, string? peerName)
    {
        _cliPath = ResolveCliPath(configuredPath);
        _peerName = peerName;
    }

    /// <summary>True when netbird.exe was located.</summary>
    public bool CliAvailable => _cliPath is not null;

    private static string? ResolveCliPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (string dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(dir.Trim(), "netbird.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (string dir in DefaultInstallDirs)
        {
            string candidate = Path.Combine(dir, "netbird.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>True when the NetBird daemon reports a connected state.</summary>
    public bool IsConnected()
    {
        if (_cliPath is null)
        {
            return false;
        }

        var (exit, output) = ProcessRunner.Run(_cliPath, "status");
        return exit == 0 && output.Contains("Connected", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Attempts to discover a peer (IP + connection status) from the NetBird CLI, or returns null.</summary>
    public PeerInfo? TryDiscoverPeer()
    {
        if (_cliPath is null)
        {
            return null;
        }

        foreach (string arguments in new[] { "status --json", "peers list --json" })
        {
            var (exit, output) = ProcessRunner.Run(_cliPath, arguments);
            if (exit != 0 || string.IsNullOrWhiteSpace(output))
            {
                continue;
            }

            if (TryParsePeerJson(output, _peerName, out PeerInfo? peer))
            {
                return peer;
            }
        }

        return null;
    }

    private static bool TryParsePeerJson(string json, string? peerName, out PeerInfo? peer)
    {
        peer = null;

        try
        {
            using var document = JsonDocument.Parse(json);

            // netbird status --json: { "peers": { "total": n, "details": [ ... ] } }
            // netbird peers list --json: { "peers": [ ... ] } (or a bare array in some versions)
            if (!document.RootElement.TryGetProperty("peers", out JsonElement peers))
            {
                return false;
            }

            JsonElement details = peers.ValueKind == JsonValueKind.Object &&
                                  peers.TryGetProperty("details", out JsonElement nested) &&
                                  nested.ValueKind == JsonValueKind.Array
                ? nested
                : peers;

            if (details.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            IPAddress? fallback = null;
            foreach (JsonElement item in details.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("netbirdIp", out JsonElement ipElement) ||
                    ipElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? text = ipElement.GetString();
                if (text is null)
                {
                    continue;
                }

                int slash = text.IndexOf('/');
                if (slash >= 0)
                {
                    text = text[..slash];
                }

                if (!IPAddress.TryParse(text, out IPAddress? ip) ||
                    ip.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                bool connected = item.TryGetProperty("status", out JsonElement status) &&
                                 status.ValueKind == JsonValueKind.String &&
                                 string.Equals(status.GetString(), "Connected", StringComparison.OrdinalIgnoreCase);

                if (peerName is not null &&
                    item.TryGetProperty("fqdn", out JsonElement fqdn) &&
                    fqdn.ValueKind == JsonValueKind.String &&
                    string.Equals(fqdn.GetString(), peerName, StringComparison.OrdinalIgnoreCase))
                {
                    peer = new PeerInfo(ip, connected);
                    return true;
                }

                if (connected)
                {
                    fallback ??= ip;
                }
            }

            if (fallback is not null)
            {
                peer = new PeerInfo(fallback, true);
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            // Tolerate schema drift; discovery is best-effort.
        }

        return false;
    }
}
