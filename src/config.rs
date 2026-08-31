use std::fs;
use std::net::Ipv4Addr;
use std::path::Path;

use anyhow::{bail, Context, Result};
use serde::Deserialize;

/// Source of the local and remote tunnel IPs.
#[derive(Debug, Clone, Copy, Default, Deserialize)]
pub enum PeerDiscoveryMode {
    /// Use the literal addresses from the configuration file.
    None,
    /// Resolve the local and remote tunnel IPs from `netbird status --json`.
    #[default]
    Netbird,
}

/// Wire format used inside the tunnel.
#[derive(Debug, Clone, Copy, Default, Deserialize)]
pub enum EncapsulationMode {
    /// Bare Ethernet frame inside UDP; only interoperable with another instance of this bridge.
    Raw,
    /// RFC 7348 VXLAN over UDP/4789; interoperable with Linux `ip link add type vxlan`.
    #[default]
    Vxlan,
    /// RFC 2784/2890 GRE with protocol type 0x6558 (Transparent Ethernet Bridging).
    Gretap,
}

impl EncapsulationMode {
    /// UDP port in force for the selected mode when no override is configured.
    pub fn default_port(self) -> u16 {
        match self {
            Self::Vxlan => 4789,
            Self::Raw => 55555,
            Self::Gretap => 4789,
        }
    }

    /// Bytes of tunnel header in front of the inner Ethernet frame.
    pub fn header_size(self) -> usize {
        match self {
            Self::Raw => 0,
            Self::Vxlan => 8,
            Self::Gretap => 4,
        }
    }
}

/// How the TAP adapter's IP configuration is managed at startup.
#[derive(Debug, Clone, Copy, Default, Deserialize)]
pub enum TapAddressMode {
    /// Leave the adapter's IP configuration untouched.
    #[default]
    Manual,
    /// Request an address from a DHCP server on the bridged segment.
    Dhcp,
    /// Apply the static address from the configuration.
    Static,
}

fn default_tap_adapter() -> String {
    "Industrial-TAP".to_owned()
}
fn default_vni() -> u32 {
    4096
}
fn default_true() -> bool {
    true
}
fn default_mac_aging() -> u64 {
    300
}
fn default_loop_probe() -> u64 {
    5
}
fn default_prefix_length() -> u8 {
    24
}

/// Top-level bridge configuration, loadable from a JSON document on disk.
///
/// Field names match the C# reference configuration one-to-one (camelCase in JSON).
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct BridgeConfig {
    // ---- Core identity and tunnel endpoints ----

    /// Windows connection name of the virtual TAP adapter that supplies raw Layer 2 frames.
    #[serde(default = "default_tap_adapter")]
    pub tap_adapter_name: String,

    /// Source of the local and remote tunnel IPs: None (literal values) or Netbird.
    pub peer_discovery: PeerDiscoveryMode,

    /// Remote bridge peer: NetBird FQDN, short hostname, or tunnel IP. Empty prompts for
    /// interactive selection when discovery is enabled; must be an IP when discovery is None.
    pub remote_peer: String,

    /// Explicit local tunnel IP; only used when `peer_discovery` is None.
    pub local_tunnel_ip: String,

    /// Tunnel adapter connection name. Optional under NetBird discovery; required when
    /// `peer_discovery` is None.
    pub wire_guard_adapter_name: Option<String>,

    // ---- Encapsulation ----

    /// Wire format used inside the tunnel: Raw, Vxlan or Gretap.
    pub encapsulation_mode: EncapsulationMode,

    /// UDP port for Raw/VXLAN. 0 selects the mode default (Raw 55555, VXLAN 4789).
    pub encapsulation_port: u16,

    /// 24-bit VXLAN network identifier; must match the VNI on the peer.
    #[serde(default = "default_vni")]
    pub vxlan_vni: u32,

    /// Optional 32-bit GRE key (RFC 2890). 0 disables the key field.
    pub gre_key: u32,

    // ---- Filtering, learning and loop protection ----

    /// Enables the broad industrial pass / consumer-noise drop filter.
    #[serde(default = "default_true")]
    pub enable_filter: bool,

    /// Learns MAC addresses so known-local unicast frames are not flooded across the tunnel.
    #[serde(default = "default_true")]
    pub enable_mac_learning: bool,

    /// MAC entry lifetime in seconds before an address is considered unknown again.
    #[serde(default = "default_mac_aging")]
    pub mac_aging_seconds: u64,

    /// Emits periodic probe frames and reports when one returns, indicating a Layer 2 loop.
    #[serde(default = "default_true")]
    pub enable_loop_detection: bool,

    /// Probe interval in seconds.
    #[serde(default = "default_loop_probe")]
    pub loop_probe_seconds: u64,

    /// Stops the bridge when a loop is detected, instead of only logging it.
    #[serde(default = "default_true")]
    pub stop_on_loop_detected: bool,

    // ---- TAP adapter self-configuration ----

    /// Forces a TAP MTU instead of deriving it from the tunnel MTU and encapsulation overhead.
    pub tap_mtu_override: Option<u16>,

    /// Applies the derived/overridden MTU to the TAP adapter at startup.
    #[serde(default = "default_true")]
    pub enforce_tap_mtu: bool,

    /// How the TAP adapter's IP configuration is managed: Manual, Dhcp or Static.
    pub tap_address_mode: TapAddressMode,

    /// Static address applied when `tap_address_mode` is Static.
    pub tap_static_address: Option<String>,

    /// Prefix length for the static address.
    #[serde(default = "default_prefix_length")]
    pub tap_static_prefix_length: u8,

    /// Ignores default routes on the TAP adapter so a DHCP gateway cannot hijack host traffic.
    #[serde(default = "default_true")]
    pub isolate_tap_routing: bool,

    /// Unbinds IPv6 and link-layer discovery from the TAP adapter to cut broadcast noise.
    pub disable_ipv6_on_tap: bool,

    // ---- Provisioning and discovery helpers ----

    /// When true, a missing TAP adapter is provisioned via tapctl.exe (requires elevation).
    pub auto_create_tap_adapter: bool,

    /// Optional explicit path to netbird.exe; discovered automatically when empty.
    pub netbird_cli_path: Option<String>,
}

