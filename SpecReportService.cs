using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace SpecIQ;

internal class SpecReport
{
    public string   MachineName    { get; set; } = Environment.MachineName;
    public DateTime GeneratedAt    { get; set; } = DateTime.Now;

    // Hardware
    public string CpuName       { get; set; } = "";
    public int    CpuCores      { get; set; }
    public int    CpuThreads    { get; set; }
    public int    CpuMaxMHz     { get; set; }
    public long   RamBytes      { get; set; }
    public string GpuName       { get; set; } = "";
    public string OsCaption     { get; set; } = "";
    public string OsBuild       { get; set; } = "";
    public string OsArch        { get; set; } = "";
    public int    BattFullMwh   { get; set; } = -1;
    public int    BattDesignMwh { get; set; } = -1;

    // Benchmark scores  (0 = not available)
    public int    Gb6Single  { get; set; }
    public int    Gb6Multi   { get; set; }
    public double CbSingle   { get; set; }
    public double CbMulti    { get; set; }
    public int    AiFp32     { get; set; }
    public int    AiFp16     { get; set; }
    public int    AiQuant    { get; set; }
    public string AiBackend  { get; set; } = "";
    public double SpeedAvg   { get; set; }
    public int    SpeedRuns  { get; set; }
}

internal static class SpecReportService
{
    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Gathers all hardware info and best benchmark scores.
    /// WMI queries run off the UI thread; call from any context.
    /// </summary>
    public static async Task<SpecReport> GatherAsync()
    {
        var r = new SpecReport();

        // WMI is slow — run all queries in parallel on the thread pool
        await Task.WhenAll(
            Task.Run(() => GatherCpu(r)),
            Task.Run(() => GatherRam(r)),
            Task.Run(() => GatherGpu(r)),
            Task.Run(() => GatherOs(r)),
            Task.Run(() => GatherBattery(r)));

        GatherScores(r); // all JSON reads — fast, stays on calling thread
        return r;
    }

    // ── Hardware collectors ───────────────────────────────────────────────

