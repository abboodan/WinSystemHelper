using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class TrackedConsentCommandTests : TestWorkspace
{
    private const long Admin = 11;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneShotMicAllowsOtherCommandsAndStopCancelsConsentOrRecording(bool approve)
    {
        using QueueStore store = Store("tracked-mic");
        var sessions = new ConsentRunner();

        await RunLanesAsync(store, sessions, async () =>
        {
            await EnqueueAsync(store, 1, "/mic 2");
            string directory = await sessions.ConsentOpened.Task.WaitAsync(Deadline);
            Assert.Equal("🎤 Recording requested (2s); awaiting local consent.", await NextReplyAsync(store));
            Assert.False(sessions.RecorderStarted.Task.IsCompleted);
            Assert.Equal("running", ReadState(store, 1));

            await EnqueueAsync(store, 2, "/ping");
            Assert.StartsWith("🏓 Pong.", await NextReplyAsync(store));

            await EnqueueAsync(store, 3, "/mic 2");
            Assert.Equal("🎤 Active Alarm recording is already running.", await NextReplyAsync(store));
            await EnqueueAsync(store, 4, "/mic 2 loop");
            Assert.Equal("🎤 Active Alarm recording is already running.", await NextReplyAsync(store));
            Assert.Equal(1, sessions.ConsentWindows);

            if (approve)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "allow"), "yes");
                await sessions.RecorderStarted.Task.WaitAsync(Deadline);
                await EnqueueAsync(store, 5, "/ping");
                Assert.StartsWith("🏓 Pong.", await NextReplyAsync(store));
                Assert.Equal("running", ReadState(store, 1));
            }

            await EnqueueAsync(store, 6, "/mic stop");
            await sessions.ConsentCanceled.Task.WaitAsync(Deadline);
            if (approve)
            {
                await sessions.RecorderCanceled.Task.WaitAsync(Deadline);
            }
            else
            {
                Assert.False(sessions.RecorderStarted.Task.IsCompleted);
            }

            string[] replies = [await NextReplyAsync(store), await NextReplyAsync(store)];
            Assert.Contains("🛑 Recording stopped. Saved files remain queued for delivery.", replies);
            Assert.Contains(replies, text => text.Contains("The operation was cancelled before it finished.", StringComparison.Ordinal));
            Assert.Equal("unknown", ReadState(store, 1));
            Assert.Null(await store.ClaimAsync(true));
        });
    }

    [Fact]
    public async Task DevelopmentApprovalAllowsOtherCommandsAndCanStillBeGrantedOrRevoked()
    {
        using QueueStore store = Store("tracked-mode");
        var sessions = new ConsentRunner();

        await RunLanesAsync(store, sessions, async () =>
        {
            await EnqueueAsync(store, 1, "/mode development");
            Assert.Equal("Development mode active for this administrator and chat. Use /mode production to end it.",
                await NextReplyAsync(store));

            await EnqueueAsync(store, 2, "/ping");
            Assert.StartsWith("🏓 Pong.", await NextReplyAsync(store));
            await EnqueueAsync(store, 3, "/mode");
            Assert.Equal("Mode: development", await NextReplyAsync(store));

            await EnqueueAsync(store, 4, "/mode production");
            Assert.Equal("Production mode active. Development permission has ended.", await NextReplyAsync(store));

            await EnqueueAsync(store, 5, "/mode");
            Assert.Equal("Mode: production", await NextReplyAsync(store));
            using var timeout = new CancellationTokenSource(Deadline);
            while (ReadState(store, 1) != "completed")
            {
                await Task.Delay(20, timeout.Token);
            }
        });
    }

    private async Task RunLanesAsync(QueueStore store, ConsentRunner sessions, Func<Task> test)
    {
        using IHost host = Host.CreateApplicationBuilder().Build();
        using var log = new FileLogStore(Path.Combine(TestRoot, "logs"));
        var configuration = new AppConfiguration { BotToken = "1:synthetic", AdminChatIds = [99, Admin], MicCooldownSeconds = 0 };
        var bot = new TelegramBotClient(configuration.BotToken);
        using var worker = new Worker(NullLogger<Worker>.Instance, configuration, bot, new UnusedHttpClientFactory(),
            host.Services.GetRequiredService<IHostApplicationLifetime>(),
            new TelegramMenuService(bot, NullLogger<TelegramMenuService>.Instance), new TelegramNotifier(store),
            new CooldownService(), new FakeProcessRunner(), new SharedPathProvider(TestRoot),
            new ConfigurationFileService(TestRoot), sessions, store, log);
        var consumer = (CommandQueueConsumer)typeof(Worker)
            .GetField("_commandQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;

        using var stopping = new CancellationTokenSource();
        Task ordinary = consumer.RunCommandQueueAsync(false, stopping.Token);
        Task control = consumer.RunCommandQueueAsync(true, stopping.Token);
        try
        {
            await test();
        }
        finally
        {
            stopping.Cancel();
            // Closing the fake consent window also revokes any active recording or grant.
            sessions.CloseConsent.TrySetResult(0);
            await Task.WhenAll(ordinary, control).WaitAsync(Deadline);
        }
    }

    private static Task EnqueueAsync(QueueStore store, int id, string text)
    {
        var update = new Update
        {
            Id = id,
            Message = new Message
            {
                Id = id,
                Chat = new Chat { Id = Admin, Type = ChatType.Private },
                From = new User { Id = Admin, FirstName = "Admin" },
                Text = text,
                Date = DateTime.UtcNow
            }
        };
        return store.AcceptAsync(id, Admin, JsonSerializer.Serialize(update),
            CommandPolicy.IsReadOnly(text), CommandPolicy.IsControl(text), QueueStore.Now);
    }

    private static async Task<string> NextReplyAsync(QueueStore store)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        OutboxItem? reply;
        while ((reply = await store.ClaimAsync(false)) is null)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.Equal(Admin, reply.ChatId);
        return reply.Text;
    }

    private static string? ReadState(QueueStore store, int id)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(store.Root, "queue.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        db.Open();
        using var query = QueueStore.Command(db, "SELECT state FROM commands WHERE id=$id", ("$id", id));
        return (string?)query.ExecuteScalar();
    }

    private sealed class ConsentRunner : IWindowsSessionProcessRunner
    {
        private static readonly WindowsConsoleSession ConsoleSession = new(1, "S-1-5-21-1-2-3-1001");
        private int _consentWindows;

        public TaskCompletionSource<string> ConsentOpened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<uint> CloseConsent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConsentCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecorderStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecorderCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WindowsConsoleSession? RequiredSession { get; private set; }
        public int ConsentWindows => Volatile.Read(ref _consentWindows);

        public async Task<uint> StartEncodedPowerShellAndWaitAsync(
            string script, TimeSpan timeout, CancellationToken token, WindowsConsoleSession? requiredSession = null)
        {
            RequiredSession = requiredSession;
            Interlocked.Increment(ref _consentWindows);
            string encoded = Regex.Match(script, "FromBase64String\\('([^']+)'\\)").Groups[1].Value;
            ConsentOpened.TrySetResult(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            try
            {
                return await CloseConsent.Task.WaitAsync(token);
            }
            finally
            {
                ConsentCanceled.TrySetResult();
            }
        }

        public async Task<uint> StartProcessAndWaitAsync(
            string fileName, string arguments, TimeSpan timeout, CancellationToken token, WindowsConsoleSession? requiredSession = null)
        {
            Assert.Contains("/recordmic-helper", arguments);
            RecorderStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            }
            finally
            {
                RecorderCanceled.TrySetResult();
            }
        }

        public string GetActiveConsoleSessionStatus() => "synthetic";
        public string? TryGetActiveUserSid() => ConsoleSession.UserSid;
        public WindowsConsoleSession? TryGetActiveConsoleSession() => ConsoleSession;
        public void StartProcess(string fileName, string arguments) { }
        public void StartWindowsPowerShell(string arguments) { }
        public void StartEncodedPowerShell(string script) => throw new NotSupportedException();
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP calls are expected.");
    }
}
