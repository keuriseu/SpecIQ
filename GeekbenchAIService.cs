using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace SpecIQ;

public record AIBenchmarkResult(int FullPrecision, int HalfPrecision, int Quantized, bool Gpu = false);

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

    public static async Task<AIBenchmarkResult> RunAsync(
        string exePath,
        IProgress<string> progress,
        bool gpu = false,
        CancellationToken ct = default)
    {
        var args = gpu ? "--gpu OpenCL --no-upload" : "--no-upload";

        var psi = new ProcessStartInfo(exePath, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = !gpu,
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

        return ParseResult(lines, gpu);
    }

    private static AIBenchmarkResult ParseResult(IEnumerable<string> lines, bool gpu)
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

        return new AIBenchmarkResult(fp32, fp16, quant, Gpu: gpu);
    }
}
