using System.ComponentModel;
using System.ServiceProcess;
using System.Text;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed class ServiceCommandHandler(
    ILogger<Worker> logger,
    CommandReply replies,
    CommandBackgroundWork backgroundWork,
    ConfirmationService confirmations) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/services", "/service"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken) => request.Word switch
    {
        "/services" => HandleServicesCommand(),
        "/service" => HandleServiceCommandAsync(request.Text, cancellationToken),
        _ => Task.CompletedTask
    };

    private Task HandleServicesCommand()
    {
        backgroundWork.QueueBackgroundWork(
            SendServicesAsync,
            "Services command failed.");
        return Task.CompletedTask;
    }

    private async Task SendServicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            ServiceController[] services = ServiceController.GetServices();
            try
            {
                string[] lines = services
                    .OrderByDescending(service => service.ServiceName.Equals(ServiceConstants.ServiceName, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(service => service.ServiceName, StringComparer.OrdinalIgnoreCase)
                    .Take(30)
                    .Select(service =>
                    {
                        try
                        {
                            return $"{service.ServiceName} - {service.Status}";
                        }
                        catch
                        {
                            return $"{service.ServiceName} - Unknown";
                        }
                    })
                    .ToArray();

                string message = string.Join(
                    Environment.NewLine,
                    ["🧩 Windows services:", .. lines, "", "Use /service status [ServiceName] for details."]);

                await replies.SendTelegramMessageOnceAsync(
                    TruncateForTelegram(message, 3900),
                    cancellationToken);
            }
            finally
            {
                foreach (ServiceController service in services)
                {
                    service.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Services command failed.");
            await replies.SendTelegramMessageOnceAsync($"⚠️ Services command failed: {ex.Message}", CancellationToken.None);
        }
    }

    private async Task HandleServiceCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string[] parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            await replies.SendTelegramMessageOnceAsync(
                "⚠️ Usage: /service status|start|stop|restart [ServiceName]",
                cancellationToken);
            return;
        }

        string action = parts[1].ToLowerInvariant();
        string serviceName = string.Join(' ', parts.Skip(2)).Trim();

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            await replies.SendTelegramMessageOnceAsync(
                "⚠️ Usage: /service status|start|stop|restart [ServiceName]",
                cancellationToken);
            return;
        }

        switch (action)
        {
            case "status":
                backgroundWork.QueueBackgroundWork(
                    token => SendServiceStatusAsync(serviceName, token),
                    "Service status command failed.");
                break;
            case "start":
            case "stop":
            case "restart":
                await confirmations.RequestDangerousConfirmationAsync(
                    $"service:{action}:{serviceName}",
                    $"{action} Windows service {serviceName}",
                    token => ControlServiceAsync(action, serviceName, token),
                    cancellationToken);
                break;
            default:
                await replies.SendTelegramMessageOnceAsync(
                    "⚠️ Usage: /service status|start|stop|restart [ServiceName]",
                    cancellationToken);
                break;
        }
    }

    private async Task SendServiceStatusAsync(string serviceName, CancellationToken cancellationToken)
    {
        try
        {
            using ServiceController service = new(serviceName);
            await replies.SendTelegramMessageOnceAsync(
                $"🧩 Service {service.ServiceName}: {service.Status}",
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            await replies.SendTelegramMessageOnceAsync(
                $"⚠️ Service {serviceName} was not found or could not be queried.",
                cancellationToken);
        }
    }

    private async Task ControlServiceAsync(
        string action,
        string serviceName,
        CancellationToken cancellationToken)
    {
        try
        {
            using ServiceController service = new(serviceName);

            switch (action)
            {
                case "start":
                    if (service.Status == ServiceControllerStatus.Running)
                    {
                        await replies.SendTelegramMessageOnceAsync(
                            $"🧩 Service {service.ServiceName} is already running.",
                            cancellationToken);
                        return;
                    }

                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    break;
                case "stop":
                    if (service.Status == ServiceControllerStatus.Stopped)
                    {
                        await replies.SendTelegramMessageOnceAsync(
                            $"🧩 Service {service.ServiceName} is already stopped.",
                            cancellationToken);
                        return;
                    }

                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    break;
                case "restart":
                    if (service.Status != ServiceControllerStatus.Stopped)
                    {
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }

                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    break;
            }

            await replies.SendTelegramMessageOnceAsync(
                $"✅ Service {service.ServiceName} {action} completed. Current status: {service.Status}.",
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
        {
            logger.LogWarning(ex, "Service control command failed for {ServiceName}.", serviceName);
            await replies.SendTelegramMessageOnceAsync(
                $"⚠️ Service {serviceName} {action} failed: {ex.Message}",
                CancellationToken.None);
        }
    }
}
