# WGL2Bridge — Complete Code Documentation

> A single self-contained, Layer 2 Ethernet bridge that reads raw frames from a TAP-Windows6
> adapter, filters consumer broadcast noise, encapsulates the surviving frames (Raw / VXLAN),
> and sends them over a WireGuard/NetBird tunnel to a peer that injects them onto a remote
> segment — so two segments behave like one switch across an encrypted WAN.

This document is a full reference for the source code: every namespace, class, method and
configuration key, with a description of what each does. It complements (does not replace)
the operational `README.md`.

---

## Table of contents

1. [High-level overview](#1-high-level-overview)
2. [Repository / project layout](#2-repository--project-layout)
3. [Runtime architecture](#3-runtime-architecture)
4. [Execution lifecycle](#4-execution-lifecycle)
5. [Data-flow internals (two pumps)](#5-data-flow-internals-two-pumps)
6. [Concurrency model](#6-concurrency-model)
7. [Namespace & type reference](#7-namespace--type-reference)
   - [WGL2Bridge (Program)](#71-wgl2bridge--program-entry-point)
   - [WGL2Bridge.Config](#72-wgl2bridgeconfig)
   - [WGL2Bridge.Core](#73-wgl2bridgecore)
   - [WGL2Bridge.Logging](#74-wgl2bridgelogging)
   - [WGL2Bridge.Metrics](#75-wgl2bridgemetrics)
   - [WGL2Bridge.NativeMethods](#76-wgl2bridgenativemethods)
   - [WGL2Bridge.Netbird](#77-wgl2bridgenetbird)
   - [WGL2Bridge.Network](#78-wgl2bridgenetwork)
   - [WGL2Bridge.Peer](#79-wgl2bridgepeer)
   - [WGL2Bridge.Platform](#710-wgl2bridgeplatform)
   - [WGL2Bridge.Service](#711-wgl2bridgeservice)
   - [WGL2Bridge.Tap](#712-wgl2bridgetap)
   - [WGL2Bridge.Transport](#713-wgl2bridgetransport)
8. [Configuration reference](#8-configuration-reference)
9. [Encapsulation formats](#9-encapsulation-formats)
10. [Filtering & forwarding policy](#10-filtering--forwarding-policy)
11. [Error handling & reconnection](#11-error-handling--reconnection)
12. [Platform / build notes](#12-platform--build-notes)

---

## 1. High-level overview

WGL2Bridge is a Windows console application / Windows service written in C# targeting
**.NET 10** (`net10.0-windows`) and published as a **NativeAOT** `win-x64` executable. It has no
NuGet dependencies — everything is the base class library plus `advapi32`/`kernel32` P/Invoke.

It does three jobs:

1. **Read/write raw Ethernet frames** from a TAP-Windows6 virtual adapter (a raw character device).
2. **Filter, learn MACs, and rate-limit** those frames.
3. **Encapsulate** survivors (VXLAN or Raw IP) and **forward them over a WireGuard/NetBird tunnel**,
   and symmetrically decapsulate inbound tunnel packets back onto the TAP.

Two symmetric, concurrent forwarding pumps run per session, plus four supporting pumps
(probes, stats, interface watch, health check).

---

## 2. Repository / project layout

```
WGL2Bridge/
├─ Program.cs                     Entry point / CLI / mode dispatch
├─ WGL2Bridge.csproj              Build & NativeAOT settings
├─ WGL2Bridge.sln                 Solution file
├─ app.manifest                   requireAdministrator elevation manifest
├─ appsettings.json               Runtime configuration (camelCase)
├─ README.md                      Operational guide
├─ DOCUMENTATION.md               This file
│
├─ Config/
│  ├─ BridgeConfig.cs             Config record + validation
│  └─ ConfigJsonContext.cs        System.Text.Json source-gen context (AOT)
│
├─ Core/
│  └─ Bridge.cs                   Session orchestration + the six pumps
│
├─ Logging/
│  └─ BridgeLog.cs                Logging facade, console + rotating file sinks
│
├─ Metrics/
│  └─ MetricsEndpoint.cs          Loopback-only text/plain HTTP metrics
│
├─ NativeMethods/
│  └─ WindowsNative.cs            P/Invoke: DeviceIoControl, ReadFile/WriteFile, CreateFile, CancelIoEx
│
├─ Netbird/
│  └─ NetbirdStatus.cs            NetBird CLI wrapper (status + peer JSON parsing)
│
├─ Network/
│  ├─ Ethernet.cs                 Span-based frame parsing helpers
│  ├─ FrameFilter.cs              Frame policy engine (one table per frame field)
│  ├─ FilterPolicy.cs             Resolved policy: per-group drop sets
│  ├─ MacTable.cs                 Learning bridge FDB (open-addressed hash)
│  ├─ LoopDetector.cs             0x88B5 loop probes
│  ├─ BroadcastLimiter.cs         Per-second broadcast/multicast rate limiter
│  ├─ UnknownMacFloodLimiter.cs   Per-destination-MAC flood budget (server mode)
│  └─ WireGuardInterface.cs       Tunnel adapter IPv4 + MTU resolution
│
├─ Peer/
│  ├─ IPeerProvider.cs            PeerInfo record + provider interface
│  ├─ NetbirdPeerProvider.cs      NetBird-backed provider
│  └─ PeerResolver.cs             Exact-name/address peer resolution
│
├─ Platform/
│  ├─ Ipv4Cidr.cs                 IPv4/CIDR parsing + subnet-mask conversion
│  └─ ProcessRunner.cs            Timed, non-blocking external process runner
│
├─ Service/
│  └─ WindowsService.cs           Minimal advapi32 service host
│
├─ Tap/
│  ├─ TapAdapter.cs               Raw overlapped handle to the TAP device
│  ├─ TapProvisioner.cs           Name → device path + GUID resolution
│  ├─ TapCreator.cs               Creates the adapter when missing
│  └─ TapNetworkConfigurator.cs   Route isolation, address, MTU, DHCP renew
│
└─ Transport/
   ├─ IBridgeTransport.cs         Encapsulation strategy interface
   ├─ VxlanTransport.cs           VXLAN/UDP-4789 transport
   ├─ RawTransport.cs             Raw-IP transport
   └─ TransportFactory.cs         Mode → transport selection
```

---

## 3. Runtime architecture

```
                         raw Ethernet                       VXLAN/UDP (Raw IP)         WireGuard (NetBird)      raw Ethernet
+---------------------+   frames    +----------------+   ------------->   +----------------+  --------->  +----------------+   frames   +------------------+
|  Host application   | ----------> |  TAP adapter   |                    |     Bridge     |              |  Remote peer   | ---------> |   Remote LAN     |
|  (mgmt tool, shell) | <---------- | (TAP-Windows6) | <----------------  | (filter+encap) |  <--------   | (Linux/OpenWrt)| <--------- | (servers, hosts) |
+---------------------+             +----------------+                    +----------------+              +----------------+            +------------------+
```

Key design properties:

- **Symmetric pipeline** — the same filter/MAC/loop logic runs in both directions.
- **Fail-open policy** — frames forward unless explicitly blocked; truncated headers pass.
- **Pinned egress** — the encapsulation socket binds to the tunnel interface IP so frames cannot
  leak onto the physical LAN.
- **Self-healing sessions** — any pump fault tears the session down; the loop reconnects after
  `reconnectDelaySeconds`, reusing the cached peer address.
- **Zero-allocation hot path** — pooled buffers, `Span`-based parsing, fixed-size bitfields and an
  open-addressed MAC table.
- **AOT-safe** — all JSON uses source generation; no reflection-based serialization.

---

## 4. Execution lifecycle

`Program.Main` is the entry point. It:

1. **Parses arguments**
   - `--check` → dry-run validation mode.
   - `--service` → run under the Service Control Manager.
   - Any non-`--` argument → configuration file path (default `appsettings.json`).
2. **Loads config** via `LoadConfig` using the source-generated `ConfigJsonContext`.
3. **Validates config** via `BridgeConfig.Validate()`; any error prints to stderr and returns `1`.
4. **Initializes logging** (`BridgeLog.Initialize`).
5. **Dispatches** to one of three modes:
   - **Check mode** (`RunCheck`) — resolve peer, tunnel, TAP and compute MTU *without opening any
     device*; returns `0`/`1`.
   - **Service mode** — `WindowsService.Run("WGL2Bridge", RunBridge)` (blocks on the SCM dispatcher).
   - **Console mode** — wires `Ctrl+C` to a `CancellationTokenSource` and awaits `RunBridge`.

`RunBridge` performs the real startup:

1. Determines whether overlay discovery is required (`PeerName` set **or** `PeerAddress` empty).
   If not required, the NetBird CLI is never probed.
2. Resolves the peer (`PeerResolver` + `NetbirdPeerProvider`); throws if unknown.
3. Resolves the TAP adapter (`TapProvisioner`); creates it (`TapCreator`) if missing and allowed.
4. Applies route isolation and address configuration (`TapNetworkConfigurator`).
5. Constructs the `Bridge` and optionally starts `MetricsEndpoint.RunAsync`.
6. Awaits `Bridge.RunAsync(cancellationToken)`; on exit cancels metrics and awaits it.

---

## 5. Data-flow internals (two pumps)

### TAP → tunnel (`Bridge.PumpTapToTunnel`)

1. Rent a pooled buffer; pre-write the constant transport header into the headroom.
2. Overlapped `TapAdapter.ReadAsync` reads one frame into `buffer[headroom..]`.
3. `FrameFilter.Allow` → drop if blocked.
4. `MacTable.Learn` (source MAC → `Port.Tap`); `LoopDetector.Observe` → react to our own probe.
5. `MacTable.ShouldForward` → suppress known-local unicast.
6. `BroadcastLimiter.Allow` → storm control for multicast/broadcast.
7. `IBridgeTransport.SendAsync(buffer, headroom, length)` → `sendto` the peer.

### tunnel → TAP (`Bridge.PumpTunnelToTap`)

1. `IBridgeTransport.ReceiveAsync` → one packet into the pooled buffer.
2. `IBridgeTransport.ValidateIncoming` → returns inner-frame offset or `-1` (wrong VNI/protocol).
3. `FrameFilter.Allow` → drop if blocked.
4. `MacTable.Learn` (source MAC → `Port.Tunnel`, unless it equals our own TAP MAC — a reflection).
5. `LoopDetector.Observe`, `MacTable.ShouldForward`, `BroadcastLimiter.Allow`.
6. Overlapped `TapAdapter.WriteAsync` → inject the frame into the TAP device.

---

## 6. Concurrency model

`Bridge.RunAsync` opens a session and starts six pumps linked to a session
`CancellationTokenSource`:

| Pump | Cadence | Purpose |
| ---- | ------- | ------- |
| `PumpTapToTunnel` | continuous | Forwarding TAP → tunnel |
| `PumpTunnelToTap` | continuous | Forwarding tunnel → TAP |
| `PumpProbes` | `loopProbeIntervalSeconds` | Inject loop-detection probes |
| `PumpStats` | `statsIntervalSeconds` | Log aggregated counters |
| `PumpInterfaceWatch` | `tunnelHealthCheckSeconds` | Verify the tunnel adapter/IP is present |
| `PumpHealthCheck` | fixed 30 s | Peer connectivity + no-inbound-traffic heuristic |

`AwaitFirstFaultAsync` waits until the **first** pump faults or cancels and rethrows, tearing the
session down. `Task.WhenAll` is deliberately *not* used because the forwarding pumps are infinite
loops — a single failing watcher would otherwise never be observed.

Session teardown (`finally` block) drains **every** pump before disposing the TAP handle, because
disposing a handle with overlapped I/O in flight is undefined behavior (a prior shutdown hang).

Counters use `Interlocked` on `long` fields (no locks) and are read atomically for stats/metrics.

---

## 7. Namespace & type reference

### 7.1 `WGL2Bridge` — Program (entry point)

**File:** `Program.cs`

`public static class Program`

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `Main(string[] args)` | `static async Task<int>` | Parses args, loads & validates config, initializes logging, dispatches to check/service/console modes. Returns process exit code (`0` ok, `1` fatal/misconfig). Never retries config errors. |
| `RunCheck(BridgeConfig)` | `static bool` | Dry-run: resolves peer/tunnel/TAP and computes MTU without opening devices. A null peer is accepted in Lan/server mode. Returns `true` if the configuration resolves. Uses the real transport object (constructing it opens no socket) so the reported MTU matches runtime. |
| `RunBridge(BridgeConfig, CancellationToken)` | `static async Task` | Resolves runtime state, creates the adapter if needed, applies networking config (address + route isolation only when `bridgeSide` is `Client`), runs the `Bridge` plus the optional metrics endpoint. In Lan mode no peer is required; the bridge learns clients. |
| `LoadConfig(string path)` | `static BridgeConfig` | Reads a JSON file and deserializes via `ConfigJsonContext.Default.BridgeConfig`. |

Behavioural notes:
- Argument parsing: `--check`, `--service`, and the first non-`--` argument as the config path.
- Misconfiguration exits with code `1`; a fatal exception is caught and logged (`BridgeLog.Error`
  if logging is up, otherwise stderr).

---

### 7.2 `WGL2Bridge.Config`

#### `BridgeConfig` (`Config/BridgeConfig.cs`)

`public sealed record BridgeConfig` — the single source of truth for configuration. Every property
has a sensible default; `init`-only. Related enum `TransportMode` (`Raw`, `Vxlan`) lives here too.
See [§8](#8-configuration-reference) for the property/knob table.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `Validate()` | `IReadOnlyList<string>` | Returns a list of human-readable configuration problems (empty = valid). Resolves the whole filter policy (ports, protocols, ICMPv6 types, EtherTypes, VLANs, MAC ranges) plus names, enum, VNI ranges, timings, log settings and `TapIpAddress` CIDR syntax. |
| `GetWarnings()` | `IReadOnlyList<string>` | Non-fatal problems: keys ignored in the current `BridgeSide` (`PeerAddress`/`PeerName`/`TapIpAddress` in `Lan`; `AllowedClientPeers` and the client/server tuning keys in `Client`). Logged once at startup and by `--check`. |

#### `TransportMode` enum

| Value | Meaning |
| ----- | ------- |
| `Raw` | Bare Ethernet frame as the payload of a raw IP packet (peer needs matching software). |
| `Vxlan` | VXLAN (UDP/4789) encapsulation; Linux-kernel-peer compatible. Production default. |

#### `BridgeSide` enum

| Value | Meaning |
| ----- | ------- |
| `Client` | Default. The TAP is a standalone adapter: WGL2Bridge configures its address (DHCP or `TapIpAddress`), applies route isolation and renews DHCP after reconnect. |
| `Lan` | The TAP is a port of a Windows network bridge; the bridge/underlying NIC owns the LAN address. WGL2Bridge skips TAP address configuration, route isolation, MTU and DHCP renewal. Acts as the **multi-client server** side. |

#### Supported topologies

| Topology | Status |
| -------- | ------ |
| Client ↔ Lan (both ends WGL2Bridge) | Supported, primary deployment. |
| Client ↔ Linux kernel VXLAN bridged to its LAN | Supported; the peer applies no filters, so protection is inbound-only on our side. |
| Lan ↔ Lan (site-to-site join) | **Not officially supported** (expected to work as a symmetric server pair, but untested; MTU must be set manually on both Windows bridges and two DHCP servers would need split scopes in one subnet). |

#### `ConfigJsonContext` (`Config/ConfigJsonContext.cs`)

`public sealed partial class ConfigJsonContext : JsonSerializerContext` — compile-time JSON
serialization context (source generation) so NativeAOT never uses reflection-based serialization.
`[JsonSourceGenerationOptions]` sets camelCase naming, `UseStringEnumConverter`, indented output,
and omits nulls when writing. `[JsonSerializable(typeof(BridgeConfig))]` registers the type.

---

### 7.3 `WGL2Bridge.Core`

#### `Bridge` (`Core/Bridge.cs`)

`[SupportedOSPlatform("windows")] public sealed class Bridge : IAsyncDisposable`

Owns one bridging session: the TAP adapter, transport, filter, MAC table, loop detector, broadcast
limiters, and the tunnel interface descriptor. Re-resolves tunnel/peer every session so it survives
the adapter being destroyed/recreated or the peer address changing.

**Constructor** `Bridge(config, peerAddress, peerProvider, tapDevicePath, tapAdapterId)`
Builds all collaborators: `TapAdapter`, `TapNetworkConfigurator`, `IBridgeTransport` (via factory),
`FrameFilter` over a resolved `FilterPolicy`, `MacTable` (capacity 2048), `LoopDetector`, two
`BroadcastLimiter`s, an `UnknownMacFloodLimiter`, and a `WireGuardInterface` named from config.
`peerAddress` is nullable: it may be unresolved at startup when the overlay is not up yet, in which
case the session loop resolves it later.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `RunAsync(CancellationToken)` | `async Task` | Main loop: open session → start pumps → await first fault → drain → close → delay → reconnect, until cancellation or stop. A missing tunnel interface is treated as a wait state and retried every 1 s rather than counted as a session failure. |
| `AwaitFirstFaultAsync(Task[])` | `static async Task` | Waits for the first pump to fault/cancel, then rethrows. Ignores pumps that complete normally. |
| `RenewDhcpAfterForwardingStartedAsync(ct)` | `async Task` | Waits a short delay after recovery, then triggers `ipconfig /renew` so DHCPDISCOVER has a forwarding path. |
| `OpenSession()` | `void` | Resolves the tunnel, re-resolves the peer (throwing a recoverable `IOException` if it is still unavailable), computes buffer size, opens the TAP, logs TAP status/MAC, derives & applies TAP MTU, opens the transport bound to the local tunnel IP. |
| `RefreshPeer()` | `void` | Re-resolves the peer; updates the cached address on change. Throws `IOException` when no cached peer exists and resolution fails, or when the overlay reports the peer not connected — both are retried by the session loop. |
| `CloseSession()` | `void` | Closes the transport. The TAP handle is kept open across sessions so its media state does not toggle (a Windows network change that can make NetBird reconnect). |
| `LogTapOperationalStatus()` | `void` | Enumerates the TAP NIC and logs its operational status and IPv4 addresses (warns if not `Up`). |
| `PumpTapToTunnel(ct)` | `async Task` | The TAP → tunnel forwarding pump (see [§5](#5-data-flow-internals-two-pumps)). |
| `PumpTunnelToTap(ct)` | `async Task` | The tunnel → TAP forwarding pump. |
| `PumpStats(ct)` | `async Task` | Periodically logs per-direction counters at `Debug`; no-op if `StatsIntervalSeconds <= 0`. In server mode it also logs each connected client. |
| `PumpInterfaceWatch(ct)` | `async Task` | Liveness: checks the tunnel IP is still bindable (fails → `TunnelUnavailableException` → prompt retry). Heartbeat logged at `Trace`. Deliberately free of CLI calls so a hung process can't stall it. |
| `IsLocalAddressPresent(IPAddress)` | `static bool` | Binds a UDP socket to the address to test whether it is still assigned locally (`WSAEADDRNOTAVAIL` ⇒ gone). |
| `PumpHealthCheck(ct)` | `async Task` | Secondary checks on a fixed 30 s cadence: NetBird peer connected, and a "no inbound traffic while forwarding" heuristic. Heartbeat logged at `Trace`. |
| `ResolvePeerBoundedAsync(ct)` | `async Task<PeerInfo?>` | Runs peer resolution off-thread with a 10 s timeout so a hung CLI/API can't stall the loop. |
| `PumpProbes(ct)` | `async Task` | Every `loopProbeIntervalSeconds` builds one `LoopDetector` probe and both writes it to the TAP and sends it over the tunnel (single peer in client mode, all clients via `FloodToClientsAsync` in server mode), so loops formed on the local segment and loops formed through the tunnel are both observable. Rents `transport.HeaderLength + LoopDetector.ProbeLength` and pre-writes the transport header. No-op if loop detection is disabled. |
| `GetMetricsSnapshot()` | `string` | Returns a `text/plain` snapshot of all counters for the metrics endpoint, including one generated counter per configured filter value (`filter_mac_…`, `filter_ethertype_…`, `filter_ip_…`, `filter_udp_…`, `filter_tcp_…`, `filter_icmpv4_…`, `filter_icmpv6_…`, `filter_vlan`), emitted only while non-zero. In server mode it includes a `client <endpoint> idle_s <n> macs <n>` line per connected client. |
| `OnLoopDetected()` | `void` | Logs a loop error; sets `_stopRequested` + cancels the session when `StopOnLoopDetected`, else warns and continues. |
| `LogSessionFailure(Exception, ref string?, int)` | `void` | Logs a session failure, de-duplicating repeated identical messages to `Debug`. |
| `FormatMac(ReadOnlySpan<byte>)` | `static string` | Formats a 6-byte MAC as `AA:BB:CC:DD:EE:FF`. |
| `DisposeAsync()` | `ValueTask` | Sets stop, cancels the session, closes the session, disposes the transport and the TAP adapter. |

Private counters (all `long`, updated with `Interlocked`): `_tapFramesRead`, `_tapFilterDropped`,
`_tapMacSuppressed`, `_tapForwarded`, `_tunnelPacketsReceived`, `_tunnelInvalid`,
`_tunnelFilterDropped`, `_tunnelMacSuppressed`, `_tunnelWritten`, `_broadcastDropped`,
`_clientForwarded`, `_clientDropped`, `_clientRejected`, `_flooded`, `_unknownMacFlooded`,
`_unknownMacDropped`, `_remoteMacOwnerGone`, `_remoteMacsEvicted`, `_lastTapForwardMs`,
`_lastTunnelReceiveMs`. One-shot log flags: `_firstTapFrameLogged`,
`_firstTunnelPacketLogged`, `_tapMacReflectedLogged`.

#### Server (Lan) mode

When `bridgeSide` is `Lan`, the bridge acts as a **multi-client server**:

- Opens the transport with **no fixed peer**; inbound packets from any source are accepted
  (optionally restricted by `allowedClientPeers`).
- Learns each client's `IPEndPoint` from inbound traffic and maintains a **sticky** `MAC → client`
  binding table (`_remoteMacs`), alongside the existing `MacTable` for LAN-vs-tunnel classification.
  `_clientEndpointByAddress` maps a client address to its current endpoint so a client whose UDP
  source port changed is still matched.
- **TAP → tunnel:** broadcast/multicast is flooded to all clients (subject to `maxBroadcastPps`); a
  known remote MAC whose owner is connected is sent only to that client; a known LAN MAC is left to
  the Windows bridge; unknown unicast — and unicast whose remembered owner is offline — is flooded
  to all clients but **budgeted per destination MAC** by `UnknownMacFloodLimiter`
  (`maxUnknownMacFloodPps`, default 10/s). Exceeding the budget drops the client copies only; the
  frame still reaches the LAN, and a Warning naming the MAC is logged once per MAC.
- **Tunnel → TAP:** broadcast/multicast is written to the LAN and (optionally) flooded to other
  clients; known LAN unicast is written to the TAP; known remote unicast is forwarded directly to
  that client; unknown unicast is written to the LAN and flooded to other clients under the same
  per-MAC budget.
- **Sticky bindings.** A binding outlives the client's connection: `PruneClientsLocked` removes the
  client from `_clients` (and `_clientEndpointByAddress`) but keeps `_remoteMacs`; `RemoveMacsFor`
  no longer exists. Bindings expire only after `remoteMacAgingSeconds` (default 3600, validated to be
  >= `clientAgingSeconds`) and are capped at `maxRemoteMacs` (default 4096) with least-recently-seen
  eviction. This is what stops one-way traffic (syslog, SNMP traps, UDP video) to a quiet host from
  generating an unbounded N-way copy on every frame.
- `ResolveRemote(mac, nowMs, out endpoint)` returns a `RemoteMacState`: `Connected` (send one copy to
  the live endpoint), `OwnerDisconnected` (remembered but not connected — falls back to the budgeted
  flood, which is how a MAC that moved to another client is rediscovered), or `Unknown` (never seen).
- Clients idle longer than `clientAgingSeconds` stop counting as connected; at most `maxClients` are
  tracked, least-recently-seen evicted first.
- `allowClientToClient: false` keeps client-originated traffic on the LAN and never forwards it to
  another client.
- Counters: `clients`, `client_forwarded`, `client_dropped`, `client_rejected`, `flooded`,
  `unknown_mac_flooded`, `unknown_mac_dropped`, `remote_mac_owner_gone`, `remote_macs_evicted`.
- **Client mode is unaffected:** with a single peer nothing is multiplied, `_remoteMacs` is never
  populated, and the flood limiter is never consulted — unknown unicast is one `SendAsync`. All of
  the above is server-mode-only behavior.

Server-mode helpers (all called under `_clientLock` except the pumps and async members):

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `SeedClient(IPAddress)` | `void` | Seeds a configured client address so the first frame matches a known client. |
| `IsClientAllowed(IPAddress)` | `bool` | `allowedClientPeers` check; empty list accepts any peer. |
| `TouchClient(IPEndPoint, nowMs)` | `void` | Refreshes the client, updates `_clientEndpointByAddress`, evicts the least-recently-seen client at `maxClients` (keeping its MAC bindings), and prunes every 30 s. |
| `LearnRemoteMac(mac, endpoint, nowMs)` | `void` | Inserts or refreshes a binding; evicts the least-recently-seen binding when at `maxRemoteMacs`. |
| `EvictOldestRemoteMacLocked()` | `void` | Removes the oldest binding and increments `_remoteMacsEvicted`. |
| `ResolveRemote(mac, nowMs, out endpoint)` | `RemoteMacState` | `Connected` / `OwnerDisconnected` / `Unknown`; expires bindings older than `remoteMacAgingSeconds` and resolves the owner through `_clientEndpointByAddress`. |
| `PruneClientsLocked(nowMs)` | `void` | Drops idle clients and their address mapping, then calls `PruneRemoteMacsLocked`. |
| `ForgetClientAddressLocked(IPEndPoint)` | `void` | Removes the address→endpoint mapping only if it still points at the given endpoint. |
| `PruneRemoteMacsLocked(nowMs)` | `void` | Expires bindings past `remoteMacAgingSeconds` in one pass, then enforces `maxRemoteMacs`. |
| `SnapshotClients()` | `IPEndPoint[]` | Copies the live client endpoints (after pruning) for flooding. |
| `BuildClientReport()` | `string` | `client <endpoint> idle_s <n> macs <n>` per connected client (MACs counted per address). |
| `FloodToClientsAsync(buffer, offset, length, except, ct)` | `async Task` | Writes the client header once, then sends to every live client except `except`; increments `_flooded`. |
| `EnsureClientHeader(buffer, frameOffset)` | `void` | Re-writes the transport header immediately before the frame. |

---

### 7.4 `WGL2Bridge.Logging`

**File:** `Logging/BridgeLog.cs`

#### `LogLevel` enum
`Trace=0, Debug=1, Information=2, Warning=3, Error=4, Critical=5`.

#### `BridgeLog`
`public static class BridgeLog` — the logging facade used everywhere. Explicit-level methods; **not**
used in the per-packet hot path except for exceptional/one-shot conditions. Each sink has its own
minimum level configured independently.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `IsInitialized` | `bool` (get) | True once `Initialize` has succeeded. |
| `Initialize(BridgeConfig)` | `static void` | Builds the console sink and (if a path is set) the file sink; a file-open failure is reported to stderr but is non-fatal. |
| `Info(message, category="Bridge")` | `static void` | Lifecycle / milestone events. |
| `Trace(message, category="Bridge")` | `static void` | Very fine-grained diagnostics (periodic heartbeats, per-tick detail). |
| `Debug(message, category="Bridge")` | `static void` | Diagnostic detail (resolved config, retries). |
| `Warning(message, category="Bridge")` | `static void` | Recoverable conditions. |
| `Error(message, category="Bridge")` | `static void` | Fatal / configuration failures. |
| `Write(LogLevel, message, category)` | `static void` | Guards `Sinks` with a lock and dispatches to each sink whose minimum level is met. |

#### `ILogSink` (internal)
`LogLevel MinimumLevel { get; }` and `void Write(LogLevel, string, string)` — one logging destination.

#### `ConsoleLogSink` (internal)
`SimpleConsole`-style: `HH:mm:ss.fff message`, single line, no color (clean redirection).

#### `FileLogSink` (internal)
Plain-text file sink: `yyyy-MM-dd HH:mm:ss.fff [Level] category: message`, `AutoFlush`, created
under a created directory if needed. `Rotate()` moves the file to `<path>.1` (overwriting an
existing `.1`) when `_maxBytes` would be exceeded. Constructor creates the directory and opens the
writer.

---

### 7.5 `WGL2Bridge.Metrics`

#### `MetricsEndpoint` (`Metrics/MetricsEndpoint.cs`)

`[SupportedOSPlatform("windows")] public static class MetricsEndpoint` — a minimal, loopback-only
`text/plain` HTTP endpoint built on a raw `TcpListener` (avoids `HttpListener` URL ACLs; AOT-friendly).

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `RunAsync(port, snapshot, ct)` | `static async Task` | Listens on `127.0.0.1:<port>` and logs the listening URL at `Information`; accepts clients and dispatches each to `HandleClientAsync` on the thread pool. Stops on cancellation. |
| `HandleClientAsync(TcpClient, Func<string>)` | `static async Task` | Reads (and discards) the request, writes an HTTP/1.1 200 with `Content-Type: text/plain; charset=utf-8` and the `snapshot()` body, then closes. Swallows client-disconnect errors. |

---

### 7.6 `WGL2Bridge.NativeMethods`

#### `WindowsNative` (`NativeMethods/WindowsNative.cs`)

`internal static partial class WindowsNative` — source-generated P/Invoke declarations for the TAP
data path and setup. TAP-Windows6 uses `METHOD_BUFFERED` control codes rooted at
`FILE_DEVICE_UNKNOWN` (0x22), matching OpenVPN's `tap-windows.h`.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `TapIoctlGetMac` | `uint` (get) | `TAP_WIN_IOCTL_GET_MAC` control code. |
| `TapIoctlSetMediaStatus` | `uint` (get) | `TAP_WIN_IOCTL_SET_MEDIA_STATUS` control code. |
| `CtlCode(uint function)` | `static uint` | Builds the `CTL_CODE` value (device/access/function/method). |
| `DeviceIoControl(...)` | `static partial bool` | Synchronous IOCTL (no OVERLAPPED) for one-shot setup calls. |
| `ReadFile(...)` | `static unsafe partial bool` | Overlapped TAP read; `lpNumberOfBytesRead` must be null. |
| `WriteFile(...)` | `static unsafe partial bool` | Overlapped TAP write; `lpNumberOfBytesWritten` must be null. |
| `CreateFile(...)` | `static partial nint` | `CreateFileW` opening the TAP path with `FILE_FLAG_OVERLAPPED` explicitly. |
| `CancelIoEx(SafeFileHandle, NativeOverlapped*)` | `static unsafe partial bool` | Cancels pending overlapped I/O; a null OVERLAPPED cancels all pending ops on the handle. |

Constants: `GenericRead`, `GenericWrite`, `FileShareRead`, `FileShareWrite`, `OpenExisting`,
`FileAttributeSystem`, `FileFlagOverlapped`.

---

### 7.7 `WGL2Bridge.Netbird`

#### `NetbirdStatus` (`Netbird/NetbirdStatus.cs`)

`[SupportedOSPlatform("windows")] public sealed class NetbirdStatus` — wraps the NetBird CLI. The
CLI is used to (1) verify daemon connectivity and (2) resolve the configured `PeerName` FQDN to a
peer IP. Only an exact FQDN match is accepted; JSON parsing is tolerant (`JsonDocument`) because the
CLI schema is not version-stable.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `NetbirdStatus(configuredPath, peerName)` | ctor | Resolves the CLI path and stores the target peer FQDN. |
| `CliAvailable` | `bool` (get) | True when `netbird.exe` was located. |
| `ResolveCliPath(string?)` | `static string?` | Uses the configured path if valid, else searches `PATH`, then `C:\Program Files\NetBird` and `(x86)`. |
| `IsConnected()` | `bool` | Runs `netbird status`; true when exit code 0 and output contains "Connected" (case-insensitive). |
| `TryDiscoverPeer()` | `PeerInfo?` | Tries `status --json` then `peers list --json`, parsing with `TryParsePeerJson`; returns the first successful result. |
| `TryParsePeerJson(json, peerName, out PeerInfo?)` | `static bool` | Parses peers from either schema shape (`peers.details[]` or `peers[]`), strips `/prefix` from `netbirdIp`, and returns **only** an exact FQDN match on `peerName`. Returns false when `peerName` is empty or unmatched — it never falls back to another (even connected) peer. Tolerates `JsonException`. |

---

### 7.8 `WGL2Bridge.Network`

#### `Ethernet` (`Network/Ethernet.cs`)

`public static class Ethernet` — span-based, allocation-free Ethernet frame helpers.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `HeaderLength` | `const int` = 14 | Untagged Ethernet header length. |
| `MacLength` | `const int` = 6 | MAC address length. |
| `BroadcastMac` | `const ulong` = `0xFFFF_FFFF_FFFF` | Broadcast MAC. |
| `Vlan`/`QinQ`/`Ipv4`/`Ipv6`/`LoopProbe` | `const ushort` | EtherTypes: `0x8100`, `0x88A8`, `0x0800`, `0x86DD`, `0x88B5`. |
| `ReadMac(ReadOnlySpan<byte>)` | `static ulong` | Reads the destination MAC as a 48-bit value. |
| `ReadUInt16BigEndian(frame, offset)` | `static ushort` | Big-endian 16-bit read at an offset. |
| `IsMulticast(ulong mac)` | `static bool` | Tests the I/G bit of a 48-bit MAC. |
| `IsMulticast(ReadOnlySpan<byte> frame)` | `static bool` | Tests the I/G bit of the frame's first byte (multicast or broadcast). |
| `TryParseEtherType(string?, out ushort)` | `static bool` | Parses hex text with optional `0x` prefix (`0x88CC` / `88CC`); false if malformed or > 0xFFFF. |
| `ReadEtherType(frame, out int l3Offset)` | `static ushort` | Walks VLAN/QinQ tags and returns the inner EtherType plus the L3 header offset. |

#### `FrameFilter` (`Network/FrameFilter.cs`)

`public sealed class FrameFilter` — evaluates a pre-resolved `FilterPolicy` and reports which group
rejected a frame. Every lookup is a pre-built table or `HashSet` — O(1) and allocation free.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `FrameFilter(FilterPolicy)` | ctor | Takes the resolved policy (see `FilterPolicy.Resolve`); the filter itself holds no configuration parsing. |
| `Allow(frame)` | `bool` | Convenience overload ignoring the reason. |
| `Allow(frame, out FilterDrop drop)` | `bool` | Main entry: truncated frame → pass; destination-MAC table; VLAN allow-list; EtherType table; then IPv4/IPv6 evaluation. `drop` carries the field that rejected the frame plus the value that matched. |
| `IsLinkLocal(frame, macs, out mac)` | `static bool` | Multicast-bit check plus one `HashSet<ulong>` lookup; returns the destination MAC so the drop can be attributed. |
| `VlanAllowed(frame)` | `bool` | True when VLAN filtering is off, or the frame carries an allowed 802.1Q/QinQ tag. Untagged frames are dropped only when an allow list is configured. |
| `AllowIpv4(ip, out drop)` | `bool` | Protocol table, then ICMPv4 type (when configured) or TCP/UDP port; later fragments and other protocols pass; truncated headers pass. |
| `AllowIpv6(ip, out drop)` | `bool` | Walks extension headers (0/43/60), passes fragments (44), checks the final next-header against the protocol table, ICMPv6 types, then TCP/UDP ports. |
| `AllowTransport(ip, ihl, isUdp, out drop)` | `bool` | IPv4 transport-port lookup; later fragments and truncated headers pass. |
| `AllowTransport6(ip, offset, isUdp, out drop)` | `bool` | Same for an IPv6 transport header at `offset`. |

#### `FilterPolicy` (`Network/FilterPolicy.cs`)

`public sealed class FilterPolicy` — the effective policy: one plain list per frame field, so a value
can only be in the list for the field it belongs to. Config semantics: `null` means the built-in
default set, an explicit array replaces it, `[]` means "drop nothing" — there is no separate
enable/disable flag and no concept grouping.
One rule is not configurable: **802.3x PAUSE (`01:80:C2:00:00:01`) is always dropped**, because 802.3
does not allow bridges to forward MAC Control frames. `CollectWarnings` reports a destination-MAC list
that no longer drops STP BPDUs, since cross-site loop protection then rests solely on `LoopDetector`.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `FilterDropReason` | enum | `None`, `LinkLocal`, `Vlan`, `EtherType`, `IpProtocol`, `UdpPort`, `TcpPort`, `Icmpv4Type`, `Icmpv6Type`. |
| `FilterDrop` | `readonly record struct` | `(FilterDropReason Reason, ulong Value)` — the field that matched and the exact value; `FilterDrop.None` for a forwarded frame. |
| `DefaultLinkLocalMacs` | `string[]` | `01:80:C2:00:00:00-0F`, Cisco `01:00:0C:CC:CC:CC`/`CC:CD`. |
| `DefaultIpProtocols` | `int[]` | `2, 88, 89, 103, 112` (IGMP/EIGRP/OSPF/PIM + VRRP/CARP). |
| `DefaultUdpPorts` | `int[]` | The eight discovery ports plus HSRP `1985` and GLBP `3222`. |
| `Resolve(config, errors?)` | `static FilterPolicy` | Builds the tables and the configured-value arrays, honouring null ⇒ built-in / array ⇒ replacement / empty ⇒ none; always adds the PAUSE MAC; appends validation errors (invalid ports, protocols, ICMP types, EtherTypes, VLANs, MAC ranges). Called by `BridgeConfig.Validate`. |
| `CollectWarnings(config, warnings)` | `static void` | Warns when `DropLinkLocalMacs` no longer drops the STP MAC (loop detection becomes the only cross-site guard). Called by `BridgeConfig.GetWarnings`. |
| `Description` | `string` | One-line effective-policy summary logged once at session start. |
| `…Values` properties | `int[]` / `ushort[]` / `ulong[]` | The configured values in configuration order; the bridge sizes one counter per entry from these and names metrics after them. |
| `HasTcpPorts` / `HasIcmpv4Types` / `HasIcmpv6Types` | `bool` | False when that list is empty, so `FrameFilter` skips the lookup on the hot path. |

#### `MacTable` (`Network/MacTable.cs`)

`public sealed class MacTable` — a learning bridge forwarding table (IEEE 802.1D). Fixed-size
open-addressed hash map keyed on a 48-bit MAC; no steady-state allocations.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `Port` enum | `None=0, Tap=1, Tunnel=2` | Ingress port a MAC was last seen on. |
| `MacTable(capacity=2048, aging=null)` | ctor | Rounds capacity up to a power of two; default aging 300 s. |
| `Learn(mac, port, nowMs)` | `void` | Learns/refreshes the MAC→port mapping; ignores zero/multicast MACs; linear-probes; best-effort evicts when full. Lock-guarded. |
| `Resolve(mac, nowMs)` | `Port` | Resolves a destination MAC's port; returns `None` when absent or expired (lazy aging). |
| `ShouldForward(destMac, ingressPort, nowMs)` | `bool` | Broadcast/multicast always flood; known-same-port unicast is suppressed. |
| `Hash(ulong)` | `static ulong` | 64-bit mix (Murmur-style finalizer) for bucket selection. |

#### `LoopDetector` (`Network/LoopDetector.cs`)

`public sealed class LoopDetector` — inserts 0x88B5 probes tagged with a per-instance random ID;
only our own probe returning raises the alarm (foreign probe traffic is ignored). `Bridge.PumpProbes`
writes each probe to the TAP **and** sends a copy over the tunnel, so a loop that closes only through
the tunnel is detected as well; `Observe` is therefore called on both the TAP and the tunnel receive
paths.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `ProbeLength` | `const int` = 22 | 14-byte header + 8-byte payload. |
| `LoopDetector()` | ctor | Generates a fresh random instance ID (`RandomNumberGenerator`). |
| `LoopDetected` | `bool` (get) | True once our own probe has returned. |
| `BuildProbe(destination, sourceMac)` | `int` | Writes broadcast 0x88B5 probe with magic `WGL2` + instance ID; returns 22. Throws if the buffer is too small. |
| `Observe(frame)` | `bool` | Returns true exactly once when the frame is our own probe (EtherType + magic + instance ID match). |
| `TimeToSend(nowMs, interval)` | `bool` | Rate-gate for probe injection; updates the last-send timestamp. |

#### `BroadcastLimiter` (`Network/BroadcastLimiter.cs`)

`public sealed class BroadcastLimiter` — per-second broadcast/multicast storm control (zero
allocations; tiny critical section because broadcast is not the hot path).

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `BroadcastLimiter(maxPerSecond)` | ctor | `<= 0` disables limiting. |
| `Allow(nowMs)` | `bool` | Rolls the 1-second window when elapsed, then admits frames up to `_maxPerSecond`. |

#### `UnknownMacFloodLimiter` (`Network/UnknownMacFloodLimiter.cs`)

`public sealed class UnknownMacFloodLimiter` — per-destination-MAC flood budget for unknown unicast,
**used in Lan/server mode only**. One unresolved MAC floods one copy per connected client, so an
uncapped flood multiplies LAN ingress by the client count; this caps the multiplication per MAC
while leaving other MACs untouched. State is a fixed-size open-addressed table, so a flood of
distinct destination MACs cannot grow memory.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `Capacity` | `const int` = 256 | Destination MACs tracked concurrently; when full, the oldest window is recycled. |
| `UnknownMacFloodLimiter(maxPerSecond)` | ctor | `<= 0` disables limiting. |
| `MaxPerSecond` | `int` (get) | Configured budget, echoed in the Warning log line. |
| `Allow(mac, nowMs)` | `bool` | Convenience overload without the first-clip signal. |
| `Allow(mac, nowMs, out firstClip)` | `bool` | Rolls the 1-second window per MAC, admits frames up to the budget; `firstClip` is true once per tracked MAC so the caller can log the first drop without flooding the log (`Warned` is never reset until the slot is recycled). |
| `Hash(ulong)` | `static int` | 64-bit multiply/mix folded into the table index. |

#### `WireGuardInterface` (`Network/WireGuardInterface.cs`)

`[SupportedOSPlatform("windows")] public sealed class WireGuardInterface` — resolves the overlay
tunnel adapter's IPv4 address (socket bind target) and MTU (for TAP MTU derivation).
`TunnelUnavailableException` (same file) signals that the interface or its IPv4 address is missing —
expected while NetBird reconnects after a network change; the bridge waits and retries.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `Name` | `required string` (init) | Tunnel interface friendly name. |
| `LocalAddress` | `IPAddress?` (get) | Local IPv4 address (bind target). |
| `Mtu` | `int` (get, default 1500) | Interface MTU. |
| `Resolve(BridgeConfig)` | `void` | Finds the NIC by name (throws if missing); uses `TunnelLocalAddress` override or the first non-link-local IPv4 (throws if none); queries the MTU. |
| `IsLinkLocal(IPAddress)` | `static bool` | Detects `169.254.x.x` APIPA addresses. |
| `QueryMtu()` | `int` | Parses `netsh interface ipv4 show interfaces` for the adapter's MTU; defaults to 1500. |

---

### 7.9 `WGL2Bridge.Peer`

#### `IPeerProvider` + `PeerInfo` (`Peer/IPeerProvider.cs`)

- `public sealed record PeerInfo(IPAddress Address, bool Connected)` — a resolved peer.
- `public interface IPeerProvider` — `string Name { get; }` and `PeerInfo? Resolve()`.

#### `NetbirdPeerProvider` (`Peer/NetbirdPeerProvider.cs`)

`public sealed class NetbirdPeerProvider(NetbirdStatus netbird) : IPeerProvider` — provider backed
by the NetBird daemon CLI. `Name => "NetBird"`; `Resolve()` delegates to `netbird.TryDiscoverPeer()`.

#### `PeerResolver` (`Peer/PeerResolver.cs`)

`public sealed class PeerResolver(BridgeConfig config, IPeerProvider overlay) : IPeerProvider`
— resolves the configured peer without guessing. Resolution precedence:

1. If `PeerName` is set → overlay discovery, accepting **only** an exact FQDN match.
2. If `PeerAddress` is set (or the named peer was not found) → parse as IP, or resolve the hostname
   via DNS (IPv4).
3. Otherwise → `null` (the caller logs and waits); it never selects an arbitrary connected peer.

`Name => overlay.Name`.

---

### 7.10 `WGL2Bridge.Platform`

#### `Ipv4Cidr` (`Platform/Ipv4Cidr.cs`)

`public static class Ipv4Cidr` — IPv4/CIDR parsing used only during configuration.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `TryParse(value, out address, out prefixLength)` | `static bool` | Parses `a.b.c.d[/prefix]`; a bare address defaults to `/24`; validates prefix 1–32. |
| `PrefixToSubnetMask(prefixLength)` | `static string` | Converts a prefix length to a dotted-quad mask (e.g. 24 → `255.255.255.0`). |

#### `ProcessRunner` (`Platform/ProcessRunner.cs`)

`internal static class ProcessRunner` — runs CLI tools without leaking console windows; drains both
streams concurrently to avoid deadlock.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `Run(fileName, arguments, timeoutMs=20000)` | `static (int, string)` | Runs to completion, returning exit code + combined stdout/stderr. On timeout, kills the process tree and returns `(-1, "…timed out…")`. |
| `Drain(Task<string>)` | `static string` | Blocking-await helper for a stream reader; returns the exception message on failure. |

---

### 7.11 `WGL2Bridge.Service`

#### `WindowsService` (`Service/WindowsService.cs`)

`[SupportedOSPlatform("windows")] public static partial class WindowsService` — a minimal Windows
Service host implemented directly over `advapi32`, so the single NativeAOT binary runs under the SCM
with no extra package.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `Run(serviceName, runner)` | `static unsafe void` | Registers the service main with `StartServiceCtrlDispatcher` and blocks until the service stops. Throws if not started by the SCM (Win32 error 1063). |
| `ServiceMain(uint, nint)` | `[UnmanagedCallersOnly] static unsafe void` | Registers the control handler, reports `START_PENDING` → `RUNNING`, invokes the runner, then reports `STOPPED`. |
| `ServiceControlHandler(control, ...)` | `[UnmanagedCallersOnly] static uint` | Handles `STOP`/`SHUTDOWN` (report `STOP_PENDING`, cancel the CTS), `INTERROGATE`, and returns `ERROR_CALL_NOT_IMPLEMENTED` otherwise. |
| `SetStatus(state, waitHint, checkpoint, exitCode)` | `static void` | Fills and reports a `SERVICE_STATUS` struct (type `WIN32_OWN_PROCESS`, accepts stop/shutdown). |

P/Invoke: `StartServiceCtrlDispatcherW`, `RegisterServiceCtrlHandlerExW`, `SetServiceStatus`.

---

### 7.12 `WGL2Bridge.Tap`

#### `TapAdapter` (`Tap/TapAdapter.cs`)

`[SupportedOSPlatform("windows")] public sealed class TapAdapter : IDisposable` — a raw handle to a
TAP-Windows6 virtual Ethernet device, opened with `FILE_FLAG_OVERLAPPED`. The data path uses explicit
overlapped `ReadFile`/`WriteFile` bound to the thread pool (`ThreadPoolBoundHandle`) — the same
mechanism OpenVPN uses — because higher-level APIs don't reliably deliver frames from this device.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `TapAdapter(devicePath)` | ctor | Stores the Win32 device path (`\\.\Global\{GUID}.tap`). |
| `DevicePath` | `string` (get) | The device path. |
| `MacAddress` | `ReadOnlySpan<byte>` (get) | Driver-reported MAC, refreshed on each open. |
| `IsOpen` | `bool` (get) | True while the handle is valid. |
| `Open()` | `bool` | `CreateFile` with overlapped flag, binds the handle to the thread pool, queries the MAC, forces media status connected. Idempotent: returns `false` if the handle is already open, so the adapter's media state is not toggled on every tunnel reconnect. |
| `ReadAsync(buffer, offset, count, ct)` | `ValueTask<int>` | Overlapped read of one frame. |
| `WriteAsync(buffer, offset, count, ct)` | `ValueTask<int>` | Overlapped write of one frame. |
| `StartOperation(read, buffer, offset, count, ct)` | `ValueTask<int>` (private, unsafe) | Core machinery: pins the buffer, allocates a `NativeOverlapped`, registers cancellation **before** issuing I/O, handles `ERROR_IO_PENDING` (997), and cancels explicitly if the token fired mid-flight. |
| `IoCompletion(...)` | `static unsafe void` | Thread-pool completion callback → `OverlappedState.Complete`. |
| `QueryMac()` | `void` | Issues `GET_MAC`; throws on failure. |
| `SetMediaStatus(bool)` | `void` | Issues `SET_MEDIA_STATUS` (1 = connected); throws on failure. |
| `Ioctl(code, input, output)` | `bool` | Wraps `DeviceIoControl`. |
| `Dispose()` | `void` | Disposes the handle and the bound handle. |

`OverlappedState` (private, unsafe nested class): per-operation state holding the handle, bound
handle, `TaskCompletionSource<int>`, buffer pin, `NativeOverlapped*`, and a cancellation
registration.
- `FromPointer` recovers the state from a `NativeOverlapped*`.
- `TryCancel()` calls `CancelIoEx` with a **null** OVERLAPPED (never touches the possibly-freed
  per-op pointer, so it is safe to race the completion).
- `Free()` frees the native overlapped and the buffer pin.
- `Complete(errorCode, numBytes)` disposes the registration, frees resources, and resolves the task:
  `0` → result, `995` (`ERROR_OPERATION_ABORTED`) → canceled, otherwise a faulted `IOException`.

#### `TapAdapterInfo` + `TapProvisioner` (`Tap/TapProvisioner.cs`)

- `public sealed record TapAdapterInfo(string Name, string AdapterId, string DevicePath, string MacAddress)`.
- `[SupportedOSPlatform("windows")] public sealed class TapProvisioner` — resolves a named adapter to
  its Win32 device path and GUID (`NetCfgInstanceId`).

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `Resolve(tapName)` | `TapAdapterInfo` | Locates by name or throws. |
| `TryResolve(tapName, out info)` | `bool` | Attempts to locate by name; builds `\\.\Global\{GUID}.tap` and the MAC string. |
| `NormalizeGuid(id)` | `static string` | Ensures the GUID is wrapped in braces. |

#### `TapCreator` (`Tap/TapCreator.cs`)

`[SupportedOSPlatform("windows")] public sealed class TapCreator` — creates a TAP-Windows6 adapter
when none exists. Prefers OpenVPN's `tapctl.exe` (creates and names in one step, needs no INF), and
falls back to the devcon-style `tapinstall.exe`/`devcon.exe` with `OemVista.inf`.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `TryCreate(tapName, config, out error)` | `bool` | No-op if the adapter already exists; resolves a tool then delegates to the tapctl or tapinstall path. |
| `TryCreateWithTapCtl(tool, tapName, config, out error)` | `static bool` | Runs `tapctl create --name … --hwid …`. |
| `TryCreateWithTapInstall(tool, tapName, config, out error)` | `static bool` | Locates the INF, snapshots existing adapters, runs `install "<inf>" <hwid>`, waits for a new adapter, and renames it via `netsh` if necessary. |
| `ResolveTool(configuredPath, out isTapCtl)` | `static string?` | Resolves the creation tool from config, `PATH`, or default install dirs; reports whether it is tapctl. |
| `WaitForNewAdapter(before)` | `static string?` | Polls (up to 20 × 500 ms) for a newly appeared adapter name. |
| `LocateInf(configuredPath)` | `static string?` | Resolves the driver INF from config or defaults. |
| `TryFindInPath(name, out path)` | `static bool` | Searches `PATH` for a tool. |

#### `TapNetworkConfigurator` (`Tap/TapNetworkConfigurator.cs`)

`[SupportedOSPlatform("windows")] public sealed class TapNetworkConfigurator` — applies Windows
networking quirks around the bridged TAP adapter. Changes are made only when the current setting
differs, so repeated starts do not generate network-change events that can make NetBird bounce the
tunnel.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `TapNetworkConfigurator(config, adapterId)` | ctor | Stores config and the adapter GUID. |
| `ApplyRouteIsolation()` | `void` | Sets interface metric 9999 via `netsh` and writes `DisableDefaultRoutes=1` under the adapter's registry key so a bridged DHCP server can't hijack the default route. Skipped when both values are already set. |
| `ConfigureAddress()` | `void` | Static address if `TapIpAddress` is set, otherwise DHCP. Skipped when the adapter is already configured. |
| `SetStatic(address, prefixLength)` | `void` (private) | `netsh … static <ip> <mask> none` — empty default gateway. |
| `SetDhcp()` | `void` (private) | `netsh … source=dhcp`; treats "already enabled" as benign. |
| `RenewDhcp()` | `void` | `ipconfig /renew "<tap>"` (used after reconnect to clear stale lease/ARP state). |
| `DeriveTapMtu(tunnelMtu, encapsulationOverhead)` | `int` | `tunnelMtu - overhead - innerHeader` (inner header 18 if VLAN-tagged else 14), clamped 576–9000. |
| `ApplyMtu(mtu)` | `bool` | `netsh … mtu=<mtu> store=persistent`; returns false on failure. |
| `ApplyMtuIfNeeded(mtu)` | `bool` | Queries the adapter's current IPv4 MTU and applies only when it differs. |
| `QueryInterfaceMetric()` | `int?` (private) | Parses `netsh interface ipv4 show interfaces` for the TAP's metric. |
| `QueryDhcpEnabled()` | `bool?` (private) | Parses `netsh interface ipv4 show config` for the DHCP state. |
| `HasAddress(address, prefixLength)` | `bool` (private) | True when the TAP already has that IPv4 address and prefix. |
| `QueryIpv4Mtu()` | `int?` (private) | Reads the adapter's current IPv4 MTU (best effort). |

Constant `IsolationMetric = 9999`.

---

### 7.13 `WGL2Bridge.Transport`

#### `IBridgeTransport` (`Transport/IBridgeTransport.cs`)

`[SupportedOSPlatform("windows")] public interface IBridgeTransport : IDisposable` — the
encapsulation strategy. Implementations own their socket, precompute a constant tunnel header into
buffer headroom, and validate inbound packets. Egress is pinned by binding to the tunnel IP.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `Mode` | `TransportMode` (get) | The encapsulation mode. |
| `HeaderLength` | `int` (get) | Constant headroom bytes before the inner frame (0 for Raw). |
| `EncapsulationOverhead` | `int` (get) | Total outer size (IP + tunnel headers) for MTU derivation. |
| `Description` | `string` (get) | Human-readable summary for logs. |
| `IsOpen` | `bool` (get) | True while the socket is open. |
| `DroppedNonPeer` | `long` (get) | Count of inbound packets dropped due to non-peer source. |
| `LastReceiveSource` | `IPEndPoint?` (get) | Source endpoint of the last packet returned by `ReceiveAsync` (server mode). |
| `Open(localTunnelIp, peerIp?)` | `void` | Opens/binds the socket to the tunnel IP. A null peer opens server mode (accept any source). |
| `WriteHeader(headroom)` | `void` | Writes the constant header into reserved headroom (once per buffer). |
| `ValidateIncoming(packet)` | `int` | Validates an inbound packet; returns the inner-frame offset or `-1`. |
| `SendAsync(buffer, frameOffset, frameLength, ct)` | `ValueTask<int>` | Sends header+frame to the configured peer (client mode). |
| `SendToAsync(buffer, frameOffset, frameLength, destination, ct)` | `ValueTask<int>` | Sends header+frame to an explicit endpoint (server mode / per-client or flood). |
| `ReceiveAsync(buffer, ct)` | `ValueTask<int>` | Receives one packet; returns its length and records `LastReceiveSource`. |
| `Close()` | `void` | Closes the socket. |

#### `VxlanTransport` (`Transport/VxlanTransport.cs`)

`[SupportedOSPlatform("windows")] public sealed class VxlanTransport : IBridgeTransport` — VXLAN
over a UDP socket bound to the VXLAN port. The kernel builds the outer IP/UDP header and UDP
checksum; we prepend only the 8-byte VXLAN header. Compatible with a Linux kernel `vxlan`
interface (dstport 4789).

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `Mode` | `TransportMode` | `Vxlan`. |
| `HeaderLength` | `int` = 8 | VXLAN header. |
| `EncapsulationOverhead` | `int` = 36 | IP(20) + UDP(8) + VXLAN(8). |
| `Description` | `string` | `VXLAN (VNI <n>, UDP/<port>)`. |
| `DroppedNonPeer` / `IsOpen` | `long` / `bool` | Counters/state. |
| `LastReceiveSource` | `IPEndPoint?` (get) | Source endpoint of the last received packet (used to learn clients). |
| `Open(localTunnelIp, peerIp?)` | `void` | Binds UDP to the tunnel IP on the VXLAN port. **Intentionally not connected**, because Linux vxlan transmits from a hashed source port (not 4789) which a connected socket would filter out. A null peer opens server mode. |
| `WriteHeader(headroom)` | `void` | Sets the VXLAN "I" flag and 24-bit VNI into the 8-byte header. |
| `ValidateIncoming(packet)` | `int` | Checks the "I" flag and VNI; returns `8` on match else `-1`. |
| `SendAsync(...)` | `ValueTask<int>` | Sends the header+frame slice to the configured peer (client mode). |
| `SendToAsync(..., destination, ct)` | `ValueTask<int>` | Sends the header+frame slice to an explicit client endpoint (server mode / flood). |
| `ReceiveAsync(buffer, ct)` | `async ValueTask<int>` | `ReceiveFromAsync`; records `LastReceiveSource`; in client mode enforces `PeerSourceValidation`. |
| `Close()` / `Dispose()` | `void` | Disposes the socket. |

#### `RawTransport` (`Transport/RawTransport.cs`)

`[SupportedOSPlatform("windows")] public sealed class RawTransport : IBridgeTransport` — bare
Ethernet frame as the payload of a raw IP packet with a configurable protocol number. The kernel
adds the outer IP header (no constant headroom). No kernel-native Linux peer equivalent; the peer
must run matching software.

| Member | Kind | Description |
| ------ | ---- | ----------- |
| `Mode` | `TransportMode` | `Raw`. |
| `HeaderLength` | `int` = 0 | No headroom. |
| `EncapsulationOverhead` | `int` = 20 | IP header only. |
| `Description` | `string` | `Raw IP (protocol <n>)`. |
| `LastReceiveSource` | `IPEndPoint?` (get) | Source endpoint of the last received packet. |
| `Open(localTunnelIp, peerIp?)` | `void` | Opens a `SocketType.Raw` socket for the protocol and binds to the tunnel IP. A null peer opens server mode. |
| `WriteHeader(headroom)` | `void` | No-op. |
| `ValidateIncoming(packet)` | `int` | Verifies IPv4, valid IHL, and the configured protocol; returns the IHL (L3 payload offset) or `-1`. |
| `SendAsync(...)` | `ValueTask<int>` | Sends the frame slice to the configured peer (client mode). |
| `SendToAsync(..., destination, ct)` | `ValueTask<int>` | Sends the frame slice to an explicit client endpoint (server mode / flood). |
| `ReceiveAsync(buffer, ct)` | `async ValueTask<int>` | `ReceiveFromAsync`; records `LastReceiveSource`; in client mode enforces `PeerSourceValidation`. |
| `Close()` / `Dispose()` | `void` | Disposes the socket. |

#### `TransportFactory` (`Transport/TransportFactory.cs`)

`public static class TransportFactory` — `Create(BridgeConfig)` switches on `TransportMode` to return
`RawTransport` or `VxlanTransport`; throws `ArgumentOutOfRangeException` on an unknown mode.

#### `SocketTuning` (`Transport/SocketTuning.cs`)

`internal static class SocketTuning` — shared socket preparation. `ApplyFragmentPolicy(socket,
allowFragmentation)` sets `DontFragment = !allowFragmentation` so an oversized encapsulated packet is
fragmented by the IP stack instead of failing the send with `WSAEMSGSIZE`; a failure to set the option
is logged at `Debug` and the OS default applies. Called from `VxlanTransport.Open` and
`RawTransport.Open`.

---

## 8. Configuration reference

`appsettings.json` is deserialized (camelCase) into `BridgeConfig`. Every key, its type and default:

| Key | Type | Default | Purpose |
| --- | ---- | ------- | ------- |
| `tapName` | string | `"WGBridge-TAP"` | Friendly name of the TAP-Windows6 adapter to use. |
| `bridgeSide` | `Client` \| `Lan` | `"Client"` | `Client` configures the TAP address/route isolation; `Lan` leaves the TAP (a bridge port) alone. |
| `tunnelInterfaceName` | string | `"NetBird"` | Friendly name of the WireGuard/NetBird tunnel interface. |
| `transportMode` | `Raw` \| `Vxlan` | `"Vxlan"` | Outer encapsulation mode. |
| `peerAddress` | string? | `null` | Peer tunnel IP or hostname (pinned peer). |
| `peerName` | string? | `null` | NetBird peer FQDN for CLI discovery. |
| `tunnelLocalAddress` | string? | `null` | Override local tunnel IP used as the encap source. |
| `vxlanVni` | int | `100` | VXLAN VNI (0..16777215) — must match the kernel peer. |
| `vxlanDestinationPort` | int | `4789` | VXLAN UDP destination port. |
| `rawIpProtocol` | int | `99` | IPv4 protocol number for Raw mode (1..255). |
| `peerSourceValidation` | bool | `true` | Drop inbound packets whose source is not the peer. |
| `allowFragmentation` | bool | `true` | Clear the DF bit so an oversized encapsulated packet is fragmented by the IP stack instead of failing the send; false drops and counts those frames. |
| `allowedClientPeers` | string[] | `[]` | Lan/server mode: optional allow-list of client tunnel IPs; empty accepts any overlay peer. |
| `allowClientToClient` | bool | `true` | Lan/server mode: allow clients to reach each other through the server. |
| `maxClients` | int | `64` | Lan/server mode: maximum tracked clients. |
| `clientAgingSeconds` | int | `300` | Lan/server mode: idle timeout before a client stops counting as connected (its MAC bindings survive). |
| `remoteMacAgingSeconds` | int | `3600` | Lan/server mode: how long a client-owned MAC binding outlives the client; must be >= `clientAgingSeconds`. |
| `maxRemoteMacs` | int | `4096` | Lan/server mode: cap on remembered client MACs (LRU eviction; counted by `remote_macs_evicted`). |
| `maxUnknownMacFloodPps` | int | `10` | Lan/server mode: per-second flood budget for one unresolved destination MAC; 0 = unlimited. |
| `reconnectDelaySeconds` | int | `5` | Delay before rebinding after a recoverable failure. |
| `tunnelHealthCheckSeconds` | int | `30` | Cadence of tunnel-interface reachability checks; 0 disables. |
| `stopOnLoopDetected` | bool | `true` | Exit when our own loop probe returns. |
| `loopProbeIntervalSeconds` | int | `10` | Seconds between loop-probe injections. |
| `statsIntervalSeconds` | int | `15` | Cadence of counter log lines; 0 disables. |
| `maxBroadcastPps` | int | `0` | Per-direction broadcast/multicast limit; 0 = unlimited. |
| `metricsPort` | int | `0` | Loopback HTTP metrics port; 0 disables. |
| `macAgingSeconds` | int | `300` | MAC table entry aging. |
| `enableLoopDetection` | bool | `true` | Enable 0x88B5 loop probes. |
| `assumeVlanTagged` | bool | `true` | Use the 18-byte inner header when deriving the TAP MTU. |
| `tapIpAddress` | string? | `null` | Static IPv4/CIDR for the TAP; null ⇒ DHCP. |
| `renewDhcpOnReconnect` | bool | `true` | After the pumps start, run `ipconfig /renew` only when the TAP has no usable IPv4 (APIPA/none); recovers a missed DHCP lease without config churn. |
| `createTapIfMissing` | bool | `true` | Create the TAP adapter if absent. |
| `tapInstallToolPath` | string? | `null` | Explicit path to `tapctl`/`tapinstall`/`devcon`. |
| `tapDriverInfPath` | string? | `null` | Explicit path to `OemVista.inf`. |
| `tapHardwareId` | string | `"tap0901"` | Hardware ID for adapter creation. |
| `consoleLogLevel` | LogLevel | `"Information"` | Minimum console level. |
| `fileLogLevel` | LogLevel | `"Debug"` | Minimum file level. |
| `logFilePath` | string | `"wgl2bridge.log"` | Plain-text log path. |
| `logMaxBytes` | long | `10485760` | Rotation threshold. |
| `dropEtherTypes` | string[]? | `null` | EtherTypes to drop (hex text); null/`[]` ⇒ none. |
| `dropLinkLocalMacs` | string[]? | `null` | Destination MACs; null ⇒ `01:80:C2:00:00:00-0F` + Cisco CDP/PVST+ MACs; array ⇒ exactly those; `[]` ⇒ none (PAUSE always dropped). |
| `dropIpProtocols` | int[]? | `null` | IP protocol numbers; null ⇒ `[2,88,89,103,112]`. |
| `dropUdpPorts` | int[]? | `null` | UDP destination ports; null ⇒ the eight discovery ports + `[1985,3222]`. |
| `dropTcpPorts` | int[]? | `null` | TCP destination ports; null/`[]` ⇒ none (lookup skipped). |
| `dropIcmpv4Types` | int[]? | `null` | ICMPv4 types; null/`[]` ⇒ none; e.g. `[5]` for redirects. |
| `dropIcmpv6Types` | int[]? | `null` | ICMPv6 types; null/`[]` ⇒ none; `[133,134]` drops Router Solicitation/Advertisement. |
| `allowVlans` | int[]? | `null` | VLAN allow list; null/empty ⇒ all VLANs allowed. |
| `netbirdCliPath` | string? | `null` | Explicit path to `netbird.exe`. |

`BridgeConfig.Validate()` enforces all ranges listed above and fails fast (exit code 1).

---

## 9. Encapsulation formats

### VXLAN mode (`VxlanTransport`)

```
+--------+--------+--------+----------------------------+
| Outer  | Outer  | VXLAN  |  Inner Ethernet frame      |
| IPv4   | UDP    | (8 B)  |  (14 + payload, tag-aware) |
| 20 B   | 8 B    |        |                            |
+--------+--------+--------+----------------------------+
   kernel-built        pre-written into headroom
```

- Header length (headroom): **8 bytes**; total outer overhead: **36 bytes**.
- VXLAN header: byte 0 = `0x08` ("I" flag), bytes 4–6 = 24-bit VNI.
- The 8-byte header is written once per pooled buffer into the headroom; `SendAsync` sends
  `buffer[0..frameOffset+frameLength]` so the header stays glued to the frame.
- Inbound validation: require length ≥ 8 and the "I" flag, then VNI match.

### Raw mode (`RawTransport`)

```
+--------+----------------------------+
| Outer  |  Inner Ethernet frame      |
| IPv4   |                            |
| 20 B   |                            |
+--------+----------------------------+
   kernel-built
```

- Header length: **0**; total outer overhead: **20 bytes**.
- Inbound validation: IPv4 version nibble, IHL ≥ 20 and within length, and `ip[9] == rawIpProtocol`;
  the inner frame begins at the IHL offset.

### TAP MTU derivation

`tapMtu = tunnelMtu − encapsulationOverhead − innerEthernetHeader`, clamped to `[576, 9000]`, where
`innerEthernetHeader = 18` when `assumeVlanTagged` else `14`.

`Client` mode applies that MTU to the TAP. `Lan` mode does not: a Windows network bridge forwards L2
frames regardless of its own MTU, so the attached segment keeps its MTU and **cannot be lowered from
this side** — the fix is DHCP option 26 / the RA MTU option on the segment.

Consequences implemented in `Bridge`:

- `_maxInnerFrame = tunnelMtu − encapsulationOverhead` (largest frame that fits the tunnel).
- **TAP → tunnel** above that limit: counted (`_oversizedOutbound`), warned at most once a minute, then
  fragmented (if `allowFragmentation`, the default) or dropped (`_oversizedDropped`). Dropping instead
  of fragmenting would otherwise fail the send with `WSAEMSGSIZE` and tear the session down.
- **tunnel → TAP** above that limit: counted (`_oversizedInbound`) and warned, then forwarded — the
  frame may still be deliverable on the far segment; it signals a peer MTU that is too high.
- `_ipFragments` counts inbound IP fragments (IPv4 MF/non-zero offset, IPv6 fragment header). A
  non-zero value on a healthy link means an MTU mismatch or fragment-based evasion.
- The receive buffer is sized for a 9000-byte frame plus headers, so an oversized datagram is received
  intact and diagnosed rather than silently truncated.
- `SocketTuning.ApplyFragmentPolicy` sets `DontFragment = !allowFragmentation` on the encapsulation
  socket (`Transport/SocketTuning.cs`).

---

## 10. Filtering & forwarding policy

Applied by `FrameFilter.Allow` in **both** directions. Each check is a single field, so the drop is
always attributable to one configured value:

1. **Truncated frame** (`< 14` bytes) → forward (fail-open).
2. **Destination MAC** (`dropLinkLocalMacs`, default the 802.1 reserved group `01:80:C2:00:00:00`–`0F`
   + Cisco `01:00:0C:CC:CC:CC`/`CC:CD`) → match drops the frame. These are 802.3/LLC frames with no
   EtherType, so no other check can catch them. 802.3x PAUSE (`…:01`) is dropped even when the list is
   cleared. Clearing the list forwards BPDUs, which startup reports as a warning because
   `LoopDetector` is then the only cross-site loop guard.
3. **VLAN allow list** (if configured) → the frame must carry an allowed 802.1Q/QinQ tag; untagged
   frames are dropped. If not configured, all VLANs (and untagged) pass.
4. **EtherType** (`dropEtherTypes`) → inner EtherType match drops the frame.
5. **IP protocol number** (`dropIpProtocols`, default `2/88/89/103/112`) → checked on IPv4 and on the
   final IPv6 next-header, so routing/multicast-control convergence and VRRP/CARP cannot span the
   tunnel.
6. **ICMPv6 type** (`dropIcmpv6Types`, empty by default) → `[133,134]` drops Router
   Solicitation/Advertisement.
7. **ICMPv4 type** (`dropIcmpv4Types`, empty by default; only when configured).
8. **TCP / UDP destination port** (`dropTcpPorts` / `dropUdpPorts`, the latter defaulting to the eight
   discovery ports + HSRP/GLBP) → later fragments and truncated transport headers pass. The TCP
   lookup is skipped entirely while `dropTcpPorts` is empty.
9. **Everything else** (other ICMP types, GRE, unrecognized EtherTypes, …) → forwards.

Every drop is reported as a `FilterDrop(Reason, Value)` and charged to a counter generated for that
exact configured value (`filter_udp_5353`, `filter_ip_89`, `filter_mac_0180c2000000`, …).

Then per direction:

- **MAC learning / suppression** (`MacTable`): learn source MAC on ingress; suppress unicast frames
  whose destination is known to live on the ingress port; flood broadcast/multicast/unknown.
- **Loop detection** (`LoopDetector`): own 0x88B5 probe returning — on the TAP or over the tunnel —
  triggers `OnLoopDetected`. Probes are emitted on both paths by `PumpProbes`, so bridges that are
  looped through the tunnel are caught, not just locally looped segments.
- **Storm control** (`BroadcastLimiter`): multicast/broadcast frames above `maxBroadcastPps` per
  second per direction are dropped silently.

---

## 11. Error handling & reconnection

- **Config errors** → fail fast, exit code `1`, no retries (`Program.Main` / `Validate`).
- **Session failures** (any pump faulting) → caught in `Bridge.RunAsync`, logged via
  `LogSessionFailure` (repeated identical messages de-duplicated), session torn down, then retried
  after `reconnectDelaySeconds`.
- **Peer not connected** → `RefreshPeer` throws, deferring the session until the overlay is up.
- **Configured peer not found** → `PeerResolver` returns `null` (never a different connected peer);
  the session logs the condition and retries, so a typo or a missing peer cannot bridge to the wrong
  machine.
- **Peer unresolved at startup** → `Program.RunBridge` only logs a warning and continues; the
  session loop re-resolves the peer each attempt, so a NetBird overlay that starts after the bridge
  does not cause an immediate process exit.
- **Tunnel interface missing at startup** → `WireGuardInterface.Resolve` throws a descriptive
  `InvalidOperationException`; `Bridge.RunAsync` catches it, logs it and retries after
  `reconnectDelaySeconds`, so a tunnel created later (e.g. by NetBird) is picked up automatically.
- **Tunnel interface missing** → `TunnelUnavailableException`; treated as a wait state (polled every
  1 s, not counted as a session failure) so a NetBird reconnect on a network change is absorbed and
  the session resumes as soon as `wt0` returns.
- **Tunnel down/missing** → `PumpInterfaceWatch` throws when the tunnel IP is no longer bindable.
- **Dead tunnel** → `PumpHealthCheck` throws when NetBird reports the peer disconnected, or when
  frames are being forwarded but no inbound traffic arrives within the check window.
- **Hung CLI** → `ProcessRunner` enforces a timeout; `ResolvePeerBoundedAsync` caps peer resolution
  at 10 s so the health loop cannot stall.
- **Loop detected** → `OnLoopDetected` logs an error and (when `stopOnLoopDetected`) stops the bridge
  to prevent a broadcast storm.

---

## 12. Platform / build notes

- **Target:** `net10.0-windows`, `win-x64`, NativeAOT (`PublishAot`, `IsAotCompatible`,
  `PublishTrimmed`), unsafe blocks enabled, invariant globalization, `OptimizationPreference=Speed`.
- **Elevation:** `app.manifest` requests `requireAdministrator` (raw TAP access, raw sockets for Raw
  mode, `netsh`/registry configuration).
- **AOT constraints:** JSON goes through `ConfigJsonContext` source generation; no reflection
  serialization; P/Invoke uses `[LibraryImport]` source generation.
- **Publish:**
  ```powershell
  dotnet publish -c Release -r win-x64 -p:PublishAot=true -p:IsAotCompatible=true
  ```
  Output: `bin\Release\net10.0-windows\win-x64\publish\WGL2Bridge.exe`.
- **Run modes:** console (default), `--check` (dry-run), `--service` (SCM). Register the service with
  `sc.exe create WGL2Bridge binPath= "…\WGL2Bridge.exe --service" start= auto`.
- **Requirements:** .NET 10 SDK, VS C++ Build Tools (for NativeAOT linking), TAP-Windows6 driver,
  and NetBird/WireGuard with an active tunnel.
