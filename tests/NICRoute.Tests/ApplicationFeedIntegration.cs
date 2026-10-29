using System.Net;
using System.Net.Sockets;
using System.Text;
using NICRoute.Core;

internal static class ApplicationFeedIntegration
{
    public static async Task<int> RunAsync(string enginePath)
    {
        var adapter = AdapterCatalog.GetAdapters().First(a => a.Available && a.Addresses.Any(ip => IPAddress.Parse(ip).AddressFamily == AddressFamily.InterNetwork));
        var address = IPAddress.Parse(adapter.Addresses.First(ip => IPAddress.Parse(ip).AddressFamily == AddressFamily.InterNetwork));
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "application-feed-" + Guid.NewGuid().ToString("N"));
        await using var controller = new EngineController(enginePath, root);
        var settings = new AppSettings { Rules = [new() { Application = new() { Name = "Application feed integration", ExecutablePath = Environment.ProcessPath! }, AdapterId = adapter.Id, Fallback = FallbackKind.Block }] };
        var portFinder = new TcpListener(IPAddress.Loopback, 0); portFinder.Start();
        var proxyPort = ((IPEndPoint)portFinder.LocalEndpoint).Port; portFinder.Stop();
        var destination = new TcpListener(address, 0); destination.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await controller.StartAsync(settings, AdapterCatalog.GetAdapters(), useTun: false, testPort: proxyPort);
            const int up = 65536, down = 131072;
            var server = Task.Run(async () =>
            {
                using var accepted = await destination.AcceptTcpClientAsync(timeout.Token);
                await accepted.GetStream().ReadExactlyAsync(new byte[up], timeout.Token);
                await accepted.GetStream().WriteAsync(new byte[down], timeout.Token);
            });
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
                var stream = client.GetStream(); var endpoint = (IPEndPoint)destination.LocalEndpoint;
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {endpoint} HTTP/1.1\r\nHost: {endpoint}\r\n\r\n"), timeout.Token);
                var response = new List<byte>(); var one = new byte[1];
                while (response.Count < 4096)
                {
                    await stream.ReadExactlyAsync(one, timeout.Token); response.Add(one[0]);
                    if (response.Count >= 4 && response.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                if (!Encoding.ASCII.GetString(response.ToArray()).Contains("200")) throw new Exception("Proxy connection was rejected.");
                await stream.WriteAsync(new byte[up], timeout.Token);
                await stream.ReadExactlyAsync(new byte[down], timeout.Token);
            }
            await server;
            ApplicationTraffic? observed = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                observed = controller.ApplicationTraffic.Snapshot().FirstOrDefault(a => a.Name == "Application feed integration");
                if (observed is { Uploaded: up, Downloaded: down, Connections: 0 }) break;
                await Task.Delay(100, timeout.Token);
            }
            if (observed is not { Uploaded: up, Downloaded: down, Connections: 0 } || !observed.ActualRoute.Contains(adapter.Name))
                throw new Exception("Actual short connection totals/attribution/interface mismatch: " + observed);
            settings.Rules[0].AdapterId = "missing"; settings.Rules[0].Fallback = FallbackKind.GlobalDefault;
            await controller.UpdateSelectionsAsync(settings, AdapterCatalog.GetAdapters());
            using (var automaticClient = new TcpClient())
            {
                await automaticClient.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
                var endpoint = (IPEndPoint)destination.LocalEndpoint;
                await automaticClient.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {endpoint} HTTP/1.1\r\nHost: {endpoint}\r\n\r\n"), timeout.Token);
                using var accepted = await destination.AcceptTcpClientAsync(timeout.Token);
                await automaticClient.GetStream().WriteAsync(new byte[] { 1 }, timeout.Token);
                await accepted.GetStream().ReadExactlyAsync(new byte[1], timeout.Token);
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    controller.RefreshObservedInterfaces(AdapterCatalog.GetAdapters());
                    observed = controller.ApplicationTraffic.Snapshot().First(a => a.Name == "Application feed integration");
                    if (observed.ActualRoute.Contains(adapter.Name + "（自动）")) break;
                    await Task.Delay(100, timeout.Token);
                }
                if (!observed.ActualRoute.Contains(adapter.Name + "（自动）")) throw new Exception("Actual automatic TCP interface could not be confirmed: " + observed);
            }
            Console.WriteLine("Actual application event feed: short TCP upload/download final counters, process attribution and bound interface verified.");
            Console.WriteLine("Actual automatic TCP egress confirmed from engine-owned Windows socket source address.");
            return 4;
        }
        finally { destination.Stop(); await controller.StopAsync(); }
    }
}
