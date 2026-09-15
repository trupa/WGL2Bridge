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

All configuration lives in `appsettings.json` (camelCase). Below are detailed descriptions of
every notable configuration key, the expected type, and how the bridge uses the value.

- tapName (string, default: "Industrial-TAP")
  - The friendly name of the TAP-Windows adapter to use. This is the adapter name visible in
    the Windows network control panel. When `createTapIfMissing` is true the program will attempt
    to create or rename an adapter to this name.

- tunnelInterfaceName (string, default: "NetBird")
  - The friendly name of the WireGuard/NetBird tunnel interface on the host. The encapsulation socket
    binds to an IP on this interface. If `peerAddress` is omitted the program will attempt discovery
    using the NetBird control plane and `peerName`.

- transportMode (string, default: "Vxlan")
  - One of `Vxlan`, `GreTap`, or `Raw`. Determines the outer encapsulation used for frames sent over
    the tunnel. `Vxlan` and `GreTap` are preferred for interoperability with kernel peers; `Raw` sends
    the Ethernet frame as a raw IP payload and requires the peer to understand the chosen IP protocol
    number.

- peerAddress (string|null, default: null)
  - The IP address (or hostname) of the remote peer to which encapsulated frames are sent. This value
    is used as a fallback when name-based discovery (see `peerName`) is not configured or fails to
    resolve. If `peerName` is set and discovery succeeds, the discovered peer IP takes precedence over
    `peerAddress`.

- peerName (string|null, default: null)
  - NetBird peer FQDN used during CLI discovery. When set the bridge attempts to discover a peer whose
    NetBird identity (FQDN) matches this value. If a matching (or any connected) peer is discovered,
    that peer's IP will be used even if `peerAddress` is configured. Set this to target a specific
    NetBird peer when multiple peers exist.

- tunnelLocalAddress (string|null, default: null)
  - Optional override for the local tunnel IP address used as the source for encapsulation packets. If null
    the bridge selects the best address assigned to `tunnelInterfaceName`.

- vxlanVni (integer, default: 100)
  - VXLAN VNI (24-bit segment identifier) used when `transportMode` is `Vxlan`. Must match the VNI configured
    on the Linux/OpenWrt kernel peer for proper decapsulation.

- vxlanDestinationPort (integer, default: 4789)
  - UDP destination port for VXLAN packets. Standard VXLAN uses 4789; change only if your peer expects a different port.

- greTapKey (integer|null, default: null)
  - Optional GRE key when using `GreTap`. If configured the peer must be configured with the same key to accept and demultiplex traffic.

- rawIpProtocol (integer, default: 99)
  - IPv4 protocol number used for `Raw` transport. The peer must be configured to receive raw IP packets of this protocol number and inject the payload as Ethernet frames.

- peerSourceValidation (boolean, default: true)
  - When true the bridge drops inbound tunnel packets that do not appear to originate from the configured `peerAddress` or discovered NetBird peer. Disable only for diagnostics or when multiple peers legitimately send traffic.

- reconnectDelaySeconds (integer, default: 5)
  - Delay (seconds) before attempting to rebind or reconnect after a recoverable failure such as transient network error.

- tunnelHealthCheckSeconds (integer, default: 30)
  - Interval in seconds between health-checks that validate the tunnel reachability and the peer's presence. Set to 0 to disable periodic checks.

- stopOnLoopDetected (boolean, default: true)
  - If true the process will exit when its own loop-detection probe is observed coming back from the tunnel. Useful to prevent bridging two ends of the same segment back-to-back.

- enableLoopDetection (boolean, default: true)
  - If enabled the bridge periodically injects a special 0x88B5 probe frame to detect loops. Disable for environments where loop-detection probes are undesirable.

- loopProbeIntervalSeconds (integer, default: 10)
  - Seconds between loop-detection probe injections when `enableLoopDetection` is true.

- statsIntervalSeconds (integer, default: 15)
  - Interval for logging aggregated packet counters and other runtime statistics. Set to 0 to disable periodic stats logging.

- maxBroadcastPps (integer, default: 0)
  - Per-direction broadcast/multicast forwarding limit (frames per second). When set to a positive value the bridge silently drops excess multicast/broadcast frames above the rate to limit storms and chattiness over the tunnel. A value of 0 disables rate limiting.

