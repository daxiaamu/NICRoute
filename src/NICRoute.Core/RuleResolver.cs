namespace NICRoute.Core;

public static class RuleResolver
{
    public const string SystemTag = "windows-default";
    public const string BlockTag = "offline";
    public const string GlobalTag = "global-default";

    public static RouteDecision Global(AppSettings settings, IReadOnlyList<AdapterInfo> adapters)
    {
        if (string.IsNullOrEmpty(settings.GlobalAdapterId))
            return new(SystemTag, "Windows 自动选择", false, false);
        var adapter = adapters.FirstOrDefault(a => a.Id == settings.GlobalAdapterId);
        return adapter?.Available == true
            ? new(adapter.Tag, adapter.Name, false, false)
            : new(BlockTag, "全局网卡不可用 · 不联网", false, true);
    }

    public static RouteDecision Resolve(AppRule rule, AppSettings settings, IReadOnlyList<AdapterInfo> adapters)
    {
        var primary = adapters.FirstOrDefault(a => a.Id == rule.AdapterId);
        if (!rule.Enabled) return Global(settings, adapters) with { Label = "规则已停用 · 跟随全局" };
        if (primary?.Available == true) return new(primary.Tag, primary.Name, false, false);
        if (rule.Fallback == FallbackKind.GlobalDefault)
        {
            var global = Global(settings, adapters);
            return global with { Label = "回退 → " + global.Label, IsFallback = true };
        }
        if (rule.Fallback == FallbackKind.Adapter)
        {
            var backup = adapters.FirstOrDefault(a => a.Id == rule.FallbackAdapterId);
            if (backup?.Available == true) return new(backup.Tag, "回退 → " + backup.Name, true, false);
        }
        return new(BlockTag, "不联网", true, true);
    }

    public static Dictionary<string, string> Selections(AppSettings settings, IReadOnlyList<AdapterInfo> adapters)
    {
        var result = new Dictionary<string, string> { [GlobalTag] = Global(settings, adapters).Outbound };
        foreach (var rule in settings.Rules.Where(r => r.Enabled))
            result[rule.SelectorTag] = Resolve(rule, settings, adapters).Outbound;
        return result;
    }
}
