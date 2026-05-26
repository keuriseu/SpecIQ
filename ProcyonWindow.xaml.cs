using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace SpecIQ;

public partial class ProcyonWindow : Window
{
    private string?                  _exePath;
    private CancellationTokenSource? _cts;
    private ProcyonCvBackend         _selectedBackend = ProcyonCvBackend.GpuF16;
    private ProcyonCvBackend         _lastBackend     = ProcyonCvBackend.GpuF16;
    private int                      _lastTrials      = 1;
    private readonly DispatcherTimer _dotTimer;
    private int                      _dotFrame;
    private DateTime                 _benchmarkStart;

    private static readonly SolidColorBrush BrushSelected   = new(Color.FromRgb(0x0E, 0xA5, 0xE9));
    private static readonly SolidColorBrush BrushNpuSelected = new(Color.FromRgb(0x05, 0x96, 0x69));
    private static readonly SolidColorBrush BrushUnselected = new(Color.FromRgb(0x1E, 0x29, 0x3B));

    public ProcyonWindow()
    {
        InitializeComponent();

        _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dotTimer.Tick += (_, _) => AnimateDots();

        Loaded += (_, _) => Init();
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

    // ── Initialise ────────────────────────────────────────────────────────

    private void Init()
    {
        _exePath = ProcyonService.FindInstalled();

        if (_exePath == null)
        {
            StatusText.Text         = "Not installed — install from ul.com/benchmarks/procyon";
            RunSingleBtn.IsEnabled  = false;
            RunTrialsBtn.IsEnabled  = false;
            return;
        }

        StatusText.Text = "v2.12 installed";

        if (ProcyonService.IsNpuAvailable())
            BtnNpuQnn.Visibility = Visibility.Visible;

        UpdateBackendButtons();
        LoadPreviousResult();
    }

    private void LoadPreviousResult()
    {
        var prev = BenchmarkHistory.Load().FirstOrDefault(e => e.Tool == HistoryTool.ProcyonCV);
        if (prev == null) return;

        PrevNoteText.Text  = prev.Note;
        PrevScoreText.Text = prev.ScoreA > 0 ? $"{(int)prev.ScoreA:N0}" : "—";
        PreviousBorder.Visibility = Visibility.Visible;
    }

    // ── Backend picker ────────────────────────────────────────────────────

    private void BtnCpuF32_Click(object sender, RoutedEventArgs e) => SelectBackend(ProcyonCvBackend.CpuF32);
    private void BtnGpuF32_Click(object sender, RoutedEventArgs e) => SelectBackend(ProcyonCvBackend.GpuF32);
    private void BtnGpuF16_Click(object sender, RoutedEventArgs e) => SelectBackend(ProcyonCvBackend.GpuF16);
    private void BtnGpuInt_Click(object sender, RoutedEventArgs e) => SelectBackend(ProcyonCvBackend.GpuInt);
    private void BtnNpuQnn_Click(object sender, RoutedEventArgs e) => SelectBackend(ProcyonCvBackend.NpuQnn);

    private void SelectBackend(ProcyonCvBackend b)
    {
        _selectedBackend = b;
        UpdateBackendButtons();
    }

    private void UpdateBackendButtons()
    {
        BtnCpuF32.Background = _selectedBackend == ProcyonCvBackend.CpuF32 ? BrushSelected : BrushUnselected;
        BtnGpuF32.Background = _selectedBackend == ProcyonCvBackend.GpuF32 ? BrushSelected : BrushUnselected;
        BtnGpuF16.Background = _selectedBackend == ProcyonCvBackend.GpuF16 ? BrushSelected : BrushUnselected;
        BtnGpuInt.Background = _selectedBackend == ProcyonCvBackend.GpuInt ? BrushSelected : BrushUnselected;
        BtnNpuQnn.Background = _selectedBackend == ProcyonCvBackend.NpuQnn ? BrushNpuSelected : BrushUnselected;
    }

    // ── Run ───────────────────────────────────────────────────────────────

    private void RunSingle_Click(object sender, RoutedEventArgs e) => _ = RunAsync(trials: 1);
    private void RunTrials_Click(object sender, RoutedEventArgs e) => _ = RunAsync(trials: 3);
    private void RunAgain_Click(object sender, RoutedEventArgs e)  => _ = RunAsync(_lastTrials);
    private void Rundown_Click(object sender, RoutedEventArgs e)   => ((App)System.Windows.Application.Current).ShowProcyonOffice();

    private async Task RunAsync(int trials)
    {
        if (_exePath == null) return;

        _lastBackend    = _selectedBackend;
        _lastTrials     = trials;
        _benchmarkStart = DateTime.Now;

        var label = ProcyonService.BackendLabel(_selectedBackend);
        RunSubtitleText.Text    = label;
        RunTrialText.Visibility = Visibility.Collapsed;
        RunPhaseText.Text       = "Starting…";
        LogText.Text            = "";
        ShowPanel(RunningPanel);
        _dotTimer.Start();

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        BenchmarkGuard.Begin();

        var results = new List<ProcyonCvResult>();
        try
        {
            for (int i = 0; i < trials && !_cts.Token.IsCancellationRequested; i++)
            {
                int trial = i;
                if (trials > 1)
                {
                    RunTrialText.Text       = $"Trial {trial + 1} of {trials}";
                    RunTrialText.Visibility = Visibility.Visible;
                }

                var progress = new Progress<string>(line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    LogText.Text += line + "\n";
                    LogScroll.ScrollToBottom();

                    // Extract meaningful phase text from log lines
                    var phase = ExtractPhase(line);
                    if (phase != null)
                        RunPhaseText.Text = trials > 1 ? $"Trial {trial + 1}  ·  {phase}" : phase;
                });

                var result = await ProcyonService.RunAsync(_exePath, _selectedBackend, progress, _cts.Token);
                results.Add(result);

                if (trials > 1 && trial < trials - 1)
                {
                    RunTrialText.Text = $"Cooldown before Trial {trial + 2} of {trials}";
                    for (int s = 60; s > 0 && !_cts.Token.IsCancellationRequested; s--)
                    {
                        RunPhaseText.Text = $"Cooling down…  {s}s";
                        try { await Task.Delay(1000, _cts.Token); }
                        catch (OperationCanceledException) { break; }
                    }
                    _cts.Token.ThrowIfCancellationRequested();
                }
            }

            if (results.Count == 0) { ShowPanel(ConfigPanel); return; }

            if (trials == 1)
                ShowResults(results[0]);
            else
                ShowTrialResults(results);
        }
        catch (OperationCanceledException) { ShowPanel(ConfigPanel); }
        catch (Exception ex)
        {
            RunPhaseText.Text  = "Error";
            LogText.Text      += $"\n{ex.Message}";
        }
        finally
        {
            BenchmarkGuard.End();
            _dotTimer.Stop();
            _cts?.Dispose(); _cts = null;
        }
    }

