namespace WinSystemHelper;

public readonly record struct ProcessCaptureResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
