mod bridge;
mod config;
mod encapsulation;
mod filter;
mod loopdetect;
mod mactable;
mod netbird;
mod provision;
mod tap;
mod tapconfig;
mod transport;
mod tunnelif;

use std::net::Ipv4Addr;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::Duration;

use anyhow::{Context, Result};
use clap::Parser;
use tokio::signal;
use tokio_util::sync::CancellationToken;
use tracing::{info, warn};

use crate::bridge::BridgeEngine;
use crate::config::{BridgeConfig, EncapsulationMode, PeerDiscoveryMode};
use crate::encapsulation::{Encapsulation, GretapEncapsulation, RawEncapsulation, VxlanEncapsulation};
use crate::filter::FrameFilter;
use crate::loopdetect::LoopDetector;
use crate::mactable::MacTable;
use crate::netbird::NetbirdStatus;
use crate::tap::TapPort;
use crate::transport::UdpTunnel;

/// Command-line arguments for the bridge process.
///
/// The bridge intentionally keeps its CLI small. Runtime behavior is driven by the JSON config,
/// while the CLI only selects where that config lives.
#[derive(Debug, Parser)]
#[command(name = "wgl2bridge-rust")]
#[command(about = "Layer 2 bridge over an encrypted tunnel")]
struct Cli {
    /// Path to bridge configuration JSON.
    #[arg(default_value = "bridge.config.json")]
    config: PathBuf,
}

#[tokio::main]
async fn main() -> Result<()> {
    // Initialize structured logging. Default to info so the bridge always prints its lifecycle
    // and stats lines; RUST_LOG overrides (e.g. RUST_LOG=debug for packet-path detail).
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info")),
        )
        .init();

    let cli = Cli::parse();
    let config = BridgeConfig::load(&cli.config)
        .with_context(|| format!("failed to load {:?}", cli.config))?;

    // Raw TAP device access requires administrator rights; warn early so the failure is obvious.
    if !provision::is_elevated() {
        warn!("not running elevated; opening the raw TAP device requires administrator rights");
    }

    // ---- 1. Tunnel endpoint discovery ----
    // Under NetBird discovery, both tunnel IPs come from `netbird status --json`; otherwise the
    // literal addresses in the config are used.
    let (local_ip, remote_ip) = resolve_tunnel_endpoints(&config).await?;
    let port = config.effective_port();

    // Locate the tunnel adapter itself: the socket binds to its IP and the TAP MTU derives from
    // its real MTU. The address match is the fallback when the adapter name differs from config.
    let tunnel_if = tunnelif::find(
        config.wire_guard_adapter_name.as_deref().unwrap_or("wt0"),
        Some(local_ip),
    )?;
    info!(
        adapter = %tunnel_if.name,
        mtu = tunnel_if.mtu,
        "tunnel adapter located"
    );
    if tunnel_if.mtu == 0 {
        warn!("tunnel adapter '{}' reported no MTU; the link may be down", tunnel_if.name);
    }

    // The UDP tunnel is the shared transport for all encapsulated Ethernet frames. It is bound to
    // the tunnel IP, so encapsulated frames cannot leak onto the physical LAN.
    let tunnel = UdpTunnel::bind(local_ip, port, remote_ip, port).await?;
    let tunnel = Arc::new(tunnel);

    // ---- 2. Encapsulation and policy layers ----
    let encapsulation: Arc<dyn Encapsulation> = match config.encapsulation_mode {
        EncapsulationMode::Raw => Arc::new(RawEncapsulation),
        EncapsulationMode::Vxlan => Arc::new(VxlanEncapsulation::new(config.vxlan_vni)),
        EncapsulationMode::Gretap => Arc::new(GretapEncapsulation::new(config.gre_key)),
    };

    let filter = FrameFilter::new(config.enable_filter);
    let mac_table = config
        .enable_mac_learning
        .then(|| MacTable::new(Duration::from_secs(config.mac_aging_seconds)));
    let loop_detector = config
        .enable_loop_detection
        .then(|| Arc::new(LoopDetector::new()));

    // ---- 3. Local Ethernet side ----
    let tap = open_tap(&config)?;

    // Apply the TAP-side IP configuration (MTU, addressing mode, route isolation). The MTU is
    // derived from the real tunnel adapter MTU: tunnel MTU - outer header - inner Ethernet header.
    let tap_mtu = config.effective_tap_mtu(tunnel_if.mtu as u16);
    info!(tap_mtu, tunnel_mtu = tunnel_if.mtu, "derived TAP MTU");

    // Provision the TAP adapter when missing and allowed, then apply its configuration.
    provision::ensure_adapter_exists(&config, tap_mtu).await?;
    tapconfig::apply(&config, tap_mtu).await?;

    let engine = BridgeEngine::new(tap.clone(), tunnel.clone(), encapsulation, filter, mac_table, loop_detector.clone());

    info!(
        vni = config.vxlan_vni,
        mode = ?config.encapsulation_mode,
        %local_ip,
        %remote_ip,
        port,
        "bridge running"
    );

    // ---- 4. Background tasks ----
    let shutdown = CancellationToken::new();

    // Ctrl+C cancels the token, which drains the tunnel pump and stops the loop detector.
    let ctrl_c = shutdown.clone();
    tokio::spawn(async move {
        if signal::ctrl_c().await.is_ok() {
            info!("shutdown requested");
            ctrl_c.cancel();
        }
    });

    // Periodic frame counters, matching the reference implementation's out=/in=/dropped= line.
    let stats = engine.stats();
    let stats_shutdown = shutdown.clone();
    let stats_task = tokio::spawn(async move {
        let mut interval = tokio::time::interval(Duration::from_secs(30));
        interval.tick().await; // Skip the immediate first tick.
        loop {
            tokio::select! {
                _ = stats_shutdown.cancelled() => return,
                _ = interval.tick() => {
                    info!(
                        out = stats.frames_to_tunnel.load(Ordering::Relaxed),
                        inbound = stats.frames_to_tap.load(Ordering::Relaxed),
                        dropped = stats.frames_dropped.load(Ordering::Relaxed),
                        "bridge stats"
                    );
                }
            }
        }
    });

    // Loop probes, when enabled.
    let probe_task = loop_detector.map(|detector| {
        tokio::spawn(detector.run(
            tap,
            tunnel,
            engine.tunnel_header(),
            Duration::from_secs(config.loop_probe_seconds),
            config.stop_on_loop_detected,
            shutdown.clone(),
        ))
    });

    // ---- 5. Run until a pump fails or shutdown fires ----
    let result = engine.run(shutdown.clone()).await;
    shutdown.cancel();
    stats_task.abort();
    if let Some(task) = probe_task {
        task.abort();
    }

    if let Err(err) = result {
        warn!(error = %err, "bridge stopped");
    }
    Ok(())
}

