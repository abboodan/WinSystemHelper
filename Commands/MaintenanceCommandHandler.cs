using System.ComponentModel;
using System.Text;
using System.Text.Json;
using static WinSystemHelper.CommandUtilities;
using static WinSystemHelper.PowerShellScripts;
using static WinSystemHelper.ServiceConstants;
using static WinSystemHelper.WindowsProcess;

namespace WinSystemHelper;

internal sealed class MaintenanceCommandHandler(
    CommandReply reply,
    ConfirmationService confirmations,
    ServiceFiles files,
    ServiceInstallation installation,
    IHostApplicationLifetime hostLifetime,
    TelegramMenuService telegramMenu,
    RuntimeConfiguration runtime,
    Action<string, string>? startDetachedProcess = null) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/restartself", "/repair", "/reinstall", "/rollback", "/stop", "/uninstall"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken) => request.Word switch
    {
        "/restartself" => confirmations.RequestDangerousConfirmationAsync(
            "restartself", "Restart the WinSystemHelper service", RestartSelfAsync, cancellationToken),
        "/repair" => RepairAsync(cancellationToken),
        "/reinstall" => confirmations.RequestDangerousConfirmationAsync(
            "reinstall", "Reinstall the WinSystemHelper Windows Service", ReinstallSelfAsync, cancellationToken),
        "/rollback" => confirmations.RequestDangerousConfirmationAsync(
            "rollback", "Rollback WinSystemHelper to the previous installed version", RollbackSelfAsync, cancellationToken),
        "/stop" => confirmations.RequestDangerousConfirmationAsync(
            "stop", "Stop the WinSystemHelper service", StopServiceAsync, cancellationToken),
        "/uninstall" => confirmations.RequestDangerousConfirmationAsync(
            "uninstall", "Stop and delete the WinSystemHelper service", UninstallServiceRemotelyAsync, cancellationToken),
        _ => Task.CompletedTask
    };

    private async Task RestartSelfAsync(CancellationToken cancellationToken)
    {
        string scriptPath = Path.Combine(
            files.GetSharedTempDirectory("Maintenance"),
            $"WinSystemHelper-restartself-{Guid.NewGuid():N}.ps1");
        string logPath = Path.Combine(files.GetSharedTempDirectory("Maintenance"), "restartself.log");

        string script = BuildRestartSelfScript(
            ServiceConstants.ServiceName,
            Environment.ProcessId,
            installation.GetUpdateStatusMarkerPath(),
            logPath);

        await File.WriteAllTextAsync(
            scriptPath,
            script,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        await reply.SendTelegramMessageOnceAsync(
            "🔄 Restarting WinSystemHelper service...",
            cancellationToken);

        ServiceInstallation.LaunchUpdaterScript(scriptPath);
        hostLifetime.StopApplication();
    }

    private async Task ReinstallSelfAsync(CancellationToken cancellationToken)
    {
        string configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        if (!File.Exists(configPath))
        {
            await reply.SendTelegramMessageOnceAsync(
                $"⚠️ Reinstall blocked: {ConfigFileName} is missing beside the service executable.",
                cancellationToken);
            return;
        }

        string executablePath;
        try
        {
            executablePath = Path.GetFullPath(GetCurrentExecutablePath());
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            await reply.SendTelegramMessageOnceAsync(
                $"⚠️ Reinstall blocked: unable to resolve executable path. {ex.Message}",
                cancellationToken);
            return;
        }

        if (ServiceInstallation.IsBuildOutputDirectory(AppContext.BaseDirectory))
        {
            await reply.SendTelegramMessageOnceAsync(
                "⚠️ Reinstall blocked: service is running from a build/debug folder. Install from a clean deployment folder first.",
                cancellationToken);
            return;
        }

        string scriptPath = Path.Combine(
            files.GetSharedTempDirectory("Maintenance"),
            $"WinSystemHelper-reinstall-{Guid.NewGuid():N}.ps1");
        string logPath = Path.Combine(files.GetSharedTempDirectory("Maintenance"), "reinstall.log");

        string script = BuildReinstallSelfScript(
            ServiceConstants.ServiceName,
            Environment.ProcessId,
            executablePath,
            installation.GetUpdateStatusMarkerPath(),
            logPath);

        await File.WriteAllTextAsync(
            scriptPath,
            script,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        await reply.SendTelegramMessageOnceAsync(
            "🛠️ Reinstalling WinSystemHelper Windows Service...",
            cancellationToken);

        ServiceInstallation.LaunchUpdaterScript(scriptPath);
        hostLifetime.StopApplication();
    }

    private async Task RollbackSelfAsync(CancellationToken cancellationToken)
    {
        string persistentBackupDirectory = installation.GetPersistentBackupDirectory();
        string backupExecutable = Path.Combine(persistentBackupDirectory, Path.GetFileName(GetCurrentExecutablePath()));

        if (ServiceInstallation.IsBuildOutputDirectory(AppContext.BaseDirectory))
        {
            await reply.SendTelegramMessageOnceAsync(
                "⚠️ Rollback blocked: service is running from a build/debug folder.",
                cancellationToken);
            return;
        }

        if (!File.Exists(backupExecutable) || new FileInfo(backupExecutable).Length == 0)
        {
            await reply.SendTelegramMessageOnceAsync(
                $"⚠️ Rollback blocked: no valid persistent backup exists at {persistentBackupDirectory}. Run one successful OTA update first.",
                cancellationToken);
            return;
        }

        string configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        if (!File.Exists(configPath))
        {
            await reply.SendTelegramMessageOnceAsync(
                $"⚠️ Rollback blocked: {ConfigFileName} is missing beside the service executable.",
                cancellationToken);
            return;
        }

        string scriptPath = Path.Combine(
            files.GetSharedTempDirectory("Maintenance"),
            $"WinSystemHelper-rollback-{Guid.NewGuid():N}.ps1");
        string logPath = Path.Combine(files.GetSharedTempDirectory("Maintenance"), "rollback.log");

        string script = BuildRollbackSelfScript(
            ServiceConstants.ServiceName,
            Environment.ProcessId,
            persistentBackupDirectory,
            AppContext.BaseDirectory,
            installation.GetUpdateStatusMarkerPath(),
            logPath);

        await File.WriteAllTextAsync(
            scriptPath,
            script,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        await reply.SendTelegramMessageOnceAsync(
            "↩️ Rolling back WinSystemHelper to the previous installed version...",
            cancellationToken);

        ServiceInstallation.LaunchUpdaterScript(scriptPath);
        hostLifetime.StopApplication();
    }

    private async Task RepairAsync(CancellationToken cancellationToken)
    {
        List<string> report = ["🛠️ WinSystemHelper repair report:"];

        string configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        report.Add(File.Exists(configPath)
            ? $"✅ Config exists: {configPath}"
            : $"⚠️ Config missing: {configPath}");

        try
        {
            string json = await File.ReadAllTextAsync(configPath, cancellationToken);
            AppConfiguration? configuration =
                JsonSerializer.Deserialize<AppConfiguration>(json, ConfigurationJsonOptions);
            int adminCount = configuration?.GetEffectiveAdminChatIds().Count ?? 0;
            report.Add(configuration is not null && !string.IsNullOrWhiteSpace(configuration.BotToken) && adminCount > 0
                ? $"✅ Config readable: {adminCount} admin(s)"
                : "⚠️ Config readable but missing BotToken/AdminChatIds");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            report.Add($"⚠️ Config read failed: {ex.Message}");
        }

        try
        {
            string currentExecutablePath = Path.GetFullPath(GetCurrentExecutablePath());
            string? serviceExecutablePath = await installation.GetConfiguredServiceExecutablePathAsync(cancellationToken);
            report.Add(string.IsNullOrWhiteSpace(serviceExecutablePath)
                ? "⚠️ Service path unavailable from sc.exe"
                : ServiceInstallation.PathsEqual(currentExecutablePath, serviceExecutablePath)
                    ? "✅ Service binPath matches running executable"
                    : $"⚠️ Service binPath mismatch: {serviceExecutablePath}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.TimeoutException)
        {
            report.Add($"⚠️ Service path check failed: {ex.Message}");
        }

        report.Add(ServiceInstallation.IsBuildOutputDirectory(AppContext.BaseDirectory)
            ? "⚠️ Running from build/debug output folder"
            : "✅ Running from deployment folder");

        try
        {
            report.Add(InstallLayout.IsWritableByNonAdministrators(AppContext.BaseDirectory)
                ? $"🚨 Install folder can be modified by non-administrators. Reinstall with /install so the service moves to {InstallLayout.DefaultInstallDirectory}."
                : "✅ Install folder is restricted to administrators");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            report.Add($"⚠️ Install folder permission check failed: {ex.Message}");
        }

        report.Add(OtaSignature.IsEnforced
            ? "✅ OTA updates require a signed package"
            : "⚠️ This build has no release signing key; OTA packages are not verified");

        report.Add(await installation.HasDotNet8RuntimeAsync(cancellationToken)
            ? "✅ .NET 8 Runtime detected"
            : "⚠️ .NET 8 Runtime not detected");

        string[] repairFolders =
            ["Updates", "Backups", "Audio", "Screen", "Camera", "Interaction", "PowerEvents", "Maintenance"];
        foreach (string purpose in repairFolders)
        {
            string directory = files.GetSharedTempDirectory(purpose);
            report.Add($"✅ Shared folder ready: {purpose}");
            int deleted = purpose == "PowerEvents" ? 0 : files.CleanupOldSharedTempItems(directory, TimeSpan.FromDays(2));
            if (deleted > 0)
            {
                report.Add($"🧹 Cleaned {deleted} old item(s) from {purpose}");
            }
        }

        await telegramMenu.RegisterAsync(
            runtime.GetAdminChatIdsSnapshot(),
            CommandReply.SendTimeout,
            cancellationToken);
        report.Add("✅ Telegram command menu registration requested");

        await reply.SendTelegramMessageOnceAsync(
            TruncateForTelegram(string.Join(Environment.NewLine, report), 3900),
            cancellationToken);
    }

    private async Task StopServiceAsync(CancellationToken cancellationToken)
    {
        await reply.SendTelegramMessageOnceAsync("💤 WinSystemHelper service is stopping...", cancellationToken);
        hostLifetime.StopApplication();
    }

    private async Task UninstallServiceRemotelyAsync(CancellationToken cancellationToken)
    {
        await reply.SendTelegramMessageOnceAsync("💣 Stopping and removing the WinSystemHelper service...", cancellationToken);
        (startDetachedProcess ?? StartDetachedProcess)("cmd.exe", "/c sc stop WinSystemHelper && sc delete WinSystemHelper");
        hostLifetime.StopApplication();
    }
}
