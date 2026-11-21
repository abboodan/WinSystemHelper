using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

public sealed class Worker : BackgroundService
{
    private static readonly TimeSpan InitialTelegramPollingFailureDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxTelegramPollingFailureDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TelegramPollTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TelegramPollNetworkTimeout = TimeSpan.FromSeconds(45);
    private const int TelegramUpdateLimit = 20;
    private readonly ILogger<Worker> _logger;
    private readonly DevelopmentMode _developmentMode;
    private readonly ITelegramBotClient _botClient;
    private readonly TelegramMenuService _telegramMenuService;
    private readonly PendingUpdateStatus _pendingUpdateStatus;
    private readonly PowerEventService _powerEvents;
    private readonly PowerEventMonitor _powerMonitor;
    private readonly ServiceTelemetry _telemetry = new();
    private readonly TelegramConnection _connection;
    private readonly SmartAlertService _alerts;
    private readonly RuntimeConfiguration _runtime;
    private readonly MicrophoneCommandHandler _microphone;
    private readonly ImageCommandHandler _images;
    private readonly CommandBackgroundWork _backgroundWork;
    private readonly CommandQueueConsumer _commandQueue;
    private readonly QueueStore _queueStore;
    private Task[] _commandConsumers = [];
    private bool _queueWasOffline;

    private readonly CancellationTokenSource _serviceStopping = new();

