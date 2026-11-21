using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class RecorderFailureTests : TestWorkspace
{
    [Fact(DisplayName = "Microphone diagnostics preserve the original Windows audio result")]
    public async Task MicrophoneDiagnosticsPreserveTheOriginalWindowsAudioResult()
    {
        Assert.True(await MicrophoneFailureAsync(nameof(MicrophoneDiagnosticsPreserveTheOriginalWindowsAudioResult)));
    }

    [Fact(DisplayName = "Microphone diagnostics distinguish denied input access from a denied file write")]
    public async Task MicrophoneDiagnosticsDistinguishDeniedInputAccessFromADeniedFileWrite()
    {
        Assert.True(await MicrophoneFailureAsync(nameof(MicrophoneDiagnosticsDistinguishDeniedInputAccessFromADeniedFileWrite)));
    }

    [Fact(DisplayName = "Microphone diagnostics distinguish an empty recording from a timeout")]
    public async Task MicrophoneDiagnosticsDistinguishAnEmptyRecordingFromATimeout()
    {
        Assert.True(await MicrophoneFailureAsync(nameof(MicrophoneDiagnosticsDistinguishAnEmptyRecordingFromATimeout)));
    }

    [Fact(DisplayName = "Recorder process returns the actual storage error without exposing the local path")]
    public async Task RecorderProcessReturnsTheActualStorageErrorWithoutExposingTheLocalPath()
    {
        Assert.True(await MicrophoneFailureAsync(nameof(RecorderProcessReturnsTheActualStorageErrorWithoutExposingTheLocalPath)));
    }

    [Fact(DisplayName = "File log and queue display the same failure returned by the real recorder helper")]
    public async Task FileLogAndQueueDisplayTheSameFailureReturnedByTheRealRecorderHelper()
    {
        Assert.True(await MicrophoneFailureAsync(nameof(FileLogAndQueueDisplayTheSameFailureReturnedByTheRealRecorderHelper)));
    }

    private async Task<bool> MicrophoneFailureAsync(string check)
    {
        using (var store = Store("microphone-failure"))
        {
            string busy = MicRecorderFailure.Describe((uint)MicRecorderFailure.Encode(MicRecordingStage.OpenMicrophone,
                new NAudio.MmException(NAudio.MmResult.AlreadyAllocated, "waveInOpen")));
            bool observed = busy.Contains("opening the microphone") && busy.Contains("device is busy") && busy.Contains("AlreadyAllocated");
            if (check == nameof(MicrophoneDiagnosticsPreserveTheOriginalWindowsAudioResult))
            {
                return observed;
            }

            var denied = new UnauthorizedAccessException();
            string inputDenied = MicRecorderFailure.Describe((uint)MicRecorderFailure.Encode(MicRecordingStage.OpenMicrophone, denied));
            string outputDenied = MicRecorderFailure.Describe((uint)MicRecorderFailure.Encode(MicRecordingStage.SaveOutput, denied));
            observed = inputDenied.Contains("Windows denied microphone access") && outputDenied.Contains("saving the recording") &&
                outputDenied.Contains("Win32 error 5") && !outputDenied.Contains("microphone access");
            if (check == nameof(MicrophoneDiagnosticsDistinguishDeniedInputAccessFromADeniedFileWrite))
            {
                return observed;
            }
            observed = MicRecorderFailure.Describe(MicRecorderFailure.NoAudioSamples).Contains("no audio samples") &&
                MicRecorderFailure.Describe((uint)MicRecorderFailure.Encode(MicRecordingStage.RecordAudio, new TimeoutException())).Contains("timed out");
            if (check == nameof(MicrophoneDiagnosticsDistinguishAnEmptyRecordingFromATimeout))
            {
                return observed;
            }

            // A file in place of the output directory forces the real helper to fail before opening any device.
            string blocked = Path.Combine(TestRoot, "blocked-recorder-output");
            File.WriteAllText(blocked, "test fixture");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { typeof(Worker).Assembly.Location, "/recordmic-helper", "/out", Path.Combine(blocked, "capture.wav") })
            {
                start.ArgumentList.Add(argument);
            }

            using var helper = Process.Start(start)!;
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            string reason = MicRecorderFailure.Describe(unchecked((uint)helper.ExitCode));
            observed = helper.ExitCode != 0 && reason.Contains("preparing the recording file") && reason.Contains("Win32 error") && !reason.Contains(blocked);
            if (check == nameof(RecorderProcessReturnsTheActualStorageErrorWithoutExposingTheLocalPath))
            {
                return observed;
            }

            var capture = await store.ReserveAsync(1, "voice", 32128, 1, null) ?? throw new Exception("Could not reserve test audio");
            using var log = new FileLogStore(Path.Combine(TestRoot, "microphone-failure-log"));
            using var provider = new FileLoggerProvider(log);
            provider.CreateLogger("WinSystemHelper.Worker").LogWarning("Microphone capture {CaptureId} failed. {Reason}", capture.Id, reason);
            await store.FailCaptureAsync(capture.Id, reason);
            return (await store.SnapshotAsync(1)).Problems.Single().Error == reason && log.ReadTail(5, 1500).Contains(reason);
        }
    }
}
