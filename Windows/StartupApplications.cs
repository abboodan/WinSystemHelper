using System.Security;
using Microsoft.Win32;
using static WinSystemHelper.WindowsProcess;

namespace WinSystemHelper;

internal sealed class StartupApplications(
    ILogger<Worker> logger,
    IWindowsSessionProcessRunner sessions)
{
    public IReadOnlyList<StartupEntry> GetStartupEntries()
    {
        List<StartupEntry> entries = [];

        AddStartupRegistryEntries(
            Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            "HKLM Run",
            entries);
        AddStartupRegistryEntries(
            Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            "HKLM Run (WOW6432Node)",
            entries);

        string? activeUserSid = sessions.TryGetActiveUserSid();
        if (!string.IsNullOrWhiteSpace(activeUserSid))
        {
            AddStartupRegistryEntries(
                Registry.Users,
                $@"{activeUserSid}\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                "Active User Run",
                entries);
            AddStartupRegistryEntries(
                Registry.Users,
                $@"{activeUserSid}\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                "Active User Run (WOW6432Node)",
                entries);
        }

        return entries
            .GroupBy(
                entry => $"{entry.Name}\u001f{entry.Command}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private void AddStartupRegistryEntries(
        RegistryKey root,
        string subKeyPath,
        string source,
        ICollection<StartupEntry> entries)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(subKeyPath);
            if (key is null)
            {
                return;
            }

            foreach (string valueName in key.GetValueNames())
            {
                object? value = key.GetValue(valueName);
                string command = value switch
                {
                    string text => text,
                    string[] parts => string.Join(" ", parts),
                    _ => value?.ToString() ?? string.Empty
                };

                if (string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                string name = string.IsNullOrWhiteSpace(valueName) ? "(Default)" : valueName;
                entries.Add(new StartupEntry(name, Environment.ExpandEnvironmentVariables(command), source));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.LogDebug(
                ex,
                "Unable to read startup registry key {Source}: {SubKeyPath}.",
                source,
                subKeyPath);
        }
    }

    public AppLaunchInfo? TryFindStartupLaunchInfo(
        string processName,
        IEnumerable<StartupEntry> startupEntries)
    {
        foreach (StartupEntry entry in startupEntries)
        {
            if (!TryParseStartupCommand(entry.Command, out AppLaunchInfo launchInfo))
            {
                continue;
            }

            string entryName = NormalizeProcessName(entry.Name);
            string executableName = NormalizeProcessName(Path.GetFileNameWithoutExtension(launchInfo.FilePath));

            if (entryName.Equals(processName, StringComparison.OrdinalIgnoreCase) ||
                executableName.Equals(processName, StringComparison.OrdinalIgnoreCase))
            {
                return launchInfo;
            }
        }

        return null;
    }

    private static bool TryParseStartupCommand(string command, out AppLaunchInfo launchInfo)
    {
        launchInfo = default;

        string expandedCommand = Environment.ExpandEnvironmentVariables(command).Trim();
        if (string.IsNullOrWhiteSpace(expandedCommand))
        {
            return false;
        }

        string filePath;
        string arguments;

        if (expandedCommand.StartsWith('"'))
        {
            int closingQuote = expandedCommand.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                return false;
            }

            filePath = expandedCommand[1..closingQuote];
            arguments = expandedCommand[(closingQuote + 1)..].Trim();
        }
        else
        {
            int executableExtensionIndex = expandedCommand.IndexOf(
                ".exe",
                StringComparison.OrdinalIgnoreCase);

            if (executableExtensionIndex >= 0)
            {
                int executableEnd = executableExtensionIndex + ".exe".Length;
                filePath = expandedCommand[..executableEnd];
                arguments = expandedCommand[executableEnd..].Trim();
            }
            else
            {
                int separatorIndex = expandedCommand.IndexOf(' ');
                if (separatorIndex < 0)
                {
                    filePath = expandedCommand;
                    arguments = string.Empty;
                }
                else
                {
                    filePath = expandedCommand[..separatorIndex];
                    arguments = expandedCommand[(separatorIndex + 1)..].Trim();
                }
            }
        }

        filePath = filePath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        launchInfo = new AppLaunchInfo(filePath, arguments);
        return true;
    }
}
