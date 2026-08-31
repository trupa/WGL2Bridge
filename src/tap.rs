//! Raw Layer 2 access to a Windows TAP-Windows6 virtual adapter.
//!
//! The adapter is resolved by its Windows connection name through the registry, opened with
//! `FILE_FLAG_OVERLAPPED`, and bound to Tokio's IOCP-backed file type so reads and writes are
//! truly asynchronous. The bridge must run elevated: raw TAP device access requires it.

use std::future::Future;
use std::pin::Pin;

use anyhow::Result;

/// Abstraction over the local Ethernet device that feeds the bridge.
///
/// The bridge engine talks to this trait instead of a concrete driver type, so the packet path
/// stays independent of the Windows TAP plumbing and remains testable on other platforms.
pub trait TapPort: Send + Sync {
    /// Reads one Ethernet frame into `buffer`; returns the frame length in bytes.
    fn read_frame<'a>(
        &'a self,
        buffer: &'a mut [u8],
    ) -> Pin<Box<dyn Future<Output = Result<usize>> + Send + 'a>>;

    /// Injects one Ethernet frame onto the local segment.
    fn write_frame<'a>(
        &'a self,
        frame: &'a [u8],
    ) -> Pin<Box<dyn Future<Output = Result<()>> + Send + 'a>>;
}

/// Placeholder port for non-Windows targets.
///
/// The TAP driver integration is Windows-only; other platforms compile against this no-op port so
/// the rest of the bridge logic can still be built and tested.
#[cfg(not(windows))]
pub struct NullTapPort;

#[cfg(not(windows))]
impl TapPort for NullTapPort {
    fn read_frame<'a>(
        &'a self,
        _buffer: &'a mut [u8],
    ) -> Pin<Box<dyn Future<Output = Result<usize>> + Send + 'a>> {
        Box::pin(async move { Ok(0) })
    }

    fn write_frame<'a>(
        &'a self,
        _frame: &'a [u8],
    ) -> Pin<Box<dyn Future<Output = Result<()>> + Send + 'a>> {
        Box::pin(async move { Ok(()) })
    }
}

#[cfg(windows)]
pub use self::windows_tap::WindowsTapPort;

/// Checks whether a TAP adapter with the given connection name exists, without opening it.
#[cfg(windows)]
pub fn adapter_exists(connection_name: &str) -> bool {
    self::windows_tap::resolve_adapter_guid_by_name(connection_name).is_ok()
}

#[cfg(windows)]
mod windows_tap {
    use std::future::Future;
    use std::pin::Pin;

    use anyhow::{Context, Result, bail};
    use tokio::sync::Mutex;
    use windows_sys::Win32::Foundation::{
        CloseHandle, ERROR_IO_PENDING, GetLastError, HANDLE, INVALID_HANDLE_VALUE,
    };
    use windows_sys::Win32::Storage::FileSystem::{
        CreateFileW, FILE_ATTRIBUTE_SYSTEM, FILE_FLAG_OVERLAPPED, FILE_SHARE_READ,
        FILE_SHARE_WRITE, OPEN_EXISTING, ReadFile, WriteFile,
    };
    use windows_sys::Win32::System::IO::{DeviceIoControl, GetOverlappedResult, OVERLAPPED};
    use windows_sys::Win32::System::Threading::{CreateEventW, WaitForSingleObject};

    use super::TapPort;

    const GENERIC_READ: u32 = 0x8000_0000;
    const GENERIC_WRITE: u32 = 0x4000_0000;

    // CTL_CODE(FILE_DEVICE_UNKNOWN = 0x22, function, METHOD_BUFFERED = 0, FILE_ANY_ACCESS = 0).
    // tap-windows6 passes the request number straight through, with no vendor offset.

    /// TAP_WIN_IOCTL_SET_MEDIA_STATUS (request 6): forces the virtual link state to "connected".
    const IOCTL_SET_MEDIA_STATUS: u32 = (0x22 << 16) | (6 << 2);

    /// TAP_WIN_IOCTL_GET_MTU (request 3): reads the MTU configured on the TAP driver.
    const IOCTL_GET_MTU: u32 = (0x22 << 16) | (3 << 2);

