using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpecIQ;

// ── Data types ────────────────────────────────────────────────────────────────

public record BlenderSceneResult(string SceneName, double SamplesPerMinute)
{
    // samples/min is already a "higher = faster" metric — use it directly as the score
    public double Score => Math.Round(SamplesPerMinute);
}

public class BlenderRunResult
{
    public List<BlenderSceneResult> Scenes    { get; }      = [];
    public string                   DeviceType { get; init; } = "CPU";
    public string                   Version    { get; init; } = "";

    public double CompositeScore
    {
        get
        {
            var valid = Scenes.Where(s => s.SamplesPerMinute > 0).ToList();
            return valid.Count == 0 ? 0 : Math.Round(valid.Average(s => s.SamplesPerMinute));
        }
    }
}

// ── Service ───────────────────────────────────────────────────────────────────

public static class BlenderService
{
    // Standard benchmark scenes (name passed to CLI)
    public static readonly string[] SceneNames = ["monster", "junkshop", "classroom"];

    // ── Detection ─────────────────────────────────────────────────────────────

    public static string? FindCli()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 1. Installed locations
        foreach (var known in new[]
        {
            Path.Combine(pf, "Blender Benchmark Launcher", "benchmark-launcher-cli.exe"),
            Path.Combine(la, "Programs", "Blender Benchmark Launcher", "benchmark-launcher-cli.exe"),
        })
        {
            if (File.Exists(known)) return known;
        }

        // 2. Downloads folder (portable extracted zip)
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
        {
            var exes = Directory.GetFiles(downloads, "benchmark-launcher-cli.exe", SearchOption.AllDirectories);
            if (exes.Length > 0) return exes.OrderByDescending(x => x).First();
        }

