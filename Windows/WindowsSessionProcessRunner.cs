using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace WinSystemHelper;

public sealed class WindowsSessionProcessRunner : IWindowsSessionProcessRunner
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ActiveConsoleSessionUnavailable = 0xFFFFFFFF;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    public string GetActiveConsoleSessionStatus()
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        return sessionId == ActiveConsoleSessionUnavailable
            ? "Unavailable"
            : $"Available (Session {sessionId})";
    }

    public string? TryGetActiveUserSid()
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == ActiveConsoleSessionUnavailable)
        {
            return null;
        }

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
        {
            return null;
        }

        try
        {
            using WindowsIdentity identity = new(userToken);
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandleIfNeeded(userToken);
        }
    }

    public void StartProcess(string fileName, string arguments)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == ActiveConsoleSessionUnavailable)
        {
            throw new InvalidOperationException("No active console user session is available.");
        }

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query the active user token.");
        }

        IntPtr environment = IntPtr.Zero;
        try
        {
            uint creationFlags = CreateNoWindow;
            if (CreateEnvironmentBlock(out environment, userToken, inherit: false))
            {
                creationFlags |= CreateUnicodeEnvironment;
            }

            string executablePath = ResolveExecutablePath(fileName);
            string commandLine = $"\"{executablePath}\" {arguments}";
            StartupInfo startupInfo = new()
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = "winsta0\\default"
            };

            if (!CreateProcessAsUser(
                    userToken,
                    applicationName: null,
                    commandLine: commandLine,
                    processAttributes: IntPtr.Zero,
                    threadAttributes: IntPtr.Zero,
                    inheritHandles: false,
                    creationFlags: creationFlags,
                    environment: environment,
                    currentDirectory: Environment.SystemDirectory,
                    startupInfo: ref startupInfo,
                    processInformation: out ProcessInformation processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to start process in the active user session.");
            }

            CloseHandleIfNeeded(processInformation.hThread);
            CloseHandleIfNeeded(processInformation.hProcess);
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            CloseHandleIfNeeded(userToken);
        }
    }

    public async Task<uint> StartProcessAndWaitAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == ActiveConsoleSessionUnavailable)
        {
            throw new InvalidOperationException("No active console user session is available.");
        }

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query the active user token.");
        }

        IntPtr environment = IntPtr.Zero;
        ProcessInformation processInformation = default;

        try
        {
            uint creationFlags = CreateNoWindow;
            if (CreateEnvironmentBlock(out environment, userToken, inherit: false))
            {
                creationFlags |= CreateUnicodeEnvironment;
            }

            string executablePath = ResolveExecutablePath(fileName);
            string commandLine = $"\"{executablePath}\" {arguments}";
            StartupInfo startupInfo = new()
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = "winsta0\\default"
            };

            if (!CreateProcessAsUser(
                    userToken,
                    applicationName: null,
                    commandLine: commandLine,
                    processAttributes: IntPtr.Zero,
                    threadAttributes: IntPtr.Zero,
                    inheritHandles: false,
                    creationFlags: creationFlags,
                    environment: environment,
                    currentDirectory: Path.GetDirectoryName(executablePath),
                    startupInfo: ref startupInfo,
                    processInformation: out processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to start process in the active user session.");
            }

            uint waitResult = await Task.Run(
                () => WaitForSingleObject(processInformation.hProcess, (uint)timeout.TotalMilliseconds),
                cancellationToken);

            if (waitResult == WaitTimeout)
            {
                TerminateProcessIfNeeded(processInformation.hProcess);
                throw new TimeoutException("Active user process timed out.");
            }

            if (waitResult != WaitObject0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed while waiting for active user process.");
            }

            if (!GetExitCodeProcess(processInformation.hProcess, out uint exitCode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read active user process exit code.");
            }

            return exitCode;
        }
        finally
        {
            CloseHandleIfNeeded(processInformation.hThread);
            CloseHandleIfNeeded(processInformation.hProcess);

            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            CloseHandleIfNeeded(userToken);
        }
    }

    public void StartWindowsPowerShell(string arguments)
    {
        StartProcess(GetWindowsPowerShellPath(), arguments);
    }

    public void StartEncodedPowerShell(string script)
    {
        StartWindowsPowerShell(BuildEncodedPowerShellArguments(script));
    }

    public Task<uint> StartEncodedPowerShellAndWaitAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return StartProcessAndWaitAsync(
            GetWindowsPowerShellPath(),
            BuildEncodedPowerShellArguments(script),
            timeout,
            cancellationToken);
    }

    private static string BuildEncodedPowerShellArguments(string script)
    {
        string base64Script = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {base64Script}";
    }

    private static string ResolveExecutablePath(string fileName)
    {
        return Path.IsPathRooted(fileName)
            ? fileName
            : Path.Combine(Environment.SystemDirectory, fileName);
    }

    private static string GetWindowsPowerShellPath()
    {
        string powerShellPath = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        if (!File.Exists(powerShellPath))
        {
            throw new InvalidOperationException($"PowerShell was not found at {powerShellPath}.");
        }

        return powerShellPath;
    }

    private static void CloseHandleIfNeeded(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = CloseHandle(handle);
        }
    }

    private static void TerminateProcessIfNeeded(IntPtr process)
    {
        if (process != IntPtr.Zero)
        {
            _ = TerminateProcess(process, exitCode: 1);
        }
    }
}
