using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NICRoute.Core;

public sealed record ProcessSnapshotEntry(int ProcessId, int ParentProcessId, string ExecutablePath,
    DateTimeOffset StartedAt, string CompanyName = "", string ProductName = "");

public sealed record ResolvedApplicationProcess(string ApplicationIdentity, string ExecutablePath, bool IsBlocked = false);

public sealed record ApplicationProcessResolution(IReadOnlyList<ResolvedApplicationProcess> Processes)
{
    public IReadOnlyList<string> PathsFor(ApplicationTarget application) => Processes
        .Where(process => !process.IsBlocked && process.ApplicationIdentity.Equals(application.Identity, StringComparison.OrdinalIgnoreCase))
        .Select(process => process.ExecutablePath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public IReadOnlyList<string> BlockedPathsFor(ApplicationTarget application) => Processes
        .Where(process => process.IsBlocked && process.ApplicationIdentity.Equals(application.Identity, StringComparison.OrdinalIgnoreCase))
        .Select(process => process.ExecutablePath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

/// <summary>Resolves every executable that belongs to a configured desktop application.
/// Direct ancestry is authoritative. Orphaned account clients are included only when both
/// their path and file identity evidence are anchored to the configured application.</summary>
public static class ApplicationProcessResolver
{
    private const int MaximumAncestryDepth = 64;
    private static readonly ConcurrentDictionary<string, (string Company, string Product)> MetadataCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "administrator", "app", "appdata", "application", "apps", "bin", "client", "clients", "codec",
        "common", "data", "download", "downloads", "drive", "exe", "files", "helper", "host", "local",
        "network", "program", "programs", "render", "resources", "server", "system", "temp", "update",
        "updater", "user", "users", "windowsapps", "x64", "x86"
    };

    public static ApplicationProcessResolution Resolve(AppSettings settings, IEnumerable<ProcessSnapshotEntry> snapshot) =>
        Resolve(settings, snapshot, []);

    public static ApplicationProcessResolution Resolve(AppSettings settings, IEnumerable<ProcessSnapshotEntry> snapshot,
        IEnumerable<ProcessSnapshotEntry> observed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot);
        var processes = snapshot
            .Where(process => process.ProcessId > 0 && !string.IsNullOrWhiteSpace(process.ExecutablePath))
            .Concat(observed.Where(process => process.ProcessId > 0 && !string.IsNullOrWhiteSpace(process.ExecutablePath)))
            .GroupBy(process => process.ProcessId).Select(group => group.First())
            .ToArray();
        var byPid = processes.ToDictionary(process => process.ProcessId);
        var exactPaths = settings.Rules.Select(rule => NormalizePath(rule.Application.ExecutablePath))
            .Where(path => path.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        void Add(ApplicationTarget application, string path)
        {
            path = NormalizePath(path);
            if (path.Length == 0) return;
            if (!candidates.TryGetValue(path, out var owners)) candidates[path] = owners = new(StringComparer.OrdinalIgnoreCase);
            owners.Add(application.Identity);
        }

        foreach (var rule in settings.Rules.Where(rule => rule.Enabled && rule.Application.Kind == ApplicationKind.Desktop))
        {
            var application = rule.Application;
            Add(application, application.ExecutablePath); // The root remains routable before it launches.
            var rootProcesses = processes.Where(process =>
                PathsEqual(process.ExecutablePath, application.ExecutablePath)).ToArray();

            foreach (var process in processes)
            {
                if (PathsEqual(process.ExecutablePath, application.ExecutablePath)) continue;
                if (rootProcesses.Any(root => IsDescendant(process, root, byPid)))
                    Add(application, process.ExecutablePath);
            }

            foreach (var root in rootProcesses)
            foreach (var process in processes)
            {
                if (PathsEqual(process.ExecutablePath, application.ExecutablePath) ||
                    exactPaths.Contains(NormalizePath(process.ExecutablePath))) continue;
                if (IsFamilyRelated(application, root, process)) Add(application, process.ExecutablePath);
            }
        }

        // A path owned by more than one configured application is ambiguous. Exact configured
        // roots keep their direct ownership; every auxiliary path is excluded from both rules.
        var resolved = candidates.SelectMany(pair =>
        {
            var exactOwner = pair.Value.FirstOrDefault(owner => settings.Rules.Any(rule =>
                rule.Application.Identity.Equals(owner, StringComparison.OrdinalIgnoreCase) &&
                PathsEqual(rule.Application.ExecutablePath, pair.Key)));
            if (exactOwner is not null) return new[] { new ResolvedApplicationProcess(exactOwner, pair.Key) };
            return pair.Value.Count == 1
                ? new[] { new ResolvedApplicationProcess(pair.Value.Single(), pair.Key) }
                : pair.Value.Select(owner => new ResolvedApplicationProcess(owner, pair.Key, true)).ToArray();
        }).ToArray();
        return new(resolved);
    }

    public static bool RoutingChanged(ApplicationProcessResolution previous, ApplicationProcessResolution current) =>
        !RoutingIdentity(previous).SetEquals(RoutingIdentity(current));

    public static ProcessSnapshotEntry[] Capture()
    {
        var entries = new List<ProcessSnapshotEntry>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "读取 Windows 进程树");
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return [];
            do
            {
                if (entry.ProcessId is 0 or 4) continue;
                var process = OpenProcess(0x1000, false, entry.ProcessId);
                if (process == IntPtr.Zero) continue;
                try
                {
                    if (!GetProcessTimes(process, out var created, out _, out _, out _)) continue;
                    var pathBuffer = new StringBuilder(32768);
                    var length = (uint)pathBuffer.Capacity;
                    if (!QueryFullProcessImageNameW(process, 0, pathBuffer, ref length)) continue;
                    var path = NormalizePath(pathBuffer.ToString());
                    if (path.Length == 0) continue;
                    var started = DateTime.FromFileTimeUtc(((long)created.HighDateTime << 32) | created.LowDateTime);
                    var (company, product) = Metadata(path);
                    entries.Add(new(checked((int)entry.ProcessId), checked((int)entry.ParentProcessId), path,
                        new DateTimeOffset(started, TimeSpan.Zero), company, product));
                }
                finally { CloseHandle(process); }
            } while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return entries.ToArray();
    }

    private static bool IsDescendant(ProcessSnapshotEntry process, ProcessSnapshotEntry root,
        IReadOnlyDictionary<int, ProcessSnapshotEntry> processes)
    {
        var current = process;
        for (var depth = 0; depth < MaximumAncestryDepth && current.ParentProcessId != root.ProcessId; depth++)
        {
            if (!processes.TryGetValue(current.ParentProcessId, out var parent)) return false;
            current = parent;
            if (current.StartedAt > process.StartedAt) return false; // PID was reused after this child started.
        }
        return current.ParentProcessId == root.ProcessId && root.StartedAt <= process.StartedAt;
    }

    private static bool IsFamilyRelated(ApplicationTarget application, ProcessSnapshotEntry root, ProcessSnapshotEntry candidate)
    {
        if (candidate.ExecutablePath.Length == 0 ||
            !candidate.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var rootMetadata = Metadata(root.ExecutablePath);
        var rootCompany = Tokens(root.CompanyName.Length > 0 ? root.CompanyName : rootMetadata.Company);
        var rootProduct = Tokens(root.ProductName.Length > 0 ? root.ProductName : rootMetadata.Product);
        var rootPath = DistinctiveTokens(Path.GetFileNameWithoutExtension(application.ExecutablePath));
        rootPath.UnionWith(DistinctiveTokenParts(Path.GetDirectoryName(application.ExecutablePath) ?? ""));
        var rootIdentity = new HashSet<string>(rootCompany, StringComparer.OrdinalIgnoreCase);
        rootIdentity.UnionWith(rootProduct); rootIdentity.UnionWith(rootPath);
        var candidateMetadata = Metadata(candidate.ExecutablePath);
        var candidateCompany = Tokens(candidate.CompanyName.Length > 0 ? candidate.CompanyName : candidateMetadata.Company);
        var candidateProduct = Tokens(candidate.ProductName.Length > 0 ? candidate.ProductName : candidateMetadata.Product);
        var candidatePath = DistinctiveTokens(Path.GetFileNameWithoutExtension(candidate.ExecutablePath));
        candidatePath.UnionWith(DistinctiveTokenParts(Path.GetDirectoryName(candidate.ExecutablePath) ?? ""));
        var candidateIdentity = new HashSet<string>(candidateCompany, StringComparer.OrdinalIgnoreCase);
        candidateIdentity.UnionWith(candidateProduct); candidateIdentity.UnionWith(candidatePath);

        var exactCompany = rootCompany.Count > 0 && rootCompany.SetEquals(candidateCompany);
        var exactProduct = rootProduct.Count > 0 && rootProduct.SetEquals(candidateProduct);
        var metadataAnchor = rootIdentity.Overlaps(candidateCompany) || rootIdentity.Overlaps(candidateProduct);
        var pathAnchor = rootPath.Overlaps(candidatePath);
        return pathAnchor && (exactCompany || exactProduct || metadataAnchor);
    }

    private static HashSet<string> Tokens(string value)
    {
        value = Regex.Replace(value, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return value.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 2 && !GenericTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> DistinctiveTokens(string value) => Tokens(value);

    private static HashSet<string> DistinctiveTokenParts(string path)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            result.UnionWith(DistinctiveTokens(part));
        return result;
    }

    private static HashSet<string> RoutingIdentity(ApplicationProcessResolution resolution) => resolution.Processes
        .Select(process => process.ApplicationIdentity.ToLowerInvariant() + "\0" + NormalizePath(process.ExecutablePath).ToLowerInvariant() +
            (process.IsBlocked ? "\0blocked" : "\0allowed"))
        .ToHashSet(StringComparer.Ordinal);

    private static bool PathsEqual(string left, string right) => NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static (string Company, string Product) Metadata(string path) => MetadataCache.GetOrAdd(NormalizePath(path), candidate =>
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(candidate);
            return (version.CompanyName ?? "", version.ProductName ?? "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return ("", "");
        }
    });

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var trimmed = path.Trim();
        try { return Path.IsPathFullyQualified(trimmed) ? Path.GetFullPath(trimmed).TrimEnd('\\', '/') : trimmed; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return trimmed; }
    }

    private static readonly char[] separators = [' ', '-', '_', '.', '\\', '/', '(', ')', '[', ']', ',', ':', ';'];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId; public IntPtr DefaultHeap, ModuleThread; public uint ParentProcessId;
        public int BasePriority, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint LowDateTime, HighDateTime; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
