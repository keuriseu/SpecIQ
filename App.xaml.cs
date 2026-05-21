using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace SpecIQ;

public partial class App : System.Windows.Application
{
    private WinForms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Apply Windows 11 rounded corners to every window when it loads
        EventManager.RegisterClassHandler(
            typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => AppHelpers.SetRoundedCorners((Window)s)));

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "SpecIQ — System Monitor",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        _trayIcon.DoubleClick += (_, _) => ToggleOverlay();

        try
        {
            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "speciq.ico");
            if (System.IO.File.Exists(iconPath))
                _trayIcon.Icon = new Icon(iconPath);
        }
        catch { }
    }

    private WinForms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

        var showItem = new WinForms.ToolStripMenuItem("Show / Hide Overlay");
        showItem.Click += (_, _) => ToggleOverlay();
        menu.Items.Add(showItem);

        var resetPosItem = new WinForms.ToolStripMenuItem("Reset Overlay Position");
        resetPosItem.Click += (_, _) => ResetOverlayPosition();
        menu.Items.Add(resetPosItem);

        var snapshotItem = new WinForms.ToolStripMenuItem("System Snapshot…");
        snapshotItem.Click += (_, _) => ShowSnapshot();
        menu.Items.Add(snapshotItem);

        var historyItem = new WinForms.ToolStripMenuItem("Benchmark History…");
        historyItem.Click += (_, _) => ShowHistory();
        menu.Items.Add(historyItem);

        var benchItem = new WinForms.ToolStripMenuItem("Geekbench 6…");
        benchItem.Click += (_, _) => ShowBenchmark();
        menu.Items.Add(benchItem);

        var rundownItem = new WinForms.ToolStripMenuItem("Battery Rundown…");
        rundownItem.Click += (_, _) => ShowRundown();
        menu.Items.Add(rundownItem);

        var speedometerItem = new WinForms.ToolStripMenuItem("Speedometer 3.1…");
        speedometerItem.Click += (_, _) => ShowSpeedometer();
        menu.Items.Add(speedometerItem);

        var geekbenchAiItem = new WinForms.ToolStripMenuItem("Geekbench AI…");
        geekbenchAiItem.Click += (_, _) => ShowGeekbenchAI();
        menu.Items.Add(geekbenchAiItem);

        var cinebenchItem = new WinForms.ToolStripMenuItem("Cinebench…");
        cinebenchItem.Click += (_, _) => ShowCinebench();
        menu.Items.Add(cinebenchItem);

        var procyonItem = new WinForms.ToolStripMenuItem("Procyon AI CV…");
        procyonItem.Click += (_, _) => ShowProcyon();
        menu.Items.Add(procyonItem);

        var procyonOfficeItem = new WinForms.ToolStripMenuItem("Procyon Office…");
        procyonOfficeItem.Click += (_, _) => ShowProcyonOffice();
        menu.Items.Add(procyonOfficeItem);

        var procyonEssentialsItem = new WinForms.ToolStripMenuItem("Procyon Essentials…");
        procyonEssentialsItem.Click += (_, _) => ShowProcyonEssentials();
        menu.Items.Add(procyonEssentialsItem);

        var blenderItem = new WinForms.ToolStripMenuItem("Blender…");
        blenderItem.Click += (_, _) => ShowBlender();
        menu.Items.Add(blenderItem);

        var aboutItem = new WinForms.ToolStripMenuItem("About SpecIQ");
        aboutItem.Click += (_, _) => ShowAbout();
        menu.Items.Add(aboutItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var startItem = new WinForms.ToolStripMenuItem("Start with Windows")
        {
            Checked      = IsStartWithWindows(),
            CheckOnClick = true,
        };
        startItem.Click += (_, _) => SetStartWithWindows(startItem.Checked);
        menu.Items.Add(startItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            _trayIcon!.Visible = false;
            _trayIcon.Dispose();
            Shutdown();
        };
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ToggleOverlay()
    {
        if (MainWindow is { } window)
        {
            if (window.IsVisible)
                window.Hide();
            else
                window.Show();
        }
    }

    private void ResetOverlayPosition()
    {
        if (MainWindow is MainWindow mw)
            mw.ResetPosition();
    }

    internal void UpdateTrayTooltip(int cpuPct, int battPct)
    {
        if (_trayIcon == null) return;
        var batt = battPct is >= 0 and <= 100 ? $"  🔋{battPct}%" : "";
        _trayIcon.Text = $"SpecIQ  CPU {cpuPct}%{batt}";
    }

    private SpecReportWindow? _snapshotWindow;

    internal void ShowSnapshot()
    {
        if (_snapshotWindow is { IsLoaded: true })
        {
            _snapshotWindow.Activate();
            return;
        }
        _snapshotWindow = new SpecReportWindow();
        _snapshotWindow.Show();
    }

    private HistoryWindow? _historyWindow;

    internal void ShowHistory()
    {
        if (_historyWindow is { IsLoaded: true })
        {
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new HistoryWindow();
        _historyWindow.Show();
    }

    private BenchmarkWindow? _benchmarkWindow;

    internal void ShowBenchmark()
    {
        if (_benchmarkWindow is { IsLoaded: true })
        {
            _benchmarkWindow.Activate();
            return;
        }
        _benchmarkWindow = new BenchmarkWindow();
        _benchmarkWindow.Show();
    }

    private RundownWindow? _rundownWindow;

    private void ShowRundown()
    {
        if (_rundownWindow is { IsLoaded: true })
        {
            _rundownWindow.Activate();
            return;
        }
        _rundownWindow = new RundownWindow();
        _rundownWindow.Show();
    }

    private SpeedometerWindow? _speedometerWindow;

    internal void ShowSpeedometer()
    {
        if (_speedometerWindow is { IsLoaded: true })
        {
            _speedometerWindow.Activate();
            return;
        }
        _speedometerWindow = new SpeedometerWindow();
        _speedometerWindow.Show();
    }

    private GeekbenchAIWindow? _geekbenchAIWindow;

    internal void ShowGeekbenchAI()
    {
        if (_geekbenchAIWindow is { IsLoaded: true })
        {
            _geekbenchAIWindow.Activate();
            return;
        }
        _geekbenchAIWindow = new GeekbenchAIWindow();
        _geekbenchAIWindow.Show();
    }

    private CinebenchWindow? _cinebenchWindow;

    internal void ShowCinebench()
    {
        if (_cinebenchWindow is { IsLoaded: true })
        {
            _cinebenchWindow.Activate();
            return;
        }
        _cinebenchWindow = new CinebenchWindow();
        _cinebenchWindow.Show();
    }

    private ProcyonOfficeWindow? _procyonOfficeWindow;

    internal void ShowProcyonOffice()
    {
        if (_procyonOfficeWindow is { IsLoaded: true })
        {
            _procyonOfficeWindow.Activate();
            return;
        }
        _procyonOfficeWindow = new ProcyonOfficeWindow();
        _procyonOfficeWindow.Show();
    }

    private ProcyonEssentialsWindow? _procyonEssentialsWindow;

    internal void ShowProcyonEssentials()
    {
        if (_procyonEssentialsWindow is { IsLoaded: true })
        {
            _procyonEssentialsWindow.Activate();
            return;
        }
        _procyonEssentialsWindow = new ProcyonEssentialsWindow();
        _procyonEssentialsWindow.Show();
    }

    private PugetBenchWindow? _pugetBenchWindow;

    internal void ShowPugetBench()
    {
        if (_pugetBenchWindow is { IsLoaded: true })
        {
            _pugetBenchWindow.Activate();
            return;
        }
        _pugetBenchWindow = new PugetBenchWindow();
        _pugetBenchWindow.Show();
    }

    private BlenderWindow? _blenderWindow;

    internal void ShowBlender()
    {
        if (_blenderWindow is { IsLoaded: true })
        {
            _blenderWindow.Activate();
            return;
        }
        _blenderWindow = new BlenderWindow();
        _blenderWindow.Show();
    }

    private ProcyonWindow? _procyonWindow;

    internal void ShowProcyon()
    {
        if (_procyonWindow is { IsLoaded: true })
        {
            _procyonWindow.Activate();
            return;
        }
        _procyonWindow = new ProcyonWindow();
        _procyonWindow.Show();
    }

    private SuiteWindow? _suiteWindow;

    internal void ShowSuite()
    {
        if (_suiteWindow is { IsLoaded: true })
        {
            _suiteWindow.Activate();
            return;
        }
        _suiteWindow = new SuiteWindow();
        _suiteWindow.Show();
    }

    private AboutWindow? _aboutWindow;

    private void ShowAbout()
    {
        if (_aboutWindow is { IsLoaded: true })
        {
            _aboutWindow.Activate();
            return;
        }
        _aboutWindow = new AboutWindow();
        _aboutWindow.Show();
    }

    private const string RunRegistryKey   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunRegistryValue = "SpecIQ";

    private static bool IsStartWithWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: false);
            return key?.GetValue(RunRegistryValue) != null;
        }
        catch { return false; }
    }

    private static void SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
            if (enable)
            {
                var exePath = Environment.ProcessPath
                    ?? System.IO.Path.Combine(AppContext.BaseDirectory, "SpecIQ.exe");
                key?.SetValue(RunRegistryValue, $"\"{exePath}\"");
            }
            else
            {
                key?.DeleteValue(RunRegistryValue, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        base.OnExit(e);
    }
}
