using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NICRoute.Core;

public sealed record ApplicationTraffic(string Key, string Name, string Detail, bool Configured,
    double? UploadRate, double? DownloadRate, long Uploaded, long Downloaded, int Connections, string ActualRoute, string Status);

public sealed class ApplicationTrafficTracker
{
    private sealed class Totals(string key, string name, string detail)
    {
        public string Key = key, Name = name, Detail = detail;
        public bool Configured;
        public long Up, Down, SampleUp, SampleDown;
        public string LastRoute = "";
    }
    private sealed class Connection(Totals app, ObservedConnection metadata)
    {
        public Totals App = app;
        public ObservedConnection Metadata = metadata;
        public long Up, Down;
        public bool Active;
        public bool Retired;
        public long FirstSeen = Stopwatch.GetTimestamp();
        public string ObservedInterface = "";
    }
    private readonly object sync = new();
    private readonly Dictionary<string, Totals> apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private readonly Queue<string> closed = new();
    private readonly Dictionary<string, Totals> processCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> interfaceNames = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string[]> observedTcp = new Dictionary<string, string[]>();
    private long observedAt;
    public void UpdateObservedTcp(IReadOnlyDictionary<string, string[]> value, long timestamp = 0)
    { lock (sync) { observedTcp = value; observedAt = timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp; } }
    private ApplicationTarget[] configured = [];
    private bool running, online;
    private long lastSample;
    private static readonly Regex PackagePath = new(@"(?i)^.+\\WindowsApps\\([^\\]+)_[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+_[^\\_]+_[^\\_]*_([^\\]+)\\", RegexOptions.NonBacktracking);
    public void Configure(AppSettings settings, IReadOnlyList<AdapterInfo> adapters)
    {
        lock (sync)
        {
            configured = SettingsStore.Clone(settings).Rules.Select(r => r.Application).ToArray();
            processCache.Clear();
            foreach (var app in apps.Values) app.Configured = false;
            foreach (var target in configured)
            {
                var totals = GetApp(target.Identity, target.Name, target.Detail); totals.Configured = true;
                totals.Name = target.Name; totals.Detail = target.Detail;
            }
            foreach (var adapter in adapters) interfaceNames[adapter.Tag] = adapter.Name;
        }
    }
    public void Begin()
    {
        lock (sync) { running = true; online = false; connections.Clear(); closed.Clear(); lastSample = 0; observedTcp = new Dictionary<string, string[]>(); observedAt = 0; }
    }
    public void Stop()
    {
        lock (sync) { running = online = false; foreach (var connection in connections.Values) connection.Active = false; }
    }
    public void SetOnline(bool value)
    {
        lock (sync)
        {
            online = running && value;
            if (!online) foreach (var connection in connections.Values) connection.Active = false;
        }
    }
    public void Receive(ObservedConnectionBatch batch)
    {
        lock (sync)
        {
            if (!running) return;
            if (batch.Reset) foreach (var connection in connections.Values) connection.Active = false;
            foreach (var item in batch.Events)
            {
                connections.TryGetValue(item.Id, out var connection);
                if (item.Connection is { } metadata)
                {
                    if (connection is null)
                    {
                        if (connections.Count >= 60000) throw new InvalidDataException("应用监控连接数过多。");
                        connection = new(Identify(metadata), metadata); connections.Add(item.Id, connection);
                    }
                    connection.Metadata = metadata;
                    Account(connection, metadata.Uploaded, metadata.Downloaded);
                    connection.Active = item.Kind != 2 && !metadata.Closed;
                }
                else if (connection is not null && item.Kind == 1)
                    Account(connection, Add(connection.Up, item.UploadDelta), Add(connection.Down, item.DownloadDelta));
                if (connection is null) continue;
                if (item.Kind == 2 || connection.Metadata.Closed)
                {
                    connection.Active = false;
                }
                var route = Route(connection.Metadata);
                if (route != "出口待确认") connection.App.LastRoute = route;
            }
            foreach (var connection in connections.Values.Where(c => !c.Active && !c.Retired))
            { connection.Retired = true; closed.Enqueue(connection.Metadata.Id); }
            // Each ID enters the bounded replay cache only once, including reconnect replays.
            while (closed.Count > 4096)
            { var old = closed.Dequeue(); if (connections.TryGetValue(old, out var value) && !value.Active) connections.Remove(old); }
        }
    }
    private static void Account(Connection connection, long up, long down)
    {
        connection.App.Up = up >= connection.Up ? Add(connection.App.Up, up - connection.Up) : Math.Max(0, connection.App.Up - (connection.Up - up));
        connection.App.Down = down >= connection.Down ? Add(connection.App.Down, down - connection.Down) : Math.Max(0, connection.App.Down - (connection.Down - down));
        connection.Up = up; connection.Down = down;
    }
    private static long Add(long value, long delta) => delta < 0 ? value : value > long.MaxValue - delta ? long.MaxValue : value + delta;
    private Totals GetApp(string key, string name, string detail)
    {
        if (apps.TryGetValue(key, out var value)) return value;
        if (apps.Count >= 4096) { key = "overflow"; name = "其他应用（监控容量已满）"; detail = "超过4096个应用后合并统计"; }
        if (!apps.TryGetValue(key, out value)) apps.Add(key, value = new(key, name, detail));
        return value;
    }
    private Totals Identify(ObservedConnection connection)
    {
        var path = connection.ProcessPath;
        if (path.Length > 0 && processCache.TryGetValue(path, out var cached)) return cached;
        var identified = IdentifyPath(connection);
        if (path.Length > 0 && processCache.Count < 4096) processCache[path] = identified;
        return identified;
    }
    private Totals IdentifyPath(ObservedConnection connection)
    {
        var path = connection.ProcessPath;
        foreach (var target in configured)
        {
            var matches = target.Kind == ApplicationKind.Desktop ? path.Equals(target.ExecutablePath, StringComparison.OrdinalIgnoreCase) :
                path.Length > 0 && Regex.IsMatch(path, target.PackagePathPattern(), RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
            if (matches) return GetApp(target.Identity, target.Name, target.Detail);
        }
        var package = PackagePath.Match(path);
        if (package.Success)
        {
            var family = package.Groups[1].Value + "_" + package.Groups[2].Value;
            return GetApp("package:" + family, package.Groups[1].Value, family);
        }
        if (path.Length > 0)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return GetApp("exe:" + path, name.Length > 0 ? name : path, path);
        }
        return GetApp("unidentified:" + connection.ProcessId, "未识别进程" + (connection.ProcessId > 0 ? " · PID " + connection.ProcessId : ""),
            "引擎没有提供进程路径，无法归属到具体应用");
    }
    private string Route(ObservedConnection connection)
    {
        foreach (var tag in connection.Chain.Prepend(connection.Outbound))
        {
            if (interfaceNames.TryGetValue(tag, out var name)) return name;
            if (tag == RuleResolver.BlockTag) return "不联网";
            if (tag == RuleResolver.SystemTag)
            {
                if (connections.TryGetValue(connection.Id, out var tracked) && connection.Network == "tcp")
                {
                    if (tracked.ObservedInterface.Length > 0) return tracked.ObservedInterface + "（自动）";
                    if (observedAt >= tracked.FirstSeen && observedTcp.TryGetValue(ObservedTcpInterfaces.Endpoint(connection.Destination), out var names) && names.Length == 1)
                    { tracked.ObservedInterface = names[0]; return names[0] + "（自动）"; }
                }
                return "Windows 自动（网卡待确认）";
            }
        }
        return "出口待确认";
    }
    public IReadOnlyList<ApplicationTraffic> Snapshot(long timestamp = 0)
    {
        lock (sync)
        {
            if (timestamp == 0) timestamp = Stopwatch.GetTimestamp();
            var seconds = lastSample == 0 || timestamp <= lastSample ? 0 : Stopwatch.GetElapsedTime(lastSample, timestamp).TotalSeconds;
            var active = connections.Values.Where(c => c.Active).ToLookup(c => c.App.Key, StringComparer.OrdinalIgnoreCase);
            var results = apps.Values.Select(app =>
            {
                var current = active[app.Key].ToArray();
                var routes = current.Select(c => Route(c.Metadata)).Distinct().Order().ToArray();
                double? up = running && online && seconds is > 0 and <= 10 ? Math.Max(0, app.Up - app.SampleUp) / seconds : null;
                double? down = running && online && seconds is > 0 and <= 10 ? Math.Max(0, app.Down - app.SampleDown) / seconds : null;
                app.SampleUp = app.Up; app.SampleDown = app.Down;
                var route = routes.Length > 0 ? string.Join("、", routes) : app.LastRoute.Length > 0 ? "最近：" + app.LastRoute : "—";
                return new ApplicationTraffic(app.Key, app.Name, app.Detail, app.Configured, up, down, app.Up, app.Down,
                    current.Length, route, !running ? "未启动分流" : !online ? "监控重连中" : current.Length == 0 ? "无活动连接" : current.Length + " 个连接");
            }).OrderByDescending(a => a.Configured).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            lastSample = timestamp; return results;
        }
    }
}
