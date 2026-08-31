//! NetBird peer discovery.
//!
//! Reads the local NetBird client state via `netbird status --json` so the bridge can bind to the
//! NetBird tunnel IP and resolve a peer's tunnel IP by FQDN instead of hard-coding addresses.

use std::net::Ipv4Addr;
use std::path::Path;

use anyhow::{bail, Context, Result};
use serde::Deserialize;

/// One peer in the NetBird mesh.
#[derive(Debug, Clone)]
pub struct NetbirdPeer {
    pub fqdn: String,
    pub address: Ipv4Addr,
    pub status: String,
}

/// Local NetBird client state.
#[derive(Debug)]
pub struct NetbirdStatus {
    pub local_ip: Ipv4Addr,
    pub local_fqdn: String,
    pub peers: Vec<NetbirdPeer>,
}

#[derive(Deserialize)]
struct StatusJson {
    #[serde(rename = "netbirdIp")]
    netbird_ip: String,
    #[serde(default)]
    fqdn: String,
    #[serde(default)]
    peers: Option<PeersJson>,
}

#[derive(Default, Deserialize)]
struct PeersJson {
    #[serde(default)]
    details: Option<Vec<PeerJson>>,
}

#[derive(Deserialize)]
struct PeerJson {
    #[serde(rename = "netbirdIp")]
    netbird_ip: String,
    #[serde(default)]
    fqdn: String,
    #[serde(default)]
    status: String,
}

impl NetbirdStatus {
    /// Runs `netbird status --json` and parses the result.
    pub async fn query(cli_path: Option<&str>) -> Result<Self> {
        let executable = resolve_cli(cli_path)?;

        let output = tokio::process::Command::new(&executable)
            .args(["status", "--json"])
            .output()
            .await
            .with_context(|| format!("failed to start '{executable}'"))?;

        if !output.status.success() {
            bail!(
                "'netbird status --json' failed with {}. {}",
                output.status,
                String::from_utf8_lossy(&output.stderr).trim()
            );
        }

        Self::parse(std::str::from_utf8(&output.stdout).context("netbird status output is not UTF-8")?)
    }

    /// Parses the JSON document produced by `netbird status --json`.
    pub fn parse(json: &str) -> Result<Self> {
        let status: StatusJson = serde_json::from_str(json).context("invalid netbird status JSON")?;

        let local_ip = parse_address(&status.netbird_ip)
            .context("NetBird status contains no usable 'netbirdIp'; is the client connected?")?;

        let peers = status
            .peers
            .and_then(|p| p.details)
            .unwrap_or_default()
            .into_iter()
            .filter_map(|p| {
                parse_address(&p.netbird_ip).ok().map(|address| NetbirdPeer {
                    fqdn: p.fqdn,
                    address,
                    status: p.status,
                })
            })
            .collect();

        Ok(Self {
            local_ip,
            local_fqdn: status.fqdn,
            peers,
        })
    }

    /// Resolves a peer by full FQDN, short hostname, or tunnel IP.
    pub fn resolve_peer(&self, name_or_address: &str) -> Result<&NetbirdPeer> {
        for peer in &self.peers {
            if peer.fqdn.eq_ignore_ascii_case(name_or_address)
                || short_name(&peer.fqdn).eq_ignore_ascii_case(name_or_address)
                || peer.address.to_string() == name_or_address
            {
                return Ok(peer);
            }
        }

        let known = if self.peers.is_empty() {
            "(no peers reported)".to_owned()
        } else {
            self.peers
                .iter()
                .map(|p| format!("{} [{}]", p.fqdn, p.address))
                .collect::<Vec<_>>()
                .join(", ")
        };
        bail!("NetBird peer '{name_or_address}' was not found. Known peers: {known}");
    }

    /// Prompts the operator to pick a peer from the connected NetBird mesh.
    pub fn select_peer_interactively(&self) -> Result<&NetbirdPeer> {
        if self.peers.is_empty() {
            bail!("NetBird reports no peers to bridge to");
        }
        if self.peers.len() == 1 {
            return Ok(&self.peers[0]);
        }
        if !std::io::IsTerminal::is_terminal(&std::io::stdin()) {
            bail!("no remote peer configured and stdin is redirected; set 'remote_peer' in the configuration");
        }

        println!();
        println!("Select the remote bridge peer:");
        for (i, peer) in self.peers.iter().enumerate() {
            println!("  [{}] {:<40} {:<16} {}", i + 1, peer.fqdn, peer.address, peer.status);
        }

        let mut line = String::new();
        loop {
            print!("Peer [1-{}]: ", self.peers.len());
            use std::io::Write;
            std::io::stdout().flush()?;

            line.clear();
            if std::io::stdin().read_line(&mut line)? == 0 {
                bail!("peer selection aborted");
            }
            let input = line.trim();

            if let Ok(choice) = input.parse::<usize>() {
                if (1..=self.peers.len()).contains(&choice) {
                    return Ok(&self.peers[choice - 1]);
                }
            }
            if let Ok(peer) = self.resolve_peer(input) {
                return Ok(peer);
            }
            println!("Invalid selection.");
        }
    }
}

fn short_name(fqdn: &str) -> &str {
    match fqdn.find('.') {
        Some(dot) => &fqdn[..dot],
        None => fqdn,
    }
}

/// NetBird reports the local address in CIDR form ("100.99.58.65/16"); peers are bare IPs.
fn parse_address(value: &str) -> Result<Ipv4Addr> {
    let bare = match value.find('/') {
        Some(slash) => &value[..slash],
        None => value,
    };
    bare.parse::<Ipv4Addr>().with_context(|| format!("invalid IP address '{value}'"))
}

fn resolve_cli(configured: Option<&str>) -> Result<String> {
    if let Some(path) = configured.filter(|p| !p.trim().is_empty()) {
        if Path::new(path).exists() {
            return Ok(path.to_owned());
        }
        bail!("NetBird CLI not found at '{path}'");
    }

    const CANDIDATES: [&str; 3] = [
        r"C:\Program Files\Netbird\netbird.exe",
        r"C:\Program Files (x86)\Netbird\netbird.exe",
        r"C:\Program Files\NetBird\netbird.exe",
    ];
    for candidate in CANDIDATES {
        if Path::new(candidate).exists() {
            return Ok(candidate.to_owned());
        }
    }

    if let Ok(path_var) = std::env::var("PATH") {
        for directory in path_var.split(';').filter(|d| !d.trim().is_empty()) {
            let candidate = Path::new(directory.trim()).join("netbird.exe");
            if candidate.exists() {
                return Ok(candidate.to_string_lossy().into_owned());
            }
        }
    }

    bail!("netbird.exe was not found. Install the NetBird client or set 'netbird_cli_path' in the configuration.")
}
