using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class TelegramUpdateAttachmentTests : TestWorkspace
{
    [Theory]
    [InlineData("/update", "Use /ota-attachment")]
    [InlineData("/update invalid-signature", "Stage: verifying update signature")]
    public async Task UnwrappedTelegramZipStillRequiresASignature(string caption, string expectedReply)
    {
        string archivePath = Path.Combine(TestRoot, "unsigned.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("WinSystemHelper.exe");
        }

        string reply = await ApplyAttachmentAsync(archivePath, caption);

        Assert.True(reply.Contains(expectedReply, StringComparison.Ordinal), reply);
    }

    [Fact]
    public async Task AttachmentPreservesTheExactBytesCoveredByTheReleaseSignature()
    {
        string packagePath = Path.Combine(TestRoot, "original.zip");
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("WinSystemHelper.exe");
        }

        (string privateKey, string publicKey) = OtaSignature.GenerateKeyPair();
        string signature = OtaSignature.Sign(packagePath, privateKey);
        byte[] original = await File.ReadAllBytesAsync(packagePath);
        string attachmentPath = WriteAttachment(original, signature);
        string stagedPath = Path.Combine(TestRoot, "staged.zip");

        string stagedSignature = await TelegramUpdateAttachment.UnpackAsync(attachmentPath, stagedPath, CancellationToken.None);

        Assert.Equal(signature, stagedSignature);
        Assert.Equal(original, await File.ReadAllBytesAsync(stagedPath));
        Assert.True(OtaSignature.Verify(stagedPath, stagedSignature, publicKey));
    }

    [Fact]
    public async Task TelegramAttachmentWithAnUntrustedSignatureIsRejectedBeforeExtractingPayload()
    {
        byte[] invalidZip = [1, 2, 3];
        string packagePath = Path.Combine(TestRoot, "foreign.zip");
        await File.WriteAllBytesAsync(packagePath, invalidZip);
        string signature = OtaSignature.Sign(packagePath, OtaSignature.GenerateKeyPair().PrivateKey);
        string attachmentPath = WriteAttachment(invalidZip, signature);

        string reply = await ApplyAttachmentAsync(attachmentPath, "/update");

        Assert.Contains("Stage: verifying update signature", reply);
        Assert.Contains("it has no valid release signature", reply);
    }

    [Fact]
    public async Task TelegramAttachmentWithAnOversizedSignatureIsRejected()
    {
        string attachmentPath = WriteAttachment([1, 2, 3], new string('A', OtaSignature.MaxSignatureTextLength + 1));

        string reply = await ApplyAttachmentAsync(attachmentPath, "/update");

        Assert.Contains("The Telegram attachment has an empty or oversized release signature.", reply);
    }

    private string WriteAttachment(byte[] package, string signature)
    {
        string path = Path.Combine(TestRoot, "telegram.zip");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (Stream entry = archive.CreateEntry("WinSystemHelper-ota.zip").Open())
        {
            entry.Write(package);
        }

        using var writer = new StreamWriter(archive.CreateEntry("WinSystemHelper-ota.zip.sig").Open());
        writer.Write(signature);
        return path;
    }

    private async Task<string> ApplyAttachmentAsync(string archivePath, string caption)
    {
        byte[] attachment = await File.ReadAllBytesAsync(archivePath);
        using var http = new HttpClient(new TelegramFileHandler(attachment));
        var bot = new TelegramBotClient("1:synthetic", http);
        using IHost host = Host.CreateApplicationBuilder().Build();
        using QueueStore store = Store("telegram-ota");
        var configuration = new AppConfiguration { AdminChatIds = [17] };
        var runtime = new RuntimeConfiguration(configuration, new ConfigurationFileService(TestRoot), store);
        var commandReply = new CommandReply(new TelegramNotifier(store), runtime);
        var files = new ServiceFiles(NullLogger<Worker>.Instance, new SharedPathProvider(TestRoot));
        var installation = new ServiceInstallation(new FakeProcessRunner(), files);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var httpFactory = new UnusedHttpClientFactory();
        var service = new OtaUpdateService(commandReply, new UpdatePackage(bot, httpFactory, commandReply),
            new GitHubReleaseClient(httpFactory, runtime), files, installation, lifetime, NullLogger<Worker>.Instance);
        var message = new Message
        {
            Caption = caption,
            Document = new Document { FileId = "file-id", FileName = Path.GetFileName(archivePath), FileSize = attachment.Length }
        };

        await service.ApplyAsync(message, caption, CancellationToken.None);

        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        OutboxItem reply = Assert.IsType<OutboxItem>(await store.ClaimAsync(media: false));
        return reply.Text;
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Attachment must use Telegram.");
    }

    private sealed class TelegramFileHandler(byte[] attachment) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            HttpContent content = path.EndsWith("/getFile", StringComparison.OrdinalIgnoreCase)
                ? new StringContent("""{"ok":true,"result":{"file_id":"file-id","file_unique_id":"unique-file-id","file_path":"documents/update.zip"}}""", Encoding.UTF8, "application/json")
                : new ByteArrayContent(attachment);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