- metricsPort (integer, default: 0)
  - Local-only HTTP port (loopback) for serving simple text metrics and counters. Set to 0 to disable. Example: `metricsPort: 9080` exposes counters at `http://127.0.0.1:9080/` for local scraping or debugging.

- macAgingSeconds (integer, default: 300)
  - Time in seconds after which learned MAC table entries expire if not refreshed. Tune based on network churn.

- assumeVlanTagged (boolean, default: true)
  - When true the bridge assumes incoming Ethernet frames on the TAP are 802.1Q-tagged and accounts for the 4-byte VLAN tag when deriving the TAP MTU (inner header length = 18 bytes). Set to false for untagged deployments so the derived TAP MTU uses the 14-byte inner Ethernet header. Incorrect setting can cause MTU/fragmentation issues.

- tapIpAddress (string|null, default: null)
  - Optional static IPv4/CIDR to assign to the TAP adapter (e.g. "192.168.100.2/24"). When null the TAP falls back to DHCP.

- renewDhcpOnReconnect (boolean, default: true)
  - When true the bridge executes an `ipconfig /renew` against the TAP adapter after a successful reconnect sequence when the TAP is configured for DHCP (i.e. `tapIpAddress` is null). This is done after the forwarding pumps are up so the DHCPDISCOVER packets have a forwarding path.

- createTapIfMissing (boolean, default: true)
  - When true the program will attempt to create and configure a TAP adapter if an adapter named `tapName` is not present. This requires the TAP driver installers/tools referenced below and elevated permissions.

- tapInstallToolPath (string|null, default: null)
  - Explicit path to installer helper (`tapctl.exe`, `tapinstall.exe`, or `devcon.exe`). When null the bridge searches common locations (OpenVPN bin, PATH) before failing.

- tapDriverInfPath (string|null, default: null)
  - Explicit path to the TAP driver INF (e.g. `OemVista.inf`). Required only when the automatic search cannot locate the driver package.

- netbirdCliPath (string|null, default: null)
  - Optional explicit path to the `netbird.exe` CLI used for discovery and status checks. When null the bridge searches PATH and common install directories. If the CLI cannot be found NetBird-based discovery is unavailable and the resolver falls back to the configured `peerAddress` (if provided).

- tapHardwareId (string, default: "tap0901")
  - The hardware ID used when creating the TAP device. Typically `tap0901` for OpenVPN's TAP-Windows6 adapters.

- consoleLogLevel / fileLogLevel (string, default: "Information" / "Debug")
  - Logging levels for console and file sinks respectively. Accepts standard serilog level names (e.g. `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`).

- logFilePath (string, default: "wgl2bridge.log")
  - Path to the plain-text application log file. If a relative path is provided it is resolved against the process working directory (services typically write under ProgramData or the service's working directory). Ensure the process has write permissions for the containing directory when running as a service.

- logMaxBytes (integer, default: 10485760)
  - Maximum size in bytes for the log file before rotation. When the file reaches this size it is rotated to `wgl2bridge.log.1` and a fresh file is started. Set to a larger value for long-term retention or smaller for low-disk-space environments.

- dropUdpPorts (array of integers, default: `[5353,5355,1900,3702,137,138,17500,27036]`)
  - UDP destination ports to silently drop when observed on the TAP input. These are common consumer/service discovery and broadcast ports (mDNS, LLMNR, SSDP, WS-Discovery, NetBIOS) which reduce unnecessary chattiness over the bridge.

- allowVlans (array of integers|null, default: null)
  - When set, only frames tagged with one of the specified VLAN IDs are forwarded; untagged frames are dropped. When null all VLANs are allowed and VLAN tags are preserved across encapsulation.


## appsettings.json example

```json
{
  "tapName": "Industrial-TAP",
  "tunnelInterfaceName": "NetBird",
  "transportMode": "Vxlan",
  "peerAddress": null,
  "vxlanVni": 100,
  "createTapIfMissing": true
}
```

## Logging levels reference

`Info` = lifecycle, `Warning` = recoverable, `Error` = fatal/config, `Debug` = diagnostics.

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

## License

Copyright (c) KaicapTech

WGL2Bridge is provided under the MIT License. You may use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, subject to the following conditions:

- The above copyright notice and this permission notice shall be included in all copies or substantial
  portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

If you prefer, add a separate LICENSE file with the exact MIT text and update this README to point to it.