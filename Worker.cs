using System.Diagnostics;
using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
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
    private static readonly Uri TelegramApiBaseUri = new("https://api.telegram.org");
    private static readonly Uri PublicIpUri = new("https://api.ipify.org");
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

    private readonly ILogger<Worker> _logger;
    private readonly AppConfiguration _configuration;
    private readonly ITelegramBotClient _botClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly SemaphoreSlim _resumeNotificationLock = new(1, 1);
    private readonly SemaphoreSlim _micRecordingLock = new(1, 1);
    private readonly object _micLoopSync = new();
    private readonly object _wakeEventWatcherSync = new();
    private readonly CancellationTokenSource _serviceStopping = new();
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
        if (string.IsNullOrWhiteSpace(_configuration.BotToken) || _configuration.AdminChatId == 0)
        {
            _logger.LogCritical(
                "Fatal configuration error: BotToken or AdminChatId is missing in config.json. The service will stop.");
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

            using CancellationTokenSource chatTimeout =
                CreateTimeoutTokenSource(cancellationToken, TelegramSendTimeout);
            await _botClient.GetChat(
                chatId: _configuration.AdminChatId,
                cancellationToken: chatTimeout.Token);

            _logger.LogInformation(
                "Telegram configuration validated for bot @{BotUsername} and admin chat {AdminChatId}.",
                bot.Username,
                _configuration.AdminChatId);

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

        await SendTelegramMessageWithRetryAsync(
            $"🚀 Startup/Boot Alert: {machineName} booted up at {timestamp}. User: {userName}.",
            cancellationToken);

        _logger.LogInformation("Startup alert sent.");
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

            await SendTelegramMessageWithRetryAsync(
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
        if (message.Chat.Id != _configuration.AdminChatId)
        {
            return;
        }

        string? text = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string command = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

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
            case "/stop":
                await StopServiceAsync(cancellationToken);
                break;
            case "/uninstall":
                await UninstallServiceRemotelyAsync(cancellationToken);
                break;
        }
    }

    private async Task SendStatusAsync(CancellationToken cancellationToken)
    {
        string status = string.Join(
            Environment.NewLine,
            "📊 WinSystemHelper status:",
            $"Machine: {Environment.MachineName}",
            $"User: {Environment.UserName}",
            $"Service uptime: {_uptime.Elapsed:dd\\.hh\\:mm\\:ss}",
            $"Network available: {NetworkInterface.GetIsNetworkAvailable()}",
            $"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        await SendTelegramMessageOnceAsync(status, cancellationToken);
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
            await SendTelegramMessageOnceAsync("⏳ User ignored the prompt (Timeout).", CancellationToken.None);
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
                GetSharedInteractionTempDirectory(),
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
            await SendTelegramMessageOnceAsync("⏳ User ignored the prompt (Timeout).", CancellationToken.None);
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
            if (!string.IsNullOrWhiteSpace(tempFile) && File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary prompt file {TempFile}.", tempFile);
                }
            }
        }
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
                GetSharedAudioTempDirectory(),
                $"WinSystemHelper-active-alarm-{Guid.NewGuid():N}.wav");

            await RunMicRecorderHelperAsync(tempFile, durationSeconds, cancellationToken);

            await using FileStream voiceStream = File.OpenRead(tempFile);
            using CancellationTokenSource timeout =
                CreateTimeoutTokenSource(cancellationToken, TelegramSendTimeout);

            await _botClient.SendVoice(
                chatId: _configuration.AdminChatId,
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

            if (!string.IsNullOrWhiteSpace(tempFile) && File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary audio file {TempFile}.", tempFile);
                }
            }
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

    private static string EncodeUtf8Base64(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
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

    private static string GetSharedAudioTempDirectory()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            "WinSystemHelper",
            "Audio");

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetSharedInteractionTempDirectory()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            "WinSystemHelper",
            "Interaction");

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
            using CancellationTokenSource timeout =
                CreateTimeoutTokenSource(cancellationToken, TelegramSendTimeout);

            await _botClient.SetMyCommands(
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
                    new BotCommand { Command = "help", Description = "Show available commands" },
                    new BotCommand { Command = "stop", Description = "Stop the service" },
                    new BotCommand { Command = "uninstall", Description = "Stop and delete the service" }
                ],
                cancellationToken: timeout.Token);

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

    private async Task SendTelegramMessageWithRetryAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                await SendTelegramMessageOnceAsync(text, cancellationToken);
                return;
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
                    "Telegram message send failed on attempt {Attempt}. Retrying in {RetryDelay}.",
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
            "Fatal Telegram configuration error. ErrorCode: {ErrorCode}. Check BotToken and AdminChatId in config.json. The service will stop.",
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
            chatId: _configuration.AdminChatId,
            text: text,
            cancellationToken: timeout.Token);
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
