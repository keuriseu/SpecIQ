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
    private string?                    _lastResultUrl;

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
        _dotTimer.Stop();
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
        _cts?.Dispose();
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
        RunPhaseText.Text       = trials > 1 ? "Trial 1 of 3" : "Starting…";
        RunTrialText.Visibility = Visibility.Collapsed;
        LogText.Text            = "";
        _dotTimer.Start();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        var results = new List<BenchmarkResult>();

        try
        {
            for (int i = 0; i < trials && !_cts.Token.IsCancellationRequested; i++)
            {
                int trial = i;
                if (trials > 1)
                {
                    RunTrialText.Text       = $"Trial {trial + 1} of {trials}";
                    RunTrialText.Visibility = Visibility.Visible;
                    if (trial > 0) LogText.Text += $"\n── Trial {trial + 1} ──\n";
                }

                var progress = new Progress<string>(line =>
                {
                    if (line.Contains("Single-Core", StringComparison.OrdinalIgnoreCase))
                        RunPhaseText.Text = trials > 1 ? $"Trial {trial + 1} of {trials}  ·  Single-Core" : "Single-Core";
                    else if (line.Contains("Multi-Core", StringComparison.OrdinalIgnoreCase))
                        RunPhaseText.Text = trials > 1 ? $"Trial {trial + 1} of {trials}  ·  Multi-Core" : "Multi-Core";
                    else if (line.Contains("OpenCL",  StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("Vulkan",  StringComparison.OrdinalIgnoreCase))
                        RunPhaseText.Text = line.Trim();

                    LogText.Text += line + "\n";
                    LogScroll.ScrollToBottom();
                });

                var result = await GeekbenchService.RunAsync(exePath, progress, gpu, _cts.Token);
                results.Add(result);

                if (trials > 1 && trial < trials - 1)
                {
                    for (int s = 60; s > 0; s--)
                    {
                        RunPhaseText.Text = $"Cooldown  {s}s";
                        try { await Task.Delay(1000, _cts.Token); }
                        catch (OperationCanceledException) { break; }
                    }
                }
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
        ResultTitle.Text  = gpu ? "GPU BENCHMARK RESULTS" : "CPU BENCHMARK RESULTS";
        ResultLabelA.Text = gpu ? "OpenCL" : "Single-Core";
        ResultLabelB.Text = gpu ? "Vulkan"  : "Multi-Core";
        ResultSingle.Text = result.SingleCore > 0 ? $"{result.SingleCore:N0}" : "—";
        ResultMulti.Text  = result.MultiCore  > 0 ? $"{result.MultiCore:N0}"  : "—";

        ShowPanel(ResultPanel);
        _lastResultUrl = result.ResultUrl;
        ViewResultsBtn.Visibility = result.ResultUrl != null ? Visibility.Visible : Visibility.Collapsed;

        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.ShowBenchmarkScore(result);
    }

    private void ShowTrialResults(List<BenchmarkResult> results, bool gpu)
    {
        var prefix = gpu ? "GPU" : "CPU";
        TrialResultTitle.Text = $"{prefix}  ×3 TRIALS";
        TrialLabelA.Text      = gpu ? "OpenCL" : "Single";
        TrialLabelB.Text      = gpu ? "Vulkan"  : "Multi";
        TrialAvgLabelA.Text   = gpu ? "AVG OPENCL" : "AVG SINGLE";
        TrialAvgLabelB.Text   = gpu ? "AVG VULKAN"  : "AVG MULTI";

        TrialA1.Text = results.Count > 0 ? $"{results[0].SingleCore:N0}" : "—";
        TrialA2.Text = results.Count > 1 ? $"{results[1].SingleCore:N0}" : "—";
        TrialA3.Text = results.Count > 2 ? $"{results[2].SingleCore:N0}" : "—";

        TrialB1.Text = results.Count > 0 ? $"{results[0].MultiCore:N0}" : "—";
        TrialB2.Text = results.Count > 1 ? $"{results[1].MultiCore:N0}" : "—";
        TrialB3.Text = results.Count > 2 ? $"{results[2].MultiCore:N0}" : "—";

        var aAvg = results.Select(r => r.SingleCore).ToList();
        var bAvg = results.Select(r => r.MultiCore).ToList();
        TrialAvgA.Text = aAvg.Max() > 0 ? $"{(int)aAvg.Average():N0}" : "—";
        TrialAvgB.Text = bAvg.Max() > 0 ? $"{(int)bAvg.Average():N0}" : "—";

        ShowPanel(TrialResultPanel);
        _lastResultUrl = results.LastOrDefault(r => r.ResultUrl != null)?.ResultUrl;
        ViewTrialResultsBtn.Visibility = _lastResultUrl != null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _dotTimer.Stop();
        ShowPanel(ReadyPanel);
    }

    private void ViewResults_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResultUrl == null) return;
        Process.Start(new ProcessStartInfo(_lastResultUrl) { UseShellExecute = true });
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
        AppHelpers.SetDotOpacities(_dotFrame, Dot1, Dot2, Dot3);
    }
}
