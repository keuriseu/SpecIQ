using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;

namespace SpecIQ;

public partial class SpecReportWindow : Window
{
    private SpecReport?          _report;
    private readonly DispatcherTimer _dotTimer;
    private int                  _dotFrame;

    public SpecReportWindow()
    {
        InitializeComponent();

        _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dotTimer.Tick += (_, _) => AnimateDots();

        Loaded += (_, _) => _ = LoadAsync();
    }

    // ── Window chrome ─────────────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ── Data loading ──────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        ShowPanel(LoadingPanel);
        _dotTimer.Start();
        try
        {
            _report         = await SpecReportService.GatherAsync();
            ReportText.Text = SpecReportService.FormatReport(_report);
            ShowPanel(ReportPanel);
        }
        catch (Exception ex)
        {
            ReportText.Text = $"Error gathering system info:\n{ex.Message}";
            ShowPanel(ReportPanel);
        }
        finally
        {
            _dotTimer.Stop();
        }
    }

    // ── Buttons ───────────────────────────────────────────────────────────

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_report == null) return;
        try
        {
            Clipboard.SetText(SpecReportService.FormatReport(_report));
            AppHelpers.FlashButton(CopyBtn, "Copied!");
        }
        catch { }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    // ── Helpers ───────────────────────────────────────────────────────────

    private void ShowPanel(FrameworkElement panel)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ReportPanel .Visibility = Visibility.Collapsed;
        panel.Visibility        = Visibility.Visible;
        AppHelpers.FadeIn(panel);
    }

    private void AnimateDots()
    {
        _dotFrame = (_dotFrame + 1) % 3;
        AppHelpers.SetDotOpacities(_dotFrame, Dot1, Dot2, Dot3);
    }
}
