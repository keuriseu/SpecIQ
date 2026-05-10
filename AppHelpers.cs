using System.Text.Json;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SpecIQ;

/// <summary>
/// Shared utilities used by multiple windows and services.
/// All members are stateless / pure so they are safe to call from any thread
/// (UI helpers must of course be called on the dispatcher thread).
/// </summary>
internal static class AppHelpers
{
    // ── JSON ──────────────────────────────────────────────────────────────

    /// <summary>Shared options for human-readable JSON persistence files.</summary>
    public static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ── Path validation ───────────────────────────────────────────────────

    /// <summary>
    /// Returns true when <paramref name="path"/> is rooted inside one of the
    /// standard Windows install locations (Program Files, Program Files (x86),
    /// or the current user's LocalAppData).  Used to reject paths returned by
    /// external tools such as <c>where.exe</c> that could be PATH-poisoned.
    /// </summary>
    public static bool IsAllowedExePath(string path)
    {
        var pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return path.StartsWith(pf,    StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(pfx86, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(local, StringComparison.OrdinalIgnoreCase);
    }

    // ── Formatting ────────────────────────────────────────────────────────

    /// <summary>
    /// Formats a duration as "1h 23m" (hours present) or "4m 05s" (minutes only).
    /// </summary>
    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
            : $"{t.Minutes}m {t.Seconds:D2}s";

    // ── UI helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Briefly sets a button's content to <paramref name="flashText"/>, then
    /// restores the original content after two seconds.
    /// </summary>
    public static void FlashButton(System.Windows.Controls.Button btn, string flashText = "Copied!")
    {
        var original = btn.Content;
        btn.Content  = flashText;
        var timer    = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick  += (_, _) => { btn.Content = original; timer.Stop(); };
        timer.Start();
    }

    /// <summary>
    /// Fades <paramref name="element"/> in from 0→1 opacity over 150 ms.
    /// Call after setting Visibility = Visible.
    /// </summary>
    public static void FadeIn(UIElement element)
    {
        element.Opacity = 0;
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>
    /// Applies the three-step opacity cycle to a dot-indicator row.
    /// Call this inside a per-tick handler after incrementing <paramref name="frame"/>.
    /// Works with any <see cref="UIElement"/> (Ellipse, TextBlock, etc.).
    /// </summary>
    public static void SetDotOpacities(int frame, UIElement dot1, UIElement dot2, UIElement dot3)
    {
        dot1.Opacity = frame == 0 ? 1.0 : frame == 2 ? 0.25 : 0.5;
        dot2.Opacity = frame == 1 ? 1.0 : frame == 0 ? 0.25 : 0.5;
        dot3.Opacity = frame == 2 ? 1.0 : frame == 1 ? 0.25 : 0.5;
    }
}
