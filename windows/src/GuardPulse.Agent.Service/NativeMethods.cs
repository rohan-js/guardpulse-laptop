// P/Invoke surface for the device service: process suspension/resume (ntdll),
// interactive session enumeration (WTS) and launching the per-user session agent
// into a user session (CreateProcessAsUser). All handles are pointer-sized (x64 safe).

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GuardPulse.Agent.Service;

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    // ---- process access rights ----
    public const int PROCESS_TERMINATE = 0x0001;
    public const int PROCESS_SUSPEND_RESUME = 0x0800;
    public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // ---- token rights / privileges ----
    public const int TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const int TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    // ---- WTS_CONNECTSTATE_CLASS ----
    public const int WTS_ACTIVE = 0x0;

    // ---- process creation flags ----
    public const int CREATE_UNICODE_ENVIRONMENT = 0x0400;
    public const int STARTF_USESHOWWINDOW = 0x00000001;
    public const short SW_SHOWNORMAL = 1;

    // ---------------------------------------------------------------- ntdll
    [LibraryImport("ntdll.dll")]
    internal static partial uint NtSuspendProcess(IntPtr processHandle);

    [LibraryImport("ntdll.dll")]
    internal static partial uint NtResumeProcess(IntPtr processHandle);

    // ------------------------------------------------------------- kernel32
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(
        int dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll")]
    internal static partial int WTSGetActiveConsoleSessionId();

    // -------------------------------------------------------------- wtsapi32
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSEnumerateSessionsW(
        IntPtr hServer,
        int Reserved,
        int dwVersion,
        out IntPtr ppSessionInfo,
        out int pcCount);

    [LibraryImport("wtsapi32.dll")]
    internal static partial void WTSFreeMemory(IntPtr pMemory);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQueryUserToken(int sessionId, out IntPtr phToken);

    // -------------------------------------------------------------- advapi32
    // Classic DllImport: the source generator cannot marshal STARTUPINFOW's
    // embedded string pointers (SYSLIB1051).
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessAsUserW(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        int dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(
        IntPtr processHandle,
        int desiredAccess,
        out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupPrivilegeValueW(
        [MarshalAs(UnmanagedType.LPWStr)] string? lpSystemName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpName,
        out LUID lpLuid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    // --------------------------------------------------------------- userenv
    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateEnvironmentBlock(
        out IntPtr lpEnvironment,
        IntPtr hToken,
        [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    // ---------------------------------------------------------------- structs
    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOW
    {
        public int cb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpReserved;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDesktop;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    // -------------------------------------------------------------- helpers
    /// <summary>Session ids of currently active (interactive) sessions.</summary>
    internal static List<int> GetInteractiveSessionIds()
    {
        var result = new List<int>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var pSessions, out var count))
        {
            return result;
        }

        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(nint.Add(pSessions, i * size));
                if (info.State == WTS_ACTIVE)
                {
                    result.Add(info.SessionId);
                }
            }
        }
        finally
        {
            WTSFreeMemory(pSessions);
        }

        return result;
    }

    /// <summary>Enables a privilege (e.g. SeAssignPrimaryTokenPrivilege) on our own process token.</summary>
    internal static void EnablePrivilege(string privilegeName)
    {
        using var self = Process.GetCurrentProcess();
        if (!OpenProcessToken(self.Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            return;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, privilegeName, out var luid))
            {
                return;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
            };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
