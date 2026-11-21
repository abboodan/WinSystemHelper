using Telegram.Bot;
using Telegram.Bot.Types;
using System.Net.NetworkInformation;
using Telegram.Bot.Exceptions;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed class TelegramConnection(
    IHttpClientFactory httpClientFactory,
    ILogger<Worker> logger,
    CommandReply reply,
    IHostApplicationLifetime hostLifetime,
    ITelegramBotClient botClient,
    RuntimeConfiguration runtime)
{
    private static readonly TimeSpan InitialInternetRetryDelay = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MaxInternetRetryDelay = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ConnectivityProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly Uri TelegramApiBaseUri = new("https://api.telegram.org");

    private const int TelegramBadRequestErrorCode = 400;

    private const int TelegramUnauthorizedErrorCode = 401;

    private const int TelegramForbiddenErrorCode = 403;

    public async Task<bool> WaitForInternetConnectivityAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                if (NetworkInterface.GetIsNetworkAvailable() && await CanReachTelegramAsync(cancellationToken))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Connectivity check failed.");
            }

            TimeSpan retryDelay = CalculateBackoff(
                attempt,
                InitialInternetRetryDelay,
                MaxInternetRetryDelay);

            logger.LogInformation(
                "Internet connectivity unavailable. Attempt {Attempt}; retrying in {RetryDelay}.",
                attempt,
                retryDelay);

            try
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        return false;
    }

    private async Task<bool> CanReachTelegramAsync(CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout =
                CreateTimeoutTokenSource(cancellationToken, ConnectivityProbeTimeout);

            using HttpClient httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = Timeout.InfiniteTimeSpan;

            using HttpResponseMessage response = await httpClient.GetAsync(
                TelegramApiBaseUri,
                timeout.Token);

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task SendTelegramBroadcastWithRetryAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                if (await reply.SendTelegramBroadcastOnceAsync(text, cancellationToken))
                {
                    return;
                }

                throw new InvalidOperationException("Telegram broadcast failed for every configured admin chat.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ApiRequestException ex) when (IsFatalTelegramConfigurationException(ex))
            {
                LogCriticalAndStopForFatalTelegramConfiguration(ex);
                return;
            }
            catch (Exception ex)
            {
                TimeSpan retryDelay = CalculateBackoff(
                    attempt,
                    InitialInternetRetryDelay,
                    MaxInternetRetryDelay);

                logger.LogWarning(
                    ex,
                    "Telegram broadcast failed on attempt {Attempt}. Retrying in {RetryDelay}.",
                    attempt,
                    retryDelay);

                await Task.Delay(retryDelay, cancellationToken);
            }
        }
    }

    public void LogCriticalAndStopForFatalTelegramConfiguration(ApiRequestException exception)
    {
        logger.LogCritical(
            exception,
            "Fatal Telegram configuration error. ErrorCode: {ErrorCode}. Check BotToken and AdminChatIds in config.json. The service will stop.",
            exception.ErrorCode);

        hostLifetime.StopApplication();
    }

    public static bool IsFatalTelegramConfigurationException(ApiRequestException exception)
    {
        return exception.ErrorCode is
            TelegramBadRequestErrorCode or
            TelegramUnauthorizedErrorCode or
            TelegramForbiddenErrorCode;
    }

    public async Task<bool> ValidateConfigurationOrStopAsync(CancellationToken cancellationToken)
    {
        long[] adminChatIds = runtime.GetAdminChatIdsSnapshot();
        if (string.IsNullOrWhiteSpace(runtime.Current.BotToken) || adminChatIds.Length == 0)
        {
            logger.LogCritical(
                "Fatal configuration error: BotToken or AdminChatIds is missing in config.json. The service will stop.");
            hostLifetime.StopApplication();
            return false;
        }

        if (!await WaitForInternetConnectivityAsync(cancellationToken))
        {
            return false;
        }

        try
        {
            using CancellationTokenSource botTimeout =
                CreateTimeoutTokenSource(cancellationToken, CommandReply.SendTimeout);
            User bot = await botClient.GetMe(botTimeout.Token);

            foreach (long adminChatId in adminChatIds)
            {
                using CancellationTokenSource chatTimeout =
                    CreateTimeoutTokenSource(cancellationToken, CommandReply.SendTimeout);

                await botClient.GetChat(
                    chatId: adminChatId,
                    cancellationToken: chatTimeout.Token);
            }

            logger.LogInformation(
                "Telegram configuration validated for bot @{BotUsername} and {AdminChatCount} admin chat(s).",
                bot.Username,
                adminChatIds.Length);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ApiRequestException ex) when (IsFatalTelegramConfigurationException(ex))
        {
            LogCriticalAndStopForFatalTelegramConfiguration(ex);
            return false;
        }
        catch (Exception ex) when (ex is ApiRequestException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                ex,
                "Telegram configuration validation hit a transient error. Polling retry logic will continue.");
            return true;
        }
    }
}
