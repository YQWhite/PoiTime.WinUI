using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace PoiTime.StartupService;

internal sealed partial class PoiTimeStartupService : ServiceBase
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint MaximumAllowed = 0x02000000;
    private readonly string _appPath;
    private readonly HashSet<int> _launchedSessions = new();
    private readonly object _syncRoot = new();

    public PoiTimeStartupService(string appPath)
    {
        _appPath = appPath;
        ServiceName = "PoiTimeStartup";
        CanHandleSessionChangeEvent = true;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        try
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId != uint.MaxValue)
                TryLaunch((int)sessionId);
        }
        catch (Exception ex)
        {
            // Keep the service running so it can still launch the app on the next SessionLogon event.
            TryWriteEventLog($"Failed to launch PoiTime on service start: {ex}");
        }
    }

    private void TryWriteEventLog(string message)
    {
        try
        {
            EventLog.WriteEntry(message, EventLogEntryType.Warning);
        }
        catch
        {
        }
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        if (changeDescription.Reason == SessionChangeReason.SessionLogon)
            TryLaunch(changeDescription.SessionId);
        else if (changeDescription.Reason == SessionChangeReason.SessionLogoff)
        {
            lock (_syncRoot)
                _launchedSessions.Remove(changeDescription.SessionId);
        }
    }

    private void TryLaunch(int sessionId)
    {
        lock (_syncRoot)
        {
            if (!_launchedSessions.Add(sessionId))
                return;
        }

        try
        {
            if (IsAppRunning(sessionId))
                return;

            LaunchInSession(sessionId);
        }
        catch (Exception ex)
        {
            lock (_syncRoot)
                _launchedSessions.Remove(sessionId);
            TryWriteEventLog($"Failed to launch PoiTime in session {sessionId}: {ex}");
        }
    }

    private bool IsAppRunning(int sessionId)
    {
        string processName = Path.GetFileNameWithoutExtension(_appPath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == sessionId)
                        return true;
                }
                catch
                {
                }
            }
        }
        return false;
    }

    private void LaunchInSession(int sessionId)
    {
        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;

        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonationLevel.SecurityImpersonation,
                    TokenType.TokenPrimary, out primaryToken))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var startupInfo = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = "winsta0\\default"
            };
            string commandLine = $"\"{_appPath}\" /autostart";
            string workingDirectory = Path.GetDirectoryName(_appPath) ?? AppContext.BaseDirectory;

            if (!CreateProcessAsUser(primaryToken, _appPath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment, environment, workingDirectory, ref startupInfo, out var processInfo))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
        }
        finally
        {
            if (environment != IntPtr.Zero)
                DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero)
                CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero)
                CloseHandle(userToken);
        }
    }

    private enum SecurityImpersonationLevel
    {
        SecurityAnonymous,
        SecurityIdentification,
        SecurityImpersonation,
        SecurityDelegation
    }

    private enum TokenType
    {
        TokenPrimary = 1,
        TokenImpersonation
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string lpDesktop;
        public string? lpTitle;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    // WTSGetActiveConsoleSessionId is exported by kernel32.dll, not wtsapi32.dll.
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        SecurityImpersonationLevel impersonationLevel, TokenType tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(IntPtr token, string applicationName, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
