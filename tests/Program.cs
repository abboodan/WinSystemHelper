using System.Diagnostics;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WinSystemHelper;

var testRoot = Path.Combine(Path.GetTempPath(), "WinSystemHelper-queue-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
bool OnlyTrustedAccess(string path)
{
    System.Security.AccessControl.FileSystemSecurity security = Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl()
        : new FileInfo(path).GetAccessControl();
    using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
    var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
    if (!security.AreAccessRulesProtected || rules.Count == 0) return false;
    foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
    {
        var sid = (System.Security.Principal.SecurityIdentifier)rule.IdentityReference;
        if (!sid.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) &&
            !sid.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) &&
            !sid.Equals(current.User!)) return false;
    }
    return true;
}
QueueStore Store(string name, Func<int>? limit = null) => new(Path.Combine(testRoot, name), limit ?? (() => 512));
async Task<CaptureReservation> Capture(QueueStore store, int size = 32768, bool commit = true)
{
    var item = await store.ReserveAsync(1, "voice", size + 128, 1, null) ?? throw new Exception("Reservation rejected");
    await File.WriteAllBytesAsync(item.Path, new byte[size]);
    await File.WriteAllTextAsync(item.Path + ".complete", "complete");
    if (commit) await store.CommitCaptureAsync(item);
    return item;
}

