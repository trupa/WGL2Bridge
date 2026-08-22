using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace WGL2Bridge;

public enum PeerDiscoveryMode
{
    /// <summary>Use the literal addresses from the configuration file.</summary>
    None,

    /// <summary>Resolve the local and remote tunnel IPs from `netbird status --json`.</summary>
    Netbird
}

public sealed record NetbirdPeer(string Fqdn, IPAddress Address, string Status);

/// <summary>
/// Reads the local NetBird client state via `netbird status --json` so the bridge can bind to the
/// NetBird tunnel IP and resolve a peer's tunnel IP by FQDN instead of hard-coding addresses.
/// </summary>
public sealed class NetbirdStatus
{
    private readonly List<NetbirdPeer> _peers;

    private NetbirdStatus(IPAddress localIp, string localFqdn, string interfaceName, List<NetbirdPeer> peers)
    {
        LocalIp = localIp;
        LocalFqdn = localFqdn;
        InterfaceName = interfaceName;
        _peers = peers;
    }

    public IPAddress LocalIp { get; }

    public string LocalFqdn { get; }

    /// <summary>NetBird's WireGuard interface name ("wt0" on Windows/userspace, configurable on Linux).</summary>
    public string InterfaceName { get; }

    public IReadOnlyList<NetbirdPeer> Peers => _peers;

    private static readonly string[] CliSearchPaths =
    [
        @"C:\Program Files\Netbird\netbird.exe",
        @"C:\Program Files (x86)\Netbird\netbird.exe",
        @"C:\Program Files\NetBird\netbird.exe"
    ];

    public static async Task<NetbirdStatus> QueryAsync(string? cliPath, CancellationToken cancellationToken)
    {
        string executable = ResolveCli(cliPath);

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--json");

        using Process process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException($"Failed to start '{executable}'.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        string json = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'netbird status --json' failed with exit code {process.ExitCode}. {error.Trim()}");
        }

        return Parse(json);
    }

    public static NetbirdStatus Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("netbirdIp", out JsonElement localElement) ||
            localElement.GetString() is not string localCidr)
        {
            throw new InvalidOperationException("NetBird status contains no 'netbirdIp'. Is the client connected?");
        }

        IPAddress localIp = ParseAddress(localCidr);
        string localFqdn = root.TryGetProperty("fqdn", out JsonElement fqdnElement) ? fqdnElement.GetString() ?? "" : "";

        // NetBird names its userspace WireGuard interface "wt0" and does not report it in the JSON.
        string interfaceName = root.TryGetProperty("interface", out JsonElement ifElement)
            ? ifElement.GetString() ?? "wt0"
            : "wt0";

        var peers = new List<NetbirdPeer>();
        if (root.TryGetProperty("peers", out JsonElement peersElement) &&
            peersElement.TryGetProperty("details", out JsonElement details) &&
            details.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement peer in details.EnumerateArray())
            {
                if (peer.TryGetProperty("netbirdIp", out JsonElement ipElement) &&
                    ipElement.GetString() is string peerIp)
                {
                    peers.Add(new NetbirdPeer(
                        peer.TryGetProperty("fqdn", out JsonElement peerFqdn) ? peerFqdn.GetString() ?? "" : "",
                        ParseAddress(peerIp),
                        peer.TryGetProperty("status", out JsonElement status) ? status.GetString() ?? "" : ""));
                }
            }
        }

        return new NetbirdStatus(localIp, localFqdn, interfaceName, peers);
    }

    /// <summary>Resolves a peer by full FQDN, short hostname, or tunnel IP.</summary>
    public NetbirdPeer ResolvePeer(string nameOrAddress)    {
        foreach (NetbirdPeer peer in _peers)
        {
            if (string.Equals(peer.Fqdn, nameOrAddress, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ShortName(peer.Fqdn), nameOrAddress, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(peer.Address.ToString(), nameOrAddress, StringComparison.Ordinal))
            {
                return peer;
            }
        }

        string known = _peers.Count == 0
            ? "(no peers reported)"
            : string.Join(", ", _peers.Select(p => $"{p.Fqdn} [{p.Address}]"));

        throw new InvalidOperationException(
            $"NetBird peer '{nameOrAddress}' was not found. Known peers: {known}");
    }

    /// <summary>True when a peer matches by full FQDN, short hostname, or tunnel IP.</summary>
    public bool HasPeer(string nameOrAddress)
    {
        foreach (NetbirdPeer peer in _peers)
        {
            if (string.Equals(peer.Fqdn, nameOrAddress, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ShortName(peer.Fqdn), nameOrAddress, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(peer.Address.ToString(), nameOrAddress, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ShortName(string fqdn)
    {
        int dot = fqdn.IndexOf('.');
        return dot > 0 ? fqdn[..dot] : fqdn;
    }

    /// <summary>Prompts the operator to pick a peer from the connected NetBird mesh.</summary>
    public NetbirdPeer SelectPeerInteractively()
    {
        if (_peers.Count == 0)
        {
            throw new InvalidOperationException("NetBird reports no peers to bridge to.");
        }

        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "No remote peer configured and stdin is redirected; set 'RemotePeer' in the configuration.");
        }

        if (_peers.Count == 1)
        {
            return _peers[0];
        }

        Console.WriteLine();
        Console.WriteLine("Select the remote bridge peer:");
        for (int i = 0; i < _peers.Count; i++)
        {
            NetbirdPeer peer = _peers[i];
            Console.WriteLine($"  [{i + 1}] {peer.Fqdn,-40} {peer.Address,-16} {peer.Status}");
        }

        while (true)
        {
            Console.Write($"Peer [1-{_peers.Count}]: ");
            string? input = Console.ReadLine();

            if (input is null)
            {
                throw new InvalidOperationException("Peer selection aborted.");
            }

            input = input.Trim();

            if (int.TryParse(input, out int choice) && choice >= 1 && choice <= _peers.Count)
            {
                return _peers[choice - 1];
            }

            foreach (NetbirdPeer peer in _peers)
            {
                if (string.Equals(peer.Fqdn, input, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ShortName(peer.Fqdn), input, StringComparison.OrdinalIgnoreCase))
                {
                    return peer;
                }
            }

            Console.WriteLine("Invalid selection.");
        }
    }

    /// <summary>NetBird reports the local address in CIDR form ("100.99.58.65/16"); peers are bare IPs.</summary>
    private static IPAddress ParseAddress(string value)
    {
        int slash = value.IndexOf('/');
        string address = slash >= 0 ? value[..slash] : value;

        // While the daemon is connecting, fields like netbirdIp are empty or invalid; surface that
        // as InvalidOperationException so the caller treats it as "tunnel not ready" and retries.
        if (!IPAddress.TryParse(address, out IPAddress? parsed))
        {
            throw new InvalidOperationException(
                $"NetBird reported an invalid address '{value}'. The client is probably still connecting.");
        }

        return parsed;
    }

    private static string ResolveCli(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? configured
                : throw new FileNotFoundException($"NetBird CLI not found at '{configured}'.", configured);
        }

        foreach (string candidate in CliSearchPaths)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim(), "netbird.exe");
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

        throw new FileNotFoundException(
            "netbird.exe was not found. Install the NetBird client or set 'NetbirdCliPath' in the configuration.");
    }
}