    /// Network adapter class GUID used by the registry adapter enumeration.
    const ADAPTER_CLASS_GUID: &str = "{4D36E972-E325-11CE-BFC1-08002BE10318}";

    /// tap-windows6 device accessed with manual overlapped I/O.
    ///
    /// tap-windows6 rejects handles bound to an I/O completion port (ERROR_INVALID_PARAMETER), so
    /// this uses plain event-signalled OVERLAPPED operations. Each direction owns an event object
    /// and a mutex; the event wait runs on Tokio's blocking pool so the async runtime is never
    /// stalled while the driver holds a pending read.
    pub struct WindowsTapPort {
        adapter_name: String,
        device_guid: String,
        driver_mtu: Option<u32>,
        handle: SendHandle,
        read_event: SendHandle,
        write_event: SendHandle,
        read_lock: Mutex<()>,
        write_lock: Mutex<()>,
    }

    /// A raw HANDLE that can be sent across the blocking pool boundary.
    ///
    /// Safety: the handle is owned by the port, closed exactly once in Drop, and every operation
    /// on it is serialised by the per-direction mutex, so no two threads touch it concurrently in
    /// the same direction.
    #[derive(Clone, Copy)]
    struct SendHandle(HANDLE);
    unsafe impl Send for SendHandle {}
    unsafe impl Sync for SendHandle {}

    impl WindowsTapPort {
        /// Opens the TAP device backing the adapter with the given Windows connection name and
        /// forces its media state to "connected".
        pub fn open(adapter_name: &str) -> Result<Self> {
            let guid = resolve_adapter_guid_by_name(adapter_name)?;
            let device_path = format!(r"\\.\Global\{guid}.tap");

            let handle = open_device(&device_path).with_context(|| {
                format!(
                    "failed to open TAP device '{device_path}' for adapter '{adapter_name}'; \
                     ensure the TAP-Windows driver is installed and the process is elevated"
                )
            })?;

            // The media status IOCTL must run before any data I/O begins.
            if let Err(err) = device_io_control(handle, IOCTL_SET_MEDIA_STATUS, 1) {
                unsafe { CloseHandle(handle) };
                return Err(err).context(
                    "TAP_IOCTL_SET_MEDIA_STATUS failed; the adapter is not a tap-windows6 device",
                );
            }

            // The MTU query is best-effort: some driver builds do not answer it.
            let driver_mtu = device_io_control(handle, IOCTL_GET_MTU, 0).ok().map(|v| v as u32);

            let read_event = create_event()?;
            let write_event = match create_event() {
                Ok(event) => event,
                Err(err) => {
                    unsafe {
                        CloseHandle(read_event.0);
                        CloseHandle(handle);
                    }
                    return Err(err);
                }
            };

            Ok(Self {
                adapter_name: adapter_name.to_owned(),
                device_guid: guid,
                driver_mtu,
                handle: SendHandle(handle),
                read_event,
                write_event,
                read_lock: Mutex::new(()),
                write_lock: Mutex::new(()),
            })
        }

        /// Windows connection name of the adapter.
        pub fn adapter_name(&self) -> &str {
            &self.adapter_name
        }

        /// NetCfgInstanceId of the adapter, used in the device path.
        pub fn device_guid(&self) -> &str {
            &self.device_guid
        }

        /// MTU reported by the TAP driver at open time, or `None` if the driver did not answer.
        pub fn driver_mtu(&self) -> Option<u32> {
            self.driver_mtu
        }
    }

    impl Drop for WindowsTapPort {
        fn drop(&mut self) {
            unsafe {
                CloseHandle(self.read_event.0);
                CloseHandle(self.write_event.0);
                CloseHandle(self.handle.0);
            }
        }
    }

