using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AllsioPush.Models;

namespace AllsioPush.Services;

public static class CredentialManager
{
    private const string CredentialTarget = "AllsioPush_AuthSession";

    public static void SaveSession(AuthSession session)
    {
        var json = JsonSerializer.Serialize(session);
        var bytes = Encoding.UTF8.GetBytes(json);

        var cred = new CREDENTIAL
        {
            Type = CRED_TYPE.GENERIC,
            TargetName = CredentialTarget,
            CredentialBlob = Marshal.AllocHGlobal(bytes.Length),
            CredentialBlobSize = (uint)bytes.Length,
            Persist = CRED_PERSIST.LOCAL_MACHINE,
        };

        Marshal.Copy(bytes, 0, cred.CredentialBlob, bytes.Length);

        try
        {
            CredWrite(ref cred, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(cred.CredentialBlob);
        }
    }

    public static AuthSession? LoadSession()
    {
        if (!CredRead(CredentialTarget, CRED_TYPE.GENERIC, 0, out var credPtr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            var json = Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize<AuthSession>(json);
        }
        catch
        {
            return null;
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public static void ClearSession()
    {
        CredDelete(CredentialTarget, CRED_TYPE.GENERIC, 0);
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref CREDENTIAL credential, [In] uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, CRED_TYPE type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, CRED_TYPE type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private enum CRED_TYPE : uint { GENERIC = 1 }
    private enum CRED_PERSIST : uint { LOCAL_MACHINE = 2 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public CRED_TYPE Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CRED_PERSIST Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
