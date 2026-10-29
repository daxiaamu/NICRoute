using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NICRoute.Core;

public static class WindowsAppCatalog
{
    // Executed as the current user, even when elevated under the same account. No package files
    // are opened and no ownership/ACLs on WindowsApps are modified.
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $titles = @{}
        Get-StartApps | ForEach-Object {
            if ($_.AppID -like '*!*') {
                $family = $_.AppID.Split('!')[0]
                if (-not $titles.ContainsKey($family)) { $titles[$family] = $_.Name }
            }
        }
        $items = @(Get-AppxPackage | Where-Object {
            -not $_.IsFramework -and -not $_.IsResourcePackage -and $_.InstallLocation -and $titles.ContainsKey($_.PackageFamilyName)
        } | ForEach-Object {
            [pscustomobject]@{
                Name = $titles[$_.PackageFamilyName]
                PackageFamilyName = $_.PackageFamilyName
                PackageName = $_.Name
                PublisherId = $_.PublisherId
                InstallLocation = $_.InstallLocation
            }
        })
        ConvertTo-Json -InputObject $items -Depth 3 -Compress
        """;

    public static async Task<IReadOnlyList<ApplicationTarget>> GetAppsAsync(CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法读取 Windows 应用列表。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows 应用列表读取失败：" + error.Trim());
        var items = JsonSerializer.Deserialize<List<ApplicationTarget>>(output, SettingsStore.JsonOptions) ?? [];
        foreach (var app in items) app.Kind = ApplicationKind.WindowsPackage;
        return items.DistinctBy(a => a.PackageFamilyName).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static bool RefreshTargets(AppSettings settings, IReadOnlyList<ApplicationTarget> installed)
    {
        var changed = false;
        foreach (var rule in settings.Rules.Where(r => r.Application.Kind == ApplicationKind.WindowsPackage))
        {
            var current = installed.FirstOrDefault(a => a.PackageFamilyName == rule.Application.PackageFamilyName);
            if (current is null || current.InstallLocation == rule.Application.InstallLocation) continue;
            rule.Application = current;
            changed = true;
        }
        return changed;
    }
}
