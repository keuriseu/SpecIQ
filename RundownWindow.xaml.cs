using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms    = System.Windows.Forms;
using Color       = System.Windows.Media.Color;
using FontFamily  = System.Windows.Media.FontFamily;
using Point       = System.Windows.Point;
using Button      = System.Windows.Controls.Button;
using Clipboard   = System.Windows.Clipboard;
using MessageBox  = System.Windows.MessageBox;

namespace SpecIQ;

public partial class RundownWindow : Window
{
    // ── Sleep prevention ──────────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint ES_CONTINUOUS       = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED  = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    // ── State ─────────────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer  _clockTimer;
    private readonly Stopwatch        _stopwatch = new();
    private DateTime                  _startTime;
    private RundownResult?            _result;
    private RundownResult?            _previousResult;

    public RundownWindow()
    {
        InitializeComponent();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => TickClock();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _previousResult = RundownResult.Load();
        if (_previousResult?.Entries.Count > 0)
        {
            var d = _previousResult.TotalDuration;
            var dur = d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:D2}m" : $"{d.Minutes}m";
            PreviousSummaryText.Text = $"{_previousResult.BenchmarkType}  ·  {dur}  ·  {_previousResult.IterationCount} iters";
            PreviousResultsBorder.Visibility = Visibility.Visible;
        }
    }

    // ── Window chrome ─────────────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        AllowSleep();
        Close();
    }

    // ── Config panel ──────────────────────────────────────────────────────

    private void StartCpu_Click(object sender, RoutedEventArgs e)    => _ = StartRundownAsync(gpu: false);
    private void StartGpu_Click(object sender, RoutedEventArgs e)    => _ = StartRundownAsync(gpu: true);
    private void StartStress_Click(object sender, RoutedEventArgs e) => _ = StartRundownAsync(gpu: false, stress: true);


    private void ViewPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_previousResult != null) ShowResults(_previousResult);
    }

    // ── Rundown logic ─────────────────────────────────────────────────────

    private async Task StartRundownAsync(bool gpu, bool stress = false)
    {
        var info = await GeekbenchService.CheckAsync();
        if (info.InstalledPath is not { } exePath)
        {
            MessageBox.Show("Geekbench 6 is not installed.", "SpecIQ", MessageBoxButton.OK);
            return;
        }

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

        _result    = new RundownResult { BenchmarkType = stress ? "Stress" : gpu ? "GPU" : "CPU" };
        _startTime = DateTime.Now;
        _cts       = new CancellationTokenSource();

        var typeLabel = stress ? "CPU + GPU Stress" : gpu ? "GPU" : "CPU";
        var verLabel  = info.InstalledVersion != null ? $"Geekbench {info.InstalledVersion}" : "Geekbench";
        RunSubtitleText.Text = $"{verLabel} — {typeLabel}";
        RunLabelA.Text       = _result.LabelA.ToUpperInvariant();
        RunLabelB.Text       = _result.LabelB.ToUpperInvariant();

        ShowPanel(RunningPanel);
        RunLogText.Text     = "";
        RunScoreA.Text      = "—";
        RunScoreB.Text      = "—";
        RunScoreC.Text      = "—";
        RunScoreD.Text      = "—";
        RunIterText.Text    = "Iteration 1";
        RunBatteryText.Text = "—";
        RunElapsedText.Text = "0:00:00";
        StressGpuScoreRow.Visibility = stress ? Visibility.Visible : Visibility.Collapsed;

        _stopwatch.Restart();
        _clockTimer.Start();
        PreventSleep();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        var progress = new Progress<RundownProgress>(OnProgress);

        try
        {
            await GeekbenchService.RundownAsync(exePath, info.InstalledVersion, gpu, stress, _result, _startTime, progress, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RunLogText.Text += $"\nError: {ex.Message}";
        }
        finally
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _stopwatch.Stop();
            _clockTimer.Stop();
            AllowSleep();
            _cts = null;
        }

        if (_result.Entries.Count > 0)
            ShowResults(_result);
        else
            ShowPanel(ConfigPanel);
    }

    private void OnProgress(RundownProgress p)
    {
        // Update log
        if (!string.IsNullOrWhiteSpace(p.StatusLine))
        {
            RunLogText.Text += p.StatusLine + "\n";
            RunLogScroll.ScrollToBottom();
        }

        // Update header when new iteration starts
        RunIterText.Text = $"Iteration {p.Iteration}";

        var power = WinForms.SystemInformation.PowerStatus;
        RunBatteryText.Text = $"{(int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100)}%";

        // Update stats + chart when an iteration completes
        if (p.Completed is { } entry)
        {
            RunScoreA.Text = entry.SingleScore    > 0 ? $"{entry.SingleScore:N0}"    : "—";
            RunScoreB.Text = entry.MultiScore     > 0 ? $"{entry.MultiScore:N0}"     : "—";
            RunScoreC.Text = entry.GpuOpenClScore > 0 ? $"{entry.GpuOpenClScore:N0}" : "—";
            RunScoreD.Text = entry.GpuVulkanScore > 0 ? $"{entry.GpuVulkanScore:N0}" : "—";
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                () => DrawChart(RunChart, _result!));
        }
    }

    private void TickClock()
    {
        var elapsed = _stopwatch.Elapsed;
        RunElapsedText.Text = elapsed.ToString(@"h\:mm\:ss");

        var power = WinForms.SystemInformation.PowerStatus;
        RunBatteryText.Text = $"{(int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100)}%";
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            Dispatcher.BeginInvoke(StopRundown);
    }

    private void StopRundown()
    {
        _cts?.Cancel();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopRundown();

    // ── Results panel ─────────────────────────────────────────────────────

    private void ShowResults(RundownResult result)
    {
        var d = result.TotalDuration;
        ResTypeText.Text     = $"{result.BenchmarkType} RUNDOWN  ·  {result.MachineName}";
        ResDurationText.Text = d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:D2}m" : $"{d.Minutes}m {d.Seconds:D2}s";
        ResIterText.Text     = result.IterationCount.ToString();

        if (result.IsStress && result.Entries.Count > 0)
        {
            var hasVulkan = result.Entries.Any(e => e.GpuVulkanScore > 0);
            ResAvgCpuSingle.Text  = $"{(int)result.Entries.Average(e => e.SingleScore):N0}";
            ResAvgCpuMulti.Text   = $"{(int)result.Entries.Average(e => e.MultiScore):N0}";
            ResAvgGpuOpenCl.Text  = result.Entries.Any(e => e.GpuOpenClScore > 0)
                                    ? $"{(int)result.Entries.Average(e => e.GpuOpenClScore):N0}" : "N/A";
            ResAvgGpuVulkan.Text  = hasVulkan
                                    ? $"{(int)result.Entries.Average(e => e.GpuVulkanScore):N0}" : "N/A";
            StressStatsRow.Visibility  = Visibility.Visible;
            RegularStatsRow.Visibility = Visibility.Collapsed;
        }
        else if (result.Entries.Count > 0)
        {
            var first = result.Entries[0].SingleScore;
            var last  = result.Entries[^1].SingleScore;
            var avg   = (int)result.Entries.Average(e => e.SingleScore);
            var drop  = first > 0 ? (first - last) * 100.0 / first : 0;
            ResFirstText.Text = $"{first:N0}";
            ResLastText.Text  = $"{last:N0}";
            ResAvgText.Text   = $"{avg:N0}";
            ResDropText.Text  = $"{drop:F1}%";
            ResDropText.Foreground = drop > 20
                ? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))
                : drop > 10
                    ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
                    : new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            StressStatsRow.Visibility  = Visibility.Collapsed;
            RegularStatsRow.Visibility = Visibility.Visible;
        }

        ShowPanel(ResultsPanel);

        // Draw chart after layout
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            () => DrawChart(ResChart, result));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null && _previousResult == null) return;
        var text = (_result ?? _previousResult)!.ExportText();
        Clipboard.SetText(text);

        // Brief visual feedback
        var btn = (Button)sender;
        btn.Content = "Copied!";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { btn.Content = "Export"; t.Stop(); };
        t.Start();
    }

    // ── Chart ─────────────────────────────────────────────────────────────

    private void RunChart_Loaded(object sender, RoutedEventArgs e)
    {
        if (_result?.Entries.Count > 0) DrawChart(RunChart, _result);
    }

    private void ResChart_Loaded(object sender, RoutedEventArgs e)
    {
        var result = _result ?? _previousResult;
        if (result?.Entries.Count > 0) DrawChart(ResChart, result);
    }

    private static void DrawChart(Canvas canvas, RundownResult result)
    {
        var entries = result.Entries;
        canvas.Children.Clear();
        if (entries.Count == 0) return;

        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 10) return;

        const double padL = 46, padR = 46, padT = 10, padB = 24;
        var plotW      = w - padL - padR;
        var plotH      = h - padT - padB;
        var maxElapsed = Math.Max(entries.Max(e => e.ElapsedSeconds), 1);

        double Px(int sec) => padL + sec / (double)maxElapsed * plotW;

        var blue   = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
        var orange = new SolidColorBrush(Color.FromRgb(0xFB, 0x92, 0x3C));
        var green  = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
        var purple = new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC));
        var dimW   = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        var dimT   = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        var dotStroke = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00));

        // ── Grid lines ────────────────────────────────────────────────────
        for (int i = 0; i <= 4; i++)
        {
            var y = padT + plotH * i / 4.0;
            canvas.Children.Add(new Line
            {
                X1 = padL, X2 = padL + plotW, Y1 = y, Y2 = y,
                Stroke = dimW, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            });
        }

        // ── X labels ──────────────────────────────────────────────────────
        var xCount = Math.Min(entries.Count, 5);
        for (int i = 0; i < xCount; i++)
        {
            var idx   = i * (entries.Count - 1) / Math.Max(xCount - 1, 1);
            var entry = entries[Math.Min(idx, entries.Count - 1)];
            var t     = TimeSpan.FromSeconds(entry.ElapsedSeconds);
            var label = new TextBlock
            {
                Text = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}h" : $"{(int)t.TotalMinutes}m",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 8, Foreground = dimT
            };
            canvas.Children.Add(label);
            Canvas.SetLeft(label, Px(entry.ElapsedSeconds) - 10);
            Canvas.SetTop(label, padT + plotH + 6);
        }

        // ── Y-axis label helper ───────────────────────────────────────────
        void AddYLabel(double score, double y, bool leftSide, SolidColorBrush color)
        {
            var label = new TextBlock
            {
                Text      = FormatScore(score),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize  = 8,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, color.Color.R, color.Color.G, color.Color.B))
            };
            if (leftSide)
            {
                label.Width         = padL - 4;
                label.TextAlignment = System.Windows.TextAlignment.Right;
                Canvas.SetLeft(label, 0);
            }
            else
            {
                Canvas.SetLeft(label, padL + plotW + 4);
            }
            canvas.Children.Add(label);
            Canvas.SetTop(label, y - 7);
        }

        // ── Dot helper ────────────────────────────────────────────────────
        void AddDot(double x, double y, SolidColorBrush fill, double r = 3.5)
        {
            var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill, Stroke = dotStroke, StrokeThickness = 1 };
            canvas.Children.Add(dot);
            Canvas.SetLeft(dot, x - r);
            Canvas.SetTop(dot, y - r);
        }

        // ── Polyline helper ───────────────────────────────────────────────
        void AddLine(Func<RundownEntry, Point> pt, SolidColorBrush color)
        {
            if (entries.Count < 2) return;
            var poly = new Polyline { Stroke = color, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
            foreach (var e in entries) poly.Points.Add(pt(e));
            canvas.Children.Add(poly);
        }

        if (result.IsStress)
        {
            // ── Stress: 2-4 lines — left axis = CPU, right axis = GPU ─────
            var hasVulkan = entries.Any(e => e.GpuVulkanScore > 0);
            var maxCpu    = Math.Max(entries.Max(e => e.SingleScore), entries.Max(e => e.MultiScore)) * 1.08;
            var maxGpu    = entries.Max(e => Math.Max(e.GpuOpenClScore, e.GpuVulkanScore)) * 1.08;

            double PyCpu(double s) => maxCpu > 0 ? padT + (1 - s / maxCpu) * plotH : padT + plotH;
            double PyGpu(double s) => maxGpu > 0 ? padT + (1 - s / maxGpu) * plotH : padT + plotH;

            // Y labels: left = blue (CPU), right = green (GPU)
            for (int i = 0; i <= 4; i++)
            {
                AddYLabel(maxCpu * (4 - i) / 4.0, padT + plotH * i / 4.0, leftSide: true,  blue);
                if (maxGpu > 0)
                    AddYLabel(maxGpu * (4 - i) / 4.0, padT + plotH * i / 4.0, leftSide: false, green);
            }

            // Lines back-to-front: GPU Vulkan (if present) → GPU OpenCL → CPU Multi → CPU Single
            if (hasVulkan)
                AddLine(e => new Point(Px(e.ElapsedSeconds), PyGpu(e.GpuVulkanScore)), purple);
            AddLine(e => new Point(Px(e.ElapsedSeconds), PyGpu(e.GpuOpenClScore)), green);
            AddLine(e => new Point(Px(e.ElapsedSeconds), PyCpu(e.MultiScore)),     orange);
            AddLine(e => new Point(Px(e.ElapsedSeconds), PyCpu(e.SingleScore)),    blue);

            // Dots: battery-colored
            foreach (var entry in entries)
            {
                var dotColor = entry.BatteryPct > 50 ? Color.FromRgb(0x4A, 0xDE, 0x80)
                             : entry.BatteryPct > 20 ? Color.FromRgb(0xFB, 0xBF, 0x24)
                                                     : Color.FromRgb(0xF8, 0x71, 0x71);
                var fill = new SolidColorBrush(dotColor);
                var x    = Px(entry.ElapsedSeconds);
                AddDot(x, PyCpu(entry.SingleScore), fill, r: 3.0);
                AddDot(x, PyCpu(entry.MultiScore),  fill, r: 3.0);
                if (maxGpu > 0)
                {
                    AddDot(x, PyGpu(entry.GpuOpenClScore), fill, r: 3.0);
                    if (hasVulkan)
                        AddDot(x, PyGpu(entry.GpuVulkanScore), fill, r: 3.0);
                }
            }
        }
        else
        {
            // ── CPU / GPU: 2 lines — left = A (blue), right = B (orange) ─
            var maxA = entries.Max(e => e.SingleScore) * 1.08;
            var maxB = entries.Max(e => e.MultiScore)  * 1.08;

            double PyA(double s) => maxA > 0 ? padT + (1 - s / maxA) * plotH : padT + plotH;
            double PyB(double s) => maxB > 0 ? padT + (1 - s / maxB) * plotH : padT + plotH;

            // Y labels
            for (int i = 0; i <= 4; i++)
            {
                AddYLabel(maxA * (4 - i) / 4.0, padT + plotH * i / 4.0, leftSide: true,  blue);
                if (maxB > 0)
                    AddYLabel(maxB * (4 - i) / 4.0, padT + plotH * i / 4.0, leftSide: false, orange);
            }

            // Lines
            AddLine(e => new Point(Px(e.ElapsedSeconds), PyB(e.MultiScore)),  orange);
            AddLine(e => new Point(Px(e.ElapsedSeconds), PyA(e.SingleScore)), blue);

            // Dots
            foreach (var entry in entries)
            {
                var dotColor = entry.BatteryPct > 50 ? Color.FromRgb(0x4A, 0xDE, 0x80)
                             : entry.BatteryPct > 20 ? Color.FromRgb(0xFB, 0xBF, 0x24)
                                                     : Color.FromRgb(0xF8, 0x71, 0x71);
                var fill = new SolidColorBrush(dotColor);
                var x    = Px(entry.ElapsedSeconds);
                AddDot(x, PyA(entry.SingleScore), fill);
                if (maxB > 0)
                    AddDot(x, PyB(entry.MultiScore), fill);
            }
        }
    }

    private static string FormatScore(double score) =>
        score >= 10_000 ? $"{score / 1000:F0}k" :
        score >=  1_000 ? $"{score / 1000:F1}k" :
                          $"{score:F0}";

    // ── Panel switching ───────────────────────────────────────────────────

    private void ShowPanel(FrameworkElement panel)
    {
        ConfigPanel .Visibility = Visibility.Collapsed;
        RunningPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        panel.Visibility        = Visibility.Visible;
    }

    // ── Sleep prevention ──────────────────────────────────────────────────

    private static void PreventSleep() =>
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

    private static void AllowSleep() =>
        SetThreadExecutionState(ES_CONTINUOUS);
}
