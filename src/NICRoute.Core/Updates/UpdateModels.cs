using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NICRoute.Core.Updates;

public static class UpdateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static T Read<T>(ReadOnlySpan<byte> bytes) where T : class
    {
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("更新信息超过大小限制。");
        using (var document = JsonDocument.Parse(bytes.ToArray())) CheckDuplicates(document.RootElement);
        try { return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("更新信息为空。"); }
        catch (JsonException e) { throw new InvalidDataException("更新信息格式无效。", e); }
    }
    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("更新信息存在重复字段。");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckDuplicates(child);
    }
    public static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    public static void RequireHash(string hash)
    { if (hash is null || !Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new InvalidDataException("更新文件缺少有效 SHA256。"); }
    public static Uri Https(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("更新地址必须为 HTTPS。");
        return uri;
    }
    public static DateTimeOffset Timestamp(string value)
    {
        if (!Regex.IsMatch(value ?? "", @"(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException("更新信息时间必须包含时区。");
        return date;
    }
}

public sealed record UpdatePointer
{
    public int SchemaVersion { get; init; } = 1;
    public required string Channel { get; init; }
    public required long PolicyRevision { get; init; }
    public required string ManifestPath { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string ExpiresAt { get; init; }
    public void Validate(string channel, DateTimeOffset now)
    {
        UpdateRules.Channel(Channel);
        if (SchemaVersion != 1 || Channel != channel || PolicyRevision < 1) throw new InvalidDataException("更新渠道或协议不匹配。");
        UpdateJson.RequireHash(ManifestSha256);
        var expected = $"updates/{Channel}/manifests/{PolicyRevision}-{ManifestSha256}.json";
        if (ManifestPath != expected) throw new InvalidDataException("更新清单路径无效。");
        if (UpdateJson.Timestamp(ExpiresAt) <= now) throw new InvalidDataException("更新信息已过期，请稍后重试。");
    }
    [JsonIgnore] public string Digest => UpdateJson.Hash(UpdateJson.Bytes(this));
}

public sealed record UpdateManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string AppId { get; init; } = "NICRoute.Windows";
    public string Platform { get; init; } = "win-x64";
    public required string Channel { get; init; }
    public required long PolicyRevision { get; init; }
    public required long VersionCode { get; init; }
    public required string VersionName { get; init; }
    public long MaxForcedVersionCode { get; init; }
    public string? PublishedAt { get; init; }
    public string Changelog { get; init; } = "";
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
    public required string[] Urls { get; init; }
    public required string ReleaseUrl { get; init; }
    public bool RequiredFor(long currentCode) => currentCode <= MaxForcedVersionCode && VersionCode > currentCode;
    [JsonIgnore] public string? LocalPublishedAt
    {
        get
        {
            try { return PublishedAt is null ? null : UpdateJson.Timestamp(PublishedAt).ToLocalTime().ToString("g", CultureInfo.CurrentCulture); }
            catch (InvalidDataException) { return null; }
        }
    }
    public void Validate(UpdatePointer pointer)
    {
        UpdateRules.Channel(Channel);
        if (SchemaVersion != 1 || AppId != "NICRoute.Windows" || Platform != "win-x64" || Channel != pointer.Channel || PolicyRevision != pointer.PolicyRevision)
            throw new InvalidDataException("更新清单的应用、平台或渠道不匹配。");
        if (VersionCode < 1 || MaxForcedVersionCode < 0 || MaxForcedVersionCode >= VersionCode ||
            string.IsNullOrWhiteSpace(VersionName) || VersionName.Length > 64 || Changelog is null || Changelog.Length > 65536)
            throw new InvalidDataException("更新版本或更新策略无效。");
        if (Size is < 1 or > 1024L * 1024 * 1024) throw new InvalidDataException("更新文件大小无效。");
        UpdateJson.RequireHash(Sha256);
        if (Urls is null || Urls.Length is < 1 or > 16 || Urls.Distinct(StringComparer.Ordinal).Count() != Urls.Length)
            throw new InvalidDataException("更新下载地址无效。");
        foreach (var url in Urls) UpdateJson.Https(url);
        var release = UpdateJson.Https(ReleaseUrl);
        if (release.Host != "github.com" || !release.AbsolutePath.StartsWith("/daxiaamu/NICRoute/releases/tag/", StringComparison.Ordinal))
            throw new InvalidDataException("更新必须来自 NICRoute 官方仓库。");
        var tag = release.AbsolutePath["/daxiaamu/NICRoute/releases/tag/".Length..];
        if(tag!="v"+VersionName) throw new InvalidDataException("Release 标签与更新版本名不一致。");
        var official = $"https://github.com/daxiaamu/NICRoute/releases/download/{tag}/NICRoute-win-x64.zip";
        if (!Urls.Contains(official, StringComparer.Ordinal)) throw new InvalidDataException("更新清单缺少官方安装包地址。");
    }
}

public static class UpdateRules
{
    public static void Channel(string channel)
    { if (channel is not ("stable" or "beta")) throw new InvalidDataException("未知更新渠道。"); }
}

public sealed class ChannelTrust
{
    public long HighestRevision { get; set; }
    public string PointerDigest { get; set; } = "";
    public long MaxForcedVersionCode { get; set; }
    public UpdatePointer? Pointer { get; set; }
}
public sealed class UpdatePreferences
{
    public bool CheckOnStartup { get; set; } = true;
    public int DownloadConnections { get; set; } = 32;
    public string Channel { get; set; } = "stable";
    public long SkippedVersionCode { get; set; }
    public Dictionary<string, ChannelTrust> Trust { get; set; } = [];
    public ChannelTrust For(string channel)
    { if (!Trust.TryGetValue(channel, out var value)) Trust[channel] = value = new(); return value; }
}
public sealed class UpdatePreferenceStore(string directory)
{
    public string PathName => Path.Combine(directory, "update-settings.json");
    public UpdatePreferences Load()
    {
        if (!File.Exists(PathName)) return new();
        var settings = UpdateJson.Read<UpdatePreferences>(File.ReadAllBytes(PathName));
        UpdateRules.Channel(settings.Channel);
        if (settings.SkippedVersionCode < 0 || settings.Trust is null || settings.DownloadConnections is < 1 or > 32) throw new InvalidDataException("更新设置无效。");
        foreach(var (channel,trust) in settings.Trust)
        {
            UpdateRules.Channel(channel);
            if(trust is null||trust.HighestRevision<0||trust.MaxForcedVersionCode<0) throw new InvalidDataException("更新信任状态损坏。");
            if(trust.HighestRevision>0&&(trust.Pointer is null||trust.Pointer.Channel!=channel||trust.Pointer.PolicyRevision!=trust.HighestRevision||trust.Pointer.Digest!=trust.PointerDigest))
                throw new InvalidDataException("更新信任指针与摘要不一致。");
        }
        return settings;
    }
    public void Save(UpdatePreferences settings)
    {
        Directory.CreateDirectory(directory);
        var temp = PathName + ".tmp";
        File.WriteAllBytes(temp, UpdateJson.Bytes(settings));
        File.Move(temp, PathName, true);
    }
}
