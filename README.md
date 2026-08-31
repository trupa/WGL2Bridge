# WGL2Bridge-rust

Rust port of WGL2Bridge: a Layer 2 bridge for industrial networks, tunnelled over a
WireGuard/NetBird overlay.

Feature parity with the C# reference:

- async full-duplex bridge pumps (tokio)
- Windows TAP-Windows6 device I/O: registry adapter discovery, overlapped open, media-status IOCTL
- Raw / VXLAN / GRETAP encapsulation in both directions
- Broad-Industrial-Pass filter (ARP, PROFINET, POWERLINK, EtherCAT, GOOSE; mDNS/LLMNR/SSDP/NetBIOS/WS-Discovery dropped)
- MAC learning table with aging and known-local unicast suppression
- loop detection with per-instance probe frames on both ports
- NetBird peer discovery via `netbird status --json` with interactive peer selection
- unconnected UDP tunnel bound to the tunnel IP (supports dynamic peers)
- periodic `out=/inbound=/dropped=` frame counters

## Usage

```powershell
cargo run --release -- bridge.config.json
```

The bridge must run elevated; raw TAP device access requires administrator rights.

## Configuration

See [bridge.config.json](bridge.config.json). It is strict JSON and intentionally contains no
comments; the parameter reference is below. Leave `remotePeer` empty to pick the peer
interactively from the NetBird mesh.

### Configuration reference

| Parameter | Meaning |
| --- | --- |
| `tapAdapterName` | Windows TAP adapter used as the local Layer 2 port. |
| `peerDiscovery` | `Netbird` discovers tunnel addresses; `None` uses literal config values. |
| `remotePeer` | Remote bridge FQDN, short name, or tunnel IP. Empty enables interactive selection. |
| `localTunnelIp` | Local tunnel IPv4 address when `peerDiscovery` is `None`; otherwise discovered. |
| `wireGuardAdapterName` | Optional tunnel adapter name; required with `peerDiscovery` set to `None`. |
| `encapsulationMode` | `Raw`, `Vxlan`, or `Gretap`. |
| `encapsulationPort` | UDP override; `0` selects Raw `55555` or VXLAN `4789`. |
| `vxlanVni` | 24-bit VXLAN network identifier, matching the peer. |
| `greKey` | Optional 32-bit GRE key; `0` disables it. |
| `enableFilter` | Enables the industrial protocol filter and consumer-noise drops. |
| `enableMacLearning` | Learns MAC locations and suppresses known-local unicast flooding. |
| `macAgingSeconds` | MAC entry lifetime in seconds. |
| `enableLoopDetection` | Enables tagged probes for Layer 2 loop detection. |
| `loopProbeSeconds` | Interval between loop-detection probes. |
| `stopOnLoopDetected` | Stops the bridge when its own probe returns through another port. |
| `tapMtuOverride` | Optional TAP MTU; `null` derives it from the tunnel MTU. |
| `enforceTapMtu` | Applies the selected TAP MTU through `netsh` at startup. |
| `tapAddressMode` | `Manual`, `Dhcp`, or `Static` TAP IPv4 configuration. |
| `tapStaticAddress` | Static TAP IPv4 address, used only with `Static` mode. |
| `tapStaticPrefixLength` | Prefix length for the static TAP address. |
| `isolateTapRouting` | Ignores TAP default routes to protect the NetBird route. |
| `disableIpv6OnTap` | Disables IPv6 and selected link-layer discovery bindings on the TAP. |
| `autoCreateTapAdapter` | Creates a missing TAP adapter with `tapctl.exe` when elevated. |
| `netbirdCliPath` | Optional explicit path to `netbird.exe`; `null` searches standard paths and `PATH`. |
