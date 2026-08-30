using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;

namespace WGL2Bridge.Metrics;

/// <summary>
/// Minimal loopback-only HTTP metrics endpoint. Avoids HttpListener (which needs URL ACLs) by
/// serving a tiny text/plain response over a raw TcpListener. AOT-friendly and dependency-free.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MetricsEndpoint
{
    /// <summary>Listens on 127.0.0.1:<paramref name="port"/> until <paramref name="ct"/> is cancelled.</summary>
    public static async Task RunAsync(int port, Func<string> snapshot, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client, snapshot), CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleClientAsync(TcpClient client, Func<string> snapshot)
    {
        try
        {
            using var stream = client.GetStream();

            var request = new byte[1024];
            _ = await stream.ReadAsync(request).ConfigureAwait(false);

            string body = snapshot();
            byte[] header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                "Connection: close\r\n\r\n");

            await stream.WriteAsync(header).ConfigureAwait(false);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body)).ConfigureAwait(false);
        }
        catch
        {
            // Client disconnected mid-request; nothing to do.
        }
        finally
        {
            client.Dispose();
        }
    }
}
