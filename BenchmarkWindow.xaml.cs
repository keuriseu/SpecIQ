using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace SpecIQ;

public partial class BenchmarkWindow : Window
{
    private GeekbenchInfo? _info;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _dotTimer;
    private int _dotFrame;

    public BenchmarkWindow()
    {
        InitializeComponent();

        _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dotTimer.Tick += AnimateDots;

        Loaded += async (_, _) => await CheckVersionAsync();
    }

    // ── Window chrome ─────────────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    // ── Version check ─────────────────────────────────────────────────────

    private async Task CheckVersionAsync()
    {
        ShowPanel(CheckingPanel);
        _info = await GeekbenchService.CheckAsync();

        // Show full version in title bar
        TitleVersionText.Text = _info.InstalledVersion != null
            ? $"GEEKBENCH {_info.InstalledVersion}"
            : "GEEKBENCH";

        // Status shows update state only (version is already in title)
        StatusText.Text = _info.UpdateAvailable  ? $"v{_info.LatestVersion} available"
                        : _info.IsInstalled       ? "Up to date"
                                                  : "Not installed";

        if (_info.IsInstalled)
        {
            InstallRow.Visibility = Visibility.Collapsed;
            RunRow.Visibility     = Visibility.Visible;
            UpdateBtn.Visibility  = _info.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            InstallRow.Visibility = Visibility.Visible;
            RunRow.Visibility     = Visibility.Collapsed;
            InstallBtn.IsEnabled  = true;
            InstallBtn.Content    = _info.DownloadUrl != null ? "Install" : "Download Page";
        }

        ShowPanel(ReadyPanel);
    }

    // ── Install / update ──────────────────────────────────────────────────

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_info?.DownloadUrl is not { } url)
        {
            Process.Start(new ProcessStartInfo("https://www.geekbench.com/download/") { UseShellExecute = true });
            return;
        }

        ShowPanel(InstallingPanel);
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<(int Percent, string Status)>(r =>
            {
                InstallStatusText.Text = r.Status;
                InstallPctText.Text    = r.Percent >= 0 ? $"{r.Percent}%" : "";
                InstallProgressBar.Width = r.Percent >= 0
                    ? (280.0 * r.Percent / 100.0)
                    : 0;
            });

            await GeekbenchService.DownloadAndInstallAsync(url, progress, _cts.Token);

            // Re-check after install
            await CheckVersionAsync();
        }
        catch (OperationCanceledException) { ShowPanel(ReadyPanel); }
        catch (Exception ex)
        {
            InstallStatusText.Text = $"Error: {ex.Message}";
        }
        finally { _cts = null; }
    }

    // ── Run benchmark ─────────────────────────────────────────────────────

    private void RunCpuBenchmark_Click(object sender, RoutedEventArgs e) => _ = RunBenchmarkAsync(gpu: false);
    private void RunGpuBenchmark_Click(object sender, RoutedEventArgs e) => _ = RunBenchmarkAsync(gpu: true);

    private void Rundown_Click(object sender, RoutedEventArgs e)
    {
        var win = new RundownWindow();
        win.Show();
    }

    private async Task RunBenchmarkAsync(bool gpu)
    {
        _info ??= await GeekbenchService.CheckAsync();
        if (_info.InstalledPath is not { } exePath) return;

        if (EnergyHelper.IsOn())
        {
            var r = MessageBox.Show(
                "Energy Saver is active, which throttles CPU performance and will affect results.\n\n" +
                "Disable it in Windows Settings → System → Power before running.",
                "Energy Saver Active",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;
        }

        ShowPanel(RunningPanel);
        RunPhaseText.Text = "Activating license…";
        LogText.Text      = "";
        _dotTimer.Start();
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(line =>
            {
                if (line.Contains("Single-Core", StringComparison.OrdinalIgnoreCase))
                    RunPhaseText.Text = "Single-Core";
                else if (line.Contains("Multi-Core", StringComparison.OrdinalIgnoreCase))
                    RunPhaseText.Text = "Multi-Core";
                else if (line.Contains("OpenCL", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("Vulkan",  StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("Metal",   StringComparison.OrdinalIgnoreCase))
                    RunPhaseText.Text = line.Trim();
                else if (RunPhaseText.Text == "Activating license…")
                    RunPhaseText.Text = "Starting…";

                LogText.Text += line + "\n";
                LogScroll.ScrollToBottom();
            });

            var result = await GeekbenchService.RunAsync(exePath, progress, gpu, _cts.Token);
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            ShowPanel(ReadyPanel);
        }
        catch (Exception ex)
        {
            RunPhaseText.Text = "Error";
            LogText.Text     += $"\n{ex.Message}";
            _dotTimer.Stop();
        }
        finally { _cts = null; }
    }

    private void ShowResult(BenchmarkResult result)
    {
        _dotTimer.Stop();

        if (result.Gpu)
        {
            ResultTitle.Text  = "GPU BENCHMARK RESULTS";
            ResultLabelA.Text = "OpenCL";
            ResultLabelB.Text = "Vulkan";
        }
        else
        {
            ResultTitle.Text  = "CPU BENCHMARK RESULTS";
            ResultLabelA.Text = "Single-Core";
            ResultLabelB.Text = "Multi-Core";
        }

        ResultSingle.Text = result.SingleCore > 0 ? $"{result.SingleCore:N0}" : "—";
        ResultMulti.Text  = result.MultiCore  > 0 ? $"{result.MultiCore:N0}"  : "—";

        ShowPanel(ResultPanel);

        // Flash score on main overlay
        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.ShowBenchmarkScore(result);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _dotTimer.Stop();
        ShowPanel(ReadyPanel);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void ShowPanel(FrameworkElement panel)
    {
        CheckingPanel.Visibility   = Visibility.Collapsed;
        ReadyPanel.Visibility      = Visibility.Collapsed;
        InstallingPanel.Visibility = Visibility.Collapsed;
        RunningPanel.Visibility    = Visibility.Collapsed;
        ResultPanel.Visibility     = Visibility.Collapsed;
        panel.Visibility           = Visibility.Visible;
    }

    private void AnimateDots(object? sender, EventArgs e)
    {
        _dotFrame = (_dotFrame + 1) % 3;
        Dot1.Opacity = _dotFrame == 0 ? 1.0 : _dotFrame == 2 ? 0.25 : 0.5;
        Dot2.Opacity = _dotFrame == 1 ? 1.0 : _dotFrame == 0 ? 0.25 : 0.5;
        Dot3.Opacity = _dotFrame == 2 ? 1.0 : _dotFrame == 1 ? 0.25 : 0.5;
    }
}
