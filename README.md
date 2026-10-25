# WGL2Bridge

A single self-contained, Layer 2 Ethernet bridge that reads raw frames from a
TAP-Windows6 adapter, filters consumer broadcast noise, encapsulates the surviving frames
(Raw / VXLAN), and sends them over a WireGuard/NetBird tunnel to a peer that injects them
onto a remote segment — so two segments behave like one switch across an encrypted WAN.

Primary use: remote Layer 2 access for configuration and diagnostics, not hard real-time control.

## How it works

```
                  raw Ethernet               VXLAN UDP 4789                  WireGuard                raw Ethernet
+-------------------+  frames   +----------------+   (Raw IP) +-----------------+ (NetBird) +----------------+  frames  +------------------------+
|  Host application | --------> |  TAP adapter   | ---------> |      Bridge     | --------> |  Remote peer   | -------> | Remote LAN (bridged)   |
|  (management tool,| <-------  | (TAP-Windows6) | <--------- | (filter + encap)| <-------- | (Linux/OpenWrt)| <------- | (servers, hosts, and...)|
|   console view)   |           +----------------+            +-----------------+           +----------------+          +------------------------+
+-------------------+
```

Two symmetric forwarding pumps run concurrently, alongside the probe, statistics, interface-watch
and health-check pumps:

- **TAP → tunnel**: overlapped `ReadFile` → `FrameFilter` / `MacTable` / `LoopDetector` →
  constant encapsulation header (already written into buffer headroom) → `sendto` on a socket bound
  to the tunnel IP.
- **Tunnel → TAP**: `recvfrom` → header validation + the same filters → `WriteFile` to the TAP device.

## Encapsulation modes

| Mode     | Headroom | Outer overhead | Linux peer                         |
| -------- | -------- | -------------- | ---------------------------------- |
| `Vxlan`  | 8 bytes  | 36 (IP+UDP+VXLAN) | kernel `vxlan` (dstport 4789)   |
| `Raw`    | 0        | 20 (IP)        | requires matching software (lab) |

`Vxlan` is the production mode — the Linux/OpenWrt peer is kernel-native and needs no software.
`Raw` carries the bare frame as a raw IP payload with a configurable protocol number.

Egress is pinned: the encapsulation socket binds to the tunnel interface IP, so frames cannot leak
onto the physical LAN.

## Prerequisites

### Running WGL2Bridge

> **The only component you must install to run WGL2Bridge is the TAP-Windows6 adapter driver — nothing
> else.** The published executable is self-contained (NativeAOT): it does **not** need the .NET runtime,
> the .NET SDK, the C++ Build Tools, or any other runtime dependency.

