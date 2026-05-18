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
    public int    TotalElapsedSeconds { get; set; }
    public List<SpeedometerEntry> Entries { get; set; } = [];

    [JsonIgnore] public TimeSpan TotalDuration  =>
        TotalElapsedSeconds > 0 ? TimeSpan.FromSeconds(TotalElapsedSeconds) :
        Entries.Count > 0       ? TimeSpan.FromSeconds(Entries[^1].ElapsedSeconds) :
        TimeSpan.Zero;
    [JsonIgnore] public int      IterationCount => Entries.Count;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "speedometer.json");

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppHelpers.JsonOpts));
    }

    public static SpeedometerResult? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<SpeedometerResult>(File.ReadAllText(FilePath), AppHelpers.JsonOpts)
                : null;
        }
        catch { return null; }
    }

    public string ExportText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SpecIQ Speedometer 3.1 — {MachineName}");
        sb.AppendLine($"Browser: {Browser}  ·  Started: {DateTime.Parse(StartedAt):g}  ·  Start battery: {(StartBatteryPct >= 0 ? StartBatteryPct + "%" : "?")}");
        sb.AppendLine($"Iterations: {IterationCount}  Duration: {AppHelpers.FormatDuration(TotalDuration)}");
        sb.AppendLine();
        sb.AppendLine($"Iter  {"Score",-10}  Battery  Elapsed");
        sb.AppendLine($"────  {"──────────",-10}  ───────  ───────");
        foreach (var e in Entries)
            sb.AppendLine($"{e.Iteration,4}  {e.Score,10:F2}  {e.BatteryPct,6}%  {AppHelpers.FormatDuration(TimeSpan.FromSeconds(e.ElapsedSeconds))}");
        if (Entries.Count > 1)
        {
            var first   = Entries[0].Score;
            var last    = Entries[^1].Score;
            var avg     = Entries.Average(e => e.Score);
            var endBat  = Entries[^1].BatteryPct;
            var endedAt = DateTime.Parse(StartedAt).Add(TotalDuration).ToString("g");
            sb.AppendLine();
            sb.AppendLine($"Overall: First {first:F2}  Last {last:F2}  Avg {avg:F2}");
            sb.AppendLine($"End battery: {endBat}%  ·  Ended: {endedAt}");
        }
        return sb.ToString();
    }

}
