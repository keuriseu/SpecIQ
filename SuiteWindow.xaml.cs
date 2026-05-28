using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Button     = System.Windows.Controls.Button;
using Clipboard  = System.Windows.Clipboard;
using Color      = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace SpecIQ;

// ── Benchmark descriptor ──────────────────────────────────────────────────────

enum SuiteItemState { Queued, Running, Done, Failed, Skipped }

class SuiteItem
{
    public required string         Name;
    public          SuiteItemState State  = SuiteItemState.Queued;
    public          bool           Ready;
    public          string?        SkipReason;
    public          string?        ScoreLabel;
    public          TextBlock?     StateBlock;
    public          TextBlock?     ScoreBlock;
}

// ── Window ────────────────────────────────────────────────────────────────────

public partial class SuiteWindow : Window
{
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _clockTimer;
    private readonly Stopwatch       _stopwatch = new();
    private SuiteResult?             _result;

    // Resolved during discovery
    private string?          _gbExe;
    private string?          _gbAiExe;
    private AIEntry?         _qnnEntry;
    private string?          _cbExe;
    private string?          _procyonExe;
    private string?          _procyonOfficeExe;
    private string?          _blenderCli;
    private string?          _blenderVersion;
    private bool             _hasWebView2;
    private string?          _edgeExe;
    private string?          _chromeExe;
    private ProcyonCvBackend _procyonBackend;

    private readonly List<SuiteItem> _items = [];

    // Rough per-benchmark minute estimates for the discovery panel
    private static readonly Dictionary<string, int> EstMinutes = new()
    {
        ["Geekbench 6 CPU"]      = 3,
        ["Geekbench 6 GPU"]      = 3,
        ["Geekbench AI NPU"]     = 4,
        ["Cinebench Single"]     = 2,
        ["Cinebench Multi"]      = 3,
        ["Speedometer WebView"]  = 5,
        ["Speedometer Edge"]     = 5,
        ["Speedometer Chrome"]   = 5,
        ["Procyon AI CV"]        = 6,
        ["Procyon Office"]       = 25,
        ["Blender CPU"]          = 10,
    };

    public SuiteWindow()
    {
        InitializeComponent();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => RunElapsed.Text = _stopwatch.Elapsed.ToString(@"h\:mm\:ss");
        Loaded += (_, _) =>
        {
            var s = SpecIQSettings.UiScale;
            UiScaleTransform.ScaleX = s;
            UiScaleTransform.ScaleY = s;
            _ = DiscoverAsync();
        };
    }

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

    // ── Discovery ─────────────────────────────────────────────────────────────

