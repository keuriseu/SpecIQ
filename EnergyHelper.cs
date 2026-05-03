using System.Runtime.InteropServices;

namespace SpecIQ;

internal static class EnergyHelper
{
    // SystemStatusFlag bit 0 = Energy Saver / Battery Saver (Windows 10 1709+)
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static bool IsOn() =>
        GetSystemPowerStatus(out var s) && (s.SystemStatusFlag & 1) != 0;
}
