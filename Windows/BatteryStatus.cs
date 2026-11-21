using System.Runtime.InteropServices;

namespace WinSystemHelper;

internal static class BatteryStatus
{
    private const byte BatteryLifePercentUnknown = 255;

    private const byte BatteryFlagNoSystemBattery = 128;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    public static bool TryGetBatteryMetric(out int percent, out string status)
    {
        percent = 0;
        status = "Unknown";

        if (!GetSystemPowerStatus(out SystemPowerStatus powerStatus) ||
            powerStatus.BatteryLifePercent == BatteryLifePercentUnknown ||
            (powerStatus.BatteryFlag & BatteryFlagNoSystemBattery) == BatteryFlagNoSystemBattery)
        {
            return false;
        }

        percent = powerStatus.BatteryLifePercent;
        status = powerStatus.ACLineStatus switch
        {
            0 => "Discharging",
            1 => "Charging",
            _ => "Unknown"
        };

        return true;
    }

    public static string GetBatteryStatusText(ILogger<Worker> logger)
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus powerStatus))
        {
            logger.LogDebug(
                "Unable to read system power status. Win32 error code: {ErrorCode}.",
                Marshal.GetLastWin32Error());
            return "Unavailable";
        }

        if (powerStatus.BatteryLifePercent == BatteryLifePercentUnknown ||
            (powerStatus.BatteryFlag & BatteryFlagNoSystemBattery) == BatteryFlagNoSystemBattery)
        {
            return "No system battery detected";
        }

        string status = powerStatus.ACLineStatus switch
        {
            0 => "Discharging",
            1 => "Charging",
            _ => "Unknown"
        };

        return $"{powerStatus.BatteryLifePercent}% | {status}";
    }
}
