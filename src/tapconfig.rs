//! Windows-side IP configuration of the TAP adapter at startup.
//!
//! The bridge already runs elevated, so every step is executed directly instead of being printed
//! for the operator. All steps are best-effort: a failure is logged but does not stop the bridge.

use anyhow::Result;
use tracing::{info, warn};

use crate::config::{BridgeConfig, TapAddressMode};

/// Applies the configured MTU, addressing mode, route isolation and binding policy to the TAP
/// adapter. No-op on non-Windows targets.
#[cfg(windows)]
pub async fn apply(config: &BridgeConfig, tap_mtu: u16) -> Result<()> {
    let adapter = &config.tap_adapter_name;

    if config.enforce_tap_mtu {
        run(
            "netsh",
            &[
                "interface",
                "ipv4",
                "set",
                "subinterface",
                adapter,
                &format!("mtu={tap_mtu}"),
                "store=persistent",
            ],
        )
        .await;
        info!(mtu = tap_mtu, "TAP MTU applied");
    }

    match config.tap_address_mode {
        TapAddressMode::Dhcp => {
            run(
                "netsh",
                &["interface", "ipv4", "set", "address", adapter, "source=dhcp"],
            )
            .await;
            info!("TAP address mode: DHCP (leases from the remote segment)");
        }
        TapAddressMode::Static => {
            let address = config.tap_static_address.as_deref().unwrap_or_default();
            let mask = prefix_to_mask(config.tap_static_prefix_length);
            // No gateway argument: the industrial segment must never become the default route.
            run(
                "netsh",
                &["interface", "ipv4", "set", "address", adapter, "static", address, &mask],
            )
            .await;
            info!(%address, prefix = config.tap_static_prefix_length, "TAP address mode: static (no gateway)");
        }
        TapAddressMode::Manual => {}
    }

    if config.isolate_tap_routing {
        // Without this a DHCP-supplied default route can hijack the host's traffic, including the
        // WireGuard tunnel this bridge depends on.
        run(
            "netsh",
            &[
                "interface",
                "ipv4",
                "set",
                "interface",
                adapter,
                "ignoredefaultroutes=enabled",
                "metric=9999",
            ],
        )
        .await;
        info!("TAP routing isolated: default routes ignored, interface metric 9999");
    }

    if config.disable_ipv6_on_tap {
        let escaped = adapter.replace('\'', "''");
        run(
            "powershell.exe",
            &[
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                &format!(
                    "Disable-NetAdapterBinding -Name '{escaped}' \
                     -ComponentID ms_tcpip6,ms_lltdio,ms_rspndr -ErrorAction SilentlyContinue"
                ),
            ],
        )
        .await;
        info!("IPv6 and link-layer discovery bindings disabled on the TAP adapter");
    }

    Ok(())
}

/// No-op on non-Windows targets.
#[cfg(not(windows))]
pub async fn apply(_config: &BridgeConfig, _tap_mtu: u16) -> Result<()> {
    Ok(())
}

/// Converts a prefix length to a dotted-decimal netmask (e.g. 24 -> 255.255.255.0).
#[cfg(windows)]
fn prefix_to_mask(prefix_length: u8) -> String {
    let mask: u32 = if prefix_length == 0 { 0 } else { u32::MAX << (32 - prefix_length) };
    format!(
        "{}.{}.{}.{}",
        (mask >> 24) & 0xFF,
        (mask >> 16) & 0xFF,
        (mask >> 8) & 0xFF,
        mask & 0xFF
    )
}

/// Runs a configuration command and logs a warning on failure instead of aborting the bridge.
#[cfg(windows)]
async fn run(program: &str, args: &[&str]) {
    let result = tokio::process::Command::new(program)
        .args(args)
        .output()
        .await;

    match result {
        Ok(output) if !output.status.success() => {
            warn!(
                program,
                status = %output.status,
                stderr = %String::from_utf8_lossy(&output.stderr).trim(),
                "adapter configuration command failed"
            );
        }
        Err(err) => {
            warn!(program, error = %err, "adapter configuration command failed to start");
        }
        _ => {}
    }
}
