using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using WinForms  = System.Windows.Forms;
using Color     = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Point     = System.Windows.Point;
using Button    = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;

namespace SpecIQ;

public partial class SpeedometerWindow : Window
{
    // ── State ─────────────────────────────────────────────────────────────

    private SpeedometerBrowser    _browser  = SpeedometerBrowser.WebView2;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer  _clockTimer;
    private readonly Stopwatch        _stopwatch = new();
    private SpeedometerResult?        _result;
    private SpeedometerResult?        _previousResult;
    private bool                      _rundown;

    public SpeedometerWindow()
    {
        InitializeComponent();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => TickClock();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Disable unavailable browsers
        if (SpeedometerService.FindBrowserExe(SpeedometerBrowser.Edge) == null)
        {
            BtnEdge.IsEnabled = false;
            BtnEdge.Opacity   = 0.35;
        }
        if (SpeedometerService.FindBrowserExe(SpeedometerBrowser.Chrome) == null)
        {
            BtnChrome.IsEnabled = false;
            BtnChrome.Opacity   = 0.35;
        }

        SelectBrowserButton(BtnWebView2);

        _previousResult = SpeedometerResult.Load();
        if (_previousResult?.Entries.Count > 0)
        {
            var d   = _previousResult.TotalDuration;
            var dur = d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:D2}m" : $"{d.Minutes}m";
            PreviousSummaryText.Text     = $"{_previousResult.Browser}  ·  {dur}  ·  {_previousResult.IterationCount} iters  ·  avg {_previousResult.Entries.Average(e => e.Score):F1}";
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
        Close();
    }

    // ── Browser selection ─────────────────────────────────────────────────

