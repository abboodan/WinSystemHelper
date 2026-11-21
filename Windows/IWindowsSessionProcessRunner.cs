namespace WinSystemHelper;

public sealed record WindowsConsoleSession(uint Id, string UserSid);

public interface IWindowsSessionProcessRunner
{
    string GetActiveConsoleSessionStatus();

    string? TryGetActiveUserSid();
    WindowsConsoleSession? TryGetActiveConsoleSession();

    void StartProcess(string fileName, string arguments);

    Task<uint> StartProcessAndWaitAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        WindowsConsoleSession? requiredSession = null);

    void StartWindowsPowerShell(string arguments);

    void StartEncodedPowerShell(string script);

    Task<uint> StartEncodedPowerShellAndWaitAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        WindowsConsoleSession? requiredSession = null);
}
