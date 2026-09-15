using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WGL2Bridge.NativeMethods;

/// <summary>
/// Source-generated P/Invoke declarations. TAP-Windows6 uses METHOD_BUFFERED control codes rooted at
/// FILE_DEVICE_UNKNOWN (0x22), matching OpenVPN's tap-windows.h (TAP_WIN_CONTROL_CODE macro).
/// </summary>
internal static partial class WindowsNative
{
    private const uint FileDeviceUnknown = 0x22;
    private const uint MethodBuffered = 0;
    private const uint FileAnyAccess = 0;

    private const uint CtlGetMac = 1;
    private const uint CtlSetMediaStatus = 6;

    /// <summary>TAP_WIN_IOCTL_GET_MAC — returns the 6-byte adapter MAC.</summary>
    public static uint TapIoctlGetMac => CtlCode(CtlGetMac);

    /// <summary>TAP_WIN_IOCTL_SET_MEDIA_STATUS — ULONG input, 1 = connected.</summary>
    public static uint TapIoctlSetMediaStatus => CtlCode(CtlSetMediaStatus);

    private static uint CtlCode(uint function) =>
        (FileDeviceUnknown << 16) | (FileAnyAccess << 14) | (function << 2) | MethodBuffered;

    /// <summary>
    /// Synchronous DeviceIoControl with no OVERLAPPED. Used only for one-shot IOCTLs during session
    /// setup; the data path uses overlapped ReadFile/WriteFile instead.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        uint nInBufferSize,
        byte[]? lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>Overlapped read used on the TAP data path. lpNumberOfBytesRead must be null.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ReadFile(
        SafeFileHandle hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToRead,
        uint* lpNumberOfBytesRead,
        NativeOverlapped* lpOverlapped);

    /// <summary>Overlapped write used on the TAP data path. lpNumberOfBytesWritten must be null.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool WriteFile(
        SafeFileHandle hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToWrite,
        uint* lpNumberOfBytesWritten,
        NativeOverlapped* lpOverlapped);

    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;
    public const uint FileAttributeSystem = 0x00000004;
    public const uint FileFlagOverlapped = 0x40000000;

    /// <summary>
    /// Opens the TAP device with FILE_FLAG_OVERLAPPED explicitly. File.OpenHandle's FileOptions.Asynchronous
    /// does not reliably set the overlapped flag on device paths, which breaks ThreadPoolBoundHandle.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    /// <summary>
    /// Cancels pending overlapped I/O issued against a handle. Passing a null OVERLAPPED cancels all
    /// outstanding operations on the handle, which is what session teardown needs: every pending TAP
    /// ReadFile/WriteFile completes with ERROR_OPERATION_ABORTED instead of hanging the pump.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool CancelIoEx(SafeFileHandle hFile, NativeOverlapped* lpOverlapped);
}
