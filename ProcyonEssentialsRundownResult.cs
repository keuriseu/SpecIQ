using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpecIQ;

public record ProcyonEssentialsEntry(
    int Iteration,
    int Score,
    int FileScore,
    int AppStartupScore,
    int VideoCallScore,
    int BrowserTabsScore,
    int BrowserScore,
    int BatteryPct,
    int ElapsedSeconds);

public class ProcyonEssentialsRundownResult
{
    public string MachineName         { get; set; } = Environment.MachineName;
    public string StartedAt           { get; set; } = DateTime.Now.ToString("o");
    public int    StartBatteryPct     { get; set; } = -1;
    public bool   IsRundown           { get; set; }
    public int    TotalElapsedSeconds { get; set; }
    /// <summary>Battery % captured at the moment the run was cancelled (trip-wire or stop).
    /// -1 means the loop exited normally; use <see cref="Entries"/>[^1].BatteryPct instead.</summary>
    public int    EndBatteryPct       { get; set; } = -1;
    /// <summary>1-based iteration number that was running when the run was cancelled and
    /// did not produce a score. Null when all started iterations completed.</summary>
    public int?   IncompleteIteration { get; set; }
    public List<ProcyonEssentialsEntry> Entries { get; set; } = [];

    [JsonIgnore] public TimeSpan TotalDuration =>
        TotalElapsedSeconds > 0 ? TimeSpan.FromSeconds(TotalElapsedSeconds) :
        Entries.Count > 0       ? TimeSpan.FromSeconds(Entries[^1].ElapsedSeconds) :
        TimeSpan.Zero;
    [JsonIgnore] public int IterationCount => Entries.Count;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "procyon_essentials_rundown.json");

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppHelpers.JsonOpts));
    }

    public static ProcyonEssentialsRundownResult? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ProcyonEssentialsRundownResult>(
                    File.ReadAllText(FilePath), AppHelpers.JsonOpts)
                : null;
        }
        catch { return null; }
    }

    public string ExportText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SpecIQ Procyon Essentials — {MachineName}");
        sb.AppendLine($"Started: {DateTime.Parse(StartedAt):g}  ·  Start battery: {(StartBatteryPct >= 0 ? StartBatteryPct + "%" : "?")}");
        sb.AppendLine($"Iterations: {IterationCount}  ·  Duration: {AppHelpers.FormatDuration(TotalDuration)}");
        sb.AppendLine();
        sb.AppendLine($"{"Iter",-4}  {"Score",-7}  {"File",-7}  {"AppLaunch",-9}  {"Video",-7}  {"Tabs",-7}  {"Browser",-7}  Battery  Elapsed");
        sb.AppendLine($"{"────",-4}  {"───────",-7}  {"───────",-7}  {"─────────",-9}  {"───────",-7}  {"───────",-7}  {"───────",-7}  ───────  ───────");
        foreach (var e in Entries)
            sb.AppendLine(
                $"{e.Iteration,-4}  {e.Score,-7:N0}  {e.FileScore,-7:N0}  {e.AppStartupScore,-9:N0}" +
                $"  {e.VideoCallScore,-7:N0}  {e.BrowserTabsScore,-7:N0}  {e.BrowserScore,-7:N0}" +
                $"  {e.BatteryPct,6}%  {AppHelpers.FormatDuration(TimeSpan.FromSeconds(e.ElapsedSeconds))}");
        if (Entries.Count > 1)
        {
            var first   = Entries[0].Score;
            var last    = Entries[^1].Score;
            var avg     = (int)Entries.Average(e => e.Score);
            var endBat  = EndBatteryPct >= 0 ? EndBatteryPct : Entries[^1].BatteryPct;
            var endedAt = DateTime.Parse(StartedAt).Add(TotalDuration).ToString("g");
            sb.AppendLine();
            sb.AppendLine($"Overall: First {first:N0}  Last {last:N0}  Avg {avg:N0}");
            sb.AppendLine($"End battery: {endBat}%  ·  Ended: {endedAt}");
            if (IncompleteIteration.HasValue)
                sb.AppendLine($"Note: Iteration {IncompleteIteration} did not complete (run cancelled at {endBat}% battery).");
        }
        return sb.ToString();
    }
}
