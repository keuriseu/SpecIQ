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

public partial class BlenderWindow : Window
{
    // ── Sleep prevention ──────────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint ES_CONTINUOUS       = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED  = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    // ── State ─────────────────────────────────────────────────────────────

    private string?                  _cli;
    private string?                  _blenderVersion;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _clockTimer;
    private readonly Stopwatch       _stopwatch = new();
    private BlenderRundownResult?    _result;
    private BlenderRundownResult?    _previousResult;
    private bool                     _rundown;
    private int                      _maxIterations;
    private int                      _tickCount;
    private string                   _deviceType = "CPU";
    private AppLog?                  _log;

    private static readonly Color Accent = Color.FromRgb(0xF9, 0x73, 0x16);

    public BlenderWindow()
    {
        InitializeComponent();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => TickClock();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cli = BlenderService.FindCli();

        if (_cli == null)
        {
            StatusText.Text = "Launcher not found";
            SetSceneStatus("✗ Launcher not found", false);
            NoLauncherBorder.Visibility = Visibility.Visible;
            DisableRunButtons();
            LoadPreviousResult();
            return;
        }

        StatusText.Text = "Checking launcher…";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            _blenderVersion = await BlenderService.GetLatestBlenderVersionAsync(_cli, cts.Token);
            StatusText.Text = _blenderVersion != null
                ? $"Blender {_blenderVersion}  ·  {System.IO.Path.GetFileName(_cli)}"
                : "Could not determine Blender version";

            if (_blenderVersion != null)
                await RefreshReadinessAsync(cts.Token);
            else
                DisableRunButtons();
        }
        catch
        {
            StatusText.Text = "Error communicating with launcher";
            DisableRunButtons();
        }

