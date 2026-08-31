//! Learning bridge forwarding database.
//!
//! Unicast frames whose destination was last seen on the ingress port are dropped instead of being
//! flooded across the tunnel; broadcast, multicast and unknown destinations still flood, which is
//! standard 802.1D behaviour.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{Duration, Instant};

/// Port a frame arrived on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BridgePort {
    /// The local TAP adapter.
    Tap,
    /// The encapsulated tunnel to the remote peer.
    Tunnel,
}

struct Entry {
    port: BridgePort,
    seen: Instant,
}

/// Learning bridge forwarding database with lazy aging and a hard capacity.
pub struct MacTable {
    entries: Mutex<HashMap<u64, Entry>>,
    aging: Duration,
    capacity: usize,
}

impl MacTable {
    pub fn new(aging: Duration) -> Self {
        Self {
            entries: Mutex::new(HashMap::new()),
            aging,
            capacity: 4096,
        }
    }

    /// Learns the source address and decides whether the frame may cross to the other port.
    ///
    /// `frame` must start at the Ethernet header: bytes 0..6 destination MAC, bytes 6..12 source.
    pub fn should_forward(&self, frame: &[u8], ingress: BridgePort) -> bool {
        if frame.len() < 12 {
            return false;
        }

        let now = Instant::now();
        let source = mac_key(&frame[6..12]);
        let destination = mac_key(&frame[0..6]);

        let mut entries = self.entries.lock().unwrap();

        // Never learn from a multicast source address; it is invalid and would poison the table.
        if frame[6] & 0x01 == 0 {
            match entries.get_mut(&source) {
                Some(existing) => {
                    // Refresh lazily: only rewrite when the port moved or the entry is half-aged.
                    if existing.port != ingress || now.duration_since(existing.seen) > self.aging / 2 {
                        existing.port = ingress;
                        existing.seen = now;
                    }
                }
                None => {
                    if entries.len() >= self.capacity {
                        let aging = self.aging;
                        entries.retain(|_, e| now.duration_since(e.seen) <= aging);
                    }
                    if entries.len() < self.capacity {
                        entries.insert(source, Entry { port: ingress, seen: now });
                    }
                }
            }
        }

        // Bit 0 of the first destination byte marks broadcast/multicast: always flooded.
        if frame[0] & 0x01 != 0 {
            return true;
        }

        // Known-local unicast: the destination was last seen on the ingress port, so flooding it
        // across the tunnel would be pointless.
        match entries.get(&destination) {
            Some(entry) if now.duration_since(entry.seen) <= self.aging => entry.port != ingress,
            _ => true, // Unknown or aged-out destination: flood.
        }
    }
}

fn mac_key(mac: &[u8]) -> u64 {
    ((mac[0] as u64) << 40)
        | ((mac[1] as u64) << 32)
        | ((mac[2] as u64) << 24)
        | ((mac[3] as u64) << 16)
        | ((mac[4] as u64) << 8)
        | mac[5] as u64
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame(dst: [u8; 6], src: [u8; 6]) -> Vec<u8> {
        let mut f = vec![0u8; 60];
        f[0..6].copy_from_slice(&dst);
        f[6..12].copy_from_slice(&src);
        f
    }

    const A: [u8; 6] = [0x02, 0, 0, 0, 0, 1];
    const B: [u8; 6] = [0x02, 0, 0, 0, 0, 2];
    const BCAST: [u8; 6] = [0xFF; 6];

    #[test]
    fn known_local_unicast_is_not_flooded() {
        let table = MacTable::new(Duration::from_secs(300));
        assert!(table.should_forward(&frame(BCAST, A), BridgePort::Tap)); // learn A on Tap
        assert!(!table.should_forward(&frame(A, B), BridgePort::Tap)); // A known-local: drop
    }

    #[test]
    fn unknown_unicast_floods() {
        let table = MacTable::new(Duration::from_secs(300));
        assert!(table.should_forward(&frame(B, A), BridgePort::Tap));
    }

    #[test]
    fn broadcast_always_floods() {
        let table = MacTable::new(Duration::from_secs(300));
        assert!(table.should_forward(&frame(BCAST, A), BridgePort::Tunnel));
    }
}