try
{
    using (var store = Store("commands"))
    {
        var update = new Update { Id = 10, Message = new Message { Id = 10, Chat = new Chat { Id = 1, Type = ChatType.Private }, Date = DateTime.UtcNow, Text = "/ping" } };
        string serialized = JsonSerializer.Serialize(update);
        Check(JsonSerializer.Deserialize<Update>(serialized)?.Message?.Text == "/ping", "Telegram update round trip");
        await store.AcceptAsync(10, 1, serialized, true, false, QueueStore.Now);
        await store.AcceptAsync(10, 1, serialized, true, false, QueueStore.Now);
        Check(await store.OffsetAsync() == 11 && (await store.SnapshotAsync()).Commands == 1, "Atomic offset and duplicate prevention");
        await store.AcceptAsync(11, 1, "control", false, true, QueueStore.Now);
        Check((await store.ClaimCommandAsync(true))?.UpdateId == 11, "Control lane independent of ordinary commands");
        await store.AcceptAsync(12, 0, null, false, false, QueueStore.Now);
        Check(await store.OffsetAsync() == 13 && (await store.SnapshotAsync()).Commands == 2, "Unauthorized payload discarded");
        await store.ClaimCommandAsync(false);
    }
    using (var store = Store("commands"))
    {
        Check((await store.ClaimCommandAsync(false))?.UpdateId == 10, "Read-only running command recovered");
        Check(await store.ClaimCommandAsync(true) is null, "Interrupted side effect is not executed again");
        await store.ReportUnknownAsync();
        Check((await store.ClaimAsync(false))?.Text.Contains("unknown") == true, "Unknown outcome is reported");
    }
    Check(CommandPolicy.IsExpired("/screen", 0, 300001) && !CommandPolicy.IsExpired("/screen", 0, 300000), "Five-minute expiry boundary");
    Check(!CommandPolicy.IsExpired("/mic stop", 0, 900000) && !CommandPolicy.IsExpired("/status", 0, 900000), "Stop and status do not expire");
    using (var store = Store("stop-pending"))
    {
        var pending = new Update { Id = 1, Message = new Message { Id = 1, Chat = new Chat { Id = 1, Type = ChatType.Private }, Date = DateTime.UtcNow, Text = "/mic 10 loop" } };
        await store.AcceptAsync(1, 1, JsonSerializer.Serialize(pending), false, false, QueueStore.Now);
        await store.CancelOlderRecordingCommandsAsync(2);
        Check(await store.ClaimCommandAsync(false) is null, "Stop cancels older queued recording requests");
    }
    int limit = 1;
    using (var store = Store("capacity", () => limit))
    {
        var first = await store.ReserveAsync(1, "voice", 900000, 1, null);
        Check(first is not null && await store.ReserveAsync(1, "voice", 200000, 1, null) is null, "In-progress media counts toward capacity");
        Check((await store.SnapshotAsync()).Paused, "Quota pause is visible");
        limit = 2; store.ConfigurationChanged();
        Check(await store.ReserveAsync(1, "voice", 1000, 1, null) is not null, "Raising limit resumes below hysteresis threshold");
        limit = 1;
        Check(await store.ReserveAsync(1, "voice", 200000, 1, null) is null && (await store.SnapshotAsync()).Bytes == 901000, "Lowering limit preserves existing media");
        await store.FailCaptureAsync(first!.Id);
        await store.ManageAsync(first.Id, 1, true);
        Check(await store.ReserveAsync(1, "voice", 1000, 1, null) is not null, "Freeing capacity resumes production");
    }
    string recovered;
    using (var store = Store("recovery")) { recovered = (await Capture(store, commit: false)).Id; }
    using (var store = Store("recovery"))
    {
        var item = await store.ClaimAsync(true);
        Check(item?.Id == recovered && File.Exists(item.Path), "Completed capture recovers after process restart");
    }
    using (var store = Store("recovery"))
    {
        var item = await store.ClaimAsync(true);
        Check(item?.Id == recovered, "Interrupted upload becomes eligible again");
        await store.CompleteAsync(recovered);
        Check(!File.Exists(item!.Path) && (await store.SnapshotAsync()).Bytes == 0, "Delete follows durable delivery confirmation");
    }
    using (var store = Store("incomplete"))
    {
        var r = await store.ReserveAsync(1, "voice", 100, 1, null);
        await File.WriteAllBytesAsync(r!.Path, new byte[50]);
    }
    using (var store = Store("incomplete"))
    {
        Check(await store.ClaimAsync(true) is null && (await store.SnapshotAsync()).Failed == 1, "Incomplete capture is quarantined");
    }
    using (var store = Store("locked-file"))
    {
        var capture = await Capture(store, commit: false);
        bool failed = false;
        await using (var locked = new FileStream(capture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { await store.CommitCaptureAsync(capture); } catch (IOException) { failed = true; }
        }
        Check(failed && File.Exists(capture.Path) && (await store.SnapshotAsync()).Bytes > 0, "Filesystem failure preserves capture and reservation");
        await store.CommitCaptureAsync(capture);
        Check((await store.ClaimAsync(true))?.Id == capture.Id, "Capture can recover after filesystem failure");
    }
    using (var store = Store("persisted-pause", () => 1))
    {
        await store.ReserveAsync(1, "voice", 900000, 1, null);
        await store.ReserveAsync(1, "voice", 200000, 1, null);
    }
    using (var store = Store("persisted-pause", () => 1))
        Check(await store.ReserveAsync(1, "voice", 1000, 1, null) is null && (await store.SnapshotAsync()).Paused,
            "Quota hysteresis survives restart above 80 percent");
    var rootMethod = typeof(QueueStore).GetMethod("DefaultRoot", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    string RootFor(string token) => (string)rootMethod.Invoke(null, [new AppConfiguration { BotToken = token }])!;
    Check(RootFor("1:synthetic") == RootFor("1:rotated") && RootFor("1:synthetic") != RootFor("2:synthetic"), "Bot identities isolate queues while token rotation preserves ownership");
    using (var store = Store("delivery"))
    {
        var transport = new FakeTransport();
        var cfg = new AppConfiguration { AdminChatIds = [1] };
        var sender = new OutboxSender(store, transport, cfg, NullLogger<OutboxSender>.Instance);
        var media = await Capture(store);
        transport.Fail = true;
        await sender.DeliverOneAsync(true, CancellationToken.None);
        Check((await store.SnapshotAsync()).Bytes > 0, "Network failure retains file");
        transport.Fail = false;
        await store.ConnectedAsync();
        await sender.DeliverOneAsync(true, CancellationToken.None);
        Check((await store.SnapshotAsync()).Bytes == 0 && transport.Delivered == 1, "Reconnect drains preserved file");
        await Capture(store);
        cfg.AdminChatIds = [2];
        await sender.DeliverOneAsync(true, CancellationToken.None);
        Check((await store.SnapshotAsync()).Failed == 1 && transport.Delivered == 1, "Removed admin receives no media");
        await store.EnqueueTextAsync(1, "expired confirmation", expires: QueueStore.Now - 1);
        Check(await store.ClaimAsync(false) is null, "Expired confirmation never sent");
        await store.EnqueueTextAsync(1, "one", dedupe: "one");
        await store.EnqueueTextAsync(1, "one", dedupe: "one");
        Check((await store.ClaimAsync(false))?.Text == "one" && await store.ClaimAsync(false) is null, "Idempotent result enqueue");
    }
    using (var store = Store("text-capacity"))
    {
        for (int i = 0; i < 1900; i++) await store.EnqueueTextAsync(1, "synthetic");
        bool rejected = false;
        try { await store.EnqueueTextAsync(1, "full"); } catch (QueueCapacityException) { rejected = true; }
        await store.EnqueueTextAsync(1, "control reply", priority: true);
        Check(rejected && (await store.ClaimAsync(false))?.Text == "control reply", "Text limit reserves control capacity");
    }
    using (var store = Store("broadcast-atomic"))
    {
        for (int i = 0; i < 1899; i++) await store.EnqueueTextAsync(1, "synthetic");
        bool rejected = false;
        try { await store.EnqueueBroadcastAsync([1,2], "broadcast", "atomic"); } catch (QueueCapacityException) { rejected = true; }
        Check(rejected && (await store.SnapshotAsync()).Pending == 1899, "Broadcast storage is all-or-nothing");
    }
    using (var store = Store("fifo"))
    {
        var first = await Capture(store);
        await Task.Delay(2);
        await Capture(store);
        Check((await store.ClaimAsync(true))?.Id == first.Id, "Oldest media is claimed first");
        await store.RetryLaterAsync(first.Id, TimeSpan.FromMinutes(1), "network", false);
        Check(await store.ClaimAsync(true) is null, "Later media cannot overtake retrying media");
        await store.ConnectedAsync();
        Check((await store.ClaimAsync(true))?.Id == first.Id, "Reconnection wakes the oldest pending file");
        await store.CompleteAsync(first.Id);
        await store.RetryLaterAsync(first.Id, TimeSpan.Zero, "network", false);
        Check((await store.ClaimAsync(true))?.Id != first.Id, "Cleanup failure cannot resurrect an acknowledged delivery");
    }
    using (var store = Store("retry-policy"))
    {
        var transport = new FakeTransport { Error = new ApiRequestException("synthetic", 429, new ResponseParameters { RetryAfter = 2 }) };
        var sender = new OutboxSender(store, transport, new AppConfiguration { AdminChatIds = [1] }, NullLogger<OutboxSender>.Instance);
        await Capture(store);
        await sender.DeliverOneAsync(true, CancellationToken.None);
        await store.ConnectedAsync();
        Check(await store.ClaimAsync(true) is null, "Reconnection does not override Telegram Retry-After");
        transport.Error = new ApiRequestException("synthetic", 400);
        await store.EnqueueTextAsync(1, "bad request");
        await sender.DeliverOneAsync(false, CancellationToken.None);
        Check((await store.SnapshotAsync()).Failed == 1, "Permanent API errors stop automatic retries");
    }
    using (var store = Store("lost-ack"))
    {
        var transport = new FakeTransport { LoseAcknowledgement = true };
        var sender = new OutboxSender(store, transport, new AppConfiguration { AdminChatIds = [1] }, NullLogger<OutboxSender>.Instance);
        await Capture(store);
        await sender.DeliverOneAsync(true, CancellationToken.None);
        Check(transport.Delivered == 1 && (await store.SnapshotAsync()).Bytes > 0, "Lost acknowledgement retains the local file");
        await store.ConnectedAsync();
        await sender.DeliverOneAsync(true, CancellationToken.None);
        Check(transport.Delivered == 2 && (await store.SnapshotAsync()).Bytes == 0, "Lost acknowledgement may duplicate delivery but does not lose the file");
    }
    using (var store = Store("blocked-upload"))
    {
        var transport = new FakeTransport { HoldMedia = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var sender = new OutboxSender(store, transport, new AppConfiguration { AdminChatIds = [1,2] }, NullLogger<OutboxSender>.Instance);
        await Capture(store);
        var upload = sender.DeliverOneAsync(true, CancellationToken.None);
        await transport.MediaStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await store.EnqueueTextAsync(2, "control response", priority: true);
        await sender.DeliverOneAsync(false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        Check(transport.Delivered == 1 && !upload.IsCompleted, "Text lane remains available during a blocked upload");
        transport.HoldMedia.SetResult();
        await upload;
    }
    using (var store = Store("control-limit"))
    {
        for (int i = 0; i < 950; i++) await store.AcceptAsync(i, 1, "ordinary", false, false, QueueStore.Now);
        await store.AcceptAsync(950, 1, "rejected", false, false, QueueStore.Now);
        await store.AcceptAsync(951, 1, "/mic stop", false, true, QueueStore.Now);
        Check((await store.SnapshotAsync()).Commands == 951 && (await store.ClaimCommandAsync(true))?.UpdateId == 951,
            "Full ordinary command lane retains stop capacity");
    }
    using (var store = Store("local-stop"))
    {
        var runner = new FakeSessionRunner();
        await using var session = await LocalRecordingSession.StartAsync(store.Root, runner, CancellationToken.None);
        var watch = Stopwatch.StartNew();
        await System.IO.File.WriteAllTextAsync(Path.Combine(runner.Directory!, "stop"), "stop");
        try { await Task.Delay(1500, session.Token); } catch (OperationCanceledException) { }
        Check(session.Token.IsCancellationRequested && watch.ElapsedMilliseconds < 1000, "Local stop cancels consent session within one second");
        runner.Window.TrySetResult(0);
        string folder = Path.Combine(testRoot, "helper-scripts");
        Directory.CreateDirectory(folder);
        await System.IO.File.WriteAllTextAsync(Path.Combine(folder, "recording.ps1"), runner.Script);
        foreach (string name in new[] { "Screenshot", "CameraCapture" })
        {
            var method = typeof(Worker).GetMethod("Build" + name + "Script", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            object[] parameters = name == "Screenshot" ? [Path.Combine(folder, "synthetic.jpg")] : [Path.Combine(folder, "synthetic.jpg"), Path.Combine(folder, "error")];
            string script = (string)method.Invoke(null, parameters)!;
            await System.IO.File.WriteAllTextAsync(Path.Combine(folder, name == "Screenshot" ? "screenshot.ps1" : "camera.ps1"), script);
        }
        var reinstall = typeof(Worker).GetMethod("BuildReinstallSelfScript", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        string reinstallScript = (string)reinstall.Invoke(null, ["SyntheticService", 1, Path.Combine(folder, "Program Files", "synthetic.exe"), Path.Combine(folder, "status.txt"), Path.Combine(folder, "reinstall.log")])!;
        await System.IO.File.WriteAllTextAsync(Path.Combine(folder, "reinstall.ps1"), reinstallScript);
        Check(reinstallScript.Contains("New-Service") && !reinstallScript.Contains("binPath="), "Reinstall registers the service with a quoted path");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(AppContext.BaseDirectory, "../../../HelperScripts.Tests.ps1"), "-Folder", folder }) start.ArgumentList.Add(argument);
        using var helper = Process.Start(start)!;
        Task<string> output = helper.StandardOutput.ReadToEndAsync(), errors = helper.StandardError.ReadToEndAsync();
        await helper.WaitForExitAsync();
        if (helper.ExitCode != 0) Console.WriteLine(await errors);
        Check(helper.ExitCode == 0, "Generated PowerShell syntax and hard screenshot write cap");
    }
    {
        long[] admins = [100, -500];
        Check(CommandPolicy.IsAuthorizedSender(100, 100, admins), "Administrator in a private chat is accepted");
        Check(!CommandPolicy.IsAuthorizedSender(-500, 777, admins), "Group member who is not an administrator is rejected");
        Check(CommandPolicy.IsAuthorizedSender(-500, 100, admins), "Administrator writing in an allowlisted group is accepted");
        Check(!CommandPolicy.IsAuthorizedSender(-500, null, admins) && !CommandPolicy.IsAuthorizedSender(999, 100, admins),
            "Anonymous sender and unlisted chat are rejected");
    }
    {
        var (privateKey, publicKey) = OtaSignature.GenerateKeyPair();
        string package = Path.Combine(testRoot, "package.zip");
        byte[] content = new byte[4096];
        Random.Shared.NextBytes(content);
        await System.IO.File.WriteAllBytesAsync(package, content);
        string signature = OtaSignature.Sign(package, privateKey);
        Check(OtaSignature.Verify(package, signature, publicKey), "Signed update package verifies");
        Check(!OtaSignature.Verify(package, signature, OtaSignature.GenerateKeyPair().PublicKey), "Signature from a different key is rejected");
        Check(!OtaSignature.Verify(package, "not-base64!", publicKey) && !OtaSignature.Verify(package, null, publicKey) &&
            !OtaSignature.Verify(package, signature, "AAAA"), "Malformed or missing signature input is rejected");
        content[100] ^= 1;
        await System.IO.File.WriteAllBytesAsync(package, content);
        Check(!OtaSignature.Verify(package, signature, publicKey), "Modified update package is rejected");
    }
    {
        var paths = new SharedPathProvider(Path.Combine(testRoot, "app-root"));
        string updates = paths.GetSharedTempDirectory("Updates");
        Check(updates.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase) && OnlyTrustedAccess(updates) &&
            OnlyTrustedAccess(Path.GetDirectoryName(updates)!), "Service working folders are private to SYSTEM and administrators");
        string script = Path.Combine(updates, "synthetic.ps1");
        await System.IO.File.WriteAllTextAsync(script, "exit 0");
        var scriptSecurity = new FileInfo(script).GetAccessControl();
        bool leaked = false;
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in scriptSecurity.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            var sid = (System.Security.Principal.SecurityIdentifier)rule.IdentityReference;
            if (sid.IsWellKnown(System.Security.Principal.WellKnownSidType.InteractiveSid) || sid.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinUsersSid) ||
                sid.IsWellKnown(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid) || sid.IsWellKnown(System.Security.Principal.WellKnownSidType.WorldSid)) leaked = true;
        }
        Check(!leaked, "Updater scripts inherit no access for ordinary users");
        bool escaped = true;
        try { paths.GetSharedTempDirectory("..\\escape"); } catch (ArgumentException) { escaped = false; }
        Check(!escaped, "Service folder names cannot leave the private root");
        Check(!SharedPathProvider.LegacyPublicRoot.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase),
            "Legacy public folder is distinct from private storage");
    }
    {
        string configDirectory = Path.Combine(testRoot, "config");
        Directory.CreateDirectory(configDirectory);
        var service = new ConfigurationFileService(configDirectory);
        service.Save(new AppConfiguration { BotToken = "1:synthetic", AdminChatIds = [1] });
        service.SaveAtomic(new AppConfiguration { BotToken = "1:synthetic", AdminChatIds = [1, 2] });
        Check(OnlyTrustedAccess(service.ConfigPath) && service.Load().AdminChatIds.Length == 2,
            "Configuration file is restricted to SYSTEM, administrators and its writer");
        Check(Directory.GetFiles(configDirectory).Length == 1, "Configuration writes leave no temporary copies");
    }
    {
        string source = Path.Combine(testRoot, "install-source"), target = Path.Combine(testRoot, "install-target");
        Directory.CreateDirectory(Path.Combine(source, "runtimes"));
        foreach (string name in new[] { "app.exe", "config.json", "config.json.bak", "setup.log", Path.Combine("runtimes", "native.dll") })
            await System.IO.File.WriteAllTextAsync(Path.Combine(source, name), "synthetic");
        int copied = InstallLayout.CopyApplicationFiles(source, target);
        Check(copied == 2 && System.IO.File.Exists(Path.Combine(target, "runtimes", "native.dll")) &&
            Directory.GetFiles(target).Length == 1, "Install copy leaves configuration and logs behind");
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string custom = Path.Combine(programFiles, "Custom", "Tool");
        Check(InstallLayout.ResolveInstallDirectory(source) == InstallLayout.DefaultInstallDirectory &&
            InstallLayout.ResolveInstallDirectory(custom + Path.DirectorySeparatorChar) == custom &&
            InstallLayout.ResolveInstallDirectory(programFiles + "Evil") == InstallLayout.DefaultInstallDirectory,
            "Install relocates to Program Files unless already inside it");
        Check(InstallLayout.IsWritableByNonAdministrators(Path.GetTempPath()) && !InstallLayout.IsWritableByNonAdministrators(programFiles),
            "Install folders writable by ordinary users are detected");
    }
    if (args.Contains("--benchmark"))
    {
        using var process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(20));
        double baseline = (process.TotalProcessorTime - cpu).TotalSeconds / timer.Elapsed.TotalSeconds * 100;
        using var store = Store("benchmark");
        await store.OffsetAsync();
        var cfg = new AppConfiguration { AdminChatIds = [1] };
        var sender = new OutboxSender(store, new FakeTransport(), cfg, NullLogger<OutboxSender>.Instance);
        using var stop = new CancellationTokenSource();
        var lanes = Task.WhenAll(sender.RunLaneAsync(false, stop.Token), sender.RunLaneAsync(true, stop.Token));
        cpu = process.TotalProcessorTime; timer.Restart();
        await Task.Delay(TimeSpan.FromSeconds(20));
        double idle = (process.TotalProcessorTime - cpu).TotalSeconds / timer.Elapsed.TotalSeconds * 100;
        stop.Cancel(); await lanes;
        GC.Collect(); GC.WaitForPendingFinalizers(); process.Refresh();
        long before = process.PrivateMemorySize64;
        for (int i = 0; i < 512; i++)
        {
            var r = await store.ReserveAsync(1, "voice", 1048576, 30, null) ?? throw new Exception("Benchmark capacity");
            await using (var file = new FileStream(r.Path, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[65536];
                for (int j = 0; j < 16; j++) await file.WriteAsync(buffer);
            }
            await File.WriteAllTextAsync(r.Path + ".complete", "complete");
            await store.CommitCaptureAsync(r);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); process.Refresh();
        double memory = (process.PrivateMemorySize64 - before) / 1048576d;
        var control = Stopwatch.StartNew();
        await store.AcceptAsync(1, 1, "/mic stop", false, true, QueueStore.Now);
        await store.ClaimCommandAsync(true);
        Console.WriteLine($"BENCHMARK baseline CPU/core={baseline:F3}%; queue idle CPU/core={idle:F3}%; extra private memory={memory:F2} MiB; 512 MiB backlog control latency={control.Elapsed.TotalMilliseconds:F2}ms");
        Check(idle - baseline < 1, "Idle queue CPU increase under 1% of one core");
        Check(memory < 32, "512 MiB backlog overhead under 32 MiB");
        Check(control.Elapsed.TotalSeconds < 1, "Control lane does not wait for uploads");
        long drained = 0;
        byte[] readBuffer = new byte[65536];
        timer.Restart(); cpu = process.TotalProcessorTime;
        while (await store.ClaimAsync(true) is { } item)
        {
            await using (var stream = System.IO.File.OpenRead(item.Path!))
            { int n; while ((n = await stream.ReadAsync(readBuffer)) > 0) drained += n; }
            await store.CompleteAsync(item.Id);
        }
        Console.WriteLine($"BENCHMARK streamed={drained / 1048576} MiB; local drain={timer.Elapsed.TotalSeconds:F2}s; CPU={(process.TotalProcessorTime - cpu).TotalSeconds:F2}s");
    }
    Console.WriteLine($"All {passed} checks passed. Synthetic media only; no microphone, camera, or live Telegram access.");
}
finally
{
    string safeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WinSystemHelper-queue-tests")) + Path.DirectorySeparatorChar;
    if (Path.GetFullPath(testRoot).StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) Directory.Delete(testRoot, true);
}

sealed class FakeTransport : IOutboxTransport
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

sealed class FakeSessionRunner : IWindowsSessionProcessRunner
{
    public string? Directory { get; private set; }
    public string Script { get; private set; } = "";
    public TaskCompletionSource<uint> Window { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<uint> StartEncodedPowerShellAndWaitAsync(string script, TimeSpan timeout, CancellationToken token)
    {
        Script = script;
        string encoded = Regex.Match(script, "FromBase64String\\('([^']+)'\\)").Groups[1].Value;
        Directory = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        System.IO.File.WriteAllText(Path.Combine(Directory, "allow"), "allow");
        return Window.Task.WaitAsync(token);
    }
    public string GetActiveConsoleSessionStatus() => "synthetic";
    public string? TryGetActiveUserSid() => null;
    public void StartProcess(string fileName, string arguments) => throw new NotSupportedException();
    public Task<uint> StartProcessAndWaitAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken token) => throw new NotSupportedException();
    public void StartWindowsPowerShell(string arguments) => throw new NotSupportedException();
    public void StartEncodedPowerShell(string script) => throw new NotSupportedException();
}
