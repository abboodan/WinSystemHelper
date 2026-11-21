using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed class PublicIpCache(
    IHttpClientFactory httpClientFactory,
    RuntimeConfiguration runtime,
    ServiceTelemetry telemetry,
    ILogger<Worker> logger)
{
    private static readonly TimeSpan PublicIpRefreshTimeout = TimeSpan.FromSeconds(3);

    private static readonly Uri PublicIpUri = new("https://api.ipify.org");

    private readonly object _publicIpSync = new();

    private string? _cachedPublicIp;

    private DateTimeOffset? _publicIpFetchedAt;

    private DateTimeOffset? _publicIpNextAttemptAt;

    private int _publicIpFailureCount;

    public PublicIpSnapshot GetPublicIpSnapshot()
    {
        lock (_publicIpSync)
        {
            return new PublicIpSnapshot(
                _cachedPublicIp,
                _publicIpFetchedAt,
                _publicIpNextAttemptAt,
                _publicIpFailureCount);
        }
    }

    public async Task<PublicIpLookupResult> GetPublicIpAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.Now;

        lock (_publicIpSync)
        {
            if (!forceRefresh &&
                !string.IsNullOrWhiteSpace(_cachedPublicIp) &&
                _publicIpFetchedAt.HasValue &&
                now - _publicIpFetchedAt.Value < GetPublicIpCacheDuration())
            {
                return PublicIpLookupResult.Cached(_cachedPublicIp, _publicIpFetchedAt.Value);
            }

            if (_publicIpNextAttemptAt.HasValue && _publicIpNextAttemptAt.Value > now)
            {
                return PublicIpLookupResult.Failed(
                    $"backoff active until {_publicIpNextAttemptAt.Value:HH:mm:ss zzz}",
                    _cachedPublicIp);
            }
        }

        try
        {
            using CancellationTokenSource timeout =
                CreateTimeoutTokenSource(cancellationToken, PublicIpRefreshTimeout);

            using HttpClient httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = Timeout.InfiniteTimeSpan;

            string publicIp = (await httpClient.GetStringAsync(PublicIpUri, timeout.Token)).Trim();
            if (string.IsNullOrWhiteSpace(publicIp))
            {
                throw new InvalidOperationException("IP service returned an empty response.");
            }

            lock (_publicIpSync)
            {
                _cachedPublicIp = publicIp;
                _publicIpFetchedAt = DateTimeOffset.Now;
                _publicIpNextAttemptAt = null;
                _publicIpFailureCount = 0;
            }

            return PublicIpLookupResult.Fresh(publicIp);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            string? staleIp;
            lock (_publicIpSync)
            {
                _publicIpFailureCount++;
                _publicIpNextAttemptAt = DateTimeOffset.Now.Add(GetPublicIpFailureBackoff());
                staleIp = _cachedPublicIp;
            }

            telemetry.RecordError($"Public IP lookup failed: {ex.Message}");
            logger.LogDebug(ex, "Public IP lookup failed.");

            return PublicIpLookupResult.Failed(ex.Message, staleIp);
        }
    }

    private TimeSpan GetPublicIpCacheDuration()
    {
        return TimeSpan.FromMinutes(Math.Clamp(runtime.Current.PublicIpCacheMinutes, 1, 1440));
    }

    public TimeSpan GetPublicIpFailureBackoff()
    {
        return TimeSpan.FromMinutes(Math.Clamp(runtime.Current.PublicIpFailureBackoffMinutes, 1, 1440));
    }
}
