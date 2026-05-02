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

        var showItem = new WinForms.ToolStripMenuItem("Show/Hide");
        showItem.Click += (_, _) => ToggleOverlay();
        menu.Items.Add(showItem);

        var benchItem = new WinForms.ToolStripMenuItem("Run Benchmark…");
        benchItem.Click += (_, _) => ShowBenchmark();
        menu.Items.Add(benchItem);

        var aboutItem = new WinForms.ToolStripMenuItem("About");
        aboutItem.Click += (_, _) => ShowAbout();
        menu.Items.Add(aboutItem);

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

    private BenchmarkWindow? _benchmarkWindow;

    private void ShowBenchmark()
    {
        if (_benchmarkWindow is { IsLoaded: true })
        {
            _benchmarkWindow.Activate();
            return;
        }
        _benchmarkWindow = new BenchmarkWindow();
        _benchmarkWindow.Show();
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
