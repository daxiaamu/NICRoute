using System.Diagnostics;
using System.Text;
using NICRoute.Core;

internal static class ApplicationTrafficTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL applications: " + message); count++; }
        var a = new AdapterInfo("a", "NIC A", "", 1, true, ["192.0.2.2"]);
        var b = new AdapterInfo("b", "NIC B", "", 2, true, ["198.51.100.2"]);
        var target = new ApplicationTarget { Name = "Example", ExecutablePath = @"C:\Apps\example.exe" };
        var settings = new AppSettings { Rules = [new() { Application = target, AdapterId = "a", Fallback = FallbackKind.Block }] };
        Check(!settings.ProbeInternet, "interface restrictions do not enable connectivity probes");
        var config = ConfigBuilder.Build(settings, [a, b], 20001, "test-only", 20002, false, 20003, 20004);
        Check(config["services"]![0]!["listen"]!.GetValue<string>() == "127.0.0.1", "application monitor is authenticated and loopback-only");
        Check(config["route"]!["rules"]!.AsArray().Last()!["invert"]!.GetValue<bool>() &&
            config["route"]!["rules"]!.AsArray().Last()!["outbound"]!.GetValue<string>() == RuleResolver.BlockTag,
            "unidentified process lookup cannot silently use the global route when strict fallback is configured");
        var tracker = new ApplicationTrafficTracker(); tracker.Configure(settings, [a, b]); tracker.Begin(); tracker.SetOnline(true);
        var tick = Stopwatch.Frequency;
        Check(tracker.Snapshot(tick).Single() is { Uploaded: 0, UploadRate: null, ActualRoute: "—" }, "idle configured app has no fabricated actual NIC");
        ObservedConnection Conn(string id, long up, long down, string outbound, bool closed = false) =>
            new(id, target.ExecutablePath, 123, "tcp", "192.0.2.1:443", outbound, [outbound, settings.Rules[0].SelectorTag], up, down, closed);
        void Send(ObservedConnection connection, int kind = 0, bool reset = false) => tracker.Receive(new(reset, [new(kind, connection.Id, connection, 0, 0)]));
        Send(Conn("one", 64, 128, a.Tag));
        var first = tracker.Snapshot(tick * 2).Single();
        Check(first is { Uploaded: 64, Downloaded: 128, UploadRate: 64, DownloadRate: 128, ActualRoute: "NIC A", Connections: 1 }, "actual connection chain and independent app directions");
        tracker.Receive(new(false, [new(1, "one", null, 100, 200)]));
        Check(tracker.Snapshot(tick * 3).Single() is { Uploaded: 164, Downloaded: 328, UploadRate: 100 }, "stream deltas aggregate without connection polling");
        Send(Conn("two", 10, 20, b.Tag));
        var two = tracker.Snapshot(tick * 4).Single();
        Check(two.Connections == 2 && two.ActualRoute.Contains("NIC A") && two.ActualRoute.Contains("NIC B"), "all simultaneous actual NICs are shown");
        Send(Conn("one", 300, 600, a.Tag, true), 2);
        Check(tracker.Snapshot(tick * 5).Single() is { Uploaded: 310, Downloaded: 620, ActualRoute: "NIC B" }, "closed connection final counters remain included without stale active NIC");
        Send(Conn("two", 15, 25, b.Tag, true), 2);
        Check(tracker.Snapshot(tick * 6).Single() is { Uploaded: 315, Downloaded: 625, Connections: 0, ActualRoute: "最近：NIC B" }, "short connections retain final counters and latest route is explicitly historical");
        for (var i = 0; i < 10; i++)
            tracker.Receive(new(true, [new(0, "one", Conn("one", 300, 600, a.Tag, true), 0, 0), new(0, "two", Conn("two", 15, 25, b.Tag, true), 0, 0)]));
        Check(tracker.Snapshot(tick * 7).Single().Uploaded == 315, "reconnect closed-history replay cannot count bytes twice");
        Send(Conn("auto", 0, 0, RuleResolver.SystemTag));
        Check(tracker.Snapshot(tick * 8).Single().ActualRoute.Contains("待确认"), "system automatic routing is not falsely reported as a configured NIC");
        tracker.SetOnline(false);
        Check(tracker.Snapshot(tick * 9).Single() is { UploadRate: null, Connections: 0 }, "disconnected subscription never reports stale live rates or active NICs");
        tracker.Stop(); Check(tracker.Snapshot(tick * 10).Single().Status == "未启动分流", "stopped engine monitoring is explicitly unavailable");
        tracker.Begin(); tracker.SetOnline(true); tracker.Snapshot(tick * 11); Send(Conn("new-session", 10, 10, a.Tag));
        Check(tracker.Snapshot(tick * 12).Single().Uploaded == 325, "restarting engine preserves app session totals with fresh connection IDs");
        tracker.Receive(new(false, [new(1, "new-session", null, 1000, 0)]));
        Send(Conn("new-session", 500, 100, a.Tag, true), 2);
        Check(tracker.Snapshot(tick * 13).Single().Uploaded == 815, "absolute closed counters correct races between new metadata and streamed deltas");
        var package = new ApplicationTarget { Kind = ApplicationKind.WindowsPackage, Name = "Tasks", PackageFamilyName = "Vendor.Tasks_publisher",
            PackageName = "Vendor.Tasks", PublisherId = "publisher", InstallLocation = @"C:\Program Files\WindowsApps\Vendor.Tasks_1.0.0.0_x64__publisher" };
        var packages = new ApplicationTrafficTracker(); packages.Configure(new() { Rules = [new() { Application = package, AdapterId = "a" }] }, [a]); packages.Begin(); packages.SetOnline(true);
        var packaged = new ObservedConnection("package-one", @"C:\Program Files\WindowsApps\Vendor.Tasks_2.0.0.0_x64__publisher\Helper\network.exe", 12, "udp", "192.0.2.1:443", a.Tag, [a.Tag], 40, 80, true);
        packages.Receive(new(true, [new(2, packaged.Id, packaged, 0, 0)]));
        Check(packages.Snapshot(tick).Single() is { Name: "Tasks", Uploaded: 40, Downloaded: 80 }, "version-independent Windows package helper attribution");
        ApplicationNetworkGuard.ValidateLayout();
        Check(ApplicationNetworkGuard.Targets(settings).Length == 1, "enabled app gets a kernel guard identity");
        settings.Rules[0].Enabled = false;
        Check(ApplicationNetworkGuard.Targets(settings).Length == 0, "disabled rule removes its app guard");
        var request = ConnectionWire.SubscriptionRequest();
        Check(request.Length == 11 && request[4] == 6 && request[5] == 8, "gRPC-Web subscription is bounded and framed");
        var metadata = Message(Num(1, 123), Str(4, target.ExecutablePath));
        var conn = Message(Str(1, "wire"), Str(5, "tcp"), Str(7, "192.0.2.1:443"), Num(16, 100), Num(17, 200), Str(19, a.Tag), Str(21, a.Tag), Field(22, metadata));
        var batch = Message(Field(1, Message(Str(2, "wire"), Field(3, conn))), Num(2, 1), Num(99, 42));
        var parsed = ConnectionWire.Parse(batch);
        Check(parsed.Reset && parsed.Events.Single().Connection is { ProcessId: 123, Uploaded: 100, Downloaded: 200 } wire && wire.ProcessPath == target.ExecutablePath,
            "official protobuf fields decode with unknown fields skipped");
        foreach (var bytes in new byte[][] { [0], [10, 20, 1], [8, 128], [11], [10, 255, 255, 255, 255, 127] })
        {
            try { ConnectionWire.Parse(bytes); throw new Exception("Malformed protobuf accepted."); }
            catch (Exception error) when (error is InvalidDataException or OverflowException) { count++; }
        }
        Console.WriteLine($"Application monitoring and guard policy: {count} checks passed."); return count;
    }
    private static byte[] Message(params byte[][] fields) => fields.SelectMany(v => v).ToArray();
    private static byte[] Num(int field, ulong value) => Message(Varint((ulong)(field << 3)), Varint(value));
    private static byte[] Str(int field, string value) => Field(field, Encoding.UTF8.GetBytes(value));
    private static byte[] Field(int field, byte[] value) => Message(Varint((ulong)(field << 3 | 2)), Varint((ulong)value.Length), value);
    private static byte[] Varint(ulong value)
    {
        var result = new List<byte>(); do { var current = (byte)(value & 127); value >>= 7; result.Add((byte)(current | (value > 0 ? 128 : 0))); } while (value > 0); return result.ToArray();
    }
}
