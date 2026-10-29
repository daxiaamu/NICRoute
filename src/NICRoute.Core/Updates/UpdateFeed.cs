using System.Text.Json;

namespace NICRoute.Core.Updates;

public sealed record MetadataSource(string Name, Func<string, Uri> ForPath, bool Official = false);

public sealed class UpdateFeed
{
    private readonly IUpdateTransport transport;
    private readonly UpdatePreferences preferences;
    private readonly Action<UpdatePreferences> save;
    private readonly Func<DateTimeOffset> clock;
    private readonly IReadOnlyList<MetadataSource> sources;
    public event Action<string>? Diagnostic;
    public static IReadOnlyList<MetadataSource> DefaultSources { get; } =
    [
        new("GitHub API", p => new($"https://api.github.com/repos/daxiaamu/NICRoute/contents/{p}?ref=main"), true),
        new("GitHub Raw", p => new($"https://raw.githubusercontent.com/daxiaamu/NICRoute/main/{p}"), true),
        new("jsDelivr", p => new($"https://cdn.jsdelivr.net/gh/daxiaamu/NICRoute@main/{p}")),
        new("Fastly", p => new($"https://fastly.jsdelivr.net/gh/daxiaamu/NICRoute@main/{p}")),
        new("Gcore", p => new($"https://gcore.jsdelivr.net/gh/daxiaamu/NICRoute@main/{p}")),
        new("TestingCF", p => new($"https://testingcf.jsdelivr.net/gh/daxiaamu/NICRoute@main/{p}")),
        new("Statically", p => new($"https://cdn.statically.io/gh/daxiaamu/NICRoute/main/{p}"))
    ];
    public UpdateFeed(IUpdateTransport transport, UpdatePreferences preferences, Action<UpdatePreferences> save,
        IReadOnlyList<MetadataSource>? sources = null, Func<DateTimeOffset>? clock = null)
    {
        this.transport = transport; this.preferences = preferences; this.save = save;
        this.sources = sources ?? DefaultSources; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (this.sources.Count < 5 || !this.sources.Any(s => s.Official)) throw new ArgumentException("需要至少五个元数据入口和官方权威源。");
    }
    private async Task<byte[]?> ReadAsync(MetadataSource source, string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var uri = source.ForPath(path);
            var builder = new UriBuilder(uri) { Query = uri.Query.TrimStart('?') + (uri.Query.Length > 0 ? "&" : "") + "_=" + Guid.NewGuid().ToString("N") };
            var bytes = await transport.GetBytesAsync(builder.Uri, 1024 * 1024, timeout.Token);
            using var json = JsonDocument.Parse(bytes);
            if(json.RootElement.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("更新信息必须为 JSON 对象。");
            if (json.RootElement.TryGetProperty("encoding", out var encoding) && encoding.ValueKind==JsonValueKind.String && encoding.GetString() == "base64")
            {
                if(!json.RootElement.TryGetProperty("content",out var content)||content.ValueKind!=JsonValueKind.String)
                    throw new InvalidDataException("GitHub 更新内容无效。");
                bytes = Convert.FromBase64String(content.GetString()!);
                if (bytes.Length > 1024 * 1024) throw new InvalidDataException("清单超过大小限制。");
            }
            return bytes;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException or FormatException or InvalidDataException)
        {
            if (token.IsCancellationRequested) throw;
            Diagnostic?.Invoke(source.Name + "：" + e.Message); return null;
        }
    }
    private async Task<UpdatePointer?> PointerAsync(MetadataSource source, string channel, CancellationToken token)
    {
        var bytes = await ReadAsync(source, $"updates/{channel}/latest.json", token);
        if (bytes is null) return null;
        try { var pointer = UpdateJson.Read<UpdatePointer>(bytes); pointer.Validate(channel, clock()); return pointer; }
        catch (Exception e) when (e is InvalidDataException or JsonException)
        { Diagnostic?.Invoke(source.Name + "：" + e.Message); return null; }
    }
    public async Task<UpdateManifest> CheckAsync(string channel, CancellationToken token = default)
    {
        UpdateRules.Channel(channel);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = sources.Select(s => PointerAsync(s, channel, session.Token)).ToArray();
        try
        {
            UpdatePointer? pointer = null;
            foreach (var index in Enumerable.Range(0, sources.Count).Where(i => sources[i].Official))
                if (await tasks[index] is { } authoritative) { pointer = authoritative; break; }
            if (pointer is not null)
            {
                await Task.Delay(400, token);
                foreach (var task in tasks.Where(t => t.IsCompletedSuccessfully))
                    if (task.Result is { } mirror && mirror.PolicyRevision == pointer.PolicyRevision && mirror.Digest != pointer.Digest)
                        throw new InvalidDataException("相同更新策略版本出现冲突，已拒绝本次更新，请稍后重试。");
            }
            else
            {
                // Unsigned mirrors never establish new authority. Only an unexpired pointer
                // already pinned from GitHub can be recovered when GitHub is unavailable.
                lock (preferences) pointer = preferences.For(channel).Pointer;
                if (pointer is null) throw new IOException("无法取得官方更新信息，请稍后重试。");
                pointer.Validate(channel, clock());
            }
            lock (preferences)
            {
                var trust = preferences.For(channel);
                if (pointer.PolicyRevision < trust.HighestRevision ||
                    pointer.PolicyRevision == trust.HighestRevision && trust.PointerDigest != "" && pointer.Digest != trust.PointerDigest)
                    throw new InvalidDataException("更新信息回滚或与已接受的策略冲突，已拒绝。");
            }
            var manifestTasks = sources.Select(s => ManifestAsync(s, pointer, session.Token)).ToList();
            UpdateManifest? manifest = null;
            while (manifestTasks.Count > 0)
            {
                var completed = await Task.WhenAny(manifestTasks); manifestTasks.Remove(completed);
                if (await completed is { } valid) { manifest = valid; break; }
            }
            if (manifest is null) throw new IOException("未能取得与官方摘要一致的更新清单。");
            lock (preferences)
            {
                var old = preferences.For(channel);
                if (manifest.MaxForcedVersionCode < old.MaxForcedVersionCode)
                    throw new InvalidDataException("更新信息尝试降低已生效的强制更新边界。");
                preferences.Trust[channel] = new() { HighestRevision = pointer.PolicyRevision, PointerDigest = pointer.Digest,
                    Pointer = pointer, MaxForcedVersionCode = manifest.MaxForcedVersionCode };
                try { save(preferences); }
                catch { preferences.Trust[channel] = old; throw; }
            }
            return manifest;
        }
        finally
        {
            await session.CancelAsync();
            // Observe cancelled requests so a failed mirror cannot surface an unobserved task.
            await Task.WhenAll(tasks.Select(async t => { try { await t; } catch (OperationCanceledException) { } }));
        }
    }
    private async Task<UpdateManifest?> ManifestAsync(MetadataSource source, UpdatePointer pointer, CancellationToken token)
    {
        var bytes = await ReadAsync(source, pointer.ManifestPath, token);
        if (bytes is null) return null;
        try
        {
            if (UpdateJson.Hash(bytes) != pointer.ManifestSha256) throw new InvalidDataException("清单 SHA256 与官方指针不符。");
            var manifest = UpdateJson.Read<UpdateManifest>(bytes); manifest.Validate(pointer); return manifest;
        }
        catch (Exception e) when (e is InvalidDataException or JsonException)
        { Diagnostic?.Invoke(source.Name + "：" + e.Message); return null; }
    }
}

