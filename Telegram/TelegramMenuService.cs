using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace WinSystemHelper;

public sealed class TelegramMenuService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<TelegramMenuService> _logger;

    public TelegramMenuService(
        ITelegramBotClient botClient,
        ILogger<TelegramMenuService> logger)
    {
        _botClient = botClient;
        _logger = logger;
    }

    public async Task RegisterAsync(
        IReadOnlyCollection<long> adminChatIds,
        TimeSpan sendTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            BotCommand[] commands = BuildTelegramCommands();

            foreach (long adminChatId in adminChatIds)
            {
                try
                {
                    using CancellationTokenSource timeout =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(sendTimeout);

                    await _botClient.SetMyCommands(
                        commands,
                        scope: new BotCommandScopeChat { ChatId = adminChatId },
                        cancellationToken: timeout.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to register Telegram command menu for admin chat {AdminChatId}.",
                        adminChatId);
                }
            }

            _logger.LogInformation("Telegram command menu registered.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register Telegram command menu.");
        }
    }

    private static BotCommand[] BuildTelegramCommands()
    {
        return
        [
            new BotCommand { Command = "status", Description = "Show service status" },
            new BotCommand { Command = "healthcheck", Description = "Show fast healthcheck" },
            new BotCommand { Command = "version", Description = "Show installed version" },
            new BotCommand { Command = "confirm", Description = "Confirm pending command" },
            new BotCommand { Command = "cancel", Description = "Cancel pending command" },
            new BotCommand { Command = "lock", Description = "Lock the workstation" },
            new BotCommand { Command = "shutdown", Description = "Request shutdown" },
            new BotCommand { Command = "restart", Description = "Request restart" },
            new BotCommand { Command = "sleep", Description = "Request sleep" },
            new BotCommand { Command = "ip", Description = "Show public IP address" },
            new BotCommand { Command = "net", Description = "Show local network report" },
            new BotCommand { Command = "alarm", Description = "Play system alert sound" },
            new BotCommand { Command = "mic", Description = "Trigger overt alarm audio recording" },
            new BotCommand { Command = "msg", Description = "Show a screen message" },
            new BotCommand { Command = "ask", Description = "Ask a Yes/No question" },
            new BotCommand { Command = "prompt", Description = "Request text input" },
            new BotCommand { Command = "speak", Description = "Speak a message" },
            new BotCommand { Command = "screen", Description = "Capture the screen" },
            new BotCommand { Command = "tasks", Description = "Show top processes" },
            new BotCommand { Command = "kill", Description = "Terminate a process" },
            new BotCommand { Command = "startup", Description = "List startup applications" },
            new BotCommand { Command = "restartapp", Description = "Restart an application" },
            new BotCommand { Command = "services", Description = "List Windows services" },
            new BotCommand { Command = "service", Description = "Manage a Windows service" },
            new BotCommand { Command = "config", Description = "Manage runtime configuration" },
            new BotCommand { Command = "update", Description = "Apply an OTA update" },
            new BotCommand { Command = "help", Description = "Show available commands" },
            new BotCommand { Command = "stop", Description = "Stop the service" },
            new BotCommand { Command = "uninstall", Description = "Stop and delete the service" }
        ];
    }
}
