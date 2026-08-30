using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WGL2Bridge.Logging;

namespace WGL2Bridge.Service;

/// <summary>
/// Minimal Windows Service host implemented directly over advapi32 (StartServiceCtrlDispatcher /
/// RegisterServiceCtrlHandlerEx / SetServiceStatus), so the single NativeAOT binary can run under
/// the SCM without any extra package dependency. Register with e.g.:
/// <c>sc.exe create WGL2Bridge binPath= "C:\path\WGL2Bridge.exe --service"</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsService
{
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceControlShutdown = 0x00000005;
    private const uint ServiceControlInterrogate = 0x00000004;
    private const uint NoError = 0;
    private const uint ErrorCallNotImplemented = 120;
    private const int ErrorFailedServiceControllerConnect = 1063;

    private static Func<CancellationToken, Task>? _runner;
    private static CancellationTokenSource? _cts;
    private static nint _statusHandle;
    private static string _serviceName = "WGL2Bridge";

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public nint ServiceName;
        public nint ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    /// <summary>
    /// Runs the service control dispatcher, blocking until the service stops. Throws if the process
    /// was not started by the Service Control Manager.
    /// </summary>
    public static unsafe void Run(string serviceName, Func<CancellationToken, Task> runner)
    {
        _runner = runner;
        _cts = new CancellationTokenSource();
        _serviceName = serviceName;

        nint namePtr = Marshal.StringToHGlobalUni(serviceName);
        try
        {
            ServiceTableEntry[] entries = new ServiceTableEntry[2];
            entries[0].ServiceName = namePtr;
            entries[0].ServiceMain = (nint)(delegate* unmanaged<uint, nint, void>)&ServiceMain;
            entries[1].ServiceName = 0;
            entries[1].ServiceMain = 0;

            fixed (ServiceTableEntry* p = entries)
            {
                if (!StartServiceCtrlDispatcher(p))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == ErrorFailedServiceControllerConnect)
                    {
                        throw new InvalidOperationException("Not running under the Service Control Manager; run without '--service'.");
                    }

                    throw new InvalidOperationException($"StartServiceCtrlDispatcher failed (Win32 error {error}).");
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void ServiceMain(uint dwNumServicesArgs, nint lpServiceArgVectors)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(_serviceName, &ServiceControlHandler, 0);
        if (_statusHandle == 0)
        {
            return;
        }

        SetStatus(ServiceStartPending, waitHint: 15000, checkpoint: 1);
        SetStatus(ServiceRunning);

        try
        {
            _runner!(_cts!.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            BridgeLog.Error($"Service runner failed: {ex.Message}");
        }

        SetStatus(ServiceStopped);
    }

    [UnmanagedCallersOnly]
    private static uint ServiceControlHandler(uint control, uint eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case ServiceControlStop:
            case ServiceControlShutdown:
                SetStatus(ServiceStopPending, waitHint: 15000, checkpoint: 1);
                _cts!.Cancel();
                return NoError;

            case ServiceControlInterrogate:
                return NoError;

            default:
                return ErrorCallNotImplemented;
        }
    }

    private static void SetStatus(uint state, uint waitHint = 0, uint checkpoint = 0, uint exitCode = 0)
    {
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = ServiceAcceptStop | ServiceAcceptShutdown,
            Win32ExitCode = exitCode,
            CheckPoint = checkpoint,
            WaitHint = waitHint,
        };

        SetServiceStatus(_statusHandle, ref status);
    }

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool StartServiceCtrlDispatcher(ServiceTableEntry* lpServiceTable);

    [LibraryImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial nint RegisterServiceCtrlHandlerEx(
        string lpServiceName,
        delegate* unmanaged<uint, uint, nint, nint, uint> lpHandlerProc,
        nint lpContext);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceStatus(nint hServiceStatus, ref ServiceStatus lpServiceStatus);
}
