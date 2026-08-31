//! Tunnel adapter discovery via the native Win32 IP Helper API.
//!
//! Locates the WireGuard/NetBird tunnel adapter by connection name, description, or tunnel IP,
//! and exposes its MTU and local tunnel address. This mirrors the C# `WireGuardInterface` helper:
//! binding the encapsulation socket to the tunnel IP guarantees egress through the encrypted
//! tunnel only.

use std::net::Ipv4Addr;

use anyhow::{bail, Context, Result};

/// A network adapter as seen by the IP Helper API.
#[derive(Debug, Clone)]
pub struct TunnelInterface {
    /// Windows connection name (as shown in Network Connections).
    pub name: String,
    /// Adapter description (driver-provided friendly name).
    pub description: String,
    /// Interface MTU in bytes; 0 when the system did not report one.
    pub mtu: u32,
    /// First IPv4 unicast address assigned to the adapter, if any.
    pub ipv4: Option<Ipv4Addr>,
}

/// Finds the tunnel adapter by name or description, falling back to a tunnel IP match.
///
/// NetBird/WireGuard may name the interface differently than configured, so the address match is
/// the reliable fallback when discovery resolved the tunnel IP already.
#[cfg(windows)]
pub fn find(adapter_name: &str, fallback_address: Option<Ipv4Addr>) -> Result<TunnelInterface> {
    let adapters = enumerate_adapters()?;

    for adapter in &adapters {
        if adapter.name.eq_ignore_ascii_case(adapter_name)
            || adapter.description.eq_ignore_ascii_case(adapter_name)
        {
            return Ok(adapter.clone());
        }
    }

    if let Some(address) = fallback_address {
        for adapter in &adapters {
            if adapter.ipv4 == Some(address) {
                return Ok(adapter.clone());
            }
        }
    }

    bail!(
        "WireGuard adapter '{adapter_name}' was not found. Start the tunnel before launching the bridge."
    )
}

/// Non-Windows fallback: tunnel adapter discovery is not implemented off Windows.
#[cfg(not(windows))]
pub fn find(adapter_name: &str, _fallback_address: Option<Ipv4Addr>) -> Result<TunnelInterface> {
    let _ = adapter_name;
    bail!("tunnel adapter discovery is only implemented on Windows")
}

/// Returns the IPv4 address assigned to the tunnel adapter.
///
/// Kept as public API parity with the C# helper; used when the bind address must come from the
/// adapter rather than from config or NetBird discovery.
#[allow(dead_code)]
pub fn local_tunnel_address(tunnel: &TunnelInterface) -> Result<Ipv4Addr> {
    tunnel.ipv4.with_context(|| {
        format!(
            "WireGuard adapter '{}' has no IPv4 address assigned.",
            tunnel.name
        )
    })
}

#[cfg(windows)]
fn enumerate_adapters() -> Result<Vec<TunnelInterface>> {
    use windows_sys::Win32::Foundation::{ERROR_BUFFER_OVERFLOW, NO_ERROR};
    use windows_sys::Win32::NetworkManagement::IpHelper::{
        GetAdaptersAddresses, IP_ADAPTER_ADDRESSES_LH, GAA_FLAG_INCLUDE_PREFIX,
    };
    use windows_sys::Win32::Networking::WinSock::{AF_INET, AF_UNSPEC, SOCKADDR_IN};

    // Two-call pattern: first query sizes the buffer, the second fills it.
    let mut size: u32 = 0;
    unsafe {
        GetAdaptersAddresses(
            AF_UNSPEC as u32,
            GAA_FLAG_INCLUDE_PREFIX,
            std::ptr::null(),
            std::ptr::null_mut(),
            &mut size,
        );
    }
    if size == 0 {
        bail!("GetAdaptersAddresses reported no adapters");
    }

    let mut buffer = vec![0u8; size as usize];
    let result = unsafe {
        GetAdaptersAddresses(
            AF_UNSPEC as u32,
            GAA_FLAG_INCLUDE_PREFIX,
            std::ptr::null(),
            buffer.as_mut_ptr() as *mut IP_ADAPTER_ADDRESSES_LH,
            &mut size,
        )
    };
    if result != NO_ERROR && result != ERROR_BUFFER_OVERFLOW {
        bail!("GetAdaptersAddresses failed with Win32 error {result}");
    }

    let mut adapters = Vec::new();
    let mut current = buffer.as_ptr() as *const IP_ADAPTER_ADDRESSES_LH;

    while !current.is_null() {
        let adapter = unsafe { &*current };

        let name = wide_to_string(adapter.FriendlyName);
        let description = wide_to_string(adapter.Description);

        // Walk the unicast address list for the first IPv4 entry.
        let mut ipv4 = None;
        let mut address = adapter.FirstUnicastAddress;
        while !address.is_null() {
            let unicast = unsafe { &*address };
            let sockaddr = unicast.Address.lpSockaddr;
            if !sockaddr.is_null() && unsafe { (*sockaddr).sa_family } == AF_INET {
                let addr_in = unsafe { &*(sockaddr as *const SOCKADDR_IN) };
                // sin_addr is stored in network byte order; S_un is a union, hence the unsafe read.
                let raw = unsafe { addr_in.sin_addr.S_un.S_addr };
                ipv4 = Some(Ipv4Addr::from(u32::from_be(raw)));
                break;
            }
            address = unicast.Next;
        }

        adapters.push(TunnelInterface {
            name,
            description,
            mtu: adapter.Mtu,
            ipv4,
        });

        current = adapter.Next;
    }

    Ok(adapters)
}

/// Converts a null-terminated UTF-16 Win32 string to a Rust String.
#[cfg(windows)]
fn wide_to_string(ptr: *const u16) -> String {
    if ptr.is_null() {
        return String::new();
    }
    unsafe {
        let mut len = 0;
        while *ptr.add(len) != 0 {
            len += 1;
        }
        String::from_utf16_lossy(std::slice::from_raw_parts(ptr, len))
    }
}
