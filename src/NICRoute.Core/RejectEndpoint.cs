using System.Net;
using System.Net.Sockets;

namespace NICRoute.Core;

/// <summary>Only implements the SOCKS5 greeting. Always refuses all authentication methods.
/// No destination addresses are read; no outbound TCP or UDP socket is ever created.</summary>
public sealed class RejectEndpoint : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private Task? accepting;
    public int Port { get; private set; }
    public void Start()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                _ = RejectAsync(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task RejectAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var header = new byte[2];
                var stream = client.GetStream();
                await stream.ReadExactlyAsync(header, timeout.Token);
                if (header[0] == 5) await stream.WriteAsync(new byte[] { 5, 255 }, timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        listener.Stop();
        if (accepting is not null) await accepting;
        cancellation.Dispose();
    }
}
