using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed class StatusCommandHandler(
    ILogger<Worker> logger,
    CommandReply reply,
    TelegramNotifier telegramNotifier,
    ITelegramBotClient botClient,
    FileLogStore fileLog,
    IWindowsSessionProcessRunner windowsSessionProcessRunner,
    DevelopmentMode developmentMode,
    GitHubReleaseClient githubReleases,
    ServiceTelemetry telemetry,
    PublicIpCache publicIpCache,
    ConfirmationService confirmations,
    PowerEventMonitor powerMonitor,
    MicrophoneCommandHandler microphone,
    CommandBackgroundWork backgroundWork) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/status", "/healthcheck", "/log", "/version", "/ping"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken) => request.Word switch
    {
        "/status" => SendStatusAsync(cancellationToken),
        "/healthcheck" => SendHealthCheckAsync(cancellationToken),
        "/log" => HandleLogCommandAsync(request.Text, cancellationToken),
        "/version" => SendVersionAsync(request.Text, cancellationToken),
        "/ping" => SendPingAsync(cancellationToken),
        _ => Task.CompletedTask
    };

    private async Task SendStatusAsync(CancellationToken cancellationToken)
    {
        string processWorkingSet = "Unavailable";
        string processThreadCount = "Unavailable";

        try
        {
            using Process process = Process.GetCurrentProcess();
            processWorkingSet = $"{process.WorkingSet64 / 1024d / 1024d:N1} MB";
            processThreadCount = process.Threads.Count.ToString();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to collect current process metrics for status report.");
        }

        string status = string.Join(
            Environment.NewLine,
            "📊 WinSystemHelper Dashboard:",
            "",
            "🖥️ System & Hardware",
            $"Machine: {SafeStatusMetric("machine name", () => Environment.MachineName)}",
            $"OS: {SafeStatusMetric("OS version", () => Environment.OSVersion.VersionString)}",
            $"Architecture: OS {SafeStatusMetric("OS architecture", () => RuntimeInformation.OSArchitecture.ToString())} | Process {SafeStatusMetric("process architecture", () => RuntimeInformation.ProcessArchitecture.ToString())}",
            $"CPU cores: {SafeStatusMetric("CPU core count", () => Environment.ProcessorCount.ToString())}",
            $"System uptime: {SafeStatusMetric("system telemetry.Uptime", () => FormatDuration(TimeSpan.FromMilliseconds(Environment.TickCount64)))}",
            $"Battery: {SafeStatusMetric("battery status", () => BatteryStatus.GetBatteryStatusText(logger))}",
            "",
            "⚙️ Process & Runtime",
            $"Service PID: {Environment.ProcessId}",
            $"Executable: {SafeStatusMetric("executable path", () => Environment.ProcessPath ?? "Unavailable")}",
            $"Runtime: {SafeStatusMetric("runtime description", () => RuntimeInformation.FrameworkDescription)}",
            $"Working set: {processWorkingSet}",
            $"Managed memory: {SafeStatusMetric("managed memory", () => $"{GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024d:N1} MB")}",
            $"GC collections: Gen0={SafeStatusMetric("GC Gen0", () => GC.CollectionCount(0).ToString())} | Gen1={SafeStatusMetric("GC Gen1", () => GC.CollectionCount(1).ToString())} | Gen2={SafeStatusMetric("GC Gen2", () => GC.CollectionCount(2).ToString())}",
            $"Threads: {processThreadCount}",
            "",
            "📡 Service & State",
            $"Mode: {developmentMode.Name}",
            $"Started: {telemetry.StartedAt:yyyy-MM-dd HH:mm:ss zzz}",
            $"Service uptime: {FormatDuration(telemetry.Uptime)}",
            $"Network available: {SafeStatusMetric("network availability", () => NetworkInterface.GetIsNetworkAvailable().ToString())}",
            $"Active console session: {SafeStatusMetric("active console session", windowsSessionProcessRunner.GetActiveConsoleSessionStatus)}",
            $"Wake watcher: {powerMonitor.GetWakeWatcherStatus()}",
            $"Mic loop: {microphone.GetMicLoopStatus()}",
            $"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        await reply.SendTelegramMessageOnceAsync(status, cancellationToken);
    }

    private async Task SendHealthCheckAsync(CancellationToken cancellationToken)
    {
        TelemetrySnapshot snapshot = telemetry.GetTelemetrySnapshot();
        PublicIpSnapshot publicIp = publicIpCache.GetPublicIpSnapshot();
        int pendingConfirmations = confirmations.RemoveExpiredConfirmations();

        string status = string.Join(
            Environment.NewLine,
            "🩺 WinSystemHelper Healthcheck:",
            $"Service uptime: {FormatDuration(telemetry.Uptime)}",
            $"Network available: {SafeStatusMetric("network availability", () => NetworkInterface.GetIsNetworkAvailable().ToString())}",
            $"Last poll OK: {FormatNullableTimestamp(snapshot.LastPollingSuccessAt)}",
            $"Last poll failure: {FormatNullableTimestamp(snapshot.LastPollingFailureAt)}",
            $"Polling failure streak: {snapshot.PollingFailureStreak}",
            $"Last wake alert: {FormatNullableTimestamp(snapshot.LastWakeAlertAt)}",
            $"Last startup alert: {FormatNullableTimestamp(snapshot.LastStartupAlertAt)}",
            $"Public IP cache: {FormatPublicIpSnapshot(publicIp)}",
            $"Wake watcher: {powerMonitor.GetWakeWatcherStatus()}",
            $"Mic loop: {microphone.GetMicLoopStatus()}",
            $"Pending confirmations: {pendingConfirmations}",
            $"Last error: {snapshot.LastError ?? "None"}",
            $"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        await reply.SendTelegramMessageOnceAsync(status, cancellationToken);
    }

    private async Task HandleLogCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        const int DefaultLines = 40;
        const int MaxLines = 200;
        const int MaxCharacters = 3500;

        if (!fileLog.IsAvailable)
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Private service log storage is unavailable.", cancellationToken);
            return;
        }

        string argument = GetCommandPayload(commandText).Trim();
        if (argument.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            backgroundWork.QueueBackgroundWork(SendLogFileAsync, "Log file command failed.", waitForCompletion: false);
            return;
        }

        int lines = DefaultLines;
        if (argument.Length > 0 && (!int.TryParse(argument, out lines) || lines < 1))
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Usage: /log | /log [lines] | /log file", cancellationToken);
            return;
        }

        string tail = fileLog.ReadTail(Math.Min(lines, MaxLines), MaxCharacters);
        await telegramNotifier.SendHtmlAsync(reply.ChatId, TelegramReplies.Log(Environment.MachineName, tail),
            keyboard: null, cancellationToken);
    }

    private async Task SendLogFileAsync(CancellationToken cancellationToken)
    {
        await using MemoryStream snapshot = fileLog.CreateSnapshot();
        if (snapshot.Length == 0)
        {
            await reply.SendTelegramMessageOnceAsync("📄 The service log is empty.", cancellationToken);
            return;
        }

        string fileName = $"WinSystemHelper-{Environment.MachineName}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log";

        // The snapshot is sent directly so the media queue cannot deliver a stale copy.
        using CancellationTokenSource timeout =
            CreateTimeoutTokenSource(cancellationToken, TimeSpan.FromMinutes(2));

        try
        {
            await botClient.SendDocument(
                reply.ChatId,
                InputFile.FromStream(snapshot, fileName),
                caption: $"📄 {fileName}",
                cancellationToken: timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is RequestException or HttpRequestException or IOException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Sending the log file failed.");
            string reason = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                ? "The upload timed out."
                : ex.Message;
            await reply.SendTelegramMessageOnceAsync(
                $"⚠️ Could not send the log file: {reason} Use /log for the latest lines.",
                CancellationToken.None);
        }
    }

    private string SafeStatusMetric(string metricName, Func<string> metricFactory)
    {
        try
        {
            return metricFactory();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to collect status metric {MetricName}.", metricName);
            return "Unavailable";
        }
    }

    private static string FormatNullableTimestamp(DateTimeOffset? timestamp)
    {
        return timestamp.HasValue
            ? timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss zzz")
            : "Never";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalDays >= 1
            ? duration.ToString(@"dd\.hh\:mm\:ss")
            : duration.ToString(@"hh\:mm\:ss");
    }

    private async Task SendVersionAsync(string commandText, CancellationToken cancellationToken)
    {
        Assembly assembly = typeof(StatusCommandHandler).Assembly;
        string version =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Unknown";

        string payload = GetCommandPayload(commandText);
        if (payload.Equals("check", StringComparison.OrdinalIgnoreCase) ||
            payload.Equals("github", StringComparison.OrdinalIgnoreCase) ||
            payload.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            await SendGitHubVersionCheckAsync(version, cancellationToken);
            return;
        }

        await reply.SendTelegramMessageOnceAsync($"🏷️ WinSystemHelper Version: {version}", cancellationToken);
    }

    private async Task SendGitHubVersionCheckAsync(
        string currentVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            GitHubReleaseInfo release = await githubReleases.GetLatestGitHubReleaseInfoAsync(cancellationToken);
            string latestVersion = NormalizeReleaseVersion(release.TagName);
            bool updateAvailable = IsVersionNewer(latestVersion, currentVersion);

            string message = string.Join(
                Environment.NewLine,
                "🏷️ WinSystemHelper version check:",
                $"Current: {currentVersion}",
                $"Latest GitHub Release: {release.TagName}",
                $"Asset: {release.AssetName}",
                $"Published: {release.PublishedAt:yyyy-MM-dd HH:mm:ss zzz}",
                $"Update available: {(updateAvailable ? "Yes" : "No")}");

            await reply.SendTelegramMessageOnceAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            await reply.SendTelegramMessageOnceAsync(
                $"⚠️ GitHub version check failed: {GetUsefulExceptionMessage(ex)}",
                cancellationToken);
        }
    }

    private async Task SendPingAsync(CancellationToken cancellationToken)
    {
        string message = string.Join(
            Environment.NewLine,
            "🏓 Pong. WinSystemHelper is online.",
            $"Machine: {Environment.MachineName}",
            $"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        await reply.SendTelegramMessageOnceAsync(message, cancellationToken);
    }

    private static string NormalizeReleaseVersion(string version)
    {
        string normalized = version.Trim();
        int metadataIndex = normalized.IndexOf('+');
        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }

        int prereleaseIndex = normalized.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            normalized = normalized[..prereleaseIndex];
        }

        return normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? normalized[1..]
            : normalized;
    }

    private static bool IsVersionNewer(string latestVersion, string currentVersion)
    {
        string latest = NormalizeReleaseVersion(latestVersion);
        string current = NormalizeReleaseVersion(currentVersion);

        if (Version.TryParse(latest, out Version? latestParsed) &&
            Version.TryParse(current, out Version? currentParsed))
        {
            return latestParsed > currentParsed;
        }

        return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
    }
}