    public Worker(
        ILogger<Worker> logger,
        AppConfiguration configuration,
        ITelegramBotClient botClient,
        IHttpClientFactory httpClientFactory,
        IHostApplicationLifetime hostLifetime,
        TelegramMenuService telegramMenuService,
        TelegramNotifier telegramNotifier,
        CooldownService cooldownService,
        IProcessRunner processRunner,
        ISharedPathProvider sharedPathProvider,
        ConfigurationFileService configurationFileService,
        IWindowsSessionProcessRunner windowsSessionProcessRunner,
        QueueStore queueStore,
        FileLogStore fileLog)
    {
        _queueStore = queueStore;
        _logger = logger;
        _botClient = botClient;
        _telegramMenuService = telegramMenuService;
        _runtime = new RuntimeConfiguration(configuration, configurationFileService, queueStore);
        _developmentMode = new DevelopmentMode(queueStore.Root, windowsSessionProcessRunner, _runtime.IsAdminChatId);
        var reply = new CommandReply(telegramNotifier, _runtime);
        var githubReleases = new GitHubReleaseClient(httpClientFactory, _runtime);
        _backgroundWork = new CommandBackgroundWork(logger, _serviceStopping);
        var confirmations = new ConfirmationService(reply, telegramNotifier, _runtime, cooldownService, _backgroundWork);
        var files = new ServiceFiles(logger, sharedPathProvider);
        var captureAccess = new CaptureAccess(reply, queueStore, windowsSessionProcessRunner, _developmentMode);
        var recorder = new MicrophoneRecorder(logger, reply, queueStore, windowsSessionProcessRunner, captureAccess, _runtime);
        _microphone = new MicrophoneCommandHandler(logger, reply, queueStore, windowsSessionProcessRunner, recorder, captureAccess,
            files, new PersistentMicLoopFile(queueStore, files, logger), _runtime, cooldownService, _serviceStopping);
        _images = new ImageCommandHandler(reply, queueStore, windowsSessionProcessRunner, captureAccess, _backgroundWork);

        var queue = new QueueCommandHandler(queueStore, reply, telegramNotifier, confirmations);
        var menu = new MenuCommandHandler(botClient, queueStore, telegramNotifier, reply, _runtime, queue);
        var installation = new ServiceInstallation(processRunner, files);

        _connection = new TelegramConnection(httpClientFactory, logger, reply, hostLifetime, botClient, _runtime);
        var publicIp = new PublicIpCache(httpClientFactory, _runtime, _telemetry, logger);
        _alerts = new SmartAlertService(logger, reply, _runtime, _connection, _telemetry, publicIp, _microphone);
        _powerEvents = new PowerEventService(logger, _runtime, files, queueStore);
        _powerMonitor = new PowerEventMonitor(logger, _backgroundWork, _connection, _telemetry, _powerEvents);
        var otaUpdate = new OtaUpdateService(reply, new UpdatePackage(botClient, httpClientFactory, reply),
            githubReleases, files, installation, hostLifetime, logger);
        _pendingUpdateStatus = new PendingUpdateStatus(logger, files, installation, queueStore, _runtime);
        var commands = new CommandRegistry(
        [
            new UpdateCommandHandler(reply, confirmations, otaUpdate),
            new NetworkCommandHandler(logger, reply, _backgroundWork, cooldownService, publicIp),
            new MaintenanceCommandHandler(reply, confirmations, files, installation, hostLifetime, telegramMenuService, _runtime),
            queue,
            menu,
            new ConfigurationCommandHandler(logger, reply, botClient, _runtime, _developmentMode),
            new ProcessCommandHandler(logger, reply, windowsSessionProcessRunner, _backgroundWork, confirmations,
                new StartupApplications(logger, windowsSessionProcessRunner)),
            new ServiceCommandHandler(logger, reply, _backgroundWork, confirmations),
            _microphone,
            _images,
            new InteractionCommandHandler(logger, reply, windowsSessionProcessRunner, files, _backgroundWork),
            new ModeCommandHandler(reply, _runtime, _developmentMode),
            confirmations,
            new PowerCommandHandler(logger, reply, windowsSessionProcessRunner, confirmations, _powerEvents),
            new StatusCommandHandler(logger, reply, telegramNotifier, botClient, fileLog,
                windowsSessionProcessRunner, _developmentMode, githubReleases, _telemetry, publicIp, confirmations,
                _powerMonitor, _microphone, _backgroundWork)
        ]);
        var dispatcher = new TelegramCommandDispatcher(commands, menu, _runtime, reply);
        _commandQueue = new CommandQueueConsumer(queueStore, reply, _runtime, dispatcher, logger, hostLifetime);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _powerMonitor.RegisterWakeEventWatcher();
        _powerMonitor.RegisterPowerEventWatcher();
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _developmentMode.Disable();
        _microphone.CancelMicLoop();
        _serviceStopping.Cancel();
        _powerMonitor.UnregisterWakeEventWatcher();
        _powerMonitor.UnregisterPowerEventWatcher();
        await base.StopAsync(cancellationToken);
        try
        {
            await Task.WhenAll(_commandConsumers.Append(_microphone.LoopTask)).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource workerStopping = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            _serviceStopping.Token);

        _logger.LogInformation("WinSystemHelper started.");

        if (!await _connection.ValidateConfigurationOrStopAsync(workerStopping.Token))
        {
            return;
        }

        _backgroundWork.QueueBackgroundWork(_pendingUpdateStatus.SendPendingUpdateStatusAsync, "Update status report failed.");
        _backgroundWork.QueueBackgroundWork(_powerEvents.SendPendingPowerEventReportsAsync, "Pending power event report failed.");
        _backgroundWork.QueueBackgroundWork(_alerts.SendStartupAlertAsync, "Startup alert failed.");
        _backgroundWork.QueueBackgroundWork(_microphone.RestorePersistentMicLoopAsync, "Persistent mic loop restore failed.");
        _backgroundWork.QueueBackgroundWork(_alerts.RunSmartAlertLoopAsync, "Smart alert loop failed.");
        await _telegramMenuService.RegisterAsync(
            _runtime.GetAdminChatIdsSnapshot(),
            CommandReply.SendTimeout,
            workerStopping.Token);

        var offset = await _queueStore.OffsetAsync();
        _commandConsumers = [_commandQueue.RunCommandQueueAsync(false, workerStopping.Token), _commandQueue.RunCommandQueueAsync(true, workerStopping.Token)];
        await _queueStore.ReportUnknownAsync();
        await PollAsync(offset, workerStopping.Token);
    }

