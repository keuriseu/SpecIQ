using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpecIQ;

public enum HistoryTool { Geekbench6, GeekbenchAI, Cinebench, ProcyonCV, ProcyonOffice, Blender, Speedometer }

public class HistoryEntry
{
    public HistoryTool Tool      { get; set; }
    public string RunAt          { get; set; } = DateTime.Now.ToString("o");
    public string Note           { get; set; } = "";  // e.g. "CPU", "GPU ×3 avg", backend label
    public double ScoreA         { get; set; }         // Single-Core / FP32 / Single
    public double ScoreB         { get; set; }         // Multi-Core  / FP16 / Multi
    public double ScoreC         { get; set; }         // —           / Quantized / —
    public int?   DurationSeconds { get; set; }

    [JsonIgnore]
    public DateTime RunAtDate => DateTime.TryParse(RunAt, out var dt) ? dt : DateTime.MinValue;
}

public static class BenchmarkHistory
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "history.json");

    private const int MaxEntries = 100;

    public static void Append(HistoryEntry entry)
    {
        try
        {
            var entries = Load();
            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
                entries = entries.Take(MaxEntries).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries, AppHelpers.JsonOpts));
        }
        catch { }
    }

    /// <summary>
    /// Appends the entry only if no existing entry with the same Tool + RunAt exists.
    /// Safe to call multiple times for the same run (e.g. on iteration completion and
    /// again when "View Previous" is clicked after a reboot).
    /// </summary>
    public static void AppendIfNew(HistoryEntry entry)
    {
        try
        {
            var entries = Load();
            if (entries.Any(e => e.Tool == entry.Tool && e.RunAt == entry.RunAt)) return;
            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
                entries = entries.Take(MaxEntries).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries, AppHelpers.JsonOpts));
        }
        catch { }
    }

    public static List<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<HistoryEntry>>(
                    File.ReadAllText(FilePath), AppHelpers.JsonOpts) ?? [];
        }
        catch { }
        return [];
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { }
    }
}
