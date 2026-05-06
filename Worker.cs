using System.Diagnostics;
using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
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
    private static readonly Uri TelegramApiBaseUri = new("https://api.telegram.org");
    private static readonly Uri PublicIpUri = new("https://api.ipify.org");
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
    private readonly HashSet<long> _adminChatIds;
    private readonly AsyncLocal<long?> _replyChatId = new();
    private readonly SemaphoreSlim _resumeNotificationLock = new(1, 1);
    private readonly SemaphoreSlim _micRecordingLock = new(1, 1);
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly object _micLoopSync = new();
    private readonly object _wakeEventWatcherSync = new();
    private readonly CancellationTokenSource _serviceStopping = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private CancellationTokenSource? _micLoopCts;
    private EventLogWatcher? _wakeEventWatcher;
    private long _lastWakeEventRecordId;

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
        _adminChatIds = configuration.GetEffectiveAdminChatIds()
            .Where(chatId => chatId != 0)
            .ToHashSet();
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
        if (string.IsNullOrWhiteSpace(_configuration.BotToken) || _adminChatIds.Count == 0)
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

            foreach (long adminChatId in _adminChatIds)
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
                _adminChatIds.Count);

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
        if (!_adminChatIds.Contains(chatId))
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
                case "/lock":
                    await LockWorkstationAsync(cancellationToken);
                    break;
                case "/shutdown":
                    await ShutdownAsync(cancellationToken);
                    break;
                case "/restart":
                    await RestartAsync(cancellationToken);
                    break;
                case "/sleep":
                    await SleepAsync(cancellationToken);
                    break;
                case "/ip":
                    await SendPublicIpAsync(cancellationToken);
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
                    await HandleKillCommandAsync(text, cancellationToken);
                    break;
                case "/update":
                    await HandleUpdateCommandAsync(message, text, chatId, cancellationToken);
                    break;
                case "/stop":
                    await StopServiceAsync(cancellationToken);
                    break;
                case "/uninstall":
                    await UninstallServiceRemotelyAsync(cancellationToken);
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
            "📜 Available Commands:",
            "",
            "/status - Show service, machine, network, and timestamp details.",
            "/lock - Lock the Windows workstation.",
            "/shutdown - Gracefully shut down the PC after 10 seconds.",
            "/restart - 🔄 Restart the PC after 10 seconds.",
            "/sleep - 🌙 Put the workstation to sleep.",
            "/ip - Show the current public IP address.",
            "/alarm - Play a system alert sound.",
            "/mic [seconds] - Trigger an overt alarm and record audio for up to 60 seconds.",
            "/mic [seconds] loop - Start a persistent overt active alarm loop.",
            "/mic stop - Stop the persistent active alarm loop.",
            "/msg [text] - Show a warning message on the active user's screen.",
            "/ask [text] - Ask the active user a Yes/No question.",
            "/prompt [text] - Force a text response from the active user.",
            "/speak [text] - 🗣️ Speak a message through the active session.",
            "/screen - 🖼️ Capture and return the primary screen.",
            "/tasks - 📋 Show the top 10 memory-consuming processes.",
            "/kill [ProcessName] - 🔪 Terminate matching processes.",
            "/update [https-url] - 🔄 Apply an OTA update from a ZIP file or attached document.",
            "/help - Show this command list.",
            "/stop - Stop the WinSystemHelper service.",
            "/uninstall - Stop and delete the WinSystemHelper service.");

        await SendTelegramMessageOnceAsync(helpText, cancellationToken);
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

    private async Task SendPublicIpAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CreateTimeoutTokenSource(cancellationToken, ConnectivityProbeTimeout);

        using HttpClient httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        string publicIp = await httpClient.GetStringAsync(PublicIpUri, timeout.Token);
        await SendTelegramMessageOnceAsync($"🌐 Public IP Address: {publicIp.Trim()}", cancellationToken);
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

    private Task HandleScreenshotCommandAsync(CancellationToken cancellationToken)
    {
        QueueBackgroundWork(
            CaptureAndSendScreenshotAsync,
            "Screenshot command failed.");

        return Task.CompletedTask;
    }

    private void HandleTasksCommand()
    {
        QueueBackgroundWork(
            SendTopProcessesAsync,
            "Process list command failed.");
    }

    private async Task HandleKillCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string processName = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(processName))
        {
            await SendTelegramMessageOnceAsync("⚠️ Usage: /kill [ProcessName]", cancellationToken);
            return;
        }

        QueueBackgroundWork(
            token => KillProcessesByNameAsync(processName, token),
            "Process kill command failed.");
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
        catch (TimeoutException)
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
        catch (TimeoutException)
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
        catch (TimeoutException)
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

    private async Task RegisterTelegramMenuAsync(CancellationToken cancellationToken)
    {
        try
        {
            BotCommand[] commands = BuildTelegramCommands();

            foreach (long adminChatId in _adminChatIds)
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
            new BotCommand { Command = "lock", Description = "Lock the workstation" },
            new BotCommand { Command = "shutdown", Description = "Shut down in 10 seconds" },
            new BotCommand { Command = "restart", Description = "Restart the PC" },
            new BotCommand { Command = "sleep", Description = "Put the PC to sleep" },
            new BotCommand { Command = "ip", Description = "Show public IP address" },
            new BotCommand { Command = "alarm", Description = "Play system alert sound" },
            new BotCommand { Command = "mic", Description = "Trigger overt alarm audio recording" },
            new BotCommand { Command = "msg", Description = "Show a screen message" },
            new BotCommand { Command = "ask", Description = "Ask a Yes/No question" },
            new BotCommand { Command = "prompt", Description = "Request text input" },
            new BotCommand { Command = "speak", Description = "Speak a message" },
            new BotCommand { Command = "screen", Description = "Capture the screen" },
            new BotCommand { Command = "tasks", Description = "Show top processes" },
            new BotCommand { Command = "kill", Description = "Terminate a process" },
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
                throw new TimeoutException("Active user process timed out.");
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

        foreach (long adminChatId in _adminChatIds)
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
        return _replyChatId.Value ?? _adminChatIds.First();
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
        base.Dispose();
    }
}
