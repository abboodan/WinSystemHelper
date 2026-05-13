namespace WinSystemHelper;

internal readonly record struct UpdaterScriptOptions(
    string ServiceName,
    int ProcessId,
    string PayloadRoot,
    string TargetDirectory,
    string BackupDirectory,
    string UpdateRoot,
    string StatusMarkerPath,
    string LogPath);
