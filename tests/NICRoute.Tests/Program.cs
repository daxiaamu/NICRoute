using NICRoute.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var passed = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); passed++; }
void Rejects(Action action, string message)
{
    try { action(); } catch (InvalidDataException) { passed++; return; }
    throw new Exception("FAIL: " + message);
}

var a = new AdapterInfo("a", "Ethernet", "wired", 1, true, ["192.0.2.2"]);
var b = new AdapterInfo("b", "Wi-Fi", "wireless", 2, true, ["198.51.100.2"]);
var app = new ApplicationTarget { Name = "Browser", ExecutablePath = @"C:\Apps\Browser\browser.exe" };
var rule = new AppRule { Application = app, AdapterId = a.Id, Fallback = FallbackKind.Block };
var settings = new AppSettings { GlobalAdapterId = b.Id, Rules = [rule] };

// Exhaustive primary/backup/global availability, policy and enabled-state matrix.
foreach (var primaryUp in new[] { false, true })
foreach (var backupUp in new[] { false, true })
foreach (var globalAutomatic in new[] { false, true })
foreach (var enabled in new[] { false, true })
foreach (var fallback in Enum.GetValues<FallbackKind>())
{
    rule.Enabled = enabled; rule.Fallback = fallback; rule.FallbackAdapterId = b.Id;
    settings.GlobalAdapterId = globalAutomatic ? "" : b.Id;
    AdapterInfo[] nics = [a with { LinkUp = primaryUp }, b with { LinkUp = backupUp }];
    var expected = !enabled ? (globalAutomatic ? RuleResolver.SystemTag : backupUp ? b.Tag : RuleResolver.BlockTag)
        : primaryUp ? a.Tag
        : fallback == FallbackKind.GlobalDefault ? (globalAutomatic ? RuleResolver.SystemTag : backupUp ? b.Tag : RuleResolver.BlockTag)
        : fallback == FallbackKind.Adapter && backupUp ? b.Tag : RuleResolver.BlockTag;
    Assert(RuleResolver.Resolve(rule, settings, nics).Outbound == expected, "fallback matrix");
}
rule.Enabled = true; rule.Fallback = FallbackKind.Adapter;
Assert(RuleResolver.Resolve(rule, settings, [a with { InternetReachable = false }, b]).Outbound == b.Tag, "failed internet probe uses backup");
Assert(RuleResolver.Resolve(rule, settings, [b]).Outbound == b.Tag, "removed primary uses backup");
Assert(RuleResolver.Resolve(rule, settings, []).IsBlocked, "both removed block");
rule.Fallback = FallbackKind.GlobalDefault; settings.GlobalAdapterId = a.Id;
Assert(RuleResolver.Resolve(rule, settings, [a with { LinkUp = false }, b]).IsBlocked, "global equal to primary cannot recurse/bypass");
Assert(!(a with { Addresses = [] }).Available, "no usable IP is unavailable");

var storeApp = new ApplicationTarget { Kind = ApplicationKind.WindowsPackage, Name = "Photos", PackageName = "Microsoft.Windows.Photos",
    PackageFamilyName = "Microsoft.Windows.Photos_8wekyb3d8bbwe", PublisherId = "8wekyb3d8bbwe",
    InstallLocation = @"C:\Program Files\WindowsApps\Microsoft.Windows.Photos_2025.1.2.0_x64__8wekyb3d8bbwe" };
var regex = new Regex(storeApp.PackagePathPattern());
Assert(regex.IsMatch(storeApp.InstallLocation + @"\Photos.exe"), "package executable match");
Assert(regex.IsMatch(@"c:\program files\windowsapps\Microsoft.Windows.Photos_2026.100.8.0_arm64__8wekyb3d8bbwe\Helpers\network.exe"), "version-independent package/helper/case match");
Assert(!regex.IsMatch(@"C:\Program Files\WindowsApps\Microsoft.Windows.Photos_2026.1.0.0_x64__EVIL\Photos.exe"), "different publisher excluded");
Assert(!regex.IsMatch(storeApp.InstallLocation + @"-Other\app.exe"), "sibling directory excluded");
Assert(!regex.IsMatch(@"C:\Apps\Photos.exe"), "same exe name outside package excluded");

