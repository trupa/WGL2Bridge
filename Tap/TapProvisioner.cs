using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace WGL2Bridge.Tap;

/// <summary>Resolved identity of a TAP-Windows6 adapter.</summary>
public sealed record TapAdapterInfo(string Name, string AdapterId, string DevicePath, string MacAddress);

/// <summary>
/// Resolves a named TAP-Windows6 adapter to its Win32 device path and adapter GUID. The GUID comes
/// from the network stack (NetCfgInstanceId), which is exactly what TAP-Windows6 uses to build its
/// <c>\\.\Global\{GUID}.tap</c> device name.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TapProvisioner
{
    /// <summary>Locates the adapter by name or throws if it does not exist.</summary>
    public TapAdapterInfo Resolve(string tapName) =>
        TryResolve(tapName, out TapAdapterInfo info)
            ? info
            : throw new InvalidOperationException($"TAP adapter '{tapName}' not found.");

    /// <summary>Attempts to locate the adapter by name; returns false when it does not exist.</summary>
    public bool TryResolve(string tapName, out TapAdapterInfo info)
    {
        info = null!;

        NetworkInterface? nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Name, tapName, StringComparison.OrdinalIgnoreCase));
        if (nic is null)
        {
            return false;
        }

        string adapterId = NormalizeGuid(nic.Id);
        string devicePath = $@"\\.\Global\{adapterId}.tap";
        string mac = BitConverter.ToString(nic.GetPhysicalAddress().GetAddressBytes());

        info = new TapAdapterInfo(tapName, adapterId, devicePath, mac);
        return true;
    }

    private static string NormalizeGuid(string id)
    {
        id = id.Trim();
        if (id.StartsWith('{') && id.EndsWith('}'))
        {
            return id;
        }

        return id.StartsWith('{') ? id + "}" : "{" + id + "}";
    }
}
