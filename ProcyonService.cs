using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
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

    public static string? FindInstalled()       => File.Exists(CmdExe) ? CmdExe : null;
    public static bool    IsNpuAvailable()      => File.Exists(SnpeDef);
    public static string? FindOfficeInstalled() => File.Exists(CmdExe) && OfficeDef != null ? CmdExe : null;
    public static string? OfficeDefName         => OfficeDef != null ? Path.GetFileName(OfficeDef) : null;

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

        var procyonLog    = Path.Combine(ProcyonDocsDir, "Procyon.log");
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

        try
        {
            // ── Phase 1: wait for javaw WebSocket to open (~15–30 s) ─────────────
            progress.Report("Starting Procyon...");

            int? javawPid = null;
            int? wsPort   = null;

            using var initCts    = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linkedInit = CancellationTokenSource.CreateLinkedTokenSource(ct, initCts.Token);

            while (!linkedInit.Token.IsCancellationRequested)
            {
                await Task.Delay(2_000, linkedInit.Token);

                if (javawPid == null)
                    javawPid = FindChorosJavawPid();

                if (javawPid != null && wsPort == null)
                    wsPort = await FindListeningPortAsync(javawPid.Value, linkedInit.Token);

                if (wsPort != null) break;
            }

            if (javawPid == null)
                throw new Exception("Procyon failed to start (javaw.exe / choros.jar not found after 3 minutes).");
            if (wsPort == null)
                throw new Exception("Procyon WebSocket port not found after 3 minutes.");

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
            _ = TailProcyonLogAsync(procyonLog, logOffset, progress, ct);

            using var benchCts    = new CancellationTokenSource(TimeSpan.FromMinutes(45));
            using var linkedBench = CancellationTokenSource.CreateLinkedTokenSource(ct, benchCts.Token);

            var resultPath = await WaitForProcyonResultAsync(procyonLog, logOffset, progress, linkedBench.Token);

            if (resultPath == null)
                throw new Exception("Procyon Office benchmark timed out after 45 minutes without producing a result.");

            // ── Phase 4: parse scores from .procyon-result ZIP ────────────────────
            progress.Report($"Benchmark complete — parsing {Path.GetFileName(resultPath)}...");

            try { File.Copy(resultPath, Path.Combine(debugDir, "latest.procyon-result"), overwrite: true); } catch { }

            var result = ParseProcyonResultZip(resultPath);

            if (result.OverallScore == 0)
                throw new Exception("Benchmark completed but no overall score was found in the result file.");

            return result;
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
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

    private static async Task<int?> FindListeningPortAsync(int pid, CancellationToken ct)
    {
        try
        {
            // Get-NetTCPConnection is faster and more reliable than netstat parsing
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
    // Procyon writes: "Writtenfile is (OK, C:\...\procyon-autosave-xxx.procyon-result)"

    private static async Task<string?> WaitForProcyonResultAsync(
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
                    if (line == null) continue;

                    var m = Regex.Match(line, @"Writtenfile is \(OK, (.+\.procyon-result)\)");
                    if (m.Success)
                    {
                        var path = m.Groups[1].Value.Trim();
                        if (File.Exists(path)) return path;
                    }
                }
                offset = fs.Position;
            }
            catch { }
            await Task.Delay(1_000, ct);
        }
        return null;
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
}
