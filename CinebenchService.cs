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

    // Cinebench writes ranking files to Documents\MAXON\Cinebench_*\cb_ranking\ (newer versions)
    // or to [exeDir]\cb_ranking\ (older versions). Snapshot both before running.
    private static readonly string CbDebugPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "cinebench-debug.txt");

    /// <summary>
    /// Returns the result file written or modified at or after <paramref name="notBefore"/>.
    /// Searches all file types (not just .txt) across all known MAXON data locations.
    /// Also writes a debug log so we can diagnose when nothing is found.
    /// </summary>
    private static string? FindRankingFileWrittenAfter(string exePath, DateTime notBefore)
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),         "MAXON"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),         "Cinebench"),  // some versions skip MAXON folder
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),     "MAXON"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MAXON"),
            Path.GetDirectoryName(exePath)!,
        };

        // Collect all files written after benchmark started across all roots
        var candidates = roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            .Where(f => File.GetLastWriteTime(f) >= notBefore)
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();

        // Debug: log everything we found (or didn't) to help diagnose
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CbDebugPath)!);
            var lines = new System.Text.StringBuilder();
            lines.AppendLine($"Cinebench result search — started at {notBefore:O}  ran at {DateTime.Now:O}");
            lines.AppendLine($"Exe: {exePath}");
            lines.AppendLine();
            foreach (var root in roots)
                lines.AppendLine(Directory.Exists(root) ? $"[found]  {root}" : $"[absent] {root}");
            lines.AppendLine();
            if (candidates.Count == 0)
                lines.AppendLine("No files written after benchmark started.");
            else
                foreach (var f in candidates)
                    lines.AppendLine($"{File.GetLastWriteTime(f):HH:mm:ss.fff}  {f}");
            File.WriteAllText(CbDebugPath, lines.ToString());
        }
        catch { }

        // Only use known ranking file formats — falling back to arbitrary files (html, pref, etc.)
        // causes misparse since those aren't KEY=VALUE score files.
        return candidates.FirstOrDefault(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<CinebenchResult> RunAsync(
        string exePath,
        IProgress<string> progress,
        CinebenchMode mode = CinebenchMode.Both,
        CancellationToken ct = default)
    {
        var startedAt = DateTime.Now;
        progress.Report("Starting…");

        var args = mode switch
        {
            CinebenchMode.Single => "g_CinebenchCpu1Test=true g_acceptDisclaimer=true",
            CinebenchMode.Multi  => "g_CinebenchCpuXTest=true g_acceptDisclaimer=true",
            _                    => "g_CinebenchCpu1Test=true g_CinebenchCpuXTest=true g_acceptDisclaimer=true",
        };

        // Redirect stdout/stderr so we can parse scores from console output (Cinebench 2026+
        // no longer writes ranking .txt files — scores appear in stdout instead).
        var psi = new ProcessStartInfo(exePath, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = false,   // let the Cinebench window appear normally
        };

        using var proc = Process.Start(psi)
            ?? throw new Exception("Failed to start Cinebench.");

        var outputLines = new System.Collections.Concurrent.ConcurrentBag<string>();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) outputLines.Add(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) outputLines.Add(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

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

        await Task.Delay(2000, CancellationToken.None);

        // Append stdout/stderr to debug file alongside file-search results
        var allOutput = string.Join("\n", outputLines);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CbDebugPath)!);
            File.AppendAllText(CbDebugPath, $"\n--- stdout/stderr ---\n{allOutput}\n");
        }
        catch { }

        // 1. Try parsing scores from stdout (Cinebench 2026+)
        var fromStdout = ParseStdout(allOutput);
        if (fromStdout.SingleCore > 0 || fromStdout.MultiCore > 0) return fromStdout;

        // 2. Fall back to ranking .txt file (Cinebench R23 / 2024)
        var resultFile = FindRankingFileWrittenAfter(exePath, startedAt);
        if (resultFile != null) return ParseResultFile(resultFile);

        throw new Exception(
            "Could not read Cinebench results.\n" +
            "Check %AppData%\\SpecIQ\\cinebench-debug.txt for details.");
    }

    /// <summary>
    /// Parses Cinebench stdout output for score values.
    /// Matches explicit KEY=VALUE pairs first (R23/2024), then CB2026 labelled lines.
    /// Deliberately narrow to avoid false matches on CPU core counts etc.
    /// </summary>
    private static CinebenchResult ParseStdout(string output)
    {
        double single = 0, multi = 0;

        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();

            // KEY=VALUE format used by R23 and 2024 (may appear in stdout too)
            if (TryParseKeyValue(t, "CBCPU1",  out var v)) { single = v; continue; }
            if (TryParseKeyValue(t, "CBCPUX",  out v))     { multi  = v; continue; }
            if (TryParseKeyValue(t, "CB26CPU1", out v))     { single = v; continue; }
            if (TryParseKeyValue(t, "CB26CPUX", out v))     { multi  = v; continue; }

            // Labelled score lines: must contain "score" or "pts" near the number
            // to avoid matching thread counts, durations, etc.
            // e.g. "Single Core Score: 1234" / "CB Single 1234 pts"
            var m = System.Text.RegularExpressions.Regex.Match(t,
                @"(?:single|1t|cpu1).{0,40}?(?:score|pts)\D{0,10}?(\d{3,}(?:[.,]\d+)?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) { single = ParseNum(m.Groups[1].Value); continue; }

            m = System.Text.RegularExpressions.Regex.Match(t,
                @"(?:multi|nt|cpux).{0,40}?(?:score|pts)\D{0,10}?(\d{3,}(?:[.,]\d+)?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) { multi = ParseNum(m.Groups[1].Value); continue; }

            // Also try reversed order: "Score ... Single ... 1234"
            m = System.Text.RegularExpressions.Regex.Match(t,
                @"score.{0,40}?(?:single|1t).{0,20}?(\d{3,}(?:[.,]\d+)?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) { single = ParseNum(m.Groups[1].Value); continue; }

            m = System.Text.RegularExpressions.Regex.Match(t,
                @"score.{0,40}?(?:multi|nt).{0,20}?(\d{3,}(?:[.,]\d+)?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) { multi = ParseNum(m.Groups[1].Value); continue; }
        }

        return new CinebenchResult(single, multi);
    }

    private static bool TryParseKeyValue(string line, string key, out double value)
    {
        value = 0;
        var idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var rest = line[(idx + key.Length)..].TrimStart('=', ':', ' ', '\t');
        var num  = System.Text.RegularExpressions.Regex.Match(rest, @"(\d+(?:[.,]\d+)?)");
        return num.Success && (value = ParseNum(num.Groups[1].Value)) > 0;
    }

    private static double ParseNum(string s) =>
        double.TryParse(s.Replace(',', '.'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static CinebenchResult ParseResultFile(string path)
    {
        var entries = File.ReadAllLines(path)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

        return new CinebenchResult(
            SingleCore: ParseNum(entries.GetValueOrDefault("CBCPU1", "0")),
            MultiCore:  ParseNum(entries.GetValueOrDefault("CBCPUX", "0")));
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
