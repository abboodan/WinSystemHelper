using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Telegram.Bot.Exceptions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace WinSystemHelper;

public sealed class Worker : BackgroundService
{
    private static readonly TimeSpan InitialInternetRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxInternetRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialTelegramPollingFailureDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxTelegramPollingFailureDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TelegramPollTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TelegramPollNetworkTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TelegramSendTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectivityProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AskPromptTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TextPromptTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateDownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PublicIpRefreshTimeout = TimeSpan.FromSeconds(3);
    private static readonly Uri TelegramApiBaseUri = new("https://api.telegram.org");
    private static readonly Uri PublicIpUri = new("https://api.ipify.org");
    private static readonly JsonSerializerOptions ConfigurationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private const long MaxUpdatePackageBytes = 100L * 1024L * 1024L;
    private const int TelegramUpdateLimit = 20;
    private const int DefaultMicRecordingSeconds = 10;
    private const int MaxMicRecordingSeconds = 60;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ActiveConsoleSessionUnavailable = 0xFFFFFFFF;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const int TelegramBadRequestErrorCode = 400;
    private const int TelegramUnauthorizedErrorCode = 401;
    private const int TelegramForbiddenErrorCode = 403;
    private const uint AskYesExitCode = 1;
    private const uint AskNoExitCode = 2;
    private const byte BatteryLifePercentUnknown = 255;
    private const byte BatteryFlagNoSystemBattery = 128;
    private const string ServiceName = "WinSystemHelper";
    private const string ConfigFileName = "config.json";
    private const string ZipExtension = ".zip";
    private const string UpdateStatusMarkerFileName = "WinSystemHelper-update-status.txt";
    private const string WakeEventLogName = "System";
    private const string WakeEventLogQuery =
        "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]";

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    private readonly ILogger<Worker> _logger;
    private readonly AppConfiguration _configuration;
    private readonly ITelegramBotClient _botClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly object _configurationSync = new();
    private long[] _adminChatIds;
    private readonly AsyncLocal<long?> _replyChatId = new();
    private readonly SemaphoreSlim _resumeNotificationLock = new(1, 1);
    private readonly SemaphoreSlim _micRecordingLock = new(1, 1);
    private readonly SemaphoreSlim _screenshotLock = new(1, 1);
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, PendingConfirmation> _pendingConfirmations = new();
    private readonly object _publicIpSync = new();
    private readonly object _telemetrySync = new();
    private readonly object _alertStateSync = new();
    private readonly object _micLoopSync = new();
    private readonly object _wakeEventWatcherSync = new();
    private readonly CancellationTokenSource _serviceStopping = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private CancellationTokenSource? _micLoopCts;
    private EventLogWatcher? _wakeEventWatcher;
    private string? _cachedPublicIp;
    private DateTimeOffset? _publicIpFetchedAt;
    private DateTimeOffset? _publicIpNextAttemptAt;
    private DateTimeOffset? _lastPollingSuccessAt;
    private DateTimeOffset? _lastPollingFailureAt;
    private DateTimeOffset? _lastWakeAlertAt;
    private DateTimeOffset? _lastStartupAlertAt;
    private DateTimeOffset? _lastSmartAlertAt;
    private string? _lastError;
    private string? _lastPublicIpAlerted;
    private DateTimeOffset? _micLoopStartedAt;
    private long _lastWakeEventRecordId;
    private int _telegramPollingFailureStreak;
    private int _publicIpFailureCount;
    private bool _batteryLowAlertActive;
    private bool _diskLowAlertActive;
    private bool _micLoopLongAlertSent;
    private bool _repeatedTelegramFailureAlertActive;

    public Worker(
        ILogger<Worker> logger,
        AppConfiguration configuration,
        ITelegramBotClient botClient,
        IHttpClientFactory httpClientFactory,
        IHostApplicationLifetime hostLifetime)
    {
        _logger = logger;
        _configuration = configuration;
        _botClient = botClient;
        _httpClientFactory = httpClientFactory;
        _hostLifetime = hostLifetime;
        _adminChatIds = NormalizeAdminChatIds(configuration.GetEffectiveAdminChatIds());
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        RegisterWakeEventWatcher();
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        CancelMicLoop();
        _serviceStopping.Cancel();
        UnregisterWakeEventWatcher();
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource workerStopping = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            _serviceStopping.Token);

        _logger.LogInformation("WinSystemHelper started.");

        if (!await ValidateConfigurationOrStopAsync(workerStopping.Token))
        {
            return;
        }

        QueueBackgroundWork(SendPendingUpdateStatusAsync, "Update status report failed.");
        QueueBackgroundWork(SendStartupAlertAsync, "Startup alert failed.");
        QueueBackgroundWork(RunSmartAlertLoopAsync, "Smart alert loop failed.");
        await RegisterTelegramMenuAsync(workerStopping.Token);

        var offset = 0;
        var pollingFailureAttempt = 0;

        while (!workerStopping.Token.IsCancellationRequested)
        {
            try
            {
                using CancellationTokenSource pollingTimeout =
                    CreateTimeoutTokenSource(workerStopping.Token, TelegramPollNetworkTimeout);

                Update[] updates = await _botClient.GetUpdates(
                    offset: offset,
                    limit: TelegramUpdateLimit,
                    timeout: (int)TelegramPollTimeout.TotalSeconds,
                    allowedUpdates: [UpdateType.Message],
                    cancellationToken: pollingTimeout.Token);

                pollingFailureAttempt = 0;
                RecordPollingSuccess();

                foreach (Update update in updates)
                {
                    offset = update.Id + 1;

                    try
                    {
                        await HandleUpdateAsync(update, workerStopping.Token);
                    }
                    catch (OperationCanceledException) when (workerStopping.Token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process Telegram update {UpdateId}.", update.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (workerStopping.Token.IsCancellationRequested)
            {
                break;
            }
            catch (ApiRequestException ex) when (IsFatalTelegramConfigurationException(ex))
            {
                LogCriticalAndStopForFatalTelegramConfiguration(ex);
                break;
            }
            catch (OperationCanceledException ex)
            {
                pollingFailureAttempt++;
                await DelayAfterPollingFailureAsync(pollingFailureAttempt, ex, workerStopping.Token);
            }
            catch (Exception ex)
            {
                pollingFailureAttempt++;
                await DelayAfterPollingFailureAsync(pollingFailureAttempt, ex, workerStopping.Token);
            }
        }
    }

    private async Task<bool> ValidateConfigurationOrStopAsync(CancellationToken cancellationToken)
    {
        long[] adminChatIds = GetAdminChatIdsSnapshot();
        if (string.IsNullOrWhiteSpace(_configuration.BotToken) || adminChatIds.Length == 0)
        {
            _logger.LogCritical(
                "Fatal configuration error: BotToken or AdminChatIds is missing in config.json. The service will stop.");
            _hostLifetime.StopApplication();
            return false;
        }

        if (!await WaitForInternetConnectivityAsync(cancellationToken))
        {
            return false;
        }

        try
        {
            using CancellationTokenSource botTimeout =
                CreateTimeoutTokenSource(cancellationToken, TelegramSendTimeout);
            User bot = await _botClient.GetMe(botTimeout.Token);

            foreach (long adminChatId in adminChatIds)
            {
                using CancellationTokenSource chatTimeout =
                    CreateTimeoutTokenSource(cancellationToken, TelegramSendTimeout);

                await _botClient.GetChat(
                    chatId: adminChatId,
                    cancellationToken: chatTimeout.Token);
            }

            _logger.LogInformation(
                "Telegram configuration validated for bot @{BotUsername} and {AdminChatCount} admin chat(s).",
                bot.Username,
                adminChatIds.Length);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ApiRequestException ex) when (IsFatalTelegramConfigurationException(ex))
        {
            LogCriticalAndStopForFatalTelegramConfiguration(ex);
            return false;
        }
        catch (Exception ex) when (ex is ApiRequestException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Telegram configuration validation hit a transient error. Polling retry logic will continue.");
            return true;
        }
    }

    private async Task SendStartupAlertAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Startup detected. Waiting for internet connectivity.");

        if (!await WaitForInternetConnectivityAsync(cancellationToken))
        {
            _logger.LogWarning("Internet connectivity was not restored before the startup alert was canceled.");
            return;
        }

        string machineName = Environment.MachineName;
        string userName = Environment.UserName;
        string timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");

        await SendTelegramBroadcastWithRetryAsync(
            $"🚀 Startup/Boot Alert: {machineName} booted up at {timestamp}. User: {userName}.",
            cancellationToken);

        lock (_telemetrySync)
        {
            _lastStartupAlertAt = DateTimeOffset.Now;
        }

        _logger.LogInformation("Startup alert sent.");
    }

    private async Task SendPendingUpdateStatusAsync(CancellationToken cancellationToken)
    {
        string statusMarkerPath = GetUpdateStatusMarkerPath();
        if (!File.Exists(statusMarkerPath))
        {
            return;
        }

        try
        {
            string status = await File.ReadAllTextAsync(statusMarkerPath, Encoding.UTF8, cancellationToken);
            if (!string.IsNullOrWhiteSpace(status))
            {
                await SendTelegramBroadcastOnceAsync(status.Trim(), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read or send pending OTA update status.");
        }
        finally
        {
            DeleteTempFile(statusMarkerPath);
        }
    }

    private void RegisterWakeEventWatcher()
    {
        lock (_wakeEventWatcherSync)
        {
            if (_wakeEventWatcher is not null)
            {
                return;
            }

            try
            {
                EventLogQuery query = new(WakeEventLogName, PathType.LogName, WakeEventLogQuery);
                EventLogWatcher watcher = new(query);
                watcher.EventRecordWritten += OnWakeEventRecordWritten;
                watcher.Enabled = true;

                _wakeEventWatcher = watcher;
                _logger.LogInformation(
                    "Registered wake detector for {ProviderName} Event ID 1.",
                    "Microsoft-Windows-Power-Troubleshooter");
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
            {
                _logger.LogError(
                    ex,
                    "Failed to register wake detector against the Windows System event log.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected failure while registering the Windows System event log wake detector.");
            }
        }
    }

    private void UnregisterWakeEventWatcher()
    {
        EventLogWatcher? watcher;

        lock (_wakeEventWatcherSync)
        {
            watcher = _wakeEventWatcher;
            _wakeEventWatcher = null;
        }

        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.Enabled = false;
            watcher.EventRecordWritten -= OnWakeEventRecordWritten;
            watcher.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister wake event log watcher cleanly.");
        }
    }

    private void OnWakeEventRecordWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventException is not null)
        {
            _logger.LogWarning(e.EventException, "Wake event log watcher reported an error.");
            return;
        }

        EventRecord? record = e.EventRecord;
        if (record is null)
        {
            return;
        }

        try
        {
            long? recordId = record.RecordId;
            if (recordId.HasValue &&
                Interlocked.Exchange(ref _lastWakeEventRecordId, recordId.Value) == recordId.Value)
            {
                return;
            }

            _logger.LogInformation(
                "Power-Troubleshooter resume event detected. EventRecordId: {EventRecordId}.",
                recordId);

            QueueBackgroundWork(SendWakeAlertAsync, "Wake alert failed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process Power-Troubleshooter resume event.");
        }
        finally
        {
            record.Dispose();
        }
    }

    private async Task SendWakeAlertAsync(CancellationToken cancellationToken)
    {
        if (!await _resumeNotificationLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            _logger.LogInformation("Resume detected. Waiting for internet connectivity.");

            if (!await WaitForInternetConnectivityAsync(cancellationToken))
            {
                _logger.LogWarning("Internet connectivity was not restored before the wake alert was canceled.");
                return;
            }

            string machineName = Environment.MachineName;
            string userName = Environment.UserName;
            string timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");

            await SendTelegramBroadcastWithRetryAsync(
                $"🔔 Administrative alert: {machineName} resumed from sleep at {timestamp}. User: {userName}.",
                cancellationToken);

            lock (_telemetrySync)
            {
                _lastWakeAlertAt = DateTimeOffset.Now;
            }

            _logger.LogInformation("Wake alert sent.");
        }
        finally
        {
            _resumeNotificationLock.Release();
        }
    }

    private async Task HandleUpdateAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.Type != UpdateType.Message || update.Message is not { } message)
        {
            return;
        }

        // Security boundary: do not inspect or respond to commands from any non-admin chat.
        long chatId = message.Chat.Id;
        if (!IsAdminChatId(chatId))
        {
            return;
        }

        string? text = (message.Text ?? message.Caption)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string command = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

        long? previousReplyChatId = _replyChatId.Value;
        _replyChatId.Value = chatId;

