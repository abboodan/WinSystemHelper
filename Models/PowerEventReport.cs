namespace WinSystemHelper;

internal sealed class PowerEventReport
{
    public string Id { get; set; } = string.Empty;

    public PowerEventKind Kind { get; set; }

    public PowerEventSource Source { get; set; }

    public DateTimeOffset DetectedAt { get; set; }

    public string Message { get; set; } = string.Empty;

    public bool Delivered { get; set; }
}
