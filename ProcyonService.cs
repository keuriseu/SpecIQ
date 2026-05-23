using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SpecIQ;

public enum ProcyonCvBackend { CpuF32, GpuF32, GpuF16, GpuInt, NpuQnn }

public record ProcyonOfficeResult
{
    public int OverallScore    { get; init; }
    public int WordScore       { get; init; }
    public int ExcelScore      { get; init; }
    public int PowerPointScore { get; init; }
    public int OutlookScore    { get; init; }
}

public record ProcyonEssentialsResult
{
    public int OverallScore      { get; init; }
    public int FileScore         { get; init; }
    public int AppStartupScore   { get; init; }
    public int VideoCallScore    { get; init; }
    public int BrowserTabsScore  { get; init; }
    public int BrowserScore      { get; init; }
}

public record ProcyonCvResult
{
    public ProcyonCvBackend Backend  { get; init; }
    public bool             IsNpu    { get; init; }
    public double OverallScore       { get; init; }
    // CV1 (WinML) workloads
    public double MobileNetV3        { get; init; }
    public double InceptionV4        { get; init; }
    public double ResNet50           { get; init; }
    public double DeepLabV3          { get; init; }
    public double YoloV3             { get; init; }
    public double Esrgan             { get; init; }
    // CV2 (NPU/QNN) workloads
    public double ConvNextTiny       { get; init; }
    public double BlipBase           { get; init; }
    public double Video              { get; init; }
}

internal static class ProcyonService
{
    private static readonly string InstallDir  = @"C:\Program Files\UL\Procyon";
    private static readonly string CmdExe      = Path.Combine(InstallDir, "ProcyonCmd.exe");
    private static readonly string Cv1Def      = Path.Combine(InstallDir, "ai_computer_vision_winml.def");
    private static readonly string SnpeDef     = Path.Combine(InstallDir, "ai_computer_vision_snpe.def");

    // Office Productivity def — try common filenames in install order
    private static readonly string? OfficeDef =
        new[] { "office_productivity.def", "office_productivity_365.def", "procyon_office.def", "office.def" }
        .Select(f => Path.Combine(InstallDir, f))
        .FirstOrDefault(File.Exists);

    public static string? FindInstalled()           => File.Exists(CmdExe) ? CmdExe : null;
    public static bool    IsNpuAvailable()          => File.Exists(SnpeDef);
    public static string? FindOfficeInstalled()     => File.Exists(CmdExe) && OfficeDef != null ? CmdExe : null;
    public static string? OfficeDefName             => OfficeDef != null ? Path.GetFileName(OfficeDef) : null;
    public static string? FindEssentialsInstalled() => File.Exists(ProcyonExe) ? ProcyonExe : null;

    public static string BackendLabel(ProcyonCvBackend b) => b switch
    {
        ProcyonCvBackend.CpuF32 => "CPU  ·  FP32",
        ProcyonCvBackend.GpuF32 => "GPU  ·  FP32",
        ProcyonCvBackend.GpuF16 => "GPU  ·  FP16",
        ProcyonCvBackend.GpuInt => "GPU  ·  INT",
        ProcyonCvBackend.NpuQnn => "NPU  ·  SNPE (HTP)",
        _                       => b.ToString(),
    };

    // ── Run ───────────────────────────────────────────────────────────────