        // 3. PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var c = Path.Combine(dir.Trim(), "benchmark-launcher-cli.exe");
            if (File.Exists(c)) return c;
        }

        return null;
    }

    // ── Version / readiness ───────────────────────────────────────────────────

    // Returns e.g. "5.1.1" for the latest available Blender version in the launcher
    public static async Task<string?> GetLatestBlenderVersionAsync(string cli, CancellationToken ct)
    {
        var output = await RunAsync(cli, "blender list", ct);
        return output.Split('\n')
            .Select(l => l.Split('\t')[0].Trim())
            .FirstOrDefault(v => !string.IsNullOrEmpty(v) && !v.StartsWith("ERROR"));
    }

    // Returns true if the given Blender version is fully downloaded and valid
    public static async Task<bool> IsBlenderReadyAsync(string cli, string version, CancellationToken ct)
    {
        var (_, code) = await RunCaptureAsync(cli, $"devices --blender-version {version}", ct);
        return code == 0;
    }

    // ── Download ──────────────────────────────────────────────────────────────

    public static async Task DownloadBlenderAsync(
        string cli, string version, IProgress<string> progress, CancellationToken ct)
        => await RunStreamAsync(cli, $"blender download --blender-version {version}", progress, ct);

    public static async Task DownloadScenesAsync(
        string cli, string version, IProgress<string> progress, CancellationToken ct)
        => await RunStreamAsync(cli, $"scenes download --blender-version {version}", progress, ct);

    // ── Devices ───────────────────────────────────────────────────────────────

    // Returns device type strings: "CPU", "CUDA", "OPTIX", "HIP", etc.
    public static async Task<List<string>> GetDeviceTypesAsync(
        string cli, string version, CancellationToken ct)
    {
        var (output, code) = await RunCaptureAsync(cli, $"devices --blender-version {version}", ct);
        if (code != 0) return ["CPU"];

        // Output is tab-separated: <device_name>\t<device_type>
        // Device types (CPU, CUDA, OPTIX, HIP, ONEAPI, METAL) never contain spaces;
        // device names almost always do — use that to pick the right token.
        var types = output.Split('\n')
            .Select(l => l.Split('\t')
                          .Select(p => p.Trim())
                          .FirstOrDefault(p => !string.IsNullOrEmpty(p) && !p.Contains(' '))
                         ?? "")
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .ToList();

        return types.Count > 0 ? types : ["CPU"];
    }

    // ── Benchmark run ─────────────────────────────────────────────────────────

    public static async Task<BlenderRunResult> RunBenchmarkAsync(
        string            cli,
        string            version,
        string[]          scenes,
        string            deviceType,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var sceneArgs = string.Join(" ", scenes);
        var args      = $"benchmark --blender-version {version} --device-type {deviceType} --json {sceneArgs}";

        // Capture the JSON array that the launcher emits after all scenes finish.
        // It starts with a bare '[' line (or '{' for older single-object format),
        // distinct from progress bars which start with "N / 100 [...]".
        var jsonAccum    = new System.Text.StringBuilder();
        bool collectJson = false;

        IProgress<string> inner = new Progress<string>(line =>
        {
            progress.Report(line);
            var trimmed = line.Trim();
            if (!collectJson && (trimmed == "[" || trimmed == "{" ||
                                 (trimmed.StartsWith("{") && trimmed.EndsWith("}"))))
                collectJson = true;
            if (collectJson)
                jsonAccum.AppendLine(line);
        });

        var (fullOutput, exitCode) = await RunStreamCaptureAsync(cli, args, inner, ct);

        progress.Report($"[benchmark exit code: {exitCode}]");

        // Prefer the cleanly-captured JSON; fall back to the full mixed output.
        var jsonText = jsonAccum.Length > 0 ? jsonAccum.ToString() : fullOutput;

        var result = new BlenderRunResult { DeviceType = deviceType, Version = version };
        result.Scenes.AddRange(ParseJsonOutput(jsonText, scenes));

        // If still no scores, dump the full output so it appears in the log
        if (result.CompositeScore == 0)
        {
            progress.Report("[WARNING] No scores parsed. Full raw output:");
            foreach (var line in fullOutput.Split('\n'))
            {
                var t = line.TrimEnd('\r');
                if (!string.IsNullOrWhiteSpace(t))
                    progress.Report("  " + t);
            }
        }

        return result;
    }

    // ── JSON output parsing ───────────────────────────────────────────────────

    // Blender benchmark CLI with --json emits one JSON object per scene line.
    // Supported formats:
    //   {"scene":"monster","stats":{"samples_per_minute":1234.5,...},...}   (older launcher)
    //   {"scene":"monster","result":{"samples_per_minute":1234.5,...},...}   (newer launcher)
    //   {"scene":"monster","samples_per_minute":1234.5,...}                  (bare)
    private static IEnumerable<BlenderSceneResult> ParseJsonOutput(string output, string[] expectedScenes)
    {
        var found = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // First pass: try parsing the entire output as a JSON array or object
        // (launcher 3.x emits a pretty-printed array after all scenes complete)
        TryParseJsonObject(output.Trim(), found);

        // Second pass: line-by-line for single-line JSON objects (older launchers)
        if (found.Count == 0)
        {
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("{") && !trimmed.StartsWith("[")) continue;
                TryParseJsonObject(trimmed, found);
            }
        }

        // Third pass: extract multi-line JSON objects by bracket depth
        if (found.Count == 0)
        {
            foreach (var json in ExtractJsonObjects(output))
                TryParseJsonObject(json, found);
        }

        // Yield results; if exact name not found, try a contains-match
        // (the CLI may use longer names like "Monster Lair" for the "monster" scene key)
        foreach (var name in expectedScenes)
        {
            double score = 0;
            if (!found.TryGetValue(name, out score))
            {
                // Contains-match, case-insensitive, pick the first hit
                foreach (var kv in found)
                {
                    if (kv.Key.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        score = kv.Value;
                        break;
                    }
                }
            }
            yield return new BlenderSceneResult(name, score);
        }
    }

    private static void TryParseJsonObject(string json, Dictionary<string, double> found)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root      = doc.RootElement;

            // If the root is an array (the launcher emits a JSON array after all scenes),
            // recurse into each element.
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    TryParseElement(el, found);
                return;
            }

            TryParseElement(root, found);
        }
        catch { }
    }

    private static void TryParseElement(JsonElement el, Dictionary<string, double> found)
    {
        var sceneName = TryGetSceneName(el);
        var spm       = TryGetSpm(el);
        if (sceneName != null && spm > 0)
            found[sceneName] = spm;
    }

    // Extracts complete top-level JSON objects from a potentially mixed-content string.
    private static IEnumerable<string> ExtractJsonObjects(string text)
    {
        var sb    = new System.Text.StringBuilder();
        int depth = 0;
        bool inStr = false;
        bool escape = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (escape) { escape = false; sb.Append(c); continue; }
            if (inStr)
            {
                if (c == '\\') { escape = true; sb.Append(c); continue; }
                if (c == '"')  { inStr  = false; }
                sb.Append(c);
                continue;
            }

            if (c == '"')  { inStr = true;  sb.Append(c); continue; }
            if (c == '{')
            {
                if (depth == 0) sb.Clear();
                depth++;
                sb.Append(c);
            }
            else if (c == '}')
            {
                sb.Append(c);
                if (depth > 0 && --depth == 0)
                    yield return sb.ToString();
            }
            else if (depth > 0)
            {
                sb.Append(c);
            }
        }
    }

    // Extracts the scene name from a result element. Handles two formats:
    //   {"scene": "monster", ...}                        — older launcher (bare string)
    //   {"scene": {"label": "monster", ...}, ...}        — launcher 3.x (nested object)
    private static string? TryGetSceneName(JsonElement el)
    {
        if (!el.TryGetProperty("scene", out var scene)) return null;

        if (scene.ValueKind == JsonValueKind.String)
            return scene.GetString();

        if (scene.ValueKind == JsonValueKind.Object &&
            scene.TryGetProperty("label", out var label) &&
            label.ValueKind == JsonValueKind.String)
            return label.GetString();

        return null;
    }

    private static double TryGetSpm(JsonElement root)
    {
        // Try root.stats.samples_per_minute  (older launcher format)
        if (root.TryGetProperty("stats", out var stats) &&
            stats.TryGetProperty("samples_per_minute", out var spm1) &&
            spm1.ValueKind == JsonValueKind.Number)
            return spm1.GetDouble();

        // Try root.result.samples_per_minute  (newer launcher format)
        if (root.TryGetProperty("result", out var result) &&
            result.TryGetProperty("samples_per_minute", out var spm2) &&
            spm2.ValueKind == JsonValueKind.Number)
            return spm2.GetDouble();

        // Try root.samples_per_minute directly
        if (root.TryGetProperty("samples_per_minute", out var spm3) &&
            spm3.ValueKind == JsonValueKind.Number)
            return spm3.GetDouble();

        return 0;
    }

    // ── Process helpers ───────────────────────────────────────────────────────

    private static async Task<string> RunAsync(string cli, string args, CancellationToken ct)
    {
        var (output, _) = await RunCaptureAsync(cli, args, ct);
        return output;
    }

    private static async Task<(string output, int exitCode)> RunCaptureAsync(
        string cli, string args, CancellationToken ct)
    {
        IProgress<string> noop = new Progress<string>(_ => { });
        return await RunStreamCaptureAsync(cli, args, noop, ct);
    }

    private static async Task RunStreamAsync(
        string cli, string args, IProgress<string> progress, CancellationToken ct)
        => await RunStreamCaptureAsync(cli, args, progress, ct);

    private static async Task<(string output, int exitCode)> RunStreamCaptureAsync(
        string cli, string args, IProgress<string> progress, CancellationToken ct)
    {
        var buf = new System.Text.StringBuilder();

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(cli, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                WorkingDirectory       = Path.GetDirectoryName(cli)!,
            },
            EnableRaisingEvents = true,
        };

        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            buf.AppendLine(e.Data);
            progress.Report(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            buf.AppendLine(e.Data);
            // Only surface non-trivial errors to the log
            if (!e.Data.StartsWith("time=", StringComparison.OrdinalIgnoreCase))
                progress.Report(e.Data);
        };

        using var killReg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);

        return (buf.ToString(), proc.ExitCode);
    }
}
