using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms   = System.Windows.Forms;
using Color      = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Button     = System.Windows.Controls.Button;
using Clipboard  = System.Windows.Clipboard;
using Rectangle  = System.Windows.Shapes.Rectangle;

namespace SpecIQ;

public partial class ProcyonEssentialsWindow : Window
{
    // ── Sleep prevention ──────────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint ES_CONTINUOUS       = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED  = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    // ── State ─────────────────────────────────────────────────────────────

    private string?                            _exePath;
    private CancellationTokenSource?           _cts;
    private readonly DispatcherTimer           _clockTimer;
    private readonly Stopwatch                 _stopwatch = new();
    private ProcyonEssentialsRundownResult?    _result;
    private ProcyonEssentialsRundownResult?    _previousResult;
    private bool                               _rundown;
    private int                                _maxIterations;
    private int                                _tickCount;

    private static readonly Color Accent = Color.FromRgb(0xF9, 0x73, 0x16);

    public ProcyonEssentialsWindow()
    {
        InitializeComponent();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => TickClock();
        Loaded += OnLoaded;

        // Redraw the running chart whenever the canvas goes from zero to non-zero
        // size — this happens when the screen wakes after being off, because WPF
        // suspends rendering while the display is dark (ActualWidth stays 0) so
        // the DrawChart no-op guard fires and the chart stays blank until triggered.
        RunChart.SizeChanged += (_, _) =>
        {
            if (_result?.Entries.Count > 0)
                DrawChart(RunChart, _result);
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _exePath = ProcyonService.FindEssentialsInstalled();

        if (_exePath == null)
        {
            StatusText.Text      = "Not installed — install Procyon from ul.com/benchmarks/procyon";
            SingleBtn.IsEnabled  = false;
            TrialsBtn.IsEnabled  = false;
            RundownBtn.IsEnabled = false;
        }
        else
        {
            StatusText.Text = "Procyon Essentials ready";
        }

        _previousResult = ProcyonEssentialsRundownResult.Load();
        if (_previousResult?.Entries.Count > 0)
        {
            var avg = (int)_previousResult.Entries.Average(e => e.Score);
            PreviousSummaryText.Text = $"{AppHelpers.FormatDuration(_previousResult.TotalDuration)}"
                                    + $"  ·  {_previousResult.IterationCount} iters  ·  avg {avg:N0}";
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
        _clockTimer.Stop();
        Close();
    }

    // ── Start ─────────────────────────────────────────────────────────────

    private void StartSingle_Click(object sender, RoutedEventArgs e)  => _ = StartAsync(rundown: false, maxIterations: 1);
    private void StartTrials_Click(object sender, RoutedEventArgs e)  => _ = StartAsync(rundown: false, maxIterations: 3);
    private void StartRundown_Click(object sender, RoutedEventArgs e) => _ = StartAsync(rundown: true,  maxIterations: 0);

    private void ViewPrevious_Click(object sender, MouseButtonEventArgs e)
    {
        if (_previousResult == null) return;
        _maxIterations = _previousResult.Entries.Count == 3 ? 3 : 0;
        ShowResults(_previousResult);
    }

    private async Task StartAsync(bool rundown, int maxIterations)
    {
        if (_exePath == null) return;

        _rundown       = rundown;
        _maxIterations = maxIterations;
        _result           = new ProcyonEssentialsRundownResult();
        _result.IsRundown = rundown;
        _cts              = new CancellationTokenSource();
        _tickCount        = 0;

        RunSubtitleText.Text = maxIterations == 3 ? "3 Trials"
                             : rundown            ? "Battery Rundown"
                                                  : "Single Run";
        RunScore.Text    = "—";
        RunBattery.Text  = "—";
        RunElapsed.Text  = "0:00:00";
        RunIterText.Text = "Iteration 1";
        RunLogText.Text  = "";
        RunChart.Children.Clear();
        ShowPanel(RunningPanel);

        var startPower = WinForms.SystemInformation.PowerStatus;
        _result.StartBatteryPct = (int)Math.Clamp(startPower.BatteryLifePercent * 100, 0, 100);

        _stopwatch.Restart();
        _clockTimer.Start();

        if (rundown)
        {
            PreventSleep();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        AppLog.Trim("procyon_essentials");
        using var log = new AppLog("procyon_essentials");
        log.Write($"Mode: {(rundown ? "Rundown" : maxIterations == 3 ? "3 Trials" : "Single")}");
        log.Write($"Exe: {_exePath}");
        log.Write($"Battery: {_result.StartBatteryPct}%");
        log.Write("");

        IProgress<string> rawProgress = new Progress<string>(msg =>
        {
            RunLogText.Text += msg + "\n";
            RunLogScroll.ScrollToBottom();
        });
        IProgress<string> progress = log.Tee(rawProgress);

        BenchmarkGuard.Begin();
        try
        {
            // 2 (not 3): each failed attempt burns ~6–20 min on a throttled JVM,
            // so stopping after two consecutive failures preserves battery and avoids
            // a stuck rundown.
            const int MaxConsecutiveFails = 2;
            var consecutiveFails = 0;
            var attempts = 0;
            do
            {
                attempts++;
                var iteration = _result.Entries.Count + 1;
                RunIterText.Text = $"Iteration {iteration}";
                log.Write($"--- Iteration {iteration} start ---");

                if (attempts > 1)
                {
                    const int CooldownSeconds = 30;
                    progress.Report($"[Waiting {CooldownSeconds} s for Procyon cleanup before iteration {iteration}...]");
                    log.Write($"Waiting {CooldownSeconds} s for cleanup before iteration {iteration}...");
                    var cooldownEnd = _stopwatch.Elapsed + TimeSpan.FromSeconds(CooldownSeconds);
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        var remaining = cooldownEnd - _stopwatch.Elapsed;
                        if (remaining <= TimeSpan.Zero) break;
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500, remaining.TotalMilliseconds)), _cts.Token);
                    }
                }

                var power = WinForms.SystemInformation.PowerStatus;
                if (power.BatteryLifePercent is >= 0 and <= 0.03f) break;

                ProcyonEssentialsResult r;
                try
                {
                    r = await ProcyonService.RunEssentialsAsync(_exePath, progress, _cts.Token);
                }
                catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true) { throw; }
                catch (OperationCanceledException)
                {
                    // Per-iteration 30-min timeout fired (Procyon hung mid-workload).
                    // Procyon and javaw are already killed by RunEssentialsAsync's finally block.
                    // Skip this iteration and continue the rundown.
                    log.Write($"Iteration {iteration} timed out — Procyon did not produce a result. Skipping...");
                    progress.Report($"[Iteration {iteration} skipped — Procyon timed out]");
                    consecutiveFails++;
                    if (consecutiveFails >= MaxConsecutiveFails)
                    {
                        progress.Report($"[{consecutiveFails} consecutive failures — stopping rundown]");
                        log.Write($"{consecutiveFails} consecutive failures — stopping rundown.");
                        break;
                    }
                    continue;
                }
                catch (Exception ex)
                {
                    log.Write(ex, $"Iteration {iteration}");
                    progress.Report($"[Iteration {iteration} failed: {ex.Message}]");
                    consecutiveFails++;
                    if (consecutiveFails >= MaxConsecutiveFails)
                    {
                        progress.Report($"[{consecutiveFails} consecutive failures — stopping rundown]");
                        log.Write($"{consecutiveFails} consecutive failures — stopping rundown.");
                        break;
                    }
                    continue;
                }

                if (r.OverallScore == 0)
                {
                    progress.Report($"[Iteration {iteration}: no score returned, skipping]");
                    consecutiveFails++;
                    if (consecutiveFails >= MaxConsecutiveFails)
                    {
                        progress.Report($"[{consecutiveFails} consecutive failures — stopping rundown]");
                        log.Write($"{consecutiveFails} consecutive failures — stopping rundown.");
                        break;
                    }
                    continue;
                }

                consecutiveFails = 0;
                power = WinForms.SystemInformation.PowerStatus;
                var batteryPct = (int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100);
                var elapsed    = (int)_stopwatch.Elapsed.TotalSeconds;

                var entry = new ProcyonEssentialsEntry(
                    iteration, r.OverallScore,
                    r.FileScore, r.AppStartupScore, r.VideoCallScore,
                    r.BrowserTabsScore, r.BrowserScore,
                    batteryPct, elapsed);
                _result.Entries.Add(entry);
                _result.Save();
                SaveHistory(_result, isTrials: false, (int)_stopwatch.Elapsed.TotalSeconds);
                log.Write(
                    $"Iteration {iteration} score: {r.OverallScore}" +
                    $"  File:{r.FileScore} App:{r.AppStartupScore} Video:{r.VideoCallScore}" +
                    $" Tabs:{r.BrowserTabsScore} Browser:{r.BrowserScore}" +
                    $"  Battery:{batteryPct}%");

                RunScore.Text   = $"{_result.Entries.Average(e => e.Score):N0}";
                RunBattery.Text = $"{batteryPct}%";
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => DrawChart(RunChart, _result));

            } while (!_cts.Token.IsCancellationRequested &&
                     (rundown || (_maxIterations > 0 && attempts < _maxIterations)));
        }
        catch (OperationCanceledException)
        {
            var power      = WinForms.SystemInformation.PowerStatus;
            var endBattery = (int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100);
            var incomplete = _result.Entries.Count + 1;
            _result.EndBatteryPct       = endBattery;
            _result.TotalElapsedSeconds = (int)_stopwatch.Elapsed.TotalSeconds;
            _result.IncompleteIteration = incomplete;
            _result.Save();

            if (power.PowerLineStatus == WinForms.PowerLineStatus.Online)
                log.Write($"Run stopped — charger connected. Iteration {incomplete} did not complete.");
            else if (endBattery <= 5)
                log.Write($"Iteration {incomplete} cancelled — battery at {endBattery}% (did not complete).");
            else
                log.Write($"Run cancelled. Iteration {incomplete} did not complete.");
        }
        catch (Exception ex)
        {
            log.Write(ex, "StartAsync");
            RunLogText.Text += $"\nError: {ex.Message}";
        }
        finally
        {
            BenchmarkGuard.End();
            if (rundown)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                AllowSleep();
            }
            _stopwatch.Stop();
            _clockTimer.Stop();
            _cts = null;
        }

        if (_result.Entries.Count > 0)
            ShowResults(_result);
        else
            ShowPanel(ConfigPanel);
    }

    // ── Clock tick ────────────────────────────────────────────────────────

    private void TickClock()
    {
        var elapsed = _stopwatch.Elapsed;
        RunElapsed.Text = elapsed.ToString(@"h\:mm\:ss");
        var power = WinForms.SystemInformation.PowerStatus;
        RunBattery.Text = $"{(int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100)}%";

        if (_rundown)
        {
            // Renew every tick — Modern-Standby (S0ix) platforms can reset the
            // display-wake state during platform power transitions, so a single
            // ES_CONTINUOUS call at startup is not reliable.
            PreventSleep();

            // Stop proactively at ≤5% so the machine never reaches Windows'
            // critical-battery hibernate threshold (~4%) mid-iteration.
            // An Essentials iteration takes ~25 min; reaching 5% means there is
            // no margin to finish safely.
            var batteryPct = (int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100);
            if (batteryPct > 0 && batteryPct <= 5)
                _cts?.Cancel();

            if (_result != null && ++_tickCount % 60 == 0)
            {
                _result.TotalElapsedSeconds = (int)elapsed.TotalSeconds;
                _result.Save();
            }
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange)
        {
            // StatusChange is fired by the OS power stack even during Modern Standby
            // (S0ix), making it more reliable than TickClock for catching low-battery
            // conditions when DispatcherTimer is paused.
            var power = WinForms.SystemInformation.PowerStatus;
            if (power.PowerLineStatus == WinForms.PowerLineStatus.Online)
                Dispatcher.BeginInvoke(() => _cts?.Cancel());
            else if (power.BatteryLifePercent is > 0 and <= 0.05f)
                Dispatcher.BeginInvoke(() => _cts?.Cancel());
        }
        else if (e.Mode == PowerModes.Suspend)
        {
            // Machine is about to hibernate or sleep — cancel now so SpecIQ is
            // not in a half-started iteration state when it wakes up.  The token
            // fires synchronously here; Procyon cleanup happens in the finally block.
            _cts?.Cancel();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            // Machine just woke from sleep/hibernate.  Even if Suspend fired first,
            // the resume handler is a safety net: if Windows froze the process before
            // the Suspend cancel could propagate, cancel it now.
            Dispatcher.BeginInvoke(() => _cts?.Cancel());
        }
    }

    // Called once at rundown start and renewed every clock tick.
    // A single ES_CONTINUOUS call is insufficient on ARM64/Modern-Standby laptops:
    // the platform can reset the display-wake state during S0ix transitions, so
    // periodic renewal (≤ 60 s) is required to guarantee the screen stays on.
    private static void PreventSleep() =>
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

    private static void AllowSleep() =>
        SetThreadExecutionState(ES_CONTINUOUS);

    // ── Results ───────────────────────────────────────────────────────────

    private void ShowResults(ProcyonEssentialsRundownResult result)
    {
        ResTypeText.Text     = $"PROCYON ESSENTIALS  ·  {result.MachineName}";
        ResDurationText.Text = AppHelpers.FormatDuration(result.TotalDuration);
        ResIterText.Text     = result.IterationCount.ToString();

        var isTrials = _maxIterations == 3 && result.Entries.Count <= 3;

        if (isTrials)
        {
            ResTrial1.Text    = result.Entries.Count > 0 ? $"{result.Entries[0].Score:N0}" : "—";
            ResTrial2.Text    = result.Entries.Count > 1 ? $"{result.Entries[1].Score:N0}" : "—";
            ResTrial3.Text    = result.Entries.Count > 2 ? $"{result.Entries[2].Score:N0}" : "—";
            ResTrialsAvg.Text = $"{(int)result.Entries.Average(e => e.Score):N0}";
        }
        else if (result.Entries.Count > 1)
        {
            ResFirst.Text = $"{result.Entries[0].Score:N0}";
            ResLast.Text  = $"{result.Entries[^1].Score:N0}";
            ResAvg.Text   = $"{(int)result.Entries.Average(e => e.Score):N0}";

            if (result.IsRundown)
            {
                ResStartBat.Text = result.StartBatteryPct >= 0 ? $"{result.StartBatteryPct}%" : "—";
                var endBat = result.EndBatteryPct >= 0 ? result.EndBatteryPct : result.Entries[^1].BatteryPct;
                ResEndBat.Text  = $"{endBat}%";
                ResEndedAt.Text = DateTime.Parse(result.StartedAt).Add(result.TotalDuration).ToString("h:mm tt");
            }
        }

        var showStats = !isTrials && result.Entries.Count > 1;
        TrialsRow.Visibility  = isTrials  ? Visibility.Visible : Visibility.Collapsed;
        StatsRow.Visibility   = showStats ? Visibility.Visible : Visibility.Collapsed;
        BatteryRow.Visibility = showStats && result.IsRundown ? Visibility.Visible : Visibility.Collapsed;

        // Sub-score breakdown: show average across all entries
        if (result.Entries.Count > 0)
        {
            ResFile.Text    = $"{(int)result.Entries.Average(e => e.FileScore):N0}";
            ResApp.Text     = $"{(int)result.Entries.Average(e => e.AppStartupScore):N0}";
            ResVideo.Text   = $"{(int)result.Entries.Average(e => e.VideoCallScore):N0}";
            ResTabs.Text    = $"{(int)result.Entries.Average(e => e.BrowserTabsScore):N0}";
            ResBrowser.Text = $"{(int)result.Entries.Average(e => e.BrowserScore):N0}";
            SubScoresBorder.Visibility = Visibility.Visible;
        }

        if (result.IncompleteIteration.HasValue)
        {
            var endBat = result.EndBatteryPct >= 0 ? result.EndBatteryPct : 0;
            ResIncompleteNote.Text       = $"⚠  Iteration {result.IncompleteIteration} did not complete" +
                                           (endBat > 0 ? $" — run cancelled at {endBat}% battery." : ".");
            ResIncompleteNote.Visibility = Visibility.Visible;
        }
        else
        {
            ResIncompleteNote.Visibility = Visibility.Collapsed;
        }

        ShowPanel(ResultsPanel);
        SaveHistory(result, isTrials, (int)result.TotalDuration.TotalSeconds);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => DrawChart(ResChart, result));
    }

    private void ResChart_Loaded(object sender, RoutedEventArgs e)
    {
        var result = _result ?? _previousResult;
        if (result?.Entries.Count > 0) DrawChart(ResChart, result);
    }

    private static void SaveHistory(ProcyonEssentialsRundownResult result, bool isTrials, int durationSeconds)
    {
        if (result.Entries.Count == 0) return;
        var avg  = (int)result.Entries.Average(e => e.Score);
        var note = isTrials                  ? "×3 trials avg"
                 : result.Entries.Count == 1 ? "Single run"
                 : $"Rundown  ·  {result.IterationCount} iters";
        BenchmarkHistory.Upsert(new HistoryEntry
        {
            Tool            = HistoryTool.ProcyonEssentials,
            RunAt           = result.StartedAt,
            Note            = note,
            ScoreA          = avg,
            DurationSeconds = durationSeconds,
        });
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null && _previousResult == null) return;
        Clipboard.SetText((_result ?? _previousResult)!.ExportText());
        AppHelpers.FlashButton((Button)sender);
    }

    // ── Chart ─────────────────────────────────────────────────────────────

    private static void DrawChart(Canvas canvas, ProcyonEssentialsRundownResult result)
    {
        var entries = result.Entries;
        canvas.Children.Clear();
        if (entries.Count == 0) return;

        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 10) return;

        const double padL = 46, padR = 12, padT = 10, padB = 24;
        var plotW    = w - padL - padR;
        var plotH    = h - padT - padB;
        var n        = entries.Count;
        var maxScore = entries.Max(e => e.Score) * 1.08;

        double Py(double val) => maxScore > 0 ? padT + (1 - val / maxScore) * plotH : padT + plotH;

        var dimW = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        var dimT = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

        // Grid lines + Y labels
        for (int i = 0; i <= 4; i++)
        {
            var y = padT + plotH * i / 4.0;
            canvas.Children.Add(new Line
            {
                X1 = padL, X2 = padL + plotW, Y1 = y, Y2 = y,
                Stroke = dimW, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            });
            var val = maxScore * (4 - i) / 4.0;
            var lbl = new TextBlock
            {
                Text          = val >= 1000 ? $"{val / 1000:F1}k" : $"{val:F0}",
                FontFamily    = new FontFamily("Segoe UI"),
                FontSize      = 8,
                Foreground    = new SolidColorBrush(Color.FromArgb(0x99, Accent.R, Accent.G, Accent.B)),
                Width         = padL - 4,
                TextAlignment = System.Windows.TextAlignment.Right,
            };
            canvas.Children.Add(lbl);
            Canvas.SetLeft(lbl, 0);
            Canvas.SetTop(lbl, y - 7);
        }

        // Bars
        const double gap = 4;
        var slotW = plotW / n;
        var barW  = Math.Max(slotW - gap, 2);

        var maxIdx = Enumerable.Range(0, n).MaxBy(i => entries[i].Score);
        var minIdx = Enumerable.Range(0, n).MinBy(i => entries[i].Score);
        bool ShowLabel(int i) => n <= 4 || i == maxIdx || i == minIdx;

        for (int i = 0; i < n; i++)
        {
            var entry  = entries[i];
            var barTop = Py(entry.Score);
            var barH   = plotH + padT - barTop;
            var barX   = padL + i * slotW + (slotW - barW) / 2.0;

            var barColor = entry.BatteryPct > 50 ? Color.FromArgb(0xCC, 0x4A, 0xDE, 0x80)
                         : entry.BatteryPct > 20 ? Color.FromArgb(0xCC, 0xFB, 0xBF, 0x24)
                                                 : Color.FromArgb(0xCC, 0xF8, 0x71, 0x71);

            var rect = new Rectangle
            {
                Width   = barW,
                Height  = Math.Max(barH, 1),
                Fill    = new SolidColorBrush(barColor),
                RadiusX = 3, RadiusY = 3,
            };
            canvas.Children.Add(rect);
            Canvas.SetLeft(rect, barX);
            Canvas.SetTop(rect, barTop);

            if (ShowLabel(i))
            {
                var scoreLbl = new TextBlock
                {
                    Text          = $"{entry.Score:N0}",
                    FontFamily    = new FontFamily("Segoe UI"),
                    FontSize      = 7.5,
                    Foreground    = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                    Width         = barW + 6,
                    TextAlignment = System.Windows.TextAlignment.Center,
                };
                canvas.Children.Add(scoreLbl);
                Canvas.SetLeft(scoreLbl, barX - 3);
                var labelTop = barTop - 12;
                if (labelTop < padT) labelTop = barH > 16 ? barTop + 2 : padT;
                Canvas.SetTop(scoreLbl, labelTop);
            }

            var xLbl = new TextBlock
            {
                Text          = $"#{entry.Iteration}",
                FontFamily    = new FontFamily("Segoe UI"),
                FontSize      = 8,
                Foreground    = dimT,
                Width         = slotW,
                TextAlignment = System.Windows.TextAlignment.Center,
            };
            canvas.Children.Add(xLbl);
            Canvas.SetLeft(xLbl, padL + i * slotW);
            Canvas.SetTop(xLbl, padT + plotH + 6);
        }
    }

    // ── Panel switching ───────────────────────────────────────────────────

    private void ShowPanel(FrameworkElement panel)
    {
        ConfigPanel .Visibility = Visibility.Collapsed;
        RunningPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        panel.Visibility        = Visibility.Visible;
        AppHelpers.FadeIn(panel);
    }
}
