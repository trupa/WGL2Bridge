# WGL2Bridge

Layer 2 Ethernet bridge for industrial networks, tunnelled over a WireGuard/NetBird overlay.

It reads raw Ethernet frames from a Windows TAP adapter, filters out consumer broadcast noise,
encapsulates the surviving frames (Raw / VXLAN / GRETAP) and sends them over the encrypted tunnel
to a peer, which injects them onto the remote industrial segment. PROFINET DCP discovery, EtherCAT,
Modbus TCP, EtherNet/IP and ARP all cross the tunnel as if the two segments were one switch.

---

## 1. How it works

```mermaid
flowchart LR
    PLC[PLC / HMI / Drive] <--> SEG1[Remote industrial segment]
    SEG1 <--> BR[Linux / OpenWrt bridge<br/>br-industrial]
    BR <--> VX[vxlan0]
    VX <-. UDP 4789 over WireGuard .-> WGL[WGL2Bridge.exe]
    WGL <--> TAP[Industrial-TAP adapter]
    TAP <--> TIA[TIA Portal / engineering PC]
```

Pipeline per direction:

| Stage | TAP → tunnel | Tunnel → TAP |
| --- | --- | --- |
| Read | `ReadFile` on the TAP device (overlapped/async) | `recvfrom` on the tunnel socket |
| Classify | `IndustrialFilter.ShouldForward` on the Ethernet frame | header validation, then the same filter |
| Encapsulate | constant header written once into buffer headroom | header stripped, inner frame offset computed |
| Write | `sendto` bound to the tunnel IP | `WriteFile` to the TAP device |

Design points:

- **Zero allocations in the packet loops.** One pooled `byte[]` per direction for the process
  lifetime; the tunnel header is constant so it is written once into the buffer's headroom and the
  TAP read lands directly after it. Frames are handled as `Span<byte>` slices, never copied.
- **Egress is pinned to the tunnel.** The socket is bound to the WireGuard/NetBird interface IP, so
  encapsulated frames cannot leak onto the physical LAN.
- **Async I/O throughout.** The TAP handle is opened with `FILE_FLAG_OVERLAPPED`; both pumps are
  `async`/`await` with no dedicated threads.
- **Clean shutdown.** Ctrl+C cancels a `CancellationToken`, which drains both pumps and releases the
  TAP handle — Windows will not leave the virtual adapter wedged.

### Filter policy

Applied in both directions when `EnableBroadIndustrialFilter` is true. Inspection is on the
EtherType at frame bytes 12–13, unwrapping up to two VLAN tags (802.1Q / 802.1ad).

**Always allowed**

| EtherType | Protocol |
| --- | --- |
| `0x0806` | ARP — required for device discovery across the bridge |
| `0x8892` | PROFINET RT / DCP |
| `0x88AB` | Ethernet POWERLINK |
| `0x88E1` | EtherCAT |
| `0x891D` | IEC 61850 GOOSE |
| anything else | broad pass (LLDP, PTP, vendor L2 protocols) |

**IPv4 / IPv6 — allowed except consumer discovery**

Prioritised industrial flows: Modbus TCP `502`, EtherNet/IP explicit TCP/UDP `44818`,
EtherNet/IP implicit UDP `2222`. All other TCP passes.

Dropped UDP ports (source *or* destination, both directions):

| Port | Protocol |
| --- | --- |
| 137, 138 | NetBIOS name / datagram |
| 1900 | SSDP / UPnP |
| 3702 | WS-Discovery |
| 5353 | mDNS |
| 5355 | LLMNR |
| 17500 | Dropbox LAN sync discovery |
| 27036 | Steam in-home streaming discovery |

Fragments after the first, ICMP, and truncated headers fail open (forwarded) — the endpoint discards
what it does not want.

### Encapsulation modes

| Mode | Wire format | Default port | Interoperates with |
| --- | --- | --- | --- |
| `Raw` | bare Ethernet frame in UDP | 55555 | another WGL2Bridge only |
| `Vxlan` | RFC 7348, 8-byte header | 4789 | Linux/OpenWrt `ip link add type vxlan` |
| `Gretap` | RFC 2784/2890, IP proto 47, type `0x6558` | n/a | Linux `ip link add type gretap` |

