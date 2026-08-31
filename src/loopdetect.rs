//! Layer 2 loop detection.
//!
//! A tagged probe frame is emitted periodically on both ports. Receiving a probe carrying this
//! instance's own identifier means the frame travelled out of one port and came back through
//! another, which can only happen if a second Layer 2 path exists between the bridged segments.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

use tracing::{error, warn};

use crate::mactable::BridgePort;
use crate::tap::TapPort;
use crate::transport::UdpTunnel;

// IEEE 802a local experimental EtherType; safe for site-local use.
const PROBE_ETHER_TYPE: u16 = 0x88B5;
const PROBE_FRAME_SIZE: usize = 60; // Minimum Ethernet frame without FCS.
const MAGIC_OFFSET: usize = 14;
const INSTANCE_OFFSET: usize = 22;
const PORT_OFFSET: usize = 38;
const MAGIC: &[u8; 8] = b"WGL2LOOP";

/// Emits and consumes loop-detection probes on both bridge ports.
pub struct LoopDetector {
    instance_id: [u8; 16],
    detected: AtomicBool,
}

impl LoopDetector {
    pub fn new() -> Self {
        Self {
            instance_id: rand_id(),
            detected: AtomicBool::new(false),
        }
    }

    /// Whether a loop has been detected by this instance.
    pub fn loop_detected(&self) -> bool {
        self.detected.load(Ordering::Relaxed)
    }

    /// Consumes loop probes. Returns true when the frame was a probe and must not be forwarded.
    pub fn try_handle_probe(&self, frame: &[u8], ingress: BridgePort) -> bool {
        if frame.len() < PORT_OFFSET + 1 {
            return false;
        }

        let ether_type = u16::from_be_bytes([frame[12], frame[13]]);
        if ether_type != PROBE_ETHER_TYPE || &frame[MAGIC_OFFSET..MAGIC_OFFSET + 8] != MAGIC {
            return false;
        }

        // A probe from another bridge instance is simply swallowed; only our own indicates a loop.
        if frame[INSTANCE_OFFSET..INSTANCE_OFFSET + 16] == self.instance_id
            && !self.detected.swap(true, Ordering::Relaxed)
        {
            let origin = match frame[PORT_OFFSET] {
                0 => BridgePort::Tap,
                _ => BridgePort::Tunnel,
            };
            error!(
                ?origin,
                ?ingress,
                "LOOP DETECTED: probe sent on one port returned on another; \
                 a second Layer 2 path exists between the bridged segments"
            );
        }

        true
    }

    /// Builds the probe frame for the given origin port into `frame` (must be >= 60 bytes).
    fn build_probe(&self, frame: &mut [u8], origin: BridgePort) {
        frame[..PROBE_FRAME_SIZE].fill(0);

        // Destination: locally administered multicast so bridges flood it.
        frame[0..6].copy_from_slice(&[0x03, 0x57, 0x47, 0x4C, 0x32, 0x42]);

        // Source: locally administered unicast derived from the instance id.
        frame[6] = 0x02;
        frame[7..12].copy_from_slice(&self.instance_id[0..5]);

        frame[12..14].copy_from_slice(&PROBE_ETHER_TYPE.to_be_bytes());
        frame[MAGIC_OFFSET..MAGIC_OFFSET + 8].copy_from_slice(MAGIC);
        frame[INSTANCE_OFFSET..INSTANCE_OFFSET + 16].copy_from_slice(&self.instance_id);
        frame[PORT_OFFSET] = match origin {
            BridgePort::Tap => 0,
            BridgePort::Tunnel => 1,
        };
    }

    /// Periodically emits the probe on both ports until the shutdown token fires or a loop stops
    /// the bridge.
    pub async fn run(
        self: Arc<Self>,
        tap: Arc<dyn TapPort>,
        tunnel: Arc<UdpTunnel>,
        header: Arc<Vec<u8>>,
        interval: Duration,
        stop_on_detection: bool,
        shutdown: tokio_util::sync::CancellationToken,
    ) {
        let mut tap_probe = vec![0u8; PROBE_FRAME_SIZE];
        self.build_probe(&mut tap_probe, BridgePort::Tap);

        let mut tunnel_probe = vec![0u8; header.len() + PROBE_FRAME_SIZE];
        tunnel_probe[..header.len()].copy_from_slice(&header);
        self.build_probe(&mut tunnel_probe[header.len()..], BridgePort::Tunnel);

        let mut timer = tokio::time::interval(interval);
        timer.tick().await; // Skip the immediate first tick.

        loop {
            tokio::select! {
                _ = shutdown.cancelled() => return,
                _ = timer.tick() => {}
            }

            if self.loop_detected() {
                if stop_on_detection {
                    error!("stopping the bridge to prevent a broadcast storm; remove the redundant path, then restart");
                    shutdown.cancel();
                }
                return;
            }

            if let Err(err) = tunnel.send(&tunnel_probe).await {
                warn!(error = %err, "loop probe send to tunnel failed");
            }
            if let Err(err) = tap.write_frame(&tap_probe).await {
                warn!(error = %err, "loop probe write to TAP failed");
            }
        }
    }
}

/// Generates a per-instance identifier from the OS RNG.
fn rand_id() -> [u8; 16] {
    use std::collections::hash_map::RandomState;
    use std::hash::{BuildHasher, Hasher};

    // Two 64-bit draws from the process RNG give a per-instance identifier without pulling in an
    // extra crate; collisions across simultaneous instances are vanishingly unlikely.
    let mut id = [0u8; 16];
    let state = RandomState::new();
    let mut h1 = state.build_hasher();
    h1.write_u64(1);
    id[..8].copy_from_slice(&h1.finish().to_be_bytes());
    let state = RandomState::new();
    let mut h2 = state.build_hasher();
    h2.write_u64(2);
    id[8..].copy_from_slice(&h2.finish().to_be_bytes());
    id
}
