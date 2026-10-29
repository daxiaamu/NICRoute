using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NICRoute.Core;

/// <summary>Own a hidden, private console so Ctrl+C can shut down sing-box and undo
/// TUN configuration. Never send control events into a terminal inherited from a caller.</summary>
internal sealed class EngineProcess : IAsyncDisposable
{
    private readonly NativeLifetime job = new();
    private StreamReader? output;
    private Task? reading;
    public Process Process { get; private set; } = null!;
    public bool GracefulExit { get; private set; }

    public void Start(string executable, string config, string directory, Action<string> log)
    {
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        if (!CreatePipe(out var readPipe, out var writePipe, ref attributes, 0)) throw new Win32Exception();
        using (writePipe)
        using (var input = CreateFile("NUL", 0x80000000, 3, ref attributes, 3, 0, IntPtr.Zero))
        {
            if (!SetHandleInformation(readPipe, 1, 0)) { readPipe.Dispose(); throw new Win32Exception(); }
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x101,
                ShowWindow = 0, StdInput = input.DangerousGetHandle(), StdOutput = writePipe.DangerousGetHandle(), StdError = writePipe.DangerousGetHandle() };
            var command = new StringBuilder($"\"{executable}\" run -c \"{config}\"");
            // CREATE_NEW_CONSOLE | CREATE_SUSPENDED: assign the job before any engine code runs.
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x14,
                    IntPtr.Zero, directory, ref startup, out var info))
            { readPipe.Dispose(); throw new Win32Exception(); }
            try
            {
                Process = Process.GetProcessById((int)info.ProcessId);
                job.Add(Process);
                output = new StreamReader(new FileStream(readPipe, FileAccess.Read), Encoding.UTF8);
                if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception();
                reading = ReadAsync(log);
            }
            catch
            {
                TerminateProcess(info.Process, 1);
                readPipe.Dispose();
                throw;
            }
            finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
        }
    }
    private async Task ReadAsync(Action<string> log)
    {
        try { while (output is not null && await output.ReadLineAsync() is { } line) log(line); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
    private static bool InterruptPrivateConsole(int childId)
    {
        // GUI host normally has no console. Console-hosted tests use the kill fallback.
        if (GetConsoleWindow() != IntPtr.Zero || !AttachConsole((uint)childId)) return false;
        try
        {
            var members = new uint[8];
            var count = GetConsoleProcessList(members, (uint)members.Length);
            if (count == 0 || count > members.Length || members.Take((int)count).Any(pid => pid != childId && pid != Environment.ProcessId))
                return false;
            if (!SetConsoleCtrlHandler(IntPtr.Zero, true)) return false;
            return GenerateConsoleCtrlEvent(0, 0);
        }
        finally
        {
            // FreeConsole resets this process' control-handler state. The private console
            // remains alive until its sole engine process finishes graceful cleanup.
            FreeConsole();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Process is not null)
        {
            if (!Process.HasExited)
            {
                var signaled = InterruptPrivateConsole(Process.Id);
                if (signaled)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await Process.WaitForExitAsync(timeout.Token); GracefulExit = true; }
                    catch (OperationCanceledException) { }
                }
                if (!Process.HasExited) Process.Kill(true);
                await Process.WaitForExitAsync();
            }
            if (reading is not null) await reading;
            Process.Dispose();
        }
        output?.Dispose();
        job.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedLength; public IntPtr ReservedBytes, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string file, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList([Out] uint[] list, uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GenerateConsoleCtrlEvent(uint control, uint group);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeConsole();
}
