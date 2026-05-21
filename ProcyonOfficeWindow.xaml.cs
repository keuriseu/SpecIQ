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

public partial class ProcyonOfficeWindow : Window
{
    // ── Sleep prevention ──────────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint ES_CONTINUOUS       = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED  = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    // ── State ─────────────────────────────────────────────────────────────

    private string?                       _exePath;
    private CancellationTokenSource?      _cts;
    private readonly DispatcherTimer      _clockTimer;
    private readonly Stopwatch            _stopwatch = new();
    private ProcyonOfficeRundownResult?   _result;
    private ProcyonOfficeRundownResult?   _previousResult;
    private bool                          _rundown;
    private bool                          _isLoops5;
    private int                           _maxIterations;
    private int                           _tickCount;
    private OfficeLoopsResult?            _loopsResult;

    private static readonly Color Accent = Color.FromRgb(0x10, 0xB9, 0x81);

    public ProcyonOfficeWindow()
    {
        InitializeComponent();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => TickClock();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _exePath = ProcyonService.FindOfficeInstalled();

        if (_exePath == null)
        {
            StatusText.Text      = "Not installed — install Procyon from ul.com/benchmarks/procyon";
            SingleBtn.IsEnabled  = false;
            TrialsBtn.IsEnabled  = false;
            LoopsBtn.IsEnabled   = false;
            RundownBtn.IsEnabled = false;
        }
        else
        {
            var defName = ProcyonService.OfficeDefName;
            StatusText.Text = defName != null ? $"Installed  ·  {defName}" : "Procyon Office installed";

        }

        _previousResult = ProcyonOfficeRundownResult.Load();
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
    private void StartLoops_Click(object sender, RoutedEventArgs e)   => _ = StartLoops5Async();
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
        _result           = new ProcyonOfficeRundownResult();
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

        AppLog.Trim("procyon_office");
        using var log = new AppLog("procyon_office");
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
            // 2 (not 3): each failed attempt at end-of-rundown burns ~10–20 min of
            // battery on a throttled JVM, so stopping one attempt earlier preserves
            // the remaining charge and avoids wasting it on a third doomed start.
            const int MaxConsecutiveFails = 2;
            var attempts         = 0;
            var consecutiveFails = 0;
            do
            {
                attempts++;
                var iteration = _result.Entries.Count + 1;
                RunIterText.Text = $"Iteration {iteration}";
                log.Write($"--- Iteration {iteration} start ---");

                var power = WinForms.SystemInformation.PowerStatus;

                // Cooldown before every iteration after the first. Procyon's cleanup
                // (OfficeProductivity-Starter.exe --clean) and Office process teardown
                // can keep the system busy for several minutes; starting the next
                // iteration immediately means javaw may not appear until after the
                // startup deadline, causing a spurious "port not found" failure.
                if (attempts > 1)
                {
                    const int CooldownSeconds = 30;
                    progress.Report($"[Waiting {CooldownSeconds} s for cleanup before iteration {iteration}...]");
                    log.Write($"Waiting {CooldownSeconds} s for cleanup before iteration {iteration}...");
                    await Task.Delay(TimeSpan.FromSeconds(CooldownSeconds), _cts.Token);
                }

                ProcyonOfficeResult r;
                try
                {
                    r = await ProcyonService.RunOfficeAsync(_exePath, progress, _cts.Token);
                }
                catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true) { throw; }
                catch (OperationCanceledException)
                {
                    // Per-iteration 45-min timeout fired (Procyon hung mid-workload).
                    // Procyon and javaw are already killed by RunOfficeAsync's finally block.
                    // Skip this iteration and continue the rundown.
                    log.Write($"Iteration {iteration} timed out — Procyon did not produce a result. Skipping...");
                    progress.Report($"[Iteration {iteration} skipped — Procyon timed out after 45 min]");
                    consecutiveFails++;
                    if (consecutiveFails >= MaxConsecutiveFails)
                    {
                        log.Write($"Rundown stopped: {MaxConsecutiveFails} consecutive failures.");
                        progress.Report($"[Rundown stopped after {MaxConsecutiveFails} consecutive failures — Procyon appears stuck]");
                        break;
                    }
                    continue;
                }
                catch (Exception ex)
                {
                    // Log and continue — don't let a transient failure kill the rundown
                    log.Write(ex, $"Iteration {iteration}");
                    progress.Report($"[Iteration {iteration} failed: {ex.Message}]");
                    consecutiveFails++;
                    if (consecutiveFails >= MaxConsecutiveFails)
                    {
                        log.Write($"Rundown stopped: {MaxConsecutiveFails} consecutive failures.");
                        progress.Report($"[Rundown stopped after {MaxConsecutiveFails} consecutive failures — Procyon appears stuck]");
                        break;
                    }
                    continue;
                }

                if (r.OverallScore == 0)
                {
                    consecutiveFails++;
                    progress.Report($"[Iteration {iteration}: no score returned, skipping]");
                    if (consecutiveFails >= MaxConsecutiveFails)
                    {
                        log.Write($"Rundown stopped: {MaxConsecutiveFails} consecutive failures.");
                        progress.Report($"[Rundown stopped after {MaxConsecutiveFails} consecutive failures — Procyon appears stuck]");
                        break;
                    }
                    continue;
                }

                consecutiveFails = 0;

                power = WinForms.SystemInformation.PowerStatus;
                var batteryPct = (int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100);
                var elapsed    = (int)_stopwatch.Elapsed.TotalSeconds;

                var entry = new ProcyonOfficeEntry(
                    iteration, r.OverallScore,
                    r.WordScore, r.ExcelScore, r.PowerPointScore, r.OutlookScore,
                    batteryPct, elapsed);
                _result.Entries.Add(entry);
                _result.Save();
                log.Write($"Iteration {iteration} score: {r.OverallScore}  Word:{r.WordScore} Excel:{r.ExcelScore} PPT:{r.PowerPointScore} Outlook:{r.OutlookScore}  Battery:{batteryPct}%");

                // Keep history current after every iteration so the entry survives
                // even if the machine shuts down before the run completes normally.
                SaveHistory(_result, isTrials: false, durationSeconds: elapsed);

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

        if (_rundown || _isLoops5)
        {
            // Renew every tick — Modern-Standby (S0ix) platforms can reset the
            // display-wake state during platform power transitions, so a single
            // ES_CONTINUOUS call at startup is not reliable.
            PreventSleep();

            // Stop proactively at ≤5% so the machine never reaches Windows'
            // critical-battery hibernate threshold (~4%) mid-iteration.
            // An Office iteration takes ~25–40 min; at 5% there is no margin
            // to finish safely, and letting the machine hibernate mid-run leaves
            // SpecIQ in a zombie state on resume.
            var batteryPct = (int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100);
            if (batteryPct > 0 && batteryPct <= 5)
                _cts?.Cancel();

            // Persist elapsed time every 60 s so it's preserved if the machine dies mid-iteration
            if (++_tickCount % 60 == 0)
            {
                var elapsedSec = (int)elapsed.TotalSeconds;
                if (_rundown && _result != null)
                {
                    _result.TotalElapsedSeconds = elapsedSec;
                    _result.Save();
                }
                else if (_isLoops5 && _loopsResult != null)
                {
                    _loopsResult.TotalElapsedSeconds = elapsedSec;
                    _loopsResult.Save();
                }
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
            // not in a half-started iteration state when it wakes up.
            _cts?.Cancel();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            // Machine just woke from sleep/hibernate.  Safety net: if Windows froze
            // the process before the Suspend cancel propagated, cancel it now so the
            // rundown does not continue on a near-dead battery.
            Dispatcher.BeginInvoke(() => _cts?.Cancel());
        }
    }

    private static void PreventSleep() =>
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

    private static void AllowSleep() =>
        SetThreadExecutionState(ES_CONTINUOUS);

    // ── Results ───────────────────────────────────────────────────────────

    private void ShowResults(ProcyonOfficeRundownResult result)
    {
        ResTypeText.Text     = $"PROCYON OFFICE  ·  {result.MachineName}";
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
            var first = result.Entries[0].Score;
            var last  = result.Entries[^1].Score;
            var avg   = result.Entries.Average(e => e.Score);
            ResFirst.Text = $"{first:N0}";
            ResLast.Text  = $"{last:N0}";
            ResAvg.Text   = $"{avg:N0}";

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
            ResWord.Text    = $"{(int)result.Entries.Average(e => e.WordScore):N0}";
            ResExcel.Text   = $"{(int)result.Entries.Average(e => e.ExcelScore):N0}";
            ResPPT.Text     = $"{(int)result.Entries.Average(e => e.PowerPointScore):N0}";
            ResOutlook.Text = $"{(int)result.Entries.Average(e => e.OutlookScore):N0}";
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

    private static void SaveHistory(ProcyonOfficeRundownResult result, bool isTrials, int durationSeconds)
    {
        if (result.Entries.Count == 0) return;
        var avg  = (int)result.Entries.Average(e => e.Score);
        var note = isTrials                  ? "×3 trials avg"
                 : result.Entries.Count == 1 ? "Single run"
                 : $"Rundown  ·  {result.IterationCount} iters";
        // Upsert so that history is updated as iterations complete — this way the
        // entry is correct even if the machine shuts down before the run finishes.
        BenchmarkHistory.Upsert(new HistoryEntry
        {
            Tool            = HistoryTool.ProcyonOffice,
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

    // ── 5 Loops ───────────────────────────────────────────────────────────

    private async Task StartLoops5Async()
    {
        if (_exePath == null) return;

        _isLoops5    = true;
        _loopsResult = new OfficeLoopsResult();
        _cts         = new CancellationTokenSource();
        _tickCount   = 0;

        RunSubtitleText.Text = "5 Loops";
        RunScore.Text        = "—";
        RunBattery.Text      = "—";
        RunElapsed.Text      = "0:00:00";
        RunIterText.Text     = "Loop 1 / 5";
        RunLogText.Text      = "";
        RunChart.Children.Clear();
        WorkloadRow.Visibility = Visibility.Visible;
        ResetWorkloadPills();
        ShowPanel(RunningPanel);

        var startPower = WinForms.SystemInformation.PowerStatus;
        _loopsResult.StartBatteryPct = (int)Math.Clamp(startPower.BatteryLifePercent * 100, 0, 100);

        _stopwatch.Restart();
        _clockTimer.Start();
        PreventSleep();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        AppLog.Trim("procyon_office");
        using var log = new AppLog("procyon_office");
        log.Write("Mode: 5 Loops");
        log.Write($"Exe: {_exePath}");
        log.Write($"Battery: {_loopsResult.StartBatteryPct}%");
        log.Write("");

        IProgress<string> rawProgress = new Progress<string>(msg =>
        {
            RunLogText.Text += msg + "\n";
            RunLogScroll.ScrollToBottom();
            UpdateWorkloadIndicator(msg);
        });
        IProgress<string> progress = log.Tee(rawProgress);

        BenchmarkGuard.Begin();
        try
        {
            for (int i = 1; i <= 5; i++)
            {
                RunIterText.Text = $"Loop {i} / 5";
                log.Write($"--- Loop {i} start ---");
                ResetWorkloadPills();

                if (i > 1)
                {
                    const int CooldownSeconds = 30;
                    progress.Report($"[Waiting {CooldownSeconds} s for cleanup before loop {i}...]");
                    await Task.Delay(TimeSpan.FromSeconds(CooldownSeconds), _cts.Token);
                }

                try
                {
                    await ProcyonService.RunOfficeAsync(_exePath, progress, _cts.Token);
                }
                catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true) { throw; }
                catch (Exception ex)
                {
                    log.Write(ex, $"Loop {i}");
                    progress.Report($"[Loop {i} failed: {ex.Message}]");
                    // Continue remaining loops even after a failure
                }

                ResetWorkloadPills();

                var power      = WinForms.SystemInformation.PowerStatus;
                var batteryPct = (int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100);
                var elapsed    = (int)_stopwatch.Elapsed.TotalSeconds;
                _loopsResult.Entries.Add(new OfficeLoopsEntry(i, batteryPct, elapsed));
                _loopsResult.Save();
                log.Write($"Loop {i} complete — Battery: {batteryPct}%  Elapsed: {AppHelpers.FormatDuration(TimeSpan.FromSeconds(elapsed))}");

                RunBattery.Text = $"{batteryPct}%";
            }
        }
        catch (OperationCanceledException)
        {
            var power      = WinForms.SystemInformation.PowerStatus;
            var endBattery = (int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100);
            _loopsResult.EndBatteryPct       = endBattery;
            _loopsResult.TotalElapsedSeconds = (int)_stopwatch.Elapsed.TotalSeconds;
            _loopsResult.Save();

            if (power.PowerLineStatus == WinForms.PowerLineStatus.Online)
                log.Write("Run stopped — charger connected.");
            else if (endBattery <= 5)
                log.Write($"Run cancelled — battery at {endBattery}%.");
            else
                log.Write("Run cancelled.");
        }
        catch (Exception ex)
        {
            log.Write(ex, "StartLoops5Async");
            RunLogText.Text += $"\nError: {ex.Message}";
        }
        finally
        {
            BenchmarkGuard.End();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            AllowSleep();
            _stopwatch.Stop();
            _clockTimer.Stop();
            _cts              = null;
            _isLoops5         = false;
            WorkloadRow.Visibility = Visibility.Collapsed;
            ResetWorkloadPills();
        }

        // Capture end battery if the run completed normally (not via cancellation)
        if (_loopsResult.EndBatteryPct < 0)
        {
            var power = WinForms.SystemInformation.PowerStatus;
            _loopsResult.EndBatteryPct = (int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100);
        }
        _loopsResult.TotalElapsedSeconds = (int)_stopwatch.Elapsed.TotalSeconds;
        _loopsResult.Save();

        if (_loopsResult.Entries.Count > 0)
            ShowLoopsResults(_loopsResult);
        else
            ShowPanel(ConfigPanel);
    }

    // ── Workload indicators ───────────────────────────────────────────────

    private void UpdateWorkloadIndicator(string msg)
    {
        // Procyon log lines look like: "2026-05-20 19:08:26  INFO Begin workload set Excel1"
        if (!msg.Contains("Begin workload set", StringComparison.OrdinalIgnoreCase)) return;

        ResetWorkloadPills();
        if      (msg.EndsWith("Excel1",     StringComparison.OrdinalIgnoreCase)) SetWorkloadPill(WlExcel1,  active: true);
        else if (msg.EndsWith("Word",       StringComparison.OrdinalIgnoreCase)) SetWorkloadPill(WlWord,    active: true);
        else if (msg.EndsWith("Excel2",     StringComparison.OrdinalIgnoreCase)) SetWorkloadPill(WlExcel2,  active: true);
        else if (msg.EndsWith("PowerPoint", StringComparison.OrdinalIgnoreCase)) SetWorkloadPill(WlPPT,     active: true);
        else if (msg.EndsWith("Outlook",    StringComparison.OrdinalIgnoreCase)) SetWorkloadPill(WlOutlook, active: true);
    }

    private static void SetWorkloadPill(Border pill, bool active)
    {
        pill.Background = active
            ? new SolidColorBrush(Color.FromArgb(0x55, 0x10, 0xB9, 0x81))
            : new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
        if (pill.Child is TextBlock tb)
            tb.Foreground = active
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    }

    private void ResetWorkloadPills()
    {
        foreach (var pill in new[] { WlExcel1, WlWord, WlExcel2, WlPPT, WlOutlook })
            SetWorkloadPill(pill, active: false);
    }

    // ── Loops results ─────────────────────────────────────────────────────

    private void ShowLoopsResults(OfficeLoopsResult result)
    {
        LoopsMachine.Text = $"PROCYON OFFICE  5 LOOPS  ·  {result.MachineName}";

        LoopsStartBat.Text = result.StartBatteryPct >= 0 ? $"{result.StartBatteryPct}%" : "—";
        var endBat = result.EndBatteryPct >= 0 ? result.EndBatteryPct
                   : result.Entries.Count > 0  ? result.Entries[^1].BatteryPct
                   : -1;
        LoopsEndBat.Text   = endBat >= 0 ? $"{endBat}%" : "—";
        LoopsDuration.Text = AppHelpers.FormatDuration(result.TotalDuration);

        LoopsTable.Children.Clear();
        foreach (var e in result.Entries)
            LoopsTable.Children.Add(MakeLoopsRow(
                $"Loop {e.Iteration}",
                $"{e.BatteryPct}%",
                AppHelpers.FormatDuration(TimeSpan.FromSeconds(e.ElapsedSeconds))));

        ShowPanel(LoopsResultsPanel);
    }

    private static Grid MakeLoopsRow(string label, string battery, string elapsed)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var white = new SolidColorBrush(Colors.White);
        void Cell(int col, string text)
        {
            var tb = new TextBlock
            {
                Text       = text,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = white,
            };
            Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }
        Cell(0, label);
        Cell(1, battery);
        Cell(2, elapsed);
        return g;
    }

    private void LoopsExport_Click(object sender, RoutedEventArgs e)
    {
        if (_loopsResult == null) return;
        Clipboard.SetText(_loopsResult.ExportText());
        AppHelpers.FlashButton((Button)sender);
    }

    // ── Chart ─────────────────────────────────────────────────────────────

    private static void DrawChart(Canvas canvas, ProcyonOfficeRundownResult result)
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
        ConfigPanel       .Visibility = Visibility.Collapsed;
        RunningPanel      .Visibility = Visibility.Collapsed;
        ResultsPanel      .Visibility = Visibility.Collapsed;
        LoopsResultsPanel .Visibility = Visibility.Collapsed;
        panel.Visibility              = Visibility.Visible;
        AppHelpers.FadeIn(panel);
    }
}
