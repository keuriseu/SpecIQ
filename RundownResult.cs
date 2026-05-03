using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpecIQ;

public record RundownEntry(
    int    Iteration,
    int    Score,
    int    BatteryPct,
    int    ElapsedSeconds);

public class RundownResult
{
    public string BenchmarkType { get; set; } = "CPU";
    public string MachineName   { get; set; } = Environment.MachineName;
    public string StartedAt     { get; set; } = DateTime.Now.ToString("o");
    public List<RundownEntry> Entries { get; set; } = [];

    [JsonIgnore]
    public TimeSpan TotalDuration => Entries.Count > 0
        ? TimeSpan.FromSeconds(Entries[^1].ElapsedSeconds)
        : TimeSpan.Zero;

    [JsonIgnore]
    public int IterationCount => Entries.Count;

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
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SpecIQ Battery Rundown — {MachineName}");
        sb.AppendLine($"Type: {BenchmarkType}  Started: {DateTime.Parse(StartedAt):g}");
        sb.AppendLine($"Iterations: {IterationCount}  Duration: {FormatDuration(TotalDuration)}");
        sb.AppendLine();
        sb.AppendLine("Iter  Score    Battery  Elapsed");
        sb.AppendLine("────  ───────  ───────  ───────");
        foreach (var e in Entries)
            sb.AppendLine($"{e.Iteration,4}  {e.Score,7:N0}  {e.BatteryPct,6}%  {FormatDuration(TimeSpan.FromSeconds(e.ElapsedSeconds))}");
        if (Entries.Count > 1)
        {
            var first = Entries[0].Score;
            var last  = Entries[^1].Score;
            var avg   = (int)Entries.Average(e => e.Score);
            var drop  = first > 0 ? (first - last) * 100.0 / first : 0;
            sb.AppendLine();
            sb.AppendLine($"First: {first:N0}  Last: {last:N0}  Avg: {avg:N0}  Drop: {drop:F1}%");
        }
        return sb.ToString();
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : $"{t.Minutes}m {t.Seconds:D2}s";
}
