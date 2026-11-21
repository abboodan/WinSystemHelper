using System.IO.Compression;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using static WinSystemHelper.CommandUtilities;
using static WinSystemHelper.ServiceConstants;
using static WinSystemHelper.WindowsProcess;

namespace WinSystemHelper;

internal sealed class UpdatePackage(
    ITelegramBotClient botClient,
    IHttpClientFactory httpClientFactory,
    CommandReply reply)
{
    private static readonly TimeSpan UpdateDownloadTimeout = TimeSpan.FromMinutes(5);

    private const long MaxUpdatePackageBytes = 100L * 1024L * 1024L;

    private const long MaxTelegramUpdateDocumentBytes = 20L * 1024L * 1024L;

    private const string ZipExtension = ".zip";

    public async Task DownloadTelegramUpdatePackageAsync(
        Document document,
        string zipPath,
        CancellationToken cancellationToken)
    {
        long documentSize = document.FileSize ?? 0;

        if (documentSize > MaxTelegramUpdateDocumentBytes)
        {
            throw new InvalidOperationException(
                $"Telegram document is {FormatBytes(documentSize)}. Telegram Bot API cannot download update documents over {FormatBytes(MaxTelegramUpdateDocumentBytes)}. Use /update github or /update https://... instead.");
        }

        if (documentSize > MaxUpdatePackageBytes)
        {
            throw new InvalidOperationException(
                $"Update package is too large. Maximum allowed size is {FormatBytes(MaxUpdatePackageBytes)}.");
        }

        using CancellationTokenSource timeout =
            CreateTimeoutTokenSource(cancellationToken, UpdateDownloadTimeout);

        TGFile telegramFile = await botClient.GetFile(document.FileId, timeout.Token);
        if (string.IsNullOrWhiteSpace(telegramFile.FilePath))
        {
            throw new InvalidOperationException(
                $"Telegram returned no downloadable file path for '{document.FileName ?? document.FileId}'.");
        }

        await using FileStream destination = File.Create(zipPath);
        await botClient.DownloadFile(telegramFile, destination, timeout.Token);

        EnsureFileWithinUpdateSizeLimit(zipPath);
    }

    public async Task DownloadUrlUpdatePackageAsync(
        Uri updateUri,
        string zipPath,
        bool sendProgress,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CreateTimeoutTokenSource(cancellationToken, UpdateDownloadTimeout);

        using HttpClient httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        using HttpResponseMessage response = await httpClient.GetAsync(
            updateUri,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength > MaxUpdatePackageBytes)
        {
            throw new InvalidOperationException(
                $"Update package is too large. Maximum allowed size is {FormatBytes(MaxUpdatePackageBytes)}.");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using FileStream destination = File.Create(zipPath);
        Func<int, CancellationToken, Task>? progressCallback = sendProgress
            ? async (percent, token) =>
            {
                await reply.SendTelegramMessageOnceAsync(
                    $"⬇️ GitHub update download progress: {percent}%",
                    token);
            }
            : null;

        await CopyStreamWithLimitAsync(
            source,
            destination,
            MaxUpdatePackageBytes,
            contentLength,
            progressCallback,
            timeout.Token);

        EnsureFileWithinUpdateSizeLimit(zipPath);
    }

    public async Task<string?> DownloadSignatureTextAsync(Uri signatureUri, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CreateTimeoutTokenSource(cancellationToken, GitHubReleaseClient.LookupTimeout);

        using HttpClient httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WinSystemHelper");

        using HttpResponseMessage response = await httpClient.GetAsync(
            signatureUri,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            // A missing signature is reported by the verification step.
            return null;
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token);
        byte[] buffer = new byte[1024];
        int read = await source.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, timeout.Token);
        return Encoding.ASCII.GetString(buffer, 0, read).Trim();
    }

    private static async Task CopyStreamWithLimitAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        long? totalBytesHint,
        Func<int, CancellationToken, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long totalBytes = 0;
        int nextProgressPercent = 10;

        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                if (progressCallback is not null)
                {
                    await progressCallback(100, cancellationToken);
                }

                return;
            }

            totalBytes += bytesRead;
            if (totalBytes > maxBytes)
            {
                throw new InvalidOperationException(
                    $"Update package is too large. Maximum allowed size is {FormatBytes(maxBytes)}.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

            if (progressCallback is not null && totalBytesHint is > 0)
            {
                int percent = (int)Math.Clamp(totalBytes * 100 / totalBytesHint.Value, 0, 100);
                while (nextProgressPercent < 100 && percent >= nextProgressPercent)
                {
                    await progressCallback(nextProgressPercent, cancellationToken);
                    nextProgressPercent += 10;
                }
            }
        }
    }

    public static Task ExtractUpdatePackageAsync(
        string zipPath,
        string extractRoot,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                string fullExtractRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(extractRoot));

                using ZipArchive archive = ZipFile.OpenRead(zipPath);
                if (archive.Entries.Count == 0)
                {
                    throw new InvalidOperationException("Update package is empty.");
                }

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(entry.FullName))
                    {
                        continue;
                    }

                    string destinationPath = Path.GetFullPath(Path.Combine(extractRoot, entry.FullName));
                    if (!IsPathWithinDirectory(destinationPath, fullExtractRoot))
                    {
                        throw new InvalidOperationException(
                            $"Update package contains an unsafe path: {entry.FullName}");
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    entry.ExtractToFile(destinationPath, overwrite: true);
                }
            },
            cancellationToken);
    }

    public static string ResolveUpdatePayloadRoot(string extractRoot)
    {
        string[] rootFiles = Directory.GetFiles(extractRoot);
        string[] rootDirectories = Directory.GetDirectories(extractRoot);

        if (rootFiles.Length == 0 && rootDirectories.Length == 1)
        {
            return rootDirectories[0];
        }

        return extractRoot;
    }

    public static void ValidateUpdatePayloadRoot(string payloadRoot)
    {
        string expectedExecutableName = Path.GetFileName(GetCurrentExecutablePath());
        string expectedExecutablePath = Path.Combine(payloadRoot, expectedExecutableName);

        if (!File.Exists(expectedExecutablePath))
        {
            throw new InvalidOperationException(
                $"Update package must contain {expectedExecutableName} at the payload root.");
        }

        FileInfo executableInfo = new(expectedExecutablePath);
        if (executableInfo.Length == 0)
        {
            throw new InvalidOperationException(
                $"Update package contains an empty {expectedExecutableName} file.");
        }

        if (Directory.EnumerateFiles(payloadRoot, ConfigFileName, SearchOption.AllDirectories).Any())
        {
            throw new InvalidOperationException(
                $"Update package must not include {ConfigFileName}. Existing local configuration is preserved.");
        }
    }

    public static bool IsZipFileName(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName) &&
            Path.GetExtension(fileName).Equals(ZipExtension, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseUpdateUri(string urlText, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri? parsedUri))
        {
            error = "provide an absolute HTTPS URL ending in .zip.";
            return false;
        }

        if (!parsedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "only HTTPS update URLs are allowed.";
            return false;
        }

        if (!Path.GetExtension(parsedUri.AbsolutePath).Equals(ZipExtension, StringComparison.OrdinalIgnoreCase))
        {
            error = "update URL must point to a .zip file.";
            return false;
        }

        uri = parsedUri;
        return true;
    }

    private static void EnsureFileWithinUpdateSizeLimit(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length == 0)
        {
            throw new InvalidOperationException("Update package download produced an empty file.");
        }

        if (file.Length > MaxUpdatePackageBytes)
        {
            throw new InvalidOperationException(
                $"Update package is too large. Maximum allowed size is {FormatBytes(MaxUpdatePackageBytes)}.");
        }
    }

    private static string FormatBytes(long bytes)
    {
        return $"{bytes / 1024d / 1024d:N1} MB";
    }

    private static string EnsureTrailingDirectorySeparator(string directory)
    {
        return directory.EndsWith(Path.DirectorySeparatorChar) ||
            directory.EndsWith(Path.AltDirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = EnsureTrailingDirectorySeparator(Path.GetFullPath(directory));
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