rule.Application = storeApp; rule.Fallback = FallbackKind.Block; settings.GlobalAdapterId = b.Id;
var config = ConfigBuilder.Build(settings, [a, b], 29991, "test-secret", 29992);
Assert(config["route"]!["rules"]![1]!["process_path_regex"]![0]!.GetValue<string>() == storeApp.PackagePathPattern(), "Windows app route matcher");
Assert(config["dns"]!["rules"]![0]!["process_path_regex"] is not null, "DNS uses package matcher too");
Assert(config["inbounds"]![0]!["strict_route"]!.GetValue<bool>(), "strict routing enabled");
Assert(config["inbounds"]![0]!["address"]!.AsArray().Count == 2, "IPv4 and IPv6 TUN capture");
Assert(config["outbounds"]!.AsArray().Any(o => o!["bind_interface"]?.GetValue<string>() == a.Name), "real NIC binding emitted");
Assert(config["outbounds"]!.AsArray().Where(o => o!["type"]!.GetValue<string>() == "selector").All(o => o!["interrupt_exist_connections"]!.GetValue<bool>()), "switch closes existing connections");

var temp = Path.Combine(Path.GetTempPath(), "NICRoute-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var store = new SettingsStore(temp); store.Save(settings);
    Assert(store.Load().Rules[0].Application.PackageFamilyName == storeApp.PackageFamilyName, "settings retain package identity");
    Assert(store.Load().Rules[0].Id == rule.Id, "settings retain stable rule IDs");
    var duplicate = SettingsStore.Clone(settings); duplicate.Rules.Add(SettingsStore.Clone(settings).Rules[0]);
    Rejects(duplicate.Validate, "duplicate rules rejected");
    var invalid = SettingsStore.Clone(settings); invalid.Rules[0].Fallback = FallbackKind.Adapter; invalid.Rules[0].FallbackAdapterId = invalid.Rules[0].AdapterId;
    Rejects(invalid.Validate, "self fallback rejected");
    invalid = SettingsStore.Clone(settings); invalid.DnsServer = "example.com";
    Rejects(invalid.Validate, "ambiguous DNS host rejected");
    invalid = SettingsStore.Clone(settings); invalid.Rules[0].Application.PackageFamilyName = "";
    Rejects(invalid.Validate, "incomplete package rejected");

    var engineIndex = Array.IndexOf(args, "--engine");
    if (engineIndex >= 0)
    {
        var engine = Path.GetFullPath(args[engineIndex + 1]);
        var configPath = Path.Combine(temp, "check.json");
        await File.WriteAllTextAsync(configPath, ConfigBuilder.Serialize(config));
        var check = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "check", "-c", configPath }) check.ArgumentList.Add(arg);
        using var process = Process.Start(check)!;
        var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert(process.ExitCode == 0, "official sing-box validates generated TUN/package/DNS config: " + await error + await output);
        if (args.Contains("--integration")) await IntegrationAsync(engine, temp);
    }
    if (args.Contains("--catalog"))
    {
        var installed = await WindowsAppCatalog.GetAppsAsync();
        Assert(installed.All(p => p.Kind == ApplicationKind.WindowsPackage && p.InstallLocation.Length > 0), "installed Windows app catalog");
        Console.WriteLine($"Windows app catalog: {installed.Count} applications");
        var actual = AdapterCatalog.GetAdapters();
        Assert(actual.All(n => n.Index > 0 && n.Name != ConfigBuilder.TunName), "actual NIC discovery");
        Console.WriteLine($"NIC catalog: {actual.Count} interfaces, {actual.Count(n => n.Available)} available");
    }
}
finally
{
    // Delete only the unique directory created above, under the system temp directory.
    if (Path.GetDirectoryName(temp) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(temp).StartsWith("NICRoute-tests-"))
        Directory.Delete(temp, true);
}
passed+=await UpdateTests.RunAsync();
passed+=TrafficTests.Run();
if(args.Contains("--traffic")) passed+=await TrafficTests.VerifyNativeAsync();
var updaterIndex=Array.IndexOf(args,"--updater-e2e");
if(updaterIndex>=0) passed+=await UpdateTests.RunUpdaterAsync(args[updaterIndex+1],args[Array.IndexOf(args,"--fixtures")+1]);
var mudlIndex=Array.IndexOf(args,"--mudl");
if(mudlIndex>=0) passed+=await UpdateTests.RunMudlAsync(args[mudlIndex+1]);
Console.WriteLine($"PASS: {passed} assertions");

