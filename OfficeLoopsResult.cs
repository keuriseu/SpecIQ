using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpecIQ;

public record OfficeLoopsEntry(
    int Iteration,
    int BatteryPct,
    int ElapsedSeconds);

public class OfficeLoopsResult
{
    public string MachineName         { get; set; } = Environment.MachineName;
    public string StartedAt           { get; set; } = DateTime.Now.ToString("o");
    public int    StartBatteryPct     { get; set; } = -1;
    public int    EndBatteryPct       { get; set; } = -1;
    public int    TotalElapsedSeconds { get; set; }
    public List<OfficeLoopsEntry> Entries { get; set; } = [];

    [JsonIgnore] public TimeSpan TotalDuration =>
        TotalElapsedSeconds > 0 ? TimeSpan.FromSeconds(TotalElapsedSeconds) :
        Entries.Count > 0       ? TimeSpan.FromSeconds(Entries[^1].ElapsedSeconds) :
        TimeSpan.Zero;
    [JsonIgnore] public int IterationCount => Entries.Count;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "office_loops.json");

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppHelpers.JsonOpts));
    }

    public string ExportText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SpecIQ Procyon Office 5 Loops — {MachineName}");
        sb.AppendLine($"Started: {DateTime.Parse(StartedAt):g}  ·  Start battery: {(StartBatteryPct >= 0 ? StartBatteryPct + "%" : "?")}");
        sb.AppendLine($"Completed: {IterationCount} / 5 loops  ·  Duration: {AppHelpers.FormatDuration(TotalDuration)}");
        sb.AppendLine();
        sb.AppendLine($"{"Loop",-4}  {"Battery",-8}  Elapsed");
        sb.AppendLine($"{"────",-4}  {"───────",-8}  ───────");
        foreach (var e in Entries)
            sb.AppendLine($"{e.Iteration,-4}  {e.BatteryPct,6}%  {AppHelpers.FormatDuration(TimeSpan.FromSeconds(e.ElapsedSeconds))}");
        var endBat = EndBatteryPct >= 0 ? EndBatteryPct
                   : Entries.Count > 0  ? Entries[^1].BatteryPct
                   : -1;
        if (endBat >= 0)
        {
            sb.AppendLine();
            sb.AppendLine($"End battery: {endBat}%");
        }
        return sb.ToString();
    }
}
