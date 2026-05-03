using Windows.System.Power;

namespace SpecIQ;

internal static class EnergyHelper
{
    /// <summary>
    /// Returns true when Windows Energy Saver (or Battery Saver on older builds) is active.
    /// Uses the WinRT PowerManager API, which correctly reflects the Windows 11 24H2
    /// Energy Saver that can run while plugged in.
    /// </summary>
    public static bool IsOn() =>
        PowerManager.EnergySaverStatus == EnergySaverStatus.On;
}
