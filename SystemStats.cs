namespace SpecIQ;

/// <summary>
/// Thread-safe snapshot of the latest system stats, shared between
/// the WPF overlay and the HTTP dev server.
/// </summary>
internal static class SystemStats
{
    private static readonly object _lock = new();
    private static StatsSnapshot _current = new();

    internal static StatsSnapshot Snapshot
    {
        get { lock (_lock) return _current; }
        set { lock (_lock) _current = value; }
    }
}

internal record StatsSnapshot
{
    public string Time { get; init; } = "--:--";
    public string Date { get; init; } = "--";
    public int BatteryPct { get; init; } = -1;
    public bool BatteryCharging { get; init; }
    public int CpuPct { get; init; }
    public int RamPct { get; init; }
    public string RamUsed { get; init; } = "--";
    public string RamTotal { get; init; } = "--";
    public int GpuPct { get; init; }
    public int NpuPct { get; init; } = -1;  // -1 = unsupported
    public string NetworkName { get; init; } = "--";
    public string NetworkUp { get; init; } = "--";
    public string NetworkDown { get; init; } = "--";
}
