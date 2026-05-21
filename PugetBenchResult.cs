using System.IO;
using System.Text.Json;

namespace SpecIQ;

// Seconds is the "best of 3 loops" value as reported in the CSV.
public record PugetBenchTestResult(string TestName, string Category, double Seconds);

public class PugetBenchResult
{
    public List<PugetBenchTestResult> Tests          { get; } = [];
    public double                     CompositeScore { get; set; }
    public double                     GeneralScore   { get; set; }
    public double                     FilterScore    { get; set; }
}

public class PugetBenchSavedResult
{
    public double   CompositeScore   { get; set; }
    public double   GeneralScore     { get; set; }
    public double   FilterScore      { get; set; }
    public string   BenchmarkVersion { get; set; } = "";
    public string   MachineName      { get; set; } = Environment.MachineName;
    public string   SavedAt          { get; set; } = DateTime.Now.ToString("o");
    public List<PugetBenchTestResult> Tests { get; set; } = [];

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "pugetbench.json");

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppHelpers.JsonOpts));
        }
        catch { }
    }

    public static PugetBenchSavedResult? Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<PugetBenchSavedResult>(
                    File.ReadAllText(FilePath), AppHelpers.JsonOpts);
        }
        catch { }
        return null;
    }

    public string ExportText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"PugetBench for Photoshop — {MachineName}");
        sb.AppendLine($"Run: {DateTime.Parse(SavedAt):g}");
        if (!string.IsNullOrEmpty(BenchmarkVersion))
            sb.AppendLine($"Benchmark: v{BenchmarkVersion}");
        sb.AppendLine();
        sb.AppendLine($"Composite Score : {CompositeScore:N0}");
        sb.AppendLine($"General         : {GeneralScore:N0}");
        sb.AppendLine($"Filter          : {FilterScore:N0}");
        sb.AppendLine();
        foreach (var t in Tests)
            sb.AppendLine($"  {t.TestName,-42}  {t.Seconds,5:F2} s  [{t.Category}]");
        return sb.ToString();
    }
}
