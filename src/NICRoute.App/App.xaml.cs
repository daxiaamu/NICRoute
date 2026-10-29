using System.Security.Principal;
using System.Windows.Media.Imaging;

namespace NICRoute.App;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private bool ownsMutex;
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smokeIndex = Array.IndexOf(e.Args, "--smoke-engine");
        if (smokeIndex >= 0 && smokeIndex + 1 < e.Args.Length)
        {
            var reportPath = Path.GetFullPath(e.Args[smokeIndex + 1]);
            var report = new System.Text.StringBuilder();
            var work = Path.Combine(Path.GetDirectoryName(reportPath)!, "engine-smoke-runtime");
            try
            {
                await using var controller = new NICRoute.Core.EngineController(Path.Combine(AppContext.BaseDirectory, "engine", "sing-box.exe"), work);
                controller.Log += line => report.AppendLine(line);
                await controller.StartAsync(new(), NICRoute.Core.AdapterCatalog.GetAdapters(), useTun: false);
                await controller.StopAsync();
                report.AppendLine("PASS: GUI-hosted lifecycle; no TUN or route changes");
                File.WriteAllText(reportPath, report.ToString());
                Shutdown(0);
            }
            catch (Exception error) { File.WriteAllText(reportPath, report + "\n" + error); Shutdown(1); }
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            if (e.Args.Contains("--preview"))
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preview-error.txt"), args.Exception.ToString());
                args.Handled = true;
                Shutdown(1);
                return;
            }
            System.Windows.MessageBox.Show(args.Exception.Message, "NICRoute", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var previewIndex = Array.IndexOf(e.Args, "--preview");
        if (previewIndex < 0)
        {
            var name = @"Local\NICRoute-" + WindowsIdentity.GetCurrent().User?.Value;
            instance = new Mutex(false, name);
            try { ownsMutex = instance.WaitOne(e.Args.Contains("--takeover") ? 15000 : 0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
            {
                System.Windows.MessageBox.Show("NICRoute 已在运行。请从任务栏通知区域打开。", "NICRoute");
                Shutdown(); return;
            }
        }
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        var dataDirectory = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1]) : null;
        var window = new MainWindow(previewIndex >= 0, dataDirectory);
        if(previewIndex>=0&&e.Args.Contains("--small-window")) { window.Width=1030; window.Height=760; }
        MainWindow = window;
        window.Show();
        if(previewIndex>=0&&e.Args.Contains("--updates-tab")) window.UpdatesTab.IsSelected=true;
        var updateIndex=Array.IndexOf(e.Args,"--update-session");
        if(updateIndex>=0&&updateIndex+1<e.Args.Length&&Guid.TryParseExact(e.Args[updateIndex+1],"N",out var session))
        {
            var sessionDirectory=Path.Combine(window.DataDirectory,"updates",session.ToString("N"));
            if(Directory.Exists(sessionDirectory)) File.WriteAllText(Path.Combine(sessionDirectory,"app-ready"),"ready");
        }
        if (previewIndex >= 0 && previewIndex + 1 < e.Args.Length)
        {
            await window.PrimeTrafficPreviewAsync();
            if(e.Args.Contains("--traffic-tab")) window.TrafficTab.IsSelected=true;
            if(e.Args.Contains("--verify-rules"))
                File.WriteAllText(e.Args[previewIndex + 1] + ".checks.txt", window.VerifyRuleManagementPreview());
            if(e.Args.Contains("--verify-traffic"))
                File.AppendAllText(e.Args[previewIndex + 1] + ".checks.txt", Environment.NewLine + window.VerifyTrafficPreview());
            if(e.Args.Contains("--app-traffic-tab")) window.PreviewApplicationTraffic();
            Window rendered = window;
            if(e.Args.Contains("--update-dialog"))
            {
                var sample=new NICRoute.Core.Updates.UpdateManifest{Channel="stable",PolicyRevision=3,VersionCode=3,VersionName="0.2.1",Size=110000000,
                    Sha256=new string('0',64),PublishedAt=DateTimeOffset.UtcNow.ToString("O"),
                    Changelog="> 更新界面预览，不检查或下载真实更新。\n\n## 更新内容\n\n- 使用 **MUDL** 多镜像并发下载，最多 32 个连接。\n- 保留网卡规则，安装前重新验证 SHA256。\n- 更新失败时恢复原版本。\n\n下载完成后将停止分流并重启软件。",
                    ReleaseUrl="https://github.com/daxiaamu/NICRoute/releases/tag/v0.2.1",Urls=["https://github.com/daxiaamu/NICRoute/releases/download/v0.2.1/NICRoute-win-x64.zip"]};
                rendered=new UpdateDialog(sample,false,(_,_)=>Task.FromException<string>(new InvalidOperationException("预览不下载")),(_,_)=>Task.CompletedTask,CancellationToken.None){Owner=window};
                rendered.Show();
            }
            else if (e.Args.Contains("--editor"))
            {
                rendered = new RuleEditor(NICRoute.Core.AdapterCatalog.GetAdapters()) { Owner = window };
                rendered.Show();
            }
            await Task.Delay(e.Args.Contains("--editor") ? 4000 : 800);
            rendered.UpdateLayout();
            if(e.Args.Contains("--verify-applications"))
                File.AppendAllText(e.Args[previewIndex + 1] + ".checks.txt", Environment.NewLine + window.VerifyApplicationTrafficPreview());
            // Window.Render omits the non-client chrome. Use client dimensions to avoid
            // transparent bands from the title bar/borders in the exported preview.
            var client = (FrameworkElement)rendered.Content;
            var width = (int)(client.ActualWidth + client.Margin.Left + client.Margin.Right);
            var height = (int)(client.ActualHeight + client.Margin.Top + client.Margin.Bottom);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(rendered);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(e.Args[previewIndex + 1])) encoder.Save(file);
            if (rendered != window) rendered.Close();
            window.Close();
        }
        else if (e.Args.Contains("--start")) await window.StartRoutingAsync();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