public enum UpdateStatus { Current, Available, Failed }
public sealed record UpdateCheckResult(long SessionId, UpdateStatus Status, UpdateManifest? Manifest, string? Error, bool ManualRequested);

public sealed class UpdateManager
{
    private sealed class Session(long id, bool manual)
    { public long Id { get; } = id; public bool ManualRequested = manual; public Task<UpdateCheckResult> Task = null!; }
    private readonly object gate = new();
    private readonly long currentCode;
    private readonly UpdatePreferences preferences;
    private readonly Func<string, CancellationToken, Task<UpdateManifest>> check;
    private readonly Action<UpdatePreferences> save;
    private Session? active;
    private Task<UpdateCheckResult>? autoResult;
    private long sequence, presented;
    public UpdateManifest? KnownUpdate { get; private set; }
    public bool Checking { get { lock (gate) return active is { Task.IsCompleted: false }; } }
    public Task DrainAsync() { lock(gate) return active?.Task ?? Task.CompletedTask; }
    public bool RequiredUpdatePending
    {
        get
        {
            lock(preferences)
            {
                var boundary=preferences.For("stable").MaxForcedVersionCode;
                if(preferences.Channel=="beta") boundary=Math.Max(boundary,preferences.For("beta").MaxForcedVersionCode);
                return currentCode<=boundary;
            }
        }
    }
    public bool IsRequired(UpdateManifest manifest)
    {
        lock (preferences)
        {
            var boundary=preferences.For("stable").MaxForcedVersionCode;
            if(preferences.Channel=="beta") boundary=Math.Max(boundary,preferences.For("beta").MaxForcedVersionCode);
            return manifest.VersionCode>currentCode && currentCode<=boundary;
        }
    }
    public UpdateManager(long currentCode, UpdatePreferences preferences, Func<string, CancellationToken, Task<UpdateManifest>> check, Action<UpdatePreferences> save)
    { this.currentCode = currentCode; this.preferences = preferences; this.check = check; this.save = save; }
    public Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken token = default)
    {
        lock (gate)
        {
            if (active is { Task.IsCompleted: false }) { active.ManualRequested |= manual; return active.Task; }
            if (!manual && autoResult is not null) return autoResult;
            var session = new Session(++sequence, manual);
            // Assign the session before starting asynchronous work, including synchronous fakes.
            active = session;
            session.Task = RunAsync(session, preferences.Channel, token);
            if (!manual) autoResult = session.Task;
            return session.Task;
        }
    }
    private async Task<UpdateCheckResult> RunAsync(Session session, string channel, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            UpdateManifest selected;
            if (channel == "beta")
            {
                var candidates=new List<UpdateManifest>();
                foreach(var feed in new[]{"stable","beta"})
                    try { candidates.Add(await check(feed,token)); }
                    catch(Exception e) when(e is IOException or HttpRequestException or InvalidDataException) { }
                selected=candidates.OrderByDescending(m=>m.VersionCode).FirstOrDefault() ?? throw new IOException("无法取得有效的稳定版或测试版更新信息。");
            }
            else selected=await check("stable",token);
            lock (gate)
            {
                KnownUpdate = selected.VersionCode > currentCode ? selected : null;
                return new(session.Id, KnownUpdate is null ? UpdateStatus.Current : UpdateStatus.Available, selected, null, session.ManualRequested);
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or OperationCanceledException)
        { lock (gate) return new(session.Id, UpdateStatus.Failed, null, e.Message, session.ManualRequested); }
    }
    public bool TryClaimPresentation(UpdateCheckResult result, bool manual)
    {
        lock (gate)
        {
            if (result.SessionId <= presented) return false;
            var visible = manual || result.ManualRequested || result.Manifest is { } mandatory && IsRequired(mandatory) ||
                result.Status == UpdateStatus.Available && result.Manifest?.VersionCode != preferences.SkippedVersionCode;
            if (!visible) return false;
            presented = result.SessionId; return true;
        }
    }
    public void Skip(UpdateManifest manifest)
    {
        if (manifest.RequiredFor(currentCode) || IsRequired(manifest)) throw new InvalidOperationException("此版本不能跳过。");
        lock (gate) { preferences.SkippedVersionCode = manifest.VersionCode; save(preferences); }
    }
}
