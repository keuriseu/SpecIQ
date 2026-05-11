using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using WinForms = System.Windows.Forms;

namespace SpecIQ;

public partial class MainWindow : Window
{
    // Frozen static brushes — created once, shared across all ticks
    private static readonly SolidColorBrush BrushGreen  = Freeze(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush BrushYellow = Freeze(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly SolidColorBrush BrushRed    = Freeze(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly SolidColorBrush BrushWhite  = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush BrushBlue   = Freeze(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly SolidColorBrush BrushPurple = Freeze(Color.FromRgb(0xC0, 0x84, 0xFC));
    private static readonly SolidColorBrush BrushOrange = Freeze(Color.FromRgb(0xFB, 0x92, 0x3C));
    private static readonly SolidColorBrush BrushPink   = Freeze(Color.FromRgb(0xF4, 0x72, 0xB6));
    private static readonly SolidColorBrush BrushDim        = Freeze(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush BrushYellowBg   = Freeze(Color.FromArgb(0x30, 0xFB, 0xBF, 0x24));
    private static readonly SolidColorBrush BrushClear      = Freeze(Color.FromArgb(0x00, 0x00, 0x00, 0x00));

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static SolidColorBrush LoadBrush(float pct, SolidColorBrush accent) =>
        pct > 80 ? BrushRed : pct > 50 ? BrushYellow : accent;

    // Returns the width of the track (parent Grid) so bar widths don't self-reference
    private static double TrackWidth(FrameworkElement bar) =>
        bar.Parent is FrameworkElement parent ? parent.ActualWidth : 0;

    private readonly DispatcherTimer _timer;
    private readonly PerformanceCounter _cpuCounter;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastNetworkCheck = DateTime.MinValue;
    private List<PerformanceCounter> _npuCounters = [];
    private bool _npuReady;
    private List<PerformanceCounter> _gpuCounters = [];
    private bool _gpuReady;

    // Cached values shared across update methods within a single tick
    private int _lastRamPct;
    private WinForms.PowerStatus? _lastPower;

    // Launcher
    private bool _launcherOpen;

    // Thermal
    private int  _lastTempC      = -1;
    private bool _thermalPolling;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ── Energy Saver notification ─────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    // Windows delivers battery-saver/energy-saver state changes via this GUID
    private static Guid GUID_POWER_SAVING_STATUS = new("E00958C0-C213-4ACE-AC77-FECCED2EEEA5");

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data;           // 0 = off, 1 = on
    }

    private const int WM_POWERBROADCAST    = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;

    private IntPtr _powerNotifHandle;
    private bool   _energySaverOn;

    public MainWindow()
    {
        InitializeComponent();

        _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
        _cpuCounter.NextValue();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += Timer_Tick;

        // Init GPU and NPU counters on background thread with 1s delay so primed values are valid
        Task.Run(async () =>
        {
            await Task.Delay(1000);
            InitGpuCounters();
            _gpuReady = true;
            InitNpuCounters();
            _npuReady = true;
        });

        // Pre-fetch battery capacity off the UI thread at startup
        Task.Run(ReadBatteryCapacity);
    }

    // ── UI Scale ──────────────────────────────────────────────────────────

    private static readonly double[] ScaleSteps = [0.7, 0.8, 0.9, 1.0, 1.1, 1.2, 1.3, 1.5];

    private void ApplyScale(double scale)
    {
        scale = Math.Clamp(scale, ScaleSteps[0], ScaleSteps[^1]);
        // Snap to nearest defined step
        scale = ScaleSteps.MinBy(s => Math.Abs(s - scale));
        SpecIQSettings.UiScale       = scale;
        UiScaleTransform.ScaleX      = scale;
        UiScaleTransform.ScaleY      = scale;
        ScaleText.Text               = $"{(int)Math.Round(scale * 100)}%";
        // Re-position after size change (SizeToContent recalculates on next layout pass)
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, PositionTopRight);
    }

    private void ScaleUp_Click(object sender, RoutedEventArgs e)
    {
        var next = ScaleSteps.FirstOrDefault(s => s > SpecIQSettings.UiScale + 0.01);
        if (next > 0) ApplyScale(next);
    }

    private void ScaleDown_Click(object sender, RoutedEventArgs e)
    {
        var prev = ScaleSteps.LastOrDefault(s => s < SpecIQSettings.UiScale - 0.01);
        if (prev > 0) ApplyScale(prev);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyScale(SpecIQSettings.UiScale);
        PositionTopRight();
        _energySaverOn = EnergyHelper.IsOn(); // best-effort initial state
        UpdateAll();
        _timer.Start();
        StartThermalPolling();

        // Hook WndProc and register for WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        _powerNotifHandle = RegisterPowerSettingNotification(hwnd, ref GUID_POWER_SAVING_STATUS, 0);
#if DEBUG
        DevServer.Start(5000);
#endif
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && (int)wParam == PBT_POWERSETTINGCHANGE)
        {
            var s = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (s.PowerSetting == GUID_POWER_SAVING_STATUS)
            {
                _energySaverOn = s.Data != 0;
                UpdateEnergySaver();
            }
        }
        return IntPtr.Zero;
    }

    private void PositionTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 16;
        Top  = workArea.Top  + 16;
    }

    public void ResetPosition() => PositionTopRight();

    // ── Launcher ──────────────────────────────────────────────────────────

    private void LauncherToggle_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_launcherOpen) HideLauncher();
        else               ShowLauncher();
    }

    private void ShowLauncher()
    {
        _launcherOpen = true;
        ContentPanel.Visibility  = Visibility.Collapsed;
        LauncherPanel.Visibility = Visibility.Visible;
        AppHelpers.FadeIn(LauncherPanel);
        LauncherToggle.Foreground = BrushWhite;
        // Restore full opacity so launcher buttons are clearly visible and clickable
        RootBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase() });
        // Mirror clock into the launcher header so it stays live
        LaunchTimeText.Text = TimeText.Text;
        LaunchDateText.Text = DateText.Text;
    }

    private void HideLauncher()
    {
        if (!_launcherOpen) return;
        _launcherOpen = false;
        LauncherPanel.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility  = Visibility.Visible;
        LauncherToggle.Foreground = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    }

    private void Launch_GB6(object sender, RoutedEventArgs e)      { HideLauncher(); App().ShowBenchmark();   }
    private void Launch_GBAI(object sender, RoutedEventArgs e)     { HideLauncher(); App().ShowGeekbenchAI(); }
    private void Launch_CB(object sender, RoutedEventArgs e)       { HideLauncher(); App().ShowCinebench();   }
    private void Launch_SP(object sender, RoutedEventArgs e)       { HideLauncher(); App().ShowSpeedometer(); }
    private void Launch_Procyon(object sender, RoutedEventArgs e)  { HideLauncher(); App().ShowProcyon();     }
    private void Launch_History(object sender, RoutedEventArgs e)  { HideLauncher(); App().ShowHistory();     }
    private void Launch_Snapshot(object sender, RoutedEventArgs e) { HideLauncher(); App().ShowSnapshot();    }
    private void Launch_ResetPos(object sender, RoutedEventArgs e) { HideLauncher(); ResetPosition();         }

    private static App App() => (App)System.Windows.Application.Current;

    // ── Battery focus mode ────────────────────────────────────────────────

    private bool _batteryFocusMode;
    private int _sessionStartPct = -1;
    private DateTime _drainSampleTime = DateTime.MinValue;
    private int _drainSamplePct = -1;
    private double _drainRatePerHour = double.NaN;
    private int _batteryDesignMwh = -1;
    private int _batteryFullMwh = -1;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void BatteryRow_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleBatteryFocus(true);
    }

    private void BatteryFocus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleBatteryFocus(false);
    }

    private void ToggleBatteryFocus(bool enterFocus)
    {
        _batteryFocusMode = enterFocus;
        ContentPanel.Visibility = enterFocus ? Visibility.Collapsed : Visibility.Visible;
        BatteryFocusPanel.Visibility = enterFocus ? Visibility.Visible : Visibility.Collapsed;
        RootBorder.MinWidth = enterFocus ? 140 : 200;

        if (enterFocus)
            UpdateBatteryFocus(_lastPower ?? WinForms.SystemInformation.PowerStatus);
    }

    private void ReadBatteryCapacity()
    {
        int design = -1, full = -1;
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData");
            foreach (System.Management.ManagementObject o in s.Get()) { design = Convert.ToInt32(o["DesignedCapacity"]); break; }
        }
        catch { }
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            foreach (System.Management.ManagementObject o in s.Get()) { full = Convert.ToInt32(o["FullChargedCapacity"]); break; }
        }
        catch { }
        Dispatcher.Invoke(() => { _batteryDesignMwh = design; _batteryFullMwh = full; });
    }

    private static int ClampBatteryPct(WinForms.PowerStatus power)
    {
        var pct = (int)(power.BatteryLifePercent * 100);
        return pct > 100 ? 100 : pct;
    }

    private void TrackDrainRate(int pct, bool charging)
    {
        if (!charging && pct >= 0)
        {
            if (_sessionStartPct < 0) _sessionStartPct = pct;

            if (_drainSamplePct < 0)
            {
                _drainSamplePct = pct;
                _drainSampleTime = DateTime.Now;
            }
            else if (pct < _drainSamplePct)
            {
                var elapsed = (DateTime.Now - _drainSampleTime).TotalHours;
                if (elapsed > 0)
                    _drainRatePerHour = (_drainSamplePct - pct) / elapsed;
                _drainSamplePct = pct;
                _drainSampleTime = DateTime.Now;
            }
            else if (double.IsNaN(_drainRatePerHour))
            {
                // No drop yet — estimate from elapsed time as a fallback
                var elapsed = (DateTime.Now - _drainSampleTime).TotalHours;
                if (elapsed >= 0.25) // at least 15 min of data
                    _drainRatePerHour = 0; // still 0% drop, so rate is effectively 0
            }
        }
        else
        {
            _drainSamplePct = -1;
            _drainRatePerHour = double.NaN;
        }
    }

    private void UpdateBatteryFocus(WinForms.PowerStatus power)
    {
        var pct = ClampBatteryPct(power);
        var charging = power.PowerLineStatus == WinForms.PowerLineStatus.Online;

        BatteryFocusPct.Text = pct < 0 ? "--" : $"{pct}%";
        BatteryFocusStatus.Text = charging ? "Charging" : "Battery";
        BatteryFocusPct.Foreground = charging ? BrushGreen : pct <= 20 ? BrushRed : BrushWhite;

        TrackDrainRate(pct, charging);

        BatteryFocusDrain.Text = charging ? "↑ Charging"
            : !double.IsNaN(_drainRatePerHour) ? $"↓ {_drainRatePerHour:F1}%/hr"
            : "-- %/hr";

        var secsLeft = power.BatteryLifeRemaining;
        if (secsLeft > 0)
        {
            var t = TimeSpan.FromSeconds(secsLeft);
            BatteryFocusTime.Text = t.Hours > 0 ? $"{t.Hours}h {t.Minutes:D2}m" : $"{t.Minutes}m";
        }
        else
        {
            BatteryFocusTime.Text = charging ? "Plugged in" : "--";
        }

        if (_sessionStartPct >= 0 && pct >= 0)
        {
            var delta = _sessionStartPct - pct;
            BatteryFocusSession.Text = delta == 0 ? "No change this session"
                : delta > 0 ? $"−{delta}% this session"
                : $"+{-delta}% this session";
        }
        else
        {
            BatteryFocusSession.Text = "-- this session";
        }

        BatteryFocusCapacity.Text = _batteryFullMwh > 0 ? $"{_batteryFullMwh / 1000.0:F1} Wh" : "--";

        if (_batteryDesignMwh > 0)
        {
            BatteryFocusDesign.Text = $"/ {_batteryDesignMwh / 1000.0:F1} Wh design";
            if (_batteryFullMwh > 0)
            {
                var health = Math.Clamp((int)Math.Round(_batteryFullMwh * 100.0 / _batteryDesignMwh), 0, 100);
                BatteryFocusHealth.Text = $"{health}% health";
                BatteryFocusHealth.Foreground = health >= 80 ? BrushGreen : health >= 60 ? BrushYellow : BrushRed;
            }
        }
        else
        {
            BatteryFocusDesign.Text = "";
            BatteryFocusHealth.Text = "";
        }
    }

    // ── Benchmark score flash ─────────────────────────────────────────────

    private DispatcherTimer? _scoreDismissTimer;

    public void ShowBenchmarkScore(BenchmarkResult result)
    {
        Dispatcher.Invoke(() =>
        {
            ScoreLabelA.Text = result.Gpu ? "OpenCL"      : "Single-Core";
            ScoreLabelB.Text = result.Gpu ? "Vulkan"       : "Multi-Core";
            ScoreSingle.Text = result.SingleCore > 0 ? $"{result.SingleCore:N0}" : "—";
            ScoreMulti.Text  = result.MultiCore  > 0 ? $"{result.MultiCore:N0}"  : "—";

            _batteryFocusMode = false;
            ContentPanel.Visibility    = Visibility.Collapsed;
            BatteryFocusPanel.Visibility = Visibility.Collapsed;
            ScorePanel.Visibility      = Visibility.Visible;
            RootBorder.MinWidth        = 180;

            ScorePanel.Opacity = 0;
            ScorePanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(400)) { EasingFunction = new QuadraticEase() });

            _scoreDismissTimer?.Stop();
            _scoreDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _scoreDismissTimer.Tick += (_, _) => DismissScore();
            _scoreDismissTimer.Start();
        });
    }

    private void ScorePanel_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DismissScore();
    }

    private void DismissScore()
    {
        _scoreDismissTimer?.Stop();
        ScorePanel.Visibility     = Visibility.Collapsed;
        ContentPanel.Visibility   = Visibility.Visible;
        RootBorder.MinWidth       = 200;
    }

    // ── Hover fade ────────────────────────────────────────────────────────

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_launcherOpen) return; // keep full opacity while launcher is visible
        RootBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase() });
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HideLauncher();
        RootBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new QuadraticEase() });
    }

    // ── Update loop ───────────────────────────────────────────────────────

    private void Timer_Tick(object? sender, EventArgs e) => UpdateAll();

    private void UpdateAll()
    {
        _lastPower = WinForms.SystemInformation.PowerStatus;

        if (_batteryFocusMode)
        {
            UpdateBatteryFocus(_lastPower);
            return;
        }

        UpdateDateTime();
        UpdateBattery(_lastPower);
        UpdateCpu();
        UpdateTrayTooltip();
        UpdateRam();

        // Skip expensive perf-counter and network reads while a benchmark is running
        // so SpecIQ does not compete for resources and skew results.
        if (!BenchmarkGuard.IsActive)
        {
            UpdateGpu();
            UpdateNpu();
            UpdateNetwork();
        }

        UpdatePowerMode();
        UpdateEnergySaver();
#if DEBUG
        PublishStats(_lastPower);
#endif
    }