    private void BrowserBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        _browser = btn.Tag switch
        {
            "Edge"    => SpeedometerBrowser.Edge,
            "Chrome"  => SpeedometerBrowser.Chrome,
            _         => SpeedometerBrowser.WebView2,
        };
        SelectBrowserButton(btn);
    }

    private void SelectBrowserButton(Button selected)
    {
        foreach (var btn in new[] { BtnWebView2, BtnEdge, BtnChrome })
            btn.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        selected.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x60, 0xA5, 0xFA));
    }

    // ── Start ─────────────────────────────────────────────────────────────

    private void StartSingle_Click(object sender, RoutedEventArgs e)  => _ = StartAsync(rundown: false);
    private void StartRundown_Click(object sender, RoutedEventArgs e) => _ = StartAsync(rundown: true);
    private void ViewPrevious_Click(object sender, MouseButtonEventArgs e)
    {
        if (_previousResult != null) ShowResults(_previousResult);
    }

    private async Task StartAsync(bool rundown)
    {
        _rundown = rundown;
        _result  = new SpeedometerResult { Browser = _browser.ToString() };
        _cts     = new CancellationTokenSource();

        var browserLabel = _browser.ToString();
        RunSubtitleText.Text = rundown ? $"{browserLabel}  ·  Battery Rundown" : $"{browserLabel}  ·  Single Run";
        RunScore.Text   = "—";
        RunBattery.Text = "—";
        RunElapsed.Text = "0:00:00";
        RunIterText.Text = "Iteration 1";
        RunLogText.Text  = "";
        RunChart.Children.Clear();
        ShowPanel(RunningPanel);

        var startPower = WinForms.SystemInformation.PowerStatus;
        _result.StartBatteryPct = (int)Math.Clamp(startPower.BatteryLifePercent * 100, 0, 100);

        _stopwatch.Restart();
        _clockTimer.Start();

        var progress = new Progress<string>(msg =>
        {
            RunLogText.Text += msg + "\n";
            RunLogScroll.ScrollToBottom();
        });

        try
        {
            do
            {
                var iteration = _result.Entries.Count + 1;
                RunIterText.Text = $"Iteration {iteration}";

                var power      = WinForms.SystemInformation.PowerStatus;
                var batteryPct = (int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100);
                if (batteryPct is >= 0 and <= 3) break;

                double score;
                if (_browser == SpeedometerBrowser.WebView2)
                    score = await RunWebView2Async(progress, _cts.Token);
                else
                {
                    var exe = SpeedometerService.FindBrowserExe(_browser)
                        ?? throw new InvalidOperationException($"{_browser} not found.");
                    score = await SpeedometerService.RunViaCdpAsync(exe, progress, _cts.Token);
                }

                var elapsed = (int)_stopwatch.Elapsed.TotalSeconds;
                power      = WinForms.SystemInformation.PowerStatus;
                batteryPct = (int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100);

                var entry = new SpeedometerEntry(iteration, score, batteryPct, elapsed);
                _result.Entries.Add(entry);
                _result.Save();

                RunScore.Text = $"{score:F1}";
                RunBattery.Text = $"{batteryPct}%";
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    () => DrawChart(RunChart, _result));

            } while (rundown && !_cts.Token.IsCancellationRequested);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RunLogText.Text += $"\nError: {ex.Message}";
        }
        finally
        {
            _stopwatch.Stop();
            _clockTimer.Stop();
            _cts = null;
        }

        if (_result.Entries.Count > 0)
            ShowResults(_result);
        else
            ShowPanel(ConfigPanel);
    }

    // ── WebView2 run ──────────────────────────────────────────────────────

    private async Task<double> RunWebView2Async(IProgress<string> progress, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<double>();

        // Host the WebView2 in a separate visible window (Vulkan-style: needs a real HWND)
        Window? hostWin = null;
        WebView2? webView = null;

        await Dispatcher.InvokeAsync(() =>
        {
            webView = new WebView2
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment   = System.Windows.VerticalAlignment.Stretch,
            };
            hostWin = new Window
            {
                Title  = "Speedometer 3.1",
                Width  = 1024,
                Height = 768,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = webView,
            };
            hostWin.Closed += (_, _) => tcs.TrySetCanceled();
            hostWin.Show();
        });

        try
        {
            await webView!.EnsureCoreWebView2Async();

            webView.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                var msg = e.TryGetWebMessageAsString();
                if (double.TryParse(msg,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var score))
                    tcs.TrySetResult(score);
            };

            webView.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) return;
                progress.Report("Page loaded. Starting benchmark…");
                await webView.CoreWebView2.ExecuteScriptAsync(
                    SpeedometerService.BuildWebView2Script());
            };

            progress.Report("Navigating to Speedometer 3.1…");
            webView.CoreWebView2.Navigate(SpeedometerService.SpeedometerUrl);

            using var reg = ct.Register(() => tcs.TrySetCanceled());
            var score = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(10), ct);

            await Dispatcher.InvokeAsync(() => hostWin?.Close());
            return score;
        }
        catch
        {
            await Dispatcher.InvokeAsync(() => { try { hostWin?.Close(); } catch { } });
            throw;
        }
    }

    // ── Clock tick ────────────────────────────────────────────────────────

    private void TickClock()
    {
        RunElapsed.Text = _stopwatch.Elapsed.ToString(@"h\:mm\:ss");
        var power = WinForms.SystemInformation.PowerStatus;
        RunBattery.Text = $"{(int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100)}%";
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ── Results ───────────────────────────────────────────────────────────

    private void ShowResults(SpeedometerResult result)
    {
        ResTypeText.Text     = $"SPEEDOMETER 3.1  ·  {result.MachineName}";
        ResDurationText.Text = result.TotalDuration.TotalHours >= 1
            ? $"{(int)result.TotalDuration.TotalHours}h {result.TotalDuration.Minutes:D2}m"
            : $"{result.TotalDuration.Minutes}m {result.TotalDuration.Seconds:D2}s";
        ResIterText.Text = result.IterationCount.ToString();

        if (result.Entries.Count > 0)
        {
            var first = result.Entries[0].Score;
            var last  = result.Entries[^1].Score;
            var avg   = result.Entries.Average(e => e.Score);
            var drop  = first > 0 ? (first - last) * 100.0 / first : 0;
            ResFirst.Text = $"{first:F1}";
            ResLast.Text  = $"{last:F1}";
            ResAvg.Text   = $"{avg:F1}";
            ResDrop.Text  = $"{drop:F1}%";
            ResDrop.Foreground = drop > 20
                ? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))
                : drop > 10
                    ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
                    : new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
        }

        StatsRow.Visibility = result.Entries.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowPanel(ResultsPanel);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => DrawChart(ResChart, result));
    }

    private void ResChart_Loaded(object sender, RoutedEventArgs e)
    {
        var result = _result ?? _previousResult;
        if (result?.Entries.Count > 0) DrawChart(ResChart, result);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null && _previousResult == null) return;
        Clipboard.SetText((_result ?? _previousResult)!.ExportText());
        var btn = (Button)sender;
        btn.Content = "Copied!";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { btn.Content = "Export"; t.Stop(); };
        t.Start();
    }

    // ── Chart ─────────────────────────────────────────────────────────────

    private static void DrawChart(Canvas canvas, SpeedometerResult result)
    {
        var entries = result.Entries;
        canvas.Children.Clear();
        if (entries.Count == 0) return;

        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 10) return;

        const double padL = 46, padR = 12, padT = 10, padB = 24;
        var plotW      = w - padL - padR;
        var plotH      = h - padT - padB;
        var maxElapsed = Math.Max(entries.Max(e => e.ElapsedSeconds), 1);
        var maxScore   = entries.Max(e => e.Score) * 1.08;

        double Px(int sec)    => padL + sec / (double)maxElapsed * plotW;
        double Py(double val) => maxScore > 0 ? padT + (1 - val / maxScore) * plotH : padT + plotH;

        var blue      = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
        var dimW      = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        var dimT      = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        var dotStroke = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00));

        // Grid lines
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

        // Y labels
        for (int i = 0; i <= 4; i++)
        {
            var val   = maxScore * (4 - i) / 4.0;
            var y     = padT + plotH * i / 4.0;
            var label = new TextBlock
            {
                Text       = val >= 1000 ? $"{val / 1000:F1}k" : $"{val:F0}",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 8,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99,
                    blue.Color.R, blue.Color.G, blue.Color.B)),
                Width         = padL - 4,
                TextAlignment = System.Windows.TextAlignment.Right,
            };
            canvas.Children.Add(label);
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 7);
        }

        // X labels
        var xCount = Math.Min(entries.Count, 5);
        for (int i = 0; i < xCount; i++)
        {
            var idx   = i * (entries.Count - 1) / Math.Max(xCount - 1, 1);
            var entry = entries[Math.Min(idx, entries.Count - 1)];
            var t     = TimeSpan.FromSeconds(entry.ElapsedSeconds);
            var label = new TextBlock
            {
                Text       = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}h" : $"{(int)t.TotalMinutes}m",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 8,
                Foreground = dimT,
            };
            canvas.Children.Add(label);
            Canvas.SetLeft(label, Px(entry.ElapsedSeconds) - 10);
            Canvas.SetTop(label, padT + plotH + 6);
        }

        // Score line
        if (entries.Count >= 2)
        {
            var poly = new Polyline
            {
                Stroke          = blue,
                StrokeThickness = 1.5,
                StrokeLineJoin  = PenLineJoin.Round,
            };
            foreach (var e in entries) poly.Points.Add(new Point(Px(e.ElapsedSeconds), Py(e.Score)));
            canvas.Children.Add(poly);
        }

        // Dots
        foreach (var entry in entries)
        {
            var dotColor = entry.BatteryPct > 50 ? Color.FromRgb(0x4A, 0xDE, 0x80)
                         : entry.BatteryPct > 20 ? Color.FromRgb(0xFB, 0xBF, 0x24)
                                                  : Color.FromRgb(0xF8, 0x71, 0x71);
            var dot = new Ellipse
            {
                Width  = 7, Height = 7,
                Fill   = new SolidColorBrush(dotColor),
                Stroke = dotStroke, StrokeThickness = 1,
            };
            canvas.Children.Add(dot);
            Canvas.SetLeft(dot, Px(entry.ElapsedSeconds) - 3.5);
            Canvas.SetTop(dot,  Py(entry.Score)          - 3.5);
        }
    }

    // ── Panel switching ───────────────────────────────────────────────────

    private void ShowPanel(FrameworkElement panel)
    {
        ConfigPanel .Visibility = Visibility.Collapsed;
        RunningPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        panel.Visibility        = Visibility.Visible;
    }
}
