using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpecIQ;

public record BlenderEntry(
    int    Iteration,
    double CompositeScore,
    double MonsterSpm,
    double JunkshopSpm,
    double ClassroomSpm,
    string DeviceType,
    int    BatteryPct,
    int    ElapsedSeconds);

public class BlenderRundownResult
{
    public string MachineName     { get; set; } = Environment.MachineName;
    public string StartedAt       { get; set; } = DateTime.Now.ToString("o");
    public int    StartBatteryPct { get; set; } = -1;
    public string DeviceType      { get; set; } = "CPU";
    public bool   IsRundown       { get; set; }
    public int    TotalElapsedSeconds { get; set; }
    public List<BlenderEntry> Entries { get; set; } = [];

    [JsonIgnore] public TimeSpan TotalDuration  =>
        TotalElapsedSeconds > 0 ? TimeSpan.FromSeconds(TotalElapsedSeconds) :
        Entries.Count > 0       ? TimeSpan.FromSeconds(Entries[^1].ElapsedSeconds) :
        TimeSpan.Zero;
    [JsonIgnore] public int      IterationCount => Entries.Count;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "blender_rundown.json");

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppHelpers.JsonOpts));
    }

    public static BlenderRundownResult? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<BlenderRundownResult>(File.ReadAllText(FilePath), AppHelpers.JsonOpts)
                : null;
        }
        catch { return null; }
    }

    public string ExportText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SpecIQ Blender Battery Rundown — {MachineName}");
        sb.AppendLine($"Device: {DeviceType}");
        sb.AppendLine($"Started: {DateTime.Parse(StartedAt):g}  ·  Start battery: {(StartBatteryPct >= 0 ? StartBatteryPct + "%" : "?")}");
        sb.AppendLine($"Iterations: {IterationCount}  ·  Duration: {AppHelpers.FormatDuration(TotalDuration)}");
        sb.AppendLine();
        sb.AppendLine($"{"Iter",-4}  {"Score",-7}  {"Monster",-9}  {"Junkshop",-10}  {"Classroom",-10}  Battery  Elapsed");
        sb.AppendLine($"{"────",-4}  {"───────",-7}  {"─────────",-9}  {"──────────",-10}  {"──────────",-10}  ───────  ───────");
        foreach (var e in Entries)
        {
            var mon = e.MonsterSpm   > 0 ? $"{(int)e.MonsterSpm:N0}"   : "—";
            var jnk = e.JunkshopSpm  > 0 ? $"{(int)e.JunkshopSpm:N0}"  : "—";
            var cls = e.ClassroomSpm > 0 ? $"{(int)e.ClassroomSpm:N0}" : "—";
            sb.AppendLine($"{e.Iteration,-4}  {e.CompositeScore,-7:N0}  {mon,-9}  {jnk,-10}  {cls,-10}  {e.BatteryPct,6}%  {AppHelpers.FormatDuration(TimeSpan.FromSeconds(e.ElapsedSeconds))}");
        }
        if (Entries.Count > 1)
        {
            var scores  = Entries.Select(e => e.CompositeScore).ToList();
            var first   = scores[0];
            var last    = scores[^1];
            var avg     = scores.Average();
            var endBat  = Entries[^1].BatteryPct;
            var endedAt = DateTime.Parse(StartedAt).Add(TotalDuration).ToString("g");
            sb.AppendLine();
            sb.AppendLine($"Overall: First {first:N0}  Last {last:N0}  Avg {avg:N0}");
            sb.AppendLine($"End battery: {endBat}%  ·  Ended: {endedAt}");
        }
        return sb.ToString();
    }
}
