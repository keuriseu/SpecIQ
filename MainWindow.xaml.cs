using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace SpecIQ;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly PerformanceCounter _cpuCounter;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastNetworkCheck = DateTime.MinValue;
    private readonly double _barMaxWidth = 120;
    private string? _npuCategory;
    private string? _npuCounterName;
    private List<PerformanceCounter> _gpuCounters = [];

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

    public MainWindow()
    {
        InitializeComponent();

        _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
        _cpuCounter.NextValue(); // Prime the counter

        DetectNpuCounter();
        InitGpuCounters();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _timer.Tick += Timer_Tick;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionTopRight();
        UpdateAll();
        _timer.Start();
#if DEBUG
        DevServer.Start(5000);
#endif
    }

    private void PositionTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 16;
        Top = workArea.Top + 16;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var animation = new DoubleAnimation(0.15, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase()
        };
        RootBorder.BeginAnimation(OpacityProperty, animation);
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var animation = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new QuadraticEase()
        };
        RootBorder.BeginAnimation(OpacityProperty, animation);
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        UpdateAll();
    }

    private void UpdateAll()
    {
        UpdateDateTime();
        UpdateBattery();
        UpdateCpu();
        UpdateRam();
        UpdateGpu();
        UpdateNpu();
        UpdateNetwork();
#if DEBUG
        PublishStats();
#endif
    }

#if DEBUG
    private void PublishStats()
    {
        var power = WinForms.SystemInformation.PowerStatus;
        var batPct = (int)(power.BatteryLifePercent * 100);

        var ramParts = RamText.Text.Split('/');
        var ramUsed = ramParts.Length > 0 ? ramParts[0].Trim() : "--";
        var ramTotal = ramParts.Length > 1 ? ramParts[1].Replace("GB", "").Trim() : "--";

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        var ramPct = GlobalMemoryStatusEx(ref mem) ? (int)mem.dwMemoryLoad : 0;

        SystemStats.Snapshot = new StatsSnapshot
        {
            Time = TimeText.Text,
            Date = DateText.Text,
            BatteryPct = batPct > 100 || batPct < 0 ? -1 : batPct,
            BatteryCharging = power.PowerLineStatus == WinForms.PowerLineStatus.Online,
            CpuPct = int.TryParse(CpuText.Text.TrimEnd('%'), out var c) ? c : 0,
            RamPct = ramPct,
            RamUsed = ramUsed,
            RamTotal = ramTotal,
            GpuPct = int.TryParse(GpuText.Text.TrimEnd('%'), out var g) ? g : 0,
            NpuPct = NpuText.Text == "N/A" ? -1
                     : int.TryParse(NpuText.Text.TrimEnd('%'), out var n) ? n : -1,
            NetworkName = NetworkName.Text,
            NetworkUp = NetworkUpText.Text.Replace("↑ ", ""),
            NetworkDown = NetworkDownText.Text.Replace("↓ ", ""),
        };
    }
