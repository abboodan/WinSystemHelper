using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class LogUploadRegressionTests : TestWorkspace
{
    private const long Admin = 17;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task LogUploadLeavesTheOrdinaryLaneFreeAndReportsRejection()
    {
        using var transport = new HeldUpload();
        using var http = new HttpClient(transport);
        using QueueStore store = Store("log-upload");
        using var log = new FileLogStore(Path.Combine(TestRoot, "logs"));
        log.Write("INF", "Worker", "Synthetic log entry");
        using IHost host = Host.CreateApplicationBuilder().Build();
        var configuration = new AppConfiguration { BotToken = "1:synthetic", AdminChatIds = [99, Admin] };
        var bot = new TelegramBotClient(configuration.BotToken, http);
        var paths = new SharedPathProvider(TestRoot);
        using var worker = new Worker(NullLogger<Worker>.Instance, configuration, bot, new UnusedHttpClientFactory(),
            host.Services.GetRequiredService<IHostApplicationLifetime>(),
            new TelegramMenuService(bot, NullLogger<TelegramMenuService>.Instance), new TelegramNotifier(store),
            new CooldownService(), new FakeProcessRunner(), paths, new ConfigurationFileService(TestRoot),
            new FakeSessionRunner(), store, log);
        var consumer = (CommandQueueConsumer)typeof(Worker)
            .GetField("_commandQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;

        await EnqueueAsync(store, 1, "/log file");
        await EnqueueAsync(store, 2, "/ping");
        using var stopping = new CancellationTokenSource();
        Task lane = consumer.RunCommandQueueAsync(false, stopping.Token);
        try
        {
            await transport.Started.Task.WaitAsync(Deadline);
            using var replyDeadline = new CancellationTokenSource(Deadline);
            OutboxItem ping = await NextReplyAsync(store, replyDeadline.Token);
            Assert.StartsWith("🏓 Pong. WinSystemHelper is online.", ping.Text);
            Assert.False(transport.Continue.Task.IsCompleted);
            Assert.Equal(0, (await store.SnapshotAsync()).Bytes);

            transport.Continue.TrySetResult();
            using var failureDeadline = new CancellationTokenSource(Deadline);
            OutboxItem failure = await NextReplyAsync(store, failureDeadline.Token);
            Assert.Equal(Admin, failure.ChatId);
            Assert.StartsWith("⚠️ Could not send the log file:", failure.Text);
            Assert.Contains("Synthetic upload rejected", failure.Text);
            Assert.EndsWith("Use /log for the latest lines.", failure.Text);
        }
        finally
        {
            transport.Continue.TrySetResult();
            stopping.Cancel();
            await lane.WaitAsync(Deadline);
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
        return store.AcceptAsync(id, Admin, JsonSerializer.Serialize(update), readOnly: true, priority: false, QueueStore.Now);
    }

    private static async Task<OutboxItem> NextReplyAsync(QueueStore store, CancellationToken token)
    {
        OutboxItem? reply;
        while ((reply = await store.ClaimAsync(false)) is null)
        {
            await Task.Delay(20, token);
        }

        return reply;
    }

    private sealed class HeldUpload : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.EndsWith("/sendDocument", request.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            Started.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"ok":false,"error_code":400,"description":"Synthetic upload rejected"}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No other HTTP calls are expected.");
    }
}