    private static string? ExtractPhase(string line)
    {
        var l = line;
        if (l.Contains("MobileNet",  StringComparison.OrdinalIgnoreCase)) return "MobileNetV3";
        if (l.Contains("Inception",  StringComparison.OrdinalIgnoreCase)) return "InceptionV4";
        if (l.Contains("ResNet",     StringComparison.OrdinalIgnoreCase)) return "ResNet50";
        if (l.Contains("DeepLab",    StringComparison.OrdinalIgnoreCase)) return "DeepLabV3";
        if (l.Contains("YOLO",       StringComparison.OrdinalIgnoreCase)) return "YOLOv3";
        if (l.Contains("ESRGAN",     StringComparison.OrdinalIgnoreCase)) return "ESRGAN";
        if (l.Contains("ConvNext",   StringComparison.OrdinalIgnoreCase)) return "ConvNextTiny";
        if (l.Contains("Blip",       StringComparison.OrdinalIgnoreCase)) return "BLIP Base";
        if (l.Contains("complete",   StringComparison.OrdinalIgnoreCase)) return "Completed";
        return null;
    }

    // ── Results ───────────────────────────────────────────────────────────

    private void ShowResults(ProcyonCvResult r)
    {
        ResBackendText.Text = ProcyonService.BackendLabel(r.Backend);
        ResOverall.Text     = r.OverallScore > 0 ? $"{(int)r.OverallScore:N0}" : "—";

        if (r.IsNpu)
        {
            Cv1ModelsGrid.Visibility = Visibility.Collapsed;
            Cv2ModelsGrid.Visibility = Visibility.Visible;
            ResConvNext.Text = Fmt(r.ConvNextTiny);
            ResBlip.Text     = Fmt(r.BlipBase);
            ResVideo.Text    = Fmt(r.Video);
        }
        else
        {
            Cv1ModelsGrid.Visibility = Visibility.Visible;
            Cv2ModelsGrid.Visibility = Visibility.Collapsed;
            ResMobileNet.Text = Fmt(r.MobileNetV3);
            ResInception.Text = Fmt(r.InceptionV4);
            ResResNet.Text    = Fmt(r.ResNet50);
            ResDeepLab.Text   = Fmt(r.DeepLabV3);
            ResYolo.Text      = Fmt(r.YoloV3);
            ResEsrgan.Text    = Fmt(r.Esrgan);
        }

        ShowPanel(ResultsPanel);
        SaveHistory(r, isTrialAvg: false);
        LoadPreviousResult();
    }