    private static void GatherCpu(SpecReport r)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
            {
                r.CpuName    = (o["Name"]?.ToString() ?? "").Trim();
                r.CpuCores   = Convert.ToInt32(o["NumberOfCores"]);
                r.CpuThreads = Convert.ToInt32(o["NumberOfLogicalProcessors"]);
                r.CpuMaxMHz  = Convert.ToInt32(o["MaxClockSpeed"]);
                break;
            }
        }
        catch { }
    }

    private static void GatherRam(SpecReport r)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject o in s.Get())
            {
                r.RamBytes = Convert.ToInt64(o["TotalPhysicalMemory"]);
                break;
            }
        }
        catch { }
    }

    private static void GatherGpu(SpecReport r)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
            {
                var name = o["Name"]?.ToString() ?? "";
                // Skip the generic Microsoft fallback driver
                if (name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
                r.GpuName = name.Trim();
                break;
            }
        }
        catch { }
    }

    private static void GatherOs(SpecReport r)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Caption, BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementObject o in s.Get())
            {
                r.OsCaption = o["Caption"]?.ToString()?.Trim() ?? "";
                r.OsBuild   = o["BuildNumber"]?.ToString() ?? "";
                break;
            }
        }
        catch { }

        r.OsArch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "ARM64",
            Architecture.X64   => "x64",
            Architecture.X86   => "x86",
            _                  => RuntimeInformation.OSArchitecture.ToString(),
        };
    }

    private static void GatherBattery(SpecReport r)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                @"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData");
            foreach (ManagementObject o in s.Get())
            {
                r.BattDesignMwh = Convert.ToInt32(o["DesignedCapacity"]);
                break;
            }
        }
        catch { }

        try
        {
            using var s = new ManagementObjectSearcher(
                @"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            foreach (ManagementObject o in s.Get())
            {
                r.BattFullMwh = Convert.ToInt32(o["FullChargedCapacity"]);
                break;
            }
        }
        catch { }
    }

    // ── Score collectors ──────────────────────────────────────────────────

    private static void GatherScores(SpecReport r)
    {
        // Geekbench 6 — pick best CPU single-core run from history
        var history = BenchmarkHistory.Load();
        var gb6Best = history
            .Where(e => e.Tool == HistoryTool.Geekbench6
                     && e.ScoreA > 0
                     && !e.Note.Contains("GPU", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.ScoreA)
            .FirstOrDefault();
        if (gb6Best != null)
        {
            r.Gb6Single = (int)gb6Best.ScoreA;
            r.Gb6Multi  = (int)gb6Best.ScoreB;
        }

        // Cinebench — most recent saved result
        var cb = CinebenchSavedResult.Load();
        if (cb != null) { r.CbSingle = cb.SingleCore; r.CbMulti = cb.MultiCore; }

        // Geekbench AI — most recent saved result
        var ai = AIBenchmarkSavedResult.Load();
        if (ai != null)
        {
            r.AiFp32    = ai.FullPrecision;
            r.AiFp16    = ai.HalfPrecision;
            r.AiQuant   = ai.Quantized;
            r.AiBackend = ai.Backend;
        }

        // Speedometer — average score across all recorded iterations
        var speed = SpeedometerResult.Load();
        if (speed?.Entries.Count > 0)
        {
            r.SpeedAvg  = speed.Entries.Average(e => e.Score);
            r.SpeedRuns = speed.Entries.Count;
        }
    }

    // ── Formatter ─────────────────────────────────────────────────────────

    public static string FormatReport(SpecReport r)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"System Snapshot  ·  {r.MachineName}");
        sb.AppendLine($"Generated {r.GeneratedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        // CPU
        if (!string.IsNullOrEmpty(r.CpuName))
        {
            sb.AppendLine($"CPU      {r.CpuName}");
            if (r.CpuCores > 0)
            {
                var threads = r.CpuThreads > r.CpuCores ? $"  ·  {r.CpuThreads} threads" : "";
                var ghz     = r.CpuMaxMHz > 0 ? $"  ·  {r.CpuMaxMHz / 1000.0:F2} GHz" : "";
                sb.AppendLine($"         {r.CpuCores} cores{threads}{ghz}");
            }
        }

        // RAM
        if (r.RamBytes > 0)
            sb.AppendLine($"RAM      {r.RamBytes / (1024.0 * 1024 * 1024):F0} GB");

        // GPU
        if (!string.IsNullOrEmpty(r.GpuName))
            sb.AppendLine($"GPU      {r.GpuName}");

        // OS
        if (!string.IsNullOrEmpty(r.OsCaption))
        {
            var build = r.OsBuild.Length > 0 ? $"  ·  Build {r.OsBuild}" : "";
            sb.AppendLine($"OS       {r.OsCaption}{build}  ·  {r.OsArch}");
        }

        // Battery
        if (r.BattFullMwh > 0)
        {
            var batt = $"{r.BattFullMwh / 1000.0:F1} Wh";
            if (r.BattDesignMwh > 0)
            {
                var health = Math.Clamp((int)Math.Round(r.BattFullMwh * 100.0 / r.BattDesignMwh), 0, 100);
                batt += $"  /  {r.BattDesignMwh / 1000.0:F1} Wh design  ·  {health}% health";
            }
            sb.AppendLine($"Battery  {batt}");
        }

        // Benchmark scores
        var hasScores = r.Gb6Single > 0 || r.CbSingle > 0 || r.AiFp32 > 0 || r.SpeedAvg > 0;
        if (hasScores)
        {
            sb.AppendLine();
            sb.AppendLine("Benchmark Scores");

            if (r.Gb6Single > 0 || r.Gb6Multi > 0)
                sb.AppendLine($"  Geekbench 6 CPU  SC {r.Gb6Single:N0}  ·  MC {r.Gb6Multi:N0}");

            if (r.CbSingle > 0 || r.CbMulti > 0)
                sb.AppendLine($"  Cinebench        1T {(int)r.CbSingle:N0}  ·  nT {(int)r.CbMulti:N0}");

            if (r.AiFp32 > 0)
            {
                var backend = string.IsNullOrEmpty(r.AiBackend) ? "" : $"  ·  {r.AiBackend}";
                sb.AppendLine($"  Geekbench AI{backend}");
                sb.AppendLine($"    FP32 {r.AiFp32:N0}  ·  FP16 {r.AiFp16:N0}  ·  Quant {r.AiQuant:N0}");
            }

            if (r.SpeedAvg > 0)
            {
                var runs = r.SpeedRuns > 1 ? $"  ({r.SpeedRuns} iterations avg)" : "";
                sb.AppendLine($"  Speedometer 3.1  {r.SpeedAvg:F2}{runs}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
