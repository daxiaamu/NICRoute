using System.Text.Json;
using System.Text.Json.Nodes;

namespace NICRoute.Core;

public static class ConfigBuilder
{
    public const string TunName = "NICRoute";
    public static JsonObject Build(AppSettings settings, IReadOnlyList<AdapterInfo> adapters,
        int apiPort, string apiSecret, int rejectPort, bool useTun = true, int testPort = 0, int monitorPort = 0)
    {
        settings.Validate();
        var outbounds = new JsonArray
        {
            new JsonObject { ["type"] = "direct", ["tag"] = RuleResolver.SystemTag },
            // sing-box >= 1.13 removed block outbounds. A local SOCKS rejection endpoint lets
            // selectors switch to a real deny destination without recreating the TUN/routes.
            new JsonObject { ["type"] = "socks", ["tag"] = RuleResolver.BlockTag,
                ["server"] = "127.0.0.1", ["server_port"] = rejectPort, ["version"] = "5" }
        };
        foreach (var adapter in adapters)
            outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = adapter.Tag,
                ["bind_interface"] = adapter.Name, ["connect_timeout"] = "8s" });
        var choices = new[] { RuleResolver.SystemTag, RuleResolver.BlockTag }.Concat(adapters.Select(a => a.Tag)).ToArray();
        var selections = RuleResolver.Selections(settings, adapters);
        foreach (var (tag, selected) in selections)
            outbounds.Add(new JsonObject { ["type"] = "selector", ["tag"] = tag,
                ["outbounds"] = Array(choices), ["default"] = selected, ["interrupt_exist_connections"] = true });

        var routeRules = new JsonArray();
        // DNS is intercepted and then sent through the matching app's selected outbound.
        routeRules.Add(new JsonObject { ["port"] = 53, ["action"] = "hijack-dns" });
        var dnsServers = new JsonArray();
        var dnsRules = new JsonArray();
        foreach (var selector in selections.Keys)
            dnsServers.Add(new JsonObject { ["type"] = "udp", ["tag"] = "dns-" + selector,
                ["server"] = settings.DnsServer, ["detour"] = selector });
        foreach (var rule in settings.Rules.Where(r => r.Enabled))
        {
            var routeRule = Match(rule.Application);
            routeRule["action"] = "route";
            routeRule["outbound"] = rule.SelectorTag;
            routeRules.Add(routeRule);
            var dnsRule = Match(rule.Application);
            dnsRule["action"] = "route";
            dnsRule["server"] = "dns-" + rule.SelectorTag;
            dnsRules.Add(dnsRule);
        }
        // A failed process lookup must not silently turn a protected application's
        // connection into an unconfigured connection using the global/system route.
        if (settings.Rules.Any(r => r.Enabled && r.Fallback == FallbackKind.Block))
            routeRules.Add(new JsonObject { ["process_path_regex"] = Array([".+"]), ["invert"] = true,
                ["action"] = "route", ["outbound"] = RuleResolver.BlockTag });
        var inbound = useTun
            ? new JsonObject { ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = TunName,
                ["address"] = Array(["172.31.255.1/30", "fd42:4e49:4352::1/126"]),
                ["auto_route"] = true, ["strict_route"] = true, ["dns_mode"] = "hijack", ["mtu"] = 1500 }
            : new JsonObject { ["type"] = "mixed", ["tag"] = "test-in", ["listen"] = "127.0.0.1", ["listen_port"] = testPort };
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true, ["disabled"] = false },
            ["dns"] = new JsonObject { ["servers"] = dnsServers, ["rules"] = dnsRules,
                ["final"] = "dns-" + RuleResolver.GlobalTag },
            ["inbounds"] = new JsonArray(inbound), ["outbounds"] = outbounds,
            ["route"] = new JsonObject { ["rules"] = routeRules, ["final"] = RuleResolver.GlobalTag,
                ["auto_detect_interface"] = true, ["find_process"] = true,
                ["default_domain_resolver"] = "dns-" + RuleResolver.GlobalTag },
            ["experimental"] = new JsonObject { ["clash_api"] = new JsonObject
                { ["external_controller"] = "127.0.0.1:" + apiPort, ["secret"] = apiSecret } }
        };
        if (monitorPort > 0)
            config["services"] = new JsonArray(new JsonObject { ["type"] = "api", ["tag"] = "application-monitor",
                ["listen"] = "127.0.0.1", ["listen_port"] = monitorPort, ["secret"] = apiSecret });
        return config;
    }

    private static JsonObject Match(ApplicationTarget app) => app.Kind == ApplicationKind.Desktop
        ? new JsonObject { ["process_path_regex"] = Array(["(?i)^" + System.Text.RegularExpressions.Regex.Escape(app.ExecutablePath) + "$"]) }
        : new JsonObject { ["process_path_regex"] = Array([app.PackagePathPattern()]) };
    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    public static string Serialize(JsonNode value) => value.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