        LoadPreviousResult();
    }

    // ── Readiness / scene status ───────────────────────────────────────────

    private async Task RefreshReadinessAsync(CancellationToken ct)
    {
        if (_cli == null || _blenderVersion == null) return;

        bool ready = await BlenderService.IsBlenderReadyAsync(_cli, _blenderVersion, ct);

        SetSceneStatus(ready ? "✓ Ready" : "✗ Not downloaded", ready);
        DownloadBtn.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;

        SingleBtn.IsEnabled  = ready;
        TrialsBtn.IsEnabled  = ready;
        RundownBtn.IsEnabled = ready;

        if (ready)
        {
            try
            {
                var types = await BlenderService.GetDeviceTypesAsync(_cli, _blenderVersion, ct);
                DeviceCombo.Items.Clear();
                foreach (var t in types)
                    DeviceCombo.Items.Add(new ComboBoxItem { Content = t });
                if (DeviceCombo.Items.Count > 0)
                    DeviceCombo.SelectedIndex = 0;
            }
            catch { /* leave the hardcoded ComboBox items */ }
        }
    }

    private void SetSceneStatus(string text, bool ready)
    {
        var brush = StatusBrush(ready);
        MonsterStatus.Text      = text; MonsterStatus.Foreground   = brush;
        JunkshopStatus.Text     = text; JunkshopStatus.Foreground  = brush;
        ClassroomStatus.Text    = text; ClassroomStatus.Foreground = brush;
    }

    private void DisableRunButtons()
    {
        SingleBtn.IsEnabled  = false;
        TrialsBtn.IsEnabled  = false;
        RundownBtn.IsEnabled = false;
    }

    private void LoadPreviousResult()
    {
        _previousResult = BlenderRundownResult.Load();
        if (_previousResult?.Entries.Count > 0)
        {
            var avg = (int)_previousResult.Entries.Average(e => e.CompositeScore);
            PreviousSummaryText.Text = $"{AppHelpers.FormatDuration(_previousResult.TotalDuration)}"
                                    + $"  ·  {_previousResult.IterationCount} iters  ·  avg {avg:N0}";
            PreviousResultsBorder.Visibility = Visibility.Visible;
        }
    }

    private static SolidColorBrush StatusBrush(bool ready) =>
        new(ready ? Color.FromArgb(0xCC, 0x4A, 0xDE, 0x80)
                  : Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));

    // ── Browse for CLI ────────────────────────────────────────────────────

    private async void BrowseCli_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new WinForms.OpenFileDialog
        {
            Title       = "Locate benchmark-launcher-cli.exe",
            Filter      = "benchmark-launcher-cli.exe|benchmark-launcher-cli.exe|All executables|*.exe",
            FileName    = "benchmark-launcher-cli.exe",
        };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;

        _cli = dlg.FileName;
        NoLauncherBorder.Visibility = Visibility.Collapsed;
        StatusText.Text = "Checking launcher…";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            _blenderVersion = await BlenderService.GetLatestBlenderVersionAsync(_cli, cts.Token);
            StatusText.Text = _blenderVersion != null
                ? $"Blender {_blenderVersion}  ·  {System.IO.Path.GetFileName(_cli)}"
                : "Could not determine Blender version";

            if (_blenderVersion != null)
                await RefreshReadinessAsync(cts.Token);
            else
                DisableRunButtons();
        }
        catch
        {
            StatusText.Text = "Error communicating with launcher";
            DisableRunButtons();
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

    // ── Download ──────────────────────────────────────────────────────────

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_cli == null || _blenderVersion == null) return;

        DownloadBtn.IsEnabled = false;
        DisableRunButtons();
        DownloadProgressBorder.Visibility = Visibility.Visible;

        IProgress<string> progress = new Progress<string>(msg =>
            DownloadProgressText.Text = msg);

        using var cts = new CancellationTokenSource();
        try
        {
            DownloadProgressText.Text = "Downloading Blender…";
            await BlenderService.DownloadBlenderAsync(_cli, _blenderVersion, progress, cts.Token);
            DownloadProgressText.Text = "Downloading scenes…";
            await BlenderService.DownloadScenesAsync(_cli, _blenderVersion, progress, cts.Token);
            DownloadProgressText.Text = "Download complete.";
            await Task.Delay(1500);
        }
        catch (Exception ex)
        {
            DownloadProgressText.Text = $"Download failed: {ex.Message}";
            await Task.Delay(3000);
        }
        finally
        {
            DownloadProgressBorder.Visibility = Visibility.Collapsed;
            await RefreshReadinessAsync(CancellationToken.None);
        }
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
        if (_cli == null || _blenderVersion == null) return;

        _rundown       = rundown;
        _maxIterations = maxIterations;
        _result           = new BlenderRundownResult();
        _result.IsRundown = rundown;
        _cts              = new CancellationTokenSource();
        _tickCount        = 0;

        _deviceType = ((DeviceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CPU").Trim();
        _result.DeviceType = _deviceType;

        AppLog.Trim("blender");
        _log = new AppLog("blender");
        _log.Write($"Mode: {(rundown ? "Rundown" : maxIterations == 3 ? "3 Trials" : "Single")}");
        _log.Write($"CLI: {_cli}");
        _log.Write($"Blender version: {_blenderVersion}");
        _log.Write($"Device: {_deviceType}");

        RunSubtitleText.Text = maxIterations == 3 ? "3 Trials"
                             : rundown            ? "Battery Rundown"
                                                  : "Single Run";
        RunScore.Text    = "—";
        RunBattery.Text  = "—";
        RunElapsed.Text  = "0:00:00";
        RunIterText.Text = "Preparing…";
        RunMonster.Text  = "—";
        RunJunkshop.Text = "—";
        RunClassroom.Text = "—";
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

        var iteration = 0;
        IProgress<string> progress = _log.Tee(new Progress<string>(msg =>
        {
            RunLogText.Text += msg + "\n";
            RunLogScroll.ScrollToBottom();
        }));

        BenchmarkGuard.Begin();
        try
        {
            do
            {
                iteration++;
                RunIterText.Text = $"Iteration {iteration}";

                var power = WinForms.SystemInformation.PowerStatus;
                if (power.BatteryLifePercent is >= 0 and <= 0.03f) break;

                BlenderRunResult r;
                try
                {
                    r = await BlenderService.RunBenchmarkAsync(
                        _cli, _blenderVersion, BlenderService.SceneNames,
                        _deviceType, progress, _cts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    progress.Report($"[Iteration {iteration} failed: {ex.Message}]");
                    continue;
                }

                if (r.CompositeScore == 0)
                {
                    progress.Report($"[Iteration {iteration}: no score — skipping]");
                    continue;
                }

                power = WinForms.SystemInformation.PowerStatus;
                var batteryPct = (int)Math.Clamp(power.BatteryLifePercent * 100, 0, 100);
                var elapsed    = (int)_stopwatch.Elapsed.TotalSeconds;

                var entry = new BlenderEntry(
                    iteration,
                    r.CompositeScore,
                    r.Scenes.FirstOrDefault(s => s.SceneName == "monster")?.SamplesPerMinute   ?? 0,
                    r.Scenes.FirstOrDefault(s => s.SceneName == "junkshop")?.SamplesPerMinute  ?? 0,
                    r.Scenes.FirstOrDefault(s => s.SceneName == "classroom")?.SamplesPerMinute ?? 0,
                    _deviceType, batteryPct, elapsed);

                _result.Entries.Add(entry);
                _result.Save();
                SaveHistory(_result, isTrials: false, (int)_stopwatch.Elapsed.TotalSeconds);

                RunScore.Text    = $"{_result.Entries.Average(e => e.CompositeScore):N0}";
                RunBattery.Text  = $"{batteryPct}%";
                RunMonster.Text  = FormatAvgSpm(_result.Entries, e => e.MonsterSpm);
                RunJunkshop.Text = FormatAvgSpm(_result.Entries, e => e.JunkshopSpm);
                RunClassroom.Text = FormatAvgSpm(_result.Entries, e => e.ClassroomSpm);
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => DrawChart(RunChart, _result));

            } while (!_cts.Token.IsCancellationRequested &&
                     (rundown || iteration < _maxIterations));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
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
            _cts?.Dispose(); _cts = null;
            _log?.Dispose();
            _log = null;
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

        // Persist elapsed time every 60 s so it's preserved if the machine dies mid-iteration
        if (_rundown && _result != null && ++_tickCount % 60 == 0)
        {
            _result.TotalElapsedSeconds = (int)elapsed.TotalSeconds;
            _result.Save();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // Stop if AC power is plugged in — charger connected means the rundown is over.
        // Do NOT stop on Suspend: let the machine hibernate at critical battery and
        // naturally die; cancelling here would end the test prematurely.
        if (e.Mode == PowerModes.StatusChange)
        {
            var power = WinForms.SystemInformation.PowerStatus;
            if (power.PowerLineStatus == WinForms.PowerLineStatus.Online)
                Dispatcher.BeginInvoke(() => _cts?.Cancel());
        }
    }

    private static void PreventSleep() =>
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

    private static void AllowSleep() =>
        SetThreadExecutionState(ES_CONTINUOUS);

    // ── Results ───────────────────────────────────────────────────────────

    private void ShowResults(BlenderRundownResult result)
    {
        ResTypeText.Text     = $"BLENDER  ·  {result.DeviceType}  ·  {result.MachineName}";
        ResDurationText.Text = AppHelpers.FormatDuration(result.TotalDuration);
        ResIterText.Text     = result.IterationCount.ToString();
        ResDeviceText.Text   = result.DeviceType;

        var isTrials = _maxIterations == 3 && result.Entries.Count <= 3;

        if (isTrials)
        {
            ResTrial1.Text    = result.Entries.Count > 0 ? $"{result.Entries[0].CompositeScore:N0}" : "—";
            ResTrial2.Text    = result.Entries.Count > 1 ? $"{result.Entries[1].CompositeScore:N0}" : "—";
            ResTrial3.Text    = result.Entries.Count > 2 ? $"{result.Entries[2].CompositeScore:N0}" : "—";
            ResTrialsAvg.Text = $"{(int)result.Entries.Average(e => e.CompositeScore):N0}";
        }
        else if (result.Entries.Count > 1)
        {
            var first = result.Entries[0].CompositeScore;
            var last  = result.Entries[^1].CompositeScore;
            var avg   = result.Entries.Average(e => e.CompositeScore);
            ResFirst.Text = $"{first:N0}";
            ResLast.Text  = $"{last:N0}";
            ResAvg.Text   = $"{avg:N0}";

            if (result.IsRundown)
            {
                ResStartBat.Text = result.StartBatteryPct >= 0 ? $"{result.StartBatteryPct}%" : "—";
                ResEndBat.Text   = $"{result.Entries[^1].BatteryPct}%";
                ResEndedAt.Text  = DateTime.Parse(result.StartedAt).Add(result.TotalDuration).ToString("h:mm tt");
            }
        }

        var showStats = !isTrials && result.Entries.Count > 1;
        TrialsRow.Visibility  = isTrials  ? Visibility.Visible : Visibility.Collapsed;
        StatsRow.Visibility   = showStats ? Visibility.Visible : Visibility.Collapsed;
        BatteryRow.Visibility = showStats && result.IsRundown ? Visibility.Visible : Visibility.Collapsed;

        // Scene spm breakdown: average across all iterations
        if (result.Entries.Count > 0)
        {
            ResMonster.Text   = FormatAvgSpm(result.Entries, e => e.MonsterSpm);
            ResJunkshop.Text  = FormatAvgSpm(result.Entries, e => e.JunkshopSpm);
            ResClassroom.Text = FormatAvgSpm(result.Entries, e => e.ClassroomSpm);
            SubScoresBorder.Visibility = Visibility.Visible;
        }

        ShowPanel(ResultsPanel);
        SaveHistory(result, isTrials, (int)result.TotalDuration.TotalSeconds);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => DrawChart(ResChart, result));
    }

    private static string FormatAvgSpm(List<BlenderEntry> entries, Func<BlenderEntry, double> selector)
    {
        var valid = entries.Select(selector).Where(s => s > 0).ToList();
        return valid.Count == 0 ? "—" : $"{(int)valid.Average():N0}";
    }

    private void ResChart_Loaded(object sender, RoutedEventArgs e)
    {
        var result = _result ?? _previousResult;
        if (result?.Entries.Count > 0) DrawChart(ResChart, result);
    }

    private static void SaveHistory(BlenderRundownResult result, bool isTrials, int durationSeconds)
    {
        if (result.Entries.Count == 0) return;
        var avg  = (int)result.Entries.Average(e => e.CompositeScore);
        var note = isTrials                  ? $"×3 trials avg  ·  {result.DeviceType}"
                 : result.Entries.Count == 1 ? $"Single run  ·  {result.DeviceType}"
                 : $"Rundown  ·  {result.IterationCount} iters  ·  {result.DeviceType}";
        BenchmarkHistory.Upsert(new HistoryEntry
        {
            Tool            = HistoryTool.Blender,
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

    private static void DrawChart(Canvas canvas, BlenderRundownResult result)
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
        var maxScore = entries.Max(e => e.CompositeScore) * 1.08;

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

        var maxIdx = Enumerable.Range(0, n).MaxBy(i => entries[i].CompositeScore);
        var minIdx = Enumerable.Range(0, n).MinBy(i => entries[i].CompositeScore);
        bool ShowLabel(int i) => n <= 4 || i == maxIdx || i == minIdx;

        for (int i = 0; i < n; i++)
        {
            var entry  = entries[i];
            var barTop = Py(entry.CompositeScore);
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
                    Text          = $"{entry.CompositeScore:N0}",
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
