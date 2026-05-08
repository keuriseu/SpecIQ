using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;

namespace SpecIQ;

public partial class GeekbenchAIWindow : Window
{
    private string?               _exePath;
    private string?               _version;
    private CancellationTokenSource? _cts;
    private AIBenchmarkResult?    _lastResult;
    private readonly DispatcherTimer _dotTimer;
    private int _dotFrame;

    public GeekbenchAIWindow()
    {
        InitializeComponent();

        _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dotTimer.Tick += (_, _) => AnimateDots();

        Loaded += async (_, _) => await CheckAsync();
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

    // ── Install check ─────────────────────────────────────────────────────

    private async Task CheckAsync()
    {
        _exePath = GeekbenchAIService.FindInstalled();
        _version = _exePath != null ? GeekbenchAIService.GetInstalledVersion(_exePath) : null;

        string? latestVersion = await GeekbenchAIService.GetLatestVersionAsync();

        if (_exePath != null)
        {
            var verText   = _version != null ? $"v{_version}" : "Installed";
            var updateStr = latestVersion != null && latestVersion != _version
                            ? $"  ·  v{latestVersion} available" : "";
            StatusText.Text = verText + updateStr;
            RunCpuBtn.IsEnabled = true;
            RunGpuBtn.IsEnabled = true;
            InstallBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusText.Text       = "Not installed";
            InstallBtn.Visibility = Visibility.Visible;
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "https://www.geekbench.com/ai/download/") { UseShellExecute = true });
    }

    // ── Run ───────────────────────────────────────────────────────────────

    private void RunCpu_Click(object sender, RoutedEventArgs e) => _ = RunAsync(gpu: false);
    private void RunGpu_Click(object sender, RoutedEventArgs e) => _ = RunAsync(gpu: true);

    private async Task RunAsync(bool gpu)
    {
        if (_exePath == null) return;

        _cts = new CancellationTokenSource();
        var backendLabel = gpu ? "GPU — OpenCL" : "CPU";
        RunSubtitleText.Text = $"{backendLabel}  ·  {Environment.MachineName}";
        RunPhaseText.Text    = "Running…";
        LogText.Text         = "";
        ShowPanel(RunningPanel);
        _dotTimer.Start();

        try
        {
            var progress = new Progress<string>(line =>
            {
                LogText.Text += line + "\n";
                LogScroll.ScrollToBottom();

                // Update phase text based on keywords in output
                var t = line.Trim();
                if (t.Contains("Single", StringComparison.OrdinalIgnoreCase) &&
                    t.Contains("Precision", StringComparison.OrdinalIgnoreCase))
                    RunPhaseText.Text = "Single Precision";
                else if (t.Contains("Half", StringComparison.OrdinalIgnoreCase))
                    RunPhaseText.Text = "Half Precision";
                else if (t.Contains("Quantized", StringComparison.OrdinalIgnoreCase))
                    RunPhaseText.Text = "Quantized";
            });

            _lastResult = await GeekbenchAIService.RunAsync(_exePath, progress, gpu, _cts.Token);
            ShowResults(_lastResult, backendLabel);
        }
        catch (OperationCanceledException)
        {
            ShowPanel(ConfigPanel);
        }
        catch (Exception ex)
        {
            RunPhaseText.Text  = "Error";
            LogText.Text      += $"\n{ex.Message}";
            _dotTimer.Stop();
        }
        finally
        {
            _dotTimer.Stop();
            _cts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _dotTimer.Stop();
    }

    // ── Results ───────────────────────────────────────────────────────────

    private void ShowResults(AIBenchmarkResult result, string backendLabel)
    {
        var verStr = _version != null ? $"Geekbench AI {_version}" : "Geekbench AI";
        ResBackendText.Text = $"{verStr}  ·  {backendLabel}";
        ResMachineText.Text = Environment.MachineName;

        ResFP32.Text  = result.FullPrecision  > 0 ? $"{result.FullPrecision:N0}"  : "—";
        ResFP16.Text  = result.HalfPrecision  > 0 ? $"{result.HalfPrecision:N0}"  : "—";
        ResQuant.Text = result.Quantized      > 0 ? $"{result.Quantized:N0}"      : "—";

        ShowPanel(ResultsPanel);
    }

    private void RunAgain_Click(object sender, RoutedEventArgs e) => ShowPanel(ConfigPanel);

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult == null) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Geekbench AI — {Environment.MachineName}");
        if (_version != null) sb.AppendLine($"Version: {_version}");
        sb.AppendLine($"Backend: {(_lastResult.Gpu ? "GPU (OpenCL)" : "CPU")}");
        sb.AppendLine();
        sb.AppendLine($"Single Precision:  {(_lastResult.FullPrecision > 0 ? _lastResult.FullPrecision.ToString("N0") : "—")}");
        sb.AppendLine($"Half Precision:    {(_lastResult.HalfPrecision > 0  ? _lastResult.HalfPrecision.ToString("N0")  : "—")}");
        sb.AppendLine($"Quantized:         {(_lastResult.Quantized > 0      ? _lastResult.Quantized.ToString("N0")      : "—")}");

        Clipboard.SetText(sb.ToString());

        ExportBtn.Content = "Copied!";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { ExportBtn.Content = "Copy"; t.Stop(); };
        t.Start();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void ShowPanel(FrameworkElement panel)
    {
        ConfigPanel .Visibility = Visibility.Collapsed;
        RunningPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        panel.Visibility        = Visibility.Visible;
    }

    private void AnimateDots()
    {
        _dotFrame = (_dotFrame + 1) % 3;
        Dot1.Opacity = _dotFrame == 0 ? 1.0 : _dotFrame == 2 ? 0.25 : 0.5;
        Dot2.Opacity = _dotFrame == 1 ? 1.0 : _dotFrame == 0 ? 0.25 : 0.5;
        Dot3.Opacity = _dotFrame == 2 ? 1.0 : _dotFrame == 1 ? 0.25 : 0.5;
    }
}
