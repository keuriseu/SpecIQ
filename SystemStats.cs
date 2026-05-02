namespace SpecIQ;

/// <summary>
/// Thread-safe snapshot of the latest system stats, shared between
/// the WPF overlay and the HTTP dev server.
/// </summary>
internal static class SystemStats
{
    private static volatile StatsSnapshot _current = new();

    internal static StatsSnapshot Snapshot
    {
        get => _current;
        set => _current = value;
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