    private async Task DiscoverAsync()
    {
        RunSuiteBtn.IsEnabled = false;
        DiscoveryList.Children.Clear();
        _items.Clear();

        // Fast sync detections
        _gbExe            = GeekbenchService.FindInstalled();
        _gbAiExe          = GeekbenchAIService.FindInstalled();
        _cbExe            = CinebenchService.FindInstalled();
        _procyonExe       = ProcyonService.FindInstalled();
        _procyonOfficeExe = ProcyonService.FindOfficeInstalled();
        _edgeExe          = SpeedometerService.FindBrowserExe(SpeedometerBrowser.Edge);
        _chromeExe        = SpeedometerService.FindBrowserExe(SpeedometerBrowser.Chrome);
        _procyonBackend   = ProcyonService.IsNpuAvailable() ? ProcyonCvBackend.NpuQnn : ProcyonCvBackend.CpuF32;
        _blenderCli       = BlenderService.FindCli();

        // Async detections in parallel
        var webView2Task  = Task.Run(CheckWebView2Async);
        var qnnTask       = _gbAiExe != null
            ? Task.Run(() => GeekbenchAIService.ListAvailableAsync(_gbAiExe))
            : Task.FromResult(new List<AIEntry>());
        var blenderVerTask = _blenderCli != null
            ? Task.Run(() => BlenderService.GetLatestBlenderVersionAsync(_blenderCli, CancellationToken.None))
            : Task.FromResult<string?>(null);

        _hasWebView2    = await webView2Task;
        var aiEntries   = await qnnTask;
        _qnnEntry       = aiEntries.FirstOrDefault(e => GeekbenchAIService.CategorizeEntry(e) == AIBackend.Qnn);
        _blenderVersion = await blenderVerTask;

        bool blenderReady = false;
        if (_blenderCli != null && _blenderVersion != null)
            blenderReady = await Task.Run(() => BlenderService.IsBlenderReadyAsync(_blenderCli, _blenderVersion, CancellationToken.None));

        // Build item list in run order
        AddItem("Geekbench 6 CPU",     _gbExe    != null, "Geekbench 6 not installed");
        AddItem("Geekbench 6 GPU",     _gbExe    != null, "Geekbench 6 not installed");
        AddItem("Geekbench AI NPU",    _gbAiExe  != null && _qnnEntry != null,
                _gbAiExe == null ? "Geekbench AI not installed" : "No QNN/NPU backend found");
        AddItem("Cinebench Single",    _cbExe    != null, "Cinebench not installed");
        AddItem("Cinebench Multi",     _cbExe    != null, "Cinebench not installed");
        AddItem("Speedometer WebView", _hasWebView2, "WebView2 runtime not found");
        AddItem("Speedometer Edge",    _edgeExe  != null, "Edge not found");
        AddItem("Speedometer Chrome",  _chromeExe != null, "Chrome not found");
        AddItem("Procyon AI CV",       _procyonExe != null, "Procyon not installed");
        AddItem("Procyon Office",      _procyonOfficeExe != null, "Procyon Office not installed");
        AddItem("Blender CPU",         blenderReady,
                _blenderCli == null     ? "Blender not found" :
                _blenderVersion == null ? "Cannot reach Blender server" :
                                          "Blender scenes not downloaded");

        foreach (var item in _items)
            DiscoveryList.Children.Add(BuildDiscoveryRow(item));

        var totalMin = _items.Where(i => i.Ready).Sum(i => EstMinutes.GetValueOrDefault(i.Name, 0));
        EstTimeText.Text      = totalMin > 0 ? $"~{totalMin} min" : "—";
        RunSuiteBtn.IsEnabled = _items.Any(i => i.Ready);
    }

    private void AddItem(string name, bool ready, string skipReason)
    {
        _items.Add(new SuiteItem
        {
            Name       = name,
            Ready      = ready,
            SkipReason = ready ? null : skipReason,
        });
    }

    private static Task<bool> CheckWebView2Async()
    {
        try
        {
            var ver = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            return Task.FromResult(!string.IsNullOrEmpty(ver));
        }
        catch { return Task.FromResult(false); }
    }

    // ── Discovery row builder ─────────────────────────────────────────────────

    private static UIElement BuildDiscoveryRow(SuiteItem item)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text              = item.Name,
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 11,
            FontWeight        = item.Ready ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground        = new SolidColorBrush(item.Ready
                                    ? Colors.White
                                    : Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var badge = new Border
        {
            Background   = new SolidColorBrush(item.Ready
                               ? Color.FromRgb(0x10, 0xB9, 0x81)
                               : Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(4),
            Padding      = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip      = item.SkipReason,
        };
        badge.Child = new TextBlock
        {
            Text       = item.Ready ? "READY" : "SKIP",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 8,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.White),
        };
        Grid.SetColumn(badge, 1);
        row.Children.Add(badge);
        return row;
    }

    // ── Run ───────────────────────────────────────────────────────────────────

    private void RunSuite_Click(object sender, RoutedEventArgs e) => _ = RunSuiteAsync();
    private void Stop_Click(object sender, RoutedEventArgs e)     => _cts?.Cancel();