Use **VXLAN** for a Linux or OpenWrt peer. GRETAP works but needs a raw socket on Windows
(administrator), a firewall rule for IP protocol 47, and its receive behaviour varies by Windows
build. Use **Raw** only for Windows↔Windows.

---

## 2. Requirements

- Windows with .NET 9 SDK (or a published self-contained build).
- **tap-windows6** driver — ships with the OpenVPN installer as "TAP Virtual Ethernet Adapter".
  NetBird's and WireGuard's own adapters are Layer 3 only and **cannot** be used as the source TAP.
- WireGuard or NetBird tunnel already up.
- The bridge must run **elevated** — raw TAP device access requires it.

---

## 3. Quick start (Windows)

Create the TAP adapter once, in an elevated PowerShell:

```powershell
& "C:\Program Files\OpenVPN\bin\tapctl.exe" create --name "Industrial-TAP"
Enable-NetAdapter -Name "Industrial-TAP"
```

Give it a static IP in the remote PLC subnet, with **no gateway** (otherwise Windows parks it in
"unidentified network" and will not source traffic from it). Pick a subnet that is not already in
use on either host — check `ip a` on the peer for its LAN, Docker, and tunnel ranges first:

```powershell
New-NetIPAddress -InterfaceAlias "Industrial-TAP" -IPAddress 192.168.77.50 -PrefixLength 24
```

Optionally strip the Windows noise generators from the adapter (the filter drops their traffic
anyway, this just stops it being generated):

```powershell
Disable-NetAdapterBinding -Name "Industrial-TAP" -ComponentID ms_msclient,ms_server
```

Run it:

```powershell
dotnet run -- bridge.config.json
```

Expected output (console):

```
12:41:20.850  info: WGL2Bridge[0] NetBird: local desktop.sentros.cloud [100.99.58.65] -> peer site-1.sentros.cloud [100.99.243.76] (Connected)
12:41:20.858  info: WGL2Bridge[0] Encapsulation: VXLAN (RFC 7348), VNI 4096 :: 100.99.58.65:4789 -> 100.99.243.76:4789 (bound to 'wt0')
12:41:20.863  info: WGL2Bridge[0] Derived TAP MTU 1230 (tunnel 1280 - 36 encapsulation - 14 Ethernet).
12:41:20.866  info: WGL2Bridge[0] MTU report: TAP=1230, tunnel=1280
12:41:20.866  info: WGL2Bridge[0] Bridge running. Press Ctrl+C to stop.
```

Stats are emitted every 30 s only when counters change:

```
12:42:20.850  info: WGL2Bridge[0] [00:01:00] out=842 in=910 dropped=14
```

Format: `[uptime hh:mm:ss] out=<frames sent to tunnel> in=<frames received from tunnel> dropped=<frames filtered>`.

Set `"AutoCreateTapAdapter": true` and the bridge will run the `tapctl` + MTU + enable steps itself
when the adapter is missing (elevated only).

---

## 4. Configuration

`bridge.config.json`, loaded from the path given as the first argument, or from the executable
directory. Only four keys are normally needed — everything else is discovered at runtime.

```json
{
  "TapAdapterName": "Industrial-TAP",
  "PeerDiscovery": "Netbird",
  "RemotePeer": "",
  "EncapsulationMode": "Vxlan",
  "VxlanVni": 4096,
  "GreKey": 0,
  "EnableBroadIndustrialFilter": true,
  "AutoCreateTapAdapter": false
}
```

