using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NICRoute.Core;

/// <summary>Dynamic WFP ALE filters prevent configured apps bypassing the TUN through
/// Windows routes or explicit physical-interface binds. The forwarding engine itself
/// remains separately bound to each selected outbound interface. No Internet probes.</summary>
public sealed class ApplicationNetworkGuard : IDisposable
{
    private static readonly Guid V4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82"), V6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid AppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971"), PackageId = new("71bc78fa-f17c-4997-a602-6abb261f351c");
    private static readonly Guid NextInterface = new("93ae8f5b-7f6f-4719-98c8-14e97429ef04"), Flags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private readonly Guid sublayer = Guid.NewGuid();
    private readonly Target[] targets;
    private readonly List<ulong> filters = [];
    private WfpHandle? handle;
    private readonly bool preserveLoopback;
    public int FilterCount => filters.Count;
    public sealed record Target(string Name, string Identity, bool IsPackage);
    public static Target[] Targets(AppSettings settings) => settings.Rules.Where(r => r.Enabled).Select(r =>
        new Target(r.Application.Name, r.Application.Kind == ApplicationKind.WindowsPackage ? r.Application.PackageFamilyName : r.Application.ExecutablePath,
            r.Application.Kind == ApplicationKind.WindowsPackage)).DistinctBy(t => (t.IsPackage, t.Identity.ToLowerInvariant())).ToArray();
    public ApplicationNetworkGuard(IEnumerable<Target> targets, bool preserveLoopback = true)
    {
        this.targets = targets.ToArray(); this.preserveLoopback = preserveLoopback;
        ValidateLayout();
        var session = new Session { Key = Guid.NewGuid(), Display = new() { Name = "NICRoute application interface guard" }, Flags = 1 };
        Check(FwpmEngineOpen0(null, 10, IntPtr.Zero, ref session, out var opened), "打开 Windows 过滤引擎");
        handle = opened;
        try
        {
            var layer = new SubLayer { Key = sublayer, Display = new() { Name = "NICRoute application interface guard" }, Weight = ushort.MaxValue };
            Check(FwpmSubLayerAdd0(handle, ref layer, IntPtr.Zero), "创建网卡约束层");
            Replace(null);
        }
        catch { Dispose(); throw; }
    }
    public static void ValidateLayout()
    {
        if (IntPtr.Size != 8 || Marshal.SizeOf<Filter>() != 200 || Marshal.SizeOf<Condition>() != 40 ||
            Marshal.SizeOf<Session>() != 72 || Marshal.SizeOf<SubLayer>() != 72)
            throw new PlatformNotSupportedException("WFP 网卡约束需要 Windows x64。");
    }
    public static ulong InterfaceLuid(string name)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals(name, StringComparison.Ordinal));
        if (nic is null || !Guid.TryParse(nic.Id, out var id)) throw new IOException("没有找到网卡：" + name);
        Check(ConvertInterfaceGuidToLuid(ref id, out var luid), "读取网卡标识");
        if (luid == 0) throw new IOException("网卡标识为空，不能解除出网限制。");
        return luid;
    }
    public void AllowInterface(string name) => Replace(InterfaceLuid(name));
    public void Replace(ulong? allowedInterface)
    {
        if (handle is null || handle.IsClosed) throw new ObjectDisposedException(nameof(ApplicationNetworkGuard));
        Check(FwpmTransactionBegin0(handle, 0), "开始网卡约束事务");
        var added = new List<ulong>();
        try
        {
            foreach (var id in filters) Check(FwpmFilterDeleteById0(handle, id), "更新旧网卡约束");
            foreach (var target in targets)
            {
                IntPtr identity = IntPtr.Zero;
                try
                {
                    if (target.IsPackage)
                    {
                        var result = DeriveAppContainerSidFromAppContainerName(target.Identity, out identity);
                        if (result < 0) Marshal.ThrowExceptionForHR(result);
                    }
                    else Check(FwpmGetAppIdFromFileName0(target.Identity, out identity), "读取应用身份：" + target.Name);
                    var conditions = new List<Condition> { new() { Key = target.IsPackage ? PackageId : AppId,
                        Value = new() { Type = target.IsPackage ? 13u : 12u, Pointer = identity } } };
                    if (preserveLoopback) conditions.Add(new() { Key = Flags, Match = 8, Value = new() { Type = 3, Number = 1 } });
                    var luidMemory = IntPtr.Zero;
                    var conditionMemory = IntPtr.Zero;
                    try
                    {
                        if (allowedInterface is { } luid)
                        {
                            if (luid == 0) throw new InvalidDataException("不能允许空网卡标识。");
                            luidMemory = Marshal.AllocHGlobal(8); Marshal.WriteInt64(luidMemory, unchecked((long)luid));
                            conditions.Add(new() { Key = NextInterface, Match = 10, Value = new() { Type = 4, Pointer = luidMemory } });
                        }
                        conditionMemory = Marshal.AllocHGlobal(conditions.Count * Marshal.SizeOf<Condition>());
                        for (var i = 0; i < conditions.Count; i++) Marshal.StructureToPtr(conditions[i], conditionMemory + i * Marshal.SizeOf<Condition>(), false);
                        foreach (var layer in new[] { V4, V6 })
                        {
                            var filter = new Filter { Key = Guid.NewGuid(), Display = new() { Name = "NICRoute · " + target.Name }, Layer = layer,
                                SubLayer = sublayer, Weight = new() { Type = 1, Number = 15 }, Count = (uint)conditions.Count,
                                Conditions = conditionMemory, Action = new() { Type = 0x1001 } };
                            Check(FwpmFilterAdd0(handle, ref filter, IntPtr.Zero, out var id), "安装应用网卡约束：" + target.Name); added.Add(id);
                        }
                    }
                    finally { if (conditionMemory != IntPtr.Zero) Marshal.FreeHGlobal(conditionMemory); if (luidMemory != IntPtr.Zero) Marshal.FreeHGlobal(luidMemory); }
                }
                finally
                {
                    if (identity != IntPtr.Zero) { if (target.IsPackage) FreeSid(identity); else FwpmFreeMemory0(ref identity); }
                }
            }
            Check(FwpmTransactionCommit0(handle), "提交应用网卡约束");
            filters.Clear(); filters.AddRange(added);
        }
        catch { _ = FwpmTransactionAbort0(handle); throw; }
    }
    private static void Check(uint result, string action)
    {
        if (result != 0) throw new Win32Exception(unchecked((int)result), action + "失败 (0x" + result.ToString("X8") + ")。未能建立约束时不会继续启动分流。");
    }
    public void Dispose() { handle?.Dispose(); handle = null; filters.Clear(); }
    private sealed class WfpHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public WfpHandle() : base(true) { }
        protected override bool ReleaseHandle() => FwpmEngineClose0(handle) == 0;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Display
    { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; [MarshalAs(UnmanagedType.LPWStr)] public string? Description; }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct Value
    { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public IntPtr Pointer; [FieldOffset(8)] public uint Number; }
    [StructLayout(LayoutKind.Sequential)] private struct Condition { public Guid Key; public uint Match; public Value Value; }
    [StructLayout(LayoutKind.Sequential)] private struct ActionValue { public uint Type; public Guid Key; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct ContextValue { [FieldOffset(0)] public ulong Value; [FieldOffset(0)] public Guid Key; }
    [StructLayout(LayoutKind.Sequential)] private struct Filter
    {
        public Guid Key; public Display Display; public uint Flags; public IntPtr Provider; public Blob ProviderData;
        public Guid Layer, SubLayer; public Value Weight; public uint Count; public IntPtr Conditions; public ActionValue Action;
        public ContextValue Context; public IntPtr Reserved; public ulong Id; public Value EffectiveWeight;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Session
    { public Guid Key; public Display Display; public uint Flags, Timeout, ProcessId; public IntPtr Sid, User; public int KernelMode; }
    [StructLayout(LayoutKind.Sequential)] private struct SubLayer
    { public Guid Key; public Display Display; public uint Flags; public IntPtr Provider; public Blob Data; public ushort Weight; }
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] private static extern uint FwpmEngineOpen0(string? server, uint authentication, IntPtr identity, ref Session session, out WfpHandle engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerAdd0(WfpHandle engine, ref SubLayer layer, IntPtr security);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionBegin0(WfpHandle engine, uint flags);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionCommit0(WfpHandle engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionAbort0(WfpHandle engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterAdd0(WfpHandle engine, ref Filter filter, IntPtr security, out ulong id);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterDeleteById0(WfpHandle engine, ulong id);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] private static extern uint FwpmGetAppIdFromFileName0(string path, out IntPtr id);
    [DllImport("fwpuclnt.dll")] private static extern void FwpmFreeMemory0(ref IntPtr value);
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceGuidToLuid(ref Guid id, out ulong luid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
}
