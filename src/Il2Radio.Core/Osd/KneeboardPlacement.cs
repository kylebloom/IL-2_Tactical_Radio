using System.Globalization;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Osd;

/// <summary>Where on IL-2's picture the kneeboard is pinned.</summary>
public enum KneeboardAnchor { TopLeft, TopRight, BottomLeft, BottomRight, CentreLeft, CentreRight, Custom }

/// <summary>
/// The kneeboard's place on screen. A preset pins the sheet's matching corner (or the middle of its side) to that point of
/// the screen, <see cref="MarginShare"/> of the screen height in, so the large view grows away from it and switching size
/// never moves it off screen. <see cref="KneeboardAnchor.Custom"/> is the sheet's top-left corner in pixels from the
/// top-left of IL-2's picture, as given at <see cref="Screen"/> (scaled when IL-2 runs at another size; taken as given
/// when null). Either way the sheet is clamped to stay wholly on screen.
/// </summary>
public sealed record KneeboardPlacement(KneeboardAnchor Anchor, int X = 0, int Y = 0, (int Width, int Height)? Screen = null)
{
    /// <summary>The setting names of the presets, in the order the Settings tab lists them.</summary>
    public static readonly string[] Presets = ["centre-right", "top-right", "bottom-right", "top-left", "bottom-left", "centre-left", "custom"];

    public const string Default = "centre-right";

    /// <summary>Gap from the screen edge for the presets: a share of the screen height (about 22 px at 1080 lines).</summary>
    public const double MarginShare = 0.02;

    /// <summary>A preset name, or "custom" with its pixel corner; null for an unknown name.</summary>
    public static KneeboardPlacement? Parse(string? position, int[]? custom = null, int[]? customScreen = null)
    {
        var anchor = position?.Trim().ToLowerInvariant().Replace("center", "centre") switch
        {
            "top-left" => KneeboardAnchor.TopLeft,
            "top-right" => KneeboardAnchor.TopRight,
            "bottom-left" => KneeboardAnchor.BottomLeft,
            "bottom-right" => KneeboardAnchor.BottomRight,
            "centre-left" => KneeboardAnchor.CentreLeft,
            "centre-right" => KneeboardAnchor.CentreRight,
            "custom" => KneeboardAnchor.Custom,
            _ => (KneeboardAnchor?)null,
        };
        if (anchor is null)
            return null;
        if (anchor != KneeboardAnchor.Custom)
            return new KneeboardPlacement(anchor.Value);
        if (custom is not [var x, var y])
            return null;
        return new KneeboardPlacement(KneeboardAnchor.Custom, x, y, customScreen is [var w, var h] && w > 0 && h > 0 ? (w, h) : null);
    }

    /// <summary>The setting name of this placement's anchor ("centre-right").</summary>
    public string Name => Anchor switch
    {
        KneeboardAnchor.TopLeft => "top-left",
        KneeboardAnchor.TopRight => "top-right",
        KneeboardAnchor.BottomLeft => "bottom-left",
        KneeboardAnchor.BottomRight => "bottom-right",
        KneeboardAnchor.CentreLeft => "centre-left",
        KneeboardAnchor.CentreRight => "centre-right",
        _ => "custom",
    };

    /// <summary>The top-left pixel of a sheet of <paramref name="panel"/> size on a back buffer of <paramref name="screen"/> size.</summary>
    public (int X, int Y) Place((int Width, int Height) screen, (int Width, int Height) panel)
    {
        int margin = (int)Math.Round(screen.Height * MarginShare);
        int left = margin, right = screen.Width - margin - panel.Width;
        int top = margin, bottom = screen.Height - margin - panel.Height, middle = (screen.Height - panel.Height) / 2;
        var (x, y) = Anchor switch
        {
            KneeboardAnchor.TopLeft => (left, top),
            KneeboardAnchor.TopRight => (right, top),
            KneeboardAnchor.BottomLeft => (left, bottom),
            KneeboardAnchor.BottomRight => (right, bottom),
            KneeboardAnchor.CentreLeft => (left, middle),
            KneeboardAnchor.CentreRight => (right, middle),
            _ => Screen is { } given
                ? ((int)Math.Round(X * (double)screen.Width / given.Width), (int)Math.Round(Y * (double)screen.Height / given.Height))
                : (X, Y),
        };
        return (Math.Clamp(x, 0, Math.Max(0, screen.Width - panel.Width)), Math.Clamp(y, 0, Math.Max(0, screen.Height - panel.Height)));
    }

    /// <summary>"small" / "large" / "hidden" for a view, as settings.json writes it.</summary>
    public static string ViewName(KneeboardView view) => view.ToString().ToLowerInvariant();

    /// <summary>The view for a setting value, or null for an unknown one.</summary>
    public static KneeboardView? ParseView(string? value) =>
        Enum.TryParse<KneeboardView>(value?.Trim(), ignoreCase: true, out var view) && Enum.IsDefined(view) && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? view : null;

    /// <summary>The next view of the cycle: hidden → small → large → hidden.</summary>
    public static KneeboardView Next(KneeboardView view) => view switch
    {
        KneeboardView.Hidden => KneeboardView.Small,
        KneeboardView.Small => KneeboardView.Large,
        _ => KneeboardView.Hidden,
    };
}
