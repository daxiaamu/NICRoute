using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NICRoute.Core;

public sealed class EngineController : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly string enginePath;
    private readonly string workDirectory;
    private Process? process;
    private int enginePid;
    public void RefreshObservedInterfaces(IReadOnlyList<AdapterInfo> adapters)
    {
        var started = Stopwatch.GetTimestamp();
        ApplicationTraffic.UpdateObservedTcp(ObservedTcpInterfaces.Capture(Volatile.Read(ref enginePid), adapters), started);
    }
    private EngineProcess? ownedProcess;
    private RejectEndpoint? reject;
    private HttpClient? api;
    private ApplicationConnectionFeed? applicationFeed;
    private ApplicationNetworkGuard? networkGuard;
    public bool HasNetworkGuard => networkGuard is not null;
    public ApplicationTrafficTracker ApplicationTraffic { get; } = new();
    private bool stopping;
    private TaskCompletionSource? ready;
    private Dictionary<string, string> selections = [];
    private readonly Queue<string> recent = new();
    public event Action<string>? Log;
    public event Action? UnexpectedExit;
    public bool IsRunning => process is { HasExited: false };
    public bool IsReady { get; private set; }
    public string? GetSelectedOutbound(string selector)
    { lock (selections) return selections.GetValueOrDefault(selector); }

    public EngineController(string enginePath, string workDirectory)
    { this.enginePath = enginePath; this.workDirectory = workDirectory; }

    public async Task StartAsync(AppSettings settings, IReadOnlyList<AdapterInfo> adapters, bool useTun = true, int testPort = 0)
    {
        await gate.WaitAsync();
        try
        {
            if (IsRunning) throw new InvalidOperationException("分流引擎已启动。");
            if (!File.Exists(enginePath)) throw new FileNotFoundException("缺少分流引擎，请运行 scripts/Get-Engine.ps1 后重新打包。", enginePath);
            var lockPath = Path.Combine(Path.GetDirectoryName(enginePath)!, "engine.lock.json");
            if (!File.Exists(lockPath)) throw new InvalidDataException("缺少引擎校验文件，请重新获取完整安装包。");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(lockPath))!;
            using (var stream = File.OpenRead(enginePath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                if (!string.Equals(actual, manifest["executable_sha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("引擎 SHA256 校验失败，请重新获取完整安装包。");
            }
            Directory.CreateDirectory(workDirectory);
            stopping = false;
            IsReady = false;
            recent.Clear();
            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            reject = new();
            reject.Start();
            var port = GetFreePort();
            var monitorPort = GetFreePort();
            while (monitorPort == port) monitorPort = GetFreePort();
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var configPath = Path.Combine(workDirectory, "engine.json");
            await File.WriteAllTextAsync(configPath, ConfigBuilder.Serialize(ConfigBuilder.Build(settings, adapters, port, secret, reject.Port, useTun, testPort, monitorPort)));
            await CheckAsync(configPath);
            if (useTun)
            {
                if (settings.Rules.Any(r => r.Enabled && r.Application.Kind == ApplicationKind.Desktop &&
                    r.Application.ExecutablePath.Equals(enginePath, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("不能为分流引擎自身添加应用规则。");
                var targets = ApplicationNetworkGuard.Targets(settings);
                var guard = targets.Length > 0 ? new ApplicationNetworkGuard(targets) : null;
                networkGuard?.Dispose(); networkGuard = guard;
            }
            ownedProcess = new();
            ownedProcess.Start(enginePath, configPath, workDirectory, OnOutput);
            process = ownedProcess.Process;
            Volatile.Write(ref enginePid, process.Id);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                IsReady = false;
                Volatile.Write(ref enginePid, 0); ApplicationTraffic.Stop();
                ready?.TrySetException(new InvalidOperationException("引擎退出。请检查运行日志。"));
                if (!stopping) UnexpectedExit?.Invoke();
            };
            api = new(new SocketsHttpHandler { UseProxy = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(3) };
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var response = await api.GetAsync("version");
            response.EnsureSuccessStatusCode();
            networkGuard?.AllowInterface(ConfigBuilder.TunName);
            if (networkGuard is not null) Log?.Invoke("应用网卡约束已启用：受控应用不能绕过 NICRoute 直接使用其他网卡；不依赖联网探测。");
            selections = RuleResolver.Selections(settings, adapters);
            ApplicationTraffic.Configure(settings, adapters); ApplicationTraffic.Begin();
            applicationFeed = new(monitorPort, secret, ApplicationTraffic.Receive, ApplicationTraffic.SetOnline);
            try { await applicationFeed.Ready.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { Log?.Invoke("应用流量监控尚未连接，将自动重试；分流继续运行。"); }
            IsReady = true;
            foreach (var adapter in adapters) Log?.Invoke($"网卡出口：{adapter.Tag} → {adapter.Name}（{adapter.Status}）");
            Log?.Invoke("分流已启动。TCP / UDP 连接按应用规则选择网卡。");
        }
        catch (Exception e)
        {
            string details;
            lock (recent) details = string.Join(Environment.NewLine, recent.TakeLast(6));
            await StopCoreAsync();
            throw new InvalidOperationException(e.Message + (details.Length > 0 ? "\n" + details : ""), e);
        }
        finally { gate.Release(); }
    }

    private void OnOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var clean = Regex.Replace(line, @"\x1B\[[0-9;]*[a-zA-Z]", "");
        lock (recent) { recent.Enqueue(clean); while (recent.Count > 15) recent.Dequeue(); }
        Log?.Invoke(clean);
        if (clean.Contains("sing-box started", StringComparison.OrdinalIgnoreCase)) ready?.TrySetResult();
    }

    public async Task UpdateSelectionsAsync(AppSettings settings, IReadOnlyList<AdapterInfo> adapters)
    {
        await gate.WaitAsync();
        try
        {
            if (!IsReady || api is null) return;
            foreach (var (tag, target) in RuleResolver.Selections(settings, adapters))
            {
                if (GetSelectedOutbound(tag) == target) continue;
                try
                {
                    using var response = await api.PutAsJsonAsync("proxies/" + Uri.EscapeDataString(tag), new { name = target });
                    response.EnsureSuccessStatusCode();
                    var actual = await api.GetFromJsonAsync<JsonObject>("proxies/" + Uri.EscapeDataString(tag));
                    if (actual?["now"]?.GetValue<string>() != target) throw new IOException("引擎没有确认指定出口。");
                }
                catch (Exception error) when (target != RuleResolver.BlockTag && error is HttpRequestException or IOException or System.Text.Json.JsonException)
                {
                    using var denied = await api.PutAsJsonAsync("proxies/" + Uri.EscapeDataString(tag), new { name = RuleResolver.BlockTag });
                    denied.EnsureSuccessStatusCode();
                    lock (selections) selections[tag] = RuleResolver.BlockTag;
                    throw new IOException("指定网卡出口未能生效，已将该规则阻断。请保存并重新应用。", error);
                }
                lock (selections) selections[tag] = target;
                Log?.Invoke($"出口切换：{tag} → {target}（原有连接关闭，应用可重连）");
            }
        }
        finally { gate.Release(); }
    }

    private ProcessStartInfo StartInfo(params string[] args)
    {
        var start = new ProcessStartInfo(enginePath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workDirectory };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }
    private async Task CheckAsync(string configPath)
    {
        using var check = Process.Start(StartInfo("check", "-c", configPath))!;
        var output = check.StandardOutput.ReadToEndAsync();
        var error = check.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await check.WaitForExitAsync(timeout.Token); }
        catch { check.Kill(true); throw; }
        if (check.ExitCode != 0) throw new InvalidDataException("引擎配置检查失败：\n" + await output + await error);
    }
    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { gate.Release(); }
    }
    private async Task StopCoreAsync()
    {
        stopping = true;
        IsReady = false;
        Volatile.Write(ref enginePid, 0);
        if (applicationFeed is not null) { await applicationFeed.DisposeAsync(); applicationFeed = null; }
        ApplicationTraffic.Stop();
        if (ownedProcess is not null)
        {
            await ownedProcess.DisposeAsync();
            Log?.Invoke(ownedProcess.GracefulExit ? "引擎已正常退出并清理虚拟网卡。" : "引擎已退出。必要时请检查 Windows 网络状态。");
            ownedProcess = null;
            process = null;
        }
        networkGuard?.Dispose(); networkGuard = null;
        api?.Dispose(); api = null;
        if (reject is not null) { await reject.DisposeAsync(); reject = null; }
        // Contains an ephemeral localhost API secret. Do not leave it in a diagnostics export.
        var config = Path.Combine(workDirectory, "engine.json");
        if (File.Exists(config)) File.Delete(config);
    }
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    public async ValueTask DisposeAsync() { await StopAsync(); gate.Dispose(); }
}
