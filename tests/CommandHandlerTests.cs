using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class CommandHandlerTests : TestWorkspace
{
    private const long Admin = 11;
    private readonly AppConfiguration _config = new() { BotToken = "1:synthetic", AdminChatIds = [Admin] };

    [Theory]
    [InlineData("/stop", "Stop the WinSystemHelper service", "💤 WinSystemHelper service is stopping...")]
    [InlineData("/uninstall", "Stop and delete the WinSystemHelper service", "💣 Stopping and removing the WinSystemHelper service...")]
    public async Task MaintenanceReplyReportsWorkInProgress(string command, string description, string expectedReply)
    {
        using QueueStore store = Store("stop-reply");
        using IHost host = Host.CreateApplicationBuilder().Build();
        using var stopping = new CancellationTokenSource();
        var runtime = new RuntimeConfiguration(_config, new ConfigurationFileService(TestRoot), store);
        var notifier = new TelegramNotifier(store);
        var reply = new CommandReply(notifier, runtime);
        var confirmations = CreateConfirmation(reply, notifier, runtime, stopping);
        var files = new ServiceFiles(NullLogger<Worker>.Instance, new SharedPathProvider(TestRoot));
        var installation = new ServiceInstallation(new FakeProcessRunner(), files);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var menu = new TelegramMenuService(new TelegramBotClient(_config.BotToken), NullLogger<TelegramMenuService>.Instance);
        var launches = new List<string>();
        var handler = new MaintenanceCommandHandler(reply, confirmations, files, installation, lifetime, menu, runtime,
            (file, arguments) => launches.Add($"{file} {arguments}"));

        await handler.HandleAsync(Request(command), CancellationToken.None);
        string id = Regex.Match((await store.ClaimAsync(false))!.Text, @"/confirm (\d+)").Groups[1].Value;
        Assert.NotEmpty(id);
        var execution = new CommandExecution(2, false);
        CommandExecution.Current.Value = execution;
        try
        {
            await confirmations.HandleAsync(Request("/confirm", $"/confirm {id}"), CancellationToken.None);
            await Assert.Single(execution.BackgroundTasks);
        }
        finally
        {
            CommandExecution.Current.Value = null;
        }
        string?[] replies = [(await store.ClaimAsync(false))?.Text, (await store.ClaimAsync(false))?.Text];
        Assert.Contains($"✅ Confirmed: {description}.", replies);
        Assert.Contains(expectedReply, replies);
        Assert.Equal(command == "/uninstall" ? ["cmd.exe /c sc stop WinSystemHelper && sc delete WinSystemHelper"] : [], launches);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task HelpQueuesCommandListAndLegacyKeyboard()
    {
        using QueueStore store = Store("help-reply");
        using var stopping = new CancellationTokenSource();
        var runtime = new RuntimeConfiguration(_config, new ConfigurationFileService(TestRoot), store);
        var notifier = new TelegramNotifier(store);
        var reply = new CommandReply(notifier, runtime);
        var queue = new QueueCommandHandler(store, reply, notifier, CreateConfirmation(reply, notifier, runtime, stopping));
        var handler = new MenuCommandHandler(new TelegramBotClient(_config.BotToken), store, notifier, reply, runtime, queue);

        await handler.HandleAsync(Request("/help"), CancellationToken.None);

        OutboxItem[] replies =
        [
            Assert.IsType<OutboxItem>(await store.ClaimAsync(false)),
            Assert.IsType<OutboxItem>(await store.ClaimAsync(false))
        ];
        OutboxItem commandList = Assert.Single(replies, item => item.Text != "Command menu");
        Assert.StartsWith("📜 WinSystemHelper Commands", commandList.Text);
        OutboxItem menu = Assert.Single(replies, item => item.Text == "Command menu");
        Assert.IsType<ReplyKeyboardMarkup>(TelegramReplyStyle.Read(menu.Markup).Keyboard);
    }

    [Fact]
    public async Task QueueHandlerReturnsHtmlWithAKeyboard()
    {
        using QueueStore store = Store("queue-handler");
        using var stopping = new CancellationTokenSource();
        var runtime = new RuntimeConfiguration(_config, new ConfigurationFileService(TestRoot), store);
        var notifier = new TelegramNotifier(store);
        var reply = new CommandReply(notifier, runtime);
        var confirmation = CreateConfirmation(reply, notifier, runtime, stopping);
        var handler = new QueueCommandHandler(store, reply, notifier, confirmation);

        await handler.HandleAsync(Request("/queue"), CancellationToken.None);

        OutboxItem item = Assert.IsType<OutboxItem>(await store.ClaimAsync(false));
        Assert.Equal(Admin, item.ChatId);
        Assert.Contains("<b>Delivery queue</b>", item.Text);
        var (parseMode, keyboard) = TelegramReplyStyle.Read(item.Markup);
        Assert.Equal(ParseMode.Html, parseMode);
        Assert.NotNull(keyboard);
    }

    [Fact]
    public async Task ConfigurationHandlerPersistsACaseInsensitiveSetting()
    {
        using QueueStore store = Store("configuration-handler");
        var files = new ConfigurationFileService(TestRoot);
        var runtime = new RuntimeConfiguration(_config, files, store);
        var reply = new CommandReply(new TelegramNotifier(store), runtime);
        var mode = new DevelopmentMode(store.Root, new FakeSessionRunner(), runtime.IsAdminChatId);
        var handler = new ConfigurationCommandHandler(NullLogger<Worker>.Instance, reply, new TelegramBotClient(_config.BotToken), runtime, mode);

        await handler.HandleAsync(Request("/config", "/config set oUtBoXmAxSiZeMb 2048"), CancellationToken.None);

        Assert.Equal("✅ OutboxMaxSizeMB set to 2048.", (await store.ClaimAsync(false))?.Text);
        Assert.Equal(2048, files.Load().OutboxMaxSizeMB);
    }

    [Fact]
    public async Task ConfigurationHandlerRejectsAnOutOfRangeValue()
    {
        using QueueStore store = Store("invalid-configuration-handler");
        var files = new ConfigurationFileService(TestRoot);
        var runtime = new RuntimeConfiguration(_config, files, store);
        var reply = new CommandReply(new TelegramNotifier(store), runtime);
        var mode = new DevelopmentMode(store.Root, new FakeSessionRunner(), runtime.IsAdminChatId);
        var handler = new ConfigurationCommandHandler(NullLogger<Worker>.Instance, reply, new TelegramBotClient(_config.BotToken), runtime, mode);

        await handler.HandleAsync(Request("/config", "/config set PowerCommandPreDelaySeconds 31"), CancellationToken.None);

        Assert.Equal("⚠️ PowerCommandPreDelaySeconds must be a number between 0 and 30.", (await store.ClaimAsync(false))?.Text);
        Assert.Equal(3, _config.PowerCommandPreDelaySeconds);
        Assert.False(File.Exists(files.ConfigPath));
    }

    [Theory]
    [InlineData("/config set OutboxMaxSizeMB 2048")]
    [InlineData("/config admin add 33")]
    [InlineData("/config admin remove 22")]
    [InlineData("/config alerts off")]
    public async Task FailedConfigurationSaveLeavesLiveSettingsAndAdminsUnchanged(string command)
    {
        _config.AdminChatIds = [Admin, 22];
        _config.AdminChatId = Admin;
        using QueueStore store = Store("failed-config-write", () => _config.OutboxMaxSizeMB);
        var files = new ConfigurationFileService(TestRoot);
        files.Save(_config);
        string original = File.ReadAllText(files.ConfigPath);
        var runtime = new RuntimeConfiguration(_config, files, store);
        var reply = new CommandReply(new TelegramNotifier(store), runtime);
        var mode = new DevelopmentMode(store.Root, new FakeSessionRunner(), runtime.IsAdminChatId);
        using var http = new HttpClient(new FakeTelegramHttpHandler());
        var handler = new ConfigurationCommandHandler(NullLogger<Worker>.Instance, reply,
            new TelegramBotClient(_config.BotToken, http), runtime, mode);

        // Windows denies replacement of a file held open without delete sharing.
        using (File.Open(files.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await handler.HandleAsync(Request("/config", command), CancellationToken.None);
        }

        Assert.StartsWith("⚠️ Failed to save config.json:", (await store.ClaimAsync(false))?.Text);
        Assert.Equal(512, _config.OutboxMaxSizeMB);
        Assert.Equal(512L * 1024 * 1024, store.Limit);
        Assert.True(_config.AlertsEnabled);
        Assert.Equal([Admin, 22], _config.AdminChatIds);
        Assert.Equal(Admin, _config.AdminChatId);
        Assert.Equal([Admin, 22], runtime.GetAdminChatIdsSnapshot());
        Assert.Equal(original, File.ReadAllText(files.ConfigPath));
    }

    [Theory]
    [InlineData("/confirm", "⚠️ Usage: /confirm [Id]")]
    [InlineData("/cancel", "⚠️ Usage: /cancel [Id]")]
    public async Task ConfirmationHandlerKeepsItsUsageReplies(string command, string expected)
    {
        using QueueStore store = Store("confirmation-handler");
        using var stopping = new CancellationTokenSource();
        var runtime = new RuntimeConfiguration(_config, new ConfigurationFileService(TestRoot), store);
        var notifier = new TelegramNotifier(store);
        var reply = new CommandReply(notifier, runtime);
        var handler = CreateConfirmation(reply, notifier, runtime, stopping);

        await handler.HandleAsync(Request(command), CancellationToken.None);

        Assert.Equal(expected, (await store.ClaimAsync(false))?.Text);
    }

    [Fact]
    public void RegistryRoutesCameraAliasesToTheSameHandler()
    {
        using QueueStore store = Store("registry");
        using var stopping = new CancellationTokenSource();
        var runtime = new RuntimeConfiguration(_config, new ConfigurationFileService(TestRoot), store);
        var reply = new CommandReply(new TelegramNotifier(store), runtime);
        var sessions = new FakeSessionRunner();
        var mode = new DevelopmentMode(store.Root, sessions, runtime.IsAdminChatId);
        var captureAccess = new CaptureAccess(reply, store, sessions, mode);
        using var images = new ImageCommandHandler(reply, store, sessions, captureAccess, new CommandBackgroundWork(NullLogger<Worker>.Instance, stopping));
        var registry = new CommandRegistry([images]);

        Assert.True(registry.TryGet("/cam", out ICommandHandler? shortName));
        Assert.True(registry.TryGet("/camera", out ICommandHandler? longName));
        Assert.Same(images, shortName);
        Assert.Same(shortName, longName);
        Assert.False(registry.TryGet("/missing", out _));
    }

    [Theory]
    [InlineData("/screen", "🖼️ Screenshot capture started. I will send the image when it is ready.")]
    [InlineData("/cam", "📷 Camera capture requested. Notifying the local user first.")]
    public async Task ImageAcknowledgementFailureReleasesTheCaptureLock(string command, string acknowledgement)
    {
        using QueueStore store = Store("image-acknowledgement");
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        using var handler = CreateImageHandler(store, stopping);
        for (int index = 0; index < 1900; index++)
        {
            await store.EnqueueTextAsync(Admin, "filler", expires: QueueStore.Now - 1);
        }

        await Assert.ThrowsAsync<QueueCapacityException>(() => handler.HandleAsync(Request(command), CancellationToken.None));
        Assert.Null(await store.ClaimAsync(false));

        await handler.HandleAsync(Request(command), CancellationToken.None);

        Assert.Equal(acknowledgement, (await store.ClaimAsync(false))?.Text);
    }

    [Theory]
    [InlineData("/screen", "🖼️ Screenshot capture started. I will send the image when it is ready.")]
    [InlineData("/cam", "📷 Camera capture requested. Notifying the local user first.")]
    public async Task ImageWorkCanceledBeforeCaptureReleasesTheLock(string command, string acknowledgement)
    {
        using QueueStore store = Store("canceled-image-work");
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        using var handler = CreateImageHandler(store, stopping);
        var execution = new CommandExecution(1, false);
        CommandExecution.Current.Value = execution;
        try
        {
            await handler.HandleAsync(Request(command), CancellationToken.None);
            Assert.True(execution.BackgroundTasks.TryDequeue(out Task? work));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work!);
            Assert.Equal(acknowledgement, (await store.ClaimAsync(false))?.Text);

            await handler.HandleAsync(Request(command), CancellationToken.None);
            Assert.Equal(acknowledgement, (await store.ClaimAsync(false))?.Text);
        }
        finally
        {
            while (execution.BackgroundTasks.TryDequeue(out Task? work))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            }
            CommandExecution.Current.Value = null;
        }
    }

    private ImageCommandHandler CreateImageHandler(QueueStore store, CancellationTokenSource stopping)
    {
        var runtime = new RuntimeConfiguration(_config, new ConfigurationFileService(TestRoot), store);
        var reply = new CommandReply(new TelegramNotifier(store), runtime);
        var sessions = new FakeSessionRunner();
        var mode = new DevelopmentMode(store.Root, sessions, runtime.IsAdminChatId);
        return new ImageCommandHandler(reply, store, sessions, new CaptureAccess(reply, store, sessions, mode),
            new CommandBackgroundWork(NullLogger<Worker>.Instance, stopping));
    }

    [Fact]
    public async Task ModeHandlerReportsProductionBeforeLocalApproval()
    {
        using QueueStore store = Store("mode-handler");
        var runtime = new RuntimeConfiguration(_config, new ConfigurationFileService(TestRoot), store);
        var reply = new CommandReply(new TelegramNotifier(store), runtime);
        var mode = new DevelopmentMode(store.Root, new FakeSessionRunner(), runtime.IsAdminChatId);
        var handler = new ModeCommandHandler(reply, runtime, mode);

        await handler.HandleAsync(Request("/mode"), CancellationToken.None);

        Assert.Equal("Mode: production", (await store.ClaimAsync(false))?.Text);
    }

    [Fact]
    public async Task DispatcherRoutesPingAndRepliesToTheRequestingAdmin()
    {
        const long requestingAdmin = 22;
        _config.AdminChatIds = [Admin, requestingAdmin];
        using QueueStore store = Store("status-handler");
        using var stopping = new CancellationTokenSource();
        using var log = new FileLogStore(Path.Combine(TestRoot, "logs"));
        using IHost host = Host.CreateApplicationBuilder().Build();
        var logger = NullLogger<Worker>.Instance;
        var runtime = new RuntimeConfiguration(_config, new ConfigurationFileService(TestRoot), store);
        var notifier = new TelegramNotifier(store);
        var reply = new CommandReply(notifier, runtime);
        var cooldown = new CooldownService();
        var background = new CommandBackgroundWork(logger, stopping);
        var confirmations = new ConfirmationService(reply, notifier, runtime, cooldown, background);
        var bot = new TelegramBotClient(_config.BotToken);
        var http = new UnusedHttpClientFactory();
        var connection = new TelegramConnection(http, logger, reply, host.Services.GetRequiredService<IHostApplicationLifetime>(), bot, runtime);
        var telemetry = new ServiceTelemetry();
        var publicIp = new PublicIpCache(http, runtime, telemetry, logger);
        var files = new ServiceFiles(logger, new SharedPathProvider(TestRoot));
        var power = new PowerEventService(logger, runtime, files, store);
        using var monitor = new PowerEventMonitor(logger, background, connection, telemetry, power);
        var sessions = new FakeSessionRunner();
        var mode = new DevelopmentMode(store.Root, sessions, runtime.IsAdminChatId);
        var access = new CaptureAccess(reply, store, sessions, mode);
        var recorder = new MicrophoneRecorder(logger, reply, store, sessions, access, runtime);
        var loopFile = new PersistentMicLoopFile(store, files, logger);
        using var microphone = new MicrophoneCommandHandler(logger, reply, store, sessions, recorder, access,
            files, loopFile, runtime, cooldown, stopping);
        var releases = new GitHubReleaseClient(http, runtime);
        var status = new StatusCommandHandler(logger, reply, notifier, bot, log, sessions, mode, releases, telemetry,
            publicIp, confirmations, monitor, microphone, background);
        var menu = new MenuCommandHandler(bot, store, notifier, reply, runtime, new QueueCommandHandler(store, reply, notifier, confirmations));
        var dispatcher = new TelegramCommandDispatcher(new CommandRegistry([status]), menu, runtime, reply);
        var message = new Message
        {
            Chat = new Chat { Id = requestingAdmin, Type = ChatType.Private },
            From = new User { Id = requestingAdmin, FirstName = "Admin" },
            Text = "/PING"
        };

        await dispatcher.HandleUpdateAsync(new Update { Id = 2, Message = message }, CancellationToken.None);

        OutboxItem item = Assert.IsType<OutboxItem>(await store.ClaimAsync(false));
        Assert.Equal(requestingAdmin, item.ChatId);
        Assert.StartsWith($"🏓 Pong. WinSystemHelper is online.{Environment.NewLine}Machine: {Environment.MachineName}", item.Text);
        Assert.Null(reply.CurrentChatId);
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("This command makes no HTTP requests.");
    }

    private static ConfirmationService CreateConfirmation(
        CommandReply reply, TelegramNotifier notifier, RuntimeConfiguration runtime, CancellationTokenSource stopping)
    {
        return new ConfirmationService(reply, notifier, runtime, new CooldownService(),
            new CommandBackgroundWork(NullLogger<Worker>.Instance, stopping));
    }

    private static CommandRequest Request(string word, string? text = null)
    {
        var message = new Message
        {
            Chat = new Chat { Id = Admin, Type = ChatType.Private },
            From = new User { Id = Admin, FirstName = "Admin" },
            Text = text ?? word
        };
        return new CommandRequest(word, message.Text, 1, Admin, message);
    }
}
