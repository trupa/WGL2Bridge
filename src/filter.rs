//! Zero-allocation Layer 2 / Layer 4 frame classifier implementing the "Broad-Industrial-Pass"
//! policy. All inspection is performed over slices; nothing is copied or allocated.

const ETH_TYPE_IPV4: u16 = 0x0800;
const ETH_TYPE_ARP: u16 = 0x0806;
const ETH_TYPE_VLAN: u16 = 0x8100; // 802.1Q tag
const ETH_TYPE_QINQ: u16 = 0x88A8; // 802.1ad provider tag
const ETH_TYPE_IPV6: u16 = 0x86DD;

// Non-IP industrial Layer 2 protocols, always allowed.
const ETH_TYPE_PROFINET_RT: u16 = 0x8892;
const ETH_TYPE_POWERLINK: u16 = 0x88AB;
const ETH_TYPE_ETHERCAT: u16 = 0x88E1;
const ETH_TYPE_GOOSE: u16 = 0x891D;

const IP_PROTO_TCP: u8 = 6;
const IP_PROTO_UDP: u8 = 17;

const ETH_HEADER_LEN: usize = 14; // 6 dst + 6 src + 2 ethertype
const VLAN_TAG_LEN: usize = 4;    // TPID already counted in the ethertype field + 2 TCI

/// Consumer/Windows link-local discovery UDP ports aggressively dropped even when carried over
/// plain IPv4/IPv6, to keep the tunnel free of broadcast noise.
const BLOCKED_UDP_PORTS: [u16; 8] = [
    137,   // NetBIOS Name Service
    138,   // NetBIOS Datagram Service
    1900,  // SSDP / UPnP
    3702,  // WS-Discovery
    5353,  // mDNS / Bonjour
    5355,  // LLMNR
    17500, // Dropbox LAN sync discovery
    27036, // Steam in-home streaming discovery
];

/// Industrial UDP ports that always win over the drop list.
const INDUSTRIAL_UDP_PORTS: [u16; 2] = [
    2222,  // EtherNet/IP implicit (I/O) messaging
    44818, // EtherNet/IP UDP explicit messaging
];

/// Bridge filter for the industrial traffic policy.
///
/// When enabled, the filter applies the Broad-Industrial-Pass policy in both directions. When
/// disabled, every frame crosses the bridge verbatim.
pub struct FrameFilter {
    enabled: bool,
}

impl FrameFilter {
    pub fn new(enabled: bool) -> Self {
        Self { enabled }
    }

    /// Returns true when the frame may cross the bridge.
    ///
    /// `frame` must be a complete Ethernet II frame starting at the destination MAC.
    pub fn should_forward(&self, frame: &[u8]) -> bool {
        if !self.enabled {
            return true;
        }
        should_forward(frame)
    }
}

/// Applies the Broad-Industrial-Pass policy to a complete Ethernet II frame.
pub fn should_forward(frame: &[u8]) -> bool {
    // Runt frame: not even a full Ethernet header.
    if frame.len() < ETH_HEADER_LEN {
        return false;
    }

    // Bytes 12..13 = EtherType (big endian).
    let mut ether_type = u16::from_be_bytes([frame[12], frame[13]]);
    let mut payload_offset = ETH_HEADER_LEN;

    // 802.1Q / 802.1ad: the real EtherType sits 4 bytes further in. Industrial networks routinely
    // use VLAN priority tagging (PROFINET, GOOSE), so unwrap up to two tags.
    for _ in 0..2 {
        if ether_type != ETH_TYPE_VLAN && ether_type != ETH_TYPE_QINQ {
            break;
        }
        if frame.len() < payload_offset + VLAN_TAG_LEN + 2 {
            return false;
        }
        // +2 TCI bytes, then the next EtherType field.
        ether_type = u16::from_be_bytes([frame[payload_offset + 2], frame[payload_offset + 3]]);
        payload_offset += VLAN_TAG_LEN;
    }

    match ether_type {
        // Infrastructure baseline and non-IP industrial protocols: unconditional pass.
        ETH_TYPE_ARP
        | ETH_TYPE_PROFINET_RT
        | ETH_TYPE_POWERLINK
        | ETH_TYPE_ETHERCAT
        | ETH_TYPE_GOOSE => true,

        // IP traffic: broadly allowed, minus consumer discovery ports.
        ETH_TYPE_IPV4 => inspect_ipv4(frame, payload_offset),
        ETH_TYPE_IPV6 => inspect_ipv6(frame, payload_offset),

        // Broad pass: anything else (LLDP, PTP, vendor L2 protocols) is industrial-plausible.
        _ => true,
    }
}

