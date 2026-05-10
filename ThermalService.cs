using System.Diagnostics;
using System.Management;

namespace SpecIQ;

/// <summary>
/// Reads CPU temperature via WMI and, as a fallback, the Windows
/// "Thermal Zone Information" performance counter (works on ARM64/Snapdragon
/// where the ACPI WMI namespace is typically blocked by the BIOS).
/// Always call off the UI thread — first-run init may take ~100 ms.
/// Returns -1 when no reading is available.
/// </summary>
internal static class ThermalService
{
    // Cached perf-counter handles; initialised once on first fallback read.
    private static List<PerformanceCounter>? _thermalCounters;
    private static bool _initDone;

    /// <summary>
    /// Returns the highest thermal-zone temperature in °C, or -1 if unavailable.
    /// Tries ACPI WMI first, then the Thermal Zone Information perf counter.
    /// </summary>
    public static int ReadCpuTempC()
    {
        var temp = ReadViaAcpiWmi();
        if (temp >= 0) return temp;

        return ReadViaPerfCounters();
    }

    // ── ACPI WMI (traditional, often blocked on ARM/Snapdragon) ───────────

    private static int ReadViaAcpiWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            int maxTemp = -1;
            foreach (ManagementObject obj in searcher.Get())
            {
                // Tenths of Kelvin → °C
                var celsius = (Convert.ToInt32(obj["CurrentTemperature"]) - 2732) / 10;
                if (celsius > maxTemp) maxTemp = celsius;
            }
            return maxTemp;
        }
        catch { return -1; }
    }

    // ── Thermal Zone Information perf counter (works on ARM64) ────────────

    private static int ReadViaPerfCounters()
    {
        if (!_initDone) InitCounters();
        if (_thermalCounters is not { Count: > 0 }) return -1;

        int maxTemp = -1;
        foreach (var c in _thermalCounters)
        {
            try
            {
                var raw = c.NextValue();

                // Windows reports thermal-zone temperature in Kelvin (whole number).
                // Sanity-check: 250 K (−23 °C) … 450 K (177 °C)
                if (raw < 250f || raw > 450f) continue;

                var celsius = (int)(raw - 273f);
                if (celsius > maxTemp) maxTemp = celsius;
            }
            catch { }
        }
        return maxTemp;
    }

    private static void InitCounters()
    {
        _initDone = true;
        try
        {
            const string category = "Thermal Zone Information";
            if (!PerformanceCounterCategory.Exists(category)) return;

            var cat = new PerformanceCounterCategory(category);
            var counters = cat.GetInstanceNames()
                .Select(inst => new PerformanceCounter(category, "Temperature", inst, readOnly: true))
                .ToList();

            // Prime so the first real read returns a valid value
            foreach (var c in counters) c.NextValue();

            _thermalCounters = counters;
        }
        catch { }
    }

    // ── Clock speed ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns (currentMHz, maxMHz) from Win32_Processor, or (-1, -1) if unavailable.
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
