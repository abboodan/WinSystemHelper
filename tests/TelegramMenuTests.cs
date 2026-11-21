using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class TelegramMenuTests : TestWorkspace
{
    [Fact(DisplayName = "Telegram queue shows the latest failure reason before its item ID and escapes HTML")]
    public async Task TelegramQueueShowsTheLatestFailureReasonBeforeItsItemIDAndEscapesHTML()
    {
        Assert.True(await TelegramMenuAsync(nameof(TelegramQueueShowsTheLatestFailureReasonBeforeItsItemIDAndEscapesHTML)));
    }

    [Fact(DisplayName = "Telegram log stays within the message limit with special characters")]
    public async Task TelegramLogStaysWithinTheMessageLimitWithSpecialCharacters()
    {
        Assert.True(await TelegramMenuAsync(nameof(TelegramLogStaysWithinTheMessageLimitWithSpecialCharacters)));
    }

    [Fact(DisplayName = "Failed capture reply shows the actual reason and preserves the warning about retries")]
    public async Task FailedCaptureReplyShowsTheActualReasonAndPreservesTheWarningAboutRetries()
    {
        Assert.True(await TelegramMenuAsync(nameof(FailedCaptureReplyShowsTheActualReasonAndPreservesTheWarningAboutRetries)));
    }

    [Fact(DisplayName = "Telegram outbox preserves the inline keyboard and still sends legacy reply keyboards")]
    public async Task TelegramOutboxPreservesTheInlineKeyboardAndStillSendsLegacyReplyKeyboards()
    {
        Assert.True(await TelegramMenuAsync(nameof(TelegramOutboxPreservesTheInlineKeyboardAndStillSendsLegacyReplyKeyboards)));
    }

    [Fact(DisplayName = "Capture menu copies commands without starting a capture through a callback")]
    public async Task CaptureMenuCopiesCommandsWithoutStartingACaptureThroughACallback()
    {
        Assert.True(await TelegramMenuAsync(nameof(CaptureMenuCopiesCommandsWithoutStartingACaptureThroughACallback)));
    }

    [Fact(DisplayName = "Telegram callbacks reject unapproved senders and arbitrary commands while advancing the offset")]
    public async Task TelegramCallbacksRejectUnapprovedSendersAndArbitraryCommandsWhileAdvancingTheOffset()
    {
        Assert.True(await TelegramMenuAsync(nameof(TelegramCallbacksRejectUnapprovedSendersAndArbitraryCommandsWhileAdvancingTheOffset)));
    }

    [Fact(DisplayName = "Authorized Telegram click is durably accepted and acknowledged")]
    public async Task AuthorizedTelegramClickIsDurablyAcceptedAndAcknowledged()
    {
        Assert.True(await TelegramMenuAsync(nameof(AuthorizedTelegramClickIsDurablyAcceptedAndAcknowledged)));
    }

    [Fact(DisplayName = "Authorized queued Telegram callback refreshes the original message in place")]
    public async Task AuthorizedQueuedTelegramCallbackRefreshesTheOriginalMessageInPlace()
    {
        Assert.True(await TelegramMenuAsync(nameof(AuthorizedQueuedTelegramCallbackRefreshesTheOriginalMessageInPlace)));
    }

    private async Task<bool> TelegramMenuAsync(string check)
    {
        using (var store = Store("telegram-menu"))
        {
            const long chatId = -1;
            const long adminId = 2;
            long[] admins = [chatId, adminId];
            var older = await store.ReserveAsync(chatId, "photo", 256, 0, null) ?? throw new Exception("Could not reserve old test image");
            var latest = await store.ReserveAsync(chatId, "voice", 256, 1, null) ?? throw new Exception("Could not reserve test audio");
            await store.FailCaptureAsync(older.Id);
            await store.FailCaptureAsync(latest.Id, "Could not open audio device <Test> & Windows error 1.");
            string queue = TelegramReplies.Queue(await store.SnapshotAsync(chatId));
            bool observed = queue.Contains("<b>Delivery queue</b>") && queue.Contains("Could not open audio device &lt;Test&gt; &amp; Windows error 1.") &&
                queue.IndexOf("Could not open audio device", StringComparison.Ordinal) < queue.IndexOf(latest.Id, StringComparison.Ordinal) &&
                queue.IndexOf(latest.Id, StringComparison.Ordinal) < queue.IndexOf(older.Id, StringComparison.Ordinal);
            if (check == nameof(TelegramQueueShowsTheLatestFailureReasonBeforeItsItemIDAndEscapesHTML))
            {
                return observed;
            }
            string logText = TelegramReplies.Log("PC <test>", "old entry\n" + new string('&', 3600));
            observed = logText.Length < 4096 && logText.Contains("PC &lt;test&gt;") && logText.Contains("&amp;") && logText.Contains("</pre>");
            if (check == nameof(TelegramLogStaysWithinTheMessageLimitWithSpecialCharacters))
            {
                return observed;
            }
            string failedCommand = TelegramReplies.CommandFailure(17, "/mic", false, new MicRecorderFailure("Audio input <denied>."));
            observed = failedCommand.Contains("<b>Reason</b>  Audio input &lt;denied&gt;.") && failedCommand.Contains("won't run this command again");
            if (check == nameof(FailedCaptureReplyShowsTheActualReasonAndPreservesTheWarningAboutRetries))
            {
                return observed;
            }

            using var handler = new FakeTelegramHttpHandler();
            using var http = new HttpClient(handler);
            var bot = new TelegramBotClient("12345678:" + new string('Q', 35), http);
            var menuService = new TelegramMenuService(bot, NullLogger<TelegramMenuService>.Instance);
            var config = new AppConfiguration { AdminChatIds = admins };
            using var log = new FileLogStore(Path.Combine(TestRoot, "telegram-menu-log"));
            using IHost host = Host.CreateDefaultBuilder().ConfigureServices(services => services.AddHttpClient()).Build();
            var notifier = new TelegramNotifier(store);
            using var worker = new Worker(NullLogger<Worker>.Instance, config, bot, host.Services.GetRequiredService<IHttpClientFactory>(),
                host.Services.GetRequiredService<IHostApplicationLifetime>(), menuService, notifier, new CooldownService(),
                new FakeProcessRunner(), new SharedPathProvider(store.Root), new ConfigurationFileService(store.Root),
                new FakeSessionRunner(), store, log);

            var home = TelegramCommandMenu.Build(TelegramMenuAction.Home)!;
            await notifier.SendHtmlAsync(chatId, home.Text, home.Keyboard, CancellationToken.None);
            var queuedMenu = await store.ClaimAsync(false) ?? throw new Exception("Menu was not queued");
            var outbox = new TelegramOutboxTransport(bot);
            await outbox.SendAsync(queuedMenu, CancellationToken.None);
            await store.CompleteAsync(queuedMenu.Id);
            string legacyMarkup = JsonSerializer.Serialize(new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "/help" } }));
            await store.EnqueueTextAsync(chatId, "Command menu", legacyMarkup);
            var legacy = await store.ClaimAsync(false) ?? throw new Exception("Legacy reply keyboard was not queued");
            await outbox.SendAsync(legacy, CancellationToken.None);
            await store.CompleteAsync(legacy.Id);
            var sentMessages = handler.Requests.ToArray();
            using var inline = JsonDocument.Parse(sentMessages[0].Body);
            using var reply = JsonDocument.Parse(sentMessages[1].Body);
            observed = string.Equals(inline.RootElement.GetProperty("parse_mode").GetString(), "HTML", StringComparison.OrdinalIgnoreCase) &&
                inline.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard").GetArrayLength() > 0 &&
                reply.RootElement.GetProperty("reply_markup").GetProperty("keyboard").GetArrayLength() == 1;
            if (check == nameof(TelegramOutboxPreservesTheInlineKeyboardAndStillSendsLegacyReplyKeyboards))
            {
                return observed;
            }

            var captureMenu = TelegramCommandMenu.Build(TelegramMenuAction.Capture)!;
            var captureButtons = captureMenu.Keyboard.InlineKeyboard.SelectMany(row => row).ToArray();
            observed = captureButtons.Any(button => button.CopyText?.Text == "/cam") &&
                captureButtons.Where(button => button.CallbackData is not null).All(button => button.CallbackData == "wsh:menu:Home");
            if (check == nameof(CaptureMenuCopiesCommandsWithoutStartingACaptureThroughACallback))
            {
                return observed;
            }

            var menuMessage = new Message { Id = 57, Chat = new Chat { Id = chatId, Type = ChatType.Group },
                Date = DateTime.UtcNow, From = new User { Id = bot.BotId, IsBot = true, FirstName = "Test bot" } };
            CallbackQuery Click(long sender, string data) => new()
            {
                Id = $"callback-{sender}", From = new User { Id = sender, FirstName = "Test sender" },
                Message = menuMessage, ChatInstance = "test-chat", Data = data
            };
            MethodInfo admit = typeof(Worker).GetMethod("AcceptMenuCallbackAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task Admit(int id, CallbackQuery click)
            {
                var update = new Update { Id = id, CallbackQuery = click };
                await (Task)admit.Invoke(worker, [update, click, CancellationToken.None])!;
            }

            await Admit(101, Click(50, "wsh:menu:Queue"));
            await Admit(102, Click(adminId, "wsh:menu:/shutdown"));
            observed = await store.OffsetAsync() == 103 && (await store.SnapshotAsync()).Commands == 0 && handler.Requests.Count == 2;
            if (check == nameof(TelegramCallbacksRejectUnapprovedSendersAndArbitraryCommandsWhileAdvancingTheOffset))
            {
                return observed;
            }
            await Admit(103, Click(adminId, "wsh:menu:Queue"));
            observed = await store.OffsetAsync() == 104 && (await store.SnapshotAsync()).Commands == 1 &&
                handler.Requests.Any(request => request.Method.Equals("answerCallbackQuery", StringComparison.OrdinalIgnoreCase));
            if (check == nameof(AuthorizedTelegramClickIsDurablyAcceptedAndAcknowledged))
            {
                return observed;
            }

            using var stop = new CancellationTokenSource();
            object queueConsumer = typeof(Worker).GetField("_commandQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
            MethodInfo consume = queueConsumer.GetType().GetMethod("RunCommandQueueAsync")!;
            Task workerQueue = (Task)consume.Invoke(queueConsumer, [false, stop.Token])!;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while ((await store.SnapshotAsync()).Commands > 0) await Task.Delay(30, timeout.Token);
                var edit = handler.Requests.Single(request => request.Method.Equals("editMessageText", StringComparison.OrdinalIgnoreCase));
                using var body = JsonDocument.Parse(edit.Body);
                return body.RootElement.GetProperty("message_id").GetInt32() == menuMessage.Id &&
                    body.RootElement.GetProperty("text").GetString()!.Contains("<b>Delivery queue</b>");
            }
            finally
            {
                stop.Cancel();
                await workerQueue;
            }
        }
    }
}
