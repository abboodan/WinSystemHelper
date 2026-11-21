using System.ComponentModel;
using NAudio;

namespace WinSystemHelper;

public enum MicRecordingStage
{
    PrepareOutput = 1,
    OpenMicrophone,
    RecordAudio,
    SaveOutput
}

/// <summary>Translates recorder exit codes into safe diagnostics for the service and Telegram.</summary>
public sealed class MicRecorderFailure(string message) : IOException(message)
{
    // The child runs in a different Windows session. Use its exit status to pass
    // numeric diagnostics without trusting files or arbitrary text from that session.
    private const uint RecorderCode = 0x57000000;
    public const int MissingOutputPath = 2;
    public const int NoAudioSamples = 3;

    private enum ErrorSource : uint { WinMm = 1, Win32, Managed, Timeout }

    public static int Encode(MicRecordingStage stage, Exception exception)
    {
        uint hresult = unchecked((uint)exception.HResult);
        return exception switch
        {
            MmException audio when (uint)audio.Result <= ushort.MaxValue => Pack(stage, ErrorSource.WinMm, (uint)audio.Result),
            Win32Exception { NativeErrorCode: > 0 and <= ushort.MaxValue } native =>
                Pack(stage, ErrorSource.Win32, (uint)native.NativeErrorCode),
            TimeoutException => Pack(stage, ErrorSource.Timeout, 0),
            _ when (hresult & 0xFFFF0000) == 0x80070000 => Pack(stage, ErrorSource.Win32, hresult & 0xFFFF),
            _ when (hresult & 0xFFFF0000) == 0x80130000 => Pack(stage, ErrorSource.Managed, hresult & 0xFFFF),
            _ => exception.HResult < 0 ? exception.HResult : 1
        };
    }

    public static string Describe(uint exitCode)
    {
        if ((exitCode & 0xFF000000) != RecorderCode)
        {
            return exitCode switch
            {
                MissingOutputPath => "The microphone recorder did not receive an output path.",
                NoAudioSamples => "The microphone returned no audio samples.",
                >= 0x80000000 => $"The microphone recorder failed with Windows status 0x{exitCode:X8}.",
                _ => $"The microphone recorder exited unexpectedly (exit code {exitCode})."
            };
        }

        var stage = (MicRecordingStage)((exitCode >> 20) & 0xF);
        var source = (ErrorSource)((exitCode >> 16) & 0xF);
        int code = (int)(exitCode & 0xFFFF);
        string operation = stage switch
        {
            MicRecordingStage.PrepareOutput => "preparing the recording file",
            MicRecordingStage.OpenMicrophone => "opening the microphone",
            MicRecordingStage.RecordAudio => "recording audio",
            MicRecordingStage.SaveOutput => "saving the recording",
            _ => "running the recorder"
        };

        string cause = source switch
        {
            ErrorSource.WinMm => $"{DescribeWinMm((MmResult)code)} (Windows audio error {(MmResult)code}, {code}).",
            ErrorSource.Win32 when code == 5 && stage == MicRecordingStage.OpenMicrophone =>
                "Windows denied microphone access (Win32 error 5). Check Windows microphone privacy settings.",
            ErrorSource.Win32 => $"{new Win32Exception(code).Message.TrimEnd('.')} (Win32 error {code}).",
            ErrorSource.Managed => $"The recorder raised a .NET error (HRESULT 0x{0x80130000u | (uint)code:X8}).",
            ErrorSource.Timeout => "The recording operation timed out.",
            _ => $"The recorder returned an unrecognized status (0x{exitCode:X8})."
        };
        return $"Microphone capture failed while {operation}. {cause}";
    }

    private static int Pack(MicRecordingStage stage, ErrorSource source, uint code) =>
        (int)(RecorderCode | ((uint)stage << 20) | ((uint)source << 16) | code);

    private static string DescribeWinMm(MmResult result) => result switch
    {
        MmResult.BadDeviceId => "The selected audio input device is unavailable",
        MmResult.NotEnabled => "The audio input device could not be enabled",
        MmResult.AlreadyAllocated or MmResult.HandleBusy => "The audio input device is busy",
        MmResult.NoDriver => "Windows has no available driver for the audio input device",
        MmResult.WaveBadFormat => "The audio input device rejected the recording format",
        _ => "The Windows audio API reported a failure"
    };
}
