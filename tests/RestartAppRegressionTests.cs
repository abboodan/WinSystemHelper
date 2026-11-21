using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class RestartAppRegressionTests
{
    [Fact]
    public async Task ProcessTerminationObservesCancellationAsynchronously()
    {
        // An empty collection checks the command's cancellation contract without killing a process.
        MethodInfo method = typeof(ProcessCommandHandler).GetMethod("TerminateProcesses",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handler = new ProcessCommandHandler(null!, null!, null!, null!, null!, null!);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        object?[] arguments = method.GetParameters().Length == 2
            ? [Array.Empty<Process>(), "synthetic"]
            : [Array.Empty<Process>(), "synthetic", cancellation.Token];

        var termination = Assert.IsAssignableFrom<Task<int>>(method.Invoke(handler, arguments));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => termination);
    }
}