async Task IntegrationAsync(string enginePath, string work)
{
    // Localhost only: NO TUN, route changes, firewall changes, or external requests.
    await using var deny = new RejectEndpoint(); deny.Start();
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, deny.Port);
        await client.GetStream().WriteAsync(new byte[] { 5, 1, 0 });
        var response = new byte[2]; await client.GetStream().ReadExactlyAsync(response);
        Assert(response.SequenceEqual(new byte[] { 5, 255 }), "offline endpoint refuses SOCKS; never forwards");
    }
    var test = new AppSettings { Rules = [new() { Application = new() { Name = "test", ExecutablePath = Environment.ProcessPath! }, AdapterId = "missing", Fallback = FallbackKind.Block }] };
    var apiPort = FreePort(); var proxyPort = FreePort();
    var generated = ConfigBuilder.Build(test, AdapterCatalog.GetAdapters(), apiPort, "integration-secret", deny.Port, false, proxyPort);
    var path = Path.Combine(work, "integration.json"); await File.WriteAllTextAsync(path, ConfigBuilder.Serialize(generated));
    var start = new ProcessStartInfo(enginePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
    using var child = Process.Start(start)!;
    var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}"), Timeout = TimeSpan.FromSeconds(3) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "integration-secret");
    var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start();
    try
    {
        var ready = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (child.HasExited) throw new Exception("engine exited: " + await stdout + await stderr);
            try { using var response = await http.GetAsync("version"); if (response.IsSuccessStatusCode) { ready = true; break; } }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        Assert(ready, "actual engine starts without TUN");
        var monitoredAdapters = AdapterCatalog.GetAdapters();
        var trafficMonitor = args.Contains("--traffic") ? new AdapterTrafficMonitor() : null;
        trafficMonitor?.Sample(monitoredAdapters);
        using (var unauth = new HttpClient(new SocketsHttpHandler { UseProxy = false }))
        { Assert((await unauth.GetAsync($"http://127.0.0.1:{apiPort}/proxies")).StatusCode == HttpStatusCode.Unauthorized, "control API requires secret"); }
        var proxyData = await http.GetFromJsonAsync<JsonObject>("proxies");
        var tag = test.Rules[0].SelectorTag;
        Assert(proxyData!["proxies"]![tag]!["now"]!.GetValue<string>() == RuleResolver.BlockTag, "app selector initially offline");
        using (var change = await http.PutAsJsonAsync("proxies/" + tag, new { name = RuleResolver.SystemTag }))
            Assert(change.IsSuccessStatusCode, "runtime selector update works");
        using (var change = await http.PutAsJsonAsync("proxies/" + tag, new { name = RuleResolver.BlockTag }))
            Assert(change.IsSuccessStatusCode, "runtime fallback to offline works");

        // A real HTTP CONNECT from this executable must match the app rule and be rejected;
        // it would reach our echo server if process identification failed (global is direct).
        var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            var request = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n");
            await client.GetStream().WriteAsync(request, timeout.Token);
            // CONNECT may acknowledge before the outgoing dial. Sending triggers actual routing.
            await client.GetStream().WriteAsync(new byte[] { 42 }, timeout.Token);
            var data = new byte[512];
            try { _ = await client.GetStream().ReadAsync(data, timeout.Token); } catch (IOException) { }
            await Task.Delay(300);
            Assert(!echo.Pending(), "actual process rule prevents destination connection");
        }
        // Route switch back to direct: same executable must now reach the destination.
        using (var change = await http.PutAsJsonAsync("proxies/" + tag, new { name = RuleResolver.SystemTag })) change.EnsureSuccessStatusCode();
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n"), timeout.Token);
            await client.GetStream().WriteAsync(new byte[] { 42 }, timeout.Token);
            using var accepted = await echo.AcceptTcpClientAsync(timeout.Token);
            var data = new byte[1]; await accepted.GetStream().ReadExactlyAsync(data, timeout.Token);
            Assert(data[0] == 42, "same application reaches destination after runtime switch");
        }
        // Actual SOCKS5 UDP association traverses the same process rule / selector.
        using var udpDestination = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)udpDestination.Client.LocalEndPoint!).Port;
        var packet = new byte[] { 0, 0, 0, 1, 127, 0, 0, 1, (byte)(udpPort >> 8), (byte)udpPort, 73 };
        using (var control = await UdpAssociationAsync(proxyPort))
        using (var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            var relay = (IPEndPoint)control.Client.RemoteEndPoint!;
            var reply = new byte[10]; await control.GetStream().ReadExactlyAsync(reply, timeout.Token);
            relay = new IPEndPoint(IPAddress.Loopback, (reply[8] << 8) | reply[9]);
            await sender.SendAsync(packet, relay);
            var incoming = await udpDestination.ReceiveAsync(timeout.Token);
            Assert(incoming.Buffer.SequenceEqual(new byte[] { 73 }), "UDP datagram follows application's direct selector");
        }
        using (var change = await http.PutAsJsonAsync("proxies/" + tag, new { name = RuleResolver.BlockTag })) change.EnsureSuccessStatusCode();
        using (var control = await UdpAssociationAsync(proxyPort))
        using (var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            var reply = new byte[10]; await control.GetStream().ReadExactlyAsync(reply, timeout.Token);
            await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, (reply[8] << 8) | reply[9]));
            using var blockedTimeout = new CancellationTokenSource(500);
            var blocked = false;
            try { await udpDestination.ReceiveAsync(blockedTimeout.Token); }
            catch (OperationCanceledException) { blocked = true; }
            Assert(blocked, "UDP offline selector never forwards to destination");
        }

        var bound = AdapterCatalog.GetAdapters().FirstOrDefault(n => n.Available && n.Addresses.Any(ip => IPAddress.Parse(ip).AddressFamily == AddressFamily.InterNetwork));
        if (bound is not null)
        {
            var localAddress = IPAddress.Parse(bound.Addresses.First(ip => IPAddress.Parse(ip).AddressFamily == AddressFamily.InterNetwork));
            var boundDestination = new TcpListener(localAddress, 0); boundDestination.Start();
            try
            {
                using (var change = await http.PutAsJsonAsync("proxies/" + tag, new { name = bound.Tag })) change.EnsureSuccessStatusCode();
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
                var destinationPort = ((IPEndPoint)boundDestination.LocalEndpoint).Port;
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {localAddress}:{destinationPort} HTTP/1.1\r\nHost: {localAddress}:{destinationPort}\r\n\r\n"), timeout.Token);
                await client.GetStream().WriteAsync(new byte[] { 18 }, timeout.Token);
                using var received = await boundDestination.AcceptTcpClientAsync(timeout.Token);
                Assert(((IPEndPoint)received.Client.RemoteEndPoint!).Address.Equals(localAddress), "NIC-bound direct outbound uses selected interface source IP");
            }
            finally { boundDestination.Stop(); }
        }
        if (trafficMonitor is not null)
        {
            var readings = trafficMonitor.Sample(monitoredAdapters);
            Assert(!child.HasExited && readings.Count == monitoredAdapters.Count &&
                readings.Values.Any(r => r.State == TrafficState.Live), "native traffic monitoring works while routing engine serves TCP and UDP");
        }
    }
    finally
    {
        echo.Stop();
        if (!child.HasExited) child.Kill(true);
        await child.WaitForExitAsync();
        var log = await stdout + await stderr;
        Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(),"artifacts"));
        await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "integration-engine.log"), log);
        Assert(log.Contains(test.Rules[0].Application.ExecutablePath, StringComparison.OrdinalIgnoreCase), "engine identifies executable path in connection logs");
    }
}

static async Task<TcpClient> UdpAssociationAsync(int proxyPort)
{
    var control = new TcpClient();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await control.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
    await control.GetStream().WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
    var greeting = new byte[2]; await control.GetStream().ReadExactlyAsync(greeting, timeout.Token);
    if (greeting[0] != 5 || greeting[1] != 0) throw new Exception("SOCKS greeting rejected");
    await control.GetStream().WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, timeout.Token);
    return control;
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
}
