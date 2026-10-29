using NICRoute.Core.Updates;
using System.Text;
if(args.Length!=2 || args[0] is not ("inspect" or "index" or "validate-feed" or "check-live")) return 2;
if(args[0]=="inspect") Console.WriteLine(Encoding.UTF8.GetString(UpdateJson.Bytes(ProductIdentity.Read(args[1]))));
else if(args[0]=="validate-feed")
{
    var pointer=UpdateJson.Read<UpdatePointer>(File.ReadAllBytes(Path.Combine(args[1],"latest.json")));
    var bytes=File.ReadAllBytes(Path.Combine(args[1],"manifest.json"));
    pointer.Validate(pointer.Channel,DateTimeOffset.UtcNow);
    if(UpdateJson.Hash(bytes)!=pointer.ManifestSha256) throw new InvalidDataException("Manifest digest mismatch.");
    UpdateJson.Read<UpdateManifest>(bytes).Validate(pointer);
    Console.WriteLine("Update feed schema and integrity validated.");
}
else if(args[0]=="check-live")
{
    using var http=new UpdateHttp();
    var store=new UpdatePreferenceStore(Path.GetFullPath(args[1]));
    var feed=new UpdateFeed(http,store.Load(),store.Save);
    var manifest=await feed.CheckAsync("stable");
    Console.WriteLine(Encoding.UTF8.GetString(UpdateJson.Bytes(manifest)));
}
else
{
    var index=PackageVerifier.CreateIndex(args[1]);
    File.WriteAllBytes(Path.Combine(args[1],PackageIndex.FileName),UpdateJson.Bytes(index));
    Console.WriteLine($"Package inventory: {index.Files.Length} files, versionCode={index.VersionCode}");
}
return 0;
