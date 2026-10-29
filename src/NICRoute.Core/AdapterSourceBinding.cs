using System.Net;
using System.Net.Sockets;

namespace NICRoute.Core;

public sealed record AdapterSourceBinding(string AdapterId, string Tag, string InterfaceName, string? IPv4, string? IPv6)
{
    public static AdapterSourceBinding Capture(AdapterInfo adapter)
    {
        var addresses = adapter.Addresses
            .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
            .Where(address => address is not null)
            .ToArray();
        var ipv4 = addresses.FirstOrDefault(address => address!.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        var ipv6 = addresses.FirstOrDefault(address => address!.AddressFamily == AddressFamily.InterNetworkV6)?.ToString();
        return new(adapter.Id, adapter.Tag, adapter.Name, ipv4, ipv6);
    }

    public bool Matches(AdapterInfo adapter)
    {
        var current = Capture(adapter);
        return AdapterId == current.AdapterId && Tag == current.Tag &&
            InterfaceName.Equals(current.InterfaceName, StringComparison.Ordinal) &&
            IPv4 == current.IPv4 && IPv6 == current.IPv6;
    }

    public IEnumerable<ApplicationNetworkGuard.EgressBinding> GuardBindings()
    {
        if (IPv4 is not null) yield return new(IPv4, InterfaceName);
        if (IPv6 is not null) yield return new(IPv6, InterfaceName);
    }

    public static void ValidateUnique(IEnumerable<AdapterSourceBinding> bindings)
    {
        foreach (var group in bindings.SelectMany(binding => binding.GuardBindings())
                     .GroupBy(binding => binding.LocalAddress, StringComparer.OrdinalIgnoreCase))
        {
            var interfaces = group.Select(binding => binding.InterfaceName)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (interfaces.Length > 1)
                throw new InvalidDataException(
                    $"{group.Key} 同时属于多个网卡（{string.Join("、", interfaces)}），地址归属不唯一，不能启动网卡出口约束。");
        }
    }

    public static string StabilizeTarget(string target, IReadOnlyList<AdapterInfo> current,
        IReadOnlyDictionary<string, AdapterSourceBinding> configured, out string? changedInterface)
    {
        changedInterface = null;
        if (target is RuleResolver.SystemTag or RuleResolver.BlockTag) return target;
        var adapter = current.FirstOrDefault(item => item.Tag == target);
        if (adapter is not null && configured.TryGetValue(target, out var binding) && binding.Matches(adapter))
            return target;
        changedInterface = adapter?.Name ?? target;
        return RuleResolver.BlockTag;
    }
}
