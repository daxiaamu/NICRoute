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
        MainWindow = window;
        window.Show();
        if (previewIndex >= 0 && previewIndex + 1 < e.Args.Length)
        {
            Window rendered = window;
            if (e.Args.Contains("--editor"))
            {
                rendered = new RuleEditor(NICRoute.Core.AdapterCatalog.GetAdapters()) { Owner = window };
                rendered.Show();
            }
            await Task.Delay(e.Args.Contains("--editor") ? 4000 : 800);
            rendered.UpdateLayout();
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
