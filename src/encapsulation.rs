/// Tunnel encapsulation abstraction.
///
/// The bridge only cares about the header shape and the payload slicing. The rest of the packet
/// path should not need to know which encapsulation mode is active.
pub trait Encapsulation: Send + Sync {
    /// Number of bytes reserved at the front of the packet buffer for the tunnel header.
    fn header_size(&self) -> usize;

    /// Writes the constant tunnel header into the provided buffer.
    fn write_header(&self, out: &mut [u8]);

    /// Returns the offset of the inner Ethernet frame in a received datagram, or `None` when the
    /// datagram should be dropped (truncated header, wrong segment, wrong protocol).
    fn inner_frame_offset(&self, datagram: &[u8]) -> Option<usize>;
}

/// No encapsulation: the UDP payload is the raw Ethernet frame.
pub struct RawEncapsulation;

impl Encapsulation for RawEncapsulation {
    fn header_size(&self) -> usize {
        0
    }

    fn write_header(&self, _out: &mut [u8]) {}

    fn inner_frame_offset(&self, datagram: &[u8]) -> Option<usize> {
        (datagram.len() >= 14).then_some(0)
    }
}

/// VXLAN encapsulation (RFC 7348) for the shared bridge segment.
///
/// The header is constant for the lifetime of the bridge, so it is written once into the send
/// buffer's headroom and reused for every frame.
pub struct VxlanEncapsulation {
    vni: u32,
}

impl VxlanEncapsulation {
    pub fn new(vni: u32) -> Self {
        Self { vni }
    }
}

impl Encapsulation for VxlanEncapsulation {
    fn header_size(&self) -> usize {
        8
    }

    fn write_header(&self, out: &mut [u8]) {
        // Byte 0: flags, bit 3 (0x08) = valid VNI. Bytes 4..6: 24-bit VNI. Rest reserved.
        out[0] = 0x08;
        out[1] = 0;
        out[2] = 0;
        out[3] = 0;
        out[4] = ((self.vni >> 16) & 0xFF) as u8;
        out[5] = ((self.vni >> 8) & 0xFF) as u8;
        out[6] = (self.vni & 0xFF) as u8;
        out[7] = 0;
    }

    fn inner_frame_offset(&self, datagram: &[u8]) -> Option<usize> {
        // Smallest valid datagram: 8-byte VXLAN header + 14-byte Ethernet header.
        if datagram.len() < 8 + 14 || (datagram[0] & 0x08) == 0 {
            return None;
        }

        let vni =
            ((datagram[4] as u32) << 16) | ((datagram[5] as u32) << 8) | datagram[6] as u32;
        if vni != self.vni {
            return None; // Reject frames from other VXLAN segments.
        }

        Some(8)
    }
}

/// GRETAP encapsulation (RFC 2784/2890, Transparent Ethernet Bridging, protocol type 0x6558).
///
/// On receive, Windows raw sockets deliver the full IPv4 header, so the inner frame offset is
/// IHL + GRE header length. Sending GRE requires a raw socket, which the current UDP transport
/// does not provide; this mode is kept for parity with the reference implementation.
pub struct GretapEncapsulation {
    key: u32,
}

impl GretapEncapsulation {
    /// Creates the encapsulation; a key of 0 omits the key field.
    pub fn new(key: u32) -> Self {
        Self { key }
    }
}

impl Encapsulation for GretapEncapsulation {
    fn header_size(&self) -> usize {
        if self.key != 0 { 8 } else { 4 }
    }

    fn write_header(&self, out: &mut [u8]) {
        // Flags + version: 0x2000 when the key field is present.
        let flags: u16 = if self.key != 0 { 0x2000 } else { 0 };
        out[0] = (flags >> 8) as u8;
        out[1] = (flags & 0xFF) as u8;
        out[2] = 0x65; // Protocol type 0x6558 = Transparent Ethernet Bridging.
        out[3] = 0x58;

        if self.key != 0 {
            out[4..8].copy_from_slice(&self.key.to_be_bytes());
        }
    }

    fn inner_frame_offset(&self, datagram: &[u8]) -> Option<usize> {
        if datagram.len() < 24 {
            return None;
        }

        // IPv4 byte 0 low nibble = IHL in 32-bit words.
        let ihl = ((datagram[0] & 0x0F) as usize) * 4;
        if ihl < 20 || datagram.len() < ihl + 4 {
            return None;
        }

        let gre = &datagram[ihl..];
        let flags = u16::from_be_bytes([gre[0], gre[1]]);
        let protocol = u16::from_be_bytes([gre[2], gre[3]]);
        if protocol != 0x6558 {
            return None; // Not GRETAP (e.g. plain GRE carrying IP).
        }

        let mut gre_len = 4usize;
        if flags & 0x8000 != 0 {
            gre_len += 4; // Checksum present (checksum + reserved1).
        }
        if flags & 0x2000 != 0 {
            gre_len += 4; // Key present.
        }
        if gre.len() < gre_len + 14 {
            return None;
        }

        // When a key is configured, reject frames carrying a different key.
        if self.key != 0 {
            let key_offset = if flags & 0x8000 != 0 { 8 } else { 4 };
            if gre.len() < key_offset + 4 {
                return None;
            }
            let key = u32::from_be_bytes([
                gre[key_offset],
                gre[key_offset + 1],
                gre[key_offset + 2],
                gre[key_offset + 3],
            ]);
            if key != self.key {
                return None;
            }
        }

        Some(ihl + gre_len)
    }
}
