namespace WinSystemHelper;

public interface IWindowsSessionProcessRunner
{
    string GetActiveConsoleSessionStatus();

    string? TryGetActiveUserSid();

    void StartProcess(string fileName, string arguments);

    Task<uint> StartProcessAndWaitAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    void StartWindowsPowerShell(string arguments);

    void StartEncodedPowerShell(string script);

    Task<uint> StartEncodedPowerShellAndWaitAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
