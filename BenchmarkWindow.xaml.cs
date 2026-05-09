using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace SpecIQ;

public partial class BenchmarkWindow : Window
{
    private GeekbenchInfo?            _info;
    private CancellationTokenSource?  _cts;
    private readonly DispatcherTimer  _dotTimer;
    private int                       _dotFrame;
    private bool                      _lastGpu;
    private int                       _lastTrials = 1;

    public BenchmarkWindow()
    {
        InitializeComponent();

        _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dotTimer.Tick += (_, _) => AnimateDots();

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

        TitleVersionText.Text = _info.InstalledVersion != null
            ? $"GEEKBENCH {_info.InstalledVersion}"
            : "GEEKBENCH";

        StatusText.Text = _info.UpdateAvailable ? $"v{_info.LatestVersion} available"
                        : _info.IsInstalled      ? "Up to date"
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
                InstallStatusText.Text   = r.Status;
                InstallPctText.Text      = r.Percent >= 0 ? $"{r.Percent}%" : "";
                InstallProgressBar.Width = r.Percent >= 0 ? 280.0 * r.Percent / 100.0 : 0;
            });

            await GeekbenchService.DownloadAndInstallAsync(url, progress, _cts.Token);
            await CheckVersionAsync();
        }
        catch (OperationCanceledException) { ShowPanel(ReadyPanel); }
        catch (Exception ex) { InstallStatusText.Text = $"Error: {ex.Message}"; }
        finally { _cts = null; }
    }

    // ── Run ───────────────────────────────────────────────────────────────

    private void RunCpuSingle_Click(object sender, RoutedEventArgs e) => _ = RunBenchmarkAsync(gpu: false, trials: 1);
    private void RunCpuTrials_Click(object sender, RoutedEventArgs e) => _ = RunBenchmarkAsync(gpu: false, trials: 3);
    private void RunGpuSingle_Click(object sender, RoutedEventArgs e) => _ = RunBenchmarkAsync(gpu: true,  trials: 1);
    private void RunGpuTrials_Click(object sender, RoutedEventArgs e) => _ = RunBenchmarkAsync(gpu: true,  trials: 3);
    private void RunAgain_Click(object sender, RoutedEventArgs e)     => _ = RunBenchmarkAsync(_lastGpu, _lastTrials);

    private void Rundown_Click(object sender, RoutedEventArgs e)
    {
        new RundownWindow().Show();
    }

    private async Task RunBenchmarkAsync(bool gpu, int trials)
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

        _lastGpu    = gpu;
        _lastTrials = trials;

        ShowPanel(RunningPanel);
        RunPhaseText.Text = trials > 1 ? "Trial 1 of 3" : "Starting…";
        RunTrialText.Visibility = Visibility.Collapsed;
        LogText.Text = "";
        _dotTimer.Start();
        _cts = new CancellationTokenSource();

        var results = new List<BenchmarkResult>();

        try
        {
            for (int i = 0; i < trials && !_cts.Token.IsCancellationRequested; i++)
            {
                if (trials > 1)
                {
                    RunTrialText.Text       = $"Trial {i + 1} of {trials}";
                    RunTrialText.Visibility = Visibility.Visible;
                    LogText.Text           += i > 0 ? $"\n── Trial {i + 1} ──\n" : "";
                }

                var progress = new Progress<string>(line =>
                {
                    if (line.Contains("Single-Core", StringComparison.OrdinalIgnoreCase))
                        RunPhaseText.Text = trials > 1 ? $"Trial {i + 1} of {trials}  ·  Single-Core" : "Single-Core";
                    else if (line.Contains("Multi-Core", StringComparison.OrdinalIgnoreCase))
                        RunPhaseText.Text = trials > 1 ? $"Trial {i + 1} of {trials}  ·  Multi-Core" : "Multi-Core";
                    else if (line.Contains("OpenCL",  StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("Vulkan",  StringComparison.OrdinalIgnoreCase))
                        RunPhaseText.Text = line.Trim();

                    LogText.Text += line + "\n";
                    LogScroll.ScrollToBottom();
                });

                var result = await GeekbenchService.RunAsync(exePath, progress, gpu, _cts.Token);
                results.Add(result);
            }

            if (results.Count == 0) { ShowPanel(ReadyPanel); return; }

            if (trials == 1)
                ShowSingleResult(results[0], gpu);
            else
                ShowTrialResults(results, gpu);
        }
        catch (OperationCanceledException) { ShowPanel(ReadyPanel); }
        catch (Exception ex)
        {
            RunPhaseText.Text  = "Error";
            LogText.Text      += $"\n{ex.Message}";
        }
        finally
        {
            _dotTimer.Stop();
            _cts = null;
        }
    }

    // ── Results ───────────────────────────────────────────────────────────

    private void ShowSingleResult(BenchmarkResult result, bool gpu)
    {
        _dotTimer.Stop();

        ResultTitle.Text   = gpu ? "GPU BENCHMARK RESULTS" : "CPU BENCHMARK RESULTS";
        ResultLabelA.Text  = gpu ? "OpenCL" : "Single-Core";
        ResultLabelB.Text  = gpu ? "Vulkan"  : "Multi-Core";
        ResultSingle.Text  = result.SingleCore > 0 ? $"{result.SingleCore:N0}" : "—";
        ResultMulti.Text   = result.MultiCore  > 0 ? $"{result.MultiCore:N0}"  : "—";

        ShowPanel(ResultPanel);

        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.ShowBenchmarkScore(result);
    }

    private void ShowTrialResults(List<BenchmarkResult> results, bool gpu)
    {
        _dotTimer.Stop();

        var prefix = gpu ? "GPU" : "CPU";
        TrialResultTitle.Text  = $"{prefix}  ×3 TRIALS";
        TrialLabelA.Text       = gpu ? "OpenCL" : "Single";
        TrialLabelB.Text       = gpu ? "Vulkan"  : "Multi";
        TrialAvgLabelA.Text    = gpu ? "AVG OPENCL" : "AVG SINGLE";
        TrialAvgLabelB.Text    = gpu ? "AVG VULKAN"  : "AVG MULTI";

        var aScores = results.Select(r => r.SingleCore).ToList();
        var bScores = results.Select(r => r.MultiCore).ToList();

        TrialA1.Text = aScores.Count > 0 ? $"{aScores[0]:N0}" : "—";
        TrialA2.Text = aScores.Count > 1 ? $"{aScores[1]:N0}" : "—";
        TrialA3.Text = aScores.Count > 2 ? $"{aScores[2]:N0}" : "—";

        TrialB1.Text = bScores.Count > 0 ? $"{bScores[0]:N0}" : "—";
        TrialB2.Text = bScores.Count > 1 ? $"{bScores[1]:N0}" : "—";
        TrialB3.Text = bScores.Count > 2 ? $"{bScores[2]:N0}" : "—";

        TrialAvgA.Text = aScores.Count > 0 && aScores.Max() > 0
            ? $"{(int)aScores.Average():N0}" : "—";
        TrialAvgB.Text = bScores.Count > 0 && bScores.Max() > 0
            ? $"{(int)bScores.Average():N0}" : "—";

        ShowPanel(TrialResultPanel);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _dotTimer.Stop();
        ShowPanel(ReadyPanel);
    }

    private void ShowPanel(FrameworkElement panel)
    {
        CheckingPanel    .Visibility = Visibility.Collapsed;
        ReadyPanel       .Visibility = Visibility.Collapsed;
        InstallingPanel  .Visibility = Visibility.Collapsed;
        RunningPanel     .Visibility = Visibility.Collapsed;
        ResultPanel      .Visibility = Visibility.Collapsed;
        TrialResultPanel .Visibility = Visibility.Collapsed;
        panel.Visibility             = Visibility.Visible;
    }

    private void AnimateDots()
    {
        _dotFrame = (_dotFrame + 1) % 3;
        Dot1.Opacity = _dotFrame == 0 ? 1.0 : _dotFrame == 2 ? 0.25 : 0.5;
        Dot2.Opacity = _dotFrame == 1 ? 1.0 : _dotFrame == 0 ? 0.25 : 0.5;
        Dot3.Opacity = _dotFrame == 2 ? 1.0 : _dotFrame == 1 ? 0.25 : 0.5;
    }
}
