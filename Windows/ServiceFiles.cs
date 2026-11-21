using System.Security;
using static WinSystemHelper.ServiceConstants;
using static WinSystemHelper.ServiceInstallation;
using System.Text;

namespace WinSystemHelper;

internal sealed class ServiceFiles(ILogger<Worker> logger, ISharedPathProvider sharedPaths)
{
    public void DeleteTempFile(string? tempFile)
    {
        if (string.IsNullOrWhiteSpace(tempFile) || !File.Exists(tempFile))
        {
            return;
        }

        try
        {
            File.Delete(tempFile);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete temporary file {TempFile}.", tempFile);
        }
    }

    public void DeleteDirectorySafe(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete temporary directory {Directory}.", directory);
        }
    }

    public string GetSharedTempDirectory(string purpose)
    {
        return sharedPaths.GetSharedTempDirectory(purpose);
    }

    /// <summary>Reads a bounded amount of text from a file an ordinary user could have written.</summary>
    public static async Task<string> ReadUntrustedTextAsync(string path, CancellationToken cancellationToken)
    {
        const int MaxBytes = 16 * 1024;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to read a redirected file.");
        }

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = new byte[MaxBytes];
        int read = await stream.ReadAtLeastAsync(buffer, MaxBytes, throwOnEndOfStream: false, cancellationToken);
        return Encoding.UTF8.GetString(buffer, 0, read).TrimStart('﻿').Trim();
    }

    public int CleanupOldSharedTempItems(string directory, TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        int deleted = 0;
        DateTime cutoffUtc = DateTime.UtcNow.Subtract(maxAge);

        foreach (string childDirectory in Directory.EnumerateDirectories(directory))
        {
            try
            {
                if (Path.GetFileName(childDirectory).Equals(PersistentBackupFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DirectoryInfo info = new(childDirectory);
                if (info.LastWriteTimeUtc < cutoffUtc)
                {
                    info.Delete(recursive: true);
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                logger.LogDebug(ex, "Failed to clean old directory {Directory}.", childDirectory);
            }
        }

        foreach (string file in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (Path.GetFileName(file).Equals(UpdateStatusMarkerFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FileInfo info = new(file);
                if (info.LastWriteTimeUtc < cutoffUtc)
                {
                    info.Delete();
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                logger.LogDebug(ex, "Failed to clean old file {File}.", file);
            }
        }

        return deleted;
    }
}
