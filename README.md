# WGL2Bridge

A single self-contained, Layer 2 Ethernet bridge that reads raw frames from a
TAP-Windows6 adapter, filters consumer broadcast noise, encapsulates the surviving frames
(Raw / VXLAN / GRETAP), and sends them over a WireGuard/NetBird tunnel to a peer that injects them
onto a remote segment — so two segments behave like one switch across an encrypted WAN.

Primary use: engineering access (TIA Portal, DCP discovery, watch tables), not hard real-time control.

## How it works

```
                  raw Ethernet               VXLAN / GRETAP                  WireGuard                raw Ethernet
+-------------------+  frames   +----------------+   (Raw IP) +-----------------+ (NetBird) +----------------+  frames  +------------------------+
|  Engineering tool | --------> |  TAP adapter   | ---------> |      Bridge     | --------> |  Remote peer   | -------> | Local industrial LAN   |
|  (TIA Portal, DCP,| <-------  | (TAP-Windows6) | <--------- | (filter + encap)| <-------- | (Linux/OpenWrt)| <------- | (PLCs, drives, I/O, ...)|
|   watch tables)   |           +----------------+            +-----------------+           +----------------+          +------------------------+
+-------------------+
```

Two symmetric pumps run concurrently:

- **TAP → tunnel**: overlapped `ReadFile` → `IndustrialFilter` / `MacTable` / `LoopDetector` →
  constant encapsulation header (already written into buffer headroom) → `sendto` on a socket bound
  to the tunnel IP.
- **Tunnel → TAP**: `recvfrom` → header validation + the same filters → `WriteFile` to the TAP device.

## Encapsulation modes

| Mode     | Headroom | Outer overhead | Linux peer                         |
| -------- | -------- | -------------- | ---------------------------------- |
| `Vxlan`  | 8 bytes  | 36 (IP+UDP+VXLAN) | kernel `vxlan` (dstport 4789)   |
| `GreTap` | 4/8 bytes | 24/28 (IP+GRE) | kernel `gretap`                 |
| `Raw`    | 0        | 20 (IP)        | requires matching software (lab) |

`Vxlan` and `GreTap` are the production modes — the Linux/OpenWrt peer is kernel-native and needs no
software. `Raw` carries the bare frame as a raw IP payload with a configurable protocol number.

Egress is pinned: the encapsulation socket binds to the tunnel interface IP, so frames cannot leak
onto the physical LAN.

## Prerequisites

