using System.Text;
using System.Text.Json;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed class PersistentMicLoopFile(
    QueueStore queueStore,
    ServiceFiles files,
    ILogger<Worker> logger)
{
    private const string MicLoopStateFileName = ServiceConstants.MicLoopStateFileName;

    public void MigrateLegacyState()
    {
        string oldState = Path.Combine(files.GetSharedTempDirectory("Audio"), MicLoopStateFileName);
        if (!File.Exists(GetPath()) && File.Exists(oldState))
        {
            File.Copy(oldState, GetPath());
            files.DeleteTempFile(oldState);
        }
    }

    public async Task<(string TempPath, string StatePath)> WriteTemporaryAsync(
        PersistentMicLoopState state, CancellationToken cancellationToken)
    {
        string statePath = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        string tempPath = $"{statePath}.{Guid.NewGuid():N}.tmp";
        string json = JsonSerializer.Serialize(state, ConfigurationJsonOptions);

        await File.WriteAllTextAsync(
            tempPath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        return (tempPath, statePath);
    }

    public async Task<PersistentMicLoopState?> ReadPersistentMicLoopStateAsync(CancellationToken cancellationToken)
    {
        string statePath = GetPath();
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            string json = await File.ReadAllTextAsync(statePath, Encoding.UTF8, cancellationToken);
            return JsonSerializer.Deserialize<PersistentMicLoopState>(json, ConfigurationJsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Failed to read persistent mic loop state. Removing invalid state file.");
            DeletePersistentMicLoopState();
            return null;
        }
    }

    public void DeletePersistentMicLoopState()
    {
        files.DeleteTempFile(GetPath());
    }

    private string GetPath()
    {
        return Path.Combine(queueStore.Root, MicLoopStateFileName);
    }
}
