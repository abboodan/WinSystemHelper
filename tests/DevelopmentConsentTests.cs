using Xunit;

namespace WinSystemHelper.Tests;

public sealed class DevelopmentConsentTests : TestWorkspace
{
    [Fact(DisplayName = "Services start in production without development permission")]
    public async Task ServicesStartInProductionWithoutDevelopmentPermission()
    {
        Assert.True(await DevelopmentModeAsync(nameof(ServicesStartInProductionWithoutDevelopmentPermission)));
    }

    [Fact(DisplayName = "Unauthorized senders cannot request development permission")]
    public async Task UnauthorizedSendersCannotRequestDevelopmentPermission()
    {
        Assert.True(await DevelopmentModeAsync(nameof(UnauthorizedSendersCannotRequestDevelopmentPermission)));
    }

    [Fact(DisplayName = "Development remains disabled until the bound console approves it")]
    public async Task DevelopmentRemainsDisabledUntilTheBoundConsoleApprovesIt()
    {
        Assert.True(await DevelopmentModeAsync(nameof(DevelopmentRemainsDisabledUntilTheBoundConsoleApprovesIt)));
    }

    [Fact(DisplayName = "Production cancels a pending request and supersedes older activations")]
    public async Task ProductionCancelsAPendingRequestAndSupersedesOlderActivations()
    {
        Assert.True(await DevelopmentModeAsync(nameof(ProductionCancelsAPendingRequestAndSupersedesOlderActivations)));
    }

    [Fact(DisplayName = "Local approval enables development mode")]
    public async Task LocalApprovalEnablesDevelopmentMode()
    {
        Assert.True(await DevelopmentModeAsync(nameof(LocalApprovalEnablesDevelopmentMode)));
    }

    [Fact(DisplayName = "One approval covers subsequent requests from the same sender and chat only")]
    public async Task OneApprovalCoversSubsequentRequestsFromTheSameSenderAndChatOnly()
    {
        Assert.True(await DevelopmentModeAsync(nameof(OneApprovalCoversSubsequentRequestsFromTheSameSenderAndChatOnly)));
    }

    [Fact(DisplayName = "Production revokes permission and cancels active development work")]
    public async Task ProductionRevokesPermissionAndCancelsActiveDevelopmentWork()
    {
        Assert.True(await DevelopmentModeAsync(nameof(ProductionRevokesPermissionAndCancelsActiveDevelopmentWork)));
    }

    [Fact(DisplayName = "Reactivation requires a fresh local approval")]
    public async Task ReactivationRequiresAFreshLocalApproval()
    {
        Assert.True(await DevelopmentModeAsync(nameof(ReactivationRequiresAFreshLocalApproval)));
    }

    [Fact(DisplayName = "A fresh local approval can reactivate development")]
    public async Task AFreshLocalApprovalCanReactivateDevelopment()
    {
        Assert.True(await DevelopmentModeAsync(nameof(AFreshLocalApprovalCanReactivateDevelopment)));
    }

    [Fact(DisplayName = "The local Stop button revokes development")]
    public async Task TheLocalStopButtonRevokesDevelopment()
    {
        Assert.True(await DevelopmentModeAsync(nameof(TheLocalStopButtonRevokesDevelopment)));
    }

    [Fact(DisplayName = "A later activation starts a separate development session")]
    public async Task ALaterActivationStartsASeparateDevelopmentSession()
    {
        Assert.True(await DevelopmentModeAsync(nameof(ALaterActivationStartsASeparateDevelopmentSession)));
    }

    [Fact(DisplayName = "A different Windows session cannot use the grant")]
    public async Task ADifferentWindowsSessionCannotUseTheGrant()
    {
        Assert.True(await DevelopmentModeAsync(nameof(ADifferentWindowsSessionCannotUseTheGrant)));
    }

    [Fact(DisplayName = "Session change revokes the grant and production remains an urgent command")]
    public async Task SessionChangeRevokesTheGrantAndProductionRemainsAnUrgentCommand()
    {
        Assert.True(await DevelopmentModeAsync(nameof(SessionChangeRevokesTheGrantAndProductionRemainsAnUrgentCommand)));
    }