| Key | Default | Meaning |
| --- | --- | --- |
| `TapAdapterName` | `Industrial-TAP` | Windows connection name of the TAP adapter (as shown in Network Connections). |
| `PeerDiscovery` | `Netbird` | `Netbird` resolves both tunnel IPs from `netbird status --json`. `None` uses literal values. |
| `RemotePeer` | *(empty)* | NetBird FQDN, short hostname, or IP of the remote bridge. **Empty prompts for interactive selection.** Must be an IP when `PeerDiscovery` is `None`. |
| `EncapsulationMode` | `Raw` | `Raw`, `Vxlan` or `Gretap`. |
| `VxlanVni` | `4096` | 24-bit VXLAN network identifier. Must match the peer. |
| `GreKey` | `0` | 32-bit GRE key; `0` omits the key field. Must match the peer. |
| `EnableBroadIndustrialFilter` | `true` | Enables the pass/drop policy above. `false` bridges everything verbatim. |
| `AutoCreateTapAdapter` | `false` | Provisions a missing TAP adapter via `tapctl.exe` (needs elevation). |

Optional keys, add only when needed:

| Key | Default | Meaning |
| --- | --- | --- |
| `WireGuardAdapterName` | auto | Tunnel adapter name. Under NetBird the adapter is found by its tunnel IP; **required** when `PeerDiscovery` is `None`. |
| `EncapsulationPort` | mode default | Overrides the UDP port (Raw 55555, VXLAN 4789). |
| `TapMtuOverride` | auto | Pins the TAP MTU instead of deriving it. |
| `NetbirdCliPath` | auto | Explicit path to `netbird.exe` if not in the standard location or `PATH`. |
| `ReconnectDelaySeconds` | `5` | Wait between tunnel rebind attempts when NetBird drops or is still starting. The poll is read-only, so it never slows NetBird's own reconnect. |
| `LogFilePath` | `wgl2bridge.log.txt` | Text log path. Relative paths are resolved under the executable directory. |
| `ConsoleLogLevel` | `Information` | Minimum level written to console (`Trace`..`Critical`, `None`). |
| `FileLogLevel` | `Debug` | Minimum level written to the file sink (`Trace`..`Critical`, `None`). |

### Tunnel recovery

The bridge survives NetBird restarts and roaming IP changes. When the tunnel drops, the session
tears down, the bridge waits `ReconnectDelaySeconds`, re-reads the local tunnel address from the
adapter and binds a fresh socket — the TAP adapter, MAC table and counters survive. The peer
address is resolved once at startup and cached, so a flap never re-prompts for peer selection. A
misconfiguration (unknown peer, missing `netbird.exe`, bad TAP name) still fails fast rather than
retrying forever.

### Adapter self-configuration

The bridge runs elevated, so it configures the TAP adapter itself at startup instead of printing
instructions. See [TapNetworkConfigurator.cs](TapNetworkConfigurator.cs).

| Key | Default | Meaning |
| --- | --- | --- |
| `EnforceTapMtu` | `true` | Applies the derived MTU with `netsh ... store=persistent`. Removes MTU mismatch as a failure mode. Re-applied after every tunnel reconnect. |
| `TapAddressMode` | `Manual` | `Manual` leaves addressing alone, `Dhcp` leases from the remote segment, `Static` applies the address below. Re-applied after every reconnect. |
| `TapStaticAddress` | — | IPv4 address applied when the mode is `Static`. No gateway is ever set. |
| `TapStaticPrefixLength` | `24` | Prefix length for the static address. |
| `IsolateTapRouting` | `true` | Sets `ignoredefaultroutes=enabled` and metric 9999. |
| `DisableIpv6OnTap` | `false` | Unbinds `ms_tcpip6`, `ms_lltdio`, `ms_rspndr` to remove IPv6 discovery noise. |

**`IsolateTapRouting` matters most with DHCP.** A DHCP server on the industrial segment will hand
out a default gateway. If Windows prefers it over the real NIC, all host traffic — including the
WireGuard tunnel this bridge depends on — is routed into the tunnel and the link deadlocks. With
this enabled the adapter takes the address and DNS but ignores the default route.

### Learning and loop protection

