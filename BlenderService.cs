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

        var jsonAccum = new System.Text.StringBuilder();
        IProgress<string> inner = new Progress<string>(line =>
        {
            progress.Report(line);
            if (line.TrimStart().StartsWith("{"))
                jsonAccum.AppendLine(line);
        });

        var (fullOutput, _) = await RunStreamCaptureAsync(cli, args, inner, ct);

        // Combine inline JSON accumulator with full output for best-effort parsing
        var jsonText = jsonAccum.Length > 0 ? jsonAccum.ToString() : fullOutput;

        var result = new BlenderRunResult { DeviceType = deviceType, Version = version };
        result.Scenes.AddRange(ParseJsonOutput(jsonText, scenes));
        return result;
    }

    // ── JSON output parsing ───────────────────────────────────────────────────

    // Blender benchmark CLI with --json emits one JSON object per scene line.
    // Format (approx): {"scene":"monster","stats":{"samples_per_minute":1234.5,...},...}
    private static IEnumerable<BlenderSceneResult> ParseJsonOutput(string output, string[] expectedScenes)
    {
        var found = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("{")) continue;
            try
            {
                using var doc  = JsonDocument.Parse(trimmed);
                var root       = doc.RootElement;

                var sceneName  = TryGetString(root, "scene");
                var spm        = TryGetSpm(root);

                if (sceneName != null && spm > 0)
                    found[sceneName] = spm;
            }
            catch { }
        }

        // Yield found scenes, then zeros for missing ones
        foreach (var name in expectedScenes)
        {
            yield return new BlenderSceneResult(
                name,
                found.TryGetValue(name, out var s) ? s : 0);
        }
    }

    private static string? TryGetString(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private static double TryGetSpm(JsonElement root)
    {
        // Try root.stats.samples_per_minute
        if (root.TryGetProperty("stats", out var stats))
        {
            if (stats.TryGetProperty("samples_per_minute", out var spm))
                return spm.GetDouble();
        }
        // Try root.samples_per_minute directly
        if (root.TryGetProperty("samples_per_minute", out var spm2))
            return spm2.GetDouble();
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
