using System.Collections.Concurrent;
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
    private readonly Func<ProcessSnapshotEntry[]> captureProcesses;
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
    private IReadOnlyDictionary<string, AdapterSourceBinding> configuredBindings =
        new Dictionary<string, AdapterSourceBinding>();
    private AppSettings runningSettings = new();
    private IReadOnlyList<AdapterInfo> runningAdapters = [];
    private bool runningUseTun = true;
    private int runningTestPort;
    private ApplicationProcessResolution applicationProcesses = new([]);
    private CancellationTokenSource? processWatcher;
    private readonly ConcurrentDictionary<string, ProcessSnapshotEntry> observedApplicationPaths = new(StringComparer.OrdinalIgnoreCase);
    private int applicationRefreshScheduled;
    private readonly Queue<string> recent = new();
    public event Action<string>? Log;
    public event Action? UnexpectedExit;
    public bool IsRunning => process is { HasExited: false };
    public bool IsReady { get; private set; }
    public string? GetSelectedOutbound(string selector)
    { lock (selections) return selections.GetValueOrDefault(selector); }

    public ApplicationProcessResolution ApplicationProcesses => applicationProcesses;

    public EngineController(string enginePath, string workDirectory)
        : this(enginePath, workDirectory, ApplicationProcessResolver.Capture) { }
    public EngineController(string enginePath, string workDirectory, Func<ProcessSnapshotEntry[]> captureProcesses)
    {
        this.enginePath = enginePath; this.workDirectory = workDirectory;
        this.captureProcesses = captureProcesses ?? throw new ArgumentNullException(nameof(captureProcesses));
    }

    public async Task StartAsync(AppSettings settings, IReadOnlyList<AdapterInfo> adapters, bool useTun = true, int testPort = 0)
    {
        await gate.WaitAsync();
        try { observedApplicationPaths.Clear(); await StartCoreAsync(settings, adapters, useTun, testPort); }
        finally { gate.Release(); }
    }
    private async Task StartCoreAsync(AppSettings settings, IReadOnlyList<AdapterInfo> adapters, bool useTun, int testPort)
    {
        try
        {
            if (IsRunning) throw new InvalidOperationException("分流引擎已启动。");
            runningSettings = SettingsStore.Clone(settings); runningAdapters = adapters.ToArray();
            runningUseTun = useTun; runningTestPort = testPort;
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
            applicationProcesses = ApplicationProcessResolver.Resolve(settings, captureProcesses(), observedApplicationPaths.Values);
            var configPath = Path.Combine(workDirectory, "engine.json");
            await File.WriteAllTextAsync(configPath, ConfigBuilder.Serialize(ConfigBuilder.Build(settings, adapters, port, secret, reject.Port, useTun, testPort, monitorPort, applicationProcesses)));
            await CheckAsync(configPath);
            if (useTun)
            {
                if (settings.Rules.Any(r => r.Enabled && r.Application.Kind == ApplicationKind.Desktop &&
                    r.Application.ExecutablePath.Equals(enginePath, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("不能为分流引擎自身添加应用规则。");
                var adapterBindings = adapters.Select(AdapterSourceBinding.Capture).ToArray();
                AdapterSourceBinding.ValidateUnique(adapterBindings);
                var targets = ApplicationNetworkGuard.Targets(settings, applicationProcesses)
                    .Where(target => target.IsPackage || File.Exists(target.Identity)).ToArray();
                var egressBindings = adapterBindings
                    .SelectMany(binding => binding.GuardBindings())
                    .DistinctBy(binding => (binding.LocalAddress.ToLowerInvariant(), binding.InterfaceName))
                    .ToArray();
                var guard = targets.Length > 0 || egressBindings.Length > 0
                    ? new ApplicationNetworkGuard(targets, preserveLoopback: true, enginePath, egressBindings)
                    : null;
                networkGuard?.Dispose(); networkGuard = guard;
                configuredBindings = adapterBindings.ToDictionary(binding => binding.Tag, StringComparer.Ordinal);
            }
            ownedProcess = new();
            ownedProcess.Start(enginePath, configPath, workDirectory, OnOutput);
            process = ownedProcess.Process;
            Volatile.Write(ref enginePid, process.Id);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                IsReady = false;
                processWatcher?.Cancel();
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
            ApplicationTraffic.Configure(settings, adapters, applicationProcesses); ApplicationTraffic.Begin();
            applicationFeed = new(monitorPort, secret, ReceiveApplicationConnections, ApplicationTraffic.SetOnline);
            try { await applicationFeed.Ready.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { Log?.Invoke("应用流量监控尚未连接，将自动重试；分流继续运行。"); }
            IsReady = true;
            StartProcessWatcher();
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
    }

    public async Task<bool> RefreshApplicationTreeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!IsReady || stopping || !runningSettings.Rules.Any(rule => rule.Enabled && rule.Application.Kind == ApplicationKind.Desktop))
                return false;
            var next = MergeApplicationProcesses(applicationProcesses,
                ApplicationProcessResolver.Resolve(runningSettings, captureProcesses(), observedApplicationPaths.Values));
            if (!ApplicationProcessResolver.RoutingChanged(applicationProcesses, next)) return false;
            var added = next.Processes.Select(process => process.ExecutablePath)
                .Except(applicationProcesses.Processes.Select(process => process.ExecutablePath), StringComparer.OrdinalIgnoreCase)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Log?.Invoke("检测到应用辅助进程：" + string.Join("、", added) + "。正在安全刷新应用树路由。");
            await StopCoreAsync();
            await StartCoreAsync(runningSettings, runningAdapters, runningUseTun, runningTestPort);
            return true;
        }
        finally { gate.Release(); }
    }

    private void StartProcessWatcher()
    {
        processWatcher?.Cancel(); processWatcher?.Dispose();
        if (!runningSettings.Rules.Any(rule => rule.Enabled && rule.Application.Kind == ApplicationKind.Desktop)) return;
        processWatcher = new();
        var token = processWatcher.Token;
        _ = Task.Run(() => WatchApplicationProcessesAsync(token));
    }
    private void ReceiveApplicationConnections(ObservedConnectionBatch batch)
    {
        ApplicationTraffic.Receive(batch);
        var added = false;
        foreach (var connection in batch.Events.Select(item => item.Connection).Where(connection => connection is not null))
        {
            var path = connection!.ProcessPath;
            if (path.Length == 0 || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                observedApplicationPaths.Count >= 4096 || observedApplicationPaths.ContainsKey(path)) continue;
            observedApplicationPaths.TryAdd(path, new(checked((int)Math.Min(connection.ProcessId, int.MaxValue)), 0, path,
                DateTimeOffset.UtcNow, "", ""));
            added = true;
        }
        if (added) ScheduleApplicationTreeRefresh();
    }
    private void ScheduleApplicationTreeRefresh()
    {
        if (Interlocked.CompareExchange(ref applicationRefreshScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100);
                await RefreshApplicationTreeAsync();
            }
            catch (ObjectDisposedException) { }
            catch (Exception error) { Log?.Invoke("应用树即时刷新失败，将由周期检查重试：" + error.Message); }
            finally { Volatile.Write(ref applicationRefreshScheduled, 0); }
        });
    }
    private async Task WatchApplicationProcessesAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(750), token); await RefreshApplicationTreeAsync(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) { Log?.Invoke("应用树刷新暂时失败，将继续重试：" + error.Message); }
        }
    }
    private static ApplicationProcessResolution MergeApplicationProcesses(ApplicationProcessResolution previous,
        ApplicationProcessResolution current) => new(previous.Processes.Concat(current.Processes)
        .DistinctBy(process => (process.ApplicationIdentity.ToLowerInvariant(), process.ExecutablePath.ToLowerInvariant())).ToArray());

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
                var selected = AdapterSourceBinding.StabilizeTarget(target, adapters, configuredBindings, out var changedInterface);
                if (changedInterface is not null)
                    Log?.Invoke($"网卡 {changedInterface} 的接口名称或绑定地址已变化，已阻断出口；请保存并重新应用。");
                if (GetSelectedOutbound(tag) == selected) continue;
                try
                {
                    using var response = await api.PutAsJsonAsync("proxies/" + Uri.EscapeDataString(tag), new { name = selected });
                    response.EnsureSuccessStatusCode();
                    var actual = await api.GetFromJsonAsync<JsonObject>("proxies/" + Uri.EscapeDataString(tag));
                    if (actual?["now"]?.GetValue<string>() != selected) throw new IOException("引擎没有确认指定出口。");
                }
                catch (Exception error) when (selected != RuleResolver.BlockTag && error is HttpRequestException or IOException or System.Text.Json.JsonException)
                {
                    using var denied = await api.PutAsJsonAsync("proxies/" + Uri.EscapeDataString(tag), new { name = RuleResolver.BlockTag });
                    denied.EnsureSuccessStatusCode();
                    lock (selections) selections[tag] = RuleResolver.BlockTag;
                    throw new IOException("指定网卡出口未能生效，已将该规则阻断。请保存并重新应用。", error);
                }
                lock (selections) selections[tag] = selected;
                Log?.Invoke($"出口切换：{tag} → {selected}（原有连接关闭，应用可重连）");
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
        processWatcher?.Cancel(); processWatcher?.Dispose(); processWatcher = null;
        IsReady = false;
        configuredBindings = new Dictionary<string, AdapterSourceBinding>();
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