| Key | Default | Meaning |
| --- | --- | --- |
| `EnableMacLearning` | `true` | Learns source MACs per port; unicast frames destined to a known-local MAC are not flooded across the tunnel. Broadcast, multicast and unknown destinations still flood (802.1D behaviour). |
| `MacAgingSeconds` | `300` | Entry lifetime before a MAC is considered unknown again. |
| `EnableLoopDetection` | `true` | Emits a tagged probe frame on both ports; receiving one back proves a second Layer 2 path exists. |
| `LoopProbeSeconds` | `5` | Probe interval. |
| `StopOnLoopDetected` | `true` | Stops the bridge on detection rather than only logging, preventing a broadcast storm. |

Loop probes use EtherType `0x88B5` (IEEE 802a local experimental) with a per-instance identifier,
and are consumed rather than bridged. Probes from a different bridge instance are silently
discarded, so only a genuine return of *this* instance's probe raises the alarm.

### Interactive peer selection

Leave `RemotePeer` empty and the bridge lists the NetBird mesh at startup:

```
Select the remote bridge peer:
  [1] site-142.sentros.cloud                   100.99.63.44     Connected
  [2] site-1.sentros.cloud                     100.99.243.76    Connected
Peer [1-2]: 2
```

Accepts an index or a hostname, auto-selects when there is exactly one peer, and fails with a clear
message if stdin is redirected — so it will not hang when run as a service.

### MTU

Derived at startup, no manual tuning required:

```
TAP MTU = tunnel MTU − outer header − 14 (inner Ethernet)
```

The outer header is 20 (IPv4) + 8 (UDP) + encapsulation header, or 20 + GRE header for GRETAP. The
WireGuard/NetBird overhead is already reflected in the tunnel adapter's own MTU, so it is not
double-counted. On a NetBird tunnel (MTU 1280):

| Mode | Derived TAP MTU |
| --- | --- |
| Raw | 1238 |
| VXLAN | 1230 |
| GRETAP (no key) | 1242 |

**The peer's bridge interfaces must use the same MTU**, otherwise large frames are silently dropped
in one direction only — the classic "discovery works, download fails" symptom.

---

## 5. Peer setup

The two ends are symmetric: each side's `RemotePeer` points at the other, and `VxlanVni` /
`GreKey` / port must match. Everything else is local.

### 5.1 Linux peer

No software needed — the kernel does VXLAN natively. Substitute the peer's own tunnel IP for
`<linux-nb-ip>` and the Windows tunnel IP for `<windows-nb-ip>`.

`eth1` is a placeholder for the **physical NIC cabled to the industrial segment** — not the NetBird
interface (`wt0`), and not the NIC carrying the box's management/internet connection. Bridging the
wrong one will blackhole the peer's own connectivity. Identify it with:

```bash
ip -br link            # all NICs, MACs, carrier state
ip route get 1.1.1.1   # prints "dev <wan-nic>" — exclude that one
sudo ethtool eth1 | grep -i 'link detected'
```

Names are often predictable rather than `ethN` (`enp3s0`, `eno2`, `ens192`). The industrial NIC must
be up with **no IP address** — it is a bridge port, not an endpoint:

```bash
sudo ip addr flush dev eth1
sudo ip link set eth1 up
```

```bash
sudo ip link add vxlan0 type vxlan id 4096 \
    local <linux-nb-ip> remote <windows-nb-ip> \
    dstport 4789 srcport 4789 4790 nolearning
sudo ip link set vxlan0 mtu 1230 up

sudo ip link add br-industrial type bridge
sudo ip link set eth1 master br-industrial
sudo ip link set vxlan0 master br-industrial
sudo ip link set eth1 mtu 1230
sudo ip link set br-industrial mtu 1230 up
```

`srcport 4789 4790` pins the source port — Linux otherwise randomises it, which makes ACLs and
packet captures harder to reason about.

Persist it:

```ini
# /etc/systemd/system/vxlan-industrial.service
[Unit]
Description=Industrial VXLAN bridge
After=netbird.service
Requires=netbird.service

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/sbin/ip link add vxlan0 type vxlan id 4096 local <linux-nb-ip> remote <windows-nb-ip> dstport 4789 srcport 4789 4790 nolearning
ExecStart=/sbin/ip link set vxlan0 mtu 1230 up
ExecStart=/sbin/ip link set vxlan0 master br-industrial
ExecStop=/sbin/ip link del vxlan0

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now vxlan-industrial
```

