using System.Net.Sockets;
using WGL2Bridge.Logging;

namespace WGL2Bridge.Transport;

/// <summary>Socket preparation shared by the transports.</summary>
internal static class SocketTuning
{
    /// <summary>
    /// Applies the fragmentation policy to the encapsulation socket. Allowing fragmentation means
    /// clearing the DF bit, so the IP stack splits an oversized encapsulated packet instead of
    /// failing the send with WSAEMSGSIZE.
    /// </summary>
    public static void ApplyFragmentPolicy(Socket socket, bool allowFragmentation)
    {
        try
        {
            socket.DontFragment = !allowFragmentation;
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
        {
            // Non-fatal: the OS default then applies.
            BridgeLog.Debug($"Could not set DontFragment='{!allowFragmentation}' on the encapsulation socket: {ex.Message}");
        }
    }
}