    private void ShowTrialResults(List<ProcyonCvResult> results)
    {
        var label = ProcyonService.BackendLabel(_lastBackend);
        TrialHeaderText.Text  = $"×3 TRIALS  ·  AI COMPUTER VISION";
        TrialBackendText.Text = label;

        var scores = results.Select(r => r.OverallScore).ToList();
        TScore1.Text = scores.Count > 0 ? FmtInt(scores[0]) : "—";
        TScore2.Text = scores.Count > 1 ? FmtInt(scores[1]) : "—";
        TScore3.Text = scores.Count > 2 ? FmtInt(scores[2]) : "—";

        // Bold the best
        var best = scores.Max();
        TScore1.FontWeight = scores.Count > 0 && scores[0] == best && best > 0 ? FontWeights.Bold : FontWeights.SemiBold;
        TScore2.FontWeight = scores.Count > 1 && scores[1] == best && best > 0 ? FontWeights.Bold : FontWeights.SemiBold;
        TScore3.FontWeight = scores.Count > 2 && scores[2] == best && best > 0 ? FontWeights.Bold : FontWeights.SemiBold;

        var avg = scores.Average();
        TAvgScore.Text = avg > 0 ? $"{(int)avg:N0}" : "—";

        ShowPanel(TrialResultsPanel);

        // Save as averaged entry
        var avgResult = new ProcyonCvResult
        {
            Backend      = _lastBackend,
            IsNpu        = _lastBackend == ProcyonCvBackend.NpuQnn,
            OverallScore = avg,
            MobileNetV3  = results.Average(r => r.MobileNetV3),
            InceptionV4  = results.Average(r => r.InceptionV4),
            ResNet50     = results.Average(r => r.ResNet50),
            DeepLabV3    = results.Average(r => r.DeepLabV3),
            YoloV3       = results.Average(r => r.YoloV3),
            Esrgan       = results.Average(r => r.Esrgan),
            ConvNextTiny = results.Average(r => r.ConvNextTiny),
            BlipBase     = results.Average(r => r.BlipBase),
            Video        = results.Average(r => r.Video),
        };
        SaveHistory(avgResult, isTrialAvg: true);
        LoadPreviousResult();
    }

    private void SaveHistory(ProcyonCvResult r, bool isTrialAvg)
    {
        var label = ProcyonService.BackendLabel(r.Backend);
        BenchmarkHistory.Append(new HistoryEntry
        {
            Tool            = HistoryTool.ProcyonCV,
            Note            = isTrialAvg ? $"{label}  ×3 avg" : label,
            ScoreA          = r.OverallScore,
            DurationSeconds = (int)(DateTime.Now - _benchmarkStart).TotalSeconds,
        });
    }

    // ── Cancel ────────────────────────────────────────────────────────────

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelBtn.Visibility          = Visibility.Collapsed;
        CancelConfirmPanel.Visibility = Visibility.Visible;
    }

    private void CancelConfirm_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _dotTimer.Stop();
        ShowPanel(ConfigPanel);
    }

    private void CancelDismiss_Click(object sender, RoutedEventArgs e)
    {
        CancelConfirmPanel.Visibility = Visibility.Collapsed;
        CancelBtn.Visibility          = Visibility.Visible;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string Fmt(double v)    => v > 0 ? $"{v:F1}"       : "—";
    private static string FmtInt(double v) => v > 0 ? $"{(int)v:N0}"  : "—";

    private void ShowPanel(FrameworkElement panel)
    {
        ConfigPanel       .Visibility = Visibility.Collapsed;
        RunningPanel      .Visibility = Visibility.Collapsed;
        ResultsPanel      .Visibility = Visibility.Collapsed;
        TrialResultsPanel .Visibility = Visibility.Collapsed;
        panel.Visibility              = Visibility.Visible;
        AppHelpers.FadeIn(panel);

        // Reset cancel UI
        CancelBtn.Visibility          = Visibility.Visible;
        CancelConfirmPanel.Visibility = Visibility.Collapsed;
    }

    private void AnimateDots()
    {
        _dotFrame = (_dotFrame + 1) % 3;
        AppHelpers.SetDotOpacities(_dotFrame, Dot1, Dot2, Dot3);
    }
}