    private async Task PollAsync(int offset, CancellationToken cancellationToken)
    {
        var pollingFailureAttempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using CancellationTokenSource pollingTimeout =
                    CreateTimeoutTokenSource(cancellationToken, TelegramPollNetworkTimeout);

                Update[] updates = await _botClient.GetUpdates(
                    offset: offset,
                    limit: TelegramUpdateLimit,
                    timeout: (int)TelegramPollTimeout.TotalSeconds,
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery],
                    cancellationToken: pollingTimeout.Token);

                pollingFailureAttempt = 0;
                _telemetry.RecordPollingSuccess();
                _alerts.ResetPollingFailureAlert();

                if (_telemetry.PollingFailureStreak == 0 && _queueWasOffline)
                {
                    _queueWasOffline = false;
                    await _queueStore.ConnectedAsync();
                }
                foreach (Update update in updates)
                {
                    await AcceptUpdateAsync(update, cancellationToken);
                    offset = update.Id + 1;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ApiRequestException ex) when (TelegramConnection.IsFatalTelegramConfigurationException(ex))
            {
                _connection.LogCriticalAndStopForFatalTelegramConfiguration(ex);
                break;
            }
            catch (Exception ex)
            {
                pollingFailureAttempt++;
                await DelayAfterPollingFailureAsync(pollingFailureAttempt, ex, cancellationToken);
            }
        }
    }

    private Task AcceptUpdateAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is { } callback)
        {
            return AcceptMenuCallbackAsync(update, callback, cancellationToken);
        }

        Message? message = update.Message;
        string? text = (message?.Text ?? message?.Caption)?.Trim();
        bool allowed = message is not null && _runtime.IsAuthorizedMessage(message) && !string.IsNullOrWhiteSpace(text);
        return _queueStore.AcceptAsync(
            update.Id,
            message?.Chat.Id ?? 0,
            allowed ? JsonSerializer.Serialize(update) : null,
            allowed && CommandPolicy.IsReadOnly(text!),
            allowed && CommandPolicy.IsControl(text!),
            message is null ? QueueStore.Now : new DateTimeOffset(DateTime.SpecifyKind(message.Date, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
    }

    private async Task AcceptMenuCallbackAsync(Update update, CallbackQuery callback, CancellationToken token)
    {
        bool authorized = TelegramCommandMenu.Resolve(callback, _botClient.BotId, _runtime.GetAdminChatIdsSnapshot()).HasValue;
        await _queueStore.AcceptAsync(update.Id, authorized ? callback.Message!.Chat.Id : 0,
            authorized ? JsonSerializer.Serialize(update) : null, readOnly: true, priority: false, QueueStore.Now);
        if (!authorized)
        {
            return;
        }

        // Acknowledge promptly even when another command is still running.
        using var timeout = CreateTimeoutTokenSource(token, TimeSpan.FromSeconds(2));
        try
        {
            await _botClient.AnswerCallbackQuery(callback.Id, cancellationToken: timeout.Token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && (ex is RequestException or HttpRequestException or OperationCanceledException))
        {
            _logger.LogDebug("Could not acknowledge a Telegram menu click ({ErrorType}).", ex.GetType().Name);
        }
    }

    private async Task DelayAfterPollingFailureAsync(
        int attempt,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _queueWasOffline = true;
        _telemetry.RecordPollingFailure(exception);

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

    public override void Dispose()
    {
        _developmentMode.Disable();
        _powerMonitor.UnregisterWakeEventWatcher();
        _powerMonitor.UnregisterPowerEventWatcher();
        _microphone.CancelMicLoop();
        _serviceStopping.Cancel();
        _serviceStopping.Dispose();
        _powerMonitor.Dispose();
        _microphone.Dispose();
        _images.Dispose();
        base.Dispose();
    }
}
