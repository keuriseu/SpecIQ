using System.Management;

namespace SpecIQ;

/// <summary>
/// Reads CPU temperature via WMI. Always call off the UI thread — each query
/// takes 50–200 ms. Returns -1 when the reading is unavailable (BIOS
/// restriction, unsupported hardware, or WMI namespace absent).
/// </summary>
internal static class ThermalService
{
    /// <summary>
    /// Returns the highest CPU/chassis temperature in °C reported by
    /// MSAcpi_ThermalZoneTemperature, or -1 if the namespace is inaccessible.
    /// </summary>
    public static int ReadCpuTempC()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            int maxTemp = -1;
            foreach (ManagementObject obj in searcher.Get())
            {
                // WMI reports in tenths of Kelvin: divide by 10 then subtract 273.2
                var celsius = (Convert.ToInt32(obj["CurrentTemperature"]) - 2732) / 10;
                if (celsius > maxTemp) maxTemp = celsius;
            }
            return maxTemp;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Returns (currentMHz, maxMHz) from Win32_Processor, or (-1, -1) if unavailable.
    /// When current &lt; 80 % of max the CPU is likely thermally throttled.
    /// </summary>
    public static (int Current, int Max) ReadClockSpeedMHz()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
                return (Convert.ToInt32(obj["CurrentClockSpeed"]),
                        Convert.ToInt32(obj["MaxClockSpeed"]));
        }
        catch { }
        return (-1, -1);
    }
}
