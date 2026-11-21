using System.Globalization;
using static WinSystemHelper.ServiceConstants;

namespace WinSystemHelper;

internal static partial class PowerShellScripts
{
    public static string BuildRestartSelfScript(
        string serviceName,
        int processId,
        string statusMarkerPath,
        string logPath)
    {
        return BuildServiceMaintenanceScript(
            "restart",
            serviceName,
            processId,
            executablePath: null,
            sourceDirectory: null,
            targetDirectory: null,
            statusMarkerPath,
            logPath);
    }

    public static string BuildReinstallSelfScript(
        string serviceName,
        int processId,
        string executablePath,
        string statusMarkerPath,
        string logPath)
    {
        return BuildServiceMaintenanceScript(
            "reinstall",
            serviceName,
            processId,
            executablePath,
            sourceDirectory: null,
            targetDirectory: null,
            statusMarkerPath,
            logPath);
    }

    public static string BuildRollbackSelfScript(
        string serviceName,
        int processId,
        string backupDirectory,
        string targetDirectory,
        string statusMarkerPath,
        string logPath)
    {
        return BuildServiceMaintenanceScript(
            "rollback",
            serviceName,
            processId,
            executablePath: null,
            backupDirectory,
            targetDirectory,
            statusMarkerPath,
            logPath);
    }

    private static string BuildServiceMaintenanceScript(
        string mode,
        string serviceName,
        int processId,
        string? executablePath,
        string? sourceDirectory,
        string? targetDirectory,
        string statusMarkerPath,
        string logPath)
    {
        return Render("ServiceMaintenance",
            ("mode", mode),
            ("serviceName", serviceName),
            ("processId", processId.ToString(CultureInfo.CurrentCulture)),
            ("executablePath", executablePath ?? string.Empty),
            ("sourceDirectory", sourceDirectory ?? string.Empty),
            ("targetDirectory", targetDirectory ?? string.Empty),
            ("statusMarkerPath", statusMarkerPath),
            ("logPath", logPath),
            ("configFileName", ConfigFileName));
    }
}