#endif

    private void UpdateDateTime()
    {
        var now = DateTime.Now;
        TimeText.Text = now.ToString("h:mm tt");
        DateText.Text = now.ToString("ddd, MMM d");
    }

    private void UpdateBattery()
    {
        var power = WinForms.SystemInformation.PowerStatus;
        var percent = (int)(power.BatteryLifePercent * 100);

        if (percent > 100) percent = 100;
        if (percent < 0)
        {
            BatteryText.Text = "N/A";
            BatteryBar.Width = 0;
            return;
        }

        BatteryText.Text = $"{percent}%";
        BatteryBar.Width = _barMaxWidth * percent / 100.0;

        // Color based on level
        if (power.PowerLineStatus == WinForms.PowerLineStatus.Online)
        {
            BatteryBar.Background = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)); // Green
            BatteryIcon.Text = "\uEA93"; // Charging icon
        }
        else if (percent <= 20)
        {
            BatteryBar.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // Red
            BatteryIcon.Text = "\uE996";
        }
        else if (percent <= 50)
        {
            BatteryBar.Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)); // Yellow
            BatteryIcon.Text = "\uE996";
        }
        else
        {
            BatteryBar.Background = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)); // Green
            BatteryIcon.Text = "\uE996";
        }
    }

    private void UpdateCpu()
    {
        try
        {
            var cpuPercent = _cpuCounter.NextValue();
            if (cpuPercent > 100) cpuPercent = 100;
            if (cpuPercent < 0) cpuPercent = 0;

            CpuText.Text = $"{cpuPercent:F0}%";
            CpuBar.Width = _barMaxWidth * cpuPercent / 100.0;

            // Color based on load
            if (cpuPercent > 80)
                CpuBar.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            else if (cpuPercent > 50)
                CpuBar.Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            else
                CpuBar.Background = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
        }
        catch
        {
            CpuText.Text = "N/A";
        }
    }

    private void UpdateRam()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            var totalGb = mem.ullTotalPhys / (1024.0 * 1024 * 1024);
            var usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / (1024.0 * 1024 * 1024);
            var percent = mem.dwMemoryLoad;

            RamText.Text = $"{usedGb:F1}/{totalGb:F0} GB";
            RamBar.Width = _barMaxWidth * percent / 100.0;

            if (percent > 85)
                RamBar.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            else if (percent > 60)
                RamBar.Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            else
                RamBar.Background = new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC));
        }
    }

    private void InitGpuCounters()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instances = category.GetInstanceNames()
                .Where(n => n.Contains("engtype_3D") || n.Contains("engtype_Graphics"))
                .ToList();

            _gpuCounters = instances
                .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true))
                .ToList();

            // Prime all counters so the next read returns a real value
            foreach (var c in _gpuCounters) c.NextValue();
        }
        catch
        {
            _gpuCounters = [];
        }
    }

    private void UpdateGpu()
    {
        try
        {
            if (_gpuCounters.Count == 0)
            {
                GpuText.Text = "N/A";
                GpuBar.Width = 0;
                return;
            }

            float totalGpu = 0;
            foreach (var c in _gpuCounters)
                totalGpu += c.NextValue();

            totalGpu = Math.Clamp(totalGpu, 0f, 100f);
            GpuText.Text = $"{totalGpu:F0}%";
            GpuBar.Width = _barMaxWidth * totalGpu / 100.0;

            if (totalGpu > 80)
                GpuBar.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            else if (totalGpu > 50)
                GpuBar.Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            else
                GpuBar.Background = new SolidColorBrush(Color.FromRgb(0xFB, 0x92, 0x3C));
        }
        catch
        {
            // Counters may become invalid if a GPU process exits; re-init next cycle
            foreach (var c in _gpuCounters) c.Dispose();
            InitGpuCounters();
            GpuText.Text = "0%";
            GpuBar.Width = 0;
        }
    }

    private void DetectNpuCounter()
    {
        // Known NPU performance counter category names across vendors
        var candidates = new[]
        {
            ("NPU", "Utilization Percentage"),
            ("NPU Engine", "Utilization Percentage"),
            ("Neural Processing Unit", "% Utilization"),
            ("Qualcomm NPU", "Utilization Percentage"),
            ("Hexagon NPU", "Utilization Percentage"),
            ("Intel NPU", "% NPU Utilization"),
            ("Intel(R) AI Boost", "% NPU Utilization"),
        };

        foreach (var (category, counter) in candidates)
        {
            try
            {
                if (PerformanceCounterCategory.Exists(category))
                {
                    _npuCategory = category;
                    _npuCounterName = counter;
                    return;
                }
            }
            catch { }
        }
    }

    private void UpdateNpu()
    {
        if (_npuCategory == null)
        {
            NpuText.Text = "N/A";
            NpuBar.Width = 0;
            return;
        }

        try
        {
            var category = new PerformanceCounterCategory(_npuCategory);
            var instances = category.GetInstanceNames();
            float total = 0;
            int count = 0;

            foreach (var instance in instances)
            {
                var counters = category.GetCounters(instance);
                foreach (var c in counters)
                {
                    if (c.CounterName == _npuCounterName)
                    {
                        total += c.NextValue();
                        count++;
                    }
                    c.Dispose();
                }
            }

            float util = count > 0 ? Math.Min(total, 100f) : 0f;
            NpuText.Text = $"{util:F0}%";
            NpuBar.Width = _barMaxWidth * util / 100.0;

            if (util > 80)
                NpuBar.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            else if (util > 50)
                NpuBar.Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            else
                NpuBar.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6));
        }
        catch
        {
            NpuText.Text = "N/A";
            NpuBar.Width = 0;
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
                NetworkIcon.Text = "\uE871"; // No network
                NetworkUpText.Text = "";
                NetworkDownText.Text = "";
                return;
            }

            var primary = interfaces
                .OrderByDescending(n => n.GetIPv4Statistics().BytesReceived)
                .First();

            NetworkName.Text = primary.Name.Length > 18
                ? primary.Name[..18] + "..."
                : primary.Name;

            if (primary.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                NetworkIcon.Text = "\uE701"; // WiFi
            else
                NetworkIcon.Text = "\uE839"; // Ethernet

            var stats = primary.GetIPv4Statistics();
            var now = DateTime.Now;

            if (_lastNetworkCheck != DateTime.MinValue)
            {
                var elapsed = (now - _lastNetworkCheck).TotalSeconds;
                if (elapsed > 0)
                {
                    var downSpeed = (stats.BytesReceived - _lastBytesReceived) / elapsed;
                    var upSpeed = (stats.BytesSent - _lastBytesSent) / elapsed;

                    NetworkDownText.Text = $"↓ {FormatSpeed(downSpeed)}";
                    NetworkUpText.Text = $"↑ {FormatSpeed(upSpeed)}";
                }
            }

            _lastBytesReceived = stats.BytesReceived;
            _lastBytesSent = stats.BytesSent;
            _lastNetworkCheck = now;
        }
        catch
        {
            NetworkName.Text = "Error";
        }
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_000_000)
            return $"{bytesPerSecond / 1_000_000:F1} MB/s";
        if (bytesPerSecond >= 1_000)
            return $"{bytesPerSecond / 1_000:F0} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _cpuCounter.Dispose();
        foreach (var c in _gpuCounters) c.Dispose();
#if DEBUG
        DevServer.Stop();
#endif
        base.OnClosed(e);
    }
}
