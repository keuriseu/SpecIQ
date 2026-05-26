using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button     = System.Windows.Controls.Button;
using Clipboard  = System.Windows.Clipboard;
using Color      = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace SpecIQ;

public partial class PugetBenchWindow : Window
{
    // ── State ──────────────────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer  _clockTimer;
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();
    private PugetBenchResult?      _result;
    private PugetBenchSavedResult? _previousResult;
    private string?                _launcherExe;

    public PugetBenchWindow()
    {
        InitializeComponent();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
            RunElapsed.Text = _stopwatch.Elapsed.ToString(@"h\:mm\:ss");
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _launcherExe = PugetBenchService.FindInstalled();
        var psExe    = PugetBenchService.FindPhotoshop();
        var assets   = PugetBenchService.AreAssetsReady();
        var version  = PugetBenchService.GetBenchmarkVersion();

        // Status rows
        SetStatus(LauncherStatus,  _launcherExe != null,
            _launcherExe != null ? "✓ Found" : "✗ Not found");
        SetStatus(PhotoshopStatus, psExe != null,
            psExe != null ? "✓ Found" : "✗ Not found");
        SetStatus(AssetsStatus,    assets,
            assets ? "✓ Ready" : "✗ Not downloaded");
        SetStatus(BenchmarkStatus, version != null,
            version != null ? $"✓ v{version}" : "✗ Not found");

        // Disable launch if prerequisites missing
        LaunchBtn.IsEnabled = _launcherExe != null && psExe != null && assets;

        // Previous result
        _previousResult = PugetBenchSavedResult.Load();
        if (_previousResult != null)
        {
            PreviousSummaryText.Text =
                $"{DateTime.Parse(_previousResult.SavedAt):g}  ·  " +
                $"{_previousResult.Tests.Count} tests";
            PreviousScoreText.Text = $"{_previousResult.CompositeScore:N0}";
            PreviousResultsBorder.Visibility = Visibility.Visible;
        }
    }

    private static void SetStatus(TextBlock tb, bool ok, string text)
    {
        tb.Text       = text;
        tb.Foreground = new SolidColorBrush(ok
            ? Color.FromRgb(0x4A, 0xDE, 0x80)    // green
            : Color.FromRgb(0xF8, 0x71, 0x71));   // red
    }

    // ── Window chrome ──────────────────────────────────────────────────────────

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

    private void ViewPrevious_Click(object sender, MouseButtonEventArgs e)
    {
        if (_previousResult != null)
            ShowResults(_previousResult);
    }

    // ── Launch & Run ───────────────────────────────────────────────────────────

    private void LaunchRun_Click(object sender, RoutedEventArgs e)
        => _ = RunBenchmarkAsync();

    private void RunAgain_Click(object sender, RoutedEventArgs e)
        => _ = RunBenchmarkAsync();