        try
        {
            switch (command)
            {
                case "/start":
                case "/help":
                    await SendHelpAsync(cancellationToken);
                    break;
                case "/status":
                    await SendStatusAsync(cancellationToken);
                    break;
                case "/healthcheck":
                    await SendHealthCheckAsync(cancellationToken);
                    break;
                case "/version":
                    await SendVersionAsync(cancellationToken);
                    break;
                case "/confirm":
                    await ConfirmPendingCommandAsync(text, chatId, cancellationToken);
                    break;
                case "/cancel":
                    await CancelPendingCommandAsync(text, chatId, cancellationToken);
                    break;
                case "/lock":
                    await LockWorkstationAsync(cancellationToken);
                    break;
                case "/shutdown":
                    await RequestDangerousConfirmationAsync(
                        "shutdown",
                        "Shut down this PC in 10 seconds",
                        ShutdownAsync,
                        cancellationToken);
                    break;
                case "/restart":
                    await RequestDangerousConfirmationAsync(
                        "restart",
                        "Restart this PC in 10 seconds",
                        RestartAsync,
                        cancellationToken);
                    break;
                case "/sleep":
                    await RequestDangerousConfirmationAsync(
                        "sleep",
                        "Put this PC to sleep",
                        SleepAsync,
                        cancellationToken);
                    break;
                case "/ip":
                    await HandlePublicIpCommandAsync(text, cancellationToken);
                    break;
                case "/net":
                    await SendNetworkReportAsync(cancellationToken);
                    break;
                case "/alarm":
                    await TriggerAlarmAsync(cancellationToken);
                    break;
                case "/mic":
                    await HandleMicCommandAsync(text, cancellationToken);
                    break;
                case "/msg":
                    await HandleScreenMessageCommandAsync(text, cancellationToken);
                    break;
                case "/ask":
                    await HandleAskCommandAsync(text, cancellationToken);
                    break;
                case "/prompt":
                    await HandlePromptCommandAsync(text, cancellationToken);
                    break;
                case "/speak":
                    await HandleSpeakCommandAsync(text, cancellationToken);
                    break;
                case "/screen":
                    await HandleScreenshotCommandAsync(cancellationToken);
                    break;
                case "/tasks":
                    HandleTasksCommand();
                    break;
                case "/kill":
                    await HandleKillCommandRequestAsync(text, cancellationToken);
                    break;
                case "/startup":
                    await SendStartupAppsAsync(cancellationToken);
                    break;
                case "/restartapp":
                    await HandleRestartAppCommandRequestAsync(text, cancellationToken);
                    break;
                case "/services":
                    HandleServicesCommand();
                    break;
                case "/service":
                    await HandleServiceCommandAsync(text, cancellationToken);
                    break;
                case "/config":
                    await HandleConfigCommandAsync(text, chatId, cancellationToken);
                    break;
                case "/update":
                    await HandleUpdateCommandRequestAsync(message, text, chatId, cancellationToken);
                    break;
                case "/stop":
                    await RequestDangerousConfirmationAsync(
                        "stop",
                        "Stop the WinSystemHelper service",
                        StopServiceAsync,
                        cancellationToken);
                    break;
                case "/uninstall":
                    await RequestDangerousConfirmationAsync(
                        "uninstall",
                        "Stop and delete the WinSystemHelper service",
                        UninstallServiceRemotelyAsync,
                        cancellationToken);
                    break;
            }
        }
        finally
        {
            _replyChatId.Value = previousReplyChatId;
        }
    }

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
            _logger.LogDebug(ex, "Failed to collect current process metrics for status report.");
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
            $"System uptime: {SafeStatusMetric("system uptime", () => FormatDuration(TimeSpan.FromMilliseconds(Environment.TickCount64)))}",
            $"Battery: {SafeStatusMetric("battery status", GetBatteryStatusText)}",
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
            $"Started: {_startedAt:yyyy-MM-dd HH:mm:ss zzz}",
            $"Service uptime: {FormatDuration(_uptime.Elapsed)}",
            $"Network available: {SafeStatusMetric("network availability", () => NetworkInterface.GetIsNetworkAvailable().ToString())}",
            $"Active console session: {SafeStatusMetric("active console session", GetActiveConsoleSessionStatus)}",
            $"Wake watcher: {GetWakeWatcherStatus()}",
            $"Mic loop: {GetMicLoopStatus()}",
            $"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        await SendTelegramMessageOnceAsync(status, cancellationToken);
    }

    private async Task SendHealthCheckAsync(CancellationToken cancellationToken)
    {
        TelemetrySnapshot telemetry = GetTelemetrySnapshot();
        PublicIpSnapshot publicIp = GetPublicIpSnapshot();
        int pendingConfirmations = RemoveExpiredConfirmations();

        string status = string.Join(
            Environment.NewLine,
            "🩺 WinSystemHelper Healthcheck:",
            $"Service uptime: {FormatDuration(_uptime.Elapsed)}",
            $"Network available: {SafeStatusMetric("network availability", () => NetworkInterface.GetIsNetworkAvailable().ToString())}",
            $"Last poll OK: {FormatNullableTimestamp(telemetry.LastPollingSuccessAt)}",
            $"Last poll failure: {FormatNullableTimestamp(telemetry.LastPollingFailureAt)}",
            $"Polling failure streak: {telemetry.PollingFailureStreak}",
            $"Last wake alert: {FormatNullableTimestamp(telemetry.LastWakeAlertAt)}",
            $"Last startup alert: {FormatNullableTimestamp(telemetry.LastStartupAlertAt)}",
            $"Public IP cache: {FormatPublicIpSnapshot(publicIp)}",
            $"Wake watcher: {GetWakeWatcherStatus()}",
            $"Mic loop: {GetMicLoopStatus()}",
            $"Pending confirmations: {pendingConfirmations}",
            $"Last error: {telemetry.LastError ?? "None"}",
            $"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        await SendTelegramMessageOnceAsync(status, cancellationToken);
    }

    private string SafeStatusMetric(string metricName, Func<string> metricFactory)
    {
        try
        {
            return metricFactory();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to collect status metric {MetricName}.", metricName);
            return "Unavailable";
        }
    }

    private string GetBatteryStatusText()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus powerStatus))
        {
            _logger.LogDebug(
                "Unable to read system power status. Win32 error code: {ErrorCode}.",
                Marshal.GetLastWin32Error());
            return "Unavailable";
        }

        if (powerStatus.BatteryLifePercent == BatteryLifePercentUnknown ||
            (powerStatus.BatteryFlag & BatteryFlagNoSystemBattery) == BatteryFlagNoSystemBattery)
        {
            return "No system battery detected";
        }

        string status = powerStatus.ACLineStatus switch
        {
            0 => "Discharging",
            1 => "Charging",
            _ => "Unknown"
        };

        return $"{powerStatus.BatteryLifePercent}% | {status}";
    }

    private static string GetActiveConsoleSessionStatus()
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        return sessionId == ActiveConsoleSessionUnavailable
            ? "Unavailable"
            : $"Available (Session {sessionId})";
    }

    private string GetWakeWatcherStatus()
    {
        lock (_wakeEventWatcherSync)
        {
            return _wakeEventWatcher is null ? "Unavailable" : "Registered";
        }
    }

    private string GetMicLoopStatus()
    {
        lock (_micLoopSync)
        {
            return _micLoopCts is null ? "Inactive" : "Active";
        }
    }

    private TelemetrySnapshot GetTelemetrySnapshot()
    {
        lock (_telemetrySync)
        {
            return new TelemetrySnapshot(
                _lastPollingSuccessAt,
                _lastPollingFailureAt,
                _lastWakeAlertAt,
                _lastStartupAlertAt,
                _lastSmartAlertAt,
                _lastError,
                _telegramPollingFailureStreak);
        }
    }

    private PublicIpSnapshot GetPublicIpSnapshot()
    {
        lock (_publicIpSync)
        {
            return new PublicIpSnapshot(
                _cachedPublicIp,
                _publicIpFetchedAt,
                _publicIpNextAttemptAt,
                _publicIpFailureCount);
        }
    }

    private void RecordPollingSuccess()
    {
        lock (_telemetrySync)
        {
            _lastPollingSuccessAt = DateTimeOffset.Now;
            _telegramPollingFailureStreak = 0;
        }

        lock (_alertStateSync)
        {
            _repeatedTelegramFailureAlertActive = false;
        }
    }

    private void RecordPollingFailure(Exception exception)
    {
        lock (_telemetrySync)
        {
            _lastPollingFailureAt = DateTimeOffset.Now;
            _telegramPollingFailureStreak++;
            _lastError = $"Telegram polling: {exception.GetType().Name}: {exception.Message}";
        }
    }

    private void RecordError(string error)
    {
        lock (_telemetrySync)
        {
            _lastError = error;
        }
    }

    private static string FormatNullableTimestamp(DateTimeOffset? timestamp)
    {
        return timestamp.HasValue
            ? timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss zzz")
            : "Never";
    }

    private static string FormatPublicIpSnapshot(PublicIpSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.PublicIp))
        {
            return snapshot.NextAttemptAt.HasValue
                ? $"Unavailable; next attempt {snapshot.NextAttemptAt.Value:HH:mm:ss zzz}"
                : "Unavailable";
        }

        string fetchedAt = snapshot.FetchedAt.HasValue
            ? snapshot.FetchedAt.Value.ToString("HH:mm:ss zzz")
            : "unknown time";

        return $"{snapshot.PublicIp} fetched at {fetchedAt}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalDays >= 1
            ? duration.ToString(@"dd\.hh\:mm\:ss")
            : duration.ToString(@"hh\:mm\:ss");
    }

    private async Task SendHelpAsync(CancellationToken cancellationToken)
    {
        string helpText = string.Join(
            Environment.NewLine,
            "📜 WinSystemHelper Commands",
            "",
            "📊 Status & Monitoring",
            "/status - Show service, machine, network, and timestamp details.",
            "/healthcheck - Show fast service health and cached state.",
            "/version - Show the installed WinSystemHelper version.",
            "/ip - Show the cached or refreshed public IP address.",
            "/ip refresh - Force a public IP refresh with timeout/backoff.",
            "/net - Show local network adapters, IPs, gateways, and DNS.",
            "/tasks - 📋 Show the top 10 memory-consuming processes.",
            "/startup - 🚀 List configured startup applications.",
            "/services - 🧩 List Windows services.",
            "",
            "✅ Confirmation",
            "/confirm [Id] - Confirm a pending dangerous command.",
            "/cancel [Id] - Cancel a pending dangerous command.",
            "",
            "🖥️ System Control",
            "/lock - Lock the Windows workstation.",
            "/shutdown - Request shutdown confirmation.",
            "/restart - 🔄 Request restart confirmation.",
            "/sleep - 🌙 Request sleep confirmation.",
            "/alarm - Play a system alert sound.",
            "/kill [ProcessName] - 🔪 Terminate matching processes.",
            "/restartapp [ProcessName] - 🔄 Restart an app in the active session.",
            "/service status|start|stop|restart [ServiceName] - 🧩 Manage a Windows service.",
            "",
            "🎙️ Interactive & Session 0 Tools",
            "/mic [seconds] - Trigger an overt alarm and record audio for up to 60 seconds.",
            "/mic [seconds] loop - Start a persistent overt active alarm loop.",
            "/mic stop - Stop the persistent active alarm loop.",
            "/msg [text] - Show a warning message on the active user's screen.",
            "/ask [text] - Ask the active user a Yes/No question.",
            "/prompt [text] - Force a text response from the active user.",
            "/speak [text] - 🗣️ Speak a message through the active session.",
            "/screen - 🖼️ Capture and return the primary screen.",
            "",
            "⚙️ Configuration",
            "/config - ⚙️ Show safe runtime configuration.",
            "/config export - ⚙️ Show a safe config.json preview.",
            "/config alerts on|off - ⚙️ Enable or disable smart alerts.",
            "/config set [Key] [Value] - ⚙️ Update runtime configuration.",
            "/config admins - 👥 List configured admins.",
            "/config admin add|remove [ChatId] - 👥 Manage admins without reinstall.",
            "",
            "🔄 Service & Updates",
            "/update [https-url] - 🔄 Apply an OTA update from a ZIP file or attached document.",
            "/stop - Request WinSystemHelper service stop.",
            "/uninstall - Request service uninstall.",
            "/help - Show this command list.");

        await SendTelegramMessageOnceAsync(helpText, cancellationToken);
    }

    private async Task SendVersionAsync(CancellationToken cancellationToken)
    {
        Assembly assembly = typeof(Worker).Assembly;
        string version =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Unknown";

        await SendTelegramMessageOnceAsync($"🏷️ WinSystemHelper Version: {version}", cancellationToken);
    }

    private async Task RequestDangerousConfirmationAsync(
        string commandKey,
        string description,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (TryGetCooldownRemaining(commandKey, GetDangerousCommandCooldown(), out TimeSpan remaining))
        {
            await SendTelegramMessageOnceAsync(
                $"⏳ Command cooldown active. Try again in {FormatDurationForHumans(remaining)}.",
                cancellationToken);
            return;
        }

        RemoveExpiredConfirmations();

        int confirmationId = CreateConfirmationId();
        DateTimeOffset expiresAt = DateTimeOffset.Now.Add(GetDangerousCommandConfirmationTimeout());
        long requesterChatId = GetReplyChatId();

        _pendingConfirmations[confirmationId] = new PendingConfirmation(
            confirmationId,
            commandKey,
            description,
            requesterChatId,
            expiresAt,
            operation);

        await SendTelegramMessageOnceAsync(
            string.Join(
                Environment.NewLine,
                $"⚠️ Confirm required: {description}.",
                $"Reply with /confirm {confirmationId} to proceed.",
                $"Reply with /cancel {confirmationId} to cancel.",
                $"Expires: {expiresAt:HH:mm:ss zzz}"),
            cancellationToken);
    }

    private async Task ConfirmPendingCommandAsync(
        string commandText,
        long chatId,
        CancellationToken cancellationToken)
    {
        if (!TryParseConfirmationId(commandText, out int confirmationId))
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /confirm [Id]", cancellationToken);
            return;
        }

        if (!_pendingConfirmations.TryRemove(confirmationId, out PendingConfirmation pending))
        {
            await SendTelegramMessageOnceAsync("⚠️ Confirmation was not found or already expired.", cancellationToken);
            return;
        }

        if (pending.ExpiresAt <= DateTimeOffset.Now)
        {
            await SendTelegramMessageOnceAsync("⚠️ Confirmation expired. Send the command again.", cancellationToken);
            return;
        }

        if (!GetAllowCrossAdminConfirmations() && pending.RequesterChatId != chatId)
        {
            _pendingConfirmations[confirmationId] = pending;
            await SendTelegramMessageOnceAsync("⚠️ This confirmation belongs to another admin.", cancellationToken);
            return;
        }

        MarkCooldown(pending.CommandKey);

        await SendTelegramMessageOnceAsync($"✅ Confirmed: {pending.Description}.", cancellationToken);

        QueueBackgroundWork(
            pending.Operation,
            $"Confirmed command failed: {pending.CommandKey}.");
    }

    private async Task CancelPendingCommandAsync(
        string commandText,
        long chatId,
        CancellationToken cancellationToken)
    {
        if (!TryParseConfirmationId(commandText, out int confirmationId))
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /cancel [Id]", cancellationToken);
            return;
        }

        if (!_pendingConfirmations.TryGetValue(confirmationId, out PendingConfirmation pending))
        {
            await SendTelegramMessageOnceAsync("⚠️ Confirmation was not found or already expired.", cancellationToken);
            return;
        }

        if (!GetAllowCrossAdminConfirmations() && pending.RequesterChatId != chatId)
        {
            await SendTelegramMessageOnceAsync("⚠️ This confirmation belongs to another admin.", cancellationToken);
            return;
        }

        _pendingConfirmations.TryRemove(confirmationId, out _);
        await SendTelegramMessageOnceAsync($"🚫 Canceled: {pending.Description}.", cancellationToken);
    }

    private async Task LockWorkstationAsync(CancellationToken cancellationToken)
    {
        try
        {
            StartProcessInActiveUserSession("rundll32.exe", "user32.dll,LockWorkStation");
            await SendTelegramMessageOnceAsync("🔒 Workstation locked.", cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Unable to lock workstation from the active user session.");
            await SendTelegramMessageOnceAsync(
                $"⚠️ Lock failed: {ex.Message}",
                cancellationToken);
        }
    }

    private async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await SendTelegramMessageOnceAsync("🛑 Initiating shutdown in 10 seconds...", cancellationToken);
        StartDetachedProcess("shutdown.exe", "-s -t 10");
    }

    private async Task RestartAsync(CancellationToken cancellationToken)
    {
        await SendTelegramMessageOnceAsync("🔄 Initiating system restart in 10 seconds...", cancellationToken);
        StartDetachedProcess("shutdown.exe", "-r -t 10");
    }

    private async Task SleepAsync(CancellationToken cancellationToken)
    {
        await SendTelegramMessageOnceAsync("🌙 Putting the workstation to sleep...", cancellationToken);
        StartDetachedProcess("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
    }

    private async Task HandlePublicIpCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string payload = GetCommandPayload(commandText);
        bool forceRefresh = payload.Equals("refresh", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(payload) && !forceRefresh)
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /ip or /ip refresh", cancellationToken);
            return;
        }

        if (forceRefresh &&
            TryGetCooldownRemaining("ip-refresh", GetPublicIpFailureBackoff(), out TimeSpan remaining))
        {
            await SendTelegramMessageOnceAsync(
                $"⏳ Public IP refresh is cooling down. Try again in {FormatDurationForHumans(remaining)}.",
                cancellationToken);
            return;
        }

        PublicIpLookupResult result = await GetPublicIpAsync(forceRefresh, cancellationToken);

        if (result.Success && !string.IsNullOrWhiteSpace(result.PublicIp))
        {
            await SendTelegramMessageOnceAsync(
                $"🌐 Public IP Address: {result.PublicIp}{(result.FromCache ? " (cached)" : string.Empty)}",
                cancellationToken);
            return;
        }

        if (forceRefresh)
        {
            MarkCooldown("ip-refresh");
        }

        string staleText = string.IsNullOrWhiteSpace(result.StalePublicIp)
            ? "No cached IP is available."
            : $"Cached IP: {result.StalePublicIp}.";

        await SendTelegramMessageOnceAsync(
            $"⚠️ Public IP unavailable: {result.ErrorMessage ?? "request failed"}. {staleText}",
            cancellationToken);
    }

    private async Task<PublicIpLookupResult> GetPublicIpAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.Now;

        lock (_publicIpSync)
        {
            if (!forceRefresh &&
                !string.IsNullOrWhiteSpace(_cachedPublicIp) &&
                _publicIpFetchedAt.HasValue &&
                now - _publicIpFetchedAt.Value < GetPublicIpCacheDuration())
            {
                return PublicIpLookupResult.Cached(_cachedPublicIp, _publicIpFetchedAt.Value);
            }

            if (_publicIpNextAttemptAt.HasValue && _publicIpNextAttemptAt.Value > now)
            {
                return PublicIpLookupResult.Failed(
                    $"backoff active until {_publicIpNextAttemptAt.Value:HH:mm:ss zzz}",
                    _cachedPublicIp);
            }
        }

        try
        {
            using CancellationTokenSource timeout =
                CreateTimeoutTokenSource(cancellationToken, PublicIpRefreshTimeout);

            using HttpClient httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = Timeout.InfiniteTimeSpan;

            string publicIp = (await httpClient.GetStringAsync(PublicIpUri, timeout.Token)).Trim();
            if (string.IsNullOrWhiteSpace(publicIp))
            {
                throw new InvalidOperationException("IP service returned an empty response.");
            }

            lock (_publicIpSync)
            {
                _cachedPublicIp = publicIp;
                _publicIpFetchedAt = DateTimeOffset.Now;
                _publicIpNextAttemptAt = null;
                _publicIpFailureCount = 0;
            }

            return PublicIpLookupResult.Fresh(publicIp);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            string? staleIp;
            lock (_publicIpSync)
            {
                _publicIpFailureCount++;
                _publicIpNextAttemptAt = DateTimeOffset.Now.Add(GetPublicIpFailureBackoff());
                staleIp = _cachedPublicIp;
            }

            RecordError($"Public IP lookup failed: {ex.Message}");
            _logger.LogDebug(ex, "Public IP lookup failed.");

            return PublicIpLookupResult.Failed(ex.Message, staleIp);
        }
    }

    private async Task SendNetworkReportAsync(CancellationToken cancellationToken)
    {
        QueueBackgroundWork(SendNetworkReportCoreAsync, "Network report command failed.");
        await Task.CompletedTask;
    }

    private async Task SendNetworkReportCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            List<string> lines =
            [
                "🌐 Network report:",
                $"Network available: {NetworkInterface.GetIsNetworkAvailable()}",
                $"Public IP cache: {FormatPublicIpSnapshot(GetPublicIpSnapshot())}",
                ""
            ];

            NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter =>
                    adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .OrderByDescending(adapter => adapter.Speed)
                .Take(6)
                .ToArray();

            if (adapters.Length == 0)
            {
                lines.Add("No active network adapters found.");
            }

            foreach (NetworkInterface adapter in adapters)
            {
                try
                {
                    IPInterfaceProperties properties = adapter.GetIPProperties();
                    string[] addresses = properties.UnicastAddresses
                        .Where(address =>
                            address.Address.AddressFamily is
                                System.Net.Sockets.AddressFamily.InterNetwork or
                                System.Net.Sockets.AddressFamily.InterNetworkV6)
                        .Select(address => address.Address.ToString())
                        .Take(4)
                        .ToArray();
                    string[] gateways = properties.GatewayAddresses
                        .Select(gateway => gateway.Address.ToString())
                        .Where(address => !string.IsNullOrWhiteSpace(address) && address != "0.0.0.0")
                        .Take(2)
                        .ToArray();
                    string[] dnsServers = properties.DnsAddresses
                        .Select(address => address.ToString())
                        .Take(3)
                        .ToArray();

                    lines.Add($"Adapter: {adapter.Name}");
                    lines.Add($"  Type: {adapter.NetworkInterfaceType} | Speed: {adapter.Speed / 1_000_000:N0} Mbps");
                    lines.Add($"  IP: {(addresses.Length == 0 ? "None" : string.Join(", ", addresses))}");
                    lines.Add($"  Gateway: {(gateways.Length == 0 ? "None" : string.Join(", ", gateways))}");
                    lines.Add($"  DNS: {(dnsServers.Length == 0 ? "None" : string.Join(", ", dnsServers))}");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to inspect network adapter {AdapterName}.", adapter.Name);
                }
            }

            await SendTelegramMessageOnceAsync(
                TruncateForTelegram(string.Join(Environment.NewLine, lines), 3900),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Network report command failed.");
            await SendTelegramMessageOnceAsync($"⚠️ Network report failed: {ex.Message}", CancellationToken.None);
        }
    }

    private async Task TriggerAlarmAsync(CancellationToken cancellationToken)
    {
        try
        {
            TriggerPowerShellBeep();

            await SendTelegramMessageOnceAsync("🚨 Scare alarm triggered via PowerShell!", cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Unable to trigger alarm from the active user session.");
            await SendTelegramMessageOnceAsync(
                $"⚠️ Alarm failed: {ex.Message}",
                cancellationToken);
        }
    }

    private async Task HandleScreenMessageCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string message = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(message))
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /msg [text]", cancellationToken);
            return;
        }

        await SendTelegramMessageOnceAsync("💬 Message sent to the screen.", cancellationToken);

        QueueBackgroundWork(
            _ =>
            {
                ShowScreenMessage(message);
                return Task.CompletedTask;
            },
            "Screen message failed.");
    }

    private async Task HandleAskCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string question = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(question))
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /ask [text]", cancellationToken);
            return;
        }

        QueueBackgroundWork(
            token => AskActiveUserAsync(question, token),
            "Interactive Yes/No prompt failed.");
    }

    private async Task HandlePromptCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string prompt = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /prompt [text]", cancellationToken);
            return;
        }

        QueueBackgroundWork(
            token => PromptActiveUserAsync(prompt, token),
            "Interactive text prompt failed.");
    }

    private async Task HandleSpeakCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string message = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(message))
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /speak [text]", cancellationToken);
            return;
        }

        QueueBackgroundWork(
            token => SpeakInActiveUserSessionAsync(message, token),
            "Text-to-speech command failed.");
    }

    private async Task HandleScreenshotCommandAsync(CancellationToken cancellationToken)
    {
        if (!await _screenshotLock.WaitAsync(0, cancellationToken))
        {
            await SendTelegramMessageOnceAsync(
                "⏳ Screenshot capture is still in progress. Ignoring this request.",
                cancellationToken);
            return;
        }

        await SendTelegramMessageOnceAsync(
            "🖼️ Screenshot capture started. I will send the image when it is ready.",
            cancellationToken);

        QueueBackgroundWork(
            CaptureAndSendScreenshotWithLockAsync,
            "Screenshot command failed.");
    }

    private async Task CaptureAndSendScreenshotWithLockAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CaptureAndSendScreenshotAsync(cancellationToken);
        }
        finally
        {
            _screenshotLock.Release();
        }
    }

    private void HandleTasksCommand()
    {
        QueueBackgroundWork(
            SendTopProcessesAsync,
            "Process list command failed.");
    }

    private async Task HandleKillCommandRequestAsync(string commandText, CancellationToken cancellationToken)
    {
        string processName = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(processName))
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /kill [ProcessName]", cancellationToken);
            return;
        }

        await RequestDangerousConfirmationAsync(
            $"kill:{NormalizeProcessName(processName)}",
            $"Terminate process {NormalizeProcessName(processName)}",
            token => KillProcessesByNameAsync(processName, token),
            cancellationToken);
    }

    private async Task SendStartupAppsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StartupEntry> entries = GetStartupEntries();

        if (entries.Count == 0)
        {
            await SendTelegramMessageOnceAsync(
                "🚀 Startup applications: none found.",
                cancellationToken);
            return;
        }

        StringBuilder message = new();
        message.AppendLine("🚀 Startup applications:");
        message.AppendLine();

        foreach (StartupEntry entry in entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            string line = $"[{entry.Name}] - {TruncateForTelegram(entry.Command, 260)}";
            if (message.Length + line.Length + Environment.NewLine.Length > 3900)
            {
                message.AppendLine("...output truncated.");
                break;
            }

            message.AppendLine(line);
        }

        await SendTelegramMessageOnceAsync(message.ToString().TrimEnd(), cancellationToken);
    }

    private async Task HandleRestartAppCommandRequestAsync(string commandText, CancellationToken cancellationToken)
    {
        string requestedProcessName = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(requestedProcessName))
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /restartapp [ProcessName]", cancellationToken);
            return;
        }

        await RequestDangerousConfirmationAsync(
            $"restartapp:{NormalizeProcessName(requestedProcessName)}",
            $"Restart app {NormalizeProcessName(requestedProcessName)} in the active session",
            token => RestartApplicationAsync(requestedProcessName, token),
            cancellationToken);
    }

    private async Task RestartApplicationAsync(
        string requestedProcessName,
        CancellationToken cancellationToken)
    {
        string processName = NormalizeProcessName(requestedProcessName);
        if (string.IsNullOrWhiteSpace(processName))
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /restartapp [ProcessName]", cancellationToken);
            return;
        }

        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            AppLaunchInfo? launchInfo = TryGetLaunchInfoFromRunningProcesses(processes);
            bool wasRunning = processes.Length > 0;

            if (launchInfo is null)
            {
                launchInfo = TryFindStartupLaunchInfo(processName, GetStartupEntries());
            }

            if (launchInfo is null)
            {
                await SendTelegramMessageOnceAsync(
                    $"⚠️ Could not determine a launch path for {processName}.",
                    cancellationToken);
                return;
            }

            if (wasRunning)
            {
                int terminatedCount = TerminateProcesses(processes, processName);
                if (terminatedCount == 0)
                {
                    await SendTelegramMessageOnceAsync(
                        $"⚠️ {processName} was found, but no matching process could be terminated.",
                        cancellationToken);
                    return;
                }
            }

            StartEncodedPowerShellInActiveUserSession(BuildLaunchApplicationScript(launchInfo.Value));

            string reply = wasRunning
                ? $"🔄 {processName} was killed and restarted successfully in the active session."
                : $"🔄 {processName} was not running, but was started successfully in the active session.";

            await SendTelegramMessageOnceAsync(reply, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Application restart command failed for {ProcessName}.", processName);
            await SendTelegramMessageOnceAsync(
                $"⚠️ Restart failed for {processName}: {ex.Message}",
                CancellationToken.None);
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private AppLaunchInfo? TryGetLaunchInfoFromRunningProcesses(IEnumerable<Process> processes)
    {
        foreach (Process process in processes)
        {
            try
            {
                string? filePath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    return new AppLaunchInfo(filePath, Arguments: string.Empty);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                _logger.LogDebug(
                    ex,
                    "Unable to read executable path for process {ProcessId}.",
                    SafeProcessId(process));
            }
        }

        return null;
    }

    private int TerminateProcesses(IEnumerable<Process> processes, string processName)
    {
        var terminatedCount = 0;

        foreach (Process process in processes)
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);

                if (!process.WaitForExit(10_000))
                {
                    _logger.LogWarning(
                        "Process {ProcessName} with PID {ProcessId} did not exit before timeout.",
                        processName,
                        SafeProcessId(process));
                    continue;
                }

                terminatedCount++;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to terminate process {ProcessName} with PID {ProcessId}.",
                    processName,
                    SafeProcessId(process));
            }
        }

        return terminatedCount;
    }

    private IReadOnlyList<StartupEntry> GetStartupEntries()
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

        string? activeUserSid = TryGetActiveUserSid();
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
            _logger.LogDebug(
                ex,
                "Unable to read startup registry key {Source}: {SubKeyPath}.",
                source,
                subKeyPath);
        }
    }

    private AppLaunchInfo? TryFindStartupLaunchInfo(
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

    private void HandleServicesCommand()
    {
        QueueBackgroundWork(
            SendServicesAsync,
            "Services command failed.");
    }

    private async Task SendServicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            ServiceController[] services = ServiceController.GetServices();
            try
            {
                string[] lines = services
                    .OrderByDescending(service => service.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(service => service.ServiceName, StringComparer.OrdinalIgnoreCase)
                    .Take(30)
                    .Select(service =>
                    {
                        try
                        {
                            return $"{service.ServiceName} - {service.Status}";
                        }
                        catch
                        {
                            return $"{service.ServiceName} - Unknown";
                        }
                    })
                    .ToArray();

                string message = string.Join(
                    Environment.NewLine,
                    ["🧩 Windows services:", .. lines, "", "Use /service status [ServiceName] for details."]);

                await SendTelegramMessageOnceAsync(
                    TruncateForTelegram(message, 3900),
                    cancellationToken);
            }
            finally
            {
                foreach (ServiceController service in services)
                {
                    service.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Services command failed.");
            await SendTelegramMessageOnceAsync($"⚠️ Services command failed: {ex.Message}", CancellationToken.None);
        }
    }

    private async Task HandleServiceCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string[] parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            await SendTelegramMessageOnceAsync(
                "⚠️ Usage: /service status|start|stop|restart [ServiceName]",
                cancellationToken);
            return;
        }

        string action = parts[1].ToLowerInvariant();
        string serviceName = string.Join(' ', parts.Skip(2)).Trim();

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            await SendTelegramMessageOnceAsync(
                "⚠️ Usage: /service status|start|stop|restart [ServiceName]",
                cancellationToken);
            return;
        }

        switch (action)
        {
            case "status":
                QueueBackgroundWork(
                    token => SendServiceStatusAsync(serviceName, token),
                    "Service status command failed.");
                break;
            case "start":
            case "stop":
            case "restart":
                await RequestDangerousConfirmationAsync(
                    $"service:{action}:{serviceName}",
                    $"{action} Windows service {serviceName}",
                    token => ControlServiceAsync(action, serviceName, token),
                    cancellationToken);
                break;
            default:
                await SendTelegramMessageOnceAsync(
                    "⚠️ Usage: /service status|start|stop|restart [ServiceName]",
                    cancellationToken);
                break;
        }
    }

    private async Task SendServiceStatusAsync(string serviceName, CancellationToken cancellationToken)
    {
        try
        {
            using ServiceController service = new(serviceName);
            await SendTelegramMessageOnceAsync(
                $"🧩 Service {service.ServiceName}: {service.Status}",
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            await SendTelegramMessageOnceAsync(
                $"⚠️ Service {serviceName} was not found or could not be queried.",
                cancellationToken);
        }
    }

    private async Task ControlServiceAsync(
        string action,
        string serviceName,
        CancellationToken cancellationToken)
    {
        try
        {
            using ServiceController service = new(serviceName);

            switch (action)
            {
                case "start":
                    if (service.Status == ServiceControllerStatus.Running)
                    {
                        await SendTelegramMessageOnceAsync(
                            $"🧩 Service {service.ServiceName} is already running.",
                            cancellationToken);
                        return;
                    }

                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    break;
                case "stop":
                    if (service.Status == ServiceControllerStatus.Stopped)
                    {
                        await SendTelegramMessageOnceAsync(
                            $"🧩 Service {service.ServiceName} is already stopped.",
                            cancellationToken);
                        return;
                    }

                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    break;
                case "restart":
                    if (service.Status != ServiceControllerStatus.Stopped)
                    {
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }

                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    break;
            }

            await SendTelegramMessageOnceAsync(
                $"✅ Service {service.ServiceName} {action} completed. Current status: {service.Status}.",
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
        {
            _logger.LogWarning(ex, "Service control command failed for {ServiceName}.", serviceName);
            await SendTelegramMessageOnceAsync(
                $"⚠️ Service {serviceName} {action} failed: {ex.Message}",
                CancellationToken.None);
        }
    }

    private async Task HandleConfigCommandAsync(
        string commandText,
        long chatId,
        CancellationToken cancellationToken)
    {
        string[] parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            await SendTelegramMessageOnceAsync(BuildSafeConfigurationReport(), cancellationToken);
            return;
        }

        string section = parts[1].ToLowerInvariant();
        switch (section)
        {
            case "admins":
                await SendTelegramMessageOnceAsync(BuildAdminsReport(), cancellationToken);
                break;
            case "admin":
                await HandleConfigAdminCommandAsync(parts, chatId, cancellationToken);
                break;
            case "alerts":
                await HandleConfigAlertsCommandAsync(parts, cancellationToken);
                break;
            case "export":
            case "raw":
                await SendTelegramMessageOnceAsync(BuildSafeConfigurationJson(), cancellationToken);
                break;
            case "set":
                await HandleConfigSetCommandAsync(parts, cancellationToken);
                break;
            default:
                await SendTelegramMessageOnceAsync(
                    "⚠️ Usage: /config | /config export | /config alerts on|off | /config admins | /config admin add|remove [ChatId] | /config set [Key] [Value]",
                    cancellationToken);
                break;
        }
    }

    private async Task HandleConfigAlertsCommandAsync(
        string[] parts,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 3 || !TryParseBoolean(parts[2], out bool enabled))
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /config alerts on|off", cancellationToken);
            return;
        }

        lock (_configurationSync)
        {
            _configuration.AlertsEnabled = enabled;
        }

        if (!await SaveConfigurationOrReplyAsync(cancellationToken))
        {
            return;
        }

        await SendTelegramMessageOnceAsync(
            enabled ? "✅ Smart alerts enabled." : "✅ Smart alerts disabled.",
            cancellationToken);
    }

    private async Task HandleConfigAdminCommandAsync(
        string[] parts,
        long requesterChatId,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 4 ||
            !long.TryParse(parts[3], out long targetChatId) ||
            targetChatId == 0)
        {
            await SendTelegramMessageOnceAsync(
                "⚠️ Usage: /config admin add|remove [ChatId]",
                cancellationToken);
            return;
        }

        string action = parts[2].ToLowerInvariant();
        switch (action)
        {
            case "add":
                try
                {
                    using CancellationTokenSource timeout =
                        CreateTimeoutTokenSource(cancellationToken, TelegramSendTimeout);
                    await _botClient.GetChat(targetChatId, timeout.Token);
                }
                catch (Exception ex) when (ex is ApiRequestException or HttpRequestException or TaskCanceledException)
                {
                    await SendTelegramMessageOnceAsync(
                        $"⚠️ Admin chat {targetChatId} could not be validated: {ex.Message}",
                        cancellationToken);
                    return;
                }

                UpdateAdminChatIds(current =>
                    current.Contains(targetChatId) ? current : [.. current, targetChatId]);
                if (!await SaveConfigurationOrReplyAsync(cancellationToken))
                {
                    return;
                }

                await SendTelegramMessageOnceAsync($"✅ Admin chat {targetChatId} added.", cancellationToken);
                break;
            case "remove":
                if (targetChatId == requesterChatId)
                {
                    if (GetAdminChatIdsSnapshot().Length <= 1)
                    {
                        await SendTelegramMessageOnceAsync(
                            "⚠️ This is the only configured admin. Add another admin first, then remove this one.",
                            cancellationToken);
                        return;
                    }
                }

                long[] currentAdmins = GetAdminChatIdsSnapshot();
                if (!currentAdmins.Contains(targetChatId))
                {
                    await SendTelegramMessageOnceAsync($"⚠️ Admin chat {targetChatId} is not configured.", cancellationToken);
                    return;
                }

                if (currentAdmins.Length <= 1)
                {
                    await SendTelegramMessageOnceAsync("⚠️ Refusing to remove the last configured admin.", cancellationToken);
                    return;
                }

                UpdateAdminChatIds(current => current.Where(id => id != targetChatId).ToArray());
                if (!await SaveConfigurationOrReplyAsync(cancellationToken))
                {
                    return;
                }

                await SendTelegramMessageOnceAsync($"✅ Admin chat {targetChatId} removed.", cancellationToken);
                break;
            default:
                await SendTelegramMessageOnceAsync(
                    "⚠️ Usage: /config admin add|remove [ChatId]",
                    cancellationToken);
                break;
        }
    }

    private async Task HandleConfigSetCommandAsync(
        string[] parts,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 4)
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /config set [Key] [Value]", cancellationToken);
            return;
        }

        string key = parts[2];
        string value = string.Join(' ', parts.Skip(3));

        if (!TrySetConfigurationValue(key, value, out string? result))
        {
            await SendTelegramMessageOnceAsync($"⚠️ {result}", cancellationToken);
            return;
        }

        if (!await SaveConfigurationOrReplyAsync(cancellationToken))
        {
            return;
        }

        await SendTelegramMessageOnceAsync($"✅ {result}", cancellationToken);
    }

    private string BuildSafeConfigurationReport()
    {
        AppConfiguration configuration = GetConfigurationSnapshot();
        return string.Join(
            Environment.NewLine,
            "⚙️ WinSystemHelper configuration:",
            $"Admins: {GetAdminChatIdsSnapshot().Length}",
            $"AlertsEnabled: {configuration.AlertsEnabled}",
            $"BatteryLowPercent: {configuration.BatteryLowPercent}",
            $"DiskLowPercent: {configuration.DiskLowPercent}",
            $"HealthCheckIntervalMinutes: {configuration.HealthCheckIntervalMinutes}",
            $"PublicIpCacheMinutes: {configuration.PublicIpCacheMinutes}",
            $"PublicIpFailureBackoffMinutes: {configuration.PublicIpFailureBackoffMinutes}",
            $"DangerousCommandConfirmationSeconds: {configuration.DangerousCommandConfirmationSeconds}",
            $"DangerousCommandCooldownSeconds: {configuration.DangerousCommandCooldownSeconds}",
            $"MicCooldownSeconds: {configuration.MicCooldownSeconds}",
            $"AllowCrossAdminConfirmations: {configuration.AllowCrossAdminConfirmations}",
            "BotToken: configured (hidden)");
    }

    private string BuildSafeConfigurationJson()
    {
        AppConfiguration configuration = GetConfigurationSnapshot();
        var safeConfiguration = new
        {
            botToken = "configured (hidden)",
            adminChatIds = GetAdminChatIdsSnapshot(),
            configuration.AlertsEnabled,
            configuration.BatteryLowPercent,
            configuration.DiskLowPercent,
            configuration.HealthCheckIntervalMinutes,
            configuration.PublicIpCacheMinutes,
            configuration.PublicIpFailureBackoffMinutes,
            configuration.DangerousCommandConfirmationSeconds,
            configuration.DangerousCommandCooldownSeconds,
            configuration.MicCooldownSeconds,
            configuration.AllowCrossAdminConfirmations
        };

        return string.Join(
            Environment.NewLine,
            "⚙️ Safe config.json preview:",
            "```json",
            JsonSerializer.Serialize(safeConfiguration, ConfigurationJsonOptions),
            "```");
    }

    private string BuildAdminsReport()
    {
        string[] admins = GetAdminChatIdsSnapshot()
            .Select((admin, index) => $"{index + 1}. {admin}")
            .ToArray();

        return string.Join(
            Environment.NewLine,
            ["👥 Configured admins:", .. admins]);
    }

    private async Task HandleUpdateCommandRequestAsync(
        Message message,
        string commandText,
        long replyChatId,
        CancellationToken cancellationToken)
    {
        if (message.Document is null && string.IsNullOrWhiteSpace(GetCommandPayload(commandText)))
        {
            await SendTelegramMessageOnceAsync(
                "⚠️ Usage: send /update https://example.com/update.zip or attach a .zip file with caption /update.",
                cancellationToken);
            return;
        }

        await RequestDangerousConfirmationAsync(
            "update",
            "Apply an OTA update and restart the service",
            token => HandleUpdateCommandAsync(message, commandText, replyChatId, token),
            cancellationToken);
    }

    private async Task HandleUpdateCommandAsync(
        Message message,
        string commandText,
        long replyChatId,
        CancellationToken cancellationToken)
    {
        if (!await _updateLock.WaitAsync(0, cancellationToken))
        {
            await SendTelegramMessageOnceAsync(
                "⚠️ OTA update is already in progress.",
                cancellationToken);
            return;
        }

        string? updateRoot = null;
        string? updaterScriptPath = null;
        var updaterLaunched = false;

        try
        {
            string updatesRoot = GetSharedTempDirectory("Updates");
            string updateId = Guid.NewGuid().ToString("N");
            updateRoot = Path.Combine(updatesRoot, updateId);
            string extractRoot = Path.Combine(updateRoot, "Extracted");
            string backupDirectory = Path.Combine(updateRoot, "Backup");
            string zipPath = Path.Combine(updateRoot, "package.zip");
            string logPath = Path.Combine(updateRoot, "update.log");
            string statusMarkerPath = GetUpdateStatusMarkerPath();
            updaterScriptPath = Path.Combine(updatesRoot, $"WinSystemHelper-update-{updateId}.ps1");

            Directory.CreateDirectory(updateRoot);
            Directory.CreateDirectory(extractRoot);

            if (message.Document is { } document)
            {
                if (!IsZipFileName(document.FileName))
                {
                    await SendTelegramMessageOnceAsync(
                        "⚠️ Update package rejected: attach a .zip file with caption /update.",
                        cancellationToken);
                    return;
                }

                await DownloadTelegramUpdatePackageAsync(document, zipPath, cancellationToken);
            }
            else
            {
                string urlText = GetCommandPayload(commandText);
                if (string.IsNullOrWhiteSpace(urlText))
                {
                    await SendTelegramMessageOnceAsync(
                        "⚠️ Usage: send /update https://example.com/update.zip or attach a .zip file with caption /update.",
                        cancellationToken);
                    return;
                }

                if (!TryParseUpdateUri(urlText, out Uri? updateUri, out string? uriError))
                {
                    await SendTelegramMessageOnceAsync($"⚠️ Update URL rejected: {uriError}", cancellationToken);
                    return;
                }

                await DownloadUrlUpdatePackageAsync(updateUri!, zipPath, cancellationToken);
            }

            await ExtractUpdatePackageAsync(zipPath, extractRoot, cancellationToken);

            string payloadRoot = ResolveUpdatePayloadRoot(extractRoot);
            ValidateUpdatePayloadRoot(payloadRoot);

            UpdaterScriptOptions scriptOptions = new(
                ServiceName,
                Environment.ProcessId,
                payloadRoot,
                AppContext.BaseDirectory,
                backupDirectory,
                updateRoot,
                statusMarkerPath,
                logPath);

            await WriteUpdaterScriptAsync(updaterScriptPath, scriptOptions, cancellationToken);

            await SendTelegramMessageOnceAsync(
                "🔄 Update received. Service is restarting to apply updates...",
                cancellationToken);

            LaunchUpdaterScript(updaterScriptPath);
            updaterLaunched = true;
            _hostLifetime.StopApplication();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OTA update command failed.");
            await SendTelegramMessageOnceAsync($"⚠️ OTA update failed: {ex.Message}", CancellationToken.None);
        }
        finally
        {
            if (!updaterLaunched)
            {
                DeleteTempFile(updaterScriptPath);
                DeleteDirectorySafe(updateRoot);
            }

            _updateLock.Release();
        }
    }

    private async Task DownloadTelegramUpdatePackageAsync(
        Document document,
        string zipPath,
        CancellationToken cancellationToken)
    {
        if (document.FileSize > MaxUpdatePackageBytes)
        {
            throw new InvalidOperationException(
                $"Update package is too large. Maximum allowed size is {FormatBytes(MaxUpdatePackageBytes)}.");
        }

        using CancellationTokenSource timeout =
            CreateTimeoutTokenSource(cancellationToken, UpdateDownloadTimeout);

        TGFile telegramFile = await _botClient.GetFile(document.FileId, timeout.Token);

        await using FileStream destination = File.Create(zipPath);
        await _botClient.DownloadFile(telegramFile, destination, timeout.Token);

        EnsureFileWithinUpdateSizeLimit(zipPath);
    }

    private async Task DownloadUrlUpdatePackageAsync(
        Uri updateUri,
        string zipPath,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CreateTimeoutTokenSource(cancellationToken, UpdateDownloadTimeout);

        using HttpClient httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        using HttpResponseMessage response = await httpClient.GetAsync(
            updateUri,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength > MaxUpdatePackageBytes)
        {
            throw new InvalidOperationException(
                $"Update package is too large. Maximum allowed size is {FormatBytes(MaxUpdatePackageBytes)}.");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using FileStream destination = File.Create(zipPath);
        await CopyStreamWithLimitAsync(source, destination, MaxUpdatePackageBytes, timeout.Token);

        EnsureFileWithinUpdateSizeLimit(zipPath);
    }

    private static async Task CopyStreamWithLimitAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long totalBytes = 0;

        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                return;
            }

            totalBytes += bytesRead;
            if (totalBytes > maxBytes)
            {
                throw new InvalidOperationException(
                    $"Update package is too large. Maximum allowed size is {FormatBytes(maxBytes)}.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
    }

    private static Task ExtractUpdatePackageAsync(
        string zipPath,
        string extractRoot,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                string fullExtractRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(extractRoot));

                using ZipArchive archive = ZipFile.OpenRead(zipPath);
                if (archive.Entries.Count == 0)
                {
                    throw new InvalidOperationException("Update package is empty.");
                }

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(entry.FullName))
                    {
                        continue;
                    }

                    string destinationPath = Path.GetFullPath(Path.Combine(extractRoot, entry.FullName));
                    if (!IsPathWithinDirectory(destinationPath, fullExtractRoot))
                    {
                        throw new InvalidOperationException(
                            $"Update package contains an unsafe path: {entry.FullName}");
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    entry.ExtractToFile(destinationPath, overwrite: true);
                }
            },
            cancellationToken);
    }

    private static string ResolveUpdatePayloadRoot(string extractRoot)
    {
        string[] rootFiles = Directory.GetFiles(extractRoot);
        string[] rootDirectories = Directory.GetDirectories(extractRoot);

        if (rootFiles.Length == 0 && rootDirectories.Length == 1)
        {
            return rootDirectories[0];
        }

        return extractRoot;
    }

    private static void ValidateUpdatePayloadRoot(string payloadRoot)
    {
        string expectedExecutableName = Path.GetFileName(GetCurrentExecutablePath());
        string expectedExecutablePath = Path.Combine(payloadRoot, expectedExecutableName);

        if (!File.Exists(expectedExecutablePath))
        {
            throw new InvalidOperationException(
                $"Update package must contain {expectedExecutableName} at the payload root.");
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

    private static void LaunchUpdaterScript(string scriptPath)
    {
        StartDetachedProcess(
            GetWindowsPowerShellPath(),
            $"-NoProfile -ExecutionPolicy Bypass -File {QuoteCommandLineArgument(scriptPath)}");
    }

    private static string GetCommandPayload(string commandText)
    {
        int separatorIndex = commandText.IndexOf(' ');
        if (separatorIndex < 0 || separatorIndex == commandText.Length - 1)
        {
            return string.Empty;
        }

        return commandText[(separatorIndex + 1)..].Trim();
    }

    private static void ShowScreenMessage(string message)
    {
        string script = BuildMessageBoxScript(
            message,
            buttons: "OK",
            icon: "Warning",
            includeAskExitCodes: false);

        StartEncodedPowerShellInActiveUserSession(script);
    }

    private async Task AskActiveUserAsync(string question, CancellationToken cancellationToken)
    {
        try
        {
            string script = BuildMessageBoxScript(
                question,
                buttons: "YesNo",
                icon: "Question",
                includeAskExitCodes: true);

            uint exitCode = await StartEncodedPowerShellInActiveUserSessionAndWaitAsync(
                script,
                AskPromptTimeout,
                cancellationToken);

            string answer = exitCode switch
            {
                AskYesExitCode => "YES",
                AskNoExitCode => "NO",
                _ => $"UNKNOWN (ExitCode {exitCode})"
            };

            await SendTelegramMessageOnceAsync($"🗣️ User answered: {answer}", cancellationToken);
        }
        catch (System.TimeoutException)
        {
            await SendTelegramBroadcastOnceAsync("⏳ User ignored the prompt (Timeout).", CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Interactive Yes/No prompt failed.");
            await SendTelegramMessageOnceAsync($"⚠️ Ask prompt failed: {ex.Message}", CancellationToken.None);
        }
    }

    private async Task PromptActiveUserAsync(string prompt, CancellationToken cancellationToken)
    {
        string? tempFile = null;

        try
        {
            tempFile = Path.Combine(
                GetSharedTempDirectory("Interaction"),
                $"WinSystemHelper-prompt-{Guid.NewGuid():N}.txt");

            string script = BuildInputBoxScript(prompt, tempFile);

            _ = await StartEncodedPowerShellInActiveUserSessionAndWaitAsync(
                script,
                TextPromptTimeout,
                cancellationToken);

            if (!File.Exists(tempFile))
            {
                await SendTelegramMessageOnceAsync("⚠️ User prompt returned no response file.", cancellationToken);
                return;
            }

            string response = await File.ReadAllTextAsync(tempFile, Encoding.UTF8, cancellationToken);
            await SendTelegramMessageOnceAsync($"📝 User replied: {response}", cancellationToken);
        }
        catch (System.TimeoutException)
        {
            await SendTelegramBroadcastOnceAsync("⏳ User ignored the prompt (Timeout).", CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Interactive text prompt failed.");
            await SendTelegramMessageOnceAsync($"⚠️ Text prompt failed: {ex.Message}", CancellationToken.None);
        }
        finally
        {
            DeleteTempFile(tempFile);
        }
    }

    private async Task SpeakInActiveUserSessionAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            StartEncodedPowerShellInActiveUserSession(BuildSpeakScript(message));
            await SendTelegramMessageOnceAsync("🗣️ Audio message played.", cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Text-to-speech command failed.");
            await SendTelegramMessageOnceAsync($"⚠️ Speak command failed: {ex.Message}", CancellationToken.None);
        }
    }

    private async Task CaptureAndSendScreenshotAsync(CancellationToken cancellationToken)
    {
        string? tempFile = null;

        try
        {
            tempFile = Path.Combine(
                GetSharedTempDirectory("Screen"),
                $"WinSystemHelper-screen-{Guid.NewGuid():N}.jpg");

            string script = BuildScreenshotScript(tempFile);

            uint exitCode = await StartEncodedPowerShellInActiveUserSessionAndWaitAsync(
                script,
                ScreenshotTimeout,
                cancellationToken);

            if (exitCode != 0)
            {
                await SendTelegramMessageOnceAsync(
                    $"⚠️ Screenshot capture failed. ExitCode: {exitCode}.",
                    cancellationToken);
                return;
            }

            FileInfo screenshotFile = new(tempFile);
            if (!screenshotFile.Exists || screenshotFile.Length == 0)
            {
                await SendTelegramMessageOnceAsync("⚠️ Screenshot capture produced no image.", cancellationToken);
                return;
            }

            await using FileStream photoStream = File.OpenRead(tempFile);
            using CancellationTokenSource timeout =
                CreateTimeoutTokenSource(cancellationToken, TelegramSendTimeout);

            await _botClient.SendPhoto(
                chatId: GetReplyChatId(),
                photo: InputFile.FromStream(photoStream, "screen.jpg"),
                caption: $"🖼️ Screenshot captured at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}.",
                cancellationToken: timeout.Token);
        }
        catch (System.TimeoutException)
        {
            await SendTelegramMessageOnceAsync("⏳ Screenshot capture timed out.", CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Screenshot command failed.");
            await SendTelegramMessageOnceAsync($"⚠️ Screenshot failed: {ex.Message}", CancellationToken.None);
        }
        finally
        {
            DeleteTempFile(tempFile);
        }
    }

    private async Task SendTopProcessesAsync(CancellationToken cancellationToken)
    {
        try
        {
            string[] lines = Process.GetProcesses()
                .Select(process =>
                {
                    try
                    {
                        return new
                        {
                            process.ProcessName,
                            process.Id,
                            process.WorkingSet64
                        };
                    }
                    catch
                    {
                        return null;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                })
                .Where(process => process is not null)
                .OrderByDescending(process => process!.WorkingSet64)
                .Take(10)
                .Select((process, index) =>
                    $"{index + 1}. {process!.ProcessName} | PID {process.Id} | {process.WorkingSet64 / 1024 / 1024:N0} MB")
                .ToArray();

            string message = string.Join(
                Environment.NewLine,
                ["📋 Top memory-consuming processes:", .. lines]);

            await SendTelegramMessageOnceAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process list command failed.");
            await SendTelegramMessageOnceAsync($"⚠️ Task list failed: {ex.Message}", CancellationToken.None);
        }
    }

    private async Task KillProcessesByNameAsync(string requestedProcessName, CancellationToken cancellationToken)
    {
        string processName = NormalizeProcessName(requestedProcessName);
        if (string.IsNullOrWhiteSpace(processName))
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /kill [ProcessName]", cancellationToken);
            return;
        }

        var matchedProcessCount = 0;
        var terminatedProcessCount = 0;

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (!process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matchedProcessCount++;
                process.Kill();
                terminatedProcessCount++;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                _logger.LogWarning(ex, "Failed to terminate process {ProcessName}.", processName);
            }
            finally
            {
                process.Dispose();
            }
        }

        if (matchedProcessCount == 0)
        {
            await SendTelegramMessageOnceAsync(
                $"⚠️ Process {processName} not found.",
                cancellationToken);
            return;
        }

        if (terminatedProcessCount == 0)
        {
            await SendTelegramMessageOnceAsync(
                $"⚠️ Process {processName} was found but could not be terminated.",
                cancellationToken);
            return;
        }

        await SendTelegramMessageOnceAsync(
            $"🔪 Process {processName} terminated.",
            cancellationToken);
    }
    private async Task HandleMicCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        MicCommand micCommand = ParseMicCommand(commandText);

        if (micCommand.Stop)
        {
            await StopMicAlarmLoopAsync(cancellationToken);
            return;
        }

        if (TryGetCooldownRemaining("mic", GetMicCooldown(), out TimeSpan remaining))
        {
            await SendTelegramMessageOnceAsync(
                $"⏳ Mic command cooldown active. Try again in {FormatDurationForHumans(remaining)}.",
                cancellationToken);
            return;
        }

        MarkCooldown("mic");

        if (micCommand.Loop)
        {
            await StartMicAlarmLoopAsync(micCommand.DurationSeconds, cancellationToken);
            return;
        }

        await StartMicAlarmRecordingAsync(micCommand.DurationSeconds, cancellationToken);
    }

    private async Task StartMicAlarmRecordingAsync(int durationSeconds, CancellationToken cancellationToken)
    {

        if (!await _micRecordingLock.WaitAsync(0, cancellationToken))
        {
            await SendTelegramMessageOnceAsync("🎤 Active Alarm recording is already running.", cancellationToken);
            return;
        }

        _micRecordingLock.Release();

        await SendTelegramMessageOnceAsync(
            $"🎤 Active Alarm triggered. Scaring intruder and recording for {durationSeconds}s...",
            cancellationToken);

        _ = Task.Run(
            () => RecordAndSendMicAlarmAsync(durationSeconds, _serviceStopping.Token),
            _serviceStopping.Token);
    }

    private async Task StartMicAlarmLoopAsync(int durationSeconds, CancellationToken cancellationToken)
    {
        CancellationTokenSource? loopCts = null;

        lock (_micLoopSync)
        {
            if (_micLoopCts is null)
            {
                loopCts = CancellationTokenSource.CreateLinkedTokenSource(_serviceStopping.Token);
                _micLoopCts = loopCts;
                _micLoopStartedAt = DateTimeOffset.Now;
                _micLoopLongAlertSent = false;
            }
        }

        if (loopCts is null)
        {
            await SendTelegramMessageOnceAsync(
                "⚠️ Active Alarm loop is already running! Send /mic stop first.",
                cancellationToken);
            return;
        }

        try
        {
            await SendTelegramMessageOnceAsync(
                $"🎤 Persistent Active Alarm loop started ({durationSeconds}s cycles).",
                cancellationToken);

            _ = Task.Run(
                () => RunMicAlarmLoopAsync(durationSeconds, loopCts),
                CancellationToken.None);
        }
        catch
        {
            lock (_micLoopSync)
            {
                if (ReferenceEquals(_micLoopCts, loopCts))
                {
                    _micLoopCts = null;
                    _micLoopStartedAt = null;
                }
            }

            loopCts.Cancel();
            loopCts.Dispose();
            throw;
        }
    }

    private async Task StopMicAlarmLoopAsync(CancellationToken cancellationToken)
    {
        CancelMicLoop();

        await SendTelegramMessageOnceAsync(
            "🛑 Persistent Active Alarm stopped.",
            cancellationToken);
    }

    private async Task RunMicAlarmLoopAsync(int durationSeconds, CancellationTokenSource loopCts)
    {
        try
        {
            while (!loopCts.Token.IsCancellationRequested)
            {
                try
                {
                    bool cycleCompleted = await RecordAndSendMicAlarmAsync(durationSeconds, loopCts.Token);
                    if (!cycleCompleted)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), loopCts.Token);
                    }
                }
                catch (OperationCanceledException) when (loopCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Persistent active alarm loop cycle failed.");

                    try
                    {
                        await SendTelegramMessageOnceAsync(
                            $"⚠️ Persistent Active Alarm cycle failed: {ex.Message}",
                            CancellationToken.None);
                    }
                    catch (Exception sendException)
                    {
                        _logger.LogWarning(sendException, "Failed to report persistent active alarm loop failure.");
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), loopCts.Token);
                    }
                    catch (OperationCanceledException) when (loopCts.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            lock (_micLoopSync)
            {
                if (ReferenceEquals(_micLoopCts, loopCts))
                {
                    _micLoopCts = null;
                    _micLoopStartedAt = null;
                }
            }

            loopCts.Dispose();
        }
    }

    private async Task<bool> RecordAndSendMicAlarmAsync(int durationSeconds, CancellationToken cancellationToken)
    {
        string? tempFile = null;

        if (!await _micRecordingLock.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            TriggerOvertRecordingWarnings();
            TriggerPowerShellBeep();

            tempFile = Path.Combine(
                GetSharedTempDirectory("Audio"),
                $"WinSystemHelper-active-alarm-{Guid.NewGuid():N}.wav");

            await RunMicRecorderHelperAsync(tempFile, durationSeconds, cancellationToken);

            await using FileStream voiceStream = File.OpenRead(tempFile);
            using CancellationTokenSource timeout =
                CreateTimeoutTokenSource(cancellationToken, TelegramSendTimeout);

            await _botClient.SendVoice(
                chatId: GetReplyChatId(),
                voice: InputFile.FromStream(voiceStream, "active-alarm.wav"),
                duration: durationSeconds,
                caption: $"🎤 Active Alarm recording ({durationSeconds}s).",
                cancellationToken: timeout.Token);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Active alarm audio recording failed.");
            await SendTelegramMessageOnceAsync(
                $"⚠️ Active Alarm recording failed: {ex.Message}",
                CancellationToken.None);

            return false;
        }
        finally
        {
            _micRecordingLock.Release();

            DeleteTempFile(tempFile);
        }
    }

    private static MicCommand ParseMicCommand(string commandText)
    {
        string[] parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var durationSeconds = DefaultMicRecordingSeconds;
        var loop = false;

        foreach (string part in parts.Skip(1))
        {
            if (part.Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                return new MicCommand(durationSeconds, Loop: false, Stop: true);
            }

            if (part.Equals("loop", StringComparison.OrdinalIgnoreCase))
            {
                loop = true;
                continue;
            }

            if (int.TryParse(part, out int requestedSeconds))
            {
                durationSeconds = Math.Clamp(requestedSeconds, 1, MaxMicRecordingSeconds);
            }
        }

        return new MicCommand(durationSeconds, loop, Stop: false);
    }

    private static void TriggerOvertRecordingWarnings()
    {
        StartProcessInActiveUserSession(
            "msg.exe",
            "* \"🚨 SECURITY BREACH: Audio recording is now ACTIVE and being transmitted to the administrator.\"");

        StartProcessInActiveUserSession(
            GetWindowsPowerShellPath(),
            "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"(New-Object -ComObject SAPI.SpVoice).Speak('Security alert. Audio recording activated.')\"");
    }

    private static void TriggerPowerShellBeep()
    {
        StartProcessInActiveUserSession(
            GetWindowsPowerShellPath(),
            "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"[console]::beep(3000, 3000)\"");
    }

    private static void StartEncodedPowerShellInActiveUserSession(string script)
    {
        StartProcessInActiveUserSession(
            GetWindowsPowerShellPath(),
            BuildEncodedPowerShellArguments(script));
    }

    private static Task<uint> StartEncodedPowerShellInActiveUserSessionAndWaitAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return StartProcessInActiveUserSessionAndWaitAsync(
            GetWindowsPowerShellPath(),
            BuildEncodedPowerShellArguments(script),
            timeout,
            cancellationToken);
    }

    private static string BuildEncodedPowerShellArguments(string script)
    {
        string base64Script = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {base64Script}";
    }

    private static string BuildMessageBoxScript(
        string message,
        string buttons,
        string icon,
        bool includeAskExitCodes)
    {
        string encodedMessage = EncodeUtf8Base64(message);
        string script = $$"""
            Add-Type -AssemblyName System.Windows.Forms
            $message = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{encodedMessage}}'))
            $result = [System.Windows.Forms.MessageBox]::Show($message, 'WinSystemHelper', [System.Windows.Forms.MessageBoxButtons]::{{buttons}}, [System.Windows.Forms.MessageBoxIcon]::{{icon}})
            """;

        if (!includeAskExitCodes)
        {
            return script + Environment.NewLine + "exit 0";
        }

        return script + Environment.NewLine + $$"""
            if ($result -eq [System.Windows.Forms.DialogResult]::Yes) {
                exit {{AskYesExitCode}}
            }

            exit {{AskNoExitCode}}
            """;
    }

    private static string BuildInputBoxScript(string prompt, string outputPath)
    {
        string encodedPrompt = EncodeUtf8Base64(prompt);
        string encodedOutputPath = EncodeUtf8Base64(outputPath);

        return $$"""
            Add-Type -AssemblyName Microsoft.VisualBasic
            $promptText = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{encodedPrompt}}'))
            $outputPath = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{encodedOutputPath}}'))
            $response = ''

            while ([string]::IsNullOrWhiteSpace($response)) {
                $response = [Microsoft.VisualBasic.Interaction]::InputBox($promptText, 'WinSystemHelper', '')
            }

            [System.IO.File]::WriteAllText($outputPath, $response, [System.Text.Encoding]::UTF8)
            exit 0
            """;
    }

    private static string BuildSpeakScript(string message)
    {
        string encodedMessage = EncodeUtf8Base64(message);

        return $$"""
            $message = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{encodedMessage}}'))
            (New-Object -ComObject SAPI.SpVoice).Speak($message) | Out-Null
            exit 0
            """;
    }

    private static string BuildLaunchApplicationScript(AppLaunchInfo launchInfo)
    {
        string encodedFilePath = EncodeUtf8Base64(launchInfo.FilePath);
        string encodedArguments = EncodeUtf8Base64(launchInfo.Arguments);

        return $$"""
            $ErrorActionPreference = 'Stop'
            $filePath = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{encodedFilePath}}'))
            $arguments = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{encodedArguments}}'))
            $startInfo = @{
                FilePath = $filePath
            }

            try {
                $workingDirectory = [System.IO.Path]::GetDirectoryName($filePath)
                if (-not [string]::IsNullOrWhiteSpace($workingDirectory) -and (Test-Path -LiteralPath $workingDirectory)) {
                    $startInfo.WorkingDirectory = $workingDirectory
                }
            }
            catch {
            }

            if (-not [string]::IsNullOrWhiteSpace($arguments)) {
                $startInfo.ArgumentList = $arguments
            }

            Start-Process @startInfo
            exit 0
            """;
    }

    private static string BuildScreenshotScript(string outputPath)
    {
        string encodedOutputPath = EncodeUtf8Base64(outputPath);

        return $$"""
            $ErrorActionPreference = 'Stop'

            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;

            public static class DpiNative {
                [DllImport("user32.dll")]
                public static extern bool SetProcessDPIAware();

                [DllImport("shcore.dll")]
                public static extern int SetProcessDpiAwareness(int value);

                [DllImport("user32.dll")]
                public static extern int GetSystemMetrics(int index);
            }
            '@

            try {
                [DpiNative]::SetProcessDpiAwareness(2) | Out-Null
            }
            catch {
                [DpiNative]::SetProcessDPIAware() | Out-Null
            }

            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing

            $outputPath = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{{encodedOutputPath}}'))
            $screen = [System.Windows.Forms.Screen]::PrimaryScreen
            $bounds = $screen.Bounds
            $width = [DpiNative]::GetSystemMetrics(0)
            $height = [DpiNative]::GetSystemMetrics(1)

            if ($width -le 0 -or $height -le 0) {
                $width = $bounds.Width
                $height = $bounds.Height
            }

            $bitmap = New-Object System.Drawing.Bitmap $width, $height
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

            try {
                $graphics.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, (New-Object System.Drawing.Size $width, $height))
                $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Jpeg)
                exit 0
            }
            catch {
                exit 1
            }
            finally {
                if ($null -ne $graphics) {
                    $graphics.Dispose()
                }

                if ($null -ne $bitmap) {
                    $bitmap.Dispose()
                }
            }
            """;
    }

    private static string BuildUpdaterScript(UpdaterScriptOptions options)
    {
        string encodedServiceName = EncodeUtf8Base64(options.ServiceName);
        string encodedPayloadRoot = EncodeUtf8Base64(options.PayloadRoot);
        string encodedTargetDirectory = EncodeUtf8Base64(options.TargetDirectory);
        string encodedBackupDirectory = EncodeUtf8Base64(options.BackupDirectory);
        string encodedUpdateRoot = EncodeUtf8Base64(options.UpdateRoot);
        string encodedStatusMarkerPath = EncodeUtf8Base64(options.StatusMarkerPath);
        string encodedLogPath = EncodeUtf8Base64(options.LogPath);

        return $$"""
            $ErrorActionPreference = 'Stop'

            function Decode-Base64Utf8([string]$value) {
                return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($value))
            }

            $ServiceName = Decode-Base64Utf8 '{{encodedServiceName}}'
            $TargetProcessId = {{options.ProcessId}}
            $PayloadRoot = Decode-Base64Utf8 '{{encodedPayloadRoot}}'
            $TargetDirectory = Decode-Base64Utf8 '{{encodedTargetDirectory}}'
            $BackupDirectory = Decode-Base64Utf8 '{{encodedBackupDirectory}}'
            $UpdateRoot = Decode-Base64Utf8 '{{encodedUpdateRoot}}'
            $StatusMarkerPath = Decode-Base64Utf8 '{{encodedStatusMarkerPath}}'
            $LogPath = Decode-Base64Utf8 '{{encodedLogPath}}'
            $ConfigFileName = '{{ConfigFileName}}'
            $PathTrimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)

            function Write-Log([string]$message) {
                $line = "$(Get-Date -Format o) $message"
                $logDirectory = [System.IO.Path]::GetDirectoryName($LogPath)
                if (-not [string]::IsNullOrWhiteSpace($logDirectory)) {
                    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
                }

                Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
            }

            function Write-UpdateStatus([string]$message) {
                $statusDirectory = [System.IO.Path]::GetDirectoryName($StatusMarkerPath)
                if (-not [string]::IsNullOrWhiteSpace($statusDirectory)) {
                    New-Item -ItemType Directory -Path $statusDirectory -Force | Out-Null
                }

                Set-Content -LiteralPath $StatusMarkerPath -Value $message -Encoding UTF8
            }

            function Invoke-Retry([scriptblock]$action, [string]$description) {
                $lastError = $null

                for ($attempt = 1; $attempt -le 30; $attempt++) {
                    try {
                        & $action
                        return
                    }
                    catch {
                        $lastError = $_
                        Write-Log "$description failed on attempt ${attempt}: $($_.Exception.Message)"

                        if ($attempt -lt 30) {
                            Start-Sleep -Seconds 2
                        }
                    }
                }

                throw "Failed to complete '$description' after 30 attempts. Last error: $($lastError.Exception.Message)"
            }

            function Copy-Tree([string]$source, [string]$destination) {
                if (-not (Test-Path -LiteralPath $source)) {
                    throw "Source path does not exist: $source"
                }

                New-Item -ItemType Directory -Path $destination -Force | Out-Null

                Get-ChildItem -LiteralPath $source -Recurse -Directory -Force | ForEach-Object {
                    $relative = $_.FullName.Substring($source.Length).TrimStart($PathTrimChars)
                    if (-not [string]::IsNullOrWhiteSpace($relative)) {
                        New-Item -ItemType Directory -Path (Join-Path $destination $relative) -Force | Out-Null
                    }
                }

                Get-ChildItem -LiteralPath $source -Recurse -File -Force | Where-Object {
                    -not $_.Name.Equals($ConfigFileName, [System.StringComparison]::OrdinalIgnoreCase)
                } | ForEach-Object {
                    $file = $_
                    $relative = $file.FullName.Substring($source.Length).TrimStart($PathTrimChars)
                    $targetPath = Join-Path $destination $relative
                    $targetParent = [System.IO.Path]::GetDirectoryName($targetPath)

                    if (-not [string]::IsNullOrWhiteSpace($targetParent)) {
                        New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
                    }

                    Invoke-Retry { Copy-Item -LiteralPath $file.FullName -Destination $targetPath -Force } "copy $relative"
                }
            }

            function Wait-ForOriginalProcessExit {
                Start-Sleep -Seconds 3
                & sc.exe stop $ServiceName | Out-Null

                $deadline = (Get-Date).AddSeconds(90)
                while ((Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Seconds 1
                }

                if (Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue) {
                    throw "Original service process $TargetProcessId did not exit before timeout."
                }
            }

            function Start-TargetService {
                & sc.exe start $ServiceName | Out-Null
                Write-Log "Service start requested."
            }

            try {
                Write-Log "OTA update script started."
                Wait-ForOriginalProcessExit

                if (Test-Path -LiteralPath $BackupDirectory) {
                    Remove-Item -LiteralPath $BackupDirectory -Recurse -Force -ErrorAction SilentlyContinue
                }

                New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null

                Write-Log "Backing up current service files."
                Copy-Tree -source $TargetDirectory -destination $BackupDirectory

                Write-Log "Copying staged update files."
                Copy-Tree -source $PayloadRoot -destination $TargetDirectory

                Write-UpdateStatus "✅ OTA update applied successfully at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')."
                Start-TargetService
                Write-Log "OTA update completed successfully."
            }
            catch {
                $failureMessage = $_.Exception.Message
                Write-Log "OTA update failed: $failureMessage"

                try {
                    if (Test-Path -LiteralPath $BackupDirectory) {
                        Write-Log "Attempting rollback from backup."
                        Copy-Tree -source $BackupDirectory -destination $TargetDirectory
                        Write-UpdateStatus "⚠️ OTA update failed and rollback was attempted at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'). Error: $failureMessage"
                    }
                    else {
                        Write-UpdateStatus "⚠️ OTA update failed before backup was created at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'). Error: $failureMessage"
                    }
                }
                catch {
                    Write-UpdateStatus "🚨 OTA update failed and rollback also failed at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'). Update error: $failureMessage Rollback error: $($_.Exception.Message)"
                    Write-Log "Rollback failed: $($_.Exception.Message)"
                }

                try {
                    Start-TargetService
                }
                catch {
                    Write-Log "Service restart failed after update failure: $($_.Exception.Message)"
                }
            }
            finally {
                try {
                    if (Test-Path -LiteralPath $UpdateRoot) {
                        Remove-Item -LiteralPath $UpdateRoot -Recurse -Force -ErrorAction SilentlyContinue
                    }
                }
                catch {
                    Write-Log "Failed to delete update staging directory: $($_.Exception.Message)"
                }

                $scriptPath = $MyInvocation.MyCommand.Path
                $escapedScriptPath = $scriptPath.Replace("'", "''")
                $cleanupScript = "Start-Sleep -Seconds 5; Remove-Item -LiteralPath '$escapedScriptPath' -Force -ErrorAction SilentlyContinue"
                $cleanupBytes = [System.Text.Encoding]::Unicode.GetBytes($cleanupScript)
                $cleanupEncoded = [System.Convert]::ToBase64String($cleanupBytes)
                Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand $cleanupEncoded" -WindowStyle Hidden
            }
            """;
    }

    private static string EncodeUtf8Base64(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string TruncateForTelegram(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static int SafeProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return 0;
        }
    }

    private static string? TryGetActiveUserSid()
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == ActiveConsoleSessionUnavailable)
        {
            return null;
        }

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
        {
            return null;
        }

        try
        {
            using WindowsIdentity identity = new(userToken);
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    private static bool IsZipFileName(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName) &&
            Path.GetExtension(fileName).Equals(ZipExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseUpdateUri(string urlText, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri? parsedUri))
        {
            error = "provide an absolute HTTPS URL ending in .zip.";
            return false;
        }

        if (!parsedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "only HTTPS update URLs are allowed.";
            return false;
        }

        if (!Path.GetExtension(parsedUri.AbsolutePath).Equals(ZipExtension, StringComparison.OrdinalIgnoreCase))
        {
            error = "update URL must point to a .zip file.";
            return false;
        }

        uri = parsedUri;
        return true;
    }

    private static void EnsureFileWithinUpdateSizeLimit(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length == 0)
        {
            throw new InvalidOperationException("Update package download produced an empty file.");
        }

        if (file.Length > MaxUpdatePackageBytes)
        {
            throw new InvalidOperationException(
                $"Update package is too large. Maximum allowed size is {FormatBytes(MaxUpdatePackageBytes)}.");
        }
    }

    private static string FormatBytes(long bytes)
    {
        return $"{bytes / 1024d / 1024d:N1} MB";
    }

    private static string EnsureTrailingDirectorySeparator(string directory)
    {
        return directory.EndsWith(Path.DirectorySeparatorChar) ||
            directory.EndsWith(Path.AltDirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = EnsureTrailingDirectorySeparator(Path.GetFullPath(directory));
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteCommandLineArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string GetUpdateStatusMarkerPath()
    {
        return Path.Combine(GetSharedTempDirectory("Updates"), UpdateStatusMarkerFileName);
    }

    private static string NormalizeProcessName(string processName)
    {
        string normalized = processName.Trim();
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private static long[] NormalizeAdminChatIds(IEnumerable<long> adminChatIds)
    {
        return adminChatIds
            .Where(chatId => chatId != 0)
            .Distinct()
            .ToArray();
    }

    private long[] GetAdminChatIdsSnapshot()
    {
        return Volatile.Read(ref _adminChatIds);
    }

    private bool IsAdminChatId(long chatId)
    {
        return GetAdminChatIdsSnapshot().Contains(chatId);
    }

    private void UpdateAdminChatIds(Func<long[], long[]> update)
    {
        lock (_configurationSync)
        {
            long[] updated = NormalizeAdminChatIds(update(GetAdminChatIdsSnapshot()));
            _configuration.AdminChatIds = updated;
            _configuration.AdminChatId = 0;
            Volatile.Write(ref _adminChatIds, updated);
        }
    }

    private AppConfiguration GetConfigurationSnapshot()
    {
        lock (_configurationSync)
        {
            return new AppConfiguration
            {
                BotToken = _configuration.BotToken,
                AdminChatIds = GetAdminChatIdsSnapshot(),
                AdminChatId = _configuration.AdminChatId,
                AlertsEnabled = _configuration.AlertsEnabled,
                BatteryLowPercent = _configuration.BatteryLowPercent,
                DiskLowPercent = _configuration.DiskLowPercent,
                HealthCheckIntervalMinutes = _configuration.HealthCheckIntervalMinutes,
                PublicIpCacheMinutes = _configuration.PublicIpCacheMinutes,
                PublicIpFailureBackoffMinutes = _configuration.PublicIpFailureBackoffMinutes,
                DangerousCommandConfirmationSeconds = _configuration.DangerousCommandConfirmationSeconds,
                DangerousCommandCooldownSeconds = _configuration.DangerousCommandCooldownSeconds,
                MicCooldownSeconds = _configuration.MicCooldownSeconds,
                AllowCrossAdminConfirmations = _configuration.AllowCrossAdminConfirmations
            };
        }
    }

    private void SaveConfiguration()
    {
        lock (_configurationSync)
        {
            string configPath = GetConfigurationPath();
            string tempPath = $"{configPath}.{Guid.NewGuid():N}.tmp";
            string json = JsonSerializer.Serialize(_configuration, ConfigurationJsonOptions);

            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, configPath, overwrite: true);
        }
    }

    private async Task<bool> SaveConfigurationOrReplyAsync(CancellationToken cancellationToken)
    {
        try
        {
            SaveConfiguration();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "Failed to save runtime configuration.");
            await SendTelegramMessageOnceAsync(
                $"⚠️ Failed to save config.json: {ex.Message}",
                cancellationToken);
            return false;
        }
    }

    private static string GetConfigurationPath()
    {
        return Path.Combine(AppContext.BaseDirectory, ConfigFileName);
    }

    private bool TrySetConfigurationValue(string key, string value, out string? result)
    {
        result = null;

        lock (_configurationSync)
        {
            switch (key.ToLowerInvariant())
            {
                case "alertsenabled":
                    if (!TryParseBoolean(value, out bool alertsEnabled))
                    {
                        result = "AlertsEnabled must be true or false.";
                        return false;
                    }

                    _configuration.AlertsEnabled = alertsEnabled;
                    result = $"AlertsEnabled set to {alertsEnabled}.";
                    return true;
                case "allowcrossadminconfirmations":
                    if (!TryParseBoolean(value, out bool allowCrossAdminConfirmations))
                    {
                        result = "AllowCrossAdminConfirmations must be true or false.";
                        return false;
                    }

                    _configuration.AllowCrossAdminConfirmations = allowCrossAdminConfirmations;
                    result = $"AllowCrossAdminConfirmations set to {allowCrossAdminConfirmations}.";
                    return true;
                case "batterylowpercent":
                    return TrySetIntConfigurationValue(
                        value,
                        1,
                        100,
                        number => _configuration.BatteryLowPercent = number,
                        "BatteryLowPercent",
                        out result);
                case "disklowpercent":
                    return TrySetIntConfigurationValue(
                        value,
                        1,
                        100,
                        number => _configuration.DiskLowPercent = number,
                        "DiskLowPercent",
                        out result);
                case "healthcheckintervalminutes":
                    return TrySetIntConfigurationValue(
                        value,
                        1,
                        1440,
                        number => _configuration.HealthCheckIntervalMinutes = number,
                        "HealthCheckIntervalMinutes",
                        out result);
                case "publicipcacheminutes":
                    return TrySetIntConfigurationValue(
                        value,
                        1,
                        1440,
                        number => _configuration.PublicIpCacheMinutes = number,
                        "PublicIpCacheMinutes",
                        out result);
                case "publicipfailurebackoffminutes":
                    return TrySetIntConfigurationValue(
                        value,
                        1,
                        1440,
                        number => _configuration.PublicIpFailureBackoffMinutes = number,
                        "PublicIpFailureBackoffMinutes",
                        out result);
                case "dangerouscommandconfirmationseconds":
                    return TrySetIntConfigurationValue(
                        value,
                        10,
                        300,
                        number => _configuration.DangerousCommandConfirmationSeconds = number,
                        "DangerousCommandConfirmationSeconds",
                        out result);
                case "dangerouscommandcooldownseconds":
                    return TrySetIntConfigurationValue(
                        value,
                        0,
                        3600,
                        number => _configuration.DangerousCommandCooldownSeconds = number,
                        "DangerousCommandCooldownSeconds",
                        out result);
                case "miccooldownseconds":
                    return TrySetIntConfigurationValue(
                        value,
                        0,
                        3600,
                        number => _configuration.MicCooldownSeconds = number,
                        "MicCooldownSeconds",
                        out result);
                default:
                    result = $"Unknown configuration key: {key}.";
                    return false;
            }
        }
    }

    private static bool TrySetIntConfigurationValue(
        string value,
        int min,
        int max,
        Action<int> assign,
        string name,
        out string? result)
    {
        if (!int.TryParse(value, out int number) || number < min || number > max)
        {
            result = $"{name} must be a number between {min} and {max}.";
            return false;
        }

        assign(number);
        result = $"{name} set to {number}.";
        return true;
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
        {
            return true;
        }

        if (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("y", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("n", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    private TimeSpan GetDangerousCommandConfirmationTimeout()
    {
        return TimeSpan.FromSeconds(Math.Clamp(_configuration.DangerousCommandConfirmationSeconds, 10, 300));
    }

    private TimeSpan GetDangerousCommandCooldown()
    {
        return TimeSpan.FromSeconds(Math.Clamp(_configuration.DangerousCommandCooldownSeconds, 0, 3600));
    }

    private TimeSpan GetMicCooldown()
    {
        return TimeSpan.FromSeconds(Math.Clamp(_configuration.MicCooldownSeconds, 0, 3600));
    }

    private TimeSpan GetSmartAlertInterval()
    {
        return TimeSpan.FromMinutes(Math.Clamp(_configuration.HealthCheckIntervalMinutes, 1, 1440));
    }

    private TimeSpan GetPublicIpCacheDuration()
    {
        return TimeSpan.FromMinutes(Math.Clamp(_configuration.PublicIpCacheMinutes, 1, 1440));
    }

    private TimeSpan GetPublicIpFailureBackoff()
    {
        return TimeSpan.FromMinutes(Math.Clamp(_configuration.PublicIpFailureBackoffMinutes, 1, 1440));
    }

    private bool GetAllowCrossAdminConfirmations()
    {
        return _configuration.AllowCrossAdminConfirmations;
    }

    private bool TryGetCooldownRemaining(
        string key,
        TimeSpan cooldown,
        out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (cooldown <= TimeSpan.Zero)
        {
            return false;
        }

        if (!_cooldowns.TryGetValue(key, out DateTimeOffset lastRunAt))
        {
            return false;
        }

        TimeSpan elapsed = DateTimeOffset.Now - lastRunAt;
        if (elapsed >= cooldown)
        {
            _cooldowns.TryRemove(key, out _);
            return false;
        }

        remaining = cooldown - elapsed;
        return true;
    }

    private void MarkCooldown(string key)
    {
        _cooldowns[key] = DateTimeOffset.Now;
    }

    private static string FormatDurationForHumans(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
        {
            return $"{Math.Ceiling(duration.TotalMinutes):N0} minute(s)";
        }

        return $"{Math.Ceiling(duration.TotalSeconds):N0} second(s)";
    }

    private int CreateConfirmationId()
    {
        int confirmationId;
        do
        {
            confirmationId = RandomNumberGenerator.GetInt32(100000, 999999);
        }
        while (_pendingConfirmations.ContainsKey(confirmationId));

        return confirmationId;
    }

    private static bool TryParseConfirmationId(string commandText, out int confirmationId)
    {
        return int.TryParse(GetCommandPayload(commandText), out confirmationId);
    }

    private int RemoveExpiredConfirmations()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        foreach (KeyValuePair<int, PendingConfirmation> pending in _pendingConfirmations)
        {
            if (pending.Value.ExpiresAt <= now)
            {
                _pendingConfirmations.TryRemove(pending.Key, out _);
            }
        }

        return _pendingConfirmations.Count;
    }

    private void DeleteTempFile(string? tempFile)
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
            _logger.LogWarning(ex, "Failed to delete temporary file {TempFile}.", tempFile);
        }
    }

    private void DeleteDirectorySafe(string? directory)
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
            _logger.LogWarning(ex, "Failed to delete temporary directory {Directory}.", directory);
        }
    }

    private static async Task RunMicRecorderHelperAsync(
        string outputPath,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        string commandLine = string.Join(
            " ",
            "/recordmic-helper",
            $"/seconds {durationSeconds}",
            $"/out \"{outputPath}\"");

        uint exitCode = await StartProcessInActiveUserSessionAndWaitAsync(
            GetCurrentExecutablePath(),
            commandLine,
            TimeSpan.FromSeconds(durationSeconds + 15),
            cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Recorder helper exited with code {exitCode}.");
        }

        FileInfo outputFile = new(outputPath);
        if (!outputFile.Exists || outputFile.Length == 0)
        {
            throw new InvalidOperationException("Recorder helper did not produce an audio file.");
        }
    }

    private static string GetSharedTempDirectory(string purpose)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            "WinSystemHelper",
            purpose);

        Directory.CreateDirectory(directory);
        return directory;
    }

    private CancellationTokenSource? CancelMicLoop()
    {
        lock (_micLoopSync)
        {
            CancellationTokenSource? loopCts = _micLoopCts;
            _micLoopCts = null;
            _micLoopStartedAt = null;
            _micLoopLongAlertSent = false;
            loopCts?.Cancel();
            return loopCts;
        }
    }

    private async Task StopServiceAsync(CancellationToken cancellationToken)
    {
        await SendTelegramMessageOnceAsync("💤 Service is entering sleep mode...", cancellationToken);
        _hostLifetime.StopApplication();
    }

    private async Task UninstallServiceRemotelyAsync(CancellationToken cancellationToken)
    {
        await SendTelegramMessageOnceAsync("💣 Self-destruct initiated. Service deleted.", cancellationToken);
        StartDetachedProcess("cmd.exe", "/c sc stop WinSystemHelper && sc delete WinSystemHelper");
        _hostLifetime.StopApplication();
    }

    private async Task<bool> WaitForInternetConnectivityAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                if (NetworkInterface.GetIsNetworkAvailable() && await CanReachTelegramAsync(cancellationToken))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Connectivity check failed.");
            }

            TimeSpan retryDelay = CalculateBackoff(
                attempt,
                InitialInternetRetryDelay,
                MaxInternetRetryDelay);

            _logger.LogInformation(
                "Internet connectivity unavailable. Attempt {Attempt}; retrying in {RetryDelay}.",
                attempt,
                retryDelay);

            try
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        return false;
    }

    private async Task<bool> CanReachTelegramAsync(CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout =
                CreateTimeoutTokenSource(cancellationToken, ConnectivityProbeTimeout);

            using HttpClient httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = Timeout.InfiniteTimeSpan;

            using HttpResponseMessage response = await httpClient.GetAsync(
                TelegramApiBaseUri,
                timeout.Token);

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task RunSmartAlertLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunSmartAlertsOnceAsync(cancellationToken);

                lock (_telemetrySync)
                {
                    _lastSmartAlertAt = DateTimeOffset.Now;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordError($"Smart alerts: {ex.GetType().Name}: {ex.Message}");
                _logger.LogWarning(ex, "Smart alert loop iteration failed.");
            }

            try
            {
                await Task.Delay(GetSmartAlertInterval(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunSmartAlertsOnceAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.AlertsEnabled)
        {
            return;
        }

        await CheckBatteryAlertAsync(cancellationToken);
        await CheckDiskAlertAsync(cancellationToken);
        await CheckPublicIpChangeAlertAsync(cancellationToken);
        await CheckMicLoopAlertAsync(cancellationToken);
        await CheckRepeatedTelegramFailureAlertAsync(cancellationToken);
    }

    private async Task CheckBatteryAlertAsync(CancellationToken cancellationToken)
    {
        if (!TryGetBatteryMetric(out int percent, out string status))
        {
            return;
        }

        int threshold = Math.Clamp(_configuration.BatteryLowPercent, 1, 100);
        bool low = percent <= threshold && status.Equals("Discharging", StringComparison.OrdinalIgnoreCase);
        bool shouldSend;

        lock (_alertStateSync)
        {
            shouldSend = low && !_batteryLowAlertActive;
            if (low)
            {
                _batteryLowAlertActive = true;
            }
            else if (percent >= Math.Min(100, threshold + 5) || !status.Equals("Discharging", StringComparison.OrdinalIgnoreCase))
            {
                _batteryLowAlertActive = false;
            }
        }

        if (shouldSend)
        {
            await SendTelegramBroadcastOnceAsync(
                $"🔋 Battery low: {percent}% | Status: {status}",
                cancellationToken);
        }
    }

    private async Task CheckDiskAlertAsync(CancellationToken cancellationToken)
    {
        int threshold = Math.Clamp(_configuration.DiskLowPercent, 1, 100);
        DriveInfo? lowestDrive = null;
        double lowestFreePercent = 100;

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed || drive.TotalSize <= 0)
                {
                    continue;
                }

                double freePercent = drive.AvailableFreeSpace * 100d / drive.TotalSize;
                if (freePercent < lowestFreePercent)
                {
                    lowestFreePercent = freePercent;
                    lowestDrive = drive;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Failed to inspect disk {DriveName}.", drive.Name);
            }
        }

        if (lowestDrive is null)
        {
            return;
        }

        bool low = lowestFreePercent <= threshold;
        bool shouldSend;

        lock (_alertStateSync)
        {
            shouldSend = low && !_diskLowAlertActive;
            if (low)
            {
                _diskLowAlertActive = true;
            }
            else if (lowestFreePercent >= Math.Min(100, threshold + 5))
            {
                _diskLowAlertActive = false;
            }
        }

        if (shouldSend)
        {
            await SendTelegramBroadcastOnceAsync(
                $"💽 Disk space low: {lowestDrive.Name} has {lowestFreePercent:N1}% free.",
                cancellationToken);
        }
    }

    private async Task CheckPublicIpChangeAlertAsync(CancellationToken cancellationToken)
    {
        PublicIpLookupResult result = await GetPublicIpAsync(forceRefresh: false, cancellationToken);
        if (!result.Success || string.IsNullOrWhiteSpace(result.PublicIp))
        {
            return;
        }

        string? previousIp;
        bool changed;

        lock (_alertStateSync)
        {
            previousIp = _lastPublicIpAlerted;
            changed = !string.IsNullOrWhiteSpace(previousIp) &&
                !previousIp.Equals(result.PublicIp, StringComparison.OrdinalIgnoreCase);
            _lastPublicIpAlerted = result.PublicIp;
        }

        if (changed)
        {
            await SendTelegramBroadcastOnceAsync(
                $"🌐 Public IP changed: {previousIp} -> {result.PublicIp}",
                cancellationToken);
        }
    }

    private async Task CheckMicLoopAlertAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? startedAt;
        bool alreadySent;

        lock (_micLoopSync)
        {
            startedAt = _micLoopStartedAt;
            alreadySent = _micLoopLongAlertSent;
            if (startedAt.HasValue &&
                !alreadySent &&
                DateTimeOffset.Now - startedAt.Value >= TimeSpan.FromMinutes(30))
            {
                _micLoopLongAlertSent = true;
            }
            else
            {
                return;
            }
        }

        await SendTelegramBroadcastOnceAsync(
            $"🎤 Persistent Active Alarm has been running since {startedAt.Value:yyyy-MM-dd HH:mm:ss zzz}.",
            cancellationToken);
    }

    private async Task CheckRepeatedTelegramFailureAlertAsync(CancellationToken cancellationToken)
    {
        TelemetrySnapshot telemetry = GetTelemetrySnapshot();
        if (telemetry.PollingFailureStreak < 5)
        {
            return;
        }

        bool shouldSend;
        lock (_alertStateSync)
        {
            shouldSend = !_repeatedTelegramFailureAlertActive;
            _repeatedTelegramFailureAlertActive = true;
        }

        if (shouldSend)
        {
            await SendTelegramBroadcastOnceAsync(
                $"⚠️ Telegram polling has failed {telemetry.PollingFailureStreak} time(s). Last error: {telemetry.LastError}",
                cancellationToken);
        }
    }

    private bool TryGetBatteryMetric(out int percent, out string status)
    {
        percent = 0;
        status = "Unknown";

        if (!GetSystemPowerStatus(out SystemPowerStatus powerStatus) ||
            powerStatus.BatteryLifePercent == BatteryLifePercentUnknown ||
            (powerStatus.BatteryFlag & BatteryFlagNoSystemBattery) == BatteryFlagNoSystemBattery)
        {
            return false;
        }

        percent = powerStatus.BatteryLifePercent;
        status = powerStatus.ACLineStatus switch
        {
            0 => "Discharging",
            1 => "Charging",
            _ => "Unknown"
        };

        return true;
    }

    private async Task RegisterTelegramMenuAsync(CancellationToken cancellationToken)
    {
        try
        {
            BotCommand[] commands = BuildTelegramCommands();

            foreach (long adminChatId in GetAdminChatIdsSnapshot())
            {
                try
                {
                    using CancellationTokenSource timeout =
                        CreateTimeoutTokenSource(cancellationToken, TelegramSendTimeout);

                    await _botClient.SetMyCommands(
                        commands,
                        scope: new BotCommandScopeChat { ChatId = adminChatId },
                        cancellationToken: timeout.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to register Telegram command menu for admin chat {AdminChatId}.",
                        adminChatId);
                }
            }

            _logger.LogInformation("Telegram command menu registered.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register Telegram command menu.");
        }
    }

    private static BotCommand[] BuildTelegramCommands()
    {
        return
        [
            new BotCommand { Command = "status", Description = "Show service status" },
            new BotCommand { Command = "healthcheck", Description = "Show fast healthcheck" },
            new BotCommand { Command = "version", Description = "Show installed version" },
            new BotCommand { Command = "confirm", Description = "Confirm pending command" },
            new BotCommand { Command = "cancel", Description = "Cancel pending command" },
            new BotCommand { Command = "lock", Description = "Lock the workstation" },
            new BotCommand { Command = "shutdown", Description = "Request shutdown" },
            new BotCommand { Command = "restart", Description = "Request restart" },
            new BotCommand { Command = "sleep", Description = "Request sleep" },
            new BotCommand { Command = "ip", Description = "Show public IP address" },
            new BotCommand { Command = "net", Description = "Show local network report" },
            new BotCommand { Command = "alarm", Description = "Play system alert sound" },
            new BotCommand { Command = "mic", Description = "Trigger overt alarm audio recording" },
            new BotCommand { Command = "msg", Description = "Show a screen message" },
            new BotCommand { Command = "ask", Description = "Ask a Yes/No question" },
            new BotCommand { Command = "prompt", Description = "Request text input" },
            new BotCommand { Command = "speak", Description = "Speak a message" },
            new BotCommand { Command = "screen", Description = "Capture the screen" },
            new BotCommand { Command = "tasks", Description = "Show top processes" },
            new BotCommand { Command = "kill", Description = "Terminate a process" },
            new BotCommand { Command = "startup", Description = "List startup applications" },
            new BotCommand { Command = "restartapp", Description = "Restart an application" },
            new BotCommand { Command = "services", Description = "List Windows services" },
            new BotCommand { Command = "service", Description = "Manage a Windows service" },
            new BotCommand { Command = "config", Description = "Manage runtime configuration" },
            new BotCommand { Command = "update", Description = "Apply an OTA update" },
            new BotCommand { Command = "help", Description = "Show available commands" },
            new BotCommand { Command = "stop", Description = "Stop the service" },
            new BotCommand { Command = "uninstall", Description = "Stop and delete the service" }
        ];
    }

    private static void StartDetachedProcess(string fileName, string arguments)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            }
        };

        process.Start();
    }

    private static void StartProcessInActiveUserSession(string fileName, string arguments)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == ActiveConsoleSessionUnavailable)
        {
            throw new InvalidOperationException("No active console user session is available.");
        }

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query the active user token.");
        }

        IntPtr environment = IntPtr.Zero;
        try
        {
            uint creationFlags = CreateNoWindow;
            if (CreateEnvironmentBlock(out environment, userToken, inherit: false))
            {
                creationFlags |= CreateUnicodeEnvironment;
            }

            string executablePath = Path.IsPathRooted(fileName)
                ? fileName
                : Path.Combine(Environment.SystemDirectory, fileName);

            string commandLine = $"\"{executablePath}\" {arguments}";
            StartupInfo startupInfo = new()
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = "winsta0\\default"
            };

            if (!CreateProcessAsUser(
                    userToken,
                    applicationName: null,
                    commandLine: commandLine,
                    processAttributes: IntPtr.Zero,
                    threadAttributes: IntPtr.Zero,
                    inheritHandles: false,
                    creationFlags: creationFlags,
                    environment: environment,
                    currentDirectory: Environment.SystemDirectory,
                    startupInfo: ref startupInfo,
                    processInformation: out ProcessInformation processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to start process in the active user session.");
            }

            CloseHandleIfNeeded(processInformation.hThread);
            CloseHandleIfNeeded(processInformation.hProcess);
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            CloseHandleIfNeeded(userToken);
        }
    }

    private static async Task<uint> StartProcessInActiveUserSessionAndWaitAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == ActiveConsoleSessionUnavailable)
        {
            throw new InvalidOperationException("No active console user session is available.");
        }

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query the active user token.");
        }

        IntPtr environment = IntPtr.Zero;
        ProcessInformation processInformation = default;

        try
        {
            uint creationFlags = CreateNoWindow;
            if (CreateEnvironmentBlock(out environment, userToken, inherit: false))
            {
                creationFlags |= CreateUnicodeEnvironment;
            }

            string executablePath = Path.IsPathRooted(fileName)
                ? fileName
                : Path.Combine(Environment.SystemDirectory, fileName);

            string commandLine = $"\"{executablePath}\" {arguments}";
            StartupInfo startupInfo = new()
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = "winsta0\\default"
            };

            if (!CreateProcessAsUser(
                    userToken,
                    applicationName: null,
                    commandLine: commandLine,
                    processAttributes: IntPtr.Zero,
                    threadAttributes: IntPtr.Zero,
                    inheritHandles: false,
                    creationFlags: creationFlags,
                    environment: environment,
                    currentDirectory: Path.GetDirectoryName(executablePath),
                    startupInfo: ref startupInfo,
                    processInformation: out processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to start process in the active user session.");
            }

            uint waitResult = await Task.Run(
                () => WaitForSingleObject(processInformation.hProcess, (uint)timeout.TotalMilliseconds),
                cancellationToken);

            if (waitResult == WaitTimeout)
            {
                TerminateProcessIfNeeded(processInformation.hProcess);
                throw new System.TimeoutException("Active user process timed out.");
            }

            if (waitResult != WaitObject0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed while waiting for recorder helper.");
            }

            if (!GetExitCodeProcess(processInformation.hProcess, out uint exitCode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read recorder helper exit code.");
            }

            return exitCode;
        }
        finally
        {
            CloseHandleIfNeeded(processInformation.hThread);
            CloseHandleIfNeeded(processInformation.hProcess);

            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            CloseHandleIfNeeded(userToken);
        }
    }

    private static string GetWindowsPowerShellPath()
    {
        string powerShellPath = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        if (!File.Exists(powerShellPath))
        {
            throw new InvalidOperationException($"PowerShell was not found at {powerShellPath}.");
        }

        return powerShellPath;
    }

    private static string GetCurrentExecutablePath()
    {
        return Environment.ProcessPath ??
            Process.GetCurrentProcess().MainModule?.FileName ??
            throw new InvalidOperationException("Unable to resolve executable path.");
    }

    private static void CloseHandleIfNeeded(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = CloseHandle(handle);
        }
    }

    private static void TerminateProcessIfNeeded(IntPtr process)
    {
        if (process != IntPtr.Zero)
        {
            _ = TerminateProcess(process, exitCode: 1);
        }
    }

    private void QueueBackgroundWork(
        Func<CancellationToken, Task> operation,
        string failureMessage)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await operation(_serviceStopping.Token);
                }
                catch (OperationCanceledException) when (_serviceStopping.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, failureMessage);
                }
            }, _serviceStopping.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, failureMessage);
        }
    }

    private async Task DelayAfterPollingFailureAsync(
        int attempt,
        Exception exception,
        CancellationToken cancellationToken)
    {
        RecordPollingFailure(exception);

        TimeSpan retryDelay = CalculateBackoff(
            attempt,
            InitialTelegramPollingFailureDelay,
            MaxTelegramPollingFailureDelay);

        _logger.LogWarning(
            exception,
            "Telegram polling failed on attempt {Attempt}. Retrying in {RetryDelay}.",
            attempt,
            retryDelay);

        await Task.Delay(retryDelay, cancellationToken);
    }

    private async Task SendTelegramBroadcastWithRetryAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                if (await SendTelegramBroadcastOnceAsync(text, cancellationToken))
                {
                    return;
                }

                throw new InvalidOperationException("Telegram broadcast failed for every configured admin chat.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ApiRequestException ex) when (IsFatalTelegramConfigurationException(ex))
            {
                LogCriticalAndStopForFatalTelegramConfiguration(ex);
                return;
            }
            catch (Exception ex)
            {
                TimeSpan retryDelay = CalculateBackoff(
                    attempt,
                    InitialInternetRetryDelay,
                    MaxInternetRetryDelay);

                _logger.LogWarning(
                    ex,
                    "Telegram broadcast failed on attempt {Attempt}. Retrying in {RetryDelay}.",
                    attempt,
                    retryDelay);

                await Task.Delay(retryDelay, cancellationToken);
            }
        }
    }

    private void LogCriticalAndStopForFatalTelegramConfiguration(ApiRequestException exception)
    {
        _logger.LogCritical(
            exception,
            "Fatal Telegram configuration error. ErrorCode: {ErrorCode}. Check BotToken and AdminChatIds in config.json. The service will stop.",
            exception.ErrorCode);

        _hostLifetime.StopApplication();
    }

    private static bool IsFatalTelegramConfigurationException(ApiRequestException exception)
    {
        return exception.ErrorCode is
            TelegramBadRequestErrorCode or
            TelegramUnauthorizedErrorCode or
            TelegramForbiddenErrorCode;
    }

    private async Task SendTelegramMessageOnceAsync(
        string text,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CreateTimeoutTokenSource(cancellationToken, TelegramSendTimeout);

        await _botClient.SendMessage(
            chatId: GetReplyChatId(),
            text: text,
            cancellationToken: timeout.Token);
    }

    private async Task<bool> SendTelegramBroadcastOnceAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var anyDelivered = false;

        foreach (long adminChatId in GetAdminChatIdsSnapshot())
        {
            try
            {
                using CancellationTokenSource timeout =
                    CreateTimeoutTokenSource(cancellationToken, TelegramSendTimeout);

                await _botClient.SendMessage(
                    chatId: adminChatId,
                    text: text,
                    cancellationToken: timeout.Token);

                anyDelivered = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Telegram broadcast delivery failed for admin chat {AdminChatId}.",
                    adminChatId);
            }
        }

        return anyDelivered;
    }

    private long GetReplyChatId()
    {
        return _replyChatId.Value ?? GetAdminChatIdsSnapshot().First();
    }

    private static CancellationTokenSource CreateTimeoutTokenSource(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        CancellationTokenSource timeoutTokenSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeoutTokenSource.CancelAfter(timeout);
        return timeoutTokenSource;
    }

    private static TimeSpan CalculateBackoff(
        int attempt,
        TimeSpan initialDelay,
        TimeSpan maxDelay)
    {
        if (attempt <= 1)
        {
            return initialDelay;
        }

        int exponent = Math.Min(attempt - 1, 16);
        double delayMilliseconds = initialDelay.TotalMilliseconds * Math.Pow(2, exponent);

        return TimeSpan.FromMilliseconds(Math.Min(delayMilliseconds, maxDelay.TotalMilliseconds));
    }

    private readonly record struct MicCommand(int DurationSeconds, bool Loop, bool Stop);

    private readonly record struct StartupEntry(string Name, string Command, string Source);

    private readonly record struct AppLaunchInfo(string FilePath, string Arguments);

    private readonly record struct TelemetrySnapshot(
        DateTimeOffset? LastPollingSuccessAt,
        DateTimeOffset? LastPollingFailureAt,
        DateTimeOffset? LastWakeAlertAt,
        DateTimeOffset? LastStartupAlertAt,
        DateTimeOffset? LastSmartAlertAt,
        string? LastError,
        int PollingFailureStreak);

    private readonly record struct PublicIpSnapshot(
        string? PublicIp,
        DateTimeOffset? FetchedAt,
        DateTimeOffset? NextAttemptAt,
        int FailureCount);

    private readonly record struct PublicIpLookupResult(
        bool Success,
        string? PublicIp,
        bool FromCache,
        string? ErrorMessage,
        string? StalePublicIp)
    {
        public static PublicIpLookupResult Cached(string publicIp, DateTimeOffset fetchedAt)
        {
            return new PublicIpLookupResult(true, publicIp, FromCache: true, ErrorMessage: null, StalePublicIp: null);
        }

        public static PublicIpLookupResult Fresh(string publicIp)
        {
            return new PublicIpLookupResult(true, publicIp, FromCache: false, ErrorMessage: null, StalePublicIp: null);
        }

        public static PublicIpLookupResult Failed(string errorMessage, string? stalePublicIp)
        {
            return new PublicIpLookupResult(false, null, FromCache: false, errorMessage, stalePublicIp);
        }
    }

    private readonly record struct PendingConfirmation(
        int Id,
        string CommandKey,
        string Description,
        long RequesterChatId,
        DateTimeOffset ExpiresAt,
        Func<CancellationToken, Task> Operation);

    private readonly record struct UpdaterScriptOptions(
        string ServiceName,
        int ProcessId,
        string PayloadRoot,
        string TargetDirectory,
        string BackupDirectory,
        string UpdateRoot,
        string StatusMarkerPath,
        string LogPath);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    public override void Dispose()
    {
        UnregisterWakeEventWatcher();
        CancelMicLoop();
        _serviceStopping.Cancel();
        _serviceStopping.Dispose();
        _resumeNotificationLock.Dispose();
        _micRecordingLock.Dispose();
        _screenshotLock.Dispose();
        base.Dispose();
    }
}
