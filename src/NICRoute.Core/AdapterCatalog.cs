using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NICRoute.Core;

public static class AdapterCatalog
{
    public static IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var result = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.Name == ConfigBuilder.TunName) continue;
            try
            {
                var properties = nic.GetIPProperties();
                int index;
                try { index = properties.GetIPv4Properties()?.Index ?? properties.GetIPv6Properties()?.Index ?? 0; }
                catch (NetworkInformationException) { index = properties.GetIPv6Properties()?.Index ?? 0; }
                // Filter Windows' NDIS filter pseudo-adapters, which have no IP interface.
                if (index <= 0) continue;
                var addresses = properties.UnicastAddresses
                    .Where(a => a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred ||
                                a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Deprecated)
                    .Select(a => a.Address)
                    .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal && !a.IsIPv6Multicast &&
                                !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any) &&
                                !(a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes()[0] == 169 && a.GetAddressBytes()[1] == 254))
                    .OrderBy(a => a.AddressFamily).Select(a => a.ToString()).ToArray();
                result.Add(new(nic.Id, nic.Name, nic.Description, index,
                    nic.OperationalStatus == OperationalStatus.Up, addresses));
            }
            catch (NetworkInformationException) { /* Adapter was removed during enumeration. */ }
        }
        return result.DistinctBy(a => a.Index).OrderByDescending(a => a.Available).ThenBy(a => a.Name).ToArray();
    }

    public static async Task<IReadOnlyList<AdapterInfo>> CheckInternetAsync(
        IReadOnlyList<AdapterInfo> adapters, string address, int port, CancellationToken cancellationToken = default)
    {
        var destination = IPAddress.Parse(address);
        return await Task.WhenAll(adapters.Select(async adapter =>
        {
            if (!adapter.LinkUp || adapter.Addresses.Count == 0) return adapter;
            var local = adapter.Addresses.Select(IPAddress.Parse).FirstOrDefault(a => a.AddressFamily == destination.AddressFamily);
            if (local is null) return adapter with { InternetReachable = false };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using var socket = new Socket(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                socket.SetSocketOption(destination.AddressFamily == AddressFamily.InterNetwork ? SocketOptionLevel.IP : SocketOptionLevel.IPv6,
                    (SocketOptionName)31 /* IP_UNICAST_IF / IPV6_UNICAST_IF */, destination.AddressFamily == AddressFamily.InterNetwork
                        ? IPAddress.HostToNetworkOrder(adapter.Index) : adapter.Index);
                socket.Bind(new IPEndPoint(local, 0));
                await socket.ConnectAsync(new IPEndPoint(destination, port), timeout.Token);
                return adapter with { InternetReachable = true };
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            { return adapter with { InternetReachable = false }; }
        }));
    }
}
