using System.IO;
using System.Text.Json;

namespace SpecIQ;

public class SuiteResult
{
    private static readonly string SavePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "SpecIQ", "suite_last.json");

    public DateTime RanAt       { get; set; } = DateTime.Now;
    public string MachineName   { get; set; } = Environment.MachineName;

    // Geekbench CPU
    public double? GbCpuSingle { get; set; }
    public double? GbCpuMulti  { get; set; }

    // Geekbench GPU
    public double? GbGpuOpenCl { get; set; }
    public double? GbGpuVulkan { get; set; }

    // Geekbench AI NPU
    public double? GbAiNpu     { get; set; }

    // Cinebench
    public double? CbSingle    { get; set; }
    public double? CbMulti     { get; set; }

    // Speedometer
    public double? SpeedWebView { get; set; }
    public double? SpeedEdge    { get; set; }
    public double? SpeedChrome  { get; set; }

    // Procyon
    public double?           ProcyonCv       { get; set; }
    public ProcyonCvBackend? ProcyonCvBackend { get; set; }
    public double?           ProcyonOffice   { get; set; }

    // Blender
    public double? BlenderScore { get; set; }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
        File.WriteAllText(SavePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static SuiteResult? Load()
    {
        if (!File.Exists(SavePath)) return null;
        try { return JsonSerializer.Deserialize<SuiteResult>(File.ReadAllText(SavePath)); }
        catch { return null; }
    }

    public string ExportText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SpecIQ Full Suite  ·  {MachineName}  ·  {RanAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine(new string('─', 50));

        void Line(string label, double? val, string? suffix = null)
        {
            if (val is null) return;
            sb.AppendLine($"{label,-30} {val.Value:N0}{(suffix != null ? "  " + suffix : "")}");
        }

        Line("Geekbench 6  Single-Core",  GbCpuSingle);
        Line("Geekbench 6  Multi-Core",   GbCpuMulti);
        Line("Geekbench 6  GPU OpenCL",   GbGpuOpenCl);
        Line("Geekbench 6  GPU Vulkan",   GbGpuVulkan);
        Line("Geekbench AI  NPU",         GbAiNpu);
        Line("Cinebench  Single-Core",    CbSingle);
        Line("Cinebench  Multi-Core",     CbMulti);

        if (SpeedWebView is not null) sb.AppendLine($"{"Speedometer  WebView",-30} {SpeedWebView.Value:F2}");
        if (SpeedEdge    is not null) sb.AppendLine($"{"Speedometer  Edge",-30} {SpeedEdge.Value:F2}");
        if (SpeedChrome  is not null) sb.AppendLine($"{"Speedometer  Chrome",-30} {SpeedChrome.Value:F2}");

        if (ProcyonCv is not null)
        {
            var backend = ProcyonCvBackend.HasValue ? ProcyonService.BackendLabel(ProcyonCvBackend.Value) : "";
            sb.AppendLine($"{"Procyon AI CV",-30} {ProcyonCv.Value:N0}  {backend}");
        }
        Line("Procyon Office",            ProcyonOffice);
        Line("Blender  CPU",              BlenderScore);

        return sb.ToString().TrimEnd();
    }
}
