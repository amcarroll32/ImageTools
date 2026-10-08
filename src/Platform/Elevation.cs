using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ImageTools.Platform;

/// <summary>
/// Administrator detection, relaunching elevated, and the backup privilege that lets an
/// elevated process list folders even admins are denied (System Volume Information,
/// other users' profiles, …). The scanner only ever reads.
/// </summary>
public static class Elevation
{
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x08;
    private const uint SE_PRIVILEGE_ENABLED = 0x02;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;
    private const int ERROR_CANCELLED = 1223;

    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Enables SeBackupPrivilege for this process. Directory listing in .NET opens folders with
    /// FILE_FLAG_BACKUP_SEMANTICS, so with this privilege enabled, read access checks are bypassed.
    /// </summary>
    public static bool TryEnableBackupPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeBackupPrivilege", out var luid))
                return false;
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                return false;
            // AdjustTokenPrivileges "succeeds" even when the token doesn't hold the privilege.
            return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public enum RelaunchResult
    {
        Started,
        Cancelled,
        Failed,
    }

    /// <summary>Starts a second copy of this app through the UAC prompt.</summary>
    public static RelaunchResult RelaunchElevated(out string? error)
    {
        error = null;
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the app's executable.");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            return RelaunchResult.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return RelaunchResult.Cancelled;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return RelaunchResult.Failed;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);
}
