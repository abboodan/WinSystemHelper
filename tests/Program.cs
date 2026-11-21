using WinSystemHelper.Tests;

if (!args.Contains("--benchmark"))
{
    Console.WriteLine("Run dotnet test for the suite, or pass --benchmark to measure the queue.");
    return 0;
}

using var workspace = new BenchmarkWorkspace();
QueueBenchmarkResult result = await QueueBenchmark.MeasureAsync(workspace.Root);
Console.WriteLine($"BENCHMARK baseline CPU/core={result.BaselineCpu:F3}%; queue idle CPU/core={result.IdleCpu:F3}%; extra private memory={result.MemoryMiB:F2} MiB; 512 MiB backlog control latency={result.ControlLatency.TotalMilliseconds:F2}ms");
Console.WriteLine($"BENCHMARK streamed={result.DrainedBytes / 1048576} MiB; local drain={result.DrainDuration.TotalSeconds:F2}s; CPU={result.DrainCpu.TotalSeconds:F2}s");
bool passed = result.IdleCpuWithinLimit && result.MemoryWithinLimit && result.ControlLatencyWithinLimit;
Console.WriteLine(passed ? "All 3 benchmark checks passed." : "One or more benchmark limits were exceeded.");
return passed ? 0 : 1;

sealed class BenchmarkWorkspace : TestWorkspace
{
    public string Root => TestRoot;
}
