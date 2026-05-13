using System.Diagnostics;

namespace WinSystemHelper;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessCaptureResult> CaptureAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        };

        try
        {
            process.Start();

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token);

            return new ProcessCaptureResult(
                process.ExitCode,
                await outputTask,
                await errorTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            throw new TimeoutException($"{fileName} timed out after {timeout}.");
        }
    }
}