    impl TapPort for WindowsTapPort {
        fn read_frame<'a>(
            &'a self,
            buffer: &'a mut [u8],
        ) -> Pin<Box<dyn Future<Output = Result<usize>> + Send + 'a>> {
            Box::pin(async move {
                let _guard = self.read_lock.lock().await;
                overlapped_io(
                    self.handle,
                    self.read_event,
                    OverlappedOp::Read(buffer),
                )
                .await
            })
        }

        fn write_frame<'a>(
            &'a self,
            frame: &'a [u8],
        ) -> Pin<Box<dyn Future<Output = Result<()>> + Send + 'a>> {
            Box::pin(async move {
                // The loop detector writes probes concurrently with the tunnel pump, so writes
                // are serialised behind the write lock.
                let _guard = self.write_lock.lock().await;
                overlapped_io(self.handle, self.write_event, OverlappedOp::Write(frame)).await?;
                Ok(())
            })
        }
    }

    /// One overlapped operation: the payload buffer is borrowed for the duration of the await.
    enum OverlappedOp<'a> {
        Read(&'a mut [u8]),
        Write(&'a [u8]),
    }

    /// Issues a single event-signalled overlapped ReadFile/WriteFile and awaits completion.
    ///
    /// The buffer stays alive in the caller's future frame for the whole operation, and the
    /// per-direction lock guarantees only one operation per direction is in flight, so the raw
    /// pointers handed to the driver remain valid until GetOverlappedResult returns.
    async fn overlapped_io(handle: SendHandle, event: SendHandle, op: OverlappedOp<'_>) -> Result<usize> {
        // Submit the I/O synchronously; the driver returns immediately with ERROR_IO_PENDING when
        // the operation is queued.
        let mut overlapped = OverlappedGuard::new(event.0);

        let pending = unsafe {
            let ov = overlapped.ptr();
            match op {
                OverlappedOp::Read(buf) => {
                    let read = ReadFile(
                        handle.0,
                        buf.as_mut_ptr(),
                        buf.len() as u32,
                        std::ptr::null_mut(),
                        ov,
                    );
                    if read != 0 {
                        return overlapped.finish(handle.0);
                    }
                }
                OverlappedOp::Write(buf) => {
                    let written = WriteFile(
                        handle.0,
                        buf.as_ptr(),
                        buf.len() as u32,
                        std::ptr::null_mut(),
                        ov,
                    );
                    if written != 0 {
                        return overlapped.finish(handle.0);
                    }
                }
            }

            let error = GetLastError();
            if error != ERROR_IO_PENDING {
                bail!("overlapped I/O submission failed with Win32 error {error}");
            }
            true
        };

        if pending {
            // Wait for the driver to signal completion on the blocking pool so the Tokio reactor
            // thread is never stalled. The OVERLAPPED and buffer stay pinned in the caller frame
            // until this await resolves. The wait goes through a helper so the closure captures
            // the whole SendHandle (Rust 2024 precise captures would otherwise grab the raw field,
            // which is not Send).
            tokio::task::spawn_blocking(move || wait_for_event(event))
                .await
                .context("overlapped wait task failed")?;

            overlapped.finish(handle.0)
        } else {
            unreachable!("submission path returns early on synchronous completion")
        }
    }

    /// Blocks the calling thread until the overlapped event is signalled by the driver.
    fn wait_for_event(event: SendHandle) {
        unsafe { WaitForSingleObject(event.0, u32::MAX) };
    }

    /// Owns the OVERLAPPED storage for one in-flight operation and collects the result.
    ///
    /// Heap-pinning the OVERLAPPED keeps its address stable while the driver holds a pointer to
    /// it. The struct is safe to hold across the blocking-pool await: the driver signals the event
    /// inside it, and GetOverlappedResult reads it only after the wait resolves.
    struct OverlappedGuard {
        inner: Box<OVERLAPPED>,
    }
    unsafe impl Send for OverlappedGuard {}

    impl OverlappedGuard {
        fn new(event: HANDLE) -> Self {
            let mut inner = Box::new(unsafe { std::mem::zeroed::<OVERLAPPED>() });
            inner.hEvent = event;
            Self { inner }
        }

        fn ptr(&mut self) -> *mut OVERLAPPED {
            &mut *self.inner
        }

        /// Collects the byte count for a completed (or synchronously finished) operation.
        fn finish(self, handle: HANDLE) -> Result<usize> {
            let mut transferred: u32 = 0;
            let ok = unsafe { GetOverlappedResult(handle, &*self.inner, &mut transferred, 1) };
            if ok == 0 {
                let error = unsafe { GetLastError() };
                bail!("GetOverlappedResult failed with Win32 error {error}");
            }
            Ok(transferred as usize)
        }
    }

    /// Converts a Rust string to a null-terminated UTF-16 buffer for Win32 APIs.
    fn to_wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    /// Creates a manual-reset event used to signal overlapped completion.
    fn create_event() -> Result<SendHandle> {
        let event = unsafe { CreateEventW(std::ptr::null(), 1, 0, std::ptr::null()) };
        if event.is_null() {
            bail!("CreateEventW failed with Win32 error {}", unsafe { GetLastError() });
        }
        Ok(SendHandle(event))
    }

    /// Opens the TAP device with overlapped I/O enabled.
    fn open_device(device_path: &str) -> Result<HANDLE> {
        let wide = to_wide(device_path);
        let handle = unsafe {
            CreateFileW(
                wide.as_ptr(),
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                std::ptr::null(),
                OPEN_EXISTING,
                FILE_ATTRIBUTE_SYSTEM | FILE_FLAG_OVERLAPPED,
                std::ptr::null_mut(),
            )
        };
        if handle.is_null() || handle == INVALID_HANDLE_VALUE {
            let error = unsafe { GetLastError() };
            bail!("CreateFileW failed with Win32 error {error}");
        }
        Ok(handle)
    }

    /// Issues a buffered IOCTL against a handle opened with FILE_FLAG_OVERLAPPED and waits for it.
    /// Such handles reject a NULL OVERLAPPED, so one backed by an event object is always supplied.
    fn device_io_control(handle: HANDLE, control_code: u32, input: i32) -> Result<i32> {
        unsafe {
            let event = CreateEventW(std::ptr::null(), 1, 0, std::ptr::null());
            if event.is_null() {
                bail!("CreateEventW failed with Win32 error {}", GetLastError());
            }

            let result = (|| {
                let mut overlapped: OVERLAPPED = std::mem::zeroed();
                overlapped.hEvent = event;

                let input_value = input;
                let mut output_value: i32 = 0;
                let mut transferred: u32 = 0;

                let ok = DeviceIoControl(
                    handle,
                    control_code,
                    &input_value as *const i32 as *const _,
                    std::mem::size_of::<i32>() as u32,
                    &mut output_value as *mut i32 as *mut _,
                    std::mem::size_of::<i32>() as u32,
                    &mut transferred,
                    &mut overlapped,
                );

                if ok == 0 {
                    let error = GetLastError();
                    if error == ERROR_IO_PENDING {
                        if GetOverlappedResult(handle, &overlapped, &mut transferred, 1) == 0 {
                            bail!(
                                "DeviceIoControl overlapped wait failed with Win32 error {}",
                                GetLastError()
                            );
                        }
                    } else {
                        bail!("DeviceIoControl failed with Win32 error {error}");
                    }
                }

                Ok(output_value)
            })();

            CloseHandle(event);
            result
        }
    }

    /// Resolves the NetCfgInstanceId (device GUID) of an adapter given its Windows connection name.
    ///
    /// The enumeration walks the network adapter class key and matches the friendly name stored
    /// under each instance's Connection subkey.
    pub(super) fn resolve_adapter_guid_by_name(connection_name: &str) -> Result<String> {
        use winreg::RegKey;
        use winreg::enums::HKEY_LOCAL_MACHINE;

        let adapter_key = format!(r"SYSTEM\CurrentControlSet\Control\Class\{ADAPTER_CLASS_GUID}");
        let connection_key =
            format!(r"SYSTEM\CurrentControlSet\Control\Network\{ADAPTER_CLASS_GUID}");

        let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
        let adapters = hklm
            .open_subkey(&adapter_key)
            .context("failed to open the network adapter class registry key")?;

        for index in adapters.enum_keys().flatten() {
            // Some device subkeys are ACL'd to SYSTEM/TrustedInstaller only; skip unreadable ones.
            let Ok(adapter) = adapters.open_subkey(&index) else { continue };
            let Ok(instance_id) = adapter.get_value::<String, _>("NetCfgInstanceId") else { continue };

            let connection_path = format!(r"{connection_key}\{instance_id}\Connection");
            let Ok(connection) = hklm.open_subkey(&connection_path) else { continue };
            let Ok(name) = connection.get_value::<String, _>("Name") else { continue };

            if name.eq_ignore_ascii_case(connection_name) {
                return Ok(instance_id);
            }
        }

        bail!(
            "no network adapter named '{connection_name}' was found; \
             check the adapter name in Network Connections"
        )
    }
}
