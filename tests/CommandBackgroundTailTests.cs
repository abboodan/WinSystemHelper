using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class CommandBackgroundTailTests : TestWorkspace
{
    private const long FirstChat = 11;
    private const long SecondChat = 22;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OrdinaryLaneStartsNextHandlerWhenThePreviousHandlerReturns()
    {
        using QueueStore store = Store("next-handler");
        using var stopping = new CancellationTokenSource();
        var handlerStarted = NewSignal();
        var returnFromHandler = NewSignal();
        var tailStarted = NewSignal();
        var releaseTail = NewSignal();
        var nextHandlerStarted = NewSignal();
        var consumer = CreateConsumer(store, stopping, async (request, _, background) =>
        {
            if (request.UpdateId == 1)
            {
                background.QueueBackgroundWork(async _ =>
                {
                    tailStarted.TrySetResult();
                    await releaseTail.Task;
                }, "Test tail failed.");
                handlerStarted.TrySetResult();
                await returnFromHandler.Task;
            }
            else
            {
                nextHandlerStarted.TrySetResult();
            }
        });

        await EnqueueAsync(store, 1);
        await EnqueueAsync(store, 2);
        Task lane = consumer.RunCommandQueueAsync(false, stopping.Token);
        try
        {
            await handlerStarted.Task.WaitAsync(Deadline);
            await tailStarted.Task.WaitAsync(Deadline);
            Assert.Equal("pending", ReadState(store.Root, 2));

            returnFromHandler.SetResult();
            await nextHandlerStarted.Task.WaitAsync(Deadline);
            Assert.False(releaseTail.Task.IsCompleted);
            Assert.Equal("running", ReadState(store.Root, 1));
        }
        finally
        {
            returnFromHandler.TrySetResult();
            releaseTail.TrySetResult();
            stopping.Cancel();
            await lane.WaitAsync(Deadline);
        }

        Assert.Equal("completed", ReadState(store.Root, 1));
        Assert.Equal("completed", ReadState(store.Root, 2));
    }

    [Fact]
    public async Task RestartReportsAnInFlightSideEffectAsUnknownWithoutReplayingIt()
    {
        using QueueStore store = Store("interrupted-tail");
        using var stopping = new CancellationTokenSource();
        var tailStarted = NewSignal();
        var releaseTail = NewSignal();
        var consumer = CreateConsumer(store, stopping, (request, _, background) =>
        {
            if (request.UpdateId == 41)
            {
                background.QueueBackgroundWork(async token =>
                {
                    tailStarted.TrySetResult();
                    await releaseTail.Task;
                    token.ThrowIfCancellationRequested();
                }, "Test tail failed.");
            }

            return Task.CompletedTask;
        });

        await EnqueueAsync(store, 41);
        await EnqueueAsync(store, 42, readOnly: true);
        Task lane = consumer.RunCommandQueueAsync(false, stopping.Token);
        try
        {
            await tailStarted.Task.WaitAsync(Deadline);
            await WaitForStateAsync(store.Root, 42, "completed");
            Assert.Equal("running", ReadState(store.Root, 41));

            // Close the original SQLite owner before its tail can record an outcome.
            stopping.Cancel();
            store.Dispose();
            using var restarted = new QueueStore(store.Root, () => 512);
            await restarted.ReportUnknownAsync();

            OutboxItem notice = Assert.IsType<OutboxItem>(await restarted.ClaimAsync(false));
            Assert.Equal(FirstChat, notice.ChatId);
            Assert.Contains("Interrupted commands (1): 41. Their result is unknown", notice.Text);
            Assert.Contains("will NOT be executed again automatically", notice.Text);
            Assert.Equal("unknown", ReadState(restarted.Root, 41));
            Assert.Null(await restarted.ClaimCommandAsync(false));
            await restarted.ReportUnknownAsync();
            Assert.Null(await restarted.ClaimAsync(false));
        }
        finally
        {
            stopping.Cancel();
            releaseTail.TrySetResult();
            await lane.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task OverlappingHandlersAndTailsReplyToTheirOwnChats()
    {
        using QueueStore store = Store("reply-routing");
        using var stopping = new CancellationTokenSource();
        var releaseFirst = NewSignal();
        var releaseSecond = NewSignal();
        var firstTailStarted = NewSignal();
        var secondTailStarted = NewSignal();
        var consumer = CreateConsumer(store, stopping, async (request, reply, background) =>
        {
            bool first = request.UpdateId == 1;
            await reply.SendTelegramMessageOnceAsync($"handler {request.UpdateId}", CancellationToken.None);
            background.QueueBackgroundWork(async token =>
            {
                (first ? firstTailStarted : secondTailStarted).TrySetResult();
                await (first ? releaseFirst : releaseSecond).Task;
                await reply.SendTelegramMessageOnceAsync($"tail {request.UpdateId}", token);
            }, "Test reply failed.");
        });

        await EnqueueAsync(store, 1, FirstChat);
        await EnqueueAsync(store, 2, SecondChat);
        Task lane = consumer.RunCommandQueueAsync(false, stopping.Token);
        try
        {
            await firstTailStarted.Task.WaitAsync(Deadline);
            await secondTailStarted.Task.WaitAsync(Deadline);
            releaseSecond.SetResult();
            await WaitForStateAsync(store.Root, 2, "completed");
            Assert.Equal("running", ReadState(store.Root, 1));
            releaseFirst.SetResult();
            await WaitForStateAsync(store.Root, 1, "completed");

            var replies = new Dictionary<string, long>();
            while (await store.ClaimAsync(false) is { } item)
            {
                replies.Add(item.Text, item.ChatId);
            }

            Assert.Equal(4, replies.Count);
            Assert.Equal(FirstChat, replies["handler 1"]);
            Assert.Equal(FirstChat, replies["tail 1"]);
            Assert.Equal(SecondChat, replies["handler 2"]);
            Assert.Equal(SecondChat, replies["tail 2"]);
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            stopping.Cancel();
            await lane.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task OrdinaryTailCapLeavesControlIndependentAndStopWaitsForOutstandingTails()
    {
        const int cap = 8;
        const int controlId = 100;
        using QueueStore store = Store("tail-cap");
        using var stopping = new CancellationTokenSource();
        TaskCompletionSource[] releases = Enumerable.Range(0, cap + 1).Select(_ => NewSignal()).ToArray();
        var dispatched = new List<int>();
        var filled = NewSignal();
        var excessHandler = NewSignal();
        var controlHandler = NewSignal();
        var consumer = CreateConsumer(store, stopping, (request, _, background) =>
        {
            int id = request.UpdateId;
            if (id == controlId)
            {
                controlHandler.TrySetResult();
                return Task.CompletedTask;
            }

            dispatched.Add(id);
            background.QueueBackgroundWork(_ => releases[id - 1].Task, "Test tail failed.");
            if (id == cap)
            {
                filled.TrySetResult();
            }
            if (id == cap + 1)
            {
                excessHandler.TrySetResult();
            }

            return Task.CompletedTask;
        });

        for (int id = 1; id <= cap + 1; id++)
        {
            await EnqueueAsync(store, id);
        }

        Task ordinary = consumer.RunCommandQueueAsync(false, stopping.Token);
        Task control = consumer.RunCommandQueueAsync(true, stopping.Token);
        try
        {
            await filled.Task.WaitAsync(Deadline);
            await Assert.ThrowsAsync<TimeoutException>(() => excessHandler.Task.WaitAsync(TimeSpan.FromMilliseconds(150)));
            Assert.Equal("pending", ReadState(store.Root, cap + 1));
            await EnqueueAsync(store, controlId, priority: true);
            await controlHandler.Task.WaitAsync(Deadline);

            // A later tail frees space even while the oldest one remains held.
            releases[cap - 1].SetResult();
            await excessHandler.Task.WaitAsync(Deadline);
            Assert.Equal(Enumerable.Range(1, cap + 1), dispatched);
            Assert.Equal("running", ReadState(store.Root, 1));

            stopping.Cancel();
            await control.WaitAsync(Deadline);
            await Assert.ThrowsAsync<TimeoutException>(() => ordinary.WaitAsync(TimeSpan.FromMilliseconds(150)));
        }
        finally
        {
            stopping.Cancel();
            foreach (TaskCompletionSource release in releases)
            {
                release.TrySetResult();
            }
            await Task.WhenAll(ordinary, control).WaitAsync(Deadline);
        }

        for (int id = 1; id <= cap + 1; id++)
        {
            Assert.Equal("completed", ReadState(store.Root, id));
        }
    }

    [Theory]
    [InlineData(true, "failed")]
    [InlineData(false, "unknown")]
    public async Task AFailingCommandWaitsForItsOtherBackgroundWorkBeforeRecordingTheOutcome(bool readOnly, string outcome)
    {
        using QueueStore store = Store("tail-failure");
        using var stopping = new CancellationTokenSource();
        var releaseTail = NewSignal();
        var failureStarted = NewSignal();
        var consumer = CreateConsumer(store, stopping, (request, _, background) =>
        {
            if (request.UpdateId == 1)
            {
                background.QueueBackgroundWork(_ =>
                {
                    failureStarted.TrySetResult();
                    return Task.FromException(new IOException("Synthetic tail failure."));
                }, "Test tail failed.");
                background.QueueBackgroundWork(_ => releaseTail.Task, "Test tail failed.");
            }

            return Task.CompletedTask;
        });

        await EnqueueAsync(store, 1, readOnly: readOnly);
        await EnqueueAsync(store, 2);
        Task lane = consumer.RunCommandQueueAsync(false, stopping.Token);
        try
        {
            await failureStarted.Task.WaitAsync(Deadline);
            await WaitForStateAsync(store.Root, 2, "completed");
            Assert.Equal("running", ReadState(store.Root, 1));

            releaseTail.SetResult();
            await WaitForStateAsync(store.Root, 1, outcome);
            await stopping.CancelAsync();
            await lane.WaitAsync(Deadline);
            OutboxItem report = Assert.IsType<OutboxItem>(await store.ClaimAsync(false));
            Assert.Equal(FirstChat, report.ChatId);
            Assert.Contains("The operation ended before completion. See /log for details.", report.Text);
            Assert.Equal(!readOnly, report.Text.Contains("won't run this command again automatically", StringComparison.Ordinal));
        }
        finally
        {
            releaseTail.TrySetResult();
            stopping.Cancel();
            await lane.WaitAsync(Deadline);
        }
    }

    private CommandQueueConsumer CreateConsumer(
        QueueStore store, CancellationTokenSource stopping,
        Func<CommandRequest, CommandReply, CommandBackgroundWork, Task> handle)
    {
        var configuration = new AppConfiguration { AdminChatIds = [FirstChat, SecondChat] };
        var runtime = new RuntimeConfiguration(configuration, new ConfigurationFileService(TestRoot), store);
        var reply = new CommandReply(new TelegramNotifier(store), runtime);
        var logger = NullLogger<Worker>.Instance;
        var background = new CommandBackgroundWork(logger, stopping);
        var handler = new ProbeHandler(request => handle(request, reply, background));
        var dispatcher = new TelegramCommandDispatcher(new CommandRegistry([handler]), null!, runtime, reply);
        return new CommandQueueConsumer(store, reply, runtime, dispatcher, logger, new TestLifetime(stopping));
    }

    private static Task EnqueueAsync(
        QueueStore store, int id, long chat = FirstChat, bool readOnly = false, bool priority = false)
    {
        var update = new Update
        {
            Id = id,
            Message = new Message
            {
                Id = id,
                Chat = new Chat { Id = chat, Type = ChatType.Private },
                From = new User { Id = chat, FirstName = "Admin" },
                Text = "/probe",
                Date = DateTime.UtcNow
            }
        };
        return store.AcceptAsync(id, chat, JsonSerializer.Serialize(update), readOnly, priority, QueueStore.Now);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string ReadState(string root, int id)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "queue.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        db.Open();
        using var query = QueueStore.Command(db, "SELECT state FROM commands WHERE id=$id", ("$id", id));
        return (string)query.ExecuteScalar()!;
    }

    private static async Task WaitForStateAsync(string root, int id, string state)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (ReadState(root, id) != state)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class ProbeHandler(Func<CommandRequest, Task> handle) : ICommandHandler
    {
        public IReadOnlyList<string> Commands { get; } = ["/probe"];

        public Task HandleAsync(CommandRequest request, CancellationToken token) => handle(request);
    }

    private sealed class TestLifetime(CancellationTokenSource stopping) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => stopping.Cancel();
    }
}
