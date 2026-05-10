namespace SpecIQ;

/// <summary>
/// Thread-safe flag that signals when a benchmark is actively running.
/// When active, the overlay skips expensive GPU / NPU / Network / Thermal
/// updates so it does not compete with the benchmark process for resources.
/// </summary>
internal static class BenchmarkGuard
{
    private static volatile int _activeCount;

    /// <summary>True while at least one benchmark window is running a test.</summary>
    public static bool IsActive => _activeCount > 0;

    /// <summary>Call when a benchmark starts. Thread-safe; supports concurrent callers.</summary>
    public static void Begin() => Interlocked.Increment(ref _activeCount);

    /// <summary>Call in a finally block when a benchmark finishes. Thread-safe.</summary>
    public static void End()
    {
        if (_activeCount > 0) Interlocked.Decrement(ref _activeCount);
    }
}
