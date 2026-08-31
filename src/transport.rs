use std::net::{Ipv4Addr, SocketAddrV4};

use anyhow::Result;
use tokio::net::UdpSocket;

/// UDP tunnel endpoint used for the encapsulated bridge traffic.
///
/// The socket is bound to the tunnel IP so encapsulated frames cannot leak onto the physical LAN,
/// and is left unconnected so the peer can change (dynamic NetBird peers) without rebinding.
pub struct UdpTunnel {
    socket: UdpSocket,
    remote: SocketAddrV4,
}

impl UdpTunnel {
    /// Binds the local UDP endpoint; egress is pinned to the tunnel interface.
    pub async fn bind(local_ip: Ipv4Addr, local_port: u16, remote_ip: Ipv4Addr, remote_port: u16) -> Result<Self> {
        let socket = UdpSocket::bind((local_ip, local_port)).await?;
        Ok(Self {
            socket,
            remote: SocketAddrV4::new(remote_ip, remote_port),
        })
    }

    /// Sends one encapsulated datagram to the configured peer.
    pub async fn send(&self, payload: &[u8]) -> Result<()> {
        self.socket.send_to(payload, self.remote).await?;
        Ok(())
    }

    /// Receives one encapsulated datagram from any peer on the bound port.
    pub async fn recv(&self, buffer: &mut [u8]) -> Result<usize> {
        let (len, _from) = self.socket.recv_from(buffer).await?;
        Ok(len)
    }
}
