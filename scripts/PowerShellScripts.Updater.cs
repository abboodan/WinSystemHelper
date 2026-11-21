using System.Globalization;
using static WinSystemHelper.ServiceConstants;

namespace WinSystemHelper;

internal static partial class PowerShellScripts
{
    public static string BuildUpdaterScript(UpdaterScriptOptions options)
    {
        return Render("Updater",
            ("serviceName", options.ServiceName),
            ("processId", options.ProcessId.ToString(CultureInfo.CurrentCulture)),
            ("payloadRoot", options.PayloadRoot),
            ("targetDirectory", options.TargetDirectory),
            ("backupDirectory", options.BackupDirectory),
            ("persistentBackupDirectory", options.PersistentBackupDirectory),
            ("updateRoot", options.UpdateRoot),
            ("statusMarkerPath", options.StatusMarkerPath),
            ("logPath", options.LogPath),
            ("configFileName", ConfigFileName));
    }
}
