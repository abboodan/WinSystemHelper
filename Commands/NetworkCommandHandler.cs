using System.Net.NetworkInformation;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed class NetworkCommandHandler(
    ILogger<Worker> logger,
    CommandReply reply,
    CommandBackgroundWork backgroundWork,
    CooldownService cooldown,
    PublicIpCache publicIp) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/ip", "/net"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken) => request.Word switch
    {
        "/ip" => HandlePublicIpCommandAsync(request.Text, cancellationToken),
        "/net" => SendNetworkReportAsync(cancellationToken),
        _ => Task.CompletedTask
    };

    private async Task HandlePublicIpCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string payload = GetCommandPayload(commandText);
        bool forceRefresh = payload.Equals("refresh", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(payload) && !forceRefresh)
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Usage: /ip or /ip refresh", cancellationToken);
            return;
        }

        if (forceRefresh &&
            cooldown.TryGetRemaining("ip-refresh", publicIp.GetPublicIpFailureBackoff(), out TimeSpan remaining))
        {
            await reply.SendTelegramMessageOnceAsync(
                $"⏳ Public IP refresh is cooling down. Try again in {FormatDurationForHumans(remaining)}.",
                cancellationToken);
            return;
        }

        PublicIpLookupResult result = await publicIp.GetPublicIpAsync(forceRefresh, cancellationToken);

        if (result.Success && !string.IsNullOrWhiteSpace(result.PublicIp))
        {
            await reply.SendTelegramMessageOnceAsync(
                $"🌐 Public IP Address: {result.PublicIp}{(result.FromCache ? " (cached)" : string.Empty)}",
                cancellationToken);
            return;
        }

        if (forceRefresh)
        {
            cooldown.Mark("ip-refresh");
        }

        string staleText = string.IsNullOrWhiteSpace(result.StalePublicIp)
            ? "No cached IP is available."
            : $"Cached IP: {result.StalePublicIp}.";

        await reply.SendTelegramMessageOnceAsync(
            $"⚠️ Public IP unavailable: {result.ErrorMessage ?? "request failed"}. {staleText}",
            cancellationToken);
    }

    private async Task SendNetworkReportAsync(CancellationToken cancellationToken)
    {
        backgroundWork.QueueBackgroundWork(SendNetworkReportCoreAsync, "Network report command failed.");
        await Task.CompletedTask;
    }

    private async Task SendNetworkReportCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            List<string> lines =
            [
                "🌐 Network report:",
                $"Network available: {NetworkInterface.GetIsNetworkAvailable()}",
                $"Public IP cache: {FormatPublicIpSnapshot(publicIp.GetPublicIpSnapshot())}",
                ""
            ];

            NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter =>
                    adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .OrderByDescending(adapter => adapter.Speed)
                .Take(6)
                .ToArray();

            if (adapters.Length == 0)
            {
                lines.Add("No active network adapters found.");
            }

            foreach (NetworkInterface adapter in adapters)
            {
                try
                {
                    IPInterfaceProperties properties = adapter.GetIPProperties();
                    string[] addresses = properties.UnicastAddresses
                        .Where(address =>
                            address.Address.AddressFamily is
                                System.Net.Sockets.AddressFamily.InterNetwork or
                                System.Net.Sockets.AddressFamily.InterNetworkV6)
                        .Select(address => address.Address.ToString())
                        .Take(4)
                        .ToArray();
                    string[] gateways = properties.GatewayAddresses
                        .Select(gateway => gateway.Address.ToString())
                        .Where(address => !string.IsNullOrWhiteSpace(address) && address != "0.0.0.0")
                        .Take(2)
                        .ToArray();
                    string[] dnsServers = properties.DnsAddresses
                        .Select(address => address.ToString())
                        .Take(3)
                        .ToArray();

                    lines.Add($"Adapter: {adapter.Name}");
                    lines.Add($"  Type: {adapter.NetworkInterfaceType} | Speed: {adapter.Speed / 1_000_000:N0} Mbps");
                    lines.Add($"  IP: {(addresses.Length == 0 ? "None" : string.Join(", ", addresses))}");
                    lines.Add($"  Gateway: {(gateways.Length == 0 ? "None" : string.Join(", ", gateways))}");
                    lines.Add($"  DNS: {(dnsServers.Length == 0 ? "None" : string.Join(", ", dnsServers))}");
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to inspect network adapter {AdapterName}.", adapter.Name);
                }
            }

            await reply.SendTelegramMessageOnceAsync(
                TruncateForTelegram(string.Join(Environment.NewLine, lines), 3900),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Network report command failed.");
            await reply.SendTelegramMessageOnceAsync($"⚠️ Network report failed: {ex.Message}", CancellationToken.None);
        }
    }
}
