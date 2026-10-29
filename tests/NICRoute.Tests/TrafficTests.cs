using System.Diagnostics;
using NICRoute.Core;

internal static class TrafficTests
{
    public static int Run()
    {
        var passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL traffic: " + name); passed++; }
        var tracker = new AdapterTrafficTracker();
        var tick = Stopwatch.Frequency;
        IReadOnlyDictionary<string, AdapterTraffic> Sample(long second, long? up, long? down, bool connected = true)
            => tracker.Update([new("nic", connected, up, down, second * tick)]);
        var initial = Sample(1, 900_000_000, 800_000_000)["nic"];
        Check(initial is { State: TrafficState.Collecting, UploadBytesPerSecond: null, UploadedBytes: 0, DownloadedBytes: 0 },
            "startup does not attribute pre-existing system counters to this session");
        var live = Sample(3, 900_002_048, 800_008_192)["nic"];
        Check(live is { State: TrafficState.Live, UploadBytesPerSecond: 1024, DownloadBytesPerSecond: 4096 },
            "independent upload/download deltas use actual elapsed monotonic time");
        Check(live is { UploadedBytes: 2048, DownloadedBytes: 8192 }, "session totals add counter deltas");
        var idle = Sample(4, 900_002_048, 800_008_192)["nic"];
        Check(idle is { State: TrafficState.Live, UploadBytesPerSecond: 0, DownloadBytesPerSecond: 0 }, "idle is zero rather than missing data");
        var duplicate = Sample(4, 900_003_072, 800_009_216)["nic"];
        Check(duplicate == idle, "duplicate timestamps cannot divide by zero or alter the baseline");
        var outOfOrder = Sample(2, 900_004_096, 800_010_240)["nic"];
        Check(outOfOrder == idle, "out of order samples are ignored");
        var reset = Sample(5, 2, 3)["nic"];
        Check(reset is { State: TrafficState.Collecting, UploadBytesPerSecond: null, UploadedBytes: 2048 },
            "counter reset starts a new baseline without negative rate or lost totals");
        var afterReset = Sample(6, 1026, 2051)["nic"];
        Check(afterReset is { UploadBytesPerSecond: 1024, DownloadBytesPerSecond: 2048, UploadedBytes: 3072 }, "sampling resumes after reset");
        var unplugged = Sample(7, null, null, false)["nic"];
        Check(unplugged is { State: TrafficState.Disconnected, UploadedBytes: 3072 }, "disconnect preserves monitored totals");
        var reconnect = Sample(8, 100_000, 200_000)["nic"];
        Check(reconnect is { State: TrafficState.Collecting, UploadBytesPerSecond: null, UploadedBytes: 3072 }, "reconnect never spikes from stale counters");
        var unavailable = Sample(9, null, null)["nic"];
        Check(unavailable is { State: TrafficState.Unavailable, UploadBytesPerSecond: null }, "read failure is not disguised as idle");
        Check(Sample(10, 200_000, 300_000)["nic"].State == TrafficState.Collecting, "first successful read after failure warms up");
        var sleep = Sample(30, 300_000, 500_000)["nic"];
        Check(sleep is { State: TrafficState.Collecting, UploadBytesPerSecond: null, UploadedBytes: 103072 },
            "sleep gap does not show a stale average as live speed; known deltas still count");
        Check(Sample(31, 301_000, 502_000)["nic"].UploadBytesPerSecond == 1000, "normal live rate returns after sleep");
        Check(Sample(32, -1, 0)["nic"].State == TrafficState.Unavailable, "invalid negative native counters are unavailable");
        var multi = new AdapterTrafficTracker();
        multi.Update([new("a", true, 10, 20, tick), new("b", true, 500, 100, tick)]);
        var two = multi.Update([new("b", true, 900, 700, tick * 2), new("a", true, 110, 220, tick * 2)]);
        Check(two["a"].UploadBytesPerSecond == 100 && two["b"].UploadBytesPerSecond == 400,
            "reordering adapters cannot exchange interface rates");
        var one = multi.Update([new("A", true, 210, 420, tick * 3)]);
        Check(one.Count == 1 && one["a"].UploadedBytes == 200, "identity comparison is case insensitive and removed interfaces are pruned");
        var readded = multi.Update([new("b", true, 2000, 2000, tick * 4)]);
        Check(readded["b"].State == TrafficState.Collecting, "reinserted interface starts a fresh baseline");
        var wide = new AdapterTrafficTracker();
        wide.Update([new("fast", true, 0, 0, tick)]);
        var fast = wide.Update([new("fast", true, 10_000_000_000, 20_000_000_000, tick * 2)])["fast"];
        Check(fast.UploadBytesPerSecond == 10_000_000_000 && fast.DownloadedBytes == 20_000_000_000,
            "64-bit counters preserve rates and totals beyond 4 GiB");
        var partial = wide.Update([new("fast", true, null, null, tick * 3), new("other", true, 0, 0, tick * 3)]);
        Check(partial["fast"].State == TrafficState.Unavailable && partial["other"].State == TrafficState.Collecting,
            "one unreadable interface does not suppress other interfaces");
        Console.WriteLine($"Traffic sampler: {passed} checks passed.");
        return passed;
    }
    public static async Task<int> VerifyNativeAsync()
    {
        var adapters = AdapterCatalog.GetAdapters();
        var monitor = new AdapterTrafficMonitor();
        monitor.Sample(adapters);
        await Task.Delay(1100);
        var readings = monitor.Sample(adapters);
        if (readings.Count != adapters.Count || readings.Values.Any(r => r.UploadBytesPerSecond < 0 || r.DownloadBytesPerSecond < 0 ||
            r.UploadedBytes < 0 || r.DownloadedBytes < 0)) throw new Exception("Native traffic sample is invalid.");
        if (!readings.Values.Any(r => r.State == TrafficState.Live)) throw new Exception("No Windows interface could provide live traffic counters.");
        Console.WriteLine($"Native Windows traffic counters: {readings.Count} interfaces, {readings.Values.Count(r => r.State == TrafficState.Live)} live.");
        return 2;
    }
}
