using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace SpecIQ;

public partial class CinebenchWindow : Window
{
    private string?                  _exePath;
    private string?                  _version;
    private CancellationTokenSource? _cts;
    private CinebenchMode            _lastMode   = CinebenchMode.Both;
    private int                      _lastTrials = 1;
    private CinebenchSavedResult?      _previousResult;
    private readonly DispatcherTimer _dotTimer;
    private int                      _dotFrame;

    public CinebenchWindow()
    {
        InitializeComponent();

        _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dotTimer.Tick += (_, _) => AnimateDots();

        Loaded += (_, _) => Initialise();
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

    // ── Init ──────────────────────────────────────────────────────────────

    private void Initialise()
    {
        _exePath = SpecIQSettings.CinebenchPath is { Length: > 0 } saved
                   && System.IO.File.Exists(saved) ? saved
                   : CinebenchService.FindInstalled();

        if (_exePath != null)
            SpecIQSettings.CinebenchPath = _exePath;

        _previousResult = CinebenchSavedResult.Load();
        RefreshReady();
    }

    private void RefreshReady()
    {
        if (_exePath != null)
        {
            _version = CinebenchService.GetInstalledVersion(_exePath);
            var shortVer = _version?.Split('.')[0];
            TitleVersionText.Text  = shortVer != null ? $"CINEBENCH {shortVer}" : "CINEBENCH";
            StatusText.Text        = shortVer ?? "Installed";
            NotFoundRow.Visibility = Visibility.Collapsed;
            if (_previousResult != null)
            {
                var s = _previousResult.SingleCore > 0 ? $"Single {Fmt(_previousResult.SingleCore)}" : "";
                var m = _previousResult.MultiCore  > 0 ? $"Multi {Fmt(_previousResult.MultiCore)}"   : "";
                var sep = s.Length > 0 && m.Length > 0 ? "  ·  " : "";
                PreviousSummaryText.Text     = s + sep + m;
                PreviousResultsBorder.Visibility = Visibility.Visible;
            }
            RunRow.Visibility      = Visibility.Visible;
        }
        else
        {
            TitleVersionText.Text  = "CINEBENCH";
            StatusText.Text        = "Not found";
            NotFoundRow.Visibility = Visibility.Visible;
            RunRow.Visibility      = Visibility.Collapsed;
        }

        ShowPanel(ReadyPanel);
    }

    // ── Browse ────────────────────────────────────────────────────────────

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new WinForms.OpenFileDialog
        {
            Title  = "Locate Cinebench.exe",
            Filter = "Cinebench.exe|Cinebench.exe|All executables|*.exe",
        };

        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
        {
            _exePath = dlg.FileName;
            SpecIQSettings.CinebenchPath = _exePath;
            RefreshReady();
        }
    }

    // ── Run ───────────────────────────────────────────────────────────────

    private void RunSingleCore_Click(object sender, RoutedEventArgs e) => _ = RunAsync(CinebenchMode.Single, trials: 1);
    private void RunMultiCore_Click(object sender, RoutedEventArgs e)  => _ = RunAsync(CinebenchMode.Multi,  trials: 1);
    private void RunThree_Click(object sender, RoutedEventArgs e)      => _ = RunAsync(CinebenchMode.Both,   trials: 3);
    private void RunAgain_Click(object sender, RoutedEventArgs e)      => _ = RunAsync(_lastMode, _lastTrials);

