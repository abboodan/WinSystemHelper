using Xunit;

namespace WinSystemHelper.Tests;

[CollectionDefinition("Queue benchmark", DisableParallelization = true)]
public sealed class BenchmarkCollection;

[Collection("Queue benchmark")]
public sealed class QueueBenchmarkTests(BenchmarkFixture fixture) : IClassFixture<BenchmarkFixture>
{
    [Fact(DisplayName = "Idle queue CPU increase under 1% of one core")]
    public async Task IdleCpuRemainsBelowLimit()
    {
        Assert.True((await fixture.Measurement).IdleCpuWithinLimit);
    }

    [Fact(DisplayName = "512 MiB backlog overhead under 32 MiB")]
    public async Task BacklogMemoryRemainsBelowLimit()
    {
        Assert.True((await fixture.Measurement).MemoryWithinLimit);
    }

    [Fact(DisplayName = "Control lane does not wait for uploads")]
    public async Task ControlLaneRemainsResponsive()
    {
        Assert.True((await fixture.Measurement).ControlLatencyWithinLimit);
    }
}

public sealed class BenchmarkFixture : TestWorkspace
{
    public BenchmarkFixture()
    {
        Measurement = QueueBenchmark.MeasureAsync(TestRoot);
    }

    internal Task<QueueBenchmarkResult> Measurement { get; }
}
