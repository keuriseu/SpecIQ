using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SpecIQ;

public enum AIBackend { Cpu, Gpu, Qnn }

public record AIBenchmarkResult(int FullPrecision, int HalfPrecision, int Quantized, AIBackend Backend);

public static class GeekbenchAIService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly string[] SearchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Geekbench AI 1"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Geekbench AI"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Geekbench AI 1"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Geekbench AI"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Geekbench AI 1"),
    ];

    public static string? FindInstalled()
    {
        // Check known install directories first
        var fromDir = SearchPaths
            .Select(d => Path.Combine(d, "banff.exe"))
            .FirstOrDefault(File.Exists);
        if (fromDir != null) return fromDir;

        // Fall back to PATH (where.exe)
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("where", "banff.exe")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true,
            });
            var line = proc?.StandardOutput.ReadLine()?.Trim();
            if (line != null && File.Exists(line)) return line;
        }
        catch { }

        return null;
    }

    public static string? GetInstalledVersion(string exePath)
    {
        try { return FileVersionInfo.GetVersionInfo(exePath).FileVersion?.Trim(); }
        catch { return null; }
    }

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
    /// Returns true when running on a Qualcomm Snapdragon ARM64 device.
    /// Checks both the CPU architecture and the processor name so x86 machines
    /// with "Snapdragon" in an unrelated string don't get flagged.
    /// </summary>
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

        // On ARM64 Windows, assume Snapdragon if WMI fails
        return true;
    }

    public static async Task<AIBenchmarkResult> RunAsync(
        string exePath,
        IProgress<string> progress,
        AIBackend backend,
        CancellationToken ct = default)
    {
        // Flag pattern mirrors Geekbench 6: --gpu OpenCL / --gpu Vulkan
        // QNN uses --npu QNN per the same convention for NPU backends
        var args = backend switch
        {
            AIBackend.Gpu => "--gpu OpenCL --no-upload",
            AIBackend.Qnn => "--npu QNN --no-upload",
            _             => "--no-upload",             // CPU
        };

        var psi = new ProcessStartInfo(exePath, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = backend == AIBackend.Cpu,
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
            progress.Report(line);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return ParseResult(lines, backend);
    }

    private static AIBenchmarkResult ParseResult(IEnumerable<string> lines, AIBackend backend)
    {
        int fp32 = 0, fp16 = 0, quant = 0;

        foreach (var line in lines)
        {
            var t = line.Trim();

            var m = Regex.Match(t, @"Single.Precision.Score\s+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) { fp32 = int.Parse(m.Groups[1].Value); continue; }

            m = Regex.Match(t, @"Half.Precision.Score\s+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) { fp16 = int.Parse(m.Groups[1].Value); continue; }

            m = Regex.Match(t, @"Quantized.Score\s+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) { quant = int.Parse(m.Groups[1].Value); continue; }
        }

        return new AIBenchmarkResult(fp32, fp16, quant, backend);
    }
}
