using System.Diagnostics;
using System.Runtime.InteropServices;
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

    private void StartCpu_Click(object sender, RoutedEventArgs e) => _ = StartRundownAsync(gpu: false);
    private void StartGpu_Click(object sender, RoutedEventArgs e) => _ = StartRundownAsync(gpu: true);

    private void ViewPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_previousResult != null) ShowResults(_previousResult);
    }

    // ── Rundown logic ─────────────────────────────────────────────────────

    private async Task StartRundownAsync(bool gpu)
    {
        var info = await GeekbenchService.CheckAsync();
        if (info.InstalledPath is not { } exePath)
        {
            MessageBox.Show("Geekbench 6 is not installed.", "SpecIQ", MessageBoxButton.OK);
            return;
        }

        _result    = new RundownResult { BenchmarkType = gpu ? "GPU" : "CPU" };
        _startTime = DateTime.Now;
        _cts       = new CancellationTokenSource();

        ShowPanel(RunningPanel);
        RunLogText.Text      = "";
        RunLastScoreText.Text = "—";
        RunIterText.Text     = "Iteration 1";
        RunBatteryText.Text  = "—";
        RunElapsedText.Text  = "0:00:00";

        _clockTimer.Start();
        PreventSleep();

        var progress = new Progress<RundownProgress>(OnProgress);

        try
        {
            await GeekbenchService.RundownAsync(exePath, gpu, _result, _startTime, progress, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RunLogText.Text += $"\nError: {ex.Message}";
        }
        finally
        {
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
            RunLastScoreText.Text = $"{entry.Score:N0}";
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                () => DrawChart(RunChart, _result!.Entries));
        }
    }

    private void TickClock()
    {
        var elapsed = DateTime.Now - _startTime;
        RunElapsedText.Text = elapsed.ToString(@"h\:mm\:ss");

        var power = WinForms.SystemInformation.PowerStatus;
        RunBatteryText.Text = $"{(int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100)}%";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _clockTimer.Stop();
        AllowSleep();

        if (_result?.Entries.Count > 0)
            ShowResults(_result);
        else
            ShowPanel(ConfigPanel);
    }

    // ── Results panel ─────────────────────────────────────────────────────

    private void ShowResults(RundownResult result)
    {
        var d = result.TotalDuration;
        ResTypeText.Text     = $"{result.BenchmarkType} RUNDOWN  ·  {result.MachineName}";
        ResDurationText.Text = d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:D2}m" : $"{d.Minutes}m {d.Seconds:D2}s";
        ResIterText.Text     = result.IterationCount.ToString();

        if (result.Entries.Count > 0)
        {
            var first = result.Entries[0].Score;
            var last  = result.Entries[^1].Score;
            var avg   = (int)result.Entries.Average(e => e.Score);
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
        }

        ShowPanel(ResultsPanel);

        // Draw chart after layout
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            () => DrawChart(ResChart, result.Entries));
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
        if (_result?.Entries.Count > 0) DrawChart(RunChart, _result.Entries);
    }

    private void ResChart_Loaded(object sender, RoutedEventArgs e)
    {
        var result = _result ?? _previousResult;
        if (result?.Entries.Count > 0) DrawChart(ResChart, result.Entries);
    }

    private static void DrawChart(Canvas canvas, List<RundownEntry> entries)
    {
        canvas.Children.Clear();
        if (entries.Count == 0) return;

        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 10) return;

        const double padL = 44, padR = 10, padT = 10, padB = 24;
        var plotW = w - padL - padR;
        var plotH = h - padT - padB;

        var scores     = entries.Select(e => (double)e.Score).ToList();
        var minScore   = scores.Min() * 0.93;
        var maxScore   = scores.Max() * 1.05;
        var maxElapsed = Math.Max(entries.Max(e => e.ElapsedSeconds), 1);

        double Px(int sec)    => padL + sec / (double)maxElapsed * plotW;
        double Py(double scr) => padT + (1 - (scr - minScore) / (maxScore - minScore)) * plotH;

        // Horizontal grid lines + Y labels
        for (int i = 0; i <= 3; i++)
        {
            var score = minScore + (maxScore - minScore) * i / 3.0;
            var y     = Py(score);

            canvas.Children.Add(new Line
            {
                X1 = padL, X2 = padL + plotW, Y1 = y, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            });

            var label = new TextBlock
            {
                Text       = FormatScore(score),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 8,
                Foreground = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
            };
            canvas.Children.Add(label);
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 7);
        }

        // X-axis labels (up to 5 time markers)
        var labelCount = Math.Min(entries.Count, 5);
        for (int i = 0; i < labelCount; i++)
        {
            var idx   = i * (entries.Count - 1) / Math.Max(labelCount - 1, 1);
            var entry = entries[Math.Min(idx, entries.Count - 1)];
            var t     = TimeSpan.FromSeconds(entry.ElapsedSeconds);
            var text  = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}h" : $"{(int)t.TotalMinutes}m";
            var x     = Px(entry.ElapsedSeconds);

            var label = new TextBlock
            {
                Text       = text,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 8,
                Foreground = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
            };
            canvas.Children.Add(label);
            Canvas.SetLeft(label, x - 10);
            Canvas.SetTop(label, padT + plotH + 6);
        }

        // Score line
        if (entries.Count > 1)
        {
            var poly = new Polyline
            {
                Stroke          = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
                StrokeThickness = 1.5,
                StrokeLineJoin  = PenLineJoin.Round
            };
            foreach (var e in entries)
                poly.Points.Add(new Point(Px(e.ElapsedSeconds), Py(e.Score)));
            canvas.Children.Add(poly);
        }

        // Dots colored by battery %
        foreach (var entry in entries)
        {
            var color = entry.BatteryPct > 50
                ? Color.FromRgb(0x4A, 0xDE, 0x80)
                : entry.BatteryPct > 20
                    ? Color.FromRgb(0xFB, 0xBF, 0x24)
                    : Color.FromRgb(0xF8, 0x71, 0x71);

            var dot = new Ellipse
            {
                Width           = 7,
                Height          = 7,
                Fill            = new SolidColorBrush(color),
                Stroke          = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00)),
                StrokeThickness = 1
            };
            canvas.Children.Add(dot);
            Canvas.SetLeft(dot, Px(entry.ElapsedSeconds) - 3.5);
            Canvas.SetTop(dot,  Py(entry.Score)           - 3.5);
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
