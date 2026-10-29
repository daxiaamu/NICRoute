using System.Diagnostics;
using System.Text.Json.Serialization;

namespace NICRoute.Core.Updates;

public sealed record InstallPlan
{
    public required string SessionId { get; init; }
    public required string DataDirectory { get; init; }
    public required string InstallDirectory { get; init; }
    public required string StageDirectory { get; init; }
    public required string ZipPath { get; init; }
    public required UpdateManifest Manifest { get; init; }
    public required long CurrentVersionCode { get; init; }
    public required int ParentProcessId { get; init; }
    public required long ParentStartedUtcTicks { get; init; }
    public bool ResumeRouting { get; init; }
    [JsonIgnore] public string SessionDirectory => Path.Combine(DataDirectory, "updates", Guid.ParseExact(SessionId, "N").ToString("N"));
    [JsonIgnore] public string ReadyPath => Path.Combine(SessionDirectory, "app-ready");
    public void Validate()
    {
        if (!Guid.TryParseExact(SessionId, "N", out _) || !Path.IsPathFullyQualified(DataDirectory) ||
            !Path.IsPathFullyQualified(InstallDirectory) || !Path.IsPathFullyQualified(ZipPath) || CurrentVersionCode < 1 ||
            Manifest.VersionCode <= CurrentVersionCode || ParentProcessId < 1 || ParentStartedUtcTicks < 1)
            throw new InvalidDataException("更新安装计划无效。");
        if (!Path.GetFullPath(StageDirectory).Equals(Path.Combine(SessionDirectory, "stage"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新暂存目录与安装会话不符。");
        var install = Path.GetFullPath(InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(install) is null ||
            install.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) ||
            install.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不能将系统根目录作为更新目标。");
        if (StageDirectory.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            install.StartsWith(Path.GetFullPath(SessionDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新暂存目录不能与安装目录重叠。");
        SafeUpdatePath.NoReparsePoints(install); SafeUpdatePath.NoReparsePoints(SessionDirectory);
        SafeUpdatePath.NoReparsePoints(ZipPath);
        Manifest.Validate(new UpdatePointer { Channel=Manifest.Channel, PolicyRevision=Manifest.PolicyRevision,
            ManifestPath="unused", ManifestSha256=new string('0',64), ExpiresAt=DateTimeOffset.UtcNow.AddDays(1).ToString("O") });
    }
}

public sealed class UpdateInstallException(string message, bool rolledBack, Exception inner) : IOException(message, inner)
{ public bool RolledBack { get; } = rolledBack; }

/// <summary>Replace only files managed by the two package inventories. User-created files
/// and LocalAppData settings are untouched. The caller keeps the backup until startup succeeds.</summary>
public sealed class FileUpdateTransaction(string stage, string install, string backup, PackageIndex index)
{
    private readonly List<(string Path, bool Existed)> touched = [];
    public async Task ApplyAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        index.Validate();
        SafeUpdatePath.NoReparsePoints(stage); SafeUpdatePath.NoReparsePoints(install); SafeUpdatePath.NoReparsePoints(backup);
        await PackageVerifier.VerifyDirectoryAsync(stage, index, token);
        if (Directory.Exists(backup)) throw new IOException("更新备份目录已存在。");
        var newPaths = index.Files.Select(f => f.Path).Append(PackageIndex.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var managed = new HashSet<string>(newPaths, StringComparer.OrdinalIgnoreCase);
        if (File.Exists(Path.Combine(install, PackageIndex.FileName)))
            managed.UnionWith(PackageVerifier.ReadIndex(install).Files.Select(f => f.Path));
        Directory.CreateDirectory(backup);
        var existed = new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        // Finish every backup before the first destination mutation.
        foreach (var relative in managed)
        {
            var source = SafeUpdatePath.InRoot(install, relative); SafeUpdatePath.NoReparsePoints(source);
            existed[relative] = File.Exists(source);
            if (!existed[relative]) continue;
            var target = SafeUpdatePath.InRoot(backup, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, false);
        }
        try
        {
            foreach (var relative in managed)
            {
                token.ThrowIfCancellationRequested();
                var target = SafeUpdatePath.InRoot(install, relative); SafeUpdatePath.NoReparsePoints(target);
                touched.Add((relative, existed[relative]));
                if (newPaths.Contains(relative))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(SafeUpdatePath.InRoot(stage, relative), target, true);
                }
                else if (File.Exists(target)) File.Delete(target);
                progress?.Report(relative);
            }
            await PackageVerifier.VerifyDirectoryAsync(install, index, token);
        }
        catch (Exception error)
        {
            try { Rollback(); }
            catch (Exception restoreError) { throw new UpdateInstallException("更新和回滚失败。请保留备份目录：" + backup, false, new AggregateException(error, restoreError)); }
            throw new UpdateInstallException("更新失败，已恢复原版本。", true, error);
        }
    }
    public void Rollback()
    {
        foreach (var (relative, existed) in touched.AsEnumerable().Reverse())
        {
            var target = SafeUpdatePath.InRoot(install, relative); SafeUpdatePath.NoReparsePoints(target);
            if (existed)
            {
                var source = SafeUpdatePath.InRoot(backup, relative); SafeUpdatePath.NoReparsePoints(source);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, true);
            }
            else if (File.Exists(target)) File.Delete(target);
        }
        touched.Clear();
    }
}

public static class UpdateInstaller
{
    public static async Task<InstallPlan> PrepareAsync(UpdateManifest manifest, string zip, string dataDirectory,
        string installDirectory, bool resumeRouting, CancellationToken token = default)
    {
        var current = ProductIdentity.Read(Path.Combine(installDirectory, "NICRoute.dll"));
        if (manifest.VersionCode <= current.VersionCode) throw new InvalidDataException("不能安装旧版本或当前版本。");
        var session = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(dataDirectory, "updates", session, "stage");
        await new PackageVerifier().ExtractAsync(zip, stage, manifest, token);
        AuthenticodeContinuity.Verify(Path.Combine(installDirectory, "NICRoute.exe"), Path.Combine(stage, "NICRoute.exe"));
        using var parent = Process.GetCurrentProcess();
        var plan = new InstallPlan { SessionId=session, DataDirectory=Path.GetFullPath(dataDirectory),
            InstallDirectory=Path.GetFullPath(installDirectory), StageDirectory=stage, ZipPath=Path.GetFullPath(zip),
            Manifest=manifest, CurrentVersionCode=current.VersionCode, ParentProcessId=parent.Id,
            ParentStartedUtcTicks=parent.StartTime.ToUniversalTime().Ticks, ResumeRouting=resumeRouting };
        plan.Validate();
        await File.WriteAllBytesAsync(Path.Combine(plan.SessionDirectory, "install-plan.json"), UpdateJson.Bytes(plan), token);
        return plan;
    }
    public static void Launch(InstallPlan plan)
    {
        plan.Validate();
        var start = new ProcessStartInfo(Path.Combine(plan.StageDirectory, "NICRoute.Updater.exe"))
        { UseShellExecute=true, WorkingDirectory=plan.StageDirectory, WindowStyle=ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--plan"); start.ArgumentList.Add(Path.Combine(plan.SessionDirectory, "install-plan.json"));
        try
        {
            var probe = Path.Combine(plan.InstallDirectory, ".nicroute-write-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { } File.Delete(probe);
        }
        catch (UnauthorizedAccessException) { start.Verb="runas"; }
        _ = Process.Start(start) ?? throw new IOException("无法启动更新安装程序。");
    }
}
