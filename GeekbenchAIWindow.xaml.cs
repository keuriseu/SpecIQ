using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;

namespace SpecIQ;

public partial class GeekbenchAIWindow : Window
{
    private string?                  _exePath;
    private string?                  _version;
    private CancellationTokenSource? _cts;
    private AIBenchmarkResult?       _lastSingleResult;
    private List<AIBenchmarkResult>? _lastTrialResults;
    private string                   _lastBackendLabel = "";
    private AIBackend                _lastBackend      = AIBackend.Cpu;
    private int                      _lastTrials       = 1;
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

            RunCpuBtn.IsEnabled      = true;
            RunCpuTrialsBtn.IsEnabled = true;
            RunGpuBtn.IsEnabled      = true;
            RunGpuTrialsBtn.IsEnabled = true;
            InstallBtn.Visibility    = Visibility.Collapsed;
        }
        else
        {
            StatusText.Text       = "Not installed";
            InstallBtn.Visibility = Visibility.Visible;
        }

        // Show QNN row only on Snapdragon devices
        if (GeekbenchAIService.IsSnapdragonDevice())
        {
            QnnRow.Visibility        = Visibility.Visible;
            RunQnnBtn.IsEnabled      = _exePath != null;
            RunQnnTrialsBtn.IsEnabled = _exePath != null;
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "https://www.geekbench.com/ai/download/") { UseShellExecute = true });
    }

    // ── Run ───────────────────────────────────────────────────────────────

    private void RunCpuSingle_Click(object sender, RoutedEventArgs e)  => _ = RunAsync(AIBackend.Cpu, trials: 1);
    private void RunCpuTrials_Click(object sender, RoutedEventArgs e)   => _ = RunAsync(AIBackend.Cpu, trials: 3);
    private void RunGpuSingle_Click(object sender, RoutedEventArgs e)  => _ = RunAsync(AIBackend.Gpu, trials: 1);
    private void RunGpuTrials_Click(object sender, RoutedEventArgs e)   => _ = RunAsync(AIBackend.Gpu, trials: 3);
    private void RunQnnSingle_Click(object sender, RoutedEventArgs e)  => _ = RunAsync(AIBackend.Qnn, trials: 1);
    private void RunQnnTrials_Click(object sender, RoutedEventArgs e)   => _ = RunAsync(AIBackend.Qnn, trials: 3);

    private void RunAgain_Click(object sender, RoutedEventArgs e) => _ = RunAsync(_lastBackend, _lastTrials);

    private async Task RunAsync(AIBackend backend, int trials)
    {
        if (_exePath == null) return;

        _lastBackend = backend;
        _lastTrials  = trials;

        _cts = new CancellationTokenSource();

        var backendLabel = backend switch
        {
            AIBackend.Gpu => "GPU — OpenCL",
            AIBackend.Qnn => "QNN — Snapdragon NPU",
            _             => "CPU",
        };
        _lastBackendLabel = backendLabel;

        RunSubtitleText.Text    = $"{backendLabel}  ·  {Environment.MachineName}";
        RunTrialText.Visibility = Visibility.Collapsed;
        RunPhaseText.Text       = "Running…";
        LogText.Text            = "";
        ShowPanel(RunningPanel);
        _dotTimer.Start();

        var results = new List<AIBenchmarkResult>();

        try
        {
            for (int i = 0; i < trials && !_cts.Token.IsCancellationRequested; i++)
            {
                if (trials > 1)
                {
                    RunTrialText.Text       = $"Trial {i + 1} of {trials}";
                    RunTrialText.Visibility = Visibility.Visible;
                    if (i > 0) LogText.Text += $"\n── Trial {i + 1} ──\n";
                }

                var progress = new Progress<string>(line =>
                {
                    LogText.Text += line + "\n";
                    LogScroll.ScrollToBottom();

                    var t = line.Trim();
                    if (t.Contains("Single", StringComparison.OrdinalIgnoreCase) &&
                        t.Contains("Precision", StringComparison.OrdinalIgnoreCase))
                        RunPhaseText.Text = trials > 1 ? $"Trial {i + 1}  ·  Single Precision" : "Single Precision";
                    else if (t.Contains("Half", StringComparison.OrdinalIgnoreCase))
                        RunPhaseText.Text = trials > 1 ? $"Trial {i + 1}  ·  Half Precision" : "Half Precision";
                    else if (t.Contains("Quantized", StringComparison.OrdinalIgnoreCase))
                        RunPhaseText.Text = trials > 1 ? $"Trial {i + 1}  ·  Quantized" : "Quantized";
                });

                var result = await GeekbenchAIService.RunAsync(_exePath, progress, backend, _cts.Token);
                results.Add(result);
            }

            if (results.Count == 0) { ShowPanel(ConfigPanel); return; }

            if (trials == 1)
            {
                _lastSingleResult = results[0];
                _lastTrialResults = null;
                ShowResults(results[0], backendLabel);
            }
            else
            {
                _lastTrialResults = results;
                _lastSingleResult = null;
                ShowTrialResults(results, backendLabel);
            }
        }
        catch (OperationCanceledException)
        {
            ShowPanel(ConfigPanel);
        }
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

        ResFP32.Text  = result.FullPrecision > 0 ? $"{result.FullPrecision:N0}" : "—";
        ResFP16.Text  = result.HalfPrecision > 0 ? $"{result.HalfPrecision:N0}" : "—";
        ResQuant.Text = result.Quantized     > 0 ? $"{result.Quantized:N0}"     : "—";

        ShowPanel(ResultsPanel);
    }

    private void ShowTrialResults(List<AIBenchmarkResult> results, string backendLabel)
    {
        var verStr = _version != null ? $"Geekbench AI {_version}" : "Geekbench AI";
        TrialResBackendText.Text = $"{verStr}  ·  {backendLabel}  ×3";
        TrialResMachineText.Text = Environment.MachineName;

        var fp32  = results.Select(r => r.FullPrecision).ToList();
        var fp16  = results.Select(r => r.HalfPrecision).ToList();
        var quant = results.Select(r => r.Quantized).ToList();

        TFP32_1.Text = fp32.Count  > 0 ? $"{fp32[0]:N0}"  : "—";
        TFP32_2.Text = fp32.Count  > 1 ? $"{fp32[1]:N0}"  : "—";
        TFP32_3.Text = fp32.Count  > 2 ? $"{fp32[2]:N0}"  : "—";

        TFP16_1.Text = fp16.Count  > 0 ? $"{fp16[0]:N0}"  : "—";
        TFP16_2.Text = fp16.Count  > 1 ? $"{fp16[1]:N0}"  : "—";
        TFP16_3.Text = fp16.Count  > 2 ? $"{fp16[2]:N0}"  : "—";

        TQuant_1.Text = quant.Count > 0 ? $"{quant[0]:N0}" : "—";
        TQuant_2.Text = quant.Count > 1 ? $"{quant[1]:N0}" : "—";
        TQuant_3.Text = quant.Count > 2 ? $"{quant[2]:N0}" : "—";

        TAvgFP32.Text  = fp32.Count  > 0 && fp32.Max()  > 0 ? $"{(int)fp32.Average():N0}"  : "—";
        TAvgFP16.Text  = fp16.Count  > 0 && fp16.Max()  > 0 ? $"{(int)fp16.Average():N0}"  : "—";
        TAvgQuant.Text = quant.Count > 0 && quant.Max() > 0 ? $"{(int)quant.Average():N0}" : "—";

        ShowPanel(TrialResultsPanel);
    }

    // ── Export ────────────────────────────────────────────────────────────

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSingleResult == null) return;
        CopyToClipboard(BuildSingleExport(_lastSingleResult, _lastBackendLabel));
        FlashButton(ExportBtn);
    }

    private void TrialExport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastTrialResults == null) return;
        CopyToClipboard(BuildTrialExport(_lastTrialResults, _lastBackendLabel));
        FlashButton(TrialExportBtn);
    }

    private string BuildSingleExport(AIBenchmarkResult r, string backendLabel)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Geekbench AI — {Environment.MachineName}");
        if (_version != null) sb.AppendLine($"Version: {_version}");
        sb.AppendLine($"Backend: {backendLabel}");
        sb.AppendLine();
        sb.AppendLine($"Single Precision:  {(r.FullPrecision > 0 ? r.FullPrecision.ToString("N0") : "—")}");
        sb.AppendLine($"Half Precision:    {(r.HalfPrecision  > 0 ? r.HalfPrecision.ToString("N0")  : "—")}");
        sb.AppendLine($"Quantized:         {(r.Quantized      > 0 ? r.Quantized.ToString("N0")      : "—")}");
        return sb.ToString();
    }

    private string BuildTrialExport(List<AIBenchmarkResult> results, string backendLabel)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Geekbench AI ×3 Trials — {Environment.MachineName}");
        if (_version != null) sb.AppendLine($"Version: {_version}");
        sb.AppendLine($"Backend: {backendLabel}");
        sb.AppendLine();

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.AppendLine($"Trial {i + 1}:");
            sb.AppendLine($"  Single Precision:  {(r.FullPrecision > 0 ? r.FullPrecision.ToString("N0") : "—")}");
            sb.AppendLine($"  Half Precision:    {(r.HalfPrecision  > 0 ? r.HalfPrecision.ToString("N0")  : "—")}");
            sb.AppendLine($"  Quantized:         {(r.Quantized      > 0 ? r.Quantized.ToString("N0")      : "—")}");
        }

        var fp32  = results.Select(r => r.FullPrecision).ToList();
        var fp16  = results.Select(r => r.HalfPrecision).ToList();
        var quant = results.Select(r => r.Quantized).ToList();

        sb.AppendLine();
        sb.AppendLine("Averages:");
        sb.AppendLine($"  Single Precision:  {(fp32.Max()  > 0 ? ((int)fp32.Average()).ToString("N0")  : "—")}");
        sb.AppendLine($"  Half Precision:    {(fp16.Max()  > 0 ? ((int)fp16.Average()).ToString("N0")  : "—")}");
        sb.AppendLine($"  Quantized:         {(quant.Max() > 0 ? ((int)quant.Average()).ToString("N0") : "—")}");
        return sb.ToString();
    }

    private static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }

    private void FlashButton(System.Windows.Controls.Button btn)
    {
        var original = btn.Content;
        btn.Content = "Copied!";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { btn.Content = original; t.Stop(); };
        t.Start();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void ShowPanel(FrameworkElement panel)
    {
        ConfigPanel      .Visibility = Visibility.Collapsed;
        RunningPanel     .Visibility = Visibility.Collapsed;
        ResultsPanel     .Visibility = Visibility.Collapsed;
        TrialResultsPanel.Visibility = Visibility.Collapsed;
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
