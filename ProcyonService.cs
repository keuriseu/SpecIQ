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
    private static readonly string InstallDir = @"C:\Program Files\UL\Procyon";
    private static readonly string CmdExe     = Path.Combine(InstallDir, "ProcyonCmd.exe");
    private static readonly string Cv1Def     = Path.Combine(InstallDir, "ai_computer_vision_winml.def");
    private static readonly string Cv2Def     = Path.Combine(InstallDir, "ai_computer_vision_2_npu_qnnep_winml.def");

    public static string? FindInstalled() => File.Exists(CmdExe) ? CmdExe : null;
    public static bool    IsNpuAvailable() => File.Exists(Cv2Def);

    public static string BackendLabel(ProcyonCvBackend b) => b switch
    {
        ProcyonCvBackend.CpuF32 => "CPU  ·  FP32",
        ProcyonCvBackend.GpuF32 => "GPU  ·  FP32",
        ProcyonCvBackend.GpuF16 => "GPU  ·  FP16",
        ProcyonCvBackend.GpuInt => "GPU  ·  INT",
        ProcyonCvBackend.NpuQnn => "NPU  ·  INT8 (QNN)",
        _                       => b.ToString(),
    };

    // ── Run ───────────────────────────────────────────────────────────────

    public static async Task<ProcyonCvResult> RunAsync(
        string exePath,
        ProcyonCvBackend backend,
        IProgress<string> progress,
        CancellationToken ct)
    {
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

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(exePath, args)
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Kill entire process tree (includes javaw.exe child) on cancellation
            ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

            _ = TailLogAsync(logPath, progress, ct);

            await proc.WaitForExitAsync(ct);

            if (ct.IsCancellationRequested) throw new OperationCanceledException();
            if (proc.ExitCode != 0)         throw new Exception($"ProcyonCmd exited with code {proc.ExitCode}");

            if (File.Exists(xmlPath)) return ParseXml(xmlPath, backend);
            if (File.Exists(csvPath)) return ParseCsv(csvPath, backend);

            throw new Exception("No result file produced.");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // ── Def-file patching ─────────────────────────────────────────────────

    private static string BuildDefXml(ProcyonCvBackend backend)
    {
        bool isNpu = backend == ProcyonCvBackend.NpuQnn;
        var doc = XDocument.Load(isNpu ? Cv2Def : Cv1Def);

        foreach (var s in doc.Root!.Element("settings")!.Elements("setting"))
        {
            var name  = s.Element("name")?.Value;
            var valEl = s.Element("value");
            if (valEl == null) continue;

            switch (name)
            {
                case "ai_device_type":
                    valEl.Value = backend switch
                    {
                        ProcyonCvBackend.CpuF32 => "CPU",
                        ProcyonCvBackend.NpuQnn => "NPU",
                        _                       => "GPU",
                    };
                    break;
                case "ai_inference_precision":
                    valEl.Value = backend switch
                    {
                        ProcyonCvBackend.GpuF16 => "float16",
                        ProcyonCvBackend.GpuInt => "integer",
                        ProcyonCvBackend.NpuQnn => "int8",
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

            // Overall: look for any element whose local name contains "Result"
            // and has a numeric "score" attribute
            var benchEl = doc.Descendants()
                .Where(e => e.Name.LocalName.Contains("Result", StringComparison.OrdinalIgnoreCase)
                         && e.Attribute("score") != null)
                .OrderByDescending(e => e.Descendants().Count()) // prefer the wrapper element
                .FirstOrDefault();
            var overall = Num(benchEl?.Attribute("score")?.Value);

            double Get(params string[] kws)
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

            return Build(backend, overall, Get);
        }
        catch { return new ProcyonCvResult { Backend = backend, IsNpu = backend == ProcyonCvBackend.NpuQnn }; }
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
                    var hit = scores.FirstOrDefault(p => p.Key.Contains(kw, StringComparison.OrdinalIgnoreCase));
                    if (hit.Value > 0) return hit.Value;
                }
                return 0;
            }

            return Build(backend, overall, Get);
        }
        catch { return new ProcyonCvResult { Backend = backend, IsNpu = backend == ProcyonCvBackend.NpuQnn }; }
    }

    private static ProcyonCvResult Build(ProcyonCvBackend b, double overall, Func<string[], double> get)
        => new()
        {
            Backend      = b,
            IsNpu        = b == ProcyonCvBackend.NpuQnn,
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
