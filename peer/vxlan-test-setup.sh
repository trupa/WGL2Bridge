#!/bin/sh
# VXLAN test bridge for WGL2Bridge — Linux peer side.
#
#   sudo ./vxlan-test-setup.sh up       create tunnel + bridge and assign the test IP
#   sudo ./vxlan-test-setup.sh down     remove everything, leaving the host as it was
#   sudo ./vxlan-test-setup.sh status   show current state
#
# No physical NIC is touched: br-industrial holds only vxlan0 plus a test IP, so this is
# safe to run on a single-port host. Add the industrial NIC later with:
#   ip link set <nic> master br-industrial

set -eu

WINDOWS_NB_IP="100.99.58.65"     # Windows peer tunnel IP (from the bridge's startup log)
LOCAL_NB_IP="100.99.63.44"       # this host's NetBird IP
TUNNEL_DEV="wt0"                 # NetBird interface
VNI="4096"                       # must match VxlanVni in bridge.config.json
PORT="4789"                      # must match the Windows side
MTU="1230"                       # must match the "Derived TAP MTU" line on Windows
TEST_IP="192.168.77.60/24"       # test address; must not collide with any local subnet
VXLAN_DEV="vxlan0"
BRIDGE_DEV="br-industrial"

need_root() {
    [ "$(id -u)" -eq 0 ] || { echo "Run as root (sudo)." >&2; exit 1; }
}

up() {
    need_root

    ip link show "$TUNNEL_DEV" >/dev/null 2>&1 || {
        echo "Tunnel interface $TUNNEL_DEV is missing — is NetBird up?" >&2
        exit 1
    }

    # Docker sets bridge-nf-call-iptables=1, which pushes bridged IPv4 through the FORWARD
    # chain where Docker's policy is DROP. ARP would cross but ping would not.
    if [ -e /proc/sys/net/bridge/bridge-nf-call-iptables ]; then
        sysctl -qw net.bridge.bridge-nf-call-iptables=0
        sysctl -qw net.bridge.bridge-nf-call-ip6tables=0
        sysctl -qw net.bridge.bridge-nf-call-arptables=0
    fi

    ip link del "$VXLAN_DEV" 2>/dev/null || true
    ip link del "$BRIDGE_DEV" 2>/dev/null || true

    ip link add "$VXLAN_DEV" type vxlan id "$VNI" \
        local "$LOCAL_NB_IP" remote "$WINDOWS_NB_IP" \
        dstport "$PORT" srcport "$PORT" $((PORT + 1)) \
        nolearning
    ip link set "$VXLAN_DEV" mtu "$MTU"

    ip link add "$BRIDGE_DEV" type bridge
    ip link set "$BRIDGE_DEV" type bridge stp_state 0
    ip link set "$BRIDGE_DEV" mtu "$MTU"
    ip link set "$VXLAN_DEV" master "$BRIDGE_DEV"

    # The IP lives on the bridge, not on the enslaved vxlan0.
    ip addr add "$TEST_IP" dev "$BRIDGE_DEV"

    ip link set "$VXLAN_DEV" up
    ip link set "$BRIDGE_DEV" up

    echo "Up: $VXLAN_DEV (VNI $VNI, $LOCAL_NB_IP -> $WINDOWS_NB_IP:$PORT) on $BRIDGE_DEV ${TEST_IP}"
}

down() {
    need_root
    ip link del "$VXLAN_DEV" 2>/dev/null || true
    ip link del "$BRIDGE_DEV" 2>/dev/null || true
    echo "Down: $VXLAN_DEV and $BRIDGE_DEV removed."
}

status() {
    echo "--- links ---"
    ip -br addr show "$VXLAN_DEV" 2>/dev/null || echo "$VXLAN_DEV: absent"
    ip -br addr show "$BRIDGE_DEV" 2>/dev/null || echo "$BRIDGE_DEV: absent"
    echo "--- bridge ports ---"
    bridge link show 2>/dev/null | grep "$BRIDGE_DEV" || echo "(none)"
    echo "--- learned MACs ---"
    bridge fdb show br "$BRIDGE_DEV" 2>/dev/null || echo "(none)"
    echo "--- bridge netfilter (want 0) ---"
    sysctl net.bridge.bridge-nf-call-iptables 2>/dev/null || echo "(br_netfilter not loaded)"
}

case "${1:-}" in
    up)     up ;;
    down)   down ;;
    status) status ;;
    *)      echo "Usage: $0 {up|down|status}" >&2; exit 1 ;;
esac
