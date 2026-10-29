using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NICRoute.Core.Updates;

public sealed record ProductIdentity(string AppId, long VersionCode, string VersionName, string Platform)
{
    public static ProductIdentity Read(string file)
    {
        using var input = File.OpenRead(file);
        using var pe = new PEReader(input);
        if (!pe.HasMetadata) throw new InvalidDataException("安装包不是 NICRoute 程序。");
        var reader = pe.GetMetadataReader();
        var definition = reader.GetAssemblyDefinition();
        if (reader.GetString(definition.Name) != "NICRoute") throw new InvalidDataException("安装包的程序集身份不匹配。");
        var values = new Dictionary<string, string>();
        foreach (var handle in definition.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (reader.GetString(type.Namespace) != "System.Reflection" || reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) continue;
            var key = blob.ReadSerializedString(); var value = blob.ReadSerializedString();
            if (key is not null && value is not null && !values.TryAdd(key, value)) throw new InvalidDataException("程序集更新身份字段重复。");
        }
        if (!long.TryParse(values.GetValueOrDefault("NICRoute.VersionCode"), out var code) || code < 1 ||
            values.GetValueOrDefault("NICRoute.AppId") != "NICRoute.Windows" || values.GetValueOrDefault("NICRoute.Platform") != "win-x64")
            throw new InvalidDataException("此程序缺少有效的 NICRoute 更新身份。");
        var version = definition.Version;
        var name=values.GetValueOrDefault("NICRoute.VersionName")??$"{version.Major}.{version.Minor}.{version.Build}";
        if(!Regex.IsMatch(name,@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$") ||
            !Version.TryParse(name.Split('-')[0],out var declared) || declared.Major!=version.Major || declared.Minor!=version.Minor || declared.Build!=version.Build)
            throw new InvalidDataException("程序本体版本字段不一致。");
        return new("NICRoute.Windows", code, name, "win-x64");
    }
}

public sealed record PackageFile(string Path, long Size, string Sha256);
public sealed record PackageIndex(int SchemaVersion, string AppId, long VersionCode, string VersionName, string Platform, PackageFile[] Files)
{
    public const string FileName = "package-manifest.json";
    public void Validate()
    {
        if (SchemaVersion != 1 || AppId != "NICRoute.Windows" || Platform != "win-x64" || VersionCode < 1 ||
            !Regex.IsMatch(VersionName ?? "", @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$") || Files is null || Files.Length is < 5 or > 5000)
            throw new InvalidDataException("安装包文件清单无效。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var file in Files)
        {
            SafeUpdatePath.Relative(file.Path); UpdateJson.RequireHash(file.Sha256);
            if (file.Path.Equals(FileName, StringComparison.OrdinalIgnoreCase) || !paths.Add(file.Path) || file.Size is < 0 or > 512L * 1024 * 1024)
                throw new InvalidDataException("安装包文件清单存在重复或非法路径。");
            size = checked(size + file.Size);
        }
        if (size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("安装包展开大小超过限制。");
        foreach (var path in new[] { "NICRoute.exe", "NICRoute.dll", "NICRoute.Updater.exe", "NICRoute.Updater.dll", "engine/sing-box.exe", "engine/engine.lock.json" })
            if (!paths.Contains(path)) throw new InvalidDataException("安装包缺少必要文件：" + path);
    }
}

public static class SafeUpdatePath
{
    public static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Any(char.IsControl) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') ||
            path.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<','>','"','|','?','*']) >= 0))
            throw new InvalidDataException("安装包包含非法路径。");
        foreach (var part in path.Split('/'))
            if (Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                throw new InvalidDataException("安装包包含设备路径。");
        return path;
    }
    public static string InRoot(string root, string relative)
    {
        Relative(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新路径超出安装目录。");
        return full;
    }
    public static void NoReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新路径中不能包含符号链接或目录联接。");
            current = Path.GetDirectoryName(current);
        }
    }
}

public sealed class PackageVerifier
{
    public static PackageIndex CreateIndex(string directory)
    {
        var identity = ProductIdentity.Read(Path.Combine(directory, "NICRoute.dll"));
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetRelativePath(directory, p).Replace('\\', '/') != PackageIndex.FileName)
            .Select(p => new PackageFile(Path.GetRelativePath(directory, p).Replace('\\', '/'), new FileInfo(p).Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant()))
            .OrderBy(p => p.Path, StringComparer.Ordinal).ToArray();
        var index = new PackageIndex(1, identity.AppId, identity.VersionCode, identity.VersionName, identity.Platform, files);
        index.Validate(); return index;
    }
    public static PackageIndex ReadIndex(string directory)
    {
        var index = UpdateJson.Read<PackageIndex>(File.ReadAllBytes(Path.Combine(directory, PackageIndex.FileName)));
        index.Validate(); return index;
    }
    public async Task<PackageIndex> ExtractAsync(string zipPath, string stage, UpdateManifest manifest, CancellationToken token = default)
    {
        // Rehash the current file every time; never trust a previous "download verified" flag.
        if (!await UpdateDownloader.MatchesAsync(zipPath, manifest, token)) throw new InvalidDataException("安装前 SHA256 重新校验失败，请重新下载。");
        SafeUpdatePath.NoReparsePoints(stage);
        if (Directory.Exists(stage)) throw new InvalidDataException("更新暂存目录已存在。");
        using var archive = ZipFile.OpenRead(zipPath);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.TrimEnd('/'); SafeUpdatePath.Relative(path);
            if (!names.Add(path) || (entry.ExternalAttributes & 0x400) != 0 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("更新包包含重复路径、符号链接或目录联接。");
        }
        var indexEntry = archive.GetEntry(PackageIndex.FileName) ?? throw new InvalidDataException("更新包缺少文件清单。");
        if (indexEntry.Length > 1024 * 1024) throw new InvalidDataException("文件清单超过大小限制。");
        using var memory = new MemoryStream();
        await using (var input = indexEntry.Open()) await CopyBoundedAsync(input,memory,indexEntry.Length,token);
        var index = UpdateJson.Read<PackageIndex>(memory.ToArray()); index.Validate();
        if (index.VersionCode != manifest.VersionCode || index.VersionName != manifest.VersionName || index.AppId != manifest.AppId || index.Platform != manifest.Platform)
            throw new InvalidDataException("更新包版本或应用身份与更新清单不符。");
        var entries = archive.Entries.Where(e => e.Name != "" && e.FullName != PackageIndex.FileName).ToDictionary(e => e.FullName, StringComparer.OrdinalIgnoreCase);
        if (entries.Count != index.Files.Length) throw new InvalidDataException("更新包包含清单之外的文件。");
        Directory.CreateDirectory(stage);
        foreach (var file in index.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(file.Path, out var entry) || entry.Length != file.Size) throw new InvalidDataException("文件缺失或大小不符：" + file.Path);
            var target = SafeUpdatePath.InRoot(stage, file.Path); SafeUpdatePath.NoReparsePoints(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var input = entry.Open()) await CopyBoundedAsync(input,output,file.Size,token);
            if (new FileInfo(target).Length != file.Size || await FileHashAsync(target, token) != file.Sha256)
                throw new InvalidDataException("安装包文件校验失败：" + file.Path);
        }
        await File.WriteAllBytesAsync(Path.Combine(stage, PackageIndex.FileName), memory.ToArray(), token);
        var identity = ProductIdentity.Read(Path.Combine(stage, "NICRoute.dll"));
        if (identity.VersionCode != manifest.VersionCode || identity.VersionName != manifest.VersionName)
            throw new InvalidDataException("程序本体版本与更新清单不一致。");
        return index;
    }
    public static async Task VerifyDirectoryAsync(string directory, PackageIndex index, CancellationToken token = default)
    {
        index.Validate();
        foreach (var file in index.Files)
        {
            var path = SafeUpdatePath.InRoot(directory, file.Path); SafeUpdatePath.NoReparsePoints(path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size || await FileHashAsync(path, token) != file.Sha256)
                throw new InvalidDataException("安装文件不完整或已被修改：" + file.Path);
        }
    }
    public static async Task<string> FileHashAsync(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
    private static async Task CopyBoundedAsync(Stream source,Stream destination,long limit,CancellationToken token)
    {
        var buffer=new byte[65536]; long count=0; int read;
        while((read=await source.ReadAsync(buffer,token))>0)
        {
            count+=read;
            if(count>limit) throw new InvalidDataException("ZIP 文件展开大小超过声明值。");
            await destination.WriteAsync(buffer.AsMemory(0,read),token);
        }
        if(count!=limit) throw new InvalidDataException("ZIP 文件展开不完整。");
    }
}