For **GRETAP** instead, swap the first two commands for:

```bash
sudo ip link add gretap0 type gretap local <linux-nb-ip> remote <windows-nb-ip>   # add `key 12345` to match GreKey
sudo ip link set gretap0 mtu 1242 up
```

### 5.2 OpenWrt peer

Install the VXLAN support packages (`vxlan` pulls in `kmod-vxlan`):

```sh
opkg update
opkg install kmod-vxlan vxlan
```

On this OpenWrt build, VXLAN is configured as an **interface** with `proto='vxlan'`, not as a
`device` section. The NetBird tunnel remains the transport, and the VXLAN interface is bridged into
the existing LAN bridge.

```sh
uci batch <<'EOF'
# Transport interface
set network.nb=interface
set network.nb.proto='none'
set network.nb.device='wt0'

# VXLAN interface — no peeraddr means it learns remote peers dynamically
set network.vxlan0=interface
set network.vxlan0.proto='vxlan'
set network.vxlan0.ipaddr='<openwrt-nb-ip>'
set network.vxlan0.port='4789'
set network.vxlan0.vid='4096'
set network.vxlan0.multipath='off'
set network.vxlan0.tunlink='nb'
set network.vxlan0.mtu='1230'
commit network
EOF
/etc/init.d/network reload
```

Attach `vxlan0` to the existing bridge. First find the bridge section name or index:

```sh
uci show network | grep -E 'br-lan|bridge|ports'
```

Then add the VXLAN interface as a bridge port on the existing bridge, for example:

```sh
uci batch <<'EOF'
add_list network.@device[0].ports='vxlan0'
commit network
EOF
/etc/init.d/network reload
```

Verify the interface exists and is attached to the bridge:

```sh
ip -d link show vxlan0
bridge link show
```

Allow only the tunnel transport in the firewall:

```sh
uci batch <<'EOF'
set firewall.netbird_zone=zone
set firewall.netbird_zone.name='netbird'
set firewall.netbird_zone.input='REJECT'
set firewall.netbird_zone.output='ACCEPT'
set firewall.netbird_zone.forward='REJECT'
add_list firewall.netbird_zone.network='nb'

set firewall.netbird_rule=rule
set firewall.netbird_rule.name='Allow-VXLAN-bridge'
set firewall.netbird_rule.src='netbird'
set firewall.netbird_rule.dest='lan'
set firewall.netbird_rule.proto='udp'
set firewall.netbird_rule.src_port='4789'
set firewall.netbird_rule.dest_port='4789'
set firewall.netbird_rule.target='ACCEPT'
commit firewall
EOF
/etc/init.d/firewall restart
```

If you want a dedicated industrial bridge instead of the existing LAN bridge, remove the LAN-side
attachment and create a separate bridge device, but keep the VXLAN as an interface section.

### 5.3 Windows peer

The same binary runs on both ends. On the second Windows host, repeat the TAP setup from §3 and use
a config that points back at the first:

```json
{
  "TapAdapterName": "Industrial-TAP",
  "PeerDiscovery": "Netbird",
  "RemotePeer": "desktop-4d6hdc7.sentros.cloud",
  "EncapsulationMode": "Raw",
  "EnableBroadIndustrialFilter": true
}
```

`Raw` is the lowest-overhead mode and is fine when both ends are this bridge. `Vxlan` also works
Windows↔Windows if you prefer one wire format everywhere.

Windows↔Windows caveats:

- Broadcast, multicast and unknown-destination frames flood both ways; MAC learning only suppresses
  unicast whose destination was last seen on the ingress port.
- The TAP adapter is an **endpoint**, not a bridge. Frames from the peer are visible to the local
  Windows IP stack only. To bridge onto a *physical* industrial LAN you must add the TAP and the
  physical NIC to a Windows bridge (Network Connections → select both → Bridge Connections), which
  is markedly less reliable than the Linux side. Prefer Linux/OpenWrt at the plant end.
