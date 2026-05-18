using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpecIQ;

public record ProcyonOfficeEntry(
    int Iteration,
    int Score,
    int WordScore,
    int ExcelScore,
    int PowerPointScore,
    int OutlookScore,
    int BatteryPct,
    int ElapsedSeconds);

public class ProcyonOfficeRundownResult
{
    public string MachineName     { get; set; } = Environment.MachineName;
    public string StartedAt       { get; set; } = DateTime.Now.ToString("o");
    public int    StartBatteryPct { get; set; } = -1;
    public int    TotalElapsedSeconds { get; set; }
    public List<ProcyonOfficeEntry> Entries { get; set; } = [];

    [JsonIgnore] public TimeSpan TotalDuration  =>
        TotalElapsedSeconds > 0 ? TimeSpan.FromSeconds(TotalElapsedSeconds) :
        Entries.Count > 0       ? TimeSpan.FromSeconds(Entries[^1].ElapsedSeconds) :
        TimeSpan.Zero;
    [JsonIgnore] public int      IterationCount => Entries.Count;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "procyon_office_rundown.json");

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppHelpers.JsonOpts));
    }

    public static ProcyonOfficeRundownResult? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ProcyonOfficeRundownResult>(File.ReadAllText(FilePath), AppHelpers.JsonOpts)
                : null;
        }
        catch { return null; }
    }

    public string ExportText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SpecIQ Procyon Office Battery Rundown — {MachineName}");
        sb.AppendLine($"Started: {DateTime.Parse(StartedAt):g}  ·  Start battery: {(StartBatteryPct >= 0 ? StartBatteryPct + "%" : "?")}");
        sb.AppendLine($"Iterations: {IterationCount}  ·  Duration: {AppHelpers.FormatDuration(TotalDuration)}");
        sb.AppendLine();
        sb.AppendLine($"{"Iter",-4}  {"Score",-7}  {"Word",-7}  {"Excel",-7}  {"PPT",-7}  {"Outlook",-7}  Battery  Elapsed");
        sb.AppendLine($"{"────",-4}  {"───────",-7}  {"───────",-7}  {"───────",-7}  {"───────",-7}  {"───────",-7}  ───────  ───────");
        foreach (var e in Entries)
            sb.AppendLine($"{e.Iteration,-4}  {e.Score,-7:N0}  {e.WordScore,-7:N0}  {e.ExcelScore,-7:N0}  {e.PowerPointScore,-7:N0}  {e.OutlookScore,-7:N0}  {e.BatteryPct,6}%  {AppHelpers.FormatDuration(TimeSpan.FromSeconds(e.ElapsedSeconds))}");
        if (Entries.Count > 1)
        {
            var scores  = Entries.Select(e => e.Score).ToList();
            var first   = scores[0];
            var last    = scores[^1];
            var avg     = (int)scores.Average();
            var endBat  = Entries[^1].BatteryPct;
            var endedAt = DateTime.Parse(StartedAt).Add(TotalDuration).ToString("g");
            sb.AppendLine();
            sb.AppendLine($"Overall: First {first:N0}  Last {last:N0}  Avg {avg:N0}");
            sb.AppendLine($"End battery: {endBat}%  ·  Ended: {endedAt}");
        }
        return sb.ToString();
    }
}
