using System.Diagnostics;
using System.IO;
using System.Xml.Linq;

namespace SpecIQ;

public enum ProcyonCvBackend { CpuF32, GpuF32, GpuF16, GpuInt, NpuQnn }

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

    public static string? FindInstalled() => File.Exists(CmdExe) ? CmdExe : null;
    public static bool    IsNpuAvailable() => File.Exists(SnpeDef);

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
}