- Do not bridge to a physical LAN at *both* ends of a network that already has another path between
  them — that is a Layer 2 loop. Loop detection will catch it and stop the bridge, but there is no
  STP to resolve it automatically.

### 5.4 NetBird access policy

Use a **bidirectional** policy between the two peers' groups, allowing the encapsulation transport in use:

- `Vxlan`: UDP 4789 (or `EncapsulationPort` override)
- `Raw`: UDP 55555 (or `EncapsulationPort` override)
- `Gretap`: IP protocol 47 (GRE)

One-way does not hold up:

- NetBird only links two peers at all when a policy exists — without one they never exchange keys.
- ACLs are enforced statefully, so a one-way policy lets *replies* back via conntrack, but any frame
  the remote segment initiates during an idle window is dropped.
- UDP conntrack expires in ~30 s, so the reverse direction flaps on a quiet segment.

For a pure TIA Portal *discovery* test a one-way policy usually works, because the PC always sends
the DCP Identify Request first and replies arrive in milliseconds. Anything beyond that — going
online, downloads, watch tables, device-initiated DCP Hello or LLDP — needs both directions.

---

## 6. Verifying

Windows:

```powershell
Get-NetAdapter -Name "Industrial-TAP"
```
Watch the bridge's `out= in= dropped=` counters. `out` climbing with `in` stuck at zero means the
return path is blocked — NetBird policy or the peer's bridge.

Linux/OpenWrt:

```sh
# Encapsulation traffic on the tunnel (pick the one matching your mode)
tcpdump -ni wt0 udp port 4789      # VXLAN (or your EncapsulationPort)
tcpdump -ni wt0 udp port 55555     # Raw (or your EncapsulationPort)
tcpdump -ni wt0 ip proto 47        # GRETAP

# Decapsulated frames on the bridge-side interface
tcpdump -eni vxlan0                # when using VXLAN
# tcpdump -eni gretap0             # when using GRETAP

bridge fdb show br br-industrial   # MACs learned from both sides
```

TIA Portal: set the **PG/PC interface** to `Industrial-TAP`, not the physical NIC, then run
Accessible Devices.

---

## 7. Troubleshooting

| Symptom | Cause |
| --- | --- |
| `No network adapter named 'X' was found` | TAP adapter missing or named differently — check Network Connections, or set `AutoCreateTapAdapter`. |
| `Failed to open TAP device ... error 5` | Not running elevated. |
| `TAP_IOCTL_SET_MEDIA_STATUS failed ... error 1` | The adapter is not a tap-windows6 device (e.g. a Wintun adapter). |
| `WireGuard adapter 'X' was not found` | Tunnel is down, or the adapter name changed — NetBird discovery falls back to matching by tunnel IP. |
| `NetBird peer 'X' was not found` | Wrong FQDN, or no policy links the peers so the peer is invisible. |
| Discovery works, downloads fail | MTU mismatch — the peer's bridge/VXLAN interfaces must match the derived TAP MTU. |
| `out` climbs, `in` stays 0 | One-way NetBird policy, peer firewall, or encapsulation mismatch (mode/port/VNI/GRE key/protocol). |
| `LOOP DETECTED` | A second Layer 2 path exists between the segments; remove the redundant link. |
| Devices appear then vanish | Device-initiated frames dropped by a unidirectional policy. |
| High `dropped` count | Normal — that is consumer broadcast noise being filtered. |

---

## 8. Limitations

- Windows-only (tap-windows6 P/Invoke); the peer side is kernel-native and needs no port.
- Flooding bridge for broadcast, multicast and unknown destinations. MAC learning suppresses
  known-local unicast, and loop detection reports a second path, but there is no STP.
- Point-to-point: one remote peer per instance.
- Peer IPs are resolved once at startup — a NetBird IP change needs a restart.
- Not suitable for hard real-time control across a WAN; intended for engineering access.
