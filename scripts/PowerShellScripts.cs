using System.Globalization;

using static WinSystemHelper.PowerShellExitCodes;

namespace WinSystemHelper;

internal static partial class PowerShellScripts
{
    public static string BuildMessageBoxScript(
        string message,
        string buttons,
        string icon,
        bool includeAskExitCodes)
    {
        string script = Render("MessageBox",
            ("message", message),
            ("buttons", buttons),
            ("icon", icon));

        if (!includeAskExitCodes)
        {
            return script + Environment.NewLine + "exit 0";
        }

        return script + Environment.NewLine + Render("AskExitCodes",
            ("askYesExitCode", AskYesExitCode.ToString(CultureInfo.CurrentCulture)),
            ("askNoExitCode", AskNoExitCode.ToString(CultureInfo.CurrentCulture)));
    }

    public static string BuildInputBoxScript(string prompt, string outputPath)
    {
        return Render("InputBox",
            ("prompt", prompt),
            ("outputPath", outputPath));
    }

    public static string BuildSpeakScript(string message)
    {
        return Render("Speak",
            ("message", message));
    }

    public static string BuildScreenshotScript(string outputPath)
    {
        return Render("Screenshot",
            ("outputPath", outputPath));
    }

    public static string BuildCameraCaptureScript(string outputPath)
    {
        return Render("CameraCapture",
            ("outputPath", outputPath),
            ("cameraInitializationFailedExitCode", CameraInitializationFailedExitCode.ToString(CultureInfo.CurrentCulture)),
            ("cameraCaptureFailedExitCode", CameraCaptureFailedExitCode.ToString(CultureInfo.CurrentCulture)),
            ("cameraSaveFailedExitCode", CameraSaveFailedExitCode.ToString(CultureInfo.CurrentCulture)));
    }

    public static string BuildLaunchApplicationScript(AppLaunchInfo launchInfo)
    {
        return Render("LaunchApplication",
            ("filePath", launchInfo.FilePath),
            ("arguments", launchInfo.Arguments));
    }
}