/// Resolves the local and remote tunnel IPs, either from NetBird or from literal config values.
async fn resolve_tunnel_endpoints(config: &BridgeConfig) -> Result<(Ipv4Addr, Ipv4Addr)> {
    match config.peer_discovery {
        PeerDiscoveryMode::Netbird => {
            let status = NetbirdStatus::query(config.netbird_cli_path.as_deref()).await?;

            let peer = if config.remote_peer.trim().is_empty() {
                status.select_peer_interactively()?
            } else {
                status.resolve_peer(config.remote_peer.trim())?
            };

            info!(
                local = %status.local_fqdn,
                local_ip = %status.local_ip,
                peer = %peer.fqdn,
                peer_ip = %peer.address,
                peer_status = %peer.status,
                "NetBird tunnel endpoints resolved"
            );

            Ok((status.local_ip, peer.address))
        }
        PeerDiscoveryMode::None => {
            let local = config
                .local_tunnel_ip
                .parse::<Ipv4Addr>()
                .context("invalid local_tunnel_ip")?;
            let remote = config
                .remote_peer
                .parse::<Ipv4Addr>()
                .context("remote_peer must be an IPv4 address when peer_discovery is None")?;
            Ok((local, remote))
        }
    }
}

/// Opens the TAP adapter that carries the local Ethernet side of the bridge.
#[cfg(windows)]
fn open_tap(config: &BridgeConfig) -> Result<Arc<dyn TapPort>> {
    let tap = crate::tap::WindowsTapPort::open(&config.tap_adapter_name)
        .context("failed to open the TAP adapter")?;
    info!(
        adapter = %tap.adapter_name(),
        guid = %tap.device_guid(),
        mtu = ?tap.driver_mtu(),
        "TAP adapter opened"
    );
    Ok(Arc::new(tap))
}

/// Non-Windows fallback: the TAP driver integration is Windows-only.
#[cfg(not(windows))]
fn open_tap(_config: &BridgeConfig) -> Result<Arc<dyn TapPort>> {
    warn!("TAP device access is only implemented on Windows; using a null port");
    Ok(Arc::new(crate::tap::NullTapPort))
}