/// `ip_offset` is the offset of the first byte of the IPv4 header inside `frame`.
fn inspect_ipv4(frame: &[u8], ip_offset: usize) -> bool {
    // Minimum IPv4 header is 20 bytes.
    if frame.len() < ip_offset + 20 {
        return true; // Malformed/truncated: fail open, the endpoint will discard it.
    }

    // IPv4 byte 0: bits 7..4 version, bits 3..0 IHL in 32-bit words.
    let ihl = ((frame[ip_offset] & 0x0F) as usize) * 4;
    if ihl < 20 || frame.len() < ip_offset + ihl {
        return true;
    }

    // IPv4 bytes 6..7: flags + fragment offset. Non-zero fragment offset => no L4 header present.
    let fragment_offset = (((frame[ip_offset + 6] & 0x1F) as u16) << 8) | frame[ip_offset + 7] as u16;
    if fragment_offset != 0 {
        return true; // Later fragments of an already-admitted datagram.
    }

    // IPv4 byte 9: protocol.
    let protocol = frame[ip_offset + 9];
    inspect_transport(frame, protocol, ip_offset + ihl)
}

/// `ip_offset` is the offset of the first byte of the IPv6 header inside `frame`.
fn inspect_ipv6(frame: &[u8], ip_offset: usize) -> bool {
    // Fixed IPv6 header is 40 bytes; byte 6 is the Next Header field.
    if frame.len() < ip_offset + 40 {
        return true;
    }

    // Extension headers are not walked: only bare TCP/UDP is port-inspected, everything else passes.
    inspect_transport(frame, frame[ip_offset + 6], ip_offset + 40)
}

fn inspect_transport(frame: &[u8], protocol: u8, l4_offset: usize) -> bool {
    // TCP/UDP both carry source port at bytes 0..1 and destination port at bytes 2..3.
    if (protocol != IP_PROTO_UDP && protocol != IP_PROTO_TCP) || frame.len() < l4_offset + 4 {
        return true; // ICMP, IGMP, GRE, truncated headers: broad pass.
    }

    let source_port = u16::from_be_bytes([frame[l4_offset], frame[l4_offset + 1]]);
    let dest_port = u16::from_be_bytes([frame[l4_offset + 2], frame[l4_offset + 3]]);

    if protocol == IP_PROTO_TCP {
        // Modbus TCP (502) and EtherNet/IP explicit (44818) are prioritised flows; no consumer
        // noise rides on TCP link-local discovery, so all TCP passes.
        return true;
    }

    // Industrial UDP (EtherNet/IP implicit 2222, explicit 44818) always wins over the drop list.
    if INDUSTRIAL_UDP_PORTS.contains(&source_port) || INDUSTRIAL_UDP_PORTS.contains(&dest_port) {
        return true;
    }

    // Consumer Windows link-local discovery noise: dropped in both directions.
    !BLOCKED_UDP_PORTS.contains(&source_port) && !BLOCKED_UDP_PORTS.contains(&dest_port)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame(ether_type: u16) -> Vec<u8> {
        let mut f = vec![0u8; 60];
        f[12] = (ether_type >> 8) as u8;
        f[13] = (ether_type & 0xFF) as u8;
        f
    }

    #[test]
    fn runt_frames_are_dropped() {
        assert!(!should_forward(&[0u8; 13]));
    }

    #[test]
    fn arp_and_industrial_ethertypes_pass() {
        for t in [ETH_TYPE_ARP, ETH_TYPE_PROFINET_RT, ETH_TYPE_POWERLINK, ETH_TYPE_ETHERCAT, ETH_TYPE_GOOSE] {
            assert!(should_forward(&frame(t)), "ethertype {t:#06x} must pass");
        }
    }

    #[test]
    fn unknown_ethertypes_broadly_pass() {
        assert!(should_forward(&frame(0x88B5))); // LLDP-class vendor/local protocols
    }

    #[test]
    fn mdns_udp_is_dropped() {
        let mut f = frame(ETH_TYPE_IPV4);
        f[14] = 0x45; // IPv4, IHL 5 (20 bytes)
        f[23] = IP_PROTO_UDP;
        // UDP header at offset 34: source 5353, destination 5353.
        f[34] = 0x14;
        f[35] = 0xE9;
        f[36] = 0x14;
        f[37] = 0xE9;
        assert!(!should_forward(&f));
    }

    #[test]
    fn industrial_udp_wins_over_drop_list() {
        let mut f = frame(ETH_TYPE_IPV4);
        f[14] = 0x45;
        f[23] = IP_PROTO_UDP;
        f[34] = 0x08;
        f[35] = 0xAE; // source 2222
        f[36] = 0x14;
        f[37] = 0xE9; // destination 5353 (would otherwise be dropped)
        assert!(should_forward(&f));
    }

    #[test]
    fn vlan_tagged_arp_passes() {
        let mut f = frame(ETH_TYPE_VLAN);
        f[16] = 0x00;
        f[17] = 0x64; // TCI
        f[18] = (ETH_TYPE_ARP >> 8) as u8;
        f[19] = (ETH_TYPE_ARP & 0xFF) as u8;
        assert!(should_forward(&f));
    }
}