    private async Task RunBenchmarkAsync()
    {
        if (_launcherExe == null) return;

        _result = null;
        _cts    = new CancellationTokenSource();

        // Reset running panel
        RunLogText.Text      = "";
        RunScore.Text        = "—";
        RunGeneralScore.Text = "—";
        RunFilterScore.Text  = "—";
        RunProgress.Text     = "— / 21";
        RunElapsed.Text      = "0:00:00";
        ActionBanner.Visibility = Visibility.Visible;
        ShowPanel(RunningPanel);

        _stopwatch.Restart();
        _clockTimer.Start();

        AppLog.Trim("pugetbench");
        using var log = new AppLog("pugetbench");
        log.Write($"Launcher: {_launcherExe}");
        log.Write($"Photoshop: {PugetBenchService.FindPhotoshop() ?? "not found"}");
        log.Write($"Assets ready: {PugetBenchService.AreAssetsReady()}");
        log.Write($"Benchmark version: {PugetBenchService.GetBenchmarkVersion() ?? "not found"}");
        log.Write("");

        IProgress<string> rawProgress = new Progress<string>(OnProgress);
        IProgress<string> progress    = log.Tee(rawProgress);

        try
        {
            _result = await PugetBenchService.RunAsync(_launcherExe, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            log.Write("Run stopped by user.");
            RunLogText.Text += "\nStopped.";
        }
        catch (Exception ex)
        {
            log.Write(ex, "RunBenchmarkAsync");
            RunLogText.Text += $"\nError: {ex.Message}";
        }
        finally
        {
            _stopwatch.Stop();
            _clockTimer.Stop();
            _cts?.Dispose(); _cts = null;
        }

        if (_result is { Tests.Count: > 0 })
        {
            var saved = new PugetBenchSavedResult
            {
                CompositeScore   = _result.CompositeScore,
                GeneralScore     = _result.GeneralScore,
                FilterScore      = _result.FilterScore,
                BenchmarkVersion = PugetBenchService.GetBenchmarkVersion() ?? "",
                Tests            = [.. _result.Tests],
            };
            saved.Save();

            BenchmarkHistory.AppendIfNew(new HistoryEntry
            {
                Tool   = HistoryTool.PugetBench,
                RunAt  = saved.SavedAt,
                Note   = "Photoshop",
                ScoreA = saved.CompositeScore,
            });

            ShowResults(saved);
        }
        else if (_cts == null) // not cancelled — stay on running panel
        {
            ShowPanel(ConfigPanel);
        }
    }

    private void OnProgress(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return;

        RunLogText.Text += msg + "\n";
        RunLogScroll.ScrollToBottom();

        // Messages starting with '[N/21]' are per-test completions.
        if (msg.StartsWith('['))
        {
            if (ActionBanner.Visibility == Visibility.Visible)
                ActionBanner.Visibility = Visibility.Collapsed;

            // Parse the test index for the progress counter.
            var m = System.Text.RegularExpressions.Regex.Match(msg, @"^\[(\s*\d+)/21\]");
            if (m.Success && int.TryParse(m.Groups[1].Value.Trim(), out int n))
                RunProgress.Text = $"{n} / 21";
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ── Results panel ──────────────────────────────────────────────────────────

    private void ShowResults(PugetBenchSavedResult result)
    {
        ResHeaderText.Text = $"PUGETBENCH FOR PHOTOSHOP  ·  {result.MachineName}";
        ResScore.Text      = $"{result.CompositeScore:N0}";
        ResGeneral.Text    = $"{result.GeneralScore:N0}";
        ResFilter.Text     = $"{result.FilterScore:N0}";

        ResTestList.Children.Clear();

        string? lastCategory = null;
        foreach (var t in result.Tests)
        {
            // Category header
            if (t.Category != lastCategory)
            {
                lastCategory = t.Category;
                ResTestList.Children.Add(new TextBlock
                {
                    Text       = t.Category.ToUpperInvariant(),
                    FontSize   = 9,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    Margin     = new Thickness(0, lastCategory == t.Category ? 0 : 6, 0, 3),
                });
            }

            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text       = t.TestName,
                FontSize   = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            };
            Grid.SetColumn(nameBlock, 0);

            var timeBlock = new TextBlock
            {
                Text                = $"{t.Seconds:F2} s",
                FontSize            = 11,
                FontFamily          = new FontFamily("Segoe UI"),
                Foreground          = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                Margin              = new Thickness(12, 0, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            };
            Grid.SetColumn(timeBlock, 1);

            row.Children.Add(nameBlock);
            row.Children.Add(timeBlock);
            ResTestList.Children.Add(row);
        }

        ShowPanel(ResultsPanel);
    }

    // ── Export ─────────────────────────────────────────────────────────────────

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var saved = _previousResult;
        if (_result != null)
        {
            saved = new PugetBenchSavedResult
            {
                CompositeScore   = _result.CompositeScore,
                GeneralScore     = _result.GeneralScore,
                FilterScore      = _result.FilterScore,
                BenchmarkVersion = PugetBenchService.GetBenchmarkVersion() ?? "",
                Tests            = _result.Tests,
            };
        }
        if (saved == null) return;
        Clipboard.SetText(saved.ExportText());
        AppHelpers.FlashButton((Button)sender);
    }

    // ── Panel switching ────────────────────────────────────────────────────────

    private void ShowPanel(FrameworkElement panel)
    {
        ConfigPanel .Visibility = Visibility.Collapsed;
        RunningPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        panel.Visibility        = Visibility.Visible;
        AppHelpers.FadeIn(panel);
    }
}
