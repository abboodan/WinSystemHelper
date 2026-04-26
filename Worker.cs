using System.Diagnostics;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
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

    private readonly ILogger<Worker> _logger;
    private readonly AppConfiguration _configuration;
    private readonly ITelegramBotClient _botClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly SemaphoreSlim _resumeNotificationLock = new(1, 1);
    private readonly SemaphoreSlim _micRecordingLock = new(1, 1);
    private readonly object _micLoopSync = new();
    private readonly CancellationTokenSource _serviceStopping = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private CancellationTokenSource? _micLoopCts;

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
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        CancelMicLoop();
        _serviceStopping.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource workerStopping = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            _serviceStopping.Token);

        _logger.LogInformation("WinSystemHelper started.");
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

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        QueueBackgroundWork(SendWakeAlertAsync, "Wake alert failed.");
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
            case "/ip":
                await SendPublicIpAsync(cancellationToken);
                break;
            case "/alarm":
                await TriggerAlarmAsync(cancellationToken);
                break;
            case "/mic":
                await HandleMicCommandAsync(text, cancellationToken);
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
            "/ip - Show the current public IP address.",
            "/alarm - Play a system alert sound.",
            "/mic [seconds] - Trigger an overt alarm and record audio for up to 60 seconds.",
            "/mic [seconds] loop - Start a persistent overt active alarm loop.",
            "/mic stop - Stop the persistent active alarm loop.",
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
                    new BotCommand { Command = "ip", Description = "Show public IP address" },
                    new BotCommand { Command = "alarm", Description = "Play system alert sound" },
                    new BotCommand { Command = "mic", Description = "Trigger overt alarm audio recording" },
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
                throw new TimeoutException("Recorder helper timed out.");
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
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        CancelMicLoop();
        _serviceStopping.Cancel();
        _serviceStopping.Dispose();
        _resumeNotificationLock.Dispose();
        _micRecordingLock.Dispose();
        base.Dispose();
    }
}