#if DEBUG
    private static int ParsePct(string text) =>
        int.TryParse(text.TrimEnd('%'), out var v) ? v : 0;

    private void PublishStats(WinForms.PowerStatus power)
    {
        var batPct = ClampBatteryPct(power);
        var ramParts = RamText.Text.Split('/');

        SystemStats.Snapshot = new StatsSnapshot
        {
            Time = TimeText.Text,
            Date = DateText.Text,
            BatteryPct = batPct < 0 ? -1 : batPct,
            BatteryCharging = power.PowerLineStatus == WinForms.PowerLineStatus.Online,
            CpuPct = ParsePct(CpuText.Text),
            RamPct = _lastRamPct,
            RamUsed = ramParts.Length > 0 ? ramParts[0].Trim() : "--",
            RamTotal = ramParts.Length > 1 ? ramParts[1].Replace("GB", "").Trim() : "--",
            GpuPct = ParsePct(GpuText.Text),
            NpuPct = NpuText.Text == "N/A" ? -1 : ParsePct(NpuText.Text),
            NetworkName = NetworkName.Text,
            NetworkUp = NetworkUpText.Text.Replace("↑ ", ""),
            NetworkDown = NetworkDownText.Text.Replace("↓ ", ""),
        };
    }
#endif

    // ── Stat updaters ─────────────────────────────────────────────────────

    private void UpdateDateTime()
    {
        var now = DateTime.Now;
        TimeText.Text      = now.ToString("h:mm tt");
        DateText.Text      = now.ToString("ddd, MMM d");
        LaunchTimeText.Text = TimeText.Text;
        LaunchDateText.Text = DateText.Text;
    }

    private void UpdateBattery(WinForms.PowerStatus power)
    {
        var percent = ClampBatteryPct(power);

        if (percent < 0)
        {
            BatteryText.Text = "N/A";
            BatteryBar.Width = 0;
            return;
        }

        BatteryText.Text = $"{percent}%";
        BatteryBar.Width = TrackWidth(BatteryBar) * percent / 100.0;

        if (power.PowerLineStatus == WinForms.PowerLineStatus.Online)
        {
            BatteryBar.Background = BrushGreen;
            BatteryIcon.Text = "";
        }
        else if (percent <= 20)
        {
            BatteryBar.Background = BrushRed;
            BatteryIcon.Text = "";
        }
        else if (percent <= 50)
        {
            BatteryBar.Background = BrushYellow;
            BatteryIcon.Text = "";
        }
        else
        {
            BatteryBar.Background = BrushGreen;
            BatteryIcon.Text = "";
        }
    }

    // ── Thermal ───────────────────────────────────────────────────────────

    private void StartThermalPolling()
    {
        _thermalPolling = true;
        _ = Task.Run(async () =>
        {
            while (_thermalPolling)
            {
                // Skip WMI call during benchmark runs; use a longer sleep to yield resources.
                if (!BenchmarkGuard.IsActive)
                {
                    var temp = ThermalService.ReadCpuTempC();
                    await Dispatcher.InvokeAsync(() => { _lastTempC = temp; UpdateTemp(); });
                }
                await Task.Delay(BenchmarkGuard.IsActive ? 15_000 : 5_000);
            }
        });
    }

    private void UpdateTemp()
    {
        if (_lastTempC < 0)
        {
            TempText.Text = "N/A";
            TempBar.Width = 0;
            return;
        }

        TempText.Text = $"{_lastTempC}°C";
        // Map 0–100 °C to bar width; clamp so > 100 still fills fully
        var pct = Math.Clamp(_lastTempC, 0, 100);
        TempBar.Width      = TrackWidth(TempBar) * pct / 100.0;
        TempBar.Background = _lastTempC > 85 ? BrushRed
                           : _lastTempC > 70 ? BrushYellow
                           : BrushGreen;
    }

    private void UpdateCpu()
    {
        try
        {
            var pct = Math.Clamp(_cpuCounter.NextValue(), 0f, 100f);
            CpuText.Text = $"{pct:F0}%";
            CpuBar.Width = TrackWidth(CpuBar) * pct / 100.0;
            CpuBar.Background = LoadBrush(pct, BrushBlue);
        }
        catch { CpuText.Text = "N/A"; }
    }

    private void UpdateTrayTooltip()
    {
        try
        {
            if (System.Windows.Application.Current is not App app) return;
            var cpuPct  = int.TryParse(CpuText.Text.TrimEnd('%'), out var c) ? c : -1;
            var battPct = _lastPower is { } p ? ClampBatteryPct(p) : -1;
            app.UpdateTrayTooltip(cpuPct, battPct);
        }
        catch { }
    }

    private void UpdateRam()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem)) return;

        var totalGb = mem.ullTotalPhys / (1024.0 * 1024 * 1024);
        var usedGb  = (mem.ullTotalPhys - mem.ullAvailPhys) / (1024.0 * 1024 * 1024);
        _lastRamPct = (int)mem.dwMemoryLoad;

        RamText.Text = $"{usedGb:F1}/{totalGb:F0} GB";
        RamBar.Width = TrackWidth(RamBar) * _lastRamPct / 100.0;
        RamBar.Background = LoadBrush(_lastRamPct, BrushPurple);
    }

    private void InitGpuCounters()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            _gpuCounters = category.GetInstanceNames()
                .Where(n => n.Contains("engtype_3D") || n.Contains("engtype_Graphics"))
                .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true))
                .ToList();
            foreach (var c in _gpuCounters) c.NextValue();
        }
        catch { _gpuCounters = []; }
    }

    private void UpdateGpu()
    {
        if (!_gpuReady || _gpuCounters.Count == 0)
        {
            GpuText.Text = _gpuReady ? "N/A" : "--";
            GpuBar.Width = 0;
            return;
        }

        try
        {
            float total = 0;
            foreach (var c in _gpuCounters) total += c.NextValue();
            total = Math.Clamp(total, 0f, 100f);

            GpuText.Text = $"{total:F0}%";
            GpuBar.Width = TrackWidth(GpuBar) * total / 100.0;
            GpuBar.Background = LoadBrush(total, BrushOrange);
        }
        catch
        {
            foreach (var c in _gpuCounters) c.Dispose();
            _gpuReady = false;
            Task.Run(async () => { await Task.Delay(1000); InitGpuCounters(); _gpuReady = true; });
            GpuText.Text = "0%";
            GpuBar.Width = 0;
        }
    }

    private static readonly (string Category, string Counter)[] NpuCandidates =
    [
        ("NPU",                  "Utilization Percentage"),
        ("NPU Engine",           "Utilization Percentage"),
        ("Neural Processing Unit", "% Utilization"),
        ("Qualcomm NPU",         "Utilization Percentage"),
        ("Hexagon NPU",          "Utilization Percentage"),
        ("Intel NPU",            "% NPU Utilization"),
        ("Intel(R) AI Boost",    "% NPU Utilization"),
    ];

    private void InitNpuCounters()
    {
        foreach (var (category, counterName) in NpuCandidates)
        {
            try
            {
                if (!PerformanceCounterCategory.Exists(category)) continue;

                var cat = new PerformanceCounterCategory(category);
                var counters = cat.GetInstanceNames()
                    .Select(instance => new PerformanceCounter(category, counterName, instance, true))
                    .ToList();

                if (counters.Count == 0) continue;

                // Prime values so first real read isn't zero
                foreach (var c in counters) c.NextValue();
                _npuCounters = counters;
                return;
            }
            catch { }
        }
    }

    private void UpdateNpu()
    {
        if (!_npuReady || _npuCounters.Count == 0)
        {
            NpuText.Text = _npuReady ? "N/A" : "--";
            NpuBar.Width = 0;
            return;
        }

        try
        {
            float total = 0;
            foreach (var c in _npuCounters) total += c.NextValue();
            total = Math.Clamp(total, 0f, 100f);

            NpuText.Text = $"{total:F0}%";
            NpuBar.Width = TrackWidth(NpuBar) * total / 100.0;
            NpuBar.Background = LoadBrush(total, BrushPink);
        }
        catch
        {
            foreach (var c in _npuCounters) c.Dispose();
            _npuReady = false;
            Task.Run(async () => { await Task.Delay(1000); InitNpuCounters(); _npuReady = true; });
            NpuText.Text = "0%";
            NpuBar.Width = 0;
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid pEffectiveOverlayPolicyGuid);

    private void UpdatePowerMode()
    {
        AcModeText.Text = ReadPowerMode(ac: true);
        DcModeText.Text = ReadPowerMode(ac: false);

        var pluggedIn = _lastPower?.PowerLineStatus == WinForms.PowerLineStatus.Online;

        // Active row: full brightness + blue dot; inactive: dimmed + hidden dot
        AcModeText.Foreground        = pluggedIn ? BrushWhite : BrushDim;
        AcActiveIndicator.Visibility = pluggedIn ? Visibility.Visible : Visibility.Hidden;
        DcModeText.Foreground        = pluggedIn ? BrushDim : BrushWhite;
        DcActiveIndicator.Visibility = pluggedIn ? Visibility.Hidden : Visibility.Visible;
    }

    private static string ReadPowerMode(bool ac)
    {
        // Correct value names confirmed from registry inspection
        var valueName = ac ? "ActiveOverlayAcPowerScheme" : "ActiveOverlayDcPowerScheme";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            var val = key?.GetValue(valueName);

            Guid guid;
            if (val is string s && Guid.TryParse(s, out var parsed))
                guid = parsed;
            else if (val is byte[] bytes && bytes.Length == 16)
                guid = new Guid(bytes);
            else
                return "Balanced";

            return PowerModeLabel(guid);
        }
        catch { return "Balanced"; }
    }

    private static string PowerModeLabel(Guid guid) =>
        guid.ToString().ToLowerInvariant() switch
        {
            "ded574b5-45a0-4f42-8737-46345c09c238" => "Best Performance",
            "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => "Better Performance",
            "00000000-0000-0000-0000-000000000000" => "Balanced",
            "961cc777-2547-4f9d-8174-7d86181b8a7a" => "Best Efficiency",
            _                                      => "Balanced",
        };

    private void UpdateEnergySaver()
    {
        if (_energySaverOn)
        {
            EnergySaverText.Text        = "ON";
            EnergySaverText.Foreground  = BrushYellow;
            EnergySaverBadge.Background = BrushYellowBg;
        }
        else
        {
            EnergySaverText.Text        = "OFF";
            EnergySaverText.Foreground  = BrushDim;
            EnergySaverBadge.Background = BrushClear;
        }
    }

    private void UpdateNetwork()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            if (interfaces.Count == 0)
            {
                NetworkName.Text = "Disconnected";
                NetworkIcon.Text = "";
                NetworkUpText.Text = "";
                NetworkDownText.Text = "";
                return;
            }

            var primary = interfaces.OrderByDescending(n => n.GetIPv4Statistics().BytesReceived).First();
            NetworkName.Text = primary.Name.Length > 18 ? primary.Name[..18] + "..." : primary.Name;
            NetworkIcon.Text = primary.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "" : "";

            var stats = primary.GetIPv4Statistics();
            var now = DateTime.Now;

            if (_lastNetworkCheck != DateTime.MinValue)
            {
                var elapsed = (now - _lastNetworkCheck).TotalSeconds;
                if (elapsed > 0)
                {
                    NetworkDownText.Text = $"↓ {FormatSpeed((stats.BytesReceived - _lastBytesReceived) / elapsed)}";
                    NetworkUpText.Text   = $"↑ {FormatSpeed((stats.BytesSent - _lastBytesSent) / elapsed)}";
                }
            }

            _lastBytesReceived = stats.BytesReceived;
            _lastBytesSent = stats.BytesSent;
            _lastNetworkCheck = now;
        }
        catch { NetworkName.Text = "Error"; }
    }

    private static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond >= 1_000_000 ? $"{bytesPerSecond / 1_000_000:F1} MB/s" :
        bytesPerSecond >= 1_000     ? $"{bytesPerSecond / 1_000:F0} KB/s" :
                                      $"{bytesPerSecond:F0} B/s";

    protected override void OnClosed(EventArgs e)
    {
        _thermalPolling = false;
        _timer.Stop();
        if (_powerNotifHandle != IntPtr.Zero) UnregisterPowerSettingNotification(_powerNotifHandle);
        _cpuCounter.Dispose();
        foreach (var c in _gpuCounters) c.Dispose();
        foreach (var c in _npuCounters) c.Dispose();
#if DEBUG
        DevServer.Stop();
#endif
        base.OnClosed(e);
    }
}
