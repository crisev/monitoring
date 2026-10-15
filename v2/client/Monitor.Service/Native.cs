using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Monitor.Core;

namespace Monitor.Service;

/// <summary>Win32 calls the service needs: user sessions, user tokens, starting the agent, process info.</summary>
internal static class Native
{
    // ---- Sessions (wtsapi32) ----

    public enum WtsState { Active = 0, Connected = 1, ConnectQuery = 2, Shadow = 3, Disconnected = 4, Idle = 5, Listen = 6, Reset = 7, Down = 8, Init = 9 }

    private const int WTSUserName = 5;
    private const int WTSDomainName = 7;
    private const int WTSSessionInfoEx = 25;

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public WtsState State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    public static List<(int Id, WtsState State)> EnumerateSessions()
    {
        var result = new List<(int, WtsState)>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count)) return result;
        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                result.Add((info.SessionId, info.State));
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
        return result;
    }

    public static string QuerySessionString(int sessionId, bool domain)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, domain ? WTSDomainName : WTSUserName, out var buffer, out _)) return "";
        try { return Marshal.PtrToStringUni(buffer) ?? ""; }
        finally { WTSFreeMemory(buffer); }
    }

    /// <summary>True if the session's desktop is locked (WTSINFOEX_LEVEL1.SessionFlags == WTS_SESSIONSTATE_LOCK).</summary>
    public static bool IsSessionLocked(int sessionId)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTSSessionInfoEx, out var buffer, out var bytes)) return false;
        try
        {
            // WTSINFOEXW { DWORD Level; WTSINFOEX_LEVEL1_W Data; }. Data holds LARGE_INTEGERs, so it is 8-byte aligned:
            // SessionId at offset 8, SessionState at 12, SessionFlags at 16.
            if (bytes < 20 || Marshal.ReadInt32(buffer, 0) != 1) return false;
            return Marshal.ReadInt32(buffer, 16) == 0; // 0 = WTS_SESSIONSTATE_LOCK, 1 = UNLOCK, -1 = unknown
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>The primary token of the user logged on to a session (requires SYSTEM). Null if nobody is logged on.</summary>
    public static SafeAccessTokenHandle? QueryUserToken(int sessionId) =>
        WTSQueryUserToken(sessionId, out var token) ? new SafeAccessTokenHandle(token) : null;

    // ---- Tokens (advapi32) ----

    private const int TokenElevationType = 18;
    public const int TokenElevationTypeFull = 2;
    public const int TokenElevationTypeLimited = 3;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returnLength);

    /// <summary>1 = default (no split token), 2 = full (elevated), 3 = limited (UAC-filtered admin).</summary>
    public static int GetElevationType(SafeAccessTokenHandle token) =>
        GetTokenInformation(token, TokenElevationType, out var type, sizeof(int), out _) ? type : 0;

    // ---- Starting the agent as the user ----

    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    /// <summary>Starts <paramref name="exePath"/> as the user logged on to <paramref name="sessionId"/>, on their desktop. Returns the PID.</summary>
    public static int StartAsSessionUser(int sessionId, string exePath)
    {
        using var userToken = QueryUserToken(sessionId) ?? throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken");
        if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");
        IntPtr env = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out env, primary, false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock");
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = new StringBuilder($"\"{exePath}\"");
            if (!CreateProcessAsUserW(primary, exePath, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_PROCESS_GROUP, env, Path.GetDirectoryName(exePath), ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            CloseHandle(primary);
        }
    }

    // ---- Processes (kernel32) ----

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public int th32ModuleID;
        public int cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(int processId, out int sessionId);

    /// <summary>Full path of a process's executable, or null (works for protected processes too).</summary>
    public static string? ProcessPath(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static int? SessionOf(int pid) => ProcessIdToSessionId(pid, out var s) ? s : null;

    public static int? PipeClientProcessId(SafePipeHandle pipe) =>
        GetNamedPipeClientProcessId(pipe, out var pid) ? pid : null;

    /// <summary>All processes with their parent PID (Toolhelp snapshot, well under a millisecond).</summary>
    public static Dictionary<int, ProcessEntry> ProcessTree()
    {
        var tree = new Dictionary<int, ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return tree;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return tree;
            do
            {
                tree[entry.th32ProcessID] = new ProcessEntry(entry.th32ProcessID, entry.th32ParentProcessID, ProcessRules.NormalizeName(entry.szExeFile));
            } while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return tree;
    }
}
