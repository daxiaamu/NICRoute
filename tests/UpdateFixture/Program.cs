using System.Reflection;
#if FAIL_STARTUP
return 42;
#else
var dataIndex=Array.IndexOf(args,"--data-dir");
if(dataIndex<0||dataIndex+1>=args.Length) return 2;
var directory=Path.GetFullPath(args[dataIndex+1]); Directory.CreateDirectory(directory);
var version=Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().Single(a=>a.Key=="NICRoute.VersionCode").Value!;
File.WriteAllText(Path.Combine(directory,"fixture-version.txt"),version);
var sessionIndex=Array.IndexOf(args,"--update-session");
if(sessionIndex>=0&&sessionIndex+1<args.Length&&Guid.TryParseExact(args[sessionIndex+1],"N",out var session))
{
    var root=Path.Combine(directory,"updates",session.ToString("N"));Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root,"app-ready"),"ready");
}
return 0;
#endif
