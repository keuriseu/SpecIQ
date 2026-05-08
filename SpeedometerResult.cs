using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpecIQ;

public record SpeedometerEntry(
    int    Iteration,
    double Score,
    int    BatteryPct,
    int    ElapsedSeconds);

public class SpeedometerResult
{
    public string  Browser         { get; set; } = "WebView2";
    public string  MachineName     { get; set; } = Environment.MachineName;
    public string  StartedAt       { get; set; } = DateTime.Now.ToString("o");
    public int     StartBatteryPct { get; set; } = -1;
    public List<SpeedometerEntry> Entries { get; set; } = [];

    [JsonIgnore] public TimeSpan TotalDuration  => Entries.Count > 0 ? TimeSpan.FromSeconds(Entries[^1].ElapsedSeconds) : TimeSpan.Zero;
    [JsonIgnore] public int      IterationCount => Entries.Count;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "speedometer.json");

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static SpeedometerResult? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<SpeedometerResult>(File.ReadAllText(FilePath), JsonOpts)
                : null;
        }
        catch { return null; }
    }

    public string ExportText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SpecIQ Speedometer 3.1 — {MachineName}");
        sb.AppendLine($"Browser: {Browser}  ·  Started: {DateTime.Parse(StartedAt):g}  ·  Start battery: {(StartBatteryPct >= 0 ? StartBatteryPct + "%" : "?")}");
        sb.AppendLine($"Iterations: {IterationCount}  Duration: {FormatDuration(TotalDuration)}");
        sb.AppendLine();
        sb.AppendLine($"Iter  {"Score",-10}  Battery  Elapsed");
        sb.AppendLine($"────  {"──────────",-10}  ───────  ───────");
        foreach (var e in Entries)
            sb.AppendLine($"{e.Iteration,4}  {e.Score,10:F2}  {e.BatteryPct,6}%  {FormatDuration(TimeSpan.FromSeconds(e.ElapsedSeconds))}");
        if (Entries.Count > 1)
        {
            var first = Entries[0].Score;
            var last  = Entries[^1].Score;
            var avg   = Entries.Average(e => e.Score);
            var drop  = first > 0 ? (first - last) * 100.0 / first : 0;
            sb.AppendLine($"Score: First {first:F2}  Last {last:F2}  Avg {avg:F2}  Drop {drop:F1}%");
        }
        return sb.ToString();
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : $"{t.Minutes}m {t.Seconds:D2}s";
}
