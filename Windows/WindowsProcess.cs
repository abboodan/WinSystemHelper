using System.Diagnostics;

namespace WinSystemHelper;

internal static class WindowsProcess
{
    public static string GetCurrentExecutablePath()
    {
        return Environment.ProcessPath ??
            Process.GetCurrentProcess().MainModule?.FileName ??
            throw new InvalidOperationException("Unable to resolve executable path.");
    }

    public static void StartDetachedProcess(string fileName, string arguments)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            }
        };

        process.Start();
    }

    public static string GetWindowsPowerShellPath()
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

    public static string QuoteCommandLineArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    public static string NormalizeProcessName(string processName)
    {
        string normalized = processName.Trim();
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }
}
