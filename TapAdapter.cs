using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WGL2Bridge;

/// <summary>
/// Raw Layer 2 access to a Windows TAP-Windows6 style virtual adapter.
/// The device is opened with FILE_FLAG_OVERLAPPED so the resulting stream performs true async I/O.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TapAdapter : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private int? _driverMtu;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string AdapterName { get; }

    public string DeviceGuid { get; }

    private TapAdapter(string adapterName, string deviceGuid, SafeFileHandle handle, int? driverMtu)
    {
        AdapterName = adapterName;
        DeviceGuid = deviceGuid;
        _handle = handle;
        _driverMtu = driverMtu;
        _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
    }

    /// <summary>
    /// Opens the TAP device backing the adapter with the given Windows connection name
    /// and forces its media state to "connected".
    /// </summary>
    public static TapAdapter Open(string adapterName)
    {
        string guid = NativeMethods.ResolveAdapterGuidByName(adapterName);
        string devicePath = $@"\\.\Global\{guid}.tap";

        SafeFileHandle handle = NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            lpSecurityAttributes: 0,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeSystem | NativeMethods.FileFlagOverlapped,
            hTemplateFile: 0);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error,
                $"Failed to open TAP device '{devicePath}' for adapter '{adapterName}'. " +
                "Ensure the TAP-Windows driver is installed and the process is running elevated.");
        }

        // The media status IOCTL must run before the handle is bound to the thread pool by FileStream.
        if (!NativeMethods.TryDeviceIoControl(handle, NativeMethods.IoctlSetMediaStatus, 1, out _))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error,
                $"TAP_IOCTL_SET_MEDIA_STATUS failed for adapter '{adapterName}' (Win32 error {error}).");
        }

        int? mtu = NativeMethods.TryDeviceIoControl(handle, NativeMethods.IoctlGetMtu, 0, out int reportedMtu)
            ? reportedMtu
            : null;

        return new TapAdapter(adapterName, guid, handle, mtu);
    }

    /// <summary>MTU reported by the TAP driver at open time, or <c>null</c> if the driver did not answer.</summary>
    public int? TryGetDriverMtu() => _driverMtu;

    /// <summary>Reads a single Ethernet frame into <paramref name="buffer"/>.</summary>
    public ValueTask<int> ReadFrameAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _stream.ReadAsync(buffer, cancellationToken);

    /// <summary>Injects a single Ethernet frame into the local segment.</summary>
    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        // The loop detector writes probes concurrently with the tunnel pump; FileStream is not
        // safe for overlapping writes, so they are serialised here.
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        // Disposing the stream closes the handle; this releases the driver so Windows does not
        // leave the virtual adapter wedged in a half-open state after shutdown.
        _stream.Dispose();
        _handle.Dispose();
        _writeLock.Dispose();
    }
}
