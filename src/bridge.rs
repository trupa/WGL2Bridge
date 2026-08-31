use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use anyhow::Result;
use tokio_util::sync::CancellationToken;

use crate::encapsulation::Encapsulation;
use crate::filter::FrameFilter;
use crate::loopdetect::LoopDetector;
use crate::mactable::{BridgePort, MacTable};
use crate::tap::TapPort;
use crate::transport::UdpTunnel;

/// Shared frame counters, printed periodically by the stats reporter.
///
/// Atomics keep the counters contention-free: the pumps increment them without taking a lock.
#[derive(Default)]
pub struct BridgeStats {
    pub frames_to_tunnel: AtomicU64,
    pub frames_to_tap: AtomicU64,
    pub frames_dropped: AtomicU64,
}

/// Coordinates the packet flow between the local TAP side and the tunnel side.
///
/// The engine owns one pooled buffer per direction for its entire lifetime; the tunnel header is
/// constant, so it is written once into the send buffer's headroom and the TAP read lands directly
/// after it. Frames are handled as slices, never copied.
pub struct BridgeEngine {
    tap: Arc<dyn TapPort>,
    tunnel: Arc<UdpTunnel>,
    encapsulation: Arc<dyn Encapsulation>,
    filter: FrameFilter,
    mac_table: Option<MacTable>,
    loop_detector: Option<Arc<LoopDetector>>,
    stats: Arc<BridgeStats>,
}

impl BridgeEngine {
    pub fn new(
        tap: Arc<dyn TapPort>,
        tunnel: Arc<UdpTunnel>,
        encapsulation: Arc<dyn Encapsulation>,
        filter: FrameFilter,
        mac_table: Option<MacTable>,
        loop_detector: Option<Arc<LoopDetector>>,
    ) -> Self {
        Self {
            tap,
            tunnel,
            encapsulation,
            filter,
            mac_table,
            loop_detector,
            stats: Arc::new(BridgeStats::default()),
        }
    }

    /// Shared counters handle for the periodic stats reporter.
    pub fn stats(&self) -> Arc<BridgeStats> {
        Arc::clone(&self.stats)
    }

    /// The constant tunnel header, pre-rendered for the probe frames.
    pub fn tunnel_header(&self) -> Arc<Vec<u8>> {
        let mut header = vec![0u8; self.encapsulation.header_size()];
        self.encapsulation.write_header(&mut header);
        Arc::new(header)
    }

    /// Runs both directions concurrently; returns when either pump fails or shutdown fires.
    pub async fn run(&self, shutdown: CancellationToken) -> Result<()> {
        let to_tunnel = self.pump_tap_to_tunnel(shutdown.clone());
        let to_tap = self.pump_tunnel_to_tap(shutdown);
        tokio::try_join!(to_tunnel, to_tap)?;
        Ok(())
    }

    /// TAP -> tunnel: reads raw Ethernet frames and encapsulates them into the tunnel.
    async fn pump_tap_to_tunnel(&self, shutdown: CancellationToken) -> Result<()> {
        let header_size = self.encapsulation.header_size();
        let mut buffer = vec![0u8; 2048 + header_size];
        let mut header = vec![0u8; header_size];
        self.encapsulation.write_header(&mut header);

        loop {
            // TAP reads are not cancellable through the driver, so shutdown is checked between
            // frames; Ctrl+C takes effect as soon as the current read completes.
            let frame_len = self.tap.read_frame(&mut buffer[header_size..]).await?;
            if shutdown.is_cancelled() {
                return Ok(());
            }
            if frame_len == 0 {
                continue;
            }

            let frame = &buffer[header_size..header_size + frame_len];

            // Probe frames are consumed, never bridged.
            if let Some(detector) = &self.loop_detector {
                if detector.try_handle_probe(frame, BridgePort::Tap) {
                    continue;
                }
            }

            if let Some(table) = &self.mac_table {
                if !table.should_forward(frame, BridgePort::Tap) {
                    self.stats.frames_dropped.fetch_add(1, Ordering::Relaxed);
                    continue;
                }
            }

            if !self.filter.should_forward(frame) {
                self.stats.frames_dropped.fetch_add(1, Ordering::Relaxed);
                continue;
            }

            buffer[..header_size].copy_from_slice(&header);
            match self.tunnel.send(&buffer[..header_size + frame_len]).await {
                Ok(()) => {
                    self.stats.frames_to_tunnel.fetch_add(1, Ordering::Relaxed);
                }
                Err(err) => {
                    // Tunnel flapping, peer temporarily unreachable, or an oversized frame: log,
                    // drop, and keep pumping rather than killing the bridge.
                    if is_message_size(&err) {
                        tracing::warn!(frame_len, "frame exceeds the tunnel MTU; lower the TAP MTU");
                    } else {
                        tracing::debug!(error = %err, "transient tunnel send error; frame dropped");
                    }
                }
            }
        }
    }

    /// Tunnel -> TAP: strips the tunnel header and injects the inner frame onto the local segment.
    async fn pump_tunnel_to_tap(&self, shutdown: CancellationToken) -> Result<()> {
        let mut buffer = vec![0u8; 2048];

        loop {
            let len = tokio::select! {
                _ = shutdown.cancelled() => return Ok(()),
                result = self.tunnel.recv(&mut buffer) => result?,
            };

            let Some(offset) = self.encapsulation.inner_frame_offset(&buffer[..len]) else {
                self.stats.frames_dropped.fetch_add(1, Ordering::Relaxed);
                continue;
            };

            let frame = &buffer[offset..len];

            if let Some(detector) = &self.loop_detector {
                if detector.try_handle_probe(frame, BridgePort::Tunnel) {
                    continue;
                }
            }

            if let Some(table) = &self.mac_table {
                if !table.should_forward(frame, BridgePort::Tunnel) {
                    self.stats.frames_dropped.fetch_add(1, Ordering::Relaxed);
                    continue;
                }
            }

            if !self.filter.should_forward(frame) {
                self.stats.frames_dropped.fetch_add(1, Ordering::Relaxed);
                continue;
            }

            if let Err(err) = self.tap.write_frame(frame).await {
                tracing::debug!(error = %err, "transient TAP write error; frame dropped");
                continue;
            }
            self.stats.frames_to_tap.fetch_add(1, Ordering::Relaxed);
        }
    }
}

/// Whether an I/O error is the "message too long" case, i.e. the frame exceeds the tunnel MTU.
fn is_message_size(err: &anyhow::Error) -> bool {
    err.downcast_ref::<std::io::Error>()
        .is_some_and(|e| e.raw_os_error() == Some(10040)) // WSAEMSGSIZE
}

