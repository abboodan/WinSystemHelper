using System.ComponentModel;
using System.Security;
using System.Text;
using System.Text.Json;
using Telegram.Bot.Types;
using static WinSystemHelper.CommandUtilities;
using static WinSystemHelper.PowerShellScripts;
using static WinSystemHelper.ServiceConstants;
using static WinSystemHelper.WindowsProcess;

namespace WinSystemHelper;

internal sealed class OtaUpdateService(
    CommandReply reply,
    UpdatePackage package,
    GitHubReleaseClient githubReleases,
    ServiceFiles files,
    ServiceInstallation installation,
    IHostApplicationLifetime hostLifetime,
    ILogger<Worker> logger)
{
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    public async Task ApplyAsync(Message message, string commandText, CancellationToken cancellationToken)
    {
        if (!await _updateLock.WaitAsync(0, cancellationToken))
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ OTA update is already in progress.", cancellationToken);
            return;
        }

        var attempt = new UpdateAttempt();
        try
        {
            StagingPaths paths = PrepareStaging(attempt);
            if (await DownloadPackageAsync(message, commandText, attempt, cancellationToken))
            {
                await InstallPackageAsync(attempt, paths, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OTA update command failed.");
            string diagnosticPath = WriteOtaFailureDiagnostic(attempt.Stage, ex, attempt.Root, attempt.ZipPath);
            await reply.SendTelegramMessageOnceAsync(
                BuildOtaFailureTelegramMessage(attempt.Stage, ex, diagnosticPath),
                CancellationToken.None);
            return;
        }
        finally
        {
            if (!attempt.Launched)
            {
                files.DeleteTempFile(attempt.ScriptPath);
                files.DeleteDirectorySafe(attempt.Root);
            }

            _updateLock.Release();
        }
    }

    private StagingPaths PrepareStaging(UpdateAttempt attempt)
    {
        attempt.Stage = "creating update staging folder";
        string updatesRoot = files.GetSharedTempDirectory("Updates");
        string updateId = Guid.NewGuid().ToString("N");
        attempt.Root = Path.Combine(updatesRoot, updateId);
        string extractRoot = Path.Combine(attempt.Root, "Extracted");
        string backupDirectory = Path.Combine(attempt.Root, "Backup");
        string persistentBackupDirectory = installation.GetPersistentBackupDirectory();
        attempt.ZipPath = Path.Combine(attempt.Root, "package.zip");
        string logPath = Path.Combine(attempt.Root, "update.log");
        string statusMarkerPath = installation.GetUpdateStatusMarkerPath();
        attempt.ScriptPath = Path.Combine(updatesRoot, $"WinSystemHelper-update-{updateId}.ps1");

        Directory.CreateDirectory(attempt.Root);
        Directory.CreateDirectory(extractRoot);

        return new StagingPaths(extractRoot, backupDirectory, persistentBackupDirectory, statusMarkerPath, logPath);
    }

    private async Task<bool> DownloadPackageAsync(
        Message message, string commandText, UpdateAttempt attempt, CancellationToken cancellationToken)
    {
        string zipPath = attempt.ZipPath!;
        if (message.Document is { } document)
        {
            if (!UpdatePackage.IsZipFileName(document.FileName))
            {
                await reply.SendTelegramMessageOnceAsync(
                    "⚠️ Update package rejected: attach a .zip file with caption /update.",
                    cancellationToken);
                return false;
            }

            attempt.Stage = $"downloading Telegram document '{document.FileName ?? document.FileId}'";
            await package.DownloadTelegramUpdatePackageAsync(document, zipPath, cancellationToken);

            // A Telegram attachment has no companion file: the signature travels in
            // the caption, as "/update <signature>".
            attempt.Signature = GetCommandPayload(commandText);
            return true;
        }

        return await DownloadUrlPackageAsync(commandText, attempt, zipPath, cancellationToken);
    }

    private async Task<bool> DownloadUrlPackageAsync(
        string commandText, UpdateAttempt attempt, string zipPath, CancellationToken cancellationToken)
    {
        string urlText = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(urlText))
        {
            await reply.SendTelegramMessageOnceAsync(
                "⚠️ Usage: send /update github, /update https://example.com/update.zip, or attach a .zip file with caption /update.",
                cancellationToken);
            return false;
        }

        var progressMessageSent = false;
        Uri? updateUri;
        Uri? signatureUri;
        if (IsGitHubUpdateAlias(urlText))
        {
            attempt.Stage = "resolving latest GitHub Release update asset";
            GitHubReleaseInfo release = await githubReleases.GetLatestGitHubReleaseInfoAsync(cancellationToken);
            updateUri = release.AssetDownloadUri;
            signatureUri = release.SignatureDownloadUri;
            attempt.Stage = $"downloading GitHub Release '{release.TagName}' asset '{release.AssetName}'";
            await reply.SendTelegramMessageOnceAsync(
                $"⬇️ GitHub update download started: {release.TagName} / {release.AssetName}",
                cancellationToken);
            progressMessageSent = true;
        }
        else if (!UpdatePackage.TryParseUpdateUri(urlText, out updateUri, out string? uriError))
        {
            await reply.SendTelegramMessageOnceAsync($"⚠️ Update URL rejected: {uriError}", cancellationToken);
            return false;
        }
        else
        {
            signatureUri = new UriBuilder(updateUri!)
            {
                Path = updateUri!.AbsolutePath + OtaSignature.SignatureFileSuffix
            }.Uri;
        }

        attempt.Stage = $"downloading update URL '{updateUri}'";
        await package.DownloadUrlUpdatePackageAsync(
            updateUri!,
            zipPath,
            progressMessageSent,
            cancellationToken);

        if (OtaSignature.IsEnforced && signatureUri is not null)
        {
            attempt.Stage = "downloading update signature";
            attempt.Signature = await package.DownloadSignatureTextAsync(signatureUri, cancellationToken);
        }
        return true;
    }

    private async Task InstallPackageAsync(
        UpdateAttempt attempt, StagingPaths paths, CancellationToken cancellationToken)
    {
        // Nothing from the package is extracted or executed before this check.
        attempt.Stage = "verifying update signature";
        if (OtaSignature.IsEnforced && !OtaSignature.Verify(attempt.ZipPath!, attempt.Signature))
        {
            throw new InvalidOperationException(
                "Update package rejected: it has no valid release signature. " +
                "Only packages signed with the release key of this project can be installed.");
        }

        attempt.Stage = "extracting update package";
        await UpdatePackage.ExtractUpdatePackageAsync(attempt.ZipPath!, paths.ExtractRoot, cancellationToken);

        attempt.Stage = "resolving update payload root";
        string payloadRoot = UpdatePackage.ResolveUpdatePayloadRoot(paths.ExtractRoot);
        attempt.Stage = $"validating update payload '{payloadRoot}'";
        UpdatePackage.ValidateUpdatePayloadRoot(payloadRoot);

        attempt.Stage = "writing updater script";

        var options = new UpdaterScriptOptions(
            ServiceConstants.ServiceName, Environment.ProcessId, payloadRoot, AppContext.BaseDirectory, paths.BackupDirectory,
            paths.PersistentBackupDirectory, attempt.Root!, paths.StatusMarkerPath, paths.LogPath);
        await WriteUpdaterScriptAsync(attempt.ScriptPath!, options, cancellationToken);

        attempt.Stage = "notifying Telegram before restart";
        await reply.SendTelegramMessageOnceAsync(
            OtaSignature.IsEnforced
                ? "🔄 Update received and signature verified. Service is restarting to apply updates..."
                : "🔄 Update received. ⚠️ This build has no release signing key, so the package was NOT verified. Service is restarting to apply updates...",
            cancellationToken);

        attempt.Stage = "launching updater script";
        ServiceInstallation.LaunchUpdaterScript(attempt.ScriptPath!);
        attempt.Launched = true;
        hostLifetime.StopApplication();
    }

    private readonly record struct StagingPaths(
        string ExtractRoot, string BackupDirectory, string PersistentBackupDirectory, string StatusMarkerPath, string LogPath);

    private sealed class UpdateAttempt
    {
        public string Stage { get; set; } = "initializing update";
        public string? Root { get; set; }
        public string? ZipPath { get; set; }
        public string? ScriptPath { get; set; }
        public string? Signature { get; set; }
        public bool Launched { get; set; }
    }

    public async Task<string?> ValidateOtaPreflightAsync(CancellationToken cancellationToken)
    {
        string baseDirectory = AppContext.BaseDirectory;
        string configPath = Path.Combine(baseDirectory, ConfigFileName);

        if (!File.Exists(configPath))
        {
            return $"⚠️ Update blocked: {ConfigFileName} is missing beside the service executable. Reinstall from the final dist folder first.";
        }

        try
        {
            string json = await File.ReadAllTextAsync(configPath, cancellationToken);
            AppConfiguration? configuration =
                JsonSerializer.Deserialize<AppConfiguration>(json, ConfigurationJsonOptions);

            if (configuration is null ||
                string.IsNullOrWhiteSpace(configuration.BotToken) ||
                configuration.GetEffectiveAdminChatIds().Count == 0)
            {
                return $"⚠️ Update blocked: {ConfigFileName} is missing BotToken or AdminChatIds.";
            }
        }
        catch (JsonException ex)
        {
            return $"⚠️ Update blocked: {ConfigFileName} is not valid JSON. {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return $"⚠️ Update blocked: unable to read {ConfigFileName}. {ex.Message}";
        }

        if (ServiceInstallation.IsBuildOutputDirectory(baseDirectory))
        {
            return "⚠️ Update blocked: service is running from a build/debug folder. Install from a clean dist folder first.";
        }

        string currentExecutablePath = Path.GetFullPath(GetCurrentExecutablePath());
        string? serviceExecutablePath;
        try
        {
            serviceExecutablePath = await installation.GetConfiguredServiceExecutablePathAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.TimeoutException)
        {
            return $"⚠️ Update blocked: unable to query the WinSystemHelper service path. {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(serviceExecutablePath))
        {
            return "⚠️ Update blocked: unable to read the WinSystemHelper service executable path from sc.exe.";
        }

        if (!ServiceInstallation.PathsEqual(currentExecutablePath, serviceExecutablePath))
        {
            return $"⚠️ Update blocked: service binPath does not match the running executable. Service: {serviceExecutablePath}";
        }

        if (!await installation.HasDotNet8RuntimeAsync(cancellationToken))
        {
            return "⚠️ Update blocked: .NET 8 Runtime is missing. Run the bootstrapper installer first.";
        }

        return null;
    }

    private string BuildOtaFailureTelegramMessage(
        string stage,
        Exception exception,
        string diagnosticPath)
    {
        string reason = GetUsefulExceptionMessage(exception);

        string message = string.Join(
            Environment.NewLine,
            "⚠️ OTA update failed.",
            $"Stage: {stage}",
            $"Reason: {reason}",
            $"Type: {exception.GetType().FullName}",
            $"Diagnostics: {diagnosticPath}");

        return TruncateForTelegram(message, 3900);
    }

    private string WriteOtaFailureDiagnostic(
        string stage,
        Exception exception,
        string? updateRoot,
        string? zipPath)
    {
        try
        {
            string diagnosticPath = Path.Combine(
                files.GetSharedTempDirectory("Updates"),
                "WinSystemHelper-last-ota-failure.txt");

            string? zipDetails = null;
            if (!string.IsNullOrWhiteSpace(zipPath))
            {
                FileInfo zipFile = new(zipPath);
                zipDetails = zipFile.Exists
                    ? $"{zipFile.FullName} ({zipFile.Length} bytes)"
                    : $"{zipPath} (missing)";
            }

            string report = string.Join(
                Environment.NewLine,
                $"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
                $"Stage: {stage}",
                $"ExceptionType: {exception.GetType().FullName}",
                $"Reason: {GetUsefulExceptionMessage(exception)}",
                $"HResult: 0x{exception.HResult:X8}",
                $"UpdateRoot: {updateRoot ?? "Unavailable"}",
                $"ZipPath: {zipDetails ?? "Unavailable"}",
                string.Empty,
                "Exception:",
                exception.ToString());

            File.WriteAllText(diagnosticPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return diagnosticPath;
        }
        catch (Exception diagnosticException)
        {
            logger.LogWarning(diagnosticException, "Failed to write OTA failure diagnostic report.");
            return $"Unavailable ({diagnosticException.GetType().Name}: {GetUsefulExceptionMessage(diagnosticException)})";
        }
    }

    private static async Task WriteUpdaterScriptAsync(
        string scriptPath,
        UpdaterScriptOptions options,
        CancellationToken cancellationToken)
    {
        string script = BuildUpdaterScript(options);
        await File.WriteAllTextAsync(
            scriptPath,
            script,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static bool IsGitHubUpdateAlias(string value)
    {
        return value.Equals("github", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("release", StringComparison.OrdinalIgnoreCase);
    }
}
