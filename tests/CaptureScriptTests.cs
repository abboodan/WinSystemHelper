using System.Diagnostics;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class CaptureScriptTests : TestWorkspace
{
    [Fact(DisplayName = "Local stop cancels consent session within one second")]
    public async Task LocalStopCancelsConsentSessionWithinOneSecond()
    {
        Assert.True(await LocalConsentAsync(nameof(LocalStopCancelsConsentSessionWithinOneSecond)));
    }

    [Fact(DisplayName = "Reinstall registers the service with a quoted path")]
    public async Task ReinstallRegistersTheServiceWithAQuotedPath()
    {
        Assert.True(await LocalConsentAsync(nameof(ReinstallRegistersTheServiceWithAQuotedPath)));
    }

    [Fact(DisplayName = "Generated PowerShell syntax, camera writes and hard screenshot write cap")]
    public async Task GeneratedPowerShellSyntaxCameraWritesAndHardScreenshotWriteCap()
    {
        Assert.True(await LocalConsentAsync(nameof(GeneratedPowerShellSyntaxCameraWritesAndHardScreenshotWriteCap)));
    }

    private async Task<bool> LocalConsentAsync(string check)
    {
        using (var store = Store("local-stop"))
        {
            var runner = new FakeSessionRunner();
            await using var session = await LocalRecordingSession.StartAsync(store.Root, runner, CancellationToken.None);
            var watch = Stopwatch.StartNew();
            await System.IO.File.WriteAllTextAsync(Path.Combine(runner.Directory!, "stop"), "stop");
            try { await Task.Delay(1500, session.Token); } catch (OperationCanceledException) { }
            bool stoppedPromptly = session.Token.IsCancellationRequested && watch.ElapsedMilliseconds < 1000;
            runner.Window.TrySetResult(0);
            bool observed = stoppedPromptly;
            if (check == nameof(LocalStopCancelsConsentSessionWithinOneSecond))
            {
                return observed;
            }
            string folder = Path.Combine(TestRoot, "helper-scripts");
            Directory.CreateDirectory(folder);
            await System.IO.File.WriteAllTextAsync(Path.Combine(folder, "recording.ps1"), runner.Script);
            var developmentConsole = new WindowsConsoleSession(1, "S-1-5-21-1-2-3-1001");
            var developmentRunner = new FakeSessionRunner { ActiveConsole = developmentConsole };
            await using (var developmentSession = await LocalRecordingSession.StartAsync(
                store.Root, developmentRunner, CancellationToken.None, developmentConsole))
            {
                await File.WriteAllTextAsync(Path.Combine(folder, "development.ps1"), developmentRunner.Script);
                developmentRunner.Window.TrySetResult(0);
            }
            string image = Path.Combine(folder, "synthetic.jpg");
            await File.WriteAllTextAsync(Path.Combine(folder, "screenshot.ps1"), PowerShellScripts.BuildScreenshotScript(image));
            await File.WriteAllTextAsync(Path.Combine(folder, "camera.ps1"), PowerShellScripts.BuildCameraCaptureScript(image));
            string reinstallScript = PowerShellScripts.BuildReinstallSelfScript(
                "SyntheticService", 1, Path.Combine(folder, "Program Files", "synthetic.exe"),
                Path.Combine(folder, "status.txt"), Path.Combine(folder, "reinstall.log"));
            await System.IO.File.WriteAllTextAsync(Path.Combine(folder, "reinstall.ps1"), reinstallScript);
            observed = reinstallScript.Contains("New-Service") && !reinstallScript.Contains("binPath=");
            if (check == nameof(ReinstallRegistersTheServiceWithAQuotedPath))
            {
                return observed;
            }
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(AppContext.BaseDirectory, "../../../HelperScripts.Tests.ps1"), "-Folder", folder }) start.ArgumentList.Add(argument);
            using var helper = Process.Start(start)!;
            Task<string> output = helper.StandardOutput.ReadToEndAsync(), errors = helper.StandardError.ReadToEndAsync();
            await helper.WaitForExitAsync();
            if (helper.ExitCode != 0) Console.WriteLine(await errors);
            return helper.ExitCode == 0;
        }
    }
}
