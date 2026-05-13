namespace WinSystemHelper;

public interface IProcessRunner
{
    Task<ProcessCaptureResult> CaptureAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
