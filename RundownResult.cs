using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpecIQ;

public record RundownEntry(
    int Iteration,
    int SingleScore,
    int MultiScore,
    int BatteryPct,
    int ElapsedSeconds,
    int GpuOpenClScore = 0,
    int GpuVulkanScore = 0);

public class RundownResult
{
    public string  BenchmarkType    { get; set; } = "CPU";
    public string? GeekbenchVersion { get; set; }
    public int     StartBatteryPct  { get; set; } = -1;
    public string  MachineName      { get; set; } = Environment.MachineName;
    public string  StartedAt        { get; set; } = DateTime.Now.ToString("o");
    public List<RundownEntry> Entries { get; set; } = [];

    [JsonIgnore] public TimeSpan TotalDuration  => Entries.Count > 0 ? TimeSpan.FromSeconds(Entries[^1].ElapsedSeconds) : TimeSpan.Zero;
    [JsonIgnore] public int      IterationCount => Entries.Count;
    [JsonIgnore] public bool     IsGpu          => BenchmarkType == "GPU";
    [JsonIgnore] public bool     IsStress       => BenchmarkType == "Stress";
    [JsonIgnore] public string   LabelA         => IsGpu ? "OpenCL" : IsStress ? "CPU Single" : "Single-Core";
    [JsonIgnore] public string   LabelB         => IsGpu ? "Vulkan"  : IsStress ? "CPU Multi"  : "Multi-Core";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "rundown.json");

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static RundownResult? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<RundownResult>(File.ReadAllText(FilePath), JsonOpts)
                : null;
        }
        catch { return null; }
    }

    public string ExportText()
    {
        var ver = GeekbenchVersion != null ? $"Geekbench {GeekbenchVersion} " : "";
        var sb  = new System.Text.StringBuilder();
        sb.AppendLine($"SpecIQ Battery Rundown — {MachineName}");
        sb.AppendLine($"{ver}{BenchmarkType}  ·  Started: {DateTime.Parse(StartedAt):g}  ·  Start battery: {(StartBatteryPct >= 0 ? StartBatteryPct + "%" : "?")}");
        sb.AppendLine($"Iterations: {IterationCount}  Duration: {FormatDuration(TotalDuration)}");
        sb.AppendLine();
        if (IsStress)
        {
            var hasVulkan = Entries.Any(e => e.GpuVulkanScore > 0);
            if (hasVulkan)
            {
                sb.AppendLine($"Iter  {"CPU Single",-12}  {"CPU Multi",-12}  {"GPU OpenCL",-12}  {"GPU Vulkan",-12}  Battery  Elapsed");
                sb.AppendLine($"────  {"────────────",-12}  {"────────────",-12}  {"────────────",-12}  {"────────────",-12}  ───────  ───────");
                foreach (var e in Entries)
                    sb.AppendLine($"{e.Iteration,4}  {e.SingleScore,12:N0}  {e.MultiScore,12:N0}  {e.GpuOpenClScore,12:N0}  {e.GpuVulkanScore,12:N0}  {e.BatteryPct,6}%  {FormatDuration(TimeSpan.FromSeconds(e.ElapsedSeconds))}");
            }
            else
            {
                sb.AppendLine($"Iter  {"CPU Single",-12}  {"CPU Multi",-12}  {"GPU OpenCL",-12}  Battery  Elapsed");
                sb.AppendLine($"────  {"────────────",-12}  {"────────────",-12}  {"────────────",-12}  ───────  ───────");
                foreach (var e in Entries)
                    sb.AppendLine($"{e.Iteration,4}  {e.SingleScore,12:N0}  {e.MultiScore,12:N0}  {e.GpuOpenClScore,12:N0}  {e.BatteryPct,6}%  {FormatDuration(TimeSpan.FromSeconds(e.ElapsedSeconds))}");
            }
        }
        else
        {
            sb.AppendLine($"Iter  {LabelA,-12}  {LabelB,-12}  Battery  Elapsed");
            sb.AppendLine($"────  {"────────────",-12}  {"────────────",-12}  ───────  ───────");
            foreach (var e in Entries)
                sb.AppendLine($"{e.Iteration,4}  {e.SingleScore,12:N0}  {e.MultiScore,12:N0}  {e.BatteryPct,6}%  {FormatDuration(TimeSpan.FromSeconds(e.ElapsedSeconds))}");
        }
        if (Entries.Count > 1)
        {
            if (IsStress)
            {
                AppendStats(sb, "CPU Single",  Entries.Select(e => e.SingleScore).ToList());
                AppendStats(sb, "CPU Multi",   Entries.Select(e => e.MultiScore).ToList());
                AppendStats(sb, "GPU OpenCL",  Entries.Select(e => e.GpuOpenClScore).ToList());
                AppendStats(sb, "GPU Vulkan",  Entries.Select(e => e.GpuVulkanScore).ToList());
            }
            else
            {
                AppendStats(sb, LabelA, Entries.Select(e => e.SingleScore).ToList());
                AppendStats(sb, LabelB, Entries.Select(e => e.MultiScore).ToList());
            }
        }
        return sb.ToString();
    }

    private static void AppendStats(System.Text.StringBuilder sb, string label, List<int> scores)
    {
        if (scores.Count == 0 || scores.Max() == 0) return;
        var first = scores[0];
        var last  = scores[^1];
        var avg   = (int)scores.Average();
        var drop  = first > 0 ? (first - last) * 100.0 / first : 0;
        sb.AppendLine($"{label}: First {first:N0}  Last {last:N0}  Avg {avg:N0}  Drop {drop:F1}%");
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : $"{t.Minutes}m {t.Seconds:D2}s";
}
