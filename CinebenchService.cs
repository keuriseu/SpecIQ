using System.Diagnostics;
using System.IO;

namespace SpecIQ;

public enum CinebenchMode { Single, Multi, Both }

public record CinebenchResult(double SingleCore, double MultiCore);

public static class CinebenchService
{
    private static readonly string[] SearchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Cinebench"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Maxon Cinebench"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Cinebench"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Maxon Cinebench"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cinebench"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maxon Cinebench"),
    ];

    public static string? FindInstalled()
    {
        var fromDir = SearchPaths
            .Select(d => Path.Combine(d, "Cinebench.exe"))
            .FirstOrDefault(File.Exists);
        if (fromDir != null) return fromDir;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo("where", "Cinebench.exe")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true,
            });
            var line = proc?.StandardOutput.ReadLine()?.Trim();
            if (line != null && File.Exists(line) && AppHelpers.IsAllowedExePath(line)) return line;
        }
        catch { }

        return null;
    }

    public static string? GetInstalledVersion(string exePath)
    {
        try { return FileVersionInfo.GetVersionInfo(exePath).ProductVersion?.Trim(); }
        catch { return null; }
    }

    public static bool IsAlreadyRunning(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        return Process.GetProcessesByName(name).Length > 0;
    }

    public static async Task<CinebenchResult> RunAsync(
        string exePath,
        IProgress<string> progress,
        CinebenchMode mode = CinebenchMode.Both,
        CancellationToken ct = default)
    {
        var rankingDir    = Path.Combine(Path.GetDirectoryName(exePath)!, "cb_ranking");
        var existingFiles = Directory.Exists(rankingDir)
            ? Directory.GetFiles(rankingDir, "*.txt").ToHashSet()
            : [];

        progress.Report("Starting…");

        var args = mode switch
        {
            CinebenchMode.Single => "g_CinebenchCpu1Test=true g_acceptDisclaimer=true",
            CinebenchMode.Multi  => "g_CinebenchCpuXTest=true g_acceptDisclaimer=true",
            _                    => "g_CinebenchCpu1Test=true g_CinebenchCpuXTest=true g_acceptDisclaimer=true",
        };

        var psi = new ProcessStartInfo(exePath, args)
        {
            UseShellExecute = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new Exception("Failed to start Cinebench.");

        var sw = Stopwatch.StartNew();
        while (!proc.HasExited && !ct.IsCancellationRequested)
        {
            progress.Report($"Running…  {sw.Elapsed:m\\:ss}");
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { break; }
        }

        if (ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new OperationCanceledException();
        }

        // Brief wait for file flush
        await Task.Delay(500, CancellationToken.None);

        if (!Directory.Exists(rankingDir))
            throw new Exception("No results found. Did the benchmark complete?");

        var newFile = Directory.GetFiles(rankingDir, "*.txt")
            .Where(f => !existingFiles.Contains(f))
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault()
            ?? throw new Exception("No result file written. The benchmark may have been cancelled.");

        return ParseResultFile(newFile);
    }

    private static CinebenchResult ParseResultFile(string path)
    {
        var entries = File.ReadAllLines(path)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

        double Get(string key) =>
            entries.TryGetValue(key, out var v) && double.TryParse(v,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

        return new CinebenchResult(
            SingleCore: Get("CBCPU1"),
            MultiCore:  Get("CBCPUX"));
    }
}

public class CinebenchSavedResult
{
    public double  SingleCore  { get; set; }
    public double  MultiCore   { get; set; }
    public string  MachineName { get; set; } = Environment.MachineName;
    public string  SavedAt     { get; set; } = DateTime.Now.ToString("o");

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "cinebench.json");

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(this, AppHelpers.JsonOpts));
        }
        catch { }
    }

    public static CinebenchSavedResult? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? System.Text.Json.JsonSerializer.Deserialize<CinebenchSavedResult>(
                    File.ReadAllText(FilePath), AppHelpers.JsonOpts)
                : null;
        }
        catch { return null; }
    }
}
