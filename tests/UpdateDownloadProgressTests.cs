using System.Net;
using Telegram.Bot;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class UpdateDownloadProgressTests : TestWorkspace
{
    [Theory]
    [InlineData(8192)]
    [InlineData(10_000_000)]
    public async Task GitHubDownloadQueuesEachTenPercentMilestoneOnce(int byteCount)
    {
        using QueueStore store = Store("update-progress");
        var configuration = new AppConfiguration { AdminChatIds = [17] };
        var runtime = new RuntimeConfiguration(configuration, new ConfigurationFileService(TestRoot), store);
        var reply = new CommandReply(new TelegramNotifier(store), runtime);
        var package = new UpdatePackage(new TelegramBotClient("1:synthetic"), new DownloadClientFactory(byteCount), reply);
        string zipPath = Path.Combine(TestRoot, "update.zip");

        await package.DownloadUrlUpdatePackageAsync(new Uri("https://example.invalid/update.zip"), zipPath,
            sendProgress: true, CancellationToken.None);

        var messages = new List<string>();
        while (await store.ClaimAsync(media: false) is { } item)
        {
            messages.Add(item.Text);
        }

        Assert.Equal(byteCount, new FileInfo(zipPath).Length);
        var expected = Enumerable.Range(1, 10).Select(step => $"⬇️ GitHub update download progress: {step * 10}%");
        Assert.Equal(expected.Order(StringComparer.Ordinal), messages.Order(StringComparer.Ordinal));
    }

    private sealed class DownloadClientFactory(int byteCount) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new DownloadHandler(byteCount));
    }

    private sealed class DownloadHandler(int byteCount) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[byteCount])
            });
        }
    }
}
