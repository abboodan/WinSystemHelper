namespace WinSystemHelper;

internal readonly record struct PublicIpLookupResult(
    bool Success,
    string? PublicIp,
    bool FromCache,
    string? ErrorMessage,
    string? StalePublicIp)
{
    public static PublicIpLookupResult Cached(string publicIp, DateTimeOffset fetchedAt)
    {
        return new PublicIpLookupResult(true, publicIp, FromCache: true, ErrorMessage: null, StalePublicIp: null);
    }

    public static PublicIpLookupResult Fresh(string publicIp)
    {
        return new PublicIpLookupResult(true, publicIp, FromCache: false, ErrorMessage: null, StalePublicIp: null);
    }

    public static PublicIpLookupResult Failed(string errorMessage, string? stalePublicIp)
    {
        return new PublicIpLookupResult(false, null, FromCache: false, errorMessage, stalePublicIp);
    }
}
