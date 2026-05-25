using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

internal static class Program
{
    private const string AppExeName = "WinSystemHelper.exe";
    private const string RuntimeInstallerFileName = "dotnet-runtime-8-win-x64.exe";
    private const string RuntimeDownloadUrl = "https://aka.ms/dotnet/8.0/dotnet-runtime-win-x64.exe";
    private const string RequiredRuntimeName = "Microsoft.NETCore.App";
    private const int RequiredRuntimeMajor = 8;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Any(arg => arg.Equals("--version", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("/version", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("WinSystemHelper Bootstrapper 1.5.8");
                return 0;
            }

            string baseDirectory = AppContext.BaseDirectory;
            string appPath = Path.Combine(baseDirectory, AppExeName);
            string logPath = Path.Combine(baseDirectory, "WinSystemHelper.Bootstrapper.log");

            Log(logPath, $"Bootstrapper started. Args: {string.Join(' ', args)}");

            if (!File.Exists(appPath))
            {
                return Fail(logPath, $"{AppExeName} was not found beside the bootstrapper.");
            }

            bool runtimeInstalled = IsDotNetRuntimeInstalled(logPath);
            if (!runtimeInstalled)
            {
                if (!IsRunningAsAdministrator())
                {
                    return Fail(logPath, "Access denied. Run as Administrator so the .NET Runtime can be installed.");
                }

                await DownloadAndInstallRuntimeAsync(baseDirectory, logPath);

                runtimeInstalled = IsDotNetRuntimeInstalled(logPath);
                if (!runtimeInstalled)
                {
                    return Fail(logPath, ".NET 8 x64 Runtime installation completed, but the runtime was not detected.");
                }
            }

            if (RequiresAdministrator(args) && !IsRunningAsAdministrator())
            {
                return Fail(logPath, "Access denied. Run as Administrator to install or uninstall the service.");
            }

            int exitCode = await RunWinSystemHelperAsync(appPath, args, logPath);
            Log(logPath, $"WinSystemHelper exited with code {exitCode}.");
            return exitCode;
        }
        catch (Exception ex)
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, "WinSystemHelper.Bootstrapper.log");
            Log(logPath, $"Fatal error: {ex}");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool RequiresAdministrator(IEnumerable<string> args)
    {
        return args.Any(arg =>
        {
            string normalized = arg.TrimStart('/', '-');
            return normalized.Equals("install", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("uninstall", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static async Task DownloadAndInstallRuntimeAsync(string baseDirectory, string logPath)
    {
        string installerPath = Path.Combine(Path.GetTempPath(), RuntimeInstallerFileName);

        Console.WriteLine(".NET 8 x64 Runtime is missing. Downloading Microsoft runtime installer...");
        Log(logPath, $"Downloading runtime from {RuntimeDownloadUrl} to {installerPath}.");

        using HttpClient httpClient = new()
        {
            Timeout = DownloadTimeout
        };

        using HttpResponseMessage response = await httpClient.GetAsync(
            RuntimeDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using (FileStream fileStream = File.Create(installerPath))
        {
            await response.Content.CopyToAsync(fileStream);
        }

        Console.WriteLine(".NET Runtime downloaded. Installing silently...");
        Log(logPath, "Starting runtime installer.");

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(installerPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = baseDirectory
            }
        };

        process.StartInfo.ArgumentList.Add("/install");
        process.StartInfo.ArgumentList.Add("/quiet");
        process.StartInfo.ArgumentList.Add("/norestart");

        process.Start();

        using CancellationTokenSource timeout = new(InstallTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException(".NET Runtime installer timed out.");
        }

        Log(logPath, $"Runtime installer exited with code {process.ExitCode}.");
        if (process.ExitCode != 0 && process.ExitCode != 3010)
        {
            throw new InvalidOperationException($".NET Runtime installer failed with exit code {process.ExitCode}.");
        }
    }

    private static async Task<int> RunWinSystemHelperAsync(string appPath, string[] args, string logPath)
    {
        Log(logPath, $"Launching {appPath}.");

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(appPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(appPath) ?? AppContext.BaseDirectory
            }
        };

        foreach (string arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static bool IsDotNetRuntimeInstalled(string logPath)
    {
        if (IsRuntimeInstalledFromDotNetList(logPath))
        {
            return true;
        }

        if (IsRuntimeInstalledFromRegistry(logPath))
        {
            return true;
        }

        return IsRuntimeInstalledFromFileSystem(logPath);
    }

    private static bool IsRuntimeInstalledFromDotNetList(string logPath)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo("dotnet", "--list-runtimes")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5_000);

            bool found = output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Any(IsRequiredRuntimeLine);

            Log(logPath, $"dotnet --list-runtimes detection: {found}.");
            return found;
        }
        catch (Exception ex)
        {
            Log(logPath, $"dotnet --list-runtimes detection failed: {ex.Message}");
            return false;
        }
    }

    private static bool IsRuntimeInstalledFromRegistry(string logPath)
    {
        string[] subKeyPaths =
        [
            @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App",
            @"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App"
        ];

        foreach (string subKeyPath in subKeyPaths)
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKeyPath);
                if (key is null)
                {
                    continue;
                }

                bool found = key.GetValueNames().Any(IsRequiredRuntimeVersion);
                Log(logPath, $"Registry detection {subKeyPath}: {found}.");
                if (found)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log(logPath, $"Registry detection failed for {subKeyPath}: {ex.Message}");
            }
        }

        return false;
    }

    private static bool IsRuntimeInstalledFromFileSystem(string logPath)
    {
        string runtimeDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "shared",
            RequiredRuntimeName);

        try
        {
            bool found = Directory.Exists(runtimeDirectory) &&
                Directory.EnumerateDirectories(runtimeDirectory)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Any(IsRequiredRuntimeVersion!);

            Log(logPath, $"File-system runtime detection: {found}.");
            return found;
        }
        catch (Exception ex)
        {
            Log(logPath, $"File-system runtime detection failed: {ex.Message}");
            return false;
        }
    }

    private static bool IsRequiredRuntimeLine(string line)
    {
        if (!line.StartsWith($"{RequiredRuntimeName} ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && IsRequiredRuntimeVersion(parts[1]);
    }

    private static bool IsRequiredRuntimeVersion(string version)
    {
        return Version.TryParse(version, out Version? parsed) &&
            parsed.Major == RequiredRuntimeMajor;
    }

    private static bool IsRunningAsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int Fail(string logPath, string message)
    {
        Log(logPath, message);
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void Log(string logPath, string message)
    {
        try
        {
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