    public static async Task<ProcyonCvResult> RunAsync(
        string exePath,
        ProcyonCvBackend backend,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var debugDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpecIQ", "procyon_debug");
        Directory.CreateDirectory(debugDir);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"SpecIQ_Procyon_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var defPath = Path.Combine(tmpDir, "run.def");
            var csvPath = Path.Combine(tmpDir, "result.csv");
            var xmlPath = Path.Combine(tmpDir, "result.xml");
            var logPath = Path.Combine(tmpDir, "run.log");

            File.WriteAllText(defPath, BuildDefXml(backend));

            var args = $"-d \"{defPath}\" --export-xml \"{xmlPath}\" --export-simple-csv \"{csvPath}\" --log \"{logPath}\"";

            var stderrLines = new System.Text.StringBuilder();

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(exePath, args)
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    WorkingDirectory       = InstallDir,
                }
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) stderrLines.AppendLine(e.Data);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Kill entire process tree (includes javaw.exe child) on cancellation
            ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

            _ = TailLogAsync(logPath, progress, ct);

            await proc.WaitForExitAsync(ct);

            if (ct.IsCancellationRequested) throw new OperationCanceledException();

            // Persist copies for debugging, overwriting previous run's files
            if (File.Exists(xmlPath)) File.Copy(xmlPath, Path.Combine(debugDir, "result.xml"), overwrite: true);
            if (File.Exists(csvPath)) File.Copy(csvPath, Path.Combine(debugDir, "result.csv"), overwrite: true);
            if (File.Exists(logPath)) File.Copy(logPath, Path.Combine(debugDir, "run.log"),    overwrite: true);

            // Check for results — try XML first, fall back to CSV if score is still 0
            if (File.Exists(xmlPath))
            {
                var r = ParseXml(xmlPath, backend);
                if (r.OverallScore > 0) return r;
            }
            if (File.Exists(csvPath)) return ParseCsv(csvPath, backend);

            var stderr = stderrLines.ToString().Trim();
            var detail = string.IsNullOrEmpty(stderr) ? "" : $"\n{stderr}";
            throw new Exception($"ProcyonCmd exited with code {proc.ExitCode} and produced no results.{detail}");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // ── Def-file patching ─────────────────────────────────────────────────

    private static string BuildDefXml(ProcyonCvBackend backend)
    {
        // SNPE (NPU) uses its own def with fixed HTP+integer settings — no patching needed
        if (backend == ProcyonCvBackend.NpuQnn)
            return XDocument.Load(SnpeDef).ToString();

        var doc = XDocument.Load(Cv1Def);
        foreach (var s in doc.Root!.Element("settings")!.Elements("setting"))
        {
            var name  = s.Element("name")?.Value;
            var valEl = s.Element("value");
            if (valEl == null) continue;

            switch (name)
            {
                case "ai_device_type":
                    valEl.Value = backend == ProcyonCvBackend.CpuF32 ? "CPU" : "GPU";
                    break;
                case "ai_inference_precision":
                    valEl.Value = backend switch
                    {
                        ProcyonCvBackend.GpuF16 => "float16",
                        ProcyonCvBackend.GpuInt => "integer",
                        _                       => "float32",
                    };
                    break;
            }
        }

        return doc.ToString();
    }

    // ── Log tailing ───────────────────────────────────────────────────────

    private static async Task TailLogAsync(string logPath, IProgress<string> progress, CancellationToken ct)
    {
        // Wait up to 15 s for Procyon to create the log file
        for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
        {
            if (File.Exists(logPath)) break;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        if (!File.Exists(logPath)) return;

        long offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(offset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                while (!sr.EndOfStream)
                {
                    var line = await sr.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line != null) progress.Report(line);
                }
                offset = fs.Position;
            }
            catch { }
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
    }

    // ── Result parsers ────────────────────────────────────────────────────

    private static ProcyonCvResult ParseXml(string path, ProcyonCvBackend backend)
    {
        try
        {
            var doc = XDocument.Load(path);

            // Modern Procyon format: scores are element text values inside <result> elements
            // e.g. <AIOverallScore>1940</AIOverallScore>, <AIMobileNetV3AverageInferenceTime>0.30</...>
            // The summary result has passIndex == -1; per-pass results have passIndex >= 0.
            var resultEl = doc.Descendants("result")
                .OrderBy(r => int.TryParse(r.Element("passIndex")?.Value, out var p) ? p : int.MaxValue)
                .FirstOrDefault();

            if (resultEl != null)
            {
                var vals = resultEl.Elements()
                    .Where(e => double.TryParse(e.Value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                    .ToDictionary(
                        e => e.Name.LocalName,
                        e => Num(e.Value),
                        StringComparer.OrdinalIgnoreCase);

                double Lookup(params string[] kws)
                {
                    foreach (var kw in kws)
                    {
                        // Prefer "Average" fields over counts/median
                        var hit = vals.FirstOrDefault(p =>
                            p.Key.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                            p.Key.Contains("Average", StringComparison.OrdinalIgnoreCase));
                        if (hit.Value > 0) return hit.Value;
                        // Any match that isn't a raw count
                        hit = vals.FirstOrDefault(p =>
                            p.Key.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                            !p.Key.EndsWith("Count", StringComparison.OrdinalIgnoreCase));
                        if (hit.Value > 0) return hit.Value;
                    }
                    return 0;
                }

                var overall = Lookup("OverallScore");
                if (overall > 0) return Build(backend, overall, kws => Lookup(kws));
            }

            // Legacy format: score attributes on Result/Workload elements (older WinML XML)
            var benchEl = doc.Descendants()
                .Where(e => e.Name.LocalName.Contains("Result", StringComparison.OrdinalIgnoreCase)
                         && e.Attribute("score") != null)
                .OrderByDescending(e => e.Descendants().Count())
                .FirstOrDefault();
            var legacyOverall = Num(benchEl?.Attribute("score")?.Value);

            double LegacyGet(params string[] kws)
            {
                foreach (var kw in kws)
                {
                    var v = doc.Descendants()
                        .Where(e => e.Name.LocalName.Contains("Workload", StringComparison.OrdinalIgnoreCase)
                                 && (e.Attribute("name")?.Value ?? "")
                                    .Contains(kw, StringComparison.OrdinalIgnoreCase)
                                 && e.Attribute("score") != null)
                        .Select(e => Num(e.Attribute("score")?.Value))
                        .FirstOrDefault(s => s > 0);
                    if (v > 0) return v;
                }
                return 0;
            }

            return Build(backend, legacyOverall, kws => LegacyGet(kws));
        }
        catch { return new ProcyonCvResult { Backend = backend }; }
    }

    private static ProcyonCvResult ParseCsv(string path, ProcyonCvBackend backend)
    {
        try
        {
            var scores  = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double overall = 0;

            foreach (var raw in File.ReadAllLines(path))
            {
                var sep = raw.LastIndexOf(',');
                if (sep < 0) continue;
                var name  = raw[..sep].Trim();
                var value = raw[(sep + 1)..].Trim();
                if (!double.TryParse(value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var score)) continue;
                if (overall == 0) overall = score;
                scores[name] = score;
            }

            double Get(params string[] kws)
            {
                foreach (var kw in kws)
                {
                    var hit = scores.FirstOrDefault(p =>
                        p.Key.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                        p.Key.Contains("Average", StringComparison.OrdinalIgnoreCase));
                    if (hit.Value > 0) return hit.Value;
                    hit = scores.FirstOrDefault(p =>
                        p.Key.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                        !p.Key.EndsWith("Count", StringComparison.OrdinalIgnoreCase));
                    if (hit.Value > 0) return hit.Value;
                }
                return 0;
            }

            return Build(backend, overall, Get);
        }
        catch { return new ProcyonCvResult { Backend = backend }; }
    }

    private static ProcyonCvResult Build(ProcyonCvBackend b, double overall, Func<string[], double> get)
        => new()
        {
            Backend      = b,
            IsNpu        = false, // SNPE uses CV1 workloads — always show the CV1 grid
            OverallScore = overall,
            MobileNetV3  = get(["MobileNet"]),
            InceptionV4  = get(["Inception"]),
            ResNet50     = get(["ResNet"]),
            DeepLabV3    = get(["DeepLab"]),
            YoloV3       = get(["YOLO", "Yolo"]),
            Esrgan       = get(["ESRGAN"]),
            ConvNextTiny = get(["ConvNext"]),
            BlipBase     = get(["Blip", "BLIP"]),
            Video        = get(["Video"]),
        };

    private static double Num(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    // ── Procyon Office Productivity ───────────────────────────────────────
    //
    // Strategy: start Procyon.exe in GUI mode (no --noUi / -d flags).
    // GUI mode initializes in ~15–30 s vs 30+ minutes for ProcyonCmd's --noUi path.
    // Once javaw is ready we find its WebSocket port, connect, and send the run command.
    // We monitor Procyon.log for completion, then parse the autosave .procyon-result
    // file (a ZIP containing Arielle.xml and Result.csv).

    private static readonly string ProcyonExe = Path.Combine(InstallDir, "Procyon.exe");

    private static readonly string ProcyonDocsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Procyon");

    // Procyon writes failed-run temp files here; a stale OpenCL failure blocks the next start
    private static readonly string ProcyonTmpDir = @"C:\ProgramData\UL\Procyon\tmp";

    public static async Task<ProcyonOfficeResult> RunOfficeAsync(
        string exePath,          // kept for API compatibility; we use ProcyonExe directly
        IProgress<string> progress,
        CancellationToken ct)
    {
        if (!File.Exists(ProcyonExe))
            throw new InvalidOperationException($"Procyon.exe not found at {ProcyonExe}");

        var debugDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpecIQ", "procyon_office_debug");
        Directory.CreateDirectory(debugDir);

        // Kill any orphaned javaw and Office apps from a previous attempt.
        KillChorosJavaw(progress);
        var killedOffice = KillOfficeSuiteProcesses(progress);

        // Wait up to 5 s for each killed Office process to fully exit so its COM server
        // has time to deregister before Procyon starts. Without this wait, the COM
        // infrastructure can still be mid-shutdown when Procyon tries to acquire Word/Excel,
        // causing RPC_E_CALL_FAILED (0x800706BE) on the very first COM call of the next loop.
        if (killedOffice.Count > 0)
        {
            foreach (var p in killedOffice)
            {
                try   { await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct); }
                catch { }
                finally { p.Dispose(); }
            }
        }

        // Delete stale .tempresult files — a failed OpenCL run from a previous session
        // causes Procyon to enter an error-recovery loop on next launch and never open
        // its WebSocket server.
        ClearProcyonTempResults(progress);

        var procyonLog     = Path.Combine(ProcyonDocsDir, "Procyon.log");
        var procyonLogInfo = new FileInfo(procyonLog);
        long logOffset     = procyonLogInfo.Exists ? procyonLogInfo.Length : 0;

        // Start Procyon.exe in GUI mode — visible window; Office COM automation needs HWND
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(ProcyonExe)
            {
                UseShellExecute  = true,
                WorkingDirectory = InstallDir,
            }
        };
        proc.Start();

        ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

        // Per-call CTS so both TailProcyonLogAsync tasks stop when RunOfficeAsync
        // returns. Without this they accumulate across loop iterations (loop N starts
        // 2 new tails but the previous N-1 iterations' tails keep running), causing
        // every log line to appear 2×N times and the workload indicators to fire N times.
        using var tailCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            // ── Phase 1: wait for javaw WebSocket to open (~15–30 s) ─────────────
            progress.Report("Starting Procyon...");
            _ = TailProcyonLogAsync(procyonLog, logOffset, progress, tailCts.Token);  // surface startup errors early

            int? javawPid = null;
            int? wsPort   = null;

            // ── Phase 1: find javaw.exe (up to 7 minutes) ──────────────────────────
            // After multiple iterations, the previous Procyon run's cleanup
            // (OfficeProductivity-Starter.exe --clean) can keep the system busy for
            // several minutes before the new javaw.exe appears.  7 minutes gives
            // comfortable headroom even on degraded systems.
            using var javawCts    = new CancellationTokenSource(TimeSpan.FromMinutes(7));
            using var linkedJavaw = CancellationTokenSource.CreateLinkedTokenSource(ct, javawCts.Token);

            int pollCount = 0;
            while (!linkedJavaw.Token.IsCancellationRequested)
            {
                await Task.Delay(2_000, linkedJavaw.Token);
                pollCount++;

                javawPid = FindChorosJavawPid();
                if (javawPid != null)
                {
                    progress.Report($"javaw.exe found (PID {javawPid}). Looking for WebSocket port...");
                    break;
                }
                if (pollCount % 5 == 0)
                    progress.Report($"Waiting for Procyon to start... ({pollCount * 2}s elapsed)");
            }

            if (javawPid == null)
                throw new Exception("Procyon failed to start (javaw.exe / choros.jar not found after 7 minutes).");

            // ── Phase 2: find WebSocket port (6 minutes, fresh timer from NOW) ─────
            // IMPORTANT: a separate CancellationTokenSource is required here.
            // Calling CancelAfter() on an already-expired CTS is a no-op, so the
            // previous single-CTS approach silently failed when javaw was found just
            // after the Phase-1 deadline.
            //
            // 6 minutes (up from 3) because at low battery Windows battery-saver can
            // throttle the JVM ~18× — a 10-second WebSocket init becomes ~180 s,
            // right at the old 3-minute limit, causing consistent port-not-found
            // failures at the end of a rundown even though javaw starts fine.
            using var portCts    = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            using var linkedPort = CancellationTokenSource.CreateLinkedTokenSource(ct, portCts.Token);

            pollCount = 0;
            while (!linkedPort.Token.IsCancellationRequested)
            {
                await Task.Delay(500, linkedPort.Token);
                pollCount++;

                // Fast native path first; fall back to netstat every 10 polls (~5 s).
                wsPort = FindListeningPortForPidFast(javawPid.Value);
                if (wsPort != null)
                {
                    progress.Report($"Port {wsPort} found via IP Helper API.");
                    break;
                }

                if (pollCount % 10 == 0)
                {
                    // Every ~5 s also try netstat for diagnostics.
                    var (netstatPort, diag) = await FindPortViaNetstatAsync(javawPid.Value, linkedPort.Token);
                    wsPort = netstatPort ?? await FindPortViaPowerShellAsync(javawPid.Value, linkedPort.Token);
                    if (wsPort != null)
                    {
                        progress.Report($"Port {wsPort} found via netstat.");
                        break;
                    }
                    progress.Report($"javaw PID {javawPid} not yet listening ({pollCount / 2}s). {diag}");
                }
            }

            if (wsPort == null)
                throw new Exception($"Procyon WebSocket port not found after 6 minutes (javaw PID {javawPid}). " +
                    "Try running SpecIQ as administrator, or check that Procyon is not blocked by a firewall.");

            // Brief extra wait for the UI to finish loading its benchmark list
            await Task.Delay(4_000, ct);
            progress.Report($"Procyon ready (port {wsPort}). Launching benchmark...");

            // ── Phase 2: send run command via WebSocket ───────────────────────────
            // Keep the WebSocket open for the full benchmark duration — Procyon aborts
            // the run if the client disconnects before the workloads finish.
            using var ws = await TriggerOfficeBenchmarkAsync(wsPort.Value, progress, ct);
            _ = DrainWebSocketAsync(ws, ct);   // background: drain progress frames

            // ── Phase 3: tail Procyon.log for progress; wait for result file ─────
            progress.Report("Office benchmark running...");
            _ = TailProcyonLogAsync(procyonLog, logOffset, progress, tailCts.Token);

            using var benchCts    = new CancellationTokenSource(TimeSpan.FromMinutes(45));
            using var linkedBench = CancellationTokenSource.CreateLinkedTokenSource(ct, benchCts.Token);

            var (resultPath, procyonError) = await WaitForProcyonResultAsync(procyonLog, logOffset, progress, linkedBench.Token);

            if (resultPath == null)
                throw new Exception("Procyon Office benchmark timed out after 45 minutes without producing a result.");

            // ── Phase 4: parse scores from .procyon-result ZIP ────────────────────
            progress.Report($"Benchmark complete — parsing {Path.GetFileName(resultPath)}...");

            try { File.Copy(resultPath, Path.Combine(debugDir, "latest.procyon-result"), overwrite: true); } catch { }

            var result = ParseProcyonResultZip(resultPath);

            if (result.OverallScore == 0)
            {
                // Prefer the Procyon log error (always available) over the ZIP JSON error
                // (rarely present in the result file).
                var msg = !string.IsNullOrEmpty(procyonError)
                    ? $"Procyon benchmark error: {procyonError}"
                    : BuildProcyonErrorMessage(resultPath);
                throw new Exception(msg);
            }

            return result;
        }
        finally
        {
            tailCts.Cancel();   // stop log tails before returning so they don't accumulate
            try { proc.Kill(entireProcessTree: true); } catch { }
            // Also kill javaw explicitly — Procyon.exe detaches it as an orphan process
            // so it survives the proc.Kill above and blocks the next run attempt.
            KillChorosJavaw(null);
            // Kill Office apps and the Procyon starter/cleanup process so they don't
            // accumulate across iterations and cause COM-automation failures or slow starts.
            foreach (var p in KillOfficeSuiteProcesses(null))
                try { p.Dispose(); } catch { }
        }
    }

    private static void KillChorosJavaw(IProgress<string>? progress)
    {
        try
        {
            var pid = FindChorosJavawPid();
            if (pid == null) return;
            progress?.Report($"Killing leftover javaw (PID {pid}) from previous attempt...");
            Process.GetProcessById(pid.Value).Kill(entireProcessTree: true);
        }
        catch { }
    }

    // Kill Office apps + Procyon's own starter/cleanup exe between iterations.
    // Stale Office processes from the previous run can cause COM-automation failures
    // (Range.Select → 0x800A03EC, RPC_E_CALL_FAILED → 0x800706BE) when Procyon tries
    // to reopen the same app; the starter/cleanup process delays javaw startup too.
    // Returns all successfully killed Process objects so the caller can wait for exit.
    internal static List<Process> KillOfficeSuiteProcesses(IProgress<string>? progress)
    {
        var killed = new List<Process>();

        // Process name (no extension) → display name for the log
        var targets = new (string Name, string Label)[]
        {
            ("EXCEL",                      "Excel"),
            ("WINWORD",                    "Word"),
            ("POWERPNT",                   "PowerPoint"),
            ("OUTLOOK",                    "Outlook"),
            ("OfficeProductivity-Starter", "Procyon Office Starter"),
        };

        foreach (var (name, label) in targets)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length == 0) continue;
                progress?.Report($"Killing {label} ({procs.Length} process{(procs.Length > 1 ? "es" : "")})...");
                foreach (var p in procs)
                {
                    try   { p.Kill(entireProcessTree: true); killed.Add(p); }
                    catch { p.Dispose(); }
                }
            }
            catch { }
        }

        return killed;
    }

    // Scans the .procyon-result ZIP for a JSON entry with a "detailedError" field and
    // builds a human-readable exception message.  Falls back to a generic message if the
    // ZIP contains no parseable error detail.
    private static string BuildProcyonErrorMessage(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                if (!entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                    !entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    using var stream = entry.Open();
                    using var doc    = JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("status", out var status) &&
                        status.TryGetProperty("detailedError", out var errEl))
                    {
                        var detail = errEl.GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(detail)) continue;

                        // Provide extra guidance for the known Orchestrator version mismatch.
                        var hint = detail.Contains("Unknown subAction", StringComparison.OrdinalIgnoreCase)
                            ? " — The installed Procyon Orchestrator does not support this workload variant." +
                              " Please update or reinstall Procyon Essentials."
                            : "";
                        return $"Procyon benchmark error: {detail}{hint}";
                    }
                }
                catch { }
            }
        }
        catch { }
        return "Benchmark completed but no overall score was found in the result file.";
    }

    private static void ClearProcyonTempResults(IProgress<string>? progress)
    {
        // Wipe the entire tmp dir — not just *.tempresult.  Procyon logs "Checking for a
        // temp resultfile from …\tmp" at startup and will hang if it finds any stale file
        // there regardless of extension (.tempresult, .tmp, .json, etc.).  A fresh empty
        // directory guarantees a clean slate for every iteration.
        try
        {
            if (!Directory.Exists(ProcyonTmpDir)) return;
            var files = Directory.GetFiles(ProcyonTmpDir, "*", SearchOption.AllDirectories);
            if (files.Length == 0) return;
            progress?.Report($"Clearing {files.Length} stale Procyon temp file(s) from {ProcyonTmpDir}...");
            foreach (var f in files)
                try { File.Delete(f); } catch { }
            // Remove any now-empty subdirectories too
            foreach (var d in Directory.GetDirectories(ProcyonTmpDir))
                try { Directory.Delete(d, recursive: true); } catch { }
        }
        catch { }
    }

    // ── Helper: find the javaw process running choros.jar ────────────────────

    private static int? FindChorosJavawPid()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'javaw.exe'");
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                var cmd = obj["CommandLine"]?.ToString() ?? "";
                if (cmd.Contains("choros.jar", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt32(obj["ProcessId"]);
            }
        }
        catch { }
        return null;
    }

    // ── Helper: find which TCP port javaw is listening on ────────────────────

    // Fast path: query the kernel's TCP listener table directly via IP Helper API.
    // No process spawn — returns in microseconds.  Falls back to netstat/PowerShell
    // only when the native call fails (e.g. access denied in very locked-down envs).
    private static int? FindListeningPortForPidFast(int pid)
    {
        try
        {
            int bufLen = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufLen, false, AF_INET,
                                TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, 0);

            var buf = Marshal.AllocHGlobal(bufLen);
            try
            {
                if (GetExtendedTcpTable(buf, ref bufLen, false, AF_INET,
                                        TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                    return null;

                int numEntries = Marshal.ReadInt32(buf);
                int rowSize    = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                int offset     = 4; // skip dwNumEntries

                for (int i = 0; i < numEntries; i++, offset += rowSize)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buf + offset);
                    if ((int)row.dwOwningPid != pid) continue;
                    // Port is stored in network byte order in the low 16 bits.
                    int port = (ushort)IPAddress.NetworkToHostOrder((short)(row.dwLocalPort & 0xFFFF));
                    if (port > 0) return port;
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
    }

    private static async Task<int?> FindListeningPortAsync(int pid, CancellationToken ct)
    {
        // Try the fast native path first.
        var fast = FindListeningPortForPidFast(pid);
        if (fast != null) return fast;
        // Fallback: spawn netstat / PowerShell.
        var (port, _) = await FindPortViaNetstatAsync(pid, ct);
        if (port != null) return port;
        return await FindPortViaPowerShellAsync(pid, ct);
    }

    private const  int AF_INET = 2;

    private enum TCP_TABLE_CLASS { TCP_TABLE_OWNER_PID_LISTENER = 3 }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, bool bOrder,
        int ulAf, TCP_TABLE_CLASS tableClass, int reserved);

    private static async Task<(int? port, string diagnostics)> FindPortViaNetstatAsync(int pid, CancellationToken ct)
    {
        try
        {
            using var ps = new Process
            {
                StartInfo = new ProcessStartInfo("netstat.exe", "-ano")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow         = true,
                }
            };
            ps.Start();
            var output = await ps.StandardOutput.ReadToEndAsync(ct);
            await ps.WaitForExitAsync(ct);

            var pidStr    = pid.ToString();
            var allForPid = new System.Text.StringBuilder();
            int? found    = null;

            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || parts[^1].Trim() != pidStr) continue;

                allForPid.AppendLine(line.Trim());   // log every socket for this PID

                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null) continue;          // already have one

                var addr  = parts[1];
                var colon = addr.LastIndexOf(':');
                if (colon < 0) continue;
                if (int.TryParse(addr[(colon + 1)..], out var p) && p > 0)
                    found = p;
            }

            var diag = allForPid.Length > 0
                ? $"netstat entries for PID {pid}:\n{allForPid}"
                : $"netstat: no entries at all for PID {pid}";

            return (found, diag);
        }
        catch (Exception ex)
        {
            return (null, $"netstat failed: {ex.Message}");
        }
    }

    private static async Task<int?> FindPortViaPowerShellAsync(int pid, CancellationToken ct)
    {
        try
        {
            using var ps = new Process
            {
                StartInfo = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -Command \"(Get-NetTCPConnection -OwningProcess {pid}" +
                    $" -State Listen -ErrorAction SilentlyContinue" +
                    $" | Sort-Object LocalPort | Select-Object -First 1).LocalPort\"")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow         = true,
                }
            };
            ps.Start();
            var output = await ps.StandardOutput.ReadToEndAsync(ct);
            await ps.WaitForExitAsync(ct);
            if (int.TryParse(output.Trim(), out var port) && port > 0)
                return port;
        }
        catch { }
        return null;
    }

    // ── Helper: connect WebSocket and send the run command ───────────────────
    // Returns the live WebSocket — caller must keep it open until benchmark finishes.

    private static async Task<ClientWebSocket> TriggerOfficeBenchmarkAsync(
        int port, IProgress<string> progress, CancellationToken ct)
    {
        var uri = new Uri($"wss://127.0.0.1:{port}/elevation");

        // ClientWebSocket cannot be reused after a failed ConnectAsync (state → Aborted),
        // so create a fresh instance on every attempt.
        ClientWebSocket? ws = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            ws?.Dispose();
            ws = new ClientWebSocket();
            // Procyon's local server uses a self-signed TLS certificate — bypass validation
            // since we are connecting to localhost only and the cert cannot be spoofed locally.
            ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            try { await ws.ConnectAsync(uri, ct); break; }
            catch when (attempt < 5 && !ct.IsCancellationRequested)
            {
                progress.Report($"WebSocket connect attempt {attempt + 1} failed, retrying...");
                await Task.Delay(2_000, ct);
            }
        }

        if (ws is null || ws.State != WebSocketState.Open)
        {
            ws?.Dispose();
            throw new Exception($"Could not connect to Procyon WebSocket at {uri} after 6 attempts.");
        }

        // Do NOT call ReceiveAsync here — cancelling it (e.g. on a timeout) transitions
        // ClientWebSocket to Aborted, making SendAsync fail.  Procyon never sends a
        // CONNECTION_ESTABLISHED frame anyway; initial CHOPS_STATE / PRODUCT_STATE frames
        // are drained by the DrainWebSocketAsync background task started by the caller.

        // Workload list comes from the server's PRODUCT_STATE message.
        // Omitting 'workloads' leaves <workload_sets/> empty and the FSM never starts.
        const string cmd =
            "{\"service\":\"/v1/run\"," +
            "\"request\":\"/OFFICE_PRODUCTIVITY_BENCHMARK_DEFAULT\"," +
            "\"parameters\":{" +
              "\"workloads\":[" +
                "\"OFFICE_PRODUCTIVITY_STARTUP_DEFAULT\"," +
                "\"OFFICE_PRODUCTIVITY_EXCEL1_DEFAULT\"," +
                "\"OFFICE_PRODUCTIVITY_WORD_DEFAULT\"," +
                "\"OFFICE_PRODUCTIVITY_EXCEL2_DEFAULT\"," +
                "\"OFFICE_PRODUCTIVITY_POWERPOINT_DEFAULT\"," +
                "\"OFFICE_PRODUCTIVITY_OUTLOOK_DEFAULT\"," +
                "\"OFFICE_PRODUCTIVITY_END_DEFAULT\"]," +
              "\"settings\":{}," +
              "\"currentUiViews\":[\"MY_SUITE\"]}}";
        await ws.SendAsync(Encoding.UTF8.GetBytes(cmd), WebSocketMessageType.Text, endOfMessage: true, ct);
        progress.Report("Run command sent to Procyon.");

        return ws;
    }

    // ── Helper: drain incoming WebSocket frames until closed or cancelled ────
    // Keeps the connection alive while the benchmark runs; Procyon sends progress
    // frames that must be consumed so the server-side send buffer doesn't stall.

    private static async Task DrainWebSocketAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16_384];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                await ws.ReceiveAsync(buf, ct);
        }
        catch { /* connection closed or cancelled — normal end of benchmark */ }
    }

    // ── Helper: tail Procyon.log from a saved offset ─────────────────────────

    private static async Task TailProcyonLogAsync(
        string logPath, long startOffset, IProgress<string> progress, CancellationToken ct)
    {
        long offset = startOffset;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(offset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                while (!sr.EndOfStream)
                {
                    var line = await sr.ReadLineAsync(ct);
                    if (line != null) progress.Report(line);
                }
                offset = fs.Position;
            }
            catch { }
            await Task.Delay(500, ct);
        }
    }

    // ── Helper: watch Procyon.log for the autosave result path ───────────────
    // Procyon writes: "Writtenfile is (OK|ERROR, C:\...\procyon-autosave-xxx.procyon-result)"
    // Also detect licence warnings (OOB_GRACE) so we can surface a helpful message.
    // Also captures the last BenchmarkRunError message for callers to use when no score is found.
    //
    // Returns (null, null) on timeout/cancellation; Task.Delay is caught explicitly so the
    // per-benchmark benchCts timeout doesn't leak a raw TaskCanceledException to callers.

    private static async Task<(string? path, string? benchmarkError)> WaitForProcyonResultAsync(
        string logPath, long startOffset, IProgress<string> progress, CancellationToken ct)
    {
        long   offset        = startOffset;
        bool   licenceWarned = false;
        string? benchmarkError = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(offset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                while (!sr.EndOfStream)
                {
                    var line = await sr.ReadLineAsync(ct);
                    if (line == null) continue;

                    // Note OOB_GRACE once — Procyon surfaces this as a licensing status but it
                    // is commonly reported even on activated Office installs and is not reliably
                    // predictive of benchmark failures (loops still complete and score normally).
                    if (!licenceWarned &&
                        line.Contains("OOB_GRACE", StringComparison.OrdinalIgnoreCase))
                    {
                        licenceWarned = true;
                        progress.Report("Note: Procyon reports Office licensing status OOB_GRACE. " +
                            "If Office is not activated, some workloads may fail.");
                    }

                    // Capture the last Procyon error for richer diagnostics when no score is found.
                    // The result ZIP rarely contains a parseable JSON error; the log always does.
                    var errMatch = Regex.Match(line,
                        @"BenchmarkRunError\{status=ERROR, message=(.+?)\}");
                    if (errMatch.Success)
                        benchmarkError = errMatch.Groups[1].Value.Trim();

                    // Match both OK and ERROR — on ERROR Procyon still writes a result file
                    // with whatever partial scores it collected before the failure.
                    var m = Regex.Match(line,
                        @"Writtenfile is \((OK|ERROR), (.+\.procyon-result)\)");
                    if (m.Success)
                    {
                        var status = m.Groups[1].Value;
                        var path   = m.Groups[2].Value.Trim();
                        if (status == "ERROR")
                            progress.Report($"Procyon reported a benchmark error. " +
                                "Attempting to parse partial results from the result file...");
                        if (File.Exists(path)) return (path, benchmarkError);
                    }
                }
                offset = fs.Position;
            }
            catch { }
            // Catch cancellation explicitly so the benchCts timeout doesn't leak a raw
            // TaskCanceledException — callers check for null and throw a clearer message.
            try { await Task.Delay(1_000, ct); }
            catch (OperationCanceledException) { return (null, null); }
        }
        return (null, null);
    }

    // ── Helper: extract scores from a .procyon-result ZIP ────────────────────
    // The ZIP contains Arielle.xml (result XML) and Result.csv.

    private static ProcyonOfficeResult ParseProcyonResultZip(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);

            var xmlEntry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("Result.xml", StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (xmlEntry != null)
            {
                var tmp = Path.GetTempFileName();
                try
                {
                    using (var dst = File.Create(tmp))
                        xmlEntry.Open().CopyTo(dst);
                    var r = ParseOfficeXml(tmp);
                    if (r.OverallScore > 0) return r;
                }
                finally { try { File.Delete(tmp); } catch { } }
            }

            var csvEntry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("Result.csv", StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
            if (csvEntry != null)
            {
                var tmp = Path.GetTempFileName();
                try
                {
                    using (var dst = File.Create(tmp))
                        csvEntry.Open().CopyTo(dst);
                    return ParseOfficeCsv(tmp);
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }
        catch { }
        return new ProcyonOfficeResult();
    }

    private static ProcyonOfficeResult ParseOfficeXml(string path)
    {
        try
        {
            var doc      = XDocument.Load(path);
            var resultEl = doc.Descendants("result")
                .OrderBy(r => int.TryParse(r.Element("passIndex")?.Value, out var p) ? p : int.MaxValue)
                .FirstOrDefault();

            if (resultEl != null)
            {
                var vals = resultEl.Elements()
                    .Where(e => double.TryParse(e.Value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                    .ToDictionary(e => e.Name.LocalName, e => Num(e.Value), StringComparer.OrdinalIgnoreCase);

                int Get(params string[] kws)
                {
                    foreach (var kw in kws)
                    {
                        var hit = vals.FirstOrDefault(p =>
                            p.Key.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                            !p.Key.EndsWith("Count", StringComparison.OrdinalIgnoreCase));
                        if (hit.Value > 0) return (int)hit.Value;
                    }
                    return 0;
                }

                var exactOverall = vals.TryGetValue("OfficeProductivityScore", out var ov) ? (int)ov : 0;
                return new ProcyonOfficeResult
                {
                    OverallScore    = exactOverall > 0 ? exactOverall : Get("Overall", "Total"),
                    WordScore       = Get("Word"),
                    ExcelScore      = Get("Excel"),
                    PowerPointScore = Get("PowerPoint", "Powerpoint", "PPT"),
                    OutlookScore    = Get("Outlook"),
                };
            }
            return new ProcyonOfficeResult();
        }
        catch { return new ProcyonOfficeResult(); }
    }

    private static ProcyonOfficeResult ParseOfficeCsv(string path)
    {
        try
        {
            var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double overall = 0;

            foreach (var raw in File.ReadAllLines(path))
            {
                var sep = raw.LastIndexOf(',');
                if (sep < 0) continue;
                var name  = raw[..sep].Trim();
                var value = raw[(sep + 1)..].Trim();
                if (!double.TryParse(value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var score)) continue;
                if (overall == 0) overall = score;
                scores[name] = score;
            }

            int Get(params string[] kws)
            {
                foreach (var kw in kws)
                {
                    var hit = scores.FirstOrDefault(p =>
                        p.Key.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                        !p.Key.EndsWith("Count", StringComparison.OrdinalIgnoreCase));
                    if (hit.Value > 0) return (int)hit.Value;
                }
                return 0;
            }

            return new ProcyonOfficeResult
            {
                OverallScore    = (int)overall,
                WordScore       = Get("Word"),
                ExcelScore      = Get("Excel"),
                PowerPointScore = Get("PowerPoint", "Powerpoint", "PPT"),
                OutlookScore    = Get("Outlook"),
            };
        }
        catch { return new ProcyonOfficeResult(); }
    }

    // ── Procyon Essentials ────────────────────────────────────────────────
    //
    // Same GUI-mode WebSocket strategy as Office.  The Essentials workload
    // IDs are discovered at runtime from the PRODUCT_STATE frame Procyon sends
    // immediately after the WebSocket handshake, with a hardcoded fallback.

    // Fallback workload ID used when PRODUCT_STATE discovery times out.
    // ESSENTIALS_BENCHMARK_CUSTOM / ESSENTIALS_CUSTOM is the variant confirmed to run on
    // Snapdragon machines; ESSENTIALS_BENCHMARK_DEFAULT fails silently on this hardware.
    private static readonly string[] EssentialsFallbackWorkloads = ["ESSENTIALS_CUSTOM"];

    public static async Task<ProcyonEssentialsResult> RunEssentialsAsync(
        string exePath,
        IProgress<string> progress,
        CancellationToken ct)
    {
        if (!File.Exists(ProcyonExe))
            throw new InvalidOperationException($"Procyon.exe not found at {ProcyonExe}");

        var debugDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpecIQ", "procyon_essentials_debug");
        Directory.CreateDirectory(debugDir);

        KillChorosJavaw(progress);
        KillEssentialsProcesses(progress);
        ClearProcyonTempResults(progress);

        var procyonLog     = Path.Combine(ProcyonDocsDir, "Procyon.log");
        var procyonLogInfo = new FileInfo(procyonLog);
        long logOffset     = procyonLogInfo.Exists ? procyonLogInfo.Length : 0;

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(ProcyonExe)
            {
                UseShellExecute  = true,
                WorkingDirectory = InstallDir,
            }
        };
        proc.Start();

        ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

        using var tailCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            // ── Phase 1: wait for javaw WebSocket ──────────────────────────────
            progress.Report("Starting Procyon...");
            _ = TailProcyonLogAsync(procyonLog, logOffset, progress, tailCts.Token);

            int? javawPid = null;
            int? wsPort   = null;

            // 6 minutes (up from 3): same throttling concern as Office — at low battery
            // the JVM initialises slowly and the combined javaw+port search needs more
            // headroom.  Also, after many iterations accumulated browser/media processes
            // slow the JVM enough to miss the old 3-minute budget.
            using var initCts    = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            using var linkedInit = CancellationTokenSource.CreateLinkedTokenSource(ct, initCts.Token);

            int pollCount = 0;
            while (!linkedInit.Token.IsCancellationRequested)
            {
                await Task.Delay(500, linkedInit.Token);
                pollCount++;

                if (javawPid == null)
                {
                    javawPid = FindChorosJavawPid();
                    if (javawPid != null)
                        progress.Report($"javaw.exe found (PID {javawPid}). Looking for WebSocket port...");
                    else if (pollCount % 20 == 0)
                        progress.Report($"Waiting for Procyon to start... ({pollCount / 2}s elapsed)");
                }

                if (javawPid != null && wsPort == null)
                {
                    // Fast native path.
                    wsPort = FindListeningPortForPidFast(javawPid.Value);
                    if (wsPort != null)
                    {
                        progress.Report($"Port {wsPort} found via IP Helper API.");
                    }
                    else if (pollCount % 10 == 0)
                    {
                        // Every ~5 s fall back to netstat for diagnostics.
                        var (netstatPort, diag) = await FindPortViaNetstatAsync(javawPid.Value, linkedInit.Token);
                        wsPort = netstatPort ?? await FindPortViaPowerShellAsync(javawPid.Value, linkedInit.Token);
                        if (wsPort != null)
                            progress.Report($"Port {wsPort} found via netstat.");
                        else
                            progress.Report($"javaw PID {javawPid} not yet listening ({pollCount / 2}s). {diag}");
                    }
                }

                if (wsPort != null) break;
            }

            if (javawPid == null)
                throw new Exception("Procyon failed to start (javaw.exe / choros.jar not found after 6 minutes).");
            if (wsPort == null)
                throw new Exception($"Procyon WebSocket port not found after 6 minutes (javaw PID {javawPid}).");

            await Task.Delay(4_000, ct);
            progress.Report($"Procyon ready (port {wsPort}). Discovering Essentials workloads...");

            // ── Phase 2: discover workloads + send run command ─────────────────
            using var ws = await TriggerEssentialsBenchmarkAsync(wsPort.Value, progress, ct);
            _ = DrainWebSocketAsync(ws, ct);

            // ── Phase 3: tail log; wait for result file ────────────────────────
            progress.Report("Essentials benchmark running...");
            _ = TailProcyonLogAsync(procyonLog, logOffset, progress, tailCts.Token);

            using var benchCts    = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedBench = CancellationTokenSource.CreateLinkedTokenSource(ct, benchCts.Token);

            var (resultPath, _) = await WaitForProcyonResultAsync(procyonLog, logOffset, progress, linkedBench.Token);

            if (resultPath == null)
                throw new Exception("Procyon Essentials benchmark timed out after 30 minutes without producing a result.");

            // ── Phase 4: parse result ZIP ──────────────────────────────────────
            progress.Report($"Benchmark complete — parsing {Path.GetFileName(resultPath)}...");
            try { File.Copy(resultPath, Path.Combine(debugDir, "latest.procyon-result"), overwrite: true); } catch { }

            var result = ParseEssentialsResultZip(resultPath);
            if (result.OverallScore == 0)
                throw new Exception(BuildProcyonErrorMessage(resultPath));

            return result;
        }
        finally
        {
            tailCts.Cancel();
            try { proc.Kill(entireProcessTree: true); } catch { }
            KillChorosJavaw(null);
            KillEssentialsProcesses(null);
        }
    }

    // Kill processes left behind by the Essentials benchmark workloads.
    // After several iterations these accumulate (especially Chromium) and slow
    // the JVM enough to miss the port-open deadline on the next iteration.
    internal static void KillEssentialsProcesses(IProgress<string>? progress)
    {
        var targets = new (string Name, string Label)[]
        {
            ("chrome",    "Chrome"),
            ("chromium",  "Chromium"),
            ("soffice",   "LibreOffice"),
            ("vscodium",  "VSCodium"),
            ("code",      "VSCode"),
        };

        foreach (var (name, label) in targets)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length == 0) continue;
                progress?.Report($"Killing {label} ({procs.Length} process{(procs.Length > 1 ? "es" : "")})...");
                foreach (var p in procs)
                    try { p.Kill(entireProcessTree: true); } catch { }
            }
            catch { }
        }
    }

    // Connects, reads initial PRODUCT_STATE frames to discover workload IDs,
    // then sends the run command.  Returns the live WebSocket (keep it open).
    private static async Task<ClientWebSocket> TriggerEssentialsBenchmarkAsync(
        int port, IProgress<string> progress, CancellationToken ct)
    {
        var uri = new Uri($"wss://127.0.0.1:{port}/elevation");

        ClientWebSocket? ws = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            ws?.Dispose();
            ws = new ClientWebSocket();
            ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

            // Use a per-attempt timeout so a hung ConnectAsync (port bound but WS server not
            // yet ready) doesn't block forever — it retries after 15 s instead.
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linkedCt   = CancellationTokenSource.CreateLinkedTokenSource(ct, connectCts.Token);
            try { await ws.ConnectAsync(uri, linkedCt.Token); break; }
            catch (OperationCanceledException) when (connectCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                progress.Report($"WebSocket connect attempt {attempt + 1} timed out (15 s), retrying...");
                await Task.Delay(2_000, ct);
            }
            catch when (attempt < 5 && !ct.IsCancellationRequested)
            {
                progress.Report($"WebSocket connect attempt {attempt + 1} failed, retrying...");
                await Task.Delay(2_000, ct);
            }
        }

        if (ws is null || ws.State != WebSocketState.Open)
        {
            ws?.Dispose();
            throw new Exception($"Could not connect to Procyon WebSocket at {uri} after 6 attempts.");
        }

        // Discover ESSENTIALS workload IDs from the server's initial PRODUCT_STATE frame.
        // Use a scoped 20-second token so ReceiveAsync is cancelled if Procyon never
        // sends a frame (e.g. initial state was broadcast before we started listening).
        // If the timeout fires: ws is Aborted → we dispose it and reconnect fresh so
        // the run command can be sent on a clean socket.
        string[] workloads;
        using var discoveryCts    = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var discoveryLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, discoveryCts.Token);
        try
        {
            workloads = await DiscoverEssentialsWorkloadsAsync(ws, progress, discoveryLinked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Discovery timed out — WebSocket is Aborted.  Reconnect for the run command.
            progress.Report("Discovery timed out (20 s) — reconnecting to send run command...");
            ws.Abort();
            ws.Dispose();
            ws = new ClientWebSocket();
            ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            using var reconnectCts    = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var reconnectLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, reconnectCts.Token);
            await ws.ConnectAsync(uri, reconnectLinked.Token);
            progress.Report("Reconnected. Using fallback workload IDs.");
            workloads = EssentialsFallbackWorkloads;
        }

        // Build and send run command.
        // Use ESSENTIALS_BENCHMARK_CUSTOM — the _DEFAULT variant fails silently on Snapdragon.
        var wlJson = string.Join(",", workloads.Select(id => $"\"{id}\""));
        var cmd =
            "{\"service\":\"/v1/run\"," +
            "\"request\":\"/ESSENTIALS_BENCHMARK_CUSTOM\"," +
            "\"parameters\":{" +
              $"\"workloads\":[{wlJson}]," +
              "\"settings\":{}," +
              "\"currentUiViews\":[\"MY_SUITE\"]}}";

        await ws.SendAsync(Encoding.UTF8.GetBytes(cmd), WebSocketMessageType.Text, endOfMessage: true, ct);
        progress.Report("Run command sent to Procyon.");
        return ws;
    }

    // Reads WebSocket frames until PRODUCT_STATE arrives, then parses the JSON
    // to extract the workloads array from the ESSENTIALS_BENCHMARK_CUSTOM entry.
    // Returns discovered IDs, or the hardcoded fallback if discovery fails.
    // The caller passes a scoped token with a deadline; if ReceiveAsync is cancelled
    // by that token the OperationCanceledException propagates so the caller can
    // reconnect on a clean socket before sending the run command.
    private static async Task<string[]> DiscoverEssentialsWorkloadsAsync(
        ClientWebSocket ws, IProgress<string> progress, CancellationToken ct)
    {
        var buf = new byte[2 * 1024 * 1024];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf, ct);
            }
            catch (OperationCanceledException) { throw; }  // propagate timeout to caller
            catch { break; }

            var text    = Encoding.UTF8.GetString(buf, 0, result.Count);
            var preview = text.Length > 200 ? text[..200] + "…" : text;
            progress.Report($"[WS frame] {preview}");

            if (!text.Contains("\"messageType\":\"PRODUCT_STATE\"", StringComparison.Ordinal))
                continue;

            progress.Report($"[WS] Got PRODUCT_STATE ({text.Length} chars), parsing...");
            try
            {
                using var doc       = JsonDocument.Parse(text);
                var       benchmarks = doc.RootElement.GetProperty("message").GetProperty("benchmarks");
                foreach (var b in benchmarks.EnumerateArray())
                {
                    var test = b.GetProperty("test").GetString() ?? "";
                    // ESSENTIALS_BENCHMARK_CUSTOM is the variant that runs on Snapdragon.
                    // ESSENTIALS_BENCHMARK_DEFAULT fails silently (empty tempdir, no log output).
                    if (!test.Equals("ESSENTIALS_BENCHMARK_CUSTOM", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!b.TryGetProperty("workloads", out var wlArr)) break;
                    var ids = wlArr.EnumerateArray()
                                   .Select(w => w.GetString()!)
                                   .Where(s => !string.IsNullOrEmpty(s))
                                   .ToArray();
                    if (ids.Length > 0)
                    {
                        progress.Report($"Discovered {ids.Length} Essentials workloads: {string.Join(", ", ids)}");
                        return ids;
                    }
                    break;
                }
                progress.Report("[WS] ESSENTIALS_BENCHMARK_DEFAULT not found in PRODUCT_STATE — using fallback.");
            }
            catch (Exception ex) { progress.Report($"[WS] JSON parse failed: {ex.Message}"); }
            break;
        }

        progress.Report($"PRODUCT_STATE did not yield workload IDs — using fallback.");
        return EssentialsFallbackWorkloads;
    }

    // ── Essentials result parsing ──────────────────────────────────────────

    private static ProcyonEssentialsResult ParseEssentialsResultZip(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);

            var xmlEntry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("Result.xml", StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (xmlEntry != null)
            {
                var tmp = Path.GetTempFileName();
                try
                {
                    using (var dst = File.Create(tmp))
                        xmlEntry.Open().CopyTo(dst);
                    var r = ParseEssentialsXml(tmp);
                    if (r.OverallScore > 0) return r;
                }
                finally { try { File.Delete(tmp); } catch { } }
            }

            var csvEntry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("Result.csv", StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
            if (csvEntry != null)
            {
                var tmp = Path.GetTempFileName();
                try
                {
                    using (var dst = File.Create(tmp))
                        csvEntry.Open().CopyTo(dst);
                    return ParseEssentialsCsv(tmp);
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }
        catch { }
        return new ProcyonEssentialsResult();
    }

    private static ProcyonEssentialsResult ParseEssentialsXml(string path)
    {
        try
        {
            var doc      = XDocument.Load(path);
            var resultEl = doc.Descendants("result")
                .OrderBy(r => int.TryParse(r.Element("passIndex")?.Value, out var p) ? p : int.MaxValue)
                .FirstOrDefault();

            if (resultEl != null)
            {
                var vals = resultEl.Elements()
                    .Where(e => double.TryParse(e.Value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                    .ToDictionary(e => e.Name.LocalName, e => Num(e.Value), StringComparer.OrdinalIgnoreCase);

                int Get(params string[] kws)
                {
                    foreach (var kw in kws)
                    {
                        var hit = vals.FirstOrDefault(p =>
                            p.Key.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                            !p.Key.EndsWith("Count", StringComparison.OrdinalIgnoreCase));
                        if (hit.Value > 0) return (int)hit.Value;
                    }
                    return 0;
                }

                return new ProcyonEssentialsResult
                {
                    OverallScore     = Get("EssentialsScore", "Overall", "Total"),
                    FileScore        = Get("FileScore",       "File"),
                    AppStartupScore  = Get("AppStartupScore", "AppLaunch", "AppStart", "Startup"),
                    VideoCallScore   = Get("VideoCallScore",  "VideoCall", "Video"),
                    BrowserTabsScore = Get("BrowserTabsScore","BrowserTabs", "Tabs"),
                    // "BrowserOverall" is specific enough to match EssentialsBrowserOverallScoreCustom
                    // without also matching EssentialsBrowserTabsOverallScoreCustom.
                    BrowserScore     = Get("BrowserOverall",  "BrowserScore"),
                };
            }
        }
        catch { }
        return new ProcyonEssentialsResult();
    }

    private static ProcyonEssentialsResult ParseEssentialsCsv(string path)
    {
        try
        {
            var scores  = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double overall = 0;

            foreach (var raw in File.ReadAllLines(path))
            {
                var sep = raw.LastIndexOf(',');
                if (sep < 0) continue;
                var name  = raw[..sep].Trim();
                var value = raw[(sep + 1)..].Trim();
                if (!double.TryParse(value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var score)) continue;
                if (overall == 0) overall = score;
                scores[name] = score;
            }

            int Get(params string[] kws)
            {
                foreach (var kw in kws)
                {
                    var hit = scores.FirstOrDefault(p =>
                        p.Key.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                        !p.Key.EndsWith("Count", StringComparison.OrdinalIgnoreCase));
                    if (hit.Value > 0) return (int)hit.Value;
                }
                return 0;
            }

            return new ProcyonEssentialsResult
            {
                OverallScore     = (int)overall,
                FileScore        = Get("File"),
                AppStartupScore  = Get("AppStartup", "AppLaunch", "AppStart", "Startup"),
                VideoCallScore   = Get("VideoCall",  "Video"),
                BrowserTabsScore = Get("BrowserTabs","Tabs"),
                BrowserScore     = Get("BrowserOverall", "BrowserScore"),
            };
        }
        catch { return new ProcyonEssentialsResult(); }
    }
}
