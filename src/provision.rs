//! Preflight provisioning for the TAP-Windows6 adapter.
//!
//! Detects a missing adapter and either creates it with tapctl.exe (when auto-provisioning is
//! enabled and the process is elevated) or prints the exact commands the operator must run.

use std::path::Path;

use anyhow::{bail, Result};
use tracing::{info, warn};

use crate::config::BridgeConfig;

/// tapctl.exe locations checked before falling back to PATH.
const TAPCTL_SEARCH_PATHS: [&str; 4] = [
    r"C:\Program Files\OpenVPN\bin\tapctl.exe",
    r"C:\Program Files (x86)\OpenVPN\bin\tapctl.exe",
    r"C:\Program Files\TAP-Windows\bin\tapctl.exe",
    r"C:\Program Files (x86)\TAP-Windows\bin\tapctl.exe",
];

/// Whether the current process is running with administrator rights.
///
/// Raw TAP device access requires elevation; the bridge warns early so the failure is obvious.
#[cfg(windows)]
pub fn is_elevated() -> bool {
    use windows_sys::Win32::Foundation::{CloseHandle, HANDLE};
    use windows_sys::Win32::Security::{
        GetTokenInformation, TokenElevation, TOKEN_ELEVATION, TOKEN_QUERY,
    };
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

    unsafe {
        let mut token: HANDLE = std::ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return false;
        }

        let mut elevation: TOKEN_ELEVATION = std::mem::zeroed();
        let mut returned: u32 = 0;
        let ok = GetTokenInformation(
            token,
            TokenElevation,
            &mut elevation as *mut _ as *mut _,
            std::mem::size_of::<TOKEN_ELEVATION>() as u32,
            &mut returned,
        );
        CloseHandle(token);

        ok != 0 && elevation.TokenIsElevated != 0
    }
}

/// Non-Windows fallback: the concept does not apply.
#[cfg(not(windows))]
pub fn is_elevated() -> bool {
    true
}

/// Ensures the configured TAP adapter exists, provisioning it when allowed.
#[cfg(windows)]
pub async fn ensure_adapter_exists(config: &BridgeConfig, tap_mtu: u16) -> Result<()> {
    if crate::tap::adapter_exists(&config.tap_adapter_name) {
        return Ok(());
    }

    info!(adapter = %config.tap_adapter_name, "TAP adapter does not exist");

    let tapctl = find_tapctl();

    if !config.auto_create_tap_adapter || tapctl.is_none() || !is_elevated() {
        print_manual_instructions(config, tap_mtu, tapctl.as_deref());
        bail!(
            "TAP adapter '{}' is missing and could not be created automatically",
            config.tap_adapter_name
        );
    }

    let tapctl = tapctl.unwrap();
    info!(%tapctl, "creating TAP adapter");
    run(&tapctl, &["create", "--name", &config.tap_adapter_name]).await?;

    // The device node takes a moment to register before its registry entries are readable.
    for _ in 0..20 {
        if crate::tap::adapter_exists(&config.tap_adapter_name) {
            info!(adapter = %config.tap_adapter_name, "TAP adapter created");

            run(
                "netsh",
                &["interface", "set", "interface", &format!("name={}", config.tap_adapter_name), "admin=enabled"],
            )
            .await?;
            run(
                "netsh",
                &[
                    "interface",
                    "ipv4",
                    "set",
                    "subinterface",
                    &config.tap_adapter_name,
                    &format!("mtu={tap_mtu}"),
                    "store=persistent",
                ],
            )
            .await?;

            info!(mtu = tap_mtu, "adapter enabled and MTU set");
            return Ok(());
        }

        tokio::time::sleep(std::time::Duration::from_millis(500)).await;
    }

    bail!(
        "tapctl reported success but adapter '{}' did not appear in the registry",
        config.tap_adapter_name
    )
}

/// Non-Windows fallback: no TAP provisioning.
#[cfg(not(windows))]
pub async fn ensure_adapter_exists(_config: &BridgeConfig, _tap_mtu: u16) -> Result<()> {
    Ok(())
}

/// Prints the exact commands the operator must run to provision the adapter manually.
#[cfg(windows)]
fn print_manual_instructions(config: &BridgeConfig, tap_mtu: u16, tapctl: Option<&str>) {
    warn!("run the following in an ELEVATED PowerShell to provision the adapter:");

    match tapctl {
        None => {
            warn!("  1. Install the OpenVPN 'TAP Virtual Ethernet Adapter' component (tap-windows6 driver)");
            warn!("     Note: WireGuard's Wintun adapter is Layer 3 only and cannot be used as the source TAP");
            warn!(
                "  2. & \"C:\\Program Files\\OpenVPN\\bin\\tapctl.exe\" create --name \"{}\"",
                config.tap_adapter_name
            );
        }
        Some(path) => {
            warn!("  & \"{path}\" create --name \"{}\"", config.tap_adapter_name);
        }
    }

    warn!(
        "  netsh interface ipv4 set subinterface \"{}\" mtu={} store=persistent",
        config.tap_adapter_name, tap_mtu
    );
    warn!("  Enable-NetAdapter -Name \"{}\"", config.tap_adapter_name);
    warn!("alternatively set \"autoCreateTapAdapter\": true in the configuration and run this bridge elevated");

    if !is_elevated() {
        warn!("this process is NOT running elevated; raw TAP access requires administrator rights");
    }
}

/// Locates tapctl.exe in the well-known install locations or on PATH.
#[cfg(windows)]
fn find_tapctl() -> Option<String> {
    for candidate in TAPCTL_SEARCH_PATHS {
        if Path::new(candidate).exists() {
            return Some(candidate.to_owned());
        }
    }

    if let Ok(path_var) = std::env::var("PATH") {
        for directory in path_var.split(';').filter(|d| !d.trim().is_empty()) {
            let candidate = Path::new(directory.trim()).join("tapctl.exe");
            if candidate.exists() {
                return Some(candidate.to_string_lossy().into_owned());
            }
        }
    }

    None
}

/// Runs a provisioning command; unlike the configurator, failures here abort the bridge.
#[cfg(windows)]
async fn run(program: &str, args: &[&str]) -> Result<()> {
    let output = tokio::process::Command::new(program).args(args).output().await?;

    let stdout = String::from_utf8_lossy(&output.stdout);
    if !stdout.trim().is_empty() {
        info!(program, output = %stdout.trim(), "provisioning command output");
    }

    if !output.status.success() {
        bail!(
            "'{} {}' failed with {}. {}",
            program,
            args.join(" "),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    Ok(())
}
