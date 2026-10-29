using NICRoute.Core.Updates;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--plan" || args.Length==3&&args[2]!="--quiet") return 2;
        var quiet=args.Length==3;
        InstallPlan? plan = null; FileUpdateTransaction? transaction = null;
        var mayRestart = false;
        try
        {
            plan = UpdateJson.Read<InstallPlan>(await File.ReadAllBytesAsync(args[1])); plan.Validate();
            if (!Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(plan.StageDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新程序不在已验证的暂存目录中。");
            var current = ProductIdentity.Read(Path.Combine(plan.InstallDirectory, "NICRoute.dll"));
            if (current.VersionCode != plan.CurrentVersionCode) throw new InvalidDataException("已安装版本发生变化，更新已取消。");
            await WaitForParentAsync(plan);
            mayRestart = true;
            var payload = Path.Combine(plan.SessionDirectory, "verified-" + Guid.NewGuid().ToString("N"));
            var index = await new PackageVerifier().ExtractAsync(plan.ZipPath, payload, plan.Manifest);
            AuthenticodeContinuity.Verify(Path.Combine(plan.InstallDirectory,"NICRoute.exe"), Path.Combine(payload,"NICRoute.exe"));
            transaction = new(payload, plan.InstallDirectory, Path.Combine(plan.SessionDirectory, "backup"), index);
            mayRestart = true;
            await transaction.ApplyAsync();
            mayRestart = false;
            Process? child=null;
            try
            {
                child=Restart(plan,true);
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
                while (!File.Exists(plan.ReadyPath))
                {
                    if (child.HasExited) throw new IOException("新版本未能正常启动。");
                    await Task.Delay(200, timeout.Token);
                }
            }
            catch
            {
                if (child is {HasExited:false}) { child.Kill(true); await child.WaitForExitAsync(); }
                transaction.Rollback(); mayRestart = true; throw;
            }
            finally { child?.Dispose(); }
            await File.WriteAllTextAsync(Path.Combine(plan.SessionDirectory,"result.txt"), "更新完成。备份保留在同一会话目录。" );
            return 0;
        }
        catch (Exception error)
        {
            if (error is UpdateInstallException { RolledBack:true }) mayRestart=true;
            if (error is UpdateInstallException { RolledBack:false }) mayRestart=false;
            if (plan is not null)
            {
                try { Directory.CreateDirectory(plan.SessionDirectory); await File.WriteAllTextAsync(Path.Combine(plan.SessionDirectory,"result.txt"), error.ToString()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { }
                if (mayRestart) try { using var child=Restart(plan,false); } catch (Exception e) when(e is IOException or System.ComponentModel.Win32Exception) { }
            }
            if(!quiet) MessageBox(IntPtr.Zero, error.Message, "NICRoute 更新未完成", 0x10);
            return 1;
        }
    }
    private static async Task WaitForParentAsync(InstallPlan plan)
    {
        Process parent;
        try { parent=Process.GetProcessById(plan.ParentProcessId); }
        catch (ArgumentException) { return; }
        using(parent)
        {
            try { if(parent.HasExited||parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedUtcTicks) return; }
            catch(InvalidOperationException) { return; }
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await parent.WaitForExitAsync(timeout.Token); }
            catch(OperationCanceledException) { throw new IOException("NICRoute 尚未退出，未替换任何文件。请退出后重新更新。"); }
        }
    }
    private static Process Restart(InstallPlan plan, bool updated)
    {
        var start=new ProcessStartInfo(Path.Combine(plan.InstallDirectory,"NICRoute.exe"))
        { UseShellExecute=false, WorkingDirectory=plan.InstallDirectory };
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(plan.DataDirectory);
        if(updated) { start.ArgumentList.Add("--update-session"); start.ArgumentList.Add(plan.SessionId); }
        if(plan.ResumeRouting) start.ArgumentList.Add("--start");
        return Process.Start(start) ?? throw new IOException("无法重启 NICRoute。");
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int MessageBox(IntPtr window,string text,string caption,uint type);
}
