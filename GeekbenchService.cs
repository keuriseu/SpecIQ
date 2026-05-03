using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SpecIQ;

public record GeekbenchInfo(
    string? InstalledPath,
    string? InstalledVersion,
    string? LatestVersion,
    string? DownloadUrl)
{
    public bool IsInstalled    => InstalledPath != null;
    public bool UpdateAvailable => IsInstalled && LatestVersion != null && InstalledVersion != LatestVersion;

    public string StatusText => !IsInstalled           ? "Not installed"
        : UpdateAvailable                              ? $"v{InstalledVersion}  ·  v{LatestVersion} available"
        : InstalledVersion != null                     ? $"v{InstalledVersion}  ·  Up to date"
                                                       : "Installed";
}

public record BenchmarkResult(int SingleCore, int MultiCore, string? ResultUrl, bool Gpu = false);

public record RundownProgress(int Iteration, string StatusLine, RundownEntry? Completed = null);

public static class GeekbenchService
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" } }
    };

    private static readonly string[] SearchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),   "Geekbench 6"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Geekbench 6"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Geekbench 6"),
    ];

    public static string? FindInstalled() =>
        SearchPaths.Select(d => Path.Combine(d, "geekbench6.exe")).FirstOrDefault(File.Exists);

    public static string? GetInstalledVersion(string exePath)
    {
        try { return FileVersionInfo.GetVersionInfo(exePath).FileVersion?.Trim(); }
        catch { return null; }
    }

    public static async Task<GeekbenchInfo> CheckAsync()
    {
        var exePath = FindInstalled();
        var installedVersion = exePath != null ? GetInstalledVersion(exePath) : null;

        string? latestVersion = null;
        string? downloadUrl   = null;

        try
        {
            // Try to read the download page for the latest version
            var html = await Http.GetStringAsync("https://www.geekbench.com/download/windows/");

            // Match CDN href directly: Geekbench-6.7.1-WindowsSetup.exe or WindowsARM64Setup.exe
            var match = Regex.Match(html, @"Geekbench-(\d+\.\d+\.\d+)-Windows(?:ARM64)?Setup\.exe");
            if (!match.Success)
                // Fallback: match text like "Geekbench 6.7.1 for Windows"
                match = Regex.Match(html, @"Geekbench[- ](\d+\.\d+\.\d+)[- ](?:for )?Windows");

            if (match.Success)
            {
                latestVersion = match.Groups[1].Value;
                downloadUrl   = BuildDownloadUrl(latestVersion);
            }
        }
        catch { /* version check is best-effort */ }

        return new GeekbenchInfo(exePath, installedVersion, latestVersion, downloadUrl);
    }

    private static string BuildDownloadUrl(string version)
    {
        var isArm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        var suffix = isArm ? "WindowsARM64Setup" : "WindowsSetup";
        return $"https://cdn.geekbench.com/Geekbench-{version}-{suffix}.exe";
    }

    public static async Task DownloadAndInstallAsync(
        string url,
        IProgress<(int Percent, string Status)> progress,
        CancellationToken ct = default)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "GeekbenchSetup.exe");

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var download = await response.Content.ReadAsStreamAsync(ct);
        await using var file     = File.Create(tempFile);

        var buffer     = new byte[81920];
        long received  = 0;
        int  read;

        while ((read = await download.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            var pct = total > 0 ? (int)(received * 100 / total) : -1;
            var mb  = received / 1_048_576.0;
            progress.Report((pct, $"Downloading…  {mb:F0} MB"));
        }
        await file.FlushAsync(ct);
        file.Close();

        progress.Report((100, "Installing…"));

        var psi  = new ProcessStartInfo(tempFile, "/S") { UseShellExecute = true, Verb = "runas" };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Installer did not start.");
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Installer exited with code {proc.ExitCode}.");
    }

    private const string LicenseEmail = "geekbench@qualcomm.com";
    private const string LicenseKey   = "ONGAL-XV7U5-ICW2I-XB67X-6HERZ-KFW2D-AXC7V-CVVMU-7Y6QI";

    private static async Task ApplyLicenseAsync(string exePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath, $"--unlock {LicenseEmail} {LicenseKey}")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start Geekbench for licensing.");
        await proc.WaitForExitAsync(ct);
        // Non-zero exit = bad key or network failure; surface to caller
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"License activation failed (exit {proc.ExitCode}).");
    }

    public static async Task<BenchmarkResult> RunAsync(
        string exePath,
        IProgress<string> progress,
        bool gpu = false,
        CancellationToken ct = default)
    {
        await ApplyLicenseAsync(exePath, ct);
        return await RunSingleAsync(exePath, progress, gpu, ct);
    }

    public static async Task RundownAsync(
        string exePath,
        string? geekbenchVersion,
        bool gpu,
        bool stress,
        RundownResult result,
        DateTime startTime,
        IProgress<RundownProgress> progress,
        CancellationToken ct)
    {
        // Snapshot conditions at start
        var startPower = System.Windows.Forms.SystemInformation.PowerStatus;
        result.StartBatteryPct  = (int)Math.Clamp(startPower.BatteryLifePercent * 100, 0, 100);
        result.GeekbenchVersion = geekbenchVersion;

        await ApplyLicenseAsync(exePath, ct);

        while (!ct.IsCancellationRequested)
        {
            var power      = System.Windows.Forms.SystemInformation.PowerStatus;
            var batteryPct = (int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100);

            // Stop at 3% — ensure results are safely written before power cuts out
            if (batteryPct is >= 0 and <= 3) break;

            var iteration = result.Entries.Count + 1;
            progress.Report(new RundownProgress(iteration, $"Starting iteration {iteration}…"));

            RundownEntry entry;

            if (stress)
            {
                var lineProgress = new Progress<string>(line =>
                    progress.Report(new RundownProgress(iteration, line)));
                var (cpu, gpuResult) = await RunStressSingleAsync(exePath, lineProgress, ct);
                var elapsed = (int)(DateTime.Now - startTime).TotalSeconds;
                entry = new RundownEntry(iteration,
                    cpu.SingleCore, cpu.MultiCore,
                    batteryPct, elapsed,
                    gpuResult.SingleCore, gpuResult.MultiCore);
            }
            else
            {
                var lineProgress = new Progress<string>(line =>
                    progress.Report(new RundownProgress(iteration, line)));
                var bench   = await RunSingleAsync(exePath, lineProgress, gpu, ct);
                var elapsed = (int)(DateTime.Now - startTime).TotalSeconds;
                entry = new RundownEntry(iteration, bench.SingleCore, bench.MultiCore, batteryPct, elapsed);
            }

            result.Entries.Add(entry);
            result.Save();

            progress.Report(new RundownProgress(iteration, "", entry));
        }
    }

    private static async Task<(BenchmarkResult Cpu, BenchmarkResult Gpu)> RunStressSingleAsync(
        string exePath,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var cpuProgress = new Progress<string>(line => progress.Report($"[CPU] {line}"));
        var gpuProgress = new Progress<string>(line => progress.Report($"[GPU] {line}"));

        var cpuTask = RunSingleAsync(exePath, cpuProgress, gpu: false, ct);
        var gpuTask = RunSingleAsync(exePath, gpuProgress, gpu: true,  ct);

        await Task.WhenAll(cpuTask, gpuTask);
        return (cpuTask.Result, gpuTask.Result);
    }

    private static async Task<BenchmarkResult> RunSingleAsync(
        string exePath,
        IProgress<string> progress,
        bool gpu,
        CancellationToken ct)
    {
        var args = gpu ? "--gpu --no-upload" : "--cpu --no-upload";
        var psi = new ProcessStartInfo(exePath, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var lines = new List<string>();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } line) return;
            lines.Add(line);
            var trimmed = line.Trim();
            if (trimmed.Length > 0) progress.Report(trimmed);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data?.Trim() is { Length: > 0 } line) progress.Report(line);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return ParseResult(lines, gpu);
    }

    private static BenchmarkResult ParseResult(IEnumerable<string> lines, bool gpu)
    {
        int    single = 0, multi = 0;
        string? url   = null;

        foreach (var line in lines)
        {
            var t = line.Trim();

            // CPU scores
            var m = Regex.Match(t, @"Single-Core Score\s+(\d+)");
            if (m.Success) { single = int.Parse(m.Groups[1].Value); continue; }

            m = Regex.Match(t, @"Multi-Core Score\s+(\d+)");
            if (m.Success) { multi = int.Parse(m.Groups[1].Value); continue; }

            // GPU scores: OpenCL → single slot, Vulkan/Metal → multi slot
            m = Regex.Match(t, @"OpenCL Score\s+(\d+)");
            if (m.Success) { single = int.Parse(m.Groups[1].Value); continue; }

            m = Regex.Match(t, @"(?:Vulkan|Metal) Score\s+(\d+)");
            if (m.Success) { multi = int.Parse(m.Groups[1].Value); continue; }

            if (t.StartsWith("https://browser.geekbench.com/")) url = t;
        }

        return new BenchmarkResult(single, multi, url, Gpu: gpu);
    }
}