impl Default for BridgeConfig {
    fn default() -> Self {
        Self {
            tap_adapter_name: default_tap_adapter(),
            peer_discovery: PeerDiscoveryMode::default(),
            remote_peer: String::new(),
            local_tunnel_ip: String::new(),
            wire_guard_adapter_name: None,
            encapsulation_mode: EncapsulationMode::default(),
            encapsulation_port: 0,
            vxlan_vni: default_vni(),
            gre_key: 0,
            enable_filter: true,
            enable_mac_learning: true,
            mac_aging_seconds: default_mac_aging(),
            enable_loop_detection: true,
            loop_probe_seconds: default_loop_probe(),
            stop_on_loop_detected: true,
            tap_mtu_override: None,
            enforce_tap_mtu: true,
            tap_address_mode: TapAddressMode::default(),
            tap_static_address: None,
            tap_static_prefix_length: default_prefix_length(),
            isolate_tap_routing: true,
            disable_ipv6_on_tap: false,
            auto_create_tap_adapter: false,
            netbird_cli_path: None,
        }
    }
}

impl BridgeConfig {
    /// Loads and validates the bridge configuration from disk.
    pub fn load(path: &Path) -> Result<Self> {
        let text = fs::read_to_string(path)
            .with_context(|| format!("configuration file not found: {}", path.display()))?;
        let config: Self = serde_json::from_str(&text)
            .with_context(|| format!("configuration file '{}' is empty or invalid", path.display()))?;
        config.validate()?;
        Ok(config)
    }

    /// UDP port in force for the selected encapsulation mode.
    pub fn effective_port(&self) -> u16 {
        match self.encapsulation_port {
            0 => self.encapsulation_mode.default_port(),
            port => port,
        }
    }

    /// TAP MTU actually in force: the override, or the value derived from the tunnel MTU.
    ///
    /// TAP MTU = tunnel MTU − outer header − 14 (inner Ethernet). The outer header is 20 (IPv4)
    /// + 8 (UDP) + encapsulation header. The WireGuard/NetBird overhead is already reflected in
    /// the tunnel adapter's own MTU, so it is not double-counted.
    pub fn effective_tap_mtu(&self, tunnel_mtu: u16) -> u16 {
        if let Some(override_mtu) = self.tap_mtu_override {
            return override_mtu;
        }
        let derived = tunnel_mtu
            .saturating_sub(28) // IPv4 + UDP
            .saturating_sub(self.encapsulation_mode.header_size() as u16)
            .saturating_sub(14); // inner Ethernet header
        derived.max(576)
    }

    fn validate(&self) -> Result<()> {
        if self.tap_adapter_name.trim().is_empty() {
            bail!("tap_adapter_name must be set");
        }

        if matches!(self.peer_discovery, PeerDiscoveryMode::None) {
            if self.local_tunnel_ip.trim().is_empty() {
                bail!("local_tunnel_ip must be set when peer_discovery is None");
            }
            if self.remote_peer.trim().is_empty() {
                bail!("remote_peer must be the tunnel IP of the remote bridge when peer_discovery is None");
            }
            if self.wire_guard_adapter_name.as_deref().unwrap_or("").trim().is_empty() {
                bail!("wire_guard_adapter_name must be set when peer_discovery is None");
            }
        }

        if self.effective_port() == 0 {
            bail!("encapsulation_port must be between 1 and 65535");
        }

        if self.vxlan_vni > 0xFF_FFFF {
            bail!("vxlan_vni must be a 24-bit value (0..16777215)");
        }

        if let Some(mtu) = self.tap_mtu_override {
            if !(576..=9000).contains(&mtu) {
                bail!("tap_mtu_override must be between 576 and 9000");
            }
        }

        if matches!(self.tap_address_mode, TapAddressMode::Static) {
            let address = self.tap_static_address.as_deref().unwrap_or("");
            if address.parse::<Ipv4Addr>().is_err() {
                bail!("tap_static_address must be a valid IPv4 address when tap_address_mode is Static");
            }
            if !(1..=32).contains(&self.tap_static_prefix_length) {
                bail!("tap_static_prefix_length must be between 1 and 32");
            }
        }

        if self.mac_aging_seconds < 1 {
            bail!("mac_aging_seconds must be at least 1");
        }
        if self.loop_probe_seconds < 1 {
            bail!("loop_probe_seconds must be at least 1");
        }
        Ok(())
    }
}
