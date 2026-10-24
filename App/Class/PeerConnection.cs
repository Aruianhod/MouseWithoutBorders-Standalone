using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MouseWithoutBorders.Class;

internal static class PeerConnection
{
    internal const int ConnectTimeoutMilliseconds = 3000;
    internal const int HandshakeReadTimeoutMilliseconds = 5000;
    // The UI timer runs every 100 ms. Poll only for missing connections, not reopen all sockets.
    internal static bool RetryDue(long tick) => tick > 0 && tick % 20 == 0;
    internal static string IncomingLabel(Socket socket) => socket.RemoteEndPoint.ToString();
    internal static async Task ConnectAsync(Socket socket, IPAddress address, int port, CancellationToken stopped,
        int timeoutMilliseconds = ConnectTimeoutMilliseconds)
    {
        socket.NoDelay = true;
        await WithDeadlineAsync(token => socket.ConnectAsync(new IPEndPoint(PeerAddressCache.Normalize(address), port), token).AsTask(), stopped, timeoutMilliseconds).ConfigureAwait(false);
    }
    internal static async Task WithDeadlineAsync(Func<CancellationToken, Task> action, CancellationToken stopped, int timeoutMilliseconds)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopped);
        timeout.CancelAfter(timeoutMilliseconds);
        try { await action(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!stopped.IsCancellationRequested)
        {
            throw new TimeoutException("连接尝试超过 " + timeoutMilliseconds + " 毫秒，将自动重试。");
        }
    }
}
