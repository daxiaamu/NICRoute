using System.Diagnostics;
using System.Net.NetworkInformation;

namespace NICRoute.Core;

public enum TrafficState { Collecting, Live, Disconnected, Unavailable }
public sealed record TrafficCounter(string Id, bool LinkUp, long? Sent, long? Received, long Timestamp);
public sealed record AdapterTraffic(TrafficState State, double? UploadBytesPerSecond, double? DownloadBytesPerSecond,
    long UploadedBytes, long DownloadedBytes);

/// <summary>Derive rates from monotonic interface counters, never from wall-clock time.
/// New/reconnected/reset counters establish a baseline without generating a traffic spike.</summary>
public sealed class AdapterTrafficTracker
{
    private sealed record Entry(TrafficCounter? Previous, AdapterTraffic Reading);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, AdapterTraffic> Update(IReadOnlyList<TrafficCounter> counters)
    {
        var ids = counters.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in entries.Keys.Where(id => !ids.Contains(id)).ToArray()) entries.Remove(id);
        foreach (var counter in counters)
        {
            entries.TryGetValue(counter.Id, out var entry);
            var uploaded = entry?.Reading.UploadedBytes ?? 0;
            var downloaded = entry?.Reading.DownloadedBytes ?? 0;
            var state = !counter.LinkUp ? TrafficState.Disconnected :
                counter.Sent is null or < 0 || counter.Received is null or < 0 ? TrafficState.Unavailable : TrafficState.Collecting;
            if (state != TrafficState.Collecting)
            {
                entries[counter.Id] = new(null, new(state, null, null, uploaded, downloaded));
                continue;
            }
            double? uploadRate = null, downloadRate = null;
            if (entry?.Previous is { } previous && counter.Sent >= previous.Sent && counter.Received >= previous.Received)
            {
                if (counter.Timestamp <= previous.Timestamp) continue; // Ignore duplicate/out-of-order samples.
                var seconds = Stopwatch.GetElapsedTime(previous.Timestamp, counter.Timestamp).TotalSeconds;
                var sent = counter.Sent!.Value - previous.Sent!.Value;
                var received = counter.Received!.Value - previous.Received!.Value;
                uploaded = AddSaturating(uploaded, sent); downloaded = AddSaturating(downloaded, received);
                // A delayed timer (e.g. system sleep) cannot show a misleading live rate.
                if (seconds <= 10)
                {
                    uploadRate = sent / seconds; downloadRate = received / seconds; state = TrafficState.Live;
                }
            }
            entries[counter.Id] = new(counter, new(state, uploadRate, downloadRate, uploaded, downloaded));
        }
        return entries.ToDictionary(pair => pair.Key, pair => pair.Value.Reading, StringComparer.OrdinalIgnoreCase);
    }
    private static long AddSaturating(long value, long delta) => value > long.MaxValue - delta ? long.MaxValue : value + delta;
}

public sealed class AdapterTrafficMonitor
{
    private readonly AdapterTrafficTracker tracker = new();
    public IReadOnlyDictionary<string, AdapterTraffic> Sample(IReadOnlyList<AdapterInfo> adapters)
    {
        Dictionary<string, NetworkInterface> interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces().ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase); }
        catch (NetworkInformationException) { interfaces = new(StringComparer.OrdinalIgnoreCase); }
        var counters = adapters.Select(adapter =>
        {
            try
            {
                if (interfaces.TryGetValue(adapter.Id, out var nic))
                {
                    var up = nic.OperationalStatus == OperationalStatus.Up;
                    if (!up) return new TrafficCounter(adapter.Id, false, null, null, Stopwatch.GetTimestamp());
                    var stats = nic.GetIPStatistics();
                    return new TrafficCounter(adapter.Id, true, stats.BytesSent, stats.BytesReceived, Stopwatch.GetTimestamp());
                }
            }
            catch (Exception error) when (error is NetworkInformationException or InvalidOperationException or NotSupportedException) { }
            return new TrafficCounter(adapter.Id, adapter.LinkUp, null, null, Stopwatch.GetTimestamp());
        }).ToArray();
        return tracker.Update(counters);
    }
}
