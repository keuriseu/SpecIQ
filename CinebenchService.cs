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
        // Saved setting takes priority — callers check this before calling FindInstalled,
        // but guard here too so the service is self-contained.
        if (SpecIQSettings.CinebenchPath is { Length: > 0 } saved && File.Exists(saved))
            return saved;

        var fromDir = SearchPaths
            .Select(d => Path.Combine(d, "Cinebench.exe"))
            .FirstOrDefault(File.Exists);
        if (fromDir != null) { SpecIQSettings.CinebenchPath = fromDir; return fromDir; }

        // Recursive search under C:\Data\ — user's benchmark/installer staging folder
        if (Directory.Exists(@"C:\Data"))
        {
            try
            {
                var found = Directory.EnumerateFiles(@"C:\Data", "Cinebench.exe",
                                SearchOption.AllDirectories)
                            .FirstOrDefault();
                if (found != null) { SpecIQSettings.CinebenchPath = found; return found; }
            }
            catch { }
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo("where", "Cinebench.exe")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true,
            });
            var line = proc?.StandardOutput.ReadLine()?.Trim();
            if (line != null && File.Exists(line) && AppHelpers.IsAllowedExePath(line))
            {
                SpecIQSettings.CinebenchPath = line;
                return line;
            }
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

            // Hex-dump binary .pref/.prf files; text-dump small XML/txt files
            foreach (var f in candidates)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                try
                {
                    var info = new System.IO.FileInfo(f);
                    if (ext is ".pref" or ".prf")
                    {
                        if (info.Length > 4096) continue;
                        var bytes = File.ReadAllBytes(f);
                        lines.AppendLine();
                        lines.AppendLine($"=== HEX {f} ({bytes.Length} bytes) ===");
                        for (int i = 0; i < bytes.Length; i += 16)
                        {
                            var chunk = bytes.Skip(i).Take(16).ToArray();
                            var hex   = string.Join(" ", chunk.Select(b => b.ToString("X2")));
                            var ascii = new string(chunk.Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
                            lines.AppendLine($"{i:X4}  {hex,-47}  {ascii}");
                        }
                    }
                    else if (ext is ".txt" or ".xml" or ".json" or ".ini" or ".cfg")
                    {
                        if (info.Length > 200_000) continue;
                        lines.AppendLine();
                        lines.AppendLine($"=== {f} ===");
                        lines.AppendLine(File.ReadAllText(f));
                    }
                }
                catch { }
            }

            File.WriteAllText(CbDebugPath, lines.ToString());
        }
        catch { }

        // Priority: .txt (R23/R24), then .pref (CB2026 rm.pref Ranking Manager), then .xml/.prf
        return candidates.FirstOrDefault(f => f.EndsWith(".txt",  StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(f => f.EndsWith(".pref", StringComparison.OrdinalIgnoreCase)
                                              && Path.GetFileName(f).Equals("rm.pref", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(f => f.EndsWith(".pref", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(f => f.EndsWith(".xml",  StringComparison.OrdinalIgnoreCase)
                                              && !f.Contains("Redshift", StringComparison.OrdinalIgnoreCase));

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

        var outputLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) outputLines.Enqueue(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) outputLines.Enqueue(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        var uiScore   = new CinebenchResult(0, 0);
        var uiTextLog = new System.Text.StringBuilder();
        while (!proc.HasExited && !ct.IsCancellationRequested)
        {
            progress.Report($"Running…  {sw.Elapsed:m\\:ss}");

            // Try to read score from Cinebench window via UIAutomation.
            // CB2026 shows the result for ~2-6 s before auto-closing.
            // Poll every second so we catch that brief window while the process is alive.
            if (uiScore.SingleCore == 0 && uiScore.MultiCore == 0)
            {
                var (score, text) = await Task.Run(() => TryReadWindowScore(proc), ct).ConfigureAwait(false);
                // Only log UIAutomation text when it changes (avoids thousands of identical lines)
                var trimmed = text.Trim();
                var last    = uiTextLog.Length > 0 ? uiTextLog.ToString().Split('\n')[^2] : "";
                if (!string.IsNullOrWhiteSpace(trimmed) && !last.EndsWith(trimmed))
                    uiTextLog.AppendLine($"[{DateTime.Now:HH:mm:ss}] {trimmed}");
                if (score.SingleCore > 0 || score.MultiCore > 0)
                    uiScore = score;
            }

            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
        }

        if (ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new OperationCanceledException();
        }

        await Task.Delay(1000, CancellationToken.None);

        var allOutput = string.Join("\n", outputLines);

        // 1. Score read from CB window via UIAutomation while it was still open
        if (uiScore.SingleCore > 0 || uiScore.MultiCore > 0)
        {
            LogDebugSuffix(allOutput, uiTextLog.ToString());
            return uiScore;
        }

        // 2. Stdout — CB2026 writes "CB 487.67 (0.00)" here; older versions use KEY=VALUE
        var fromStdout = ParseStdout(allOutput, mode);
        if (fromStdout.SingleCore > 0 || fromStdout.MultiCore > 0)
        {
            LogDebugSuffix(allOutput, uiTextLog.ToString());
            return fromStdout;
        }

        // 3. File written by Cinebench (R23/2024 .txt, or CB2026 binary rm.pref)
        var resultFile = FindRankingFileWrittenAfter(exePath, startedAt);
        // ↑ FindRankingFileWrittenAfter writes the main debug file; append extras after
        LogDebugSuffix(allOutput, uiTextLog.ToString());

        if (resultFile != null)
        {
            var parsed = ParseResultFile(resultFile);
            if (parsed.SingleCore > 0 || parsed.MultiCore > 0) return parsed;

            // Try binary heuristic for rm.pref (CB2026 stores score in proprietary format)
            if (resultFile.EndsWith("rm.pref", StringComparison.OrdinalIgnoreCase))
            {
                var binary = ParseRmPrefBinary(File.ReadAllBytes(resultFile), mode);
                if (binary.SingleCore > 0 || binary.MultiCore > 0) return binary;
            }
        }

        throw new Exception(
            "Could not read Cinebench results.\n" +
            "Check %AppData%\\SpecIQ\\cinebench-debug.txt for details.");
    }

    private static void LogDebugSuffix(string stdout, string uiText)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CbDebugPath)!);
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(stdout))
                sb.AppendLine($"\n--- stdout/stderr ---\n{stdout}");
            if (!string.IsNullOrWhiteSpace(uiText))
                sb.AppendLine($"\n--- UIAutomation window text ---\n{uiText}");
            else
                sb.AppendLine("\n--- UIAutomation: no window text captured ---");
            File.AppendAllText(CbDebugPath, sb.ToString());
        }
        catch { }
    }

    /// <summary>
    /// Reads the Cinebench score directly from its window via UI Automation.
    /// CB2026 shows the result for ~2-6 s before auto-closing — we call this every
    /// second in the wait loop so we catch the result window while the process is alive.
    /// Returns (score, windowText) — the caller logs windowText after the main debug file is written.
    /// </summary>
    private static (CinebenchResult Score, string Text) TryReadWindowScore(Process proc)
    {
        try
        {
            if (proc.HasExited) return (new CinebenchResult(0, 0), "");
            proc.Refresh();
            var hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return (new CinebenchResult(0, 0), "");

            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            if (root == null) return (new CinebenchResult(0, 0), "");

            var sb = new System.Text.StringBuilder();
            WalkAutomationTree(root, sb, depth: 0, maxDepth: 12);
            var text = sb.ToString();

            return (ParseStdout(text), text);
        }
        catch { return (new CinebenchResult(0, 0), ""); }
    }

    /// <summary>
    /// Heuristic parser for CB2026's proprietary rm.pref binary format.
    /// Observed structure: 8-byte "QC4Drm01" header + 20 bytes of packed data.
    /// From analysis: the score bytes appear at known offsets — byte[11] for single-core (uint8)
    /// and bytes[9-10] big-endian for multi-core.  Validated only when values fall in expected ranges.
    /// </summary>
    private static CinebenchResult ParseRmPrefBinary(byte[] bytes, CinebenchMode mode)
    {
        if (bytes.Length < 16) return new CinebenchResult(0, 0);

        double single = 0, multi = 0;

        // Scan every byte and every big-endian uint16 in the data section for plausible scores.
        // Single-core CB2026: typically 50–300 pts. Multi-core: 300–30,000 pts.
        // Accept the first match found; structural bytes (0x00, 0x0F, 0x01) are filtered by range.
        for (int i = 8; i < bytes.Length; i++)
        {
            var b = (double)bytes[i];
            if (mode != CinebenchMode.Multi && single == 0 && b is >= 50 and <= 300)
                single = b;
            if (mode != CinebenchMode.Single && multi == 0 && b is >= 301 and <= 30000)
                multi = b;
        }

        // Also try big-endian uint16 pairs for multi-core (may exceed byte range)
        if (mode != CinebenchMode.Single && multi == 0)
        {
            for (int i = 8; i < bytes.Length - 1; i++)
            {
                var be = (bytes[i] << 8) | bytes[i + 1];
                if (be is >= 301 and <= 30000) { multi = be; break; }
            }
        }

        return new CinebenchResult(single, multi);
    }

    private static void WalkAutomationTree(
        System.Windows.Automation.AutomationElement el,
        System.Text.StringBuilder sb, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        try
        {
            var name = el.Current.Name;
            if (!string.IsNullOrWhiteSpace(name)) sb.AppendLine(name);
        }
        catch { }

        try
        {
            var child = System.Windows.Automation.TreeWalker.ContentViewWalker.GetFirstChild(el);
            while (child != null)
            {
                WalkAutomationTree(child, sb, depth + 1, maxDepth);
                child = System.Windows.Automation.TreeWalker.ContentViewWalker.GetNextSibling(child);
            }
        }
        catch { }
    }

    /// <summary>
    /// Parses Cinebench stdout output for score values.
    /// Priority order:
    ///   1. CB2026: "CB 487.67 (0.00)" lines in stdout
    ///   2. R23/2024: KEY=VALUE pairs (CBCPU1=, CBCPUX=, etc.)
    ///   3. Labelled lines containing "score" or "pts" near single/multi keywords
    /// </summary>
    private static CinebenchResult ParseStdout(string output, CinebenchMode mode = CinebenchMode.Both)
    {
        double single = 0, multi = 0;

        // ── CB2026: "CB 487.67 (0.00)" ──────────────────────────────────────
        // CB2026 writes one such line per test (single-core OR multi-core).
        // When running Both, two lines appear; single-core scores are lower than multi-core.
        var cbMatches = System.Text.RegularExpressions.Regex.Matches(output,
            @"(?m)^CB\s+(\d+(?:[.,]\d+)?)\s*\(",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (cbMatches.Count > 0)
        {
            var scores = cbMatches.Cast<System.Text.RegularExpressions.Match>()
                .Select(m => ParseNum(m.Groups[1].Value))
                .Where(v => v > 0)
                .OrderBy(v => v)
                .ToList();

            if (scores.Count == 1)
            {
                if (mode == CinebenchMode.Multi) multi  = scores[0];
                else                             single = scores[0];
            }
            else if (scores.Count >= 2)
            {
                // Single-core runs first and scores lower; multi-core scores higher
                single = scores[0];
                multi  = scores[1];
            }
            return new CinebenchResult(single, multi);
        }

        // ── R23 / 2024 KEY=VALUE and labelled lines ──────────────────────────
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();

            if (TryParseKeyValue(t, "CBCPU1",   out var v)) { single = v; continue; }
            if (TryParseKeyValue(t, "CBCPUX",   out v))     { multi  = v; continue; }
            if (TryParseKeyValue(t, "CB26CPU1",  out v))     { single = v; continue; }
            if (TryParseKeyValue(t, "CB26CPUX",  out v))     { multi  = v; continue; }

            var m = System.Text.RegularExpressions.Regex.Match(t,
                @"(?:single|1t|cpu1).{0,40}?(?:score|pts)\D{0,10}?(\d{3,}(?:[.,]\d+)?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) { single = ParseNum(m.Groups[1].Value); continue; }

            m = System.Text.RegularExpressions.Regex.Match(t,
                @"(?:multi|nt|cpux).{0,40}?(?:score|pts)\D{0,10}?(\d{3,}(?:[.,]\d+)?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) { multi = ParseNum(m.Groups[1].Value); continue; }

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
        var text = File.ReadAllText(path);

        // Try XML format first (CB2026 rm.pref — Cinema 4D preference XML)
        if (text.TrimStart().StartsWith("<", StringComparison.Ordinal))
        {
            var fromXml = ParsePrefXml(text);
            if (fromXml.SingleCore > 0 || fromXml.MultiCore > 0) return fromXml;
        }

        // KEY=VALUE format (R23 / 2024 .txt ranking files)
        var entries = text.Split('\n')
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

        return new CinebenchResult(
            SingleCore: ParseNum(entries.GetValueOrDefault("CBCPU1",   "0")),
            MultiCore:  ParseNum(entries.GetValueOrDefault("CBCPUX",   "0")));
    }

    /// <summary>
    /// Parses Cinema 4D preference XML (.pref) for Cinebench 2026 scores.
    /// CB2026 rm.pref stores entries as: &lt;entry id="CB26CPU1" value="1234"/&gt;
    /// or nested inside &lt;group&gt; containers — we search all attributes regardless of depth.
    /// </summary>
    private static CinebenchResult ParsePrefXml(string xml)
    {
        double single = 0, multi = 0;
        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);

            // Walk every element and look for score attributes
            foreach (System.Xml.XmlNode node in doc.SelectNodes("//*")!)
            {
                if (node.Attributes == null) continue;
                var id  = node.Attributes["id"]?.Value  ?? node.Attributes["name"]?.Value ?? "";
                var val = node.Attributes["value"]?.Value ?? node.InnerText;

                if (id.Equals("CB26CPU1", StringComparison.OrdinalIgnoreCase) ||
                    id.Equals("CBCPU1",   StringComparison.OrdinalIgnoreCase))
                    single = ParseNum(val);
                else if (id.Equals("CB26CPUX", StringComparison.OrdinalIgnoreCase) ||
                         id.Equals("CBCPUX",   StringComparison.OrdinalIgnoreCase))
                    multi = ParseNum(val);
            }
        }
        catch { }
        return new CinebenchResult(single, multi);
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
