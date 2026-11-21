using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinSystemHelper.Tests;

internal static class QueueBenchmark
{
    public static async Task<QueueBenchmarkResult> MeasureAsync(string root)
    {
        using var process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(20));
        double baseline = (process.TotalProcessorTime - cpu).TotalSeconds / timer.Elapsed.TotalSeconds * 100;
        using var store = new QueueStore(Path.Combine(root, "benchmark"), () => 512);
        await store.OffsetAsync();
        var configuration = new AppConfiguration { AdminChatIds = [1] };
        var sender = new OutboxSender(store, new FakeTransport(), configuration, NullLogger<OutboxSender>.Instance);
        using var stop = new CancellationTokenSource();
        var lanes = Task.WhenAll(sender.RunLaneAsync(false, stop.Token), sender.RunLaneAsync(true, stop.Token));
        cpu = process.TotalProcessorTime;
        timer.Restart();
        await Task.Delay(TimeSpan.FromSeconds(20));
        double idle = (process.TotalProcessorTime - cpu).TotalSeconds / timer.Elapsed.TotalSeconds * 100;
        stop.Cancel();
        await lanes;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        process.Refresh();
        long before = process.PrivateMemorySize64;
        for (int i = 0; i < 512; i++)
        {
            var reservation = await store.ReserveAsync(1, QueueItemKind.Voice, 1048576, 30, null) ?? throw new Exception("Benchmark capacity");
            await using (var file = new FileStream(reservation.Path, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[65536];
                for (int j = 0; j < 16; j++)
                {
                    await file.WriteAsync(buffer);
                }
            }
            await File.WriteAllTextAsync(reservation.Path + ".complete", "complete");
            await store.CommitCaptureAsync(reservation);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        process.Refresh();
        double memory = (process.PrivateMemorySize64 - before) / 1048576d;
        var control = Stopwatch.StartNew();
        await store.AcceptAsync(1, 1, "/mic stop", false, true, QueueStore.Now);
        await store.ClaimCommandAsync(true);
        TimeSpan controlLatency = control.Elapsed;
        long drained = 0;
        byte[] readBuffer = new byte[65536];
        timer.Restart();
        cpu = process.TotalProcessorTime;
        while (await store.ClaimAsync(true) is { } item)
        {
            await using (var stream = System.IO.File.OpenRead(item.Path!))
            {
                int read;
                while ((read = await stream.ReadAsync(readBuffer)) > 0)
                {
                    drained += read;
                }
            }
            await store.CompleteAsync(item.Id);
        }

        return new QueueBenchmarkResult(baseline, idle, memory, controlLatency,
            drained, timer.Elapsed, process.TotalProcessorTime - cpu);
    }
}

internal sealed record QueueBenchmarkResult(
    double BaselineCpu, double IdleCpu, double MemoryMiB, TimeSpan ControlLatency,
    long DrainedBytes, TimeSpan DrainDuration, TimeSpan DrainCpu)
{
    public bool IdleCpuWithinLimit => IdleCpu - BaselineCpu < 1;
    public bool MemoryWithinLimit => MemoryMiB < 32;
    public bool ControlLatencyWithinLimit => ControlLatency.TotalSeconds < 1;
}
