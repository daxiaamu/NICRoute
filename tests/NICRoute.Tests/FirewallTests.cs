using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using NICRoute.Core;

internal static class FirewallTests
{
    public static async Task<int> RunAsync()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Native WFP verification requires an administrator token; filters only target this test executable.");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var datagrams = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        async Task<bool> SendDatagram()
        {
            using var sender = new UdpClient(AddressFamily.InterNetwork);
            using var timeout = new CancellationTokenSource(500);
            try
            {
                await sender.SendAsync(new byte[] { 7 }, (IPEndPoint)datagrams.Client.LocalEndPoint!, timeout.Token);
                var received = await datagrams.ReceiveAsync(timeout.Token); return received.Buffer.SequenceEqual(new byte[] { 7 });
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException) { return false; }
        }
        async Task<bool> Connect()
        {
            using var client = new TcpClient(); using var timeout = new CancellationTokenSource(1200);
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                using var accepted = await listener.AcceptTcpClientAsync(timeout.Token); return true;
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException) { return false; }
        }
        var passed = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL WFP: " + message); passed++; }
        try
        {
            Check(await Connect(), "test endpoint is reachable before guard");
            Check(await SendDatagram(), "test UDP endpoint is reachable before guard");
            using (var guard = new ApplicationNetworkGuard([new("NICRoute isolated test", Environment.ProcessPath!, false)], preserveLoopback: false))
            {
                Check(guard.FilterCount == 2 && !await Connect(), "dynamic IPv4/IPv6 filters block the test app without probes");
                Check(!await SendDatagram(), "dynamic guard blocks UDP from the test app");
                var loopback = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
                guard.AllowInterface(loopback.Name);
                Check(await Connect(), "only allowed interface can connect");
                Check(await SendDatagram(), "allowed-interface UDP can send");
                var physical = AdapterCatalog.GetAdapters().First(a => a.LinkUp);
                guard.AllowInterface(physical.Name);
                Check(!await Connect(), "wrong next-hop interface is blocked despite being reachable");
                guard.Replace(null);
                Check(!await Connect(), "no allowed interface is fail-closed");
            }
            Check(await Connect(), "disposing dynamic guard restores only the test application's access");
            using (var packaged = new ApplicationNetworkGuard([new("Isolated package SID", "Microsoft.Todos_8wekyb3d8bbwe", true)]))
                Check(packaged.FilterCount == 2, "Windows app package SID guard installs for IPv4 and IPv6");
            Console.WriteLine($"Native dynamic WFP guard: {passed} checks passed; all filters removed."); return passed;
        }
        finally { listener.Stop(); }
    }
}
