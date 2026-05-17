using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Brushes        = System.Windows.Media.Brushes;
using Color          = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily     = System.Windows.Media.FontFamily;
using Orientation    = System.Windows.Controls.Orientation;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SpecIQ;

public partial class HistoryWindow : Window
{
    public HistoryWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        BenchmarkHistory.Clear();
        EntriesPanel.Children.Clear();
        EmptyText.Visibility = Visibility.Visible;
        ClearBtn.IsEnabled   = false;
    }

    private void Refresh()
    {
        var entries = BenchmarkHistory.Load();
        EntriesPanel.Children.Clear();

        if (entries.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            ClearBtn.IsEnabled   = false;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        ClearBtn.IsEnabled   = true;

        foreach (var entry in entries)
            EntriesPanel.Children.Add(BuildRow(entry));
    }

    // ── Row builder ───────────────────────────────────────────────────────

    private static UIElement BuildRow(HistoryEntry entry)
    {
        var (badgeHex, toolShort) = entry.Tool switch
        {
            HistoryTool.Geekbench6    => ("#3B82F6", "GB6"),
            HistoryTool.GeekbenchAI   => ("#8B5CF6", "GBAI"),
            HistoryTool.Cinebench     => ("#F59E0B", "CB"),
            HistoryTool.ProcyonCV     => ("#0EA5E9", "PCV"),
            HistoryTool.ProcyonOffice => ("#10B981", "POFF"),
            HistoryTool.Blender       => ("#F97316", "BLND"),
            _                         => ("#6B7280", "?"),
        };

        // Date: absolute on older entries, relative for recent ones
        var date    = entry.RunAtDate;
        var age     = DateTime.Now - date;
        var dateStr = age.TotalMinutes < 1 ? "Just now"
                    : age.TotalHours   < 1 ? $"{(int)age.TotalMinutes}m ago"
                    : age.TotalDays    < 1 ? $"{(int)age.TotalHours}h ago  ·  {date:h:mm tt}"
                    : age.TotalDays    < 7 ? $"{(int)age.TotalDays}d ago  ·  {date:MMM d}"
                    : date.ToString("MMM d, yyyy  h:mm tt");

        // Duration
        var durStr = FormatDuration(entry.DurationSeconds);

        var row = new Border
        {
            Background   = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding      = new Thickness(10, 8, 10, 8),
            Margin       = new Thickness(0, 0, 0, 6),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ── Badge ──
        var badge = new Border
        {
            Background          = new SolidColorBrush(HexColor(badgeHex)),
            CornerRadius        = new CornerRadius(5),
            Padding             = new Thickness(5, 2, 5, 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        badge.Child = new TextBlock
        {
            Text       = toolShort,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 8,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
        };
        Grid.SetColumn(badge, 0);

        // ── Note + date + duration ──
        var middle = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 8, 0),
        };
        middle.Children.Add(new TextBlock
        {
            Text       = string.IsNullOrEmpty(entry.Note) ? "Standard run" : entry.Note,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
        });
        // Date + optional duration on one subdued line
        var meta = durStr != null ? $"{dateStr}  ·  {durStr}" : dateStr;
        middle.Children.Add(new TextBlock
        {
            Text       = meta,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 9,
            Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
        });
        Grid.SetColumn(middle, 1);

        // ── Scores ──
        var scoreStack = new StackPanel
        {
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        foreach (var (label, value) in ScoreLines(entry))
        {
            var scoreRow = new StackPanel { Orientation = Orientation.Horizontal };
            scoreRow.Children.Add(new TextBlock
            {
                Text              = label + "  ",
                FontFamily        = new FontFamily("Segoe UI"),
                FontSize          = 9,
                Foreground        = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            scoreRow.Children.Add(new TextBlock
            {
                Text       = value,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
            });
            scoreStack.Children.Add(scoreRow);
        }
        Grid.SetColumn(scoreStack, 2);

        grid.Children.Add(badge);
        grid.Children.Add(middle);
        grid.Children.Add(scoreStack);
        row.Child = grid;
        return row;
    }

    private static string? FormatDuration(int? seconds)
    {
        if (seconds is null or <= 0) return null;
        var t = TimeSpan.FromSeconds(seconds.Value);
        if (t.TotalHours >= 1)  return $"{(int)t.TotalHours}h {t.Minutes:D2}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds:D2}s";
        return $"{t.Seconds}s";
    }

    private static Color HexColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromRgb(
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    private static IEnumerable<(string Label, string Value)> ScoreLines(HistoryEntry e)
    {
        switch (e.Tool)
        {
            case HistoryTool.Geekbench6:
                if (e.ScoreA > 0) yield return ("SC", $"{(int)e.ScoreA:N0}");
                if (e.ScoreB > 0) yield return ("MC", $"{(int)e.ScoreB:N0}");
                break;
            case HistoryTool.GeekbenchAI:
                if (e.ScoreA > 0) yield return ("FP32",  $"{(int)e.ScoreA:N0}");
                if (e.ScoreB > 0) yield return ("FP16",  $"{(int)e.ScoreB:N0}");
                if (e.ScoreC > 0) yield return ("Quant", $"{(int)e.ScoreC:N0}");
                break;
            case HistoryTool.Cinebench:
                if (e.ScoreA > 0) yield return ("1T", $"{(int)e.ScoreA:N0}");
                if (e.ScoreB > 0) yield return ("nT", $"{(int)e.ScoreB:N0}");
                break;
            case HistoryTool.ProcyonCV:
            case HistoryTool.ProcyonOffice:
            case HistoryTool.Blender:
                if (e.ScoreA > 0) yield return ("Score", $"{(int)e.ScoreA:N0}");
                break;
        }
    }
}