- Windows 10/11 x64, **run elevated** (raw sockets and raw TAP device access require elevation).
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- **Visual Studio C++ Build Tools** (the `Desktop development with C++` workload) for NativeAOT linking.
- **TAP-Windows6** driver installed (e.g. [OpenVPN tap-windows6](https://github.com/OpenVPN/tap-windows6)),
  with the adapter renamed to match `tapName`. To let WGL2Bridge create the adapter itself
  (`createTapIfMissing: true`), OpenVPN's `tapctl.exe` (OpenVPN 2.5+, in `C:\Program Files\OpenVPN\bin`)
  is used when available; otherwise the driver package (`OemVista.inf`) and its devcon-style installer
  (`tapinstall.exe` / `devcon.exe`) must be present — set `tapDriverInfPath` and
  `tapInstallToolPath` if they aren't in the standard locations.
- **NetBird** (or a plain WireGuard interface) with an active tunnel; name it to match
  `tunnelInterfaceName`, or set `peerAddress` explicitly. On Windows the WireGuard/NetBird adapter
  is usually named `wt0` (or `NetBird`); check with `netsh interface show interface`.

## Build / publish

```powershell
dotnet publish -c Release -r win-x64 -p:PublishAot=true -p:IsAotCompatible=true
```

The NativeAOT executable is produced at
`bin\Release\net10.0-windows\win-x64\publish\WGL2Bridge.exe`.

## Configuration

All configuration lives in `appsettings.json` (camelCase). Notable keys:

| Key                      | Default            | Meaning                                            |
| ------------------------ | ------------------ | -------------------------------------------------- |
| `tapName`                | `Industrial-TAP`   | TAP adapter friendly name                          |
| `tunnelInterfaceName`    | `NetBird`          | Tunnel interface friendly name                     |
| `transportMode`          | `Vxlan`            | `Raw`, `Vxlan`, or `GreTap`                        |
| `peerAddress`            | `null`             | Peer tunnel IP/hostname (or discover via NetBird)  |
| `peerName`               | `null`             | NetBird peer FQDN to select during CLI discovery   |
| `tunnelLocalAddress`     | `null`             | Optional local tunnel IP override                  |
| `vxlanVni`               | `100`              | VXLAN VNI                                          |
| `vxlanDestinationPort`   | `4789`             | VXLAN UDP destination port                         |
| `greTapKey`              | `null`             | Optional GRE key                                   |
| `rawIpProtocol`          | `99`               | Raw mode IP protocol number                        |
| `peerSourceValidation`   | `true`             | Drop inbound tunnel packets not from the peer (disable for diagnostics) |
| `reconnectDelaySeconds`  | `5`                | Rebind delay after a recoverable failure           |
| `tunnelHealthCheckSeconds` | `30`             | Seconds between tunnel health checks (0 = off)     |
| `stopOnLoopDetected`     | `true`             | Exit when our own loop probe returns               |
| `enableLoopDetection`    | `true`             | Inject 0x88B5 loop-detection probes                |
| `loopProbeIntervalSeconds` | `10`             | Seconds between 0x88B5 loop probes                 |
| `statsIntervalSeconds`   | `15`              | Seconds between packet-counter log lines (0 = off) |
| `macAgingSeconds`        | `300`              | Learned-MAC aging                                  |
| `tapIpAddress`           | `null`             | Static TAP IPv4/CIDR; empty/null falls back to DHCP |
| `createTapIfMissing`     | `true`             | Create the TAP adapter when it doesn't exist        |
| `tapInstallToolPath`     | `null`             | Path to tapctl.exe / tapinstall.exe / devcon.exe (auto-searched) |
| `tapDriverInfPath`       | `null`             | Path to OemVista.inf (auto-searched)                |
| `tapHardwareId`          | `tap0901`          | Hardware ID used when creating the adapter          |
| `consoleLogLevel` / `fileLogLevel` | `Information` / `Debug` | Independent log levels              |
| `dropUdpPorts`           | `[5353,5355,1900,3702,137,138,17500,27036]` | Consumer discovery UDP ports to drop |
| `allowVlans`             | `null`             | VLAN IDs to bridge; null/empty = all VLANs + untagged |
| `assumeVlanTagged`       | `true`             | Assume 802.1Q-tagged frames when deriving the TAP MTU |
| `maxBroadcastPps`        | `0`                | Broadcast/multicast storm limit per direction (0 = off) |
| `logFilePath`            | `wgl2bridge.log`   | Plain-text log file path                       |
| `logMaxBytes`            | `10485760`         | Log size before rotating to `.1`              |
| `renewDhcpOnReconnect`   | `true`             | `ipconfig /renew` on the TAP after a reconnect |
| `metricsPort`            | `0`                | Loopback HTTP metrics port (0 = disabled)      |
| `netbirdCliPath`         | `null`             | Explicit path to netbird.exe (else auto-searched) |

Run with a custom config path: `WGL2Bridge.exe path\to\config.json`.

## Filter policy ("Broad-Industrial-Pass")

- **Allow**: ARP, PROFINET (`0x8892`), EtherCAT (`0x88A4`), GOOSE (`0x88B8`), SV (`0x88BA`),
  LLDP (`0x88CC`), all TCP, and all UDP except the configured drop ports.
- **Drop**: consumer discovery UDP — mDNS 5353, LLMNR 5355, SSDP 1900, WS-Discovery 3702,
  NetBIOS 137/138, Dropbox 17500, Steam 27036.
- **Fail open (forward)**: truncated headers, ICMP, and later IP fragments.

## Loop detection

A broadcast probe with EtherType `0x88B5`, a `WGL2` magic and a per-instance random ID is injected
into the TAP segment every `loopProbeIntervalSeconds`. If only our own probe returns, the bridge logs
the loop and (when `stopOnLoopDetected` is true) stops — preventing a broadcast storm.

## Windows networking quirks handled

- **Route isolation before DHCP**: TAP metric forced to 9999 and `DisableDefaultRoutes=1` set, so a
  bridged-segment DHCP server cannot hijack the host's default route.
- **MTU derivation**: `tapMtu = tunnelMtu - outerHeader - innerEthernetHeader`, re-derived and
  re-applied every session.

## Operations

**Dry-run check** (validate config + resolve peer/tunnel/TAP without opening devices):

```powershell
WGL2Bridge.exe --check
```

**Run as a Windows service** (register once, then start):

```powershell
sc.exe create WGL2Bridge binPath= "C:\path\to\WGL2Bridge.exe --service" start= auto
sc.exe start WGL2Bridge
sc.exe stop WGL2Bridge
sc.exe delete WGL2Bridge
```

**Metrics endpoint** — set `metricsPort` (e.g. `9080`) and browse `http://127.0.0.1:9080/`
(text/plain counters).

## Logging

- Console: `HH:mm:ss.fff message` (single line, no color).
- File: `yyyy-MM-dd HH:mm:ss.fff [Level] category: message`, rotated at `logMaxBytes`.

`Info` = lifecycle, `Warning` = recoverable, `Error` = fatal/config, `Debug` = diagnostics.
