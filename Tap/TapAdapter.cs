using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using WGL2Bridge.Network;
using WGL2Bridge.Win32;

namespace WGL2Bridge.Tap;

/// <summary>
/// Raw handle to a TAP-Windows6 virtual Ethernet device. The device is opened with
/// FILE_FLAG_OVERLAPPED and the data path uses explicit overlapped ReadFile/WriteFile bound to the
/// thread pool (the same mechanism OpenVPN uses), because higher-level APIs such as RandomAccess do
/// not reliably deliver frames from this character device. One-shot IOCTLs set up the adapter.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TapAdapter : IDisposable
{
    private SafeFileHandle? _handle;
    private ThreadPoolBoundHandle? _boundHandle;
    private byte[] _mac = new byte[Ethernet.MacLength];

    public TapAdapter(string devicePath) => DevicePath = devicePath;

    /// <summary>Win32 device path of the form \\.\Global\{GUID}.tap.</summary>
    public string DevicePath { get; }

    /// <summary>The driver-reported MAC address, refreshed on each open.</summary>
    public ReadOnlySpan<byte> MacAddress => _mac;

    /// <summary>True while the device handle is open and valid.</summary>
    public bool IsOpen => _handle is { IsClosed: false, IsInvalid: false };

    /// <summary>Opens the device, queries its MAC, and forces the media status to connected.</summary>
    public void Open()
    {
        nint raw = NativeMethods.CreateFile(
            DevicePath,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            lpSecurityAttributes: 0,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeSystem | NativeMethods.FileFlagOverlapped,
            hTemplateFile: 0);

        if (raw is -1 or 0)
        {
            throw new IOException($"TAP CreateFile failed for '{DevicePath}' (error {Marshal.GetLastPInvokeError()}).");
        }

        _handle = new SafeFileHandle(raw, ownsHandle: true);
        _boundHandle = ThreadPoolBoundHandle.BindHandle(_handle);

        QueryMac();
        SetMediaStatus(connected: true);
    }

    /// <summary>Overlapped read of one Ethernet frame into <paramref name="buffer"/>.</summary>
    public ValueTask<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        StartOperation(read: true, buffer, offset, count, ct);

    /// <summary>Overlapped write of one Ethernet frame from <paramref name="buffer"/>.</summary>
    public ValueTask<int> WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        StartOperation(read: false, buffer, offset, count, ct);

    private unsafe ValueTask<int> StartOperation(bool read, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(ct);
        }

        var state = new OverlappedState
        {
            Handle = _handle!,
            BoundHandle = _boundHandle!,
            Tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        state.BufferPin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        byte* pin = (byte*)state.BufferPin.AddrOfPinnedObject();

        NativeOverlapped* overlapped = _boundHandle!.AllocateNativeOverlapped(IoCompletion, state, null);
        overlapped->OffsetLow = 0;
        overlapped->OffsetHigh = 0;
        state.Overlapped = overlapped;

        // Register before issuing I/O so a token that fires mid-flight cancels the just-started
        // operation (CancelIoEx with a null OVERLAPPED aborts everything pending on the handle).
        state.Registration = ct.UnsafeRegister(
            static s => ((OverlappedState)s!).TryCancel(),
            state);

        bool ok = read
            ? NativeMethods.ReadFile(_handle!, pin + offset, (uint)count, null, overlapped)
            : NativeMethods.WriteFile(_handle!, pin + offset, (uint)count, null, overlapped);

        if (!ok)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != 997) // ERROR_IO_PENDING
            {
                state.Registration.Dispose();
                state.Free();
                return ValueTask.FromException<int>(
                    new IOException($"TAP {(read ? "ReadFile" : "WriteFile")} failed (Win32 error {error})."));
            }
        }

        // If the token fired between the initial check and issuing I/O, the callback above ran
        // before any operation was pending. Cancel the now-pending operation explicitly.
        if (ct.IsCancellationRequested)
        {
            state.TryCancel();
        }

        return new ValueTask<int>(state.Tcs.Task);
    }

    private static unsafe void IoCompletion(uint errorCode, uint numBytes, NativeOverlapped* pOverlapped) =>
        OverlappedState.FromPointer(pOverlapped).Complete(errorCode, numBytes);

    private void QueryMac()
    {
        var buffer = new byte[Ethernet.MacLength];
        if (!Ioctl(NativeMethods.TapIoctlGetMac, input: null, buffer))
        {
            throw new IOException($"TAP GET_MAC failed for '{DevicePath}' (error {Marshal.GetLastPInvokeError()}).");
        }

        _mac = buffer;
    }

    private void SetMediaStatus(bool connected)
    {
        byte[] input = BitConverter.GetBytes(connected ? 1u : 0u);
        if (!Ioctl(NativeMethods.TapIoctlSetMediaStatus, input, output: null))
        {
            throw new IOException($"TAP SET_MEDIA_STATUS failed for '{DevicePath}' (error {Marshal.GetLastPInvokeError()}).");
        }
    }

    private bool Ioctl(uint code, byte[]? input, byte[]? output) =>
        NativeMethods.DeviceIoControl(
            _handle!,
            code,
            input,
            (uint)(input?.Length ?? 0),
            output,
            (uint)(output?.Length ?? 0),
            out _,
            IntPtr.Zero);

    public void Dispose()
    {
        _handle?.Dispose();
        _boundHandle?.Dispose();
        _handle = null;
        _boundHandle = null;
    }

    /// <summary>Per-operation state for an overlapped ReadFile/WriteFile bound to the thread pool.</summary>
    private sealed unsafe class OverlappedState
    {
        public SafeFileHandle Handle = null!;
        public ThreadPoolBoundHandle BoundHandle = null!;
        public TaskCompletionSource<int> Tcs = null!;
        public GCHandle BufferPin;
        public NativeOverlapped* Overlapped;
        public CancellationTokenRegistration Registration;

        public static OverlappedState FromPointer(NativeOverlapped* pointer) =>
            (OverlappedState)ThreadPoolBoundHandle.GetNativeOverlappedState(pointer)!;

        /// <summary>
        /// Cancels all pending I/O on the TAP handle. Uses a null OVERLAPPED so it never touches the
        /// (possibly freed) per-operation OVERLAPPED pointer, making it safe to race the completion.
        /// </summary>
        public void TryCancel()
        {
            if (!Handle.IsClosed && !Handle.IsInvalid)
            {
                NativeMethods.CancelIoEx(Handle, null);
            }
        }

        public void Free()
        {
            BoundHandle.FreeNativeOverlapped(Overlapped);
            if (BufferPin.IsAllocated)
            {
                BufferPin.Free();
            }
        }

        public void Complete(uint errorCode, uint numBytes)
        {
            Registration.Dispose();
            Free();

            if (errorCode == 0)
            {
                Tcs.TrySetResult((int)numBytes);
            }
            else if (errorCode == 995) // ERROR_OPERATION_ABORTED
            {
                Tcs.TrySetCanceled();
            }
            else
            {
                Tcs.TrySetException(new IOException($"Overlapped I/O failed (Win32 error {errorCode})."));
            }
        }
    }
}
