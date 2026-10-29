using System.Net;
using System.Runtime.InteropServices;

namespace NICRoute.Core;

/// <summary>Resolve automatic TCP outbounds from the engine's established OS sockets.
/// Ambiguous endpoints and unrecognised source addresses remain unconfirmed.</summary>
public static class ObservedTcpInterfaces
{
    public static string Endpoint(string destination)
    {
        if (IPEndPoint.TryParse(destination, out var endpoint))
            return endpoint.Address.ToString().Split('%')[0] + "|" + endpoint.Port;
        return "";
    }
    public static IReadOnlyDictionary<string, string[]> Capture(int enginePid, IReadOnlyList<AdapterInfo> adapters)
    {
        var sources = adapters.SelectMany(a => a.Addresses.Select(ip => (Address: ip.Split('%')[0], a.Name)))
            .GroupBy(a => a.Address, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(a => a.Name).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        var routes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (enginePid <= 0) return new Dictionary<string, string[]>();
        foreach (var family in new[] { 2, 23 })
        {
            uint size = 0;
            if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0) != 122 || size is < 4 or > 16 * 1024 * 1024) continue;
            var memory = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetExtendedTcpTable(memory, ref size, false, family, 5, 0) != 0) continue;
                var count = Marshal.ReadInt32(memory); var rowSize = family == 2 ? 24 : 56;
                if (count < 0 || (long)count * rowSize + 4 > size) continue;
                for (var i = 0; i < count; i++)
                {
                    var row = memory + 4 + i * rowSize;
                    var pid = Marshal.ReadInt32(row, family == 2 ? 20 : 52);
                    var state = Marshal.ReadInt32(row, family == 2 ? 0 : 48);
                    if (pid != enginePid || state != 5) continue;
                    var local = ReadAddress(row + (family == 2 ? 4 : 0), family == 2 ? 4 : 16);
                    var remote = ReadAddress(row + (family == 2 ? 12 : 24), family == 2 ? 4 : 16);
                    var portPtr = row + (family == 2 ? 16 : 44);
                    var port = Marshal.ReadByte(portPtr) * 256 + Marshal.ReadByte(portPtr, 1);
                    if (!sources.TryGetValue(local, out var names) || names.Length != 1) continue;
                    var key = remote + "|" + port;
                    if (!routes.TryGetValue(key, out var set)) routes.Add(key, set = new(StringComparer.OrdinalIgnoreCase));
                    set.Add(names[0]);
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        return routes.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }
    private static string ReadAddress(IntPtr value, int length)
    { var bytes = new byte[length]; Marshal.Copy(value, bytes, 0, length); return new IPAddress(bytes).ToString().Split('%')[0]; }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool ordered, int family, int tableClass, uint reserved);
}