    private async Task RunAsync(CinebenchMode mode, int trials)
    {
        if (_exePath == null) return;

        if (CinebenchService.IsAlreadyRunning(_exePath))
        {
            MessageBox.Show(
                "Cinebench is already running. Please close it before starting a new run.",
                "Cinebench Running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _lastMode   = mode;
        _lastTrials = trials;

        ShowPanel(RunningPanel);
        RunPhaseText.Text       = "Starting…";
        RunTrialText.Visibility = Visibility.Collapsed;
        _dotTimer.Start();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        var results = new List<CinebenchResult>();

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

                var progress = new Progress<string>(msg =>
                {
                    RunPhaseText.Text = trials > 1 ? $"Trial {trial + 1}  ·  {msg}" : msg;
                });

                var result = await CinebenchService.RunAsync(_exePath, progress, mode, _cts.Token);
                results.Add(result);

                if (trials > 1 && trial < trials - 1)
                {
                    for (int s = 180; s > 0; s--)
                    {
                        var mins = s / 60;
                        var secs = s % 60;
                        RunPhaseText.Text = $"Trial {trial + 1} done  ·  Cooldown {mins}:{secs:D2}";
                        try { await Task.Delay(1000, _cts.Token); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }

            if (results.Count == 0) { ShowPanel(ReadyPanel); return; }

            if (trials == 1)
                ShowSingleResult(results[0], mode);
            else
                ShowTrialResults(results, mode);
        }
        catch (OperationCanceledException) { ShowPanel(ReadyPanel); }
        catch (Exception ex)
        {
            RunPhaseText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _dotTimer.Stop();
            _cts = null;
        }
    }

    // ── Results ───────────────────────────────────────────────────────────

    private static string Fmt(double v) => v > 0 ? ((int)v).ToString("N0") : "—";

    private void ShowSingleResult(CinebenchResult result, CinebenchMode mode)
    {
        ResultSingle.Text = mode == CinebenchMode.Multi  ? "—" : Fmt(result.SingleCore);
        ResultMulti.Text  = mode == CinebenchMode.Single ? "—" : Fmt(result.MultiCore);
        ShowPanel(ResultPanel);
        var saved = new CinebenchSavedResult { SingleCore = result.SingleCore, MultiCore = result.MultiCore };
        saved.Save();
        _previousResult = saved;
    }

    private void ShowTrialResults(List<CinebenchResult> results, CinebenchMode mode)
    {
        TrialS1.Text = mode == CinebenchMode.Multi ? "—" : (results.Count > 0 ? Fmt(results[0].SingleCore) : "—");
        TrialS2.Text = mode == CinebenchMode.Multi ? "—" : (results.Count > 1 ? Fmt(results[1].SingleCore) : "—");
        TrialS3.Text = mode == CinebenchMode.Multi ? "—" : (results.Count > 2 ? Fmt(results[2].SingleCore) : "—");

        TrialM1.Text = mode == CinebenchMode.Single ? "—" : (results.Count > 0 ? Fmt(results[0].MultiCore) : "—");
        TrialM2.Text = mode == CinebenchMode.Single ? "—" : (results.Count > 1 ? Fmt(results[1].MultiCore) : "—");
        TrialM3.Text = mode == CinebenchMode.Single ? "—" : (results.Count > 2 ? Fmt(results[2].MultiCore) : "—");

        TrialAvgS.Text = mode == CinebenchMode.Multi ? "—" :
            results.Select(r => r.SingleCore).Max() > 0 ? Fmt(results.Average(r => r.SingleCore)) : "—";
        TrialAvgM.Text = mode == CinebenchMode.Single ? "—" :
            results.Select(r => r.MultiCore).Max() > 0 ? Fmt(results.Average(r => r.MultiCore)) : "—";

        ShowPanel(TrialResultPanel);
        var avgS = results.Select(r => r.SingleCore).Average();
        var avgM = results.Select(r => r.MultiCore).Average();
        var saved = new CinebenchSavedResult { SingleCore = avgS, MultiCore = avgM };
        saved.Save();
        _previousResult = saved;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _dotTimer.Stop();
        ShowPanel(ReadyPanel);
    }

    private void ViewPrevious_Click(object sender, MouseButtonEventArgs e)
    {
        if (_previousResult == null) return;
        ResultSingle.Text = _previousResult.SingleCore > 0 ? Fmt(_previousResult.SingleCore) : "—";
        ResultMulti.Text  = _previousResult.MultiCore  > 0 ? Fmt(_previousResult.MultiCore)  : "—";
        ShowPanel(ResultPanel);
    }

    private void ShowPanel(FrameworkElement panel)
    {
        ReadyPanel      .Visibility = Visibility.Collapsed;
        RunningPanel    .Visibility = Visibility.Collapsed;
        ResultPanel     .Visibility = Visibility.Collapsed;
        TrialResultPanel.Visibility = Visibility.Collapsed;
        panel.Visibility            = Visibility.Visible;
    }

    private void AnimateDots()
    {
        _dotFrame = (_dotFrame + 1) % 3;
        Dot1.Opacity = _dotFrame == 0 ? 1.0 : _dotFrame == 2 ? 0.25 : 0.5;
        Dot2.Opacity = _dotFrame == 1 ? 1.0 : _dotFrame == 0 ? 0.25 : 0.5;
        Dot3.Opacity = _dotFrame == 2 ? 1.0 : _dotFrame == 1 ? 0.25 : 0.5;
    }
}
