using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;

namespace WinSystemHelper.Tests;

internal sealed class FailingService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(100, stoppingToken);
        throw new InvalidOperationException("synthetic host failure");
    }
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<string> Calls { get; } = [];
    public Task<ProcessCaptureResult> CaptureAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add($"{fileName} {arguments}");
        return Task.FromResult(new ProcessCaptureResult(0, "", ""));
    }
}

internal sealed class FakeTelegramHttpHandler : HttpMessageHandler
{
    public ConcurrentQueue<(string Method, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string method = request.RequestUri!.Segments[^1];
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((method, body));
        string json = method.Equals("answerCallbackQuery", StringComparison.OrdinalIgnoreCase)
            ? """{"ok":true,"result":true}"""
            : """{"ok":true,"result":{"message_id":57,"date":1760000000,"chat":{"id":-1,"type":"group"},"text":"Test reply"}}""";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

internal sealed class FakeTransport : IOutboxTransport
{
    public bool Fail { get; set; }
    public Exception? Error { get; set; }
    public bool LoseAcknowledgement { get; set; }
    public TaskCompletionSource? HoldMedia { get; set; }
    public TaskCompletionSource MediaStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Delivered { get; private set; }
    public async Task SendAsync(OutboxItem item, CancellationToken token)
    {
        if (Fail) throw new HttpRequestException("Synthetic outage");
        if (Error is not null) throw Error;
        if (item.Kind != "text" && HoldMedia is not null)
        {
            MediaStarted.TrySetResult();
            await HoldMedia.Task.WaitAsync(token);
        }
        Delivered++;
        if (LoseAcknowledgement) { LoseAcknowledgement = false; throw new HttpRequestException("Synthetic lost acknowledgement"); }
    }
}

internal sealed class FakeSessionRunner : IWindowsSessionProcessRunner
{
    public string? Directory { get; private set; }
    public string Script { get; private set; } = "";
    public TaskCompletionSource<uint> Window { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public WindowsConsoleSession? ActiveConsole { get; set; }
    public WindowsConsoleSession? RequiredConsole { get; private set; }
    public bool ApproveImmediately { get; set; } = true;
    public int WindowsOpened { get; private set; }
    public Task<uint> StartEncodedPowerShellAndWaitAsync(string script, TimeSpan timeout, CancellationToken token,
        WindowsConsoleSession? requiredSession = null)
    {
        RequiredConsole = requiredSession;
        WindowsOpened++;
        Script = script;
        string encoded = Regex.Match(script, "FromBase64String\\('([^']+)'\\)").Groups[1].Value;
        Directory = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        if (ApproveImmediately) File.WriteAllText(Path.Combine(Directory, "allow"), "allow");
        Window = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return Window.Task.WaitAsync(token);
    }
    public string GetActiveConsoleSessionStatus() => "synthetic";
    public string? TryGetActiveUserSid() => ActiveConsole?.UserSid;
    public WindowsConsoleSession? TryGetActiveConsoleSession() => ActiveConsole;
    public void StartProcess(string fileName, string arguments) => throw new NotSupportedException();
    public Task<uint> StartProcessAndWaitAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken token,
        WindowsConsoleSession? requiredSession = null) => throw new NotSupportedException();
    public void StartWindowsPowerShell(string arguments) => throw new NotSupportedException();
    public void StartEncodedPowerShell(string script) => throw new NotSupportedException();
}