    private async Task RunSuiteAsync()
    {
        _result = new SuiteResult();
        _cts    = new CancellationTokenSource();
        var ct  = _cts.Token;

        BuildRunningList();
        ShowPanel(RunningPanel);

        _stopwatch.Restart();
        _clockTimer.Start();

        IProgress<string> progress = new Progress<string>(msg =>
        {
            RunLogText.Text += msg + "\n";
            RunLogScroll.ScrollToBottom();
        });

        BenchmarkGuard.Begin();
        try
        {
            await RunItemAsync("Geekbench 6 CPU", progress, ct, async () =>
            {
                var r = await GeekbenchService.RunAsync(_gbExe!, progress, gpu: false, ct: ct);
                _result.GbCpuSingle = r.SingleCore;
                _result.GbCpuMulti  = r.MultiCore;
                return $"SC {r.SingleCore:N0}  ·  MC {r.MultiCore:N0}";
            });

            await RunItemAsync("Geekbench 6 GPU", progress, ct, async () =>
            {
                var r = await GeekbenchService.RunAsync(_gbExe!, progress, gpu: true, ct: ct);
                _result.GbGpuOpenCl = r.SingleCore;
                _result.GbGpuVulkan = r.MultiCore;
                return $"CL {r.SingleCore:N0}  ·  VK {r.MultiCore:N0}";
            });

            await RunItemAsync("Geekbench AI NPU", progress, ct, async () =>
            {
                var r = await GeekbenchAIService.RunAsync(_gbAiExe!, progress, _qnnEntry!, ct);
                _result.GbAiNpu = r.FullPrecision;
                return $"{r.FullPrecision:N0}";
            });

            await RunItemAsync("Cinebench Single", progress, ct, async () =>
            {
                var r = await CinebenchService.RunAsync(_cbExe!, progress, CinebenchMode.Single, ct);
                _result.CbSingle = r.SingleCore;
                return $"{r.SingleCore:N0}";
            });

            await RunItemAsync("Cinebench Multi", progress, ct, async () =>
            {
                var r = await CinebenchService.RunAsync(_cbExe!, progress, CinebenchMode.Multi, ct);
                _result.CbMulti = r.MultiCore;
                return $"{r.MultiCore:N0}";
            });

            await RunItemAsync("Speedometer WebView", progress, ct, async () =>
            {
                var score = await RunWebView2Async(progress, ct);
                _result.SpeedWebView = score;
                return $"{score:F2}";
            });

            await RunItemAsync("Speedometer Edge", progress, ct, async () =>
            {
                var score = await SpeedometerService.RunViaCdpAsync(_edgeExe!, progress, ct);
                _result.SpeedEdge = score;
                return $"{score:F2}";
            });

            await RunItemAsync("Speedometer Chrome", progress, ct, async () =>
            {
                var score = await SpeedometerService.RunViaCdpAsync(_chromeExe!, progress, ct);
                _result.SpeedChrome = score;
                return $"{score:F2}";
            });

            await RunItemAsync("Procyon AI CV", progress, ct, async () =>
            {
                var r = await ProcyonService.RunAsync(_procyonExe!, _procyonBackend, progress, ct);
                _result.ProcyonCv        = r.OverallScore;
                _result.ProcyonCvBackend = _procyonBackend;
                return $"{r.OverallScore:N0}";
            });

            await RunItemAsync("Procyon Office", progress, ct, async () =>
            {
                var r = await ProcyonService.RunOfficeAsync(_procyonOfficeExe!, progress, ct);
                _result.ProcyonOffice = r.OverallScore;
                return $"{r.OverallScore:N0}";
            });

            await RunItemAsync("Blender CPU", progress, ct, async () =>
            {
                var r = await BlenderService.RunBenchmarkAsync(
                    _blenderCli!, _blenderVersion!, BlenderService.SceneNames, "CPU", progress, ct);
                _result.BlenderScore = r.CompositeScore;
                return $"{r.CompositeScore:N0}";
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            BenchmarkGuard.End();
            _stopwatch.Stop();
            _clockTimer.Stop();
            _cts?.Dispose(); _cts = null;
        }

        _result.Save();

        if (HasAnyScore())
            ShowReport();
        else
            ShowPanel(DiscoveryPanel);
    }

    // Runs one benchmark item; handles skip / cancel / error.
    // body returns the score label string on success.
    private async Task RunItemAsync(string name, IProgress<string> progress, CancellationToken ct,
                                    Func<Task<string>> body)
    {
        var item = _items.FirstOrDefault(i => i.Name == name);
        if (item == null) return;

        if (!item.Ready || ct.IsCancellationRequested)
        {
            SetItemState(item, SuiteItemState.Skipped);
            return;
        }

        SetItemState(item, SuiteItemState.Running);
        RunningItemText.Text = name + "…";
        progress.Report($"▶ {name}");

        try
        {
            var scoreLabel = await body();
            item.ScoreLabel = scoreLabel;
            _ = Dispatcher.InvokeAsync(() => { if (item.ScoreBlock != null) item.ScoreBlock.Text = scoreLabel; });
            SetItemState(item, SuiteItemState.Done);
        }
        catch (OperationCanceledException)
        {
            SetItemState(item, SuiteItemState.Skipped);
            foreach (var i in _items.Where(i => i.State == SuiteItemState.Queued))
                SetItemState(i, SuiteItemState.Skipped);
            throw;
        }
        catch (Exception ex)
        {
            SetItemState(item, SuiteItemState.Failed);
            progress.Report($"✗ {name}: {ex.Message}");
        }
    }

    private void SetItemState(SuiteItem item, SuiteItemState state)
    {
        item.State = state;
        Dispatcher.InvokeAsync(() =>
        {
            if (item.StateBlock == null) return;
            (item.StateBlock.Text, item.StateBlock.Foreground) = state switch
            {
                SuiteItemState.Running => ("▶", new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA))),
                SuiteItemState.Done    => ("✓", new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))),
                SuiteItemState.Failed  => ("✗", new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))),
                SuiteItemState.Skipped => ("—", new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF))),
                _                      => ("·", new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))),
            };
        });
    }

    // ── Running list builder ──────────────────────────────────────────────────

    private void BuildRunningList()
    {
        RunningList.Children.Clear();
        RunLogText.Text      = "";
        RunningItemText.Text = "Preparing…";
        RunElapsed.Text      = "0:00:00";

        foreach (var item in _items)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var stateBlock = new TextBlock
            {
                Text              = item.Ready ? "·" : "—",
                FontFamily        = new FontFamily("Segoe UI"),
                FontSize          = 11,
                Foreground        = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            item.StateBlock = stateBlock;
            row.Children.Add(stateBlock);

            var nameBlock = new TextBlock
            {
                Text              = item.Name,
                FontFamily        = new FontFamily("Segoe UI"),
                FontSize          = 11,
                Foreground        = new SolidColorBrush(item.Ready
                                        ? Colors.White
                                        : Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(nameBlock, 1);
            row.Children.Add(nameBlock);

            var scoreBlock = new TextBlock
            {
                FontFamily        = new FontFamily("Segoe UI"),
                FontSize          = 10,
                FontWeight        = FontWeights.SemiBold,
                Foreground        = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            item.ScoreBlock = scoreBlock;
            Grid.SetColumn(scoreBlock, 2);
            row.Children.Add(scoreBlock);

            RunningList.Children.Add(row);

            if (!item.Ready) SetItemState(item, SuiteItemState.Skipped);
        }
    }

    // ── WebView2 ──────────────────────────────────────────────────────────────

    private async Task<double> RunWebView2Async(IProgress<string> progress, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<double>();
        Window?  hostWin = null;
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

            webView.CoreWebView2.ScriptDialogOpening += (_, e) => e.Accept();

            webView.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                var msg = e.TryGetWebMessageAsString();
                if (double.TryParse(msg, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var score))
                    tcs.TrySetResult(score);
            };

            bool scriptInjected = false;
            webView.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess || scriptInjected) return;
                scriptInjected = true;
                progress.Report("Page loaded. Starting Speedometer…");
                await webView.CoreWebView2.ExecuteScriptAsync(SpeedometerService.BuildWebView2Script());
            };

            progress.Report("Navigating to Speedometer 3.1…");
            webView.CoreWebView2.Navigate(SpeedometerService.SpeedometerUrl);

            using var reg = ct.Register(() => tcs.TrySetCanceled());
            var score = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(30), ct);
            await Dispatcher.InvokeAsync(() => hostWin?.Close());
            return score;
        }
        catch
        {
            await Dispatcher.InvokeAsync(() => { try { hostWin?.Close(); } catch { } });
            throw;
        }
    }

    // ── Report ────────────────────────────────────────────────────────────────

    private bool HasAnyScore() =>
        _result is { } r &&
        (r.GbCpuSingle.HasValue || r.GbCpuMulti.HasValue  || r.GbGpuOpenCl.HasValue ||
         r.GbAiNpu.HasValue     || r.CbSingle.HasValue     || r.CbMulti.HasValue     ||
         r.SpeedWebView.HasValue || r.SpeedEdge.HasValue   || r.SpeedChrome.HasValue ||
         r.ProcyonCv.HasValue   || r.ProcyonOffice.HasValue || r.BlenderScore.HasValue);

    private void ShowReport()
    {
        if (_result == null) return;
        ReportMachineText.Text = _result.MachineName.ToUpperInvariant();
        ReportDateText.Text    = _result.RanAt.ToString("MMM d, yyyy  h:mm tt");

        ScoreCards.Items.Clear();

        void Card(string label, string hex, string scoreText) =>
            ScoreCards.Items.Add(BuildScoreCard(label, hex, scoreText));

        if (_result.GbCpuSingle.HasValue || _result.GbCpuMulti.HasValue)
            Card("Geekbench 6 CPU", "#3B82F6",
                 string.Join("\n", new[]
                 {
                     _result.GbCpuSingle.HasValue ? $"SC  {_result.GbCpuSingle.Value:N0}" : "",
                     _result.GbCpuMulti.HasValue  ? $"MC  {_result.GbCpuMulti.Value:N0}"  : "",
                 }.Where(s => s.Length > 0)));

        if (_result.GbGpuOpenCl.HasValue || _result.GbGpuVulkan.HasValue)
            Card("Geekbench 6 GPU", "#60A5FA",
                 string.Join("\n", new[]
                 {
                     _result.GbGpuOpenCl.HasValue ? $"CL  {_result.GbGpuOpenCl.Value:N0}" : "",
                     _result.GbGpuVulkan.HasValue  ? $"VK  {_result.GbGpuVulkan.Value:N0}"  : "",
                 }.Where(s => s.Length > 0)));

        if (_result.GbAiNpu.HasValue)     Card("Geekbench AI NPU",    "#8B5CF6", $"{_result.GbAiNpu.Value:N0}");
        if (_result.CbSingle.HasValue)    Card("Cinebench Single",     "#F59E0B", $"{_result.CbSingle.Value:N0}");
        if (_result.CbMulti.HasValue)     Card("Cinebench Multi",      "#F59E0B", $"{_result.CbMulti.Value:N0}");
        if (_result.SpeedWebView.HasValue) Card("Speedometer WebView", "#059669", $"{_result.SpeedWebView.Value:F2}");
        if (_result.SpeedEdge.HasValue)   Card("Speedometer Edge",     "#059669", $"{_result.SpeedEdge.Value:F2}");
        if (_result.SpeedChrome.HasValue) Card("Speedometer Chrome",   "#059669", $"{_result.SpeedChrome.Value:F2}");
        if (_result.ProcyonCv.HasValue)   Card("Procyon AI CV",        "#0EA5E9", $"{_result.ProcyonCv.Value:N0}");
        if (_result.ProcyonOffice.HasValue) Card("Procyon Office",     "#10B981", $"{_result.ProcyonOffice.Value:N0}");
        if (_result.BlenderScore.HasValue) Card("Blender CPU",         "#F97316", $"{_result.BlenderScore.Value:N0}");

        ShowPanel(ReportPanel);
    }

    private static UIElement BuildScoreCard(string label, string accentHex, string scoreText)
    {
        var accent = HexColor(accentHex);
        var card = new Border
        {
            Background   = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding      = new Thickness(12, 10, 12, 10),
            Margin       = new Thickness(0, 0, 8, 8),
            Width        = 218,
        };
        var stack = new StackPanel();

        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new Border
        {
            Width = 6, Height = 6,
            Background   = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(3),
            Margin       = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text       = label,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, accent.R, accent.G, accent.B)),
        });
        stack.Children.Add(header);

        foreach (var line in scoreText.Split('\n'))
        {
            stack.Children.Add(new TextBlock
            {
                Text       = line,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 18,
                FontWeight = FontWeights.Light,
                Foreground = new SolidColorBrush(Colors.White),
            });
        }

        card.Child = stack;
        return card;
    }

    private static Color HexColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromRgb(
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    // ── Export ────────────────────────────────────────────────────────────────

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        Clipboard.SetText(_result.ExportText());
        AppHelpers.FlashButton((Button)sender);
    }

    // ── Panel switching ───────────────────────────────────────────────────────

    private void ShowPanel(FrameworkElement panel)
    {
        DiscoveryPanel.Visibility = Visibility.Collapsed;
        RunningPanel  .Visibility = Visibility.Collapsed;
        ReportPanel   .Visibility = Visibility.Collapsed;
        panel.Visibility          = Visibility.Visible;
        AppHelpers.FadeIn(panel);
    }
}
