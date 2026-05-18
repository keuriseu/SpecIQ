using System.IO;

namespace SpecIQ;

/// <summary>
/// Lightweight file logger. Each benchmark writes to its own dated log file
/// under %AppData%\SpecIQ\logs\. Safe to call from any thread.
/// </summary>
public sealed class AppLog : IDisposable
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpecIQ", "logs");

    private readonly StreamWriter _writer;
    private readonly object       _lock = new();

    public string FilePath { get; }

    public AppLog(string name)
    {
        Directory.CreateDirectory(LogDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        FilePath = Path.Combine(LogDir, $"{name}_{stamp}.log");
        _writer  = new StreamWriter(FilePath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
        Write($"=== SpecIQ {name} log — {DateTime.Now:g} ===");
        Write($"Machine: {Environment.MachineName}  OS: {Environment.OSVersion}");
        Write("");
    }

    public void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (_lock)
        {
            try { _writer.WriteLine(line); } catch { }
        }
    }

    public void Write(Exception ex, string context = "")
    {
        var prefix = string.IsNullOrEmpty(context) ? "EXCEPTION" : $"EXCEPTION in {context}";
        Write($"{prefix}: {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException != null)
            Write($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        Write($"  Stack: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
    }

    /// <summary>Wraps a progress reporter so every message is also written to the log.</summary>
    public IProgress<string> Tee(IProgress<string> inner) => new TeeProgress(this, inner);

    public void Dispose()
    {
        Write("");
        Write("=== end of log ===");
        lock (_lock) { try { _writer.Dispose(); } catch { } }
    }

    // ── Housekeeping: keep only the last 20 log files ─────────────────────

    public static void Trim(string namePrefix, int keep = 20)
    {
        try
        {
            var files = Directory.GetFiles(LogDir, $"{namePrefix}_*.log")
                                 .OrderByDescending(f => f)
                                 .Skip(keep);
            foreach (var f in files)
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    private sealed class TeeProgress(AppLog log, IProgress<string> inner) : IProgress<string>
    {
        public void Report(string value)
        {
            log.Write(value);
            inner.Report(value);
        }
    }
}