    private async Task<bool> DevelopmentModeAsync(string check)
    {
        using (var store = Store("development-mode"))
        {
            var console = new WindowsConsoleSession(1, "S-1-5-21-1-2-3-1001");
            var runner = new FakeSessionRunner { ActiveConsole = console, ApproveImmediately = false };
            var mode = new DevelopmentMode(store.Root, runner, id => id is 1 or 2 or 3);
            Task<bool>? waiting = null;
            try
            {
                bool observed = mode.Name == "production" && mode.GetGrant(1, 2, QueueStore.Now) is null;
                if (check == nameof(ServicesStartInProductionWithoutDevelopmentPermission))
                {
                    return observed;
                }
                observed = !await mode.EnableAsync(1, 1, 99, CancellationToken.None) && runner.WindowsOpened == 0;
                if (check == nameof(UnauthorizedSendersCannotRequestDevelopmentPermission))
                {
                    return observed;
                }
                waiting = mode.EnableAsync(2, 1, 2, CancellationToken.None);
                observed = !waiting.IsCompleted && mode.Name == "production" && runner.RequiredConsole == console;
                if (check == nameof(DevelopmentRemainsDisabledUntilTheBoundConsoleApprovesIt))
                {
                    return observed;
                }
                mode.Disable(3);
                observed = !await waiting && !await mode.EnableAsync(2, 1, 2, CancellationToken.None);
                if (check == nameof(ProductionCancelsAPendingRequestAndSupersedesOlderActivations))
                {
                    return observed;
                }

                waiting = mode.EnableAsync(4, 1, 2, CancellationToken.None);
                File.WriteAllText(Path.Combine(runner.Directory!, "allow"), "yes");
                observed = await waiting && mode.Name == "development";
                if (check == nameof(LocalApprovalEnablesDevelopmentMode))
                {
                    return observed;
                }
                DevelopmentGrant? grant = mode.GetGrant(1, 2, QueueStore.Now);
                observed = grant is not null && ReferenceEquals(grant, mode.GetGrant(1, 2, QueueStore.Now)) &&
                    runner.WindowsOpened == 2 && mode.GetGrant(1, 3, QueueStore.Now) is null &&
                    mode.GetGrant(3, 2, QueueStore.Now) is null && mode.GetGrant(1, 2, grant.ApprovedAt - 1) is null;
                if (check == nameof(OneApprovalCoversSubsequentRequestsFromTheSameSenderAndChatOnly))
                {
                    return observed;
                }
                mode.Disable(5);
                observed = grant!.Token.IsCancellationRequested && mode.Name == "production";
                if (check == nameof(ProductionRevokesPermissionAndCancelsActiveDevelopmentWork))
                {
                    return observed;
                }

                waiting = mode.EnableAsync(6, 1, 2, CancellationToken.None);
                observed = !waiting.IsCompleted && runner.WindowsOpened == 3;
                if (check == nameof(ReactivationRequiresAFreshLocalApproval))
                {
                    return observed;
                }
                File.WriteAllText(Path.Combine(runner.Directory!, "allow"), "yes");
                observed = await waiting;
                if (check == nameof(AFreshLocalApprovalCanReactivateDevelopment))
                {
                    return observed;
                }
                grant = mode.GetGrant(1, 2, QueueStore.Now);
                File.WriteAllText(Path.Combine(runner.Directory!, "stop"), "stop");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!grant!.Token.IsCancellationRequested) await Task.Delay(50, timeout.Token);
                observed = mode.GetGrant(1, 2, QueueStore.Now) is null && mode.Name == "production";
                if (check == nameof(TheLocalStopButtonRevokesDevelopment))
                {
                    return observed;
                }

                mode.Disable(7);
                runner.ApproveImmediately = true;
                observed = await mode.EnableAsync(8, 1, 2, CancellationToken.None);
                if (check == nameof(ALaterActivationStartsASeparateDevelopmentSession))
                {
                    return observed;
                }
                grant = mode.GetGrant(1, 2, QueueStore.Now);
                runner.ActiveConsole = console with { Id = 2 };
                observed = mode.GetGrant(1, 2, QueueStore.Now) is null;
                if (check == nameof(ADifferentWindowsSessionCannotUseTheGrant))
                {
                    return observed;
                }
                while (!grant!.Token.IsCancellationRequested) await Task.Delay(50, timeout.Token);
                return mode.Name == "production" && CommandPolicy.IsReadOnly("/mode") && !CommandPolicy.IsReadOnly("/mode development") &&
                    CommandPolicy.IsControl("/mode  Production") && !CommandPolicy.IsExpired("/mode production", 0, 600000);
            }
            finally
            {
                mode.Disable();
                runner.Window.TrySetResult(0);
                if (waiting is not null)
                {
                    await waiting;
                }
            }
        }
    }
}
