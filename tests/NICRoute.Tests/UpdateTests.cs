using NICRoute.Core.Updates;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

internal static class UpdateTests
{
    public static async Task<int> RunMudlAsync(string executable)
    {
        var root=Path.Combine(Path.GetTempPath(),"NICRoute-mudl-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var http=new UpdateHttp();
            var engine=new MudlDownloadEngine(Path.GetFullPath(executable),http.ResolveHttpsAsync);
            var file=Path.Combine(root,"source.zip");
            const string hash="ff5951fa8ea2c8ebd5e4d85710e5afaab49d4f944996586180c5dc08234c5436";
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await engine.DownloadAsync(["https://github.com/daxiaamu/NICRoute/releases/download/v0.1.0/NICRoute-0.1.0-source.zip"],file,32,hash,64908,null,timeout.Token);
            if(new FileInfo(file).Length!=64908||await PackageVerifier.FileHashAsync(file)!=hash) throw new Exception("Actual MUDL artifact failed integrity.");
            Console.WriteLine("Actual MUDL HTTPS download and independent SHA256 passed."); return 2;
        }
        finally {if(Path.GetDirectoryName(root)==Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)&&Path.GetFileName(root).StartsWith("NICRoute-mudl-tests-")) Directory.Delete(root,true);}
    }
    public static async Task<int> RunUpdaterAsync(string package,string fixtures)
    {
        var root=Path.Combine(Directory.GetCurrentDirectory(),"artifacts","updater-e2e-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var assertions=0;var completed=false;
        try
        {
            foreach(var variant in new[]{"new","bad"})
            {
                var caseRoot=Path.Combine(root,variant);var payload=Path.Combine(caseRoot,"payload");var install=Path.Combine(caseRoot,"install");var data=Path.Combine(caseRoot,"profile");
                CopyTree(Path.GetFullPath(package),payload);CopyTree(Path.Combine(fixtures,"old"),install);
                foreach(var name in new[]{"NICRoute.exe","NICRoute.dll","NICRoute.deps.json","NICRoute.runtimeconfig.json"})
                    File.Copy(Path.Combine(fixtures,variant,name),Path.Combine(payload,name),true);
                var identity=ProductIdentity.Read(Path.Combine(payload,"NICRoute.dll"));var index=PackageVerifier.CreateIndex(payload);
                File.WriteAllBytes(Path.Combine(payload,PackageIndex.FileName),UpdateJson.Bytes(index));
                Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"settings.json"),"preserved route rules");
                File.WriteAllText(Path.Combine(install,"custom.txt"),"preserved user file");
                var zip=Path.Combine(caseRoot,"update.zip");ZipFile.CreateFromDirectory(payload,zip);
                var manifest=new UpdateManifest{Channel="stable",PolicyRevision=2,VersionCode=2,VersionName=identity.VersionName,Sha256=await PackageVerifier.FileHashAsync(zip),Size=new FileInfo(zip).Length,
                    ReleaseUrl="https://github.com/daxiaamu/NICRoute/releases/tag/v0.2.0",Urls=["https://github.com/daxiaamu/NICRoute/releases/download/v0.2.0/NICRoute-win-x64.zip"]};
                var plan=await UpdateInstaller.PrepareAsync(manifest,zip,data,install,false);
                plan=plan with{ParentProcessId=int.MaxValue,ParentStartedUtcTicks=1};
                var planPath=Path.Combine(plan.SessionDirectory,"install-plan.json");File.WriteAllBytes(planPath,UpdateJson.Bytes(plan));
                var start=new ProcessStartInfo(Path.Combine(plan.StageDirectory,"NICRoute.Updater.exe")){UseShellExecute=false,CreateNoWindow=true};
                start.ArgumentList.Add("--plan");start.ArgumentList.Add(planPath);start.ArgumentList.Add("--quiet");
                using var helper=Process.Start(start)!;
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(55));
                try {await helper.WaitForExitAsync(timeout.Token);}catch{helper.Kill(true);throw;}
                var expectedCode=variant=="new"?2:1;
                for(var attempt=0;attempt<50&&!File.Exists(Path.Combine(data,"fixture-version.txt"));attempt++) await Task.Delay(100);
                var detail=File.Exists(Path.Combine(plan.SessionDirectory,"result.txt"))?File.ReadAllText(Path.Combine(plan.SessionDirectory,"result.txt")):"No installer result.";
                if((variant=="new"&&helper.ExitCode!=0)||(variant=="bad"&&helper.ExitCode==0)) throw new Exception("Updater process exit mismatch: "+detail);
                if(ProductIdentity.Read(Path.Combine(install,"NICRoute.dll")).VersionCode!=expectedCode) throw new Exception("Updater installed/restored wrong version: "+detail);
                if(File.ReadAllText(Path.Combine(data,"fixture-version.txt"))!=expectedCode.ToString()) throw new Exception("Updater did not restart the expected version: "+detail);
                if(File.ReadAllText(Path.Combine(data,"settings.json"))!="preserved route rules"||File.ReadAllText(Path.Combine(install,"custom.txt"))!="preserved user file") throw new Exception("Updater modified user data.");
                assertions+=4;Console.WriteLine($"Updater process {variant}: version={expectedCode}, restart and data preservation verified.");
            }
            completed=true;
        }
        finally
        {
            await Task.Delay(500);
            var allowed=Path.Combine(Directory.GetCurrentDirectory(),"artifacts")+Path.DirectorySeparatorChar;
            if(completed&&Path.GetFullPath(root).StartsWith(allowed,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(root).StartsWith("updater-e2e-")) Directory.Delete(root,true);
            if(!completed) Console.WriteLine("Updater diagnostic files retained: "+root);
        }
        return assertions;
    }
    private static void CopyTree(string source,string target)
    {
        Directory.CreateDirectory(target);
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {var destination=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination,true);}
    }
    public static async Task<int> RunAsync()
    {
        var count=0;
        void Assert(bool condition,string message) { if(!condition) throw new Exception("FAIL updates: "+message); count++; }
        void Reject(Action action,string message)
        { try { action(); } catch(Exception e) when(e is InvalidDataException or JsonException or ArgumentException or InvalidOperationException) { count++; return; } throw new Exception("FAIL updates: "+message); }
        async Task RejectAsync(Func<Task> action,string message)
        { try { await action(); } catch(Exception e) when(e is InvalidDataException or IOException or HttpRequestException) { count++; return; } throw new Exception("FAIL updates: "+message); }
        var now=DateTimeOffset.Parse("2026-10-29T00:00:00Z");
        var manifest=new UpdateManifest { Channel="stable",PolicyRevision=8,VersionCode=3,VersionName="0.3.0",Sha256=new string('a',64),Size=100,
            PublishedAt="2026-10-28T08:00:00Z",Changelog="## Update\n**safe** <script>alert(1)</script>",
            Urls=["https://github.com/daxiaamu/NICRoute/releases/download/v0.3.0/NICRoute-win-x64.zip"],
            ReleaseUrl="https://github.com/daxiaamu/NICRoute/releases/tag/v0.3.0" };
        UpdatePointer Pointer(UpdateManifest m)
        {
            var hash=UpdateJson.Hash(UpdateJson.Bytes(m));
            return new(){ Channel=m.Channel,PolicyRevision=m.PolicyRevision,ManifestSha256=hash,
                ManifestPath=$"updates/{m.Channel}/manifests/{m.PolicyRevision}-{hash}.json",ExpiresAt=now.AddDays(30).ToString("O") };
        }
        var pointer=Pointer(manifest); pointer.Validate("stable",now); manifest.Validate(pointer);
        Assert(pointer.Digest.Length==64,"canonical pointer digest is finite");
        Assert(UpdateJson.Read<UpdateManifest>(UpdateJson.Bytes(manifest)).VersionCode==3,"manifest round trip");
        Reject(()=>UpdateJson.Read<UpdatePointer>(Encoding.UTF8.GetBytes("{\"channel\":\"stable\",\"channel\":\"beta\"}")),"duplicate JSON fields");
        Reject(()=>UpdateJson.Https("http://example.com/a.zip"),"HTTPS enforced");
        Reject(()=>UpdateJson.Https("https://user:password@example.com/a.zip"),"credential-bearing download URL rejected");
        Reject(()=>pointer.Validate("beta",now),"channel isolation");
        Reject(()=>pointer.Validate("stable",now.AddYears(1)),"expiration enforced");
        Reject(()=> (manifest with {Sha256="bad"}).Validate(pointer),"invalid hash rejected");
        Reject(()=> (manifest with {MaxForcedVersionCode=3}).Validate(pointer),"forced boundary below latest");
        Reject(()=> (manifest with {Platform="win-arm64"}).Validate(pointer),"platform mismatch");
        Reject(()=> (manifest with {Urls=["https://mirror.example/a.zip"]}).Validate(pointer),"official artifact required");
        Assert((manifest with {PublishedAt="not a date"}).LocalPublishedAt is null,"invalid publication date hidden");
        Assert(manifest.LocalPublishedAt is { } date&&!date.Contains('T')&&!date.EndsWith('Z'),"date displayed in local culture");
        Assert(!(manifest with {PublishedAt=null}).RequiredFor(2),"zero forced boundary is optional");
        Assert((manifest with {MaxForcedVersionCode=2}).RequiredFor(2),"inclusive durable forced boundary");
        Assert(!(manifest with {MaxForcedVersionCode=2}).RequiredFor(3),"updated client no longer forced");

        var pref=new UpdatePreferences(); var calls=0;
        var deferred=new TaskCompletionSource<UpdateManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager=new UpdateManager(2,pref,(_,_)=>{Interlocked.Increment(ref calls);return deferred.Task;},_=>{});
        var automatic=manager.CheckAsync(false);
        var manual=manager.CheckAsync(true);
        Assert(ReferenceEquals(automatic,manual),"manual joins same automatic session");
        deferred.SetResult(manifest); var result=await manual;
        Assert(calls==1&&result.ManualRequested,"single actual request with visible manual result");
        Assert(manager.TryClaimPresentation(result,false),"automatic waiter can present joined manual result");
        Assert(!manager.TryClaimPresentation(result,true),"one dialog per shared session");
        await manager.CheckAsync(false); Assert(calls==1,"completed automatic check reused");
        var fresh=await manager.CheckAsync(true); Assert(calls==2&&fresh.SessionId!=result.SessionId,"manual check bypasses completed cache");
        manager.Skip(manifest); Assert(pref.SkippedVersionCode==3,"skip persisted");
        var suppressed=new UpdateManager(2,pref,(_,_)=>Task.FromResult(manifest),_=>{});
        var skipped=await suppressed.CheckAsync(false); Assert(!suppressed.TryClaimPresentation(skipped,false),"skipped version suppresses automatic prompt");
        Assert(suppressed.TryClaimPresentation(await suppressed.CheckAsync(true),true),"manual still shows skipped version");
        var forced=manifest with {MaxForcedVersionCode=2};
        Reject(()=>suppressed.Skip(forced),"forced update cannot be skipped");
        var betaPreferences=new UpdatePreferences{Channel="beta"};
        var betaManager=new UpdateManager(2,betaPreferences,(channel,_)=>channel=="stable"?Task.FromException<UpdateManifest>(new IOException()):Task.FromResult(manifest with {Channel="beta",VersionCode=4}),_=>{});
        Assert((await betaManager.CheckAsync(true)).Manifest!.VersionCode==4,"beta can work when stable is unavailable");
        var integerManager=new UpdateManager(2,new(),(_,_)=>Task.FromResult(manifest with{VersionCode=1,VersionName="999.0.0"}),_=>{});
        Assert((await integerManager.CheckAsync(true)).Status==UpdateStatus.Current,"versionCode alone decides newness");

        var sources=Enumerable.Range(0,5).Select(i=>new MetadataSource("source"+i,p=>new Uri($"https://source{i}.example/{p}"),i==0)).ToArray();
        var wire=new FakeTransport();
        foreach(var source in sources)
        {
            wire.Bytes[source.ForPath("updates/stable/latest.json").GetLeftPart(UriPartial.Path)]=UpdateJson.Bytes(pointer);
            wire.Bytes[source.ForPath(pointer.ManifestPath).GetLeftPart(UriPartial.Path)]=UpdateJson.Bytes(manifest);
        }
        var trust=new UpdatePreferences();
        var feed=new UpdateFeed(wire,trust,_=>{},sources,()=>now);
        Assert((await feed.CheckAsync("stable")).VersionCode==3,"authority-backed manifest accepted");
        Assert(trust.For("stable").HighestRevision==8,"highest policy revision pinned");
        wire.Bytes[sources[2].ForPath("updates/stable/latest.json").GetLeftPart(UriPartial.Path)]=UpdateJson.Bytes(pointer with{ManifestSha256=new string('b',64),ManifestPath=$"updates/stable/manifests/8-{new string('b',64)}.json"});
        await RejectAsync(()=>feed.CheckAsync("stable"),"same-revision mirror conflict rejected");
        wire.Bytes[sources[2].ForPath("updates/stable/latest.json").GetLeftPart(UriPartial.Path)]=UpdateJson.Bytes(pointer);
        wire.Bytes.Remove(sources[0].ForPath("updates/stable/latest.json").GetLeftPart(UriPartial.Path));
        Assert((await feed.CheckAsync("stable")).VersionCode==3,"pinned unexpired manifest works without authority");
        var untrusted=new UpdateFeed(wire,new(),_=>{},sources,()=>now);
        await RejectAsync(()=>untrusted.CheckAsync("stable"),"unsigned mirrors alone cannot establish a new update");
        var old=manifest with{PolicyRevision=7}; var oldPointer=Pointer(old);
        wire.Bytes[sources[0].ForPath("updates/stable/latest.json").GetLeftPart(UriPartial.Path)]=UpdateJson.Bytes(oldPointer);
        await RejectAsync(()=>feed.CheckAsync("stable"),"revision rollback rejected");

        foreach(var invalidPath in new[]{"../outside","/absolute","C:/Windows/file","folder\\file","folder/../file","file.","NUL.txt","a//b","a:b","a\n.txt"})
            Reject(()=>SafeUpdatePath.Relative(invalidPath),"unsafe package path "+invalidPath);
        Assert(SafeUpdatePath.Relative("engine/sing-box.exe")=="engine/sing-box.exe","normal package path");
        var arguments=MudlDownloadEngine.Arguments(manifest.Urls,@"C:\更新\download.zip",32,manifest.Sha256);
        Assert(arguments.Contains("32")&&arguments.Contains("json")&&arguments.Contains("--checksum=sha-256="+manifest.Sha256),"native 32-connection JSON and SHA arguments");
        Reject(()=>MudlDownloadEngine.Arguments(manifest.Urls,@"C:\a.zip",33,manifest.Sha256),"connection upper bound");
        Assert(MudlDownloadEngine.ParseProgress("{\"dl\":500,\"sz\":1000,\"sp\":200,\"th\":32,\"s\":\"dl\"}") is {Downloaded:500,BytesPerSecond:200,Connections:32},"native JSON progress parsed");
        Assert(MudlDownloadEngine.ParseProgress("{broken")==null&&MudlDownloadEngine.ParseProgress("ordinary log")==null,"native non-progress output ignored");

        var root=Path.Combine(Path.GetTempPath(),"NICRoute-update-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store=new UpdatePreferenceStore(root); store.Save(trust);
            Assert(store.Load().For("stable").HighestRevision==8,"trust survives restart");
            var payload=Encoding.UTF8.GetBytes("known update bytes");
            var downloadManifest=manifest with{Size=payload.Length,Sha256=UpdateJson.Hash(payload),Urls=[manifest.Urls[0],"https://mirror.example/update.zip"]};
            var fakeEngine=new FakeEngine(payload){CorruptFirst=true};
            var downloader=new UpdateDownloader(fakeEngine);
            var downloaded=await downloader.DownloadAsync(downloadManifest,root,32,null,CancellationToken.None);
            Assert(fakeEngine.Plans[0].Count==2&&fakeEngine.Calls==2,"parallel mirrors first; corrupt mixed bytes retry per source");
            Assert(await UpdateDownloader.MatchesAsync(downloaded,downloadManifest),"download accepted only after independent SHA");
            var before=fakeEngine.Calls;
            await downloader.DownloadAsync(downloadManifest,root,32,null,CancellationToken.None);
            Assert(fakeEngine.Calls==before,"valid cache reused after rehash");
            await File.WriteAllTextAsync(downloaded,"replaced after verification");
            Assert(!await UpdateDownloader.MatchesAsync(downloaded,downloadManifest),"tampering after download detected");
            await downloader.DownloadAsync(downloadManifest,root,32,null,CancellationToken.None);
            Assert(fakeEngine.Calls>before,"tampered cache redownloaded");

            var appDll=Path.Combine(Directory.GetCurrentDirectory(),"src","NICRoute.App","bin","Release","net10.0-windows","NICRoute.dll");
            if(File.Exists(appDll))
            {
                var identity=ProductIdentity.Read(appDll); Assert(identity.AppId=="NICRoute.Windows"&&identity.VersionCode>0,"identity read from actual PE metadata");
                Reject(()=>ProductIdentity.Read(typeof(UpdateTests).Assembly.Location),"another assembly cannot impersonate NICRoute");
                var stage=Path.Combine(root,"source"); Directory.CreateDirectory(stage);
                File.Copy(appDll,Path.Combine(stage,"NICRoute.dll"));
                foreach(var file in new[]{"NICRoute.exe","NICRoute.Updater.exe","NICRoute.Updater.dll","engine/sing-box.exe","engine/engine.lock.json"})
                { var target=SafeUpdatePath.InRoot(stage,file); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target,"new "+file); }
                var index=PackageVerifier.CreateIndex(stage); File.WriteAllBytes(Path.Combine(stage,PackageIndex.FileName),UpdateJson.Bytes(index));
                var archive=Path.Combine(root,"package.zip"); ZipFile.CreateFromDirectory(stage,archive);
                var packageManifest=manifest with{VersionCode=identity.VersionCode,VersionName=identity.VersionName,Size=new FileInfo(archive).Length,Sha256=await PackageVerifier.FileHashAsync(archive)};
                var extracted=Path.Combine(root,"extracted");
                var verified=await new PackageVerifier().ExtractAsync(archive,extracted,packageManifest);
                Assert(verified.VersionCode==identity.VersionCode,"whole package files and PE identity verified");
                await RejectAsync(()=>new PackageVerifier().ExtractAsync(archive,Path.Combine(root,"wrong-version"),packageManifest with{VersionCode=99}),"package version mismatch rejected");

                var install=Path.Combine(root,"install"); Directory.CreateDirectory(install);
                File.WriteAllText(Path.Combine(install,"NICRoute.exe"),"old version"); File.WriteAllText(Path.Combine(install,"my-custom-file.txt"),"keep me");
                var transaction=new FileUpdateTransaction(extracted,install,Path.Combine(root,"backup-success"),verified);
                await transaction.ApplyAsync();
                Assert(File.ReadAllText(Path.Combine(install,"NICRoute.exe"))=="new NICRoute.exe","transaction installs new managed files");
                Assert(File.ReadAllText(Path.Combine(install,"my-custom-file.txt"))=="keep me","user files preserved");
                transaction.Rollback();
                Assert(File.ReadAllText(Path.Combine(install,"NICRoute.exe"))=="old version"&&!File.Exists(Path.Combine(install,"NICRoute.dll")),"rollback restores old bytes and removes added files");
                var failing=new FileUpdateTransaction(extracted,install,Path.Combine(root,"backup-failure"),verified);
                await RejectAsync(()=>failing.ApplyAsync(new ThrowingProgress()),"simulated write-stage failure rolls back");
                Assert(File.ReadAllText(Path.Combine(install,"NICRoute.exe"))=="old version","failed installation leaves original executable");
                File.WriteAllText(Path.Combine(extracted,"NICRoute.exe"),"tampered stage");
                await RejectAsync(()=>PackageVerifier.VerifyDirectoryAsync(extracted,verified),"stage modification rejected before replacement");

                var unsafeZip=Path.Combine(root,"unsafe.zip");
                using(var zip=ZipFile.Open(unsafeZip,ZipArchiveMode.Create))
                { var entry=zip.CreateEntry("../outside.exe"); using var writer=new StreamWriter(entry.Open()); writer.Write("escape"); }
                await RejectAsync(()=>new PackageVerifier().ExtractAsync(unsafeZip,Path.Combine(root,"unsafe-stage"),packageManifest with{Size=new FileInfo(unsafeZip).Length,Sha256=PackageVerifier.FileHashAsync(unsafeZip).GetAwaiter().GetResult()}),"ZIP traversal blocked before extraction");
                Assert(!File.Exists(Path.Combine(root,"outside.exe")),"traversal writes no outside file");
            }
        }
        finally
        {
            var full=Path.GetFullPath(root); var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(full.StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("NICRoute-update-tests-")) Directory.Delete(full,true);
        }
        return count;
    }
    private sealed class FakeTransport : IUpdateTransport
    {
        public Dictionary<string,byte[]> Bytes {get;}=[];
        public Task<byte[]> GetBytesAsync(Uri url,int maximumBytes,CancellationToken token)
            =>Bytes.TryGetValue(url.GetLeftPart(UriPartial.Path),out var value)?Task.FromResult(value):Task.FromException<byte[]>(new HttpRequestException("offline"));
        public Task DownloadAsync(Uri url,string destination,long expectedSize,IProgress<long>? progress,CancellationToken token)=>throw new NotSupportedException();
    }
    private sealed class FakeEngine(byte[] payload) : IUpdateDownloadEngine
    {
        public bool CorruptFirst; public int Calls; public List<IReadOnlyList<string>> Plans {get;}=[];
        public async Task DownloadAsync(IReadOnlyList<string> urls,string output,int connections,string sha256,long expectedSize,IProgress<DownloadProgress>? progress,CancellationToken token)
        { Calls++; Plans.Add(urls); await File.WriteAllBytesAsync(output,CorruptFirst&&Calls==1?Encoding.UTF8.GetBytes("bad"):payload,token); }
    }
    private sealed class ThrowingProgress : IProgress<string> { private int calls; public void Report(string value) { if(++calls==4) throw new IOException("simulated disk failure"); } }
}
