using System.Text.Json;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed record GitHubReleaseInfo(
        string TagName,
        string AssetName,
        Uri AssetDownloadUri,
        Uri? SignatureDownloadUri,
        DateTimeOffset PublishedAt);

internal sealed class GitHubReleaseClient(IHttpClientFactory httpClientFactory, RuntimeConfiguration runtime)
{
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(15);

    public async Task<GitHubReleaseInfo> GetLatestGitHubReleaseInfoAsync(CancellationToken cancellationToken)
    {
        AppConfiguration configuration = runtime.GetConfigurationSnapshot();
        string owner = configuration.GitHubOwner.Trim();
        string repo = configuration.GitHubRepo.Trim();
        string assetName = configuration.GitHubReleaseAssetName.Trim();

        if (string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repo) ||
            string.IsNullOrWhiteSpace(assetName))
        {
            throw new InvalidOperationException(
                "GitHubOwner, GitHubRepo, and GitHubReleaseAssetName must be configured.");
        }

        Uri apiUri = new(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest");

        using CancellationTokenSource timeout =
            CreateTimeoutTokenSource(cancellationToken, LookupTimeout);

        using HttpClient httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WinSystemHelper");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using HttpResponseMessage response = await httpClient.GetAsync(
            apiUri,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        response.EnsureSuccessStatusCode();

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: timeout.Token);
        JsonElement root = document.RootElement;

        string tagName = root.TryGetProperty("tag_name", out JsonElement tagElement)
            ? tagElement.GetString() ?? "unknown"
            : "unknown";

        DateTimeOffset publishedAt = DateTimeOffset.Now;
        if (root.TryGetProperty("published_at", out JsonElement publishedElement) &&
            DateTimeOffset.TryParse(publishedElement.GetString(), out DateTimeOffset parsedPublishedAt))
        {
            publishedAt = parsedPublishedAt;
        }

        if (!root.TryGetProperty("assets", out JsonElement assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Latest GitHub Release contains no assets.");
        }

        Uri? packageUri = null;
        Uri? signatureUri = null;
        string signatureAssetName = assetName + OtaSignature.SignatureFileSuffix;

        foreach (JsonElement assetElement in assetsElement.EnumerateArray())
        {
            string? currentAssetName = assetElement.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString()
                : null;

            bool isPackage = string.Equals(currentAssetName, assetName, StringComparison.OrdinalIgnoreCase);
            bool isSignature = string.Equals(currentAssetName, signatureAssetName, StringComparison.OrdinalIgnoreCase);
            if (!isPackage && !isSignature)
            {
                continue;
            }

            string? downloadUrl = assetElement.TryGetProperty("browser_download_url", out JsonElement urlElement)
                ? urlElement.GetString()
                : null;

            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? assetUri) ||
                !assetUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"GitHub Release asset '{currentAssetName}' has no valid HTTPS download URL.");
            }

            if (isPackage)
            {
                packageUri = assetUri;
            }
            else
            {
                signatureUri = assetUri;
            }
        }

        if (packageUri is null)
        {
            throw new InvalidOperationException(
                $"Latest GitHub Release '{tagName}' does not contain asset '{assetName}'.");
        }

        return new GitHubReleaseInfo(tagName, assetName, packageUri, signatureUri, publishedAt);
    }
}
