using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NICRoute.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ApplicationKind>))]
public enum ApplicationKind { Desktop, WindowsPackage }
[JsonConverter(typeof(JsonStringEnumConverter<FallbackKind>))]
public enum FallbackKind { Block, GlobalDefault, Adapter }

public sealed class ApplicationTarget
{
    public ApplicationKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string PackageFamilyName { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string PublisherId { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string Identity => Kind == ApplicationKind.WindowsPackage
        ? "package:" + PackageFamilyName.ToLowerInvariant()
        : "exe:" + ExecutablePath.ToLowerInvariant();
    public string Detail => Kind == ApplicationKind.WindowsPackage ? PackageFamilyName : ExecutablePath;
    public string KindLabel => Kind == ApplicationKind.WindowsPackage ? "Windows 应用" : "桌面程序";

    // Match every executable inside this package, including helpers, across version updates.
    // Do not use process_name: two installed applications may have identical exe names.
    public string PackagePathPattern()
    {
        if (Kind != ApplicationKind.WindowsPackage) throw new InvalidOperationException();
        var root = InstallLocation.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(root) ?? throw new InvalidDataException("应用安装目录无效。");
        var folder = Path.GetFileName(root);
        if (folder.StartsWith(PackageName + "_", StringComparison.OrdinalIgnoreCase) &&
            folder.EndsWith("_" + PublisherId, StringComparison.OrdinalIgnoreCase))
        {
            // Go/RE2-compatible expression. Versions, architectures and resource IDs may change.
            return "(?i)^" + Regex.Escape(parent + "\\" + PackageName + "_") +
                   @"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+_[^\\]*_[^\\]*_" +
                   Regex.Escape(PublisherId) + @"\\.+$";
        }
        // Sideloaded/unpackaged development installs need not use WindowsApps directory names.
        return "(?i)^" + Regex.Escape(root + "\\") + @".+$";
    }
}

public sealed class AppRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public ApplicationTarget Application { get; set; } = new();
    public string AdapterId { get; set; } = "";
    public FallbackKind Fallback { get; set; } = FallbackKind.Block;
    public string FallbackAdapterId { get; set; } = "";
    public string SelectorTag => "app-" + Id.ToString("N");
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    // Empty means Windows chooses its default physical interface, never our TUN interface.
    public string GlobalAdapterId { get; set; } = "";
    public string DnsServer { get; set; } = "223.5.5.5";
    public bool ProbeInternet { get; set; }
    public string ProbeAddress { get; set; } = "223.5.5.5";
    public int ProbePort { get; set; } = 443;
    public List<AppRule> Rules { get; set; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("不支持此配置版本。");
        if (!IPAddress.TryParse(DnsServer, out _)) throw new InvalidDataException("DNS 服务器必须是 IPv4 或 IPv6 地址。");
        if (!IPAddress.TryParse(ProbeAddress, out _) || ProbePort is < 1 or > 65535)
            throw new InvalidDataException("连通性检测地址或端口无效。");
        if (Rules is null || Rules.Any(r => r is null || r.Application is null))
            throw new InvalidDataException("应用规则无效。");
        if (Rules.Select(r => r.Id).Distinct().Count() != Rules.Count)
            throw new InvalidDataException("规则 ID 重复。");
        if (Rules.GroupBy(r => r.Application.Identity, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("同一个应用只能有一条规则。");
        foreach (var rule in Rules)
        {
            var app = rule.Application;
            if (!Enum.IsDefined(app.Kind) || !Enum.IsDefined(rule.Fallback)) throw new InvalidDataException("规则类型无效。");
            if (string.IsNullOrWhiteSpace(rule.AdapterId)) throw new InvalidDataException("请选择应用的首选网卡。");
            if (rule.Fallback == FallbackKind.Adapter &&
                (string.IsNullOrWhiteSpace(rule.FallbackAdapterId) || rule.FallbackAdapterId == rule.AdapterId))
                throw new InvalidDataException("回退网卡必须与首选网卡不同。");
            if (app.Kind == ApplicationKind.Desktop &&
                (!Path.IsPathFullyQualified(app.ExecutablePath) || !app.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("桌面程序需要完整的 exe 路径。");
            if (app.Kind == ApplicationKind.WindowsPackage &&
                (string.IsNullOrWhiteSpace(app.PackageFamilyName) || string.IsNullOrWhiteSpace(app.PackageName) ||
                 string.IsNullOrWhiteSpace(app.PublisherId) || !Path.IsPathFullyQualified(app.InstallLocation)))
                throw new InvalidDataException("Windows 应用包信息不完整，请重新选择应用。");
        }
    }
}

public sealed record AdapterInfo(string Id, string Name, string Description, int Index,
    bool LinkUp, IReadOnlyList<string> Addresses, bool? InternetReachable = null)
{
    public bool Available => LinkUp && Addresses.Count > 0 && InternetReachable != false;
    public string DisplayName => Name;
    public string Status => !LinkUp ? "已断开" : Addresses.Count == 0 ? "无可用地址" :
        InternetReachable == false ? "检测未通过" : InternetReachable == true ? "检测可达" : "已连接";
    public string AddressLabel => Addresses.Count == 0 ? "暂无 IP 地址" : string.Join("  ·  ", Addresses.Take(2));
    public string Tag => "nic-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id)))[..16].ToLowerInvariant();
}

public sealed record RouteDecision(string Outbound, string Label, bool IsFallback, bool IsBlocked);
