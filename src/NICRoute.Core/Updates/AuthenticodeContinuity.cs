using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NICRoute.Core.Updates;

public static class AuthenticodeContinuity
{
    public static void Verify(string installedExecutable, string candidateExecutable)
    {
        if (!HasSignature(installedExecutable)) return;
        var oldSigner = TrustedSigner(installedExecutable);
        var newSigner = TrustedSigner(candidateExecutable);
        if (oldSigner != newSigner) throw new InvalidDataException("更新程序的签名发布者与当前版本不一致。");
    }
    private static bool HasSignature(string path)
    {
        using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
        return pe.PEHeaders.PEHeader?.CertificateTableDirectory.Size > 0;
    }
    private static string TrustedSigner(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = Marshal.StringToCoTaskMemUni(path) };
        var pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer, ProviderFlags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) throw new InvalidDataException("程序数字签名校验未通过。");
            // X509CertificateLoader does not extract certificates from Authenticode PE
            // containers. WinVerifyTrust has already validated this signed executable.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.GetCertHashString(HashAlgorithmName.SHA256);
        }
        finally { Marshal.FreeCoTaskMem(pointer); Marshal.FreeCoTaskMem(file.Path); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct TrustFile { public uint Size; public IntPtr Path, Handle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public IntPtr PolicyCallback, SipClient; public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File; public uint StateAction; public IntPtr StateData, UrlReference; public uint ProviderFlags, UiContext; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
