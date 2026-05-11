using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Management;
using System.Text.RegularExpressions;

namespace SpecIQ;

public enum AIBackend { Cpu, Gpu, Qnn }

public record AIEntry(string Framework, int FrameworkId, string Backend, int BackendId, string Device, int DeviceId);

public record AIBenchmarkResult(int FullPrecision, int HalfPrecision, int Quantized, AIBackend Backend);

public static class GeekbenchAIService
{
    private static readonly string[] SearchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Geekbench AI"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Geekbench AI 1"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Geekbench AI"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Geekbench AI 1"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Geekbench AI 1"),
    ];

    // On ARM64, prefer the native banff_aarch64.exe — the x64 banff.exe runs under emulation
    // and its cpuinfo library crashes on newer Snapdragon chips (X2E and later).
    private static string BanffExeName =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "banff_aarch64.exe"
            : "banff.exe";

    public static string? FindInstalled()
    {
        if (SpecIQSettings.BanffPath is { Length: > 0 } cached && File.Exists(cached))
        {
            // If cached path is banff.exe but aarch64 build exists next to it, upgrade silently.
            if (RuntimeInformation.OSArchitecture == Architecture.Arm64 &&
                !cached.EndsWith("banff_aarch64.exe", StringComparison.OrdinalIgnoreCase))
            {
                var aarch64 = Path.Combine(Path.GetDirectoryName(cached)!, "banff_aarch64.exe");
                if (File.Exists(aarch64)) { SpecIQSettings.BanffPath = aarch64; return aarch64; }
            }
            return cached;
        }

        // Prefer the architecture-native executable; fall back to banff.exe if not present.
        var exeNames = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? new[] { "banff_aarch64.exe", "banff.exe" }
            : new[] { "banff.exe" };

        foreach (var exe in exeNames)
        {
            var fromDir = SearchPaths
                .Select(d => Path.Combine(d, exe))
                .FirstOrDefault(File.Exists);
            if (fromDir != null) { SpecIQSettings.BanffPath = fromDir; return fromDir; }
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo("where", BanffExeName)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true,
            });
            var line = proc?.StandardOutput.ReadLine()?.Trim();
            // Only trust paths inside standard install locations to defend against PATH poisoning.
            if (line != null && File.Exists(line) && AppHelpers.IsAllowedExePath(line))
            {
                SpecIQSettings.BanffPath = line;
                return line;
            }
        }
        catch { }

        return null;
    }

    public static string? GetInstalledVersion(string exePath)
    {
        try { return FileVersionInfo.GetVersionInfo(exePath).FileVersion?.Trim(); }
        catch { return null; }
    }

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<string?> GetLatestVersionAsync()
    {
        try
        {
            var html = await Http.GetStringAsync("https://www.geekbench.com/ai/download/windows/");
            var m = Regex.Match(html, @"GeekbenchAI-(\d+\.\d+\.\d+)-Windows(?:ARM64)?Setup\.exe");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Runs banff --ai-list and returns all available framework/backend/device combinations.
    /// Times out after 8 seconds and returns an empty list so the UI never hangs.
    /// </summary>
    private static readonly string DebugDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpecIQ");

    public static async Task<List<AIEntry>> ListAvailableAsync(string exePath)
    {
        var entries = new List<AIEntry>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string rawOutput = "";
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(exePath, "--ai-list")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                WorkingDirectory       = Path.GetDirectoryName(exePath)!,
            })!;

            // Read both streams in parallel to avoid deadlock
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            rawOutput = stdoutTask.Result + stderrTask.Result;
            foreach (var line in rawOutput.Split('\n'))
            {
                var t = line.Trim();
                // Skip separator (+---+) and header (| Framework | ID |) rows
                if (!t.StartsWith('|') || t.Contains("Framework") || t.Contains("---")) continue;

                var cols = t.Split('|')
                            .Select(c => c.Trim())
                            .Where(c => c.Length > 0)
                            .ToArray();
                if (cols.Length < 6) continue;
                if (!int.TryParse(cols[1], out var fwId))  continue;
                if (!int.TryParse(cols[3], out var beId))  continue;
                if (!int.TryParse(cols[5], out var devId)) continue;

                entries.Add(new AIEntry(cols[0], fwId, cols[2], beId, cols[4], devId));
            }
        }
        catch (Exception ex)
        {
            rawOutput += $"\n[exception: {ex.Message}]";
        }
        finally
        {
            // Always save raw output so we can diagnose categorization misses
            try
            {
                Directory.CreateDirectory(DebugDir);
                File.WriteAllText(Path.Combine(DebugDir, "banff-ai-list.txt"), rawOutput);
            }
            catch { }
        }
        return entries;
    }


    /// <summary>
    /// Maps an AIEntry to the high-level CPU / GPU / NPU category used by the UI.
    /// </summary>
    public static AIBackend CategorizeEntry(AIEntry e)
    {
        if (e.Framework.Equals("QNN",      StringComparison.OrdinalIgnoreCase) ||
            e.Backend.Equals("HTP",        StringComparison.OrdinalIgnoreCase) ||
            e.Backend.Equals("NPU",        StringComparison.OrdinalIgnoreCase))
            return AIBackend.Qnn;

        if (e.Backend.Contains("GPU",      StringComparison.OrdinalIgnoreCase) ||
            e.Framework.Equals("DirectML", StringComparison.OrdinalIgnoreCase) ||
            e.Framework.Equals("OpenCL",   StringComparison.OrdinalIgnoreCase) ||
            e.Framework.Equals("Vulkan",   StringComparison.OrdinalIgnoreCase) ||
            e.Framework.Equals("CUDA",     StringComparison.OrdinalIgnoreCase))
            return AIBackend.Gpu;

        return AIBackend.Cpu;
    }

    /// <summary>
    /// Human-readable label for a discovered entry (shown in the result header).
    /// </summary>
    public static string EntryLabel(AIEntry e) => CategorizeEntry(e) switch
    {
        AIBackend.Qnn => $"QNN — Snapdragon NPU",
        AIBackend.Gpu => $"{e.Framework} — {e.Backend}",
        _             => $"{e.Framework} — CPU",
    };

    // Sentinel: when --ai-list fails, run banff with no framework/backend flags (uses its default)
    public static readonly AIEntry DefaultEntry = new("Default", -1, "CPU", -1, "", -1);

    // Fixed entry for Snapdragon NPU — uses the framework name directly since --ai-list may time out
    public static readonly AIEntry QnnEntry = new("QNN", -2, "NPU", -1, "Snapdragon NPU", -1);

    /// <summary>Returns true when running on a Qualcomm Snapdragon ARM64 device.</summary>
    public static bool IsSnapdragonDevice()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.Arm64) return false;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                if (name.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Qualcomm",   StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return true; // ARM64 Windows — assume Snapdragon if WMI fails
    }

    public static async Task<AIBenchmarkResult> RunAsync(
        string exePath,
        IProgress<string> progress,
        AIEntry entry,
        CancellationToken ct = default)
    {
        var args = entry.FrameworkId switch
        {
            -2 => "--ai --ai-framework QNN --no-upload",           // Snapdragon NPU by name
            -1 => "--ai --no-upload",                              // banff default (CPU)
            _  => $"--ai --ai-framework {entry.FrameworkId} --ai-backend {entry.BackendId} --ai-device {entry.DeviceId} --no-upload",
        };

        var psi = new ProcessStartInfo(exePath, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            WorkingDirectory       = Path.GetDirectoryName(exePath)!,
        };

        using var proc = new Process { StartInfo = psi };
        var lines = new List<string>();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } line) return;
            lines.Add(line);
            progress.Report(line.Trim());
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data?.Trim() is not { Length: > 0 } line) return;
            lines.Add(line);
            progress.Report("[err] " + line);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(45));

        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Benchmark timed out after 45 minutes.");
        }

        progress.Report($"[exit {proc.ExitCode}]");

        if (proc.ExitCode != 0)
            throw new Exception($"Geekbench AI exited with code {proc.ExitCode}.");

        return ParseResult(lines, CategorizeEntry(entry));
    }

    private static AIBenchmarkResult ParseResult(IEnumerable<string> lines, AIBackend backend)
    {
        int fp32 = 0, fp16 = 0, quant = 0;

        foreach (var line in lines)
        {
            var t = line.Trim();

            var m = Regex.Match(t, @"Single.Precision.Score\D+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) { fp32 = int.Parse(m.Groups[1].Value); continue; }

            m = Regex.Match(t, @"Half.Precision.Score\D+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) { fp16 = int.Parse(m.Groups[1].Value); continue; }

            m = Regex.Match(t, @"Quantized.Score\D+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) { quant = int.Parse(m.Groups[1].Value); continue; }
        }

        return new AIBenchmarkResult(fp32, fp16, quant, backend);
    }
}

public class AIBenchmarkSavedResult
{
    public int    FullPrecision { get; set; }
    public int    HalfPrecision { get; set; }
    public int    Quantized     { get; set; }
    public string Backend       { get; set; } = "";
    public string MachineName   { get; set; } = Environment.MachineName;
    public string SavedAt       { get; set; } = DateTime.Now.ToString("o");

    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "geekbench-ai.json");

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            System.IO.File.WriteAllText(FilePath,
                System.Text.Json.JsonSerializer.Serialize(this, AppHelpers.JsonOpts));
        }
        catch { }
    }

    public static AIBenchmarkSavedResult? Load()
    {
        try
        {
            return System.IO.File.Exists(FilePath)
                ? System.Text.Json.JsonSerializer.Deserialize<AIBenchmarkSavedResult>(
                    System.IO.File.ReadAllText(FilePath), AppHelpers.JsonOpts)
                : null;
        }
        catch { return null; }
    }
}