- Windows 10/11 x64, **run elevated** (raw sockets and raw TAP device access require elevation).
- **TAP-Windows6** driver installed (e.g. [OpenVPN tap-windows6](https://github.com/OpenVPN/tap-windows6)),
  with the adapter renamed to match `tapName`. To let WGL2Bridge create the adapter itself
  (`createTapIfMissing: true`), OpenVPN's `tapctl.exe` (OpenVPN 2.5+, in `C:\Program Files\OpenVPN\bin`)
  is used when available; otherwise the driver package (`OemVista.inf`) and its devcon-style installer
  (`tapinstall.exe` / `devcon.exe`) must be present — set `tapDriverInfPath` and
  `tapInstallToolPath` if they aren't in the standard locations.

You also need a tunnel to carry the encapsulated frames:

- **NetBird** (or a plain WireGuard interface) with an active tunnel; name it to match
  `tunnelInterfaceName`, or set `peerAddress` explicitly. On Windows the adapter is usually named
  `wt0` for NetBird or the tunnel name for a plain WireGuard config; check with
  `netsh interface show interface`. A plain WireGuard tunnel needs only `peerAddress` (leave
  `peerName` null) and does not require the NetBird CLI.

### Building from source

Building/publishing from source additionally requires the following **build-time only** tools (they are
not needed to run a published binary):

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- **Visual Studio C++ Build Tools** (the `Desktop development with C++` workload) for NativeAOT linking.

## Build / publish

```powershell
dotnet publish -c Release -r win-x64 -p:PublishAot=true -p:IsAotCompatible=true
```

The NativeAOT executable is produced at
`bin\Release\net10.0-windows\win-x64\publish\WGL2Bridge.exe`.

## Supported topologies

| Topology | Status | Notes |
|---|---|---|
| **Client ↔ Lan** (WGL2Bridge on both ends) | **Supported, primary** | `Client` on the remote machine (TAP gets an address via DHCP/`tapIpAddress`); `Lan` on the machine whose TAP is bridged to the physical LAN (acts as a multi-client server). |
| **Client ↔ Linux kernel VXLAN** | **Supported** | The Linux end is a plain `vxlan` device bridged to its LAN and applies **no** filtering; all protection comes from our ingress filters. See the recipe below. |
| **Lan ↔ Lan** (site-to-site join) | **Not supported officially** | It will probably work — both ends act as symmetric servers — but it is untested and unsupported. Neither end configures the TAP address, so the MTU must be set on both Windows bridges by hand, and two DHCP servers on the joined segment need split scopes in the same subnet. |

### Linux kernel VXLAN peer (Client mode)

On the Linux side (root):

```sh
ip link add vxlan0 type vxlan id 4096 dstport 4789 remote <client-tunnel-ip> nolearning
ip link set vxlan0 mtu 1450
ip link set vxlan0 master br0
ip link set vxlan0 up
# flood unknown/broadcast/multicast to the client (required with "nolearning")
bridge fdb append 00:00:00:00:00:00 dev vxlan0 dst <client-tunnel-ip>
```

Matching settings on the Windows client:

| Key | Value | Why |
|---|---|---|
| `vxlanVni` | `4096` | must equal the kernel `id` — a mismatch shows up as `tunnel_invalid` |
| `vxlanDestinationPort` | `4789` | must equal `dstport` |
| `assumeVlanTagged` | `false` | a plain Linux bridge emits untagged frames; `true` over-reserves 4 bytes of MTU |
| `allowVlans` | `null` | setting an allow list drops every untagged frame and silently kills the link |
| `peerAddress` | Linux tunnel IP | used for `peerSourceValidation` |

The Linux end does not run our filters, so a noisy remote LAN is filtered only on ingress here —
that is by design; the loop probe (`0x88B5`) still travels both ways, so loop detection works.

## Configuration

All configuration lives in `appsettings.json` (camelCase). Below are detailed descriptions of
every notable configuration key, the expected type, and how the bridge uses the value.

- tapName (string, default: "WGBridge-TAP")
  - The friendly name of the TAP-Windows adapter to use. This is the adapter name visible in
    the Windows network control panel. When `createTapIfMissing` is true the program will attempt
    to create or rename an adapter to this name.

- bridgeSide (string, default: "Client")
  - Which side of the connection this instance is on. `Client` treats the TAP as a standalone
    adapter: WGL2Bridge configures its IPv4 address (DHCP, or `tapIpAddress` when set), applies
    route isolation and renews DHCP after a reconnect. `Lan` treats the TAP as a port of a Windows
    network bridge and acts as a **multi-client server**: it learns clients from inbound tunnel
    traffic and forwards frames per destination MAC. It does **not** configure an address, route
    isolation or MTU on the TAP and never runs DHCP on it — the bridge (or the underlying physical
    NIC) owns the LAN address and MTU. Use `Lan` on the machine whose TAP is bridged to its
    physical NIC. See [Supported topologies](#supported-topologies): `Lan` ↔ `Lan` is not supported
    officially (it will probably work, but is untested).

- tunnelInterfaceName (string, default: "NetBird")
  - The friendly name of the WireGuard/NetBird tunnel interface on the host. The encapsulation socket
    binds to an IP on this interface. If `peerAddress` is omitted the program will attempt discovery
    using the NetBird control plane and `peerName`.

- transportMode (string, default: "Vxlan")
  - One of `Vxlan` or `Raw`. Determines the outer encapsulation used for frames sent over the
    tunnel. `Vxlan` is preferred for interoperability with kernel peers; `Raw` sends the Ethernet
    frame as a raw IP payload and requires the peer to understand the chosen IP protocol number.

- peerAddress (string|null, default: null)
  - The IP address (or hostname) of the remote peer to which encapsulated frames are sent. This value
    is used as a fallback when name-based discovery (see `peerName`) is not configured or fails to
    resolve. If `peerName` is set and discovery succeeds, the discovered peer IP takes precedence over
    `peerAddress`. In `Lan`/server mode it is optional: the server learns clients from inbound
    traffic, and a configured address is only used as the initial client (seed).

- peerName (string|null, default: null)
  - NetBird peer FQDN used during CLI discovery. When set, the bridge accepts **only** a peer whose
    NetBird identity (FQDN) matches this value exactly (case-insensitive). It never falls back to a
    different connected peer: if no match is found it logs a warning and waits/retries, so a typo or a
    missing peer can never silently bridge to the wrong machine. Set this to target a specific NetBird
    peer when multiple peers exist. Leave null when the peer is pinned via `peerAddress`.

Note on peer discovery: the NetBird CLI is only consulted when `peerName` is set. If `peerAddress` is
set and `peerName` is null the peer is pinned and NetBird is never used: no `netbird status`
subprocess is spawned and no NetBird warnings are logged. When `peerName` is set, only an exact FQDN
match is accepted — the bridge never substitutes another connected peer. If the named peer cannot be
found it logs the condition and retries; if `peerAddress` is also configured, that explicit address is
the fallback. A plain WireGuard tunnel is therefore fully supported — set `tunnelInterfaceName` to the
WireGuard adapter name and `peerAddress` to the peer's tunnel IP, and leave `peerName` null.

- tunnelLocalAddress (string|null, default: null)
  - Optional override for the local tunnel IP address used as the source for encapsulation packets. If null
    the bridge selects the best address assigned to `tunnelInterfaceName`.

- vxlanVni (integer, default: 100)
  - VXLAN VNI (24-bit segment identifier) used when `transportMode` is `Vxlan`. Must match the VNI configured
    on the Linux/OpenWrt kernel peer for proper decapsulation.

- vxlanDestinationPort (integer, default: 4789)
  - UDP destination port for VXLAN packets. Standard VXLAN uses 4789; change only if your peer expects a different port.

- rawIpProtocol (integer, default: 99)
  - IPv4 protocol number used for `Raw` transport. The peer must be configured to receive raw IP packets of this protocol number and inject the payload as Ethernet frames.

- peerSourceValidation (boolean, default: true)
  - Client mode: drops inbound tunnel packets that do not originate from the configured `peerAddress` or discovered NetBird peer. Ignored in `Lan` mode, where `allowedClientPeers` controls access.

- allowFragmentation (boolean, default: true)
  - What to do with a frame that does not fit the tunnel payload (`frame + encapsulation overhead > tunnel MTU`). `true` clears the DF bit so the IP stack fragments the encapsulated packet: full-size frames keep crossing at ~5–8 % overhead and with the usual risk that a path drops IP fragments. `false` drops them instead and counts them, which makes an MTU mismatch explicit rather than hidden. Either way a throttled warning names the exact MTU to configure.
  - This matters because a bridged (`Lan`) segment's MTU **cannot be lowered from this side**: a Windows network bridge forwards L2 frames regardless of its own MTU, so hosts on the segment keep sending 1500-byte frames. Configure the segment's MTU through **DHCP option 26** (IPv4) or the **RA MTU option** (IPv6) to eliminate the need for fragmentation.

- allowedClientPeers (array of strings, default: [])
  - Lan/server mode only. Optional allow-list of client tunnel IPv4 addresses. When empty, the server accepts any peer that can reach the tunnel (NetBird/WireGuard authenticates the peer).

- allowClientToClient (boolean, default: true)
  - Lan/server mode. When true, remote clients can reach each other through the server (standard L2 bridge). When false, client-originated traffic is only written to the LAN, never forwarded to other clients.

- maxClients (integer, default: 64)
  - Lan/server mode. Maximum number of clients tracked at once; the least-recently-seen client is evicted when exceeded.

- clientAgingSeconds (integer, default: 300)
  - Lan/server mode. Idle timeout after which a client stops counting as connected. Its learned MAC bindings are **not** dropped at the same time — see `remoteMacAgingSeconds`.

- remoteMacAgingSeconds (integer, default: 3600)
  - Lan/server mode. How long a MAC learned from a client keeps pointing at that client after the client goes idle or disconnects. The binding deliberately outlives `clientAgingSeconds`: while it exists, LAN frames for that MAC go to **one** client instead of being copied to every client. Must be >= `clientAgingSeconds`. Set this too low and one-way traffic (syslog, SNMP traps, UDP video, monitoring) to a quiet host will keep re-flooding the client set.

- maxRemoteMacs (integer, default: 4096)
  - Lan/server mode. Hard cap on remembered client MACs across all clients; the least-recently-seen binding is evicted when exceeded (`remote_macs_evicted` in the metrics).

- maxUnknownMacFloodPps (integer, default: 10, 0 = unlimited)
  - Lan/server mode. Per-second flood budget for a **single** destination MAC that cannot be resolved to a connected client (never seen, or its remembered owner is offline). Every such frame costs one copy per connected client, so this caps the multiplication factor. Exceeding it drops the extra client copies (the frame is still written to the LAN) and logs one Warning per MAC. Other MACs are unaffected. Ignored in client mode, where unknown unicast goes to the single peer and is never multiplied.

- reconnectDelaySeconds (integer, default: 5)
  - Delay (seconds) before attempting to rebind or reconnect after a recoverable failure such as transient network error.

- tunnelHealthCheckSeconds (integer, default: 30)
  - Interval in seconds for the tunnel-interface reachability check (the tunnel adapter must still exist and report Up). The peer-presence and no-inbound-traffic checks run on an independent fixed 30-second cadence. Set to 0 to disable all periodic health checks.

- stopOnLoopDetected (boolean, default: true)
  - If true the process will exit when its own loop-detection probe is observed returning on either path (TAP or tunnel). Useful to prevent bridging two ends of the same segment back-to-back.

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

- enableLoopDetection (boolean, default: true)
  - If enabled the bridge periodically injects a special 0x88B5 probe frame into the local segment and sends a copy of it over the tunnel to detect loops on both paths. Disable for environments where loop-detection probes are undesirable.

- assumeVlanTagged (boolean, default: true)
  - When true the bridge assumes incoming Ethernet frames on the TAP are 802.1Q-tagged and accounts for the 4-byte VLAN tag when deriving the TAP MTU (inner header length = 18 bytes). Set to false for untagged deployments so the derived TAP MTU uses the 14-byte inner Ethernet header. Incorrect setting can cause MTU/fragmentation issues.

- tapIpAddress (string|null, default: null)
  - Optional static IPv4/CIDR to assign to the TAP adapter (e.g. "192.168.100.2/24"). When null the TAP falls back to DHCP. Only used when `bridgeSide` is `Client`; ignored in `Lan` mode.

- renewDhcpOnReconnect (boolean, default: true)
  - When true, shortly after the forwarding pumps start the bridge checks the TAP's IPv4 address and runs `ipconfig /renew` only if the TAP has no usable address (APIPA 169.254.x.x or none). This recovers a TAP that missed DHCP before the tunnel was ready, without re-running a network configuration change on every reconnect (which can make NetBird bounce the tunnel again). Only applies when `bridgeSide` is `Client` and `tapIpAddress` is null.

- createTapIfMissing (boolean, default: true)
  - When true the program will attempt to create and configure a TAP adapter if an adapter named `tapName` is not present. This requires the TAP driver installers/tools referenced below and elevated permissions.

- tapInstallToolPath (string|null, default: null)
  - Explicit path to installer helper (`tapctl.exe`, `tapinstall.exe`, or `devcon.exe`). When null the bridge searches common locations (OpenVPN bin, PATH) before failing.

- tapDriverInfPath (string|null, default: null)
  - Explicit path to the TAP driver INF (e.g. `OemVista.inf`). Required only when the automatic search cannot locate the driver package.

- tapHardwareId (string, default: "tap0901")
  - The hardware ID used when creating the TAP device. Typically `tap0901` for OpenVPN's TAP-Windows6 adapters.

- consoleLogLevel / fileLogLevel (string, default: "Information" / "Debug")
  - Logging levels for console and file sinks respectively. Accepts the enum names `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`.

- logFilePath (string, default: "wgl2bridge.log")
  - Path to the plain-text application log file. If a relative path is provided it is resolved against the process working directory (services typically write under ProgramData or the service's working directory). Ensure the process has write permissions for the containing directory when running as a service.

- logMaxBytes (integer, default: 10485760)
  - Maximum size in bytes for the log file before rotation. When the file reaches this size it is rotated to `wgl2bridge.log.1` and a fresh file is started. Set to a larger value for long-term retention or smaller for low-disk-space environments.

Every filter key is a **list of values to drop, named after the frame field it matches**. There is no on/off flag and no grouping by concept, so a value can never be filed under the wrong idea: `null` (or omitted) means "use the built-in default set", an explicit array replaces that set, and `[]` means the key drops nothing.

- dropLinkLocalMacs (array of strings|null, default: null ⇒ `["01:80:C2:00:00:00-0F", "01:00:0C:CC:CC:CC", "01:00:0C:CC:CC:CD"]`)
  - **Destination MACs.** The default covers the IEEE 802.1 reserved group `01:80:C2:00:00:00`–`0F` (STP/RSTP/MSTP, LACP, 802.1X/EAPOL, 802.3x PAUSE, slow protocols) and Cisco `01:00:0C:CC:CC:CC`/`CC:CD` (CDP, VTP, DTP, UDLD, PVST+, PAgP). These frames carry no EtherType, so destination MAC is the only handle. Entries are single MACs or last-octet ranges such as `"01:80:C2:00:00:10-1F"`.
  - Forwarding most of these has legitimate uses (one STP domain across a stable underlay, centralized 802.1X, VTP/CDP in a single management domain), so the list is configurable. **One exception: 802.3x PAUSE (`01:80:C2:00:00:01`) is always dropped** — 802.3 does not allow bridges to forward MAC Control frames, and a forwarded PAUSE would stall the far segment. When the list no longer drops the STP MAC, startup warns that loop detection is then the only cross-site loop guard.

- dropEtherTypes (array of strings|null, default: null ⇒ `[]`)
  - **EtherType**, as hexadecimal text with an optional `0x` prefix (e.g. `"0x88CC"` blocks LLDP). Matched on the frame's inner EtherType after any VLAN tags.

- dropIpProtocols (array of integers|null, default: null ⇒ `[2, 88, 89, 103, 112]`)
  - **IANA IP protocol number**, applied to IPv4 and to the final IPv6 next-header. The default set is IGMP 2, EIGRP 88, OSPF 89, PIM 103 (routing/multicast control must not converge across the tunnel) and VRRP/CARP 112 (gateway hellos can hand a site's VIP to the other end).

- dropUdpPorts (array of integers|null, default: null ⇒ `[5353, 5355, 1900, 3702, 137, 138, 17500, 27036, 1985, 3222]`)
  - **UDP destination port.** The default set is consumer discovery (mDNS 5353, LLMNR 5355, SSDP 1900, WS-Discovery 3702, NetBIOS 137/138, Dropbox 17500, Steam 27036) plus gateway redundancy (HSRP 1985, GLBP 3222). To keep one of them, list the rest explicitly — the list is what gets dropped.

- dropTcpPorts (array of integers|null, default: null ⇒ `[]`)
  - **TCP destination port**, empty by default (all TCP passes). Useful to block specific services across the tunnel (445, 3389, …). When empty, the TCP path skips the lookup entirely.

- dropIcmpv4Types (array of integers|null, default: null ⇒ `[]`)
  - **ICMPv4 type**, empty by default. Worth considering: `5` (redirect) and `9`/`10` (router advertisement/solicitation) — the IPv4 equivalent of an RA crossing sites.

- dropIcmpv6Types (array of integers|null, default: null ⇒ `[]`)
  - **ICMPv6 type**, empty by default because an extended segment may legitimately have one IPv6 router. Set `[133, 134]` (Router Solicitation/Advertisement) when each site runs its own — otherwise an RA from the far site installs a foreign prefix and default router on local hosts.

- allowVlans (array of integers|null, default: null)
  - When set to one or more VLAN IDs, only frames tagged with one of those IDs are forwarded; untagged frames and frames with any other tag are dropped. When null (or an empty array) all VLANs are allowed and VLAN tags are preserved across encapsulation.

- netbirdCliPath (string|null, default: null)
  - Optional explicit path to the `netbird.exe` CLI used for discovery and status checks. When null the bridge searches PATH and common install directories. If the CLI cannot be found NetBird-based discovery is unavailable and the resolver falls back to the configured `peerAddress` (if provided). The CLI is only consulted when discovery is required (see the note under `peerName`).


## appsettings.json example

```json
{
  "tapName": "WGBridge-TAP",
  "tunnelInterfaceName": "NetBird",
  "transportMode": "Vxlan",
  "peerAddress": null,
  "vxlanVni": 100,
  "createTapIfMissing": true
}
```

## Logging levels reference

`Information` = lifecycle, `Warning` = recoverable, `Error` = fatal/config, `Debug` = diagnostics
(`Trace` and `Critical` provide finer control at either end).

Run with a custom config path: `WGL2Bridge.exe path\to\config.json`.

## Filter policy

Frames are forwarded by default (fail-open); restriction is opt-in. Every key names **the frame
field it matches**, so a value can only ever be in the list for the field it belongs to. `null` (or
omitted) selects the built-in default set, an explicit array replaces it, `[]` drops nothing. All
lists apply in both directions (TAP→tunnel and tunnel→TAP), and the effective policy is logged once
per start:

```
Filter: link-local=drop(18 macs) ip=drop(5 protocols) udp=drop(10 ports) tcp=none
        icmpv4=none icmpv6=none ethertype=none vlans=all.
```

| Key | Field | Built-in set (`null` ⇒ this) | Why those defaults |
| --- | --- | --- | --- |
| `dropLinkLocalMacs` | destination MAC | `01:80:C2:00:00:00-0F`, `01:00:0C:CC:CC:CC`, `01:00:0C:CC:CC:CD` (PAUSE `…:01` always dropped) | STP/RSTP/MSTP, LACP, EAPOL, PAUSE, CDP/VTP/DTP/UDLD/PVST+/PAgP are per-link; bridging them makes the far switch root, or block its own uplinks, for a topology it should not see. Clear the list only for a deliberate single-domain design — startup then warns that loop detection is the only guard |
| `dropEtherTypes` | EtherType | none | opt-in (e.g. `0x88CC` LLDP) |
| `dropIpProtocols` | IP protocol number | `2, 88, 89, 103, 112` | IGMP/EIGRP/OSPF/PIM adjacencies, querier elections and RPF state across the tunnel (PIM can blackhole multicast for both segments) + VRRP/CARP, whose hellos can hand a site's gateway VIP to a router on the other end |
| `dropUdpPorts` | UDP destination port | `5353, 5355, 1900, 3702, 137, 138, 17500, 27036, 1985, 3222` | consumer discovery chatter (mDNS/LLMNR/SSDP/WS-Discovery/NetBIOS/Dropbox/Steam) + HSRP/GLBP gateway hellos |
| `dropTcpPorts` | TCP destination port | none | opt-in (block a service across the tunnel, e.g. 445/3389) |
| `dropIcmpv4Types` | ICMPv4 type | none | opt-in (`5` redirect, `9`/`10` router advertisement — the IPv4 counterpart of an RA crossing sites) |
| `dropIcmpv6Types` | ICMPv6 type | none | opt-in; `[133, 134]` when each site has its own IPv6 router, otherwise an RA from the far site installs a foreign prefix and default router on local hosts |
| `allowVlans` | VLAN ID (allow-list) | `null` ⇒ all VLANs pass | — |

`dropTcpPorts`, `dropIcmpv4Types` and `dropIcmpv6Types` are empty by default and their lookups are
skipped entirely while empty, so they cost nothing until configured.

Do not list the loop-probe EtherType `0x88B5` anywhere: blocking it disables loop detection, and the
far end must not drop it either — a loop through the tunnel is only detected if the probe can travel
both ways.

**Everything else forwards** — all TCP (unless a TCP port is listed), other ICMP types, later IP
fragments, and any EtherType not in `dropEtherTypes` (including unrecognized types). Truncated
headers fail open.

### Filter metrics

Counters are generated from the effective policy, one per configured value, and appear only while
non-zero — so you can see exactly which value is being dropped, including values you added yourself:

```
filter_mac_0180c2000000 3    ← STP BPDUs dropped
filter_ip_89 7               ← OSPF dropped
filter_udp_5353 41           ← mDNS dropped
filter_vlan 0                ← omitted while zero
tap_filter_dropped 51
tunnel_filter_dropped 0
```

Filters are **local to each end**: the peer applies its own policy or none at all (a plain Linux
`vxlan` interface filters nothing), so our ingress filtering is what protects our segment.

## Loop detection

A broadcast probe with EtherType `0x88B5`, a `WGL2` magic and a per-instance random ID is injected
into the TAP segment every `loopProbeIntervalSeconds`, and an identical copy is sent over the tunnel
(to the single peer, or to every connected client in server mode).

Both copies matter. The local copy detects a loop that exists on the local segment; the tunnel copy
detects a loop that only exists *through* the tunnel, where the far end forwards the segment back to
us — the case that actually causes a broadcast storm across the link. Whichever copy comes back,
`LoopDetector.Observe` recognises this instance's random ID, the bridge logs the loop and (when
`stopOnLoopDetected` is true) stops, preventing the storm from propagating further.

## Windows networking quirks handled

- **Route isolation before DHCP**: TAP metric forced to 9999 and `DisableDefaultRoutes=1` set, so a
  bridged-segment DHCP server cannot hijack the host's default route.
- **MTU derivation**: `tapMtu = tunnelMtu - outerHeader - innerEthernetHeader`, re-derived and
  re-applied every session. `Client` mode applies it to the TAP; `Lan` mode cannot (a bridge port has
  no IPv4 subinterface), which is why oversized frames are detected and warned about instead of
  silently lost.

## MTU mismatch handling

The largest frame that fits the tunnel is `tunnelMtu − encapsulationOverhead` (1384 bytes for a
1420-byte tunnel with VXLAN). Frames above it mean an MTU mismatch somewhere, and each direction has
its own cause:

| Direction | Cause | Behaviour |
| --- | --- | --- |
| **TAP → tunnel** | the attached segment is at 1500 (`Lan` mode, or a client TAP whose MTU could not be applied) | counted (`oversized_outbound`); fragmented if `allowFragmentation` (default) or dropped (`oversized_dropped`). Either way one throttled `Warning` per minute names the MTU to set |
| **tunnel → TAP** | the **peer** is sending frames larger than the tunnel can carry (its `vxlan0`/bridge MTU is too high) | counted (`oversized_inbound`) and warned, then forwarded — it may still be deliverable on a 1500-byte segment |

`ip_fragments` counts IP fragments arriving from the tunnel: on a correctly configured link it is
always **0**, so a non-zero value is direct evidence of an MTU mismatch (or of fragment-based
evasion).

To eliminate the mismatch on a segment you control: set **DHCP option 26** (IPv4) or the **RA MTU
option** (IPv6) to the TAP MTU printed by `--check`. For a Linux peer, set `ip link set vxlan0 mtu
<that value>`.

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
(text/plain counters). The listening URL is logged at startup as
`Metrics endpoint listening on http://127.0.0.1:<port>/`. In `Lan`/server mode the body also
includes one `client <ip>:<port> idle_s <n> macs <n>` line per connected client (or `client none`),
so you can see who is attached. The periodic `Stats:` log also shows the client count and per-client
detail at `Debug` level.

Server-mode counters worth watching:

| Counter | Meaning |
| --- | --- |
| `flooded` | Total frames copied to more than one client (broadcast, multicast, unknown unicast). |
| `unknown_mac_flooded` | Unknown-unicast frames that were still within their per-MAC budget. |
| `unknown_mac_dropped` | Extra client copies dropped because the per-MAC budget was exhausted. |
| `remote_mac_owner_gone` | LAN frames whose remembered owner was not connected (fell back to a budgeted flood). |
| `remote_macs_evicted` | Sticky MAC bindings evicted because `maxRemoteMacs` was reached. |

## Logging

- Console: `HH:mm:ss.fff message` (single line, no color).
- File: `yyyy-MM-dd HH:mm:ss.fff [Level] category: message`, rotated at `logMaxBytes`.

`Information` = lifecycle, `Warning` = recoverable, `Error` = fatal/config, `Debug` = diagnostics.

## Example Peer configuration

- Setup a Peer running Ubuntu Linux on a Raspberry Pi 5 with ubuntu.

- The peer must have a bridge configured and the adapter to the lan in it. Example netplan file ("/etc/netplan/50-cloud-init.yaml"):
```
network:
  version: 2
  ethernets:
    eth0:
      dhcp4: false
  bridges:
    br0:
      interfaces:
        - eth0
      dhcp4: true
      addresses:
        - 192.168.1.2/24
```
- Setup a network dispatcher script ("/etc/networkd-dispatcher/routable.d/99-vxlan-bridge"): 
```
  #!/bin/bash
  if [ "$IFACE" = "wt0" ]; then
    ip link delete vxlan0 2>/dev/null

    ip link add vxlan0 type vxlan id 4096 dstport 4789 dev wt0 learning
    ip link set vxlan0 mtu 1230

    ip link set vxlan0 master br0
    ip link set vxlan0 up
  fi
 ```
Make sure to make the script executable:
 ```
 chmod +x /etc/networkd-dispatcher/routable.d/99-vxlan-bridge
```
When "wt0" comes "up" the script will create a vxlan inteface bind it to wt0 and add vxlan to the bridge.

On your windows machine, run WGL2Bridge bound to the peer running this conguration. All L2 frames along with L3 packets should make it to your Raspberry Pi peer Lan.

### Windows to Windows (one end bridged to its LAN)

Both machines run WGL2Bridge and each selects the other as its peer. Only the machine on the network
you want to reach bridges its TAP adapter to its physical NIC — the other end simply needs an address
on that network. This is the Windows-only shape of the Linux peer above.

**Machine B — on the target network (bridged).** `appsettings.json`:

```json
{
  "tapName": "WGBridge-TAP",
  "bridgeSide": "Lan",
  "tunnelInterfaceName": "wt0",
  "transportMode": "Vxlan",
  "vxlanVni": 4096,
  "peerAddress": null
}
```

Now bridge the adapter so the whole LAN, not just this host, is exposed:

1. Open **Network Connections** (`ncpa.cpl`).
2. Select the **WGBridge-TAP** adapter and the **physical NIC** on the target network.
3. Right-click → **Bridge Connections**.

**Machine A — the client (not bridged).** `appsettings.json`:

```json
{
  "tapName": "WGBridge-TAP",
  "bridgeSide": "Client",
  "tunnelInterfaceName": "wt0",
  "transportMode": "Vxlan",
  "vxlanVni": 4096,
  "peerAddress": "100.64.0.2"
}
```

Leave this adapter unbridged. It receives an address on the target network through the tunnel (DHCP,
or set `tapIpAddress` for a static one), so applications on Machine A reach the devices on that
network directly.

Notes:

- `vxlanVni` must match on both ends. Each **client** points `peerAddress` at the server's tunnel
  IP (B is `100.64.0.2` here); the server needs no `peerAddress` and learns clients automatically.
- The server accepts multiple clients (up to `maxClients`). Unicast is sent only to the client that
  owns the destination MAC; broadcast and multicast are flooded to all clients. Unknown unicast is
  flooded too, but each destination MAC gets a bounded flood budget (`maxUnknownMacFloodPps`)
  because a flood costs one copy per client. Set `allowClientToClient: false` to keep clients from
  reaching each other.
- MAC bindings are sticky: a MAC learned from a client keeps pointing at that client for
  `remoteMacAgingSeconds` even after the client goes idle, so one-way traffic to a quiet host is not
  echoed to every client. Only when the owner is genuinely gone does the frame fall back to a
  budgeted flood, which is also how a MAC that moved between clients is rediscovered.
- To restrict which overlay peers may join, set `allowedClientPeers` to their tunnel IPs; otherwise
  any peer that can reach the server's VXLAN port is accepted (the NetBird/WireGuard tunnel is the
  authentication boundary).
- Set `bridgeSide` to `Lan` on the bridged machine (B) and `Client` on the other (A). In `Lan` mode
  WGL2Bridge leaves the TAP's IP configuration alone so the bridge keeps the LAN's address and
  default route; running DHCP on a bridged TAP can otherwise pull an address across the tunnel.
- No firewall rule is required when the underlay is NetBird/WireGuard: the encapsulated packets ride
  inside WireGuard's UDP.
- Run both ends elevated (the manifest prompts for it).
- The target network's frames must fit the tunnel. `WGL2Bridge.exe --check` prints the TAP MTU it
  derives — verify a full-size frame still gets through, since a Windows bridge may not carry the
  reduced MTU over to the physical NIC.
- Leave loop detection enabled on the bridged end: if the target network reaches back through the
  tunnel, the probe returns and the bridge stops rather than storming.
- `Raw` works the same way (`"transportMode": "Raw"`, with a matching `rawIpProtocol` on both ends)
  when both ends run this software.
- If NetBird is set to **reconnect on network change**, creating/configuring the TAP can make it
  bounce `wt0` several times at startup. WGL2Bridge now changes TAP settings only when they differ
  and waits for `wt0` to return, but disabling NetBird's network-change reconnect on these hosts
  removes the churn entirely.
- If the log shows `Unknown-unicast flood budget exceeded for <mac>`, traffic for that MAC is
  reaching the LAN side faster than `maxUnknownMacFloodPps`. Either the owning client is not
  connected (check `client <ip> idle_s ...` in the metrics), or a sender is using a static ARP entry
  for a host that is not on the segment. Raising the limit is safe only if you accept up to
  `limit × clients` packets per second over the tunnel.

## License

Copyright (c) 2025 KaicapTech

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
