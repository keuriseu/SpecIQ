using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpecIQ;

public static partial class PugetBenchService
{
    // ── File paths ─────────────────────────────────────────────────────────────

    private static readonly string LauncherExe =
        @"C:\Program Files\PugetBench for Creators\PugetBench for Creators.exe";

    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "com.puget.benchmark", "logs", "_puget.log");

    private static readonly string CsvDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "com.puget.benchmark", "csv");

    private static readonly string AssetsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "com.puget.benchmark", "assets", "photoshop-assets-1.0.0");

    private static readonly string BenchmarksDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "com.puget.benchmark", "benchmarks");

    // ── Test catalogue (for live name lookup by index) ─────────────────────────
    //
    // Indices match the 1-based [N/21] counter in the log.

    private static readonly string[] TestNames =
    [
        "File Open - RAW",
        "Resize to 150MP - Preserve Details",
        "Resize to 150MP - Bicubic Smooth",
        "Rotate",
        "Select Subject",
        "Select and Mask",
        "Convert to Smart Object",
        "Paint Bucket",
        "Smudge Tool",
        "Adaptive Wide Angle",
        "Camera Raw",
        "Lens Correction",
        "Content Aware Fill",
        "Reduce Noise",
        "Smart Sharpen",
        "Iris Blur",
        "Field Blur",
        "File Save - JPG",
        "File Save - PNG",
        "File Save - PSD",
        "File Open - PSD",
    ];

    // ── Detection ──────────────────────────────────────────────────────────────

    public static string? FindInstalled() =>
        File.Exists(LauncherExe) ? LauncherExe : null;

    public static string? FindPhotoshop()
    {
        try
        {
            return Directory.EnumerateFiles(
                    @"C:\Program Files\Adobe", "Photoshop.exe",
                    SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    public static bool AreAssetsReady() => Directory.Exists(AssetsDir);

    public static string? GetBenchmarkVersion()
    {
        try
        {
            var f = Directory.EnumerateFiles(BenchmarksDir, "photoshop-benchmark-*.json")
                             .OrderDescending()
                             .FirstOrDefault();
            if (f == null) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    // ── Run ────────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    public static async Task<PugetBenchResult> RunAsync(
        string            launcherExe,
        IProgress<string> progress,
        CancellationToken ct)
    {
        // Snapshot existing CSVs so we detect the one produced by this run.
        var existingCsvs = Directory.Exists(CsvDir)
            ? new HashSet<string>(
                Directory.GetFiles(CsvDir, "*photoshop*.csv"),
                StringComparer.OrdinalIgnoreCase)
            : [];

        // Snap log byte-offset so we only see lines from this run.
        long logOffset = File.Exists(LogFile) ? new FileInfo(LogFile).Length : 0;

        // Launch or bring to front.
        var existing = Process.GetProcessesByName("PugetBench for Creators").FirstOrDefault();
        if (existing != null)
        {
            if (existing.MainWindowHandle != nint.Zero)
                SetForegroundWindow(existing.MainWindowHandle);
            progress.Report("PugetBench for Creators is already running — brought to front.");
        }
        else
        {
            Process.Start(new ProcessStartInfo(launcherExe) { UseShellExecute = true });
            progress.Report("PugetBench for Creators launched.");
        }

        progress.Report("Waiting — select Photoshop and click Run Benchmark in PugetBench.");

        var deadline  = DateTime.Now.AddMinutes(35);
        var testEndRx = TestEndRegex();
        var loopRx    = LoopStartRegex();

        while (!ct.IsCancellationRequested && DateTime.Now < deadline)
        {
            await Task.Delay(500, ct);

            // ── Check for the new results CSV (written after all 3 loops) ──────
            if (Directory.Exists(CsvDir))
            {
                var newCsv = Directory.GetFiles(CsvDir, "*photoshop*.csv")
                    .FirstOrDefault(f => !existingCsvs.Contains(f));
                if (newCsv != null)
                {
                    progress.Report($"Results file ready: {Path.GetFileName(newCsv)}");
                    return ParseCsv(newCsv);
                }
            }

            // ── Parse new log lines for live test-progress display ─────────────
            if (!File.Exists(LogFile)) continue;

            List<string> newLines;
            try
            {
                using var fs = new FileStream(LogFile, FileMode.Open,
                                              FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(logOffset, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, System.Text.Encoding.UTF8,
                                                   detectEncodingFromByteOrderMarks: false,
                                                   leaveOpen: true);
                newLines  = [];
                while (!reader.EndOfStream)
                    newLines.Add(reader.ReadLine()!);
                logOffset = fs.Position;
            }
            catch { continue; }

            foreach (var line in newLines)
            {
                // "Loop [2/3] start"
                var lm = loopRx.Match(line);
                if (lm.Success)
                {
                    progress.Report($"Loop {lm.Groups[1].Value}/3 started...");
                    continue;
                }

                // "Test [7/21] end: 1.23 seconds"
                var tm = testEndRx.Match(line);
                if (!tm.Success) continue;

                int    idx  = int.Parse(tm.Groups[1].Value);
                double secs = double.Parse(tm.Groups[2].Value, CultureInfo.InvariantCulture);
                string name = idx >= 1 && idx <= TestNames.Length ? TestNames[idx - 1] : $"Test {idx}";

                progress.Report($"[{idx,2}/21]  {name,-42}  {secs,5:F2} s");
            }
        }

        ct.ThrowIfCancellationRequested();

        throw new Exception(
            "No results CSV was produced within 35 minutes. " +
            "Ensure you selected Photoshop and clicked Run Benchmark in PugetBench.");
    }

    // ── CSV parsing ────────────────────────────────────────────────────────────

    private static PugetBenchResult ParseCsv(string path)
    {
        var result = new PugetBenchResult();

        bool inResults = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();

            if (line.StartsWith("Benchmark Results:")) { inResults = true; continue; }
            if (!inResults || string.IsNullOrWhiteSpace(line)) continue;

            // Skip header row.
            if (line.StartsWith("Test,")) continue;

            var parts = line.Split(',');
            if (parts.Length < 4) continue;

            var name    = parts[0].Trim();
            var setting = parts[1].Trim();   // "General" | "Filter" | ""
            var units   = parts[2].Trim();   // "seconds" | ""
            var valStr  = parts[3].Trim();

            if (!double.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                continue;

            if (name.StartsWith("Overall Score")) { result.CompositeScore = val; continue; }
            if (name.StartsWith("General Score")) { result.GeneralScore   = val; continue; }
            if (name.StartsWith("Filter Score"))  { result.FilterScore    = val; continue; }

            // Individual test row.
            if (units != "seconds" || string.IsNullOrEmpty(setting)) continue;

            result.Tests.Add(new PugetBenchTestResult(name, setting, val));
        }

        return result;
    }

    // ── Regexes ────────────────────────────────────────────────────────────────

    // Matches: [2026-05-19_09:41:34] [info] Test [4/21] end: 1.44 seconds
    [GeneratedRegex(@"\[info\] Test \[(\d+)/21\] end: ([\d.]+) seconds", RegexOptions.Compiled)]
    private static partial Regex TestEndRegex();

    // Matches: [2026-05-19_09:41:34] [info] Loop [2/3] start
    [GeneratedRegex(@"\[info\] Loop \[(\d+)/3\] start", RegexOptions.Compiled)]
    private static partial Regex LoopStartRegex();
}
