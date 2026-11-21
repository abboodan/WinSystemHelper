using System.Security;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using static WinSystemHelper.ServiceConstants;
using static WinSystemHelper.WindowsProcess;

namespace WinSystemHelper;

internal sealed class ServiceInstallation(IProcessRunner processRunner, ServiceFiles files)
{
    private const string UpdateStatusMarkerFileName = ServiceConstants.UpdateStatusMarkerFileName;

    internal const string PersistentBackupFolderName = "previous";

    public string GetUpdateStatusMarkerPath()
    {
        return Path.Combine(files.GetSharedTempDirectory("Updates"), UpdateStatusMarkerFileName);
    }

    public string GetPersistentBackupDirectory()
    {
        return Path.Combine(files.GetSharedTempDirectory("Backups"), PersistentBackupFolderName);
    }

    public static void LaunchUpdaterScript(string scriptPath)
    {
        StartDetachedProcess(
            GetWindowsPowerShellPath(),
            $"-NoProfile -ExecutionPolicy Bypass -File {QuoteCommandLineArgument(scriptPath)}");
    }

    public async Task<string?> GetConfiguredServiceExecutablePathAsync(CancellationToken cancellationToken)
    {
        ProcessCaptureResult result = await processRunner.CaptureAsync(
            "sc.exe",
            $"qc {ServiceConstants.ServiceName}",
            TimeSpan.FromSeconds(10),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            return null;
        }

        foreach (string line in result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separatorIndex = line.IndexOf(':');
            if (separatorIndex < 0)
            {
                continue;
            }

            string key = line[..separatorIndex].Trim();
            if (!key.Equals("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = line[(separatorIndex + 1)..].Trim();
            string? executablePath = ExtractExecutablePathFromCommandLine(value);
            return string.IsNullOrWhiteSpace(executablePath)
                ? null
                : Path.GetFullPath(executablePath);
        }

        return null;
    }

    public async Task<bool> HasDotNet8RuntimeAsync(CancellationToken cancellationToken)
    {
        if (HasDotNet8RuntimeInRegistry())
        {
            return true;
        }

        try
        {
            ProcessCaptureResult result = await processRunner.CaptureAsync(
                "dotnet.exe",
                "--list-runtimes",
                TimeSpan.FromSeconds(10),
                cancellationToken);

            return result.ExitCode == 0 &&
                result.StandardOutput
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => line.StartsWith("Microsoft.NETCore.App 8.", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.TimeoutException)
        {
            return false;
        }
    }

    private static bool HasDotNet8RuntimeInRegistry()
    {
        const string runtimeKeyPath =
            @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App";

        try
        {
            using RegistryKey? runtimeKey = Registry.LocalMachine.OpenSubKey(runtimeKeyPath);
            return runtimeKey?.GetValueNames()
                .Any(name => name.StartsWith("8.", StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsBuildOutputDirectory(string directory)
    {
        string[] parts = Path.GetFullPath(directory)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                (parts[i + 1].Equals("Debug", StringComparison.OrdinalIgnoreCase) ||
                 parts[i + 1].Equals("Release", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractExecutablePathFromCommandLine(string commandLine)
    {
        string trimmed = commandLine.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (trimmed[0] == '"')
        {
            int closingQuoteIndex = trimmed.IndexOf('"', 1);
            return closingQuoteIndex > 1 ? trimmed[1..closingQuoteIndex] : null;
        }

        int executableExtensionIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableExtensionIndex < 0
            ? trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : trimmed[..(executableExtensionIndex + 4)];
    }

    public static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}
