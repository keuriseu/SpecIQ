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
    private AIEntry?                 _lastEntry;
    private int                      _lastTrials = 1;
    private AIEntry?                 _cpuEntry;
    private AIEntry?                 _gpuEntry;
    private AIEntry?                 _qnnEntry;
    private readonly DispatcherTimer _dotTimer;
    private int                      _dotFrame;
    private AIBenchmarkSavedResult?  _previousResult;

    public GeekbenchAIWindow()
    {
        InitializeComponent();

        _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dotTimer.Tick += (_, _) => AnimateDots();

        Loaded += (_, _) => CheckAsync();
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

    // ── Install check ─────────────────────────────────────────────────────

    private void CheckAsync()
    {
        _exePath = GeekbenchAIService.FindInstalled();
        _version = _exePath != null ? GeekbenchAIService.GetInstalledVersion(_exePath) : null;

        if (_exePath == null)
        {
            StatusText.Text       = "Not installed";
            InstallBtn.Visibility = Visibility.Visible;
            return;
        }

        // Load and display any previous result immediately
        _previousResult = AIBenchmarkSavedResult.Load();
        if (_previousResult != null)
        {
            PrevHeaderText.Text = _previousResult.Backend.Length > 0
                ? $"LAST  ·  {_previousResult.Backend}"
                : "LAST RESULT";
            PrevFP32.Text  = _previousResult.FullPrecision > 0 ? $"{_previousResult.FullPrecision:N0}" : "—";
            PrevFP16.Text  = _previousResult.HalfPrecision > 0 ? $"{_previousResult.HalfPrecision:N0}" : "—";
            PrevQuant.Text = _previousResult.Quantized     > 0 ? $"{_previousResult.Quantized:N0}"     : "—";
            PreviousBorder.Visibility = Visibility.Visible;
        }

        // Phase 1 — immediate: show installed status, enable CPU straight away
        InstallBtn.Visibility  = Visibility.Collapsed;
        StatusText.Text        = _version != null ? $"v{_version}  ·  Detecting frameworks…" : "Installed  ·  Detecting frameworks…";
        _cpuEntry              = GeekbenchAIService.DefaultEntry;
        RunCpuBtn.IsEnabled    = true;
        RunCpuTrialsBtn.IsEnabled = true;

        // Show NPU button immediately on Snapdragon (QNN may not be listed until drivers load)
        if (GeekbenchAIService.IsSnapdragonDevice())
        {
            _qnnEntry                 = GeekbenchAIService.QnnEntry;
            QnnRow.Visibility         = Visibility.Visible;
            RunQnnBtn.IsEnabled       = true;
            RunQnnTrialsBtn.IsEnabled  = true;
        }

        // Phase 2 — background: run --ai-list (~10s) to get precise IDs; update GPU/QNN
        _ = Task.Run(async () =>
        {
            var available     = await GeekbenchAIService.ListAvailableAsync(_exePath);
            var latestVersion = await GeekbenchAIService.GetLatestVersionAsync();

            await Dispatcher.InvokeAsync(() =>
            {
                // Update status text
                var verText   = _version != null ? $"v{_version}" : "Installed";
                var updateStr = latestVersion != null && latestVersion != _version
                                ? $"  ·  v{latestVersion} available" : "";
                StatusText.Text = verText + updateStr;

                // Upgrade entries to use precise IDs from --ai-list
                foreach (var entry in available)
                {
                    var cat = GeekbenchAIService.CategorizeEntry(entry);
                    if (cat == AIBackend.Cpu) _cpuEntry = entry;
                    if (cat == AIBackend.Gpu && _gpuEntry == null)
                    {
                        _gpuEntry                  = entry;
                        RunGpuBtn.IsEnabled        = true;
                        RunGpuTrialsBtn.IsEnabled   = true;
                    }
                    if (cat == AIBackend.Qnn && _qnnEntry?.FrameworkId == -2)
                    {
                        // Replace sentinel with real IDs
                        _qnnEntry = entry;
                    }
                }

                // If QNN still not in list on Snapdragon, show it as disabled with hint
                if (GeekbenchAIService.IsSnapdragonDevice() && _qnnEntry?.FrameworkId == -2)
                    StatusText.Text = (StatusText.Text.Length > 0 ? StatusText.Text + "  ·  " : "") + "QNN drivers not found";
            });
        });
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "https://www.geekbench.com/ai/download/") { UseShellExecute = true });
    }

    // ── Run ───────────────────────────────────────────────────────────────

    private void RunCpuSingle_Click(object sender, RoutedEventArgs e)  => RunWith(_cpuEntry, trials: 1);
    private void RunCpuTrials_Click(object sender, RoutedEventArgs e)   => RunWith(_cpuEntry, trials: 3);
    private void RunGpuSingle_Click(object sender, RoutedEventArgs e)  => RunWith(_gpuEntry, trials: 1);
    private void RunGpuTrials_Click(object sender, RoutedEventArgs e)   => RunWith(_gpuEntry, trials: 3);
    private void RunQnnSingle_Click(object sender, RoutedEventArgs e)  => RunWith(_qnnEntry, trials: 1);
    private void RunQnnTrials_Click(object sender, RoutedEventArgs e)   => RunWith(_qnnEntry, trials: 3);
    private void RunAgain_Click(object sender, RoutedEventArgs e)       => RunWith(_lastEntry, _lastTrials);

    private void RunWith(AIEntry? entry, int trials)
    {
        if (entry == null) return;
        _ = RunAsync(entry, trials);
    }

    private async Task RunAsync(AIEntry entry, int trials)
    {
        if (_exePath == null) return;

        _lastEntry  = entry;
        _lastTrials = trials;

        var label = GeekbenchAIService.EntryLabel(entry);
        RunSubtitleText.Text    = $"{label}  ·  {Environment.MachineName}";
        RunTrialText.Visibility = Visibility.Collapsed;
        RunPhaseText.Text       = "Running…";
        LogText.Text            = "";
        ShowPanel(RunningPanel);
        _dotTimer.Start();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        var results = new List<AIBenchmarkResult>();

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
                    LogText.Text += line + "\n";
                    LogScroll.ScrollToBottom();

                    var t = line.Trim();
                    if (t.Contains("Single", StringComparison.OrdinalIgnoreCase) &&
                        t.Contains("Precision", StringComparison.OrdinalIgnoreCase))
                        RunPhaseText.Text = trials > 1 ? $"Trial {trial + 1}  ·  Single Precision" : "Single Precision";
                    else if (t.Contains("Half", StringComparison.OrdinalIgnoreCase))
                        RunPhaseText.Text = trials > 1 ? $"Trial {trial + 1}  ·  Half Precision" : "Half Precision";
                    else if (t.Contains("Quantized", StringComparison.OrdinalIgnoreCase))
                        RunPhaseText.Text = trials > 1 ? $"Trial {trial + 1}  ·  Quantized" : "Quantized";
                });

                var result = await GeekbenchAIService.RunAsync(_exePath, progress, entry, _cts.Token);
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

            if (results.Count == 0) { ShowPanel(ConfigPanel); return; }

            if (trials == 1)
            {
                _lastSingleResult = results[0];
                _lastTrialResults = null;
                ShowResults(results[0], label);
            }
            else
            {
                _lastTrialResults = results;
                _lastSingleResult = null;
                ShowTrialResults(results, label);
            }
        }
        catch (OperationCanceledException)
        {
            ShowPanel(ConfigPanel);
        }
        catch (TimeoutException ex)
        {
            RunPhaseText.Text  = "Timed out";
            LogText.Text      += $"\n{ex.Message}";
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

    private void ShowResults(AIBenchmarkResult result, string label)
    {
        var verStr = _version != null ? $"Geekbench AI {_version}" : "Geekbench AI";
        ResBackendText.Text = $"{verStr}  ·  {label}";
        ResMachineText.Text = Environment.MachineName;

        ResFP32.Text  = result.FullPrecision > 0 ? $"{result.FullPrecision:N0}" : "—";
        ResFP16.Text  = result.HalfPrecision > 0 ? $"{result.HalfPrecision:N0}" : "—";
        ResQuant.Text = result.Quantized     > 0 ? $"{result.Quantized:N0}"     : "—";

        ShowPanel(ResultsPanel);
        var saved = new AIBenchmarkSavedResult
        {
            FullPrecision = result.FullPrecision,
            HalfPrecision = result.HalfPrecision,
            Quantized     = result.Quantized,
            Backend       = label,
        };
        saved.Save();
        _previousResult = saved;
    }

    private void ShowTrialResults(List<AIBenchmarkResult> results, string label)
    {
        var verStr = _version != null ? $"Geekbench AI {_version}" : "Geekbench AI";
        TrialResBackendText.Text = $"{verStr}  ·  {label}  ×3";
        TrialResMachineText.Text = Environment.MachineName;

        TFP32_1.Text  = results.Count > 0 ? $"{results[0].FullPrecision:N0}" : "—";
        TFP32_2.Text  = results.Count > 1 ? $"{results[1].FullPrecision:N0}" : "—";
        TFP32_3.Text  = results.Count > 2 ? $"{results[2].FullPrecision:N0}" : "—";

        TFP16_1.Text  = results.Count > 0 ? $"{results[0].HalfPrecision:N0}" : "—";
        TFP16_2.Text  = results.Count > 1 ? $"{results[1].HalfPrecision:N0}" : "—";
        TFP16_3.Text  = results.Count > 2 ? $"{results[2].HalfPrecision:N0}" : "—";

        TQuant_1.Text = results.Count > 0 ? $"{results[0].Quantized:N0}" : "—";
        TQuant_2.Text = results.Count > 1 ? $"{results[1].Quantized:N0}" : "—";
        TQuant_3.Text = results.Count > 2 ? $"{results[2].Quantized:N0}" : "—";

        var fp32  = results.Select(r => r.FullPrecision).ToList();
        var fp16  = results.Select(r => r.HalfPrecision).ToList();
        var quant = results.Select(r => r.Quantized).ToList();
        TAvgFP32.Text  = fp32.Max()  > 0 ? $"{(int)fp32.Average():N0}"  : "—";
        TAvgFP16.Text  = fp16.Max()  > 0 ? $"{(int)fp16.Average():N0}"  : "—";
        TAvgQuant.Text = quant.Max() > 0 ? $"{(int)quant.Average():N0}" : "—";

        ShowPanel(TrialResultsPanel);
        var avgFP32  = results.Select(r => r.FullPrecision).ToList();
        var avgFP16  = results.Select(r => r.HalfPrecision).ToList();
        var avgQuant = results.Select(r => r.Quantized).ToList();
        var saved = new AIBenchmarkSavedResult
        {
            FullPrecision = avgFP32.Max()  > 0 ? (int)avgFP32.Average()  : 0,
            HalfPrecision = avgFP16.Max()  > 0 ? (int)avgFP16.Average()  : 0,
            Quantized     = avgQuant.Max() > 0 ? (int)avgQuant.Average() : 0,
            Backend       = label + " ×3 avg",
        };
        saved.Save();
        _previousResult = saved;
    }

    // ── Export ────────────────────────────────────────────────────────────

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSingleResult == null || _lastEntry == null) return;
        CopyToClipboard(BuildSingleExport(_lastSingleResult, GeekbenchAIService.EntryLabel(_lastEntry)));
        FlashButton(ExportBtn);
    }

    private void TrialExport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastTrialResults == null || _lastEntry == null) return;
        CopyToClipboard(BuildTrialExport(_lastTrialResults, GeekbenchAIService.EntryLabel(_lastEntry)));
        FlashButton(TrialExportBtn);
    }

    private string BuildSingleExport(AIBenchmarkResult r, string label)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Geekbench AI — {Environment.MachineName}");
        if (_version != null) sb.AppendLine($"Version: {_version}");
        sb.AppendLine($"Backend: {label}");
        sb.AppendLine();
        sb.AppendLine($"Single Precision:  {(r.FullPrecision > 0 ? r.FullPrecision.ToString("N0") : "—")}");
        sb.AppendLine($"Half Precision:    {(r.HalfPrecision  > 0 ? r.HalfPrecision.ToString("N0")  : "—")}");
        sb.AppendLine($"Quantized:         {(r.Quantized      > 0 ? r.Quantized.ToString("N0")      : "—")}");
        return sb.ToString();
    }

    private string BuildTrialExport(List<AIBenchmarkResult> results, string label)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Geekbench AI ×3 Trials — {Environment.MachineName}");
        if (_version != null) sb.AppendLine($"Version: {_version}");
        sb.AppendLine($"Backend: {label}");
        sb.AppendLine();

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.AppendLine($"Trial {i + 1}:");
            sb.AppendLine($"  Single Precision:  {(r.FullPrecision > 0 ? r.FullPrecision.ToString("N0") : "—")}");
            sb.AppendLine($"  Half Precision:    {(r.HalfPrecision  > 0 ? r.HalfPrecision.ToString("N0")  : "—")}");
            sb.AppendLine($"  Quantized:         {(r.Quantized      > 0 ? r.Quantized.ToString("N0")      : "—")}");
        }

        sb.AppendLine();
        sb.AppendLine("Averages:");
        var fp32  = results.Select(r => r.FullPrecision).ToList();
        var fp16  = results.Select(r => r.HalfPrecision).ToList();
        var quant = results.Select(r => r.Quantized).ToList();
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
