using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WGL2Bridge;

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint FileAttributeSystem = 0x00000004;
    internal const uint FileFlagOverlapped = 0x40000000;

    /// <summary>Network adapter class GUID used by the Windows registry adapter enumeration.</summary>
    internal const string AdapterClassGuid = "{4D36E972-E325-11CE-BFC1-08002BE10318}";

    private const string AdapterKey = @"SYSTEM\CurrentControlSet\Control\Class\" + AdapterClassGuid;
    private const string ConnectionKey = @"SYSTEM\CurrentControlSet\Control\Network\" + AdapterClassGuid;

    // CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, function, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    // tap-windows6 passes the request number straight through, with no 0x800 vendor offset.
    private static uint TapControlCode(uint function) => (0x22 << 16) | (function << 2);

    /// <summary>TAP_WIN_IOCTL_SET_MEDIA_STATUS (request 6) - forces the virtual adapter link state to "connected".</summary>
    internal static uint IoctlSetMediaStatus => TapControlCode(6);

    /// <summary>TAP_WIN_IOCTL_GET_MTU (request 3) - reads the MTU currently configured on the TAP driver.</summary>
    internal static uint IoctlGetMtu => TapControlCode(3);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        void* lpInBuffer,
        int nInBufferSize,
        void* lpOutBuffer,
        int nOutBufferSize,
        int* lpBytesReturned,
        NativeOverlapped* lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
    private static partial nint CreateEvent(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetOverlappedResult(SafeFileHandle hFile, NativeOverlapped* lpOverlapped,
        out int lpNumberOfBytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    private const int ErrorIoPending = 997;

    /// <summary>
    /// Issues a buffered IOCTL against a handle opened with FILE_FLAG_OVERLAPPED and waits for it.
    /// Such handles reject a NULL OVERLAPPED, so one backed by an event object is always supplied.
    /// </summary>
    internal static unsafe bool TryDeviceIoControl(SafeFileHandle handle, uint controlCode, int input, out int output)
    {
        int inputValue = input;
        int outputValue = 0;
        int transferred = 0;
        int lastError = 0;

        nint eventHandle = CreateEvent(0, manualReset: true, initialState: false, 0);
        if (eventHandle == 0)
        {
            output = 0;
            return false;
        }

        try
        {
            var overlapped = new NativeOverlapped { EventHandle = eventHandle };
            bool result = DeviceIoControl(handle, controlCode, &inputValue, sizeof(int),
                &outputValue, sizeof(int), &transferred, &overlapped);

            if (!result && Marshal.GetLastWin32Error() == ErrorIoPending)
            {
                result = GetOverlappedResult(handle, &overlapped, out transferred, bWait: true);
            }

            output = outputValue;
            lastError = result ? 0 : Marshal.GetLastWin32Error();
            return result;
        }
        finally
        {
            CloseHandle(eventHandle); // Clobbers the last error, hence the capture above.
            Marshal.SetLastPInvokeError(lastError);
        }
    }

    /// <summary>
    /// Resolves the NetCfgInstanceId (device GUID) of an adapter given its Windows connection name.
    /// </summary>
    internal static string ResolveAdapterGuidByName(string connectionName)
        => TryResolveAdapterGuidByName(connectionName)
           ?? throw new InvalidOperationException(
               $"No network adapter named '{connectionName}' was found. Check the adapter name in Network Connections.");

    /// <summary>
    /// Resolves the NetCfgInstanceId of an adapter by connection name, or <c>null</c> if it does not exist.
    /// </summary>
    internal static string? TryResolveAdapterGuidByName(string connectionName)
        => TryResolveFromRegistry(connectionName) ?? TryResolveFromNetworkInterfaces(connectionName);

    private static string? TryResolveFromRegistry(string connectionName)
    {
        RegistryKey? adapters;
        try
        {
            adapters = Registry.LocalMachine.OpenSubKey(AdapterKey, writable: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return null;
        }

        if (adapters is null)
        {
            return null;
        }

        using (adapters)
        {
            foreach (string index in adapters.GetSubKeyNames())
            {
                string? instanceId;
                try
                {
                    using RegistryKey? adapter = adapters.OpenSubKey(index, writable: false);
                    instanceId = adapter?.GetValue("NetCfgInstanceId") as string;
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
                {
                    continue; // Some device subkeys are ACL'd to SYSTEM/TrustedInstaller only.
                }

                if (instanceId is null)
                {
                    continue;
                }

                try
                {
                    using RegistryKey? connection =
                        Registry.LocalMachine.OpenSubKey($@"{ConnectionKey}\{instanceId}\Connection", writable: false);
                    if (connection?.GetValue("Name") is string name &&
                        string.Equals(name, connectionName, StringComparison.OrdinalIgnoreCase))
                    {
                        return instanceId;
                    }
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
                {
                    // Ignore and keep scanning.
                }
            }
        }

        return null;
    }

    /// <summary>Permission-free fallback: NetworkInterface.Id is the adapter's NetCfgInstanceId.</summary>
    private static string? TryResolveFromNetworkInterfaces(string connectionName)
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (string.Equals(nic.Name, connectionName, StringComparison.OrdinalIgnoreCase))
            {
                return nic.Id;
            }
        }

        return null;
    }
}
