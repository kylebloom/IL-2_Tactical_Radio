using System.Globalization;

namespace Il2Radio.Core.Osd;

public enum RtssAnchor
{
    RtssDefault, // no position tag: RTSS's own profile OSD position (top-left by default)
    LeftMiddle,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Pixel,       // explicit X,Y in RTSS zoomed pixels
}

/// <summary>
/// Where the radio text goes in the RTSS OSD. RTSS places text that follows a &lt;P=x,y&gt; tag at
/// absolute coordinates from the top-left of the frame, in "zoomed pixel units" (screen pixels
/// divided by the OSD zoom). Corner presets are converted to such coordinates from the game's
/// resolution, the zoom and the size of the text block. <see cref="Margin"/> is in screen pixels.
/// </summary>
public sealed record RtssPlacement(RtssAnchor Anchor = RtssAnchor.LeftMiddle, int Margin = 32, int X = 0, int Y = 0)
{
    /// <summary>
    /// Top of the text at 42 % of the screen height for <see cref="RtssAnchor.LeftMiddle"/>: on the
    /// left canopy side, clear of the gunsight and instrument panel, and growing downwards towards,
    /// but not into, IL-2's own bottom-left message line (~73 %).
    /// </summary>
    public const double LeftMiddleTop = 0.42;

    /// <summary>Parses --rtss-pos (a preset name, "default" or "X,Y") and --rtss-margin.</summary>
    public static RtssPlacement Parse(string? position, string? margin = null)
    {
        var placement = new RtssPlacement();
        if (int.TryParse(margin, CultureInfo.InvariantCulture, out int m))
            placement = placement with { Margin = Math.Max(0, m) };
        if (string.IsNullOrWhiteSpace(position))
            return placement;

        if (position.Split(',') is [var xs, var ys] && int.TryParse(xs, CultureInfo.InvariantCulture, out int x) && int.TryParse(ys, CultureInfo.InvariantCulture, out int y))
            return placement with { Anchor = RtssAnchor.Pixel, X = x, Y = y };

        return placement with
        {
            Anchor = position.Trim().ToLowerInvariant().Replace("_", "-") switch
            {
                "default" or "rtss" => RtssAnchor.RtssDefault,
                "left-middle" or "middle-left" or "left" => RtssAnchor.LeftMiddle,
                "top-left" => RtssAnchor.TopLeft,
                "top-right" => RtssAnchor.TopRight,
                "bottom-left" => RtssAnchor.BottomLeft,
                "bottom-right" => RtssAnchor.BottomRight,
                _ => throw new ArgumentException($"--rtss-pos {position}: expected left-middle, top-left, top-right, bottom-left, bottom-right, default or X,Y"),
            },
        };
    }

    /// <summary>
    /// The &lt;P=x,y&gt; coordinates (zoomed pixels) for a text block of <paramref name="lines"/> ×
    /// <paramref name="columns"/> characters, or null to leave positioning to RTSS. Right and bottom
    /// anchors depend on <paramref name="cell"/>, the OSD character size in zoomed pixels, which is
    /// an estimate unless calibrated.
    /// </summary>
    public (int X, int Y)? Position(int screenWidth, int screenHeight, double zoom, int lines, int columns, (int Width, int Height) cell)
    {
        if (Anchor == RtssAnchor.RtssDefault)
            return null;
        if (Anchor == RtssAnchor.Pixel)
            return (X, Y);

        zoom = zoom > 0 ? zoom : 1;
        double width = screenWidth / zoom, height = screenHeight / zoom, margin = Margin / zoom;
        double blockWidth = columns * cell.Width, blockHeight = lines * cell.Height;

        double left = margin, right = width - margin - blockWidth;
        double top = margin, bottom = height - margin - blockHeight;
        var (x, y) = Anchor switch
        {
            RtssAnchor.TopLeft => (left, top),
            RtssAnchor.TopRight => (right, top),
            RtssAnchor.BottomLeft => (left, bottom),
            RtssAnchor.BottomRight => (right, bottom),
            _ => (left, height * LeftMiddleTop),
        };
        return ((int)Math.Max(0, Math.Round(x)), (int)Math.Max(0, Math.Round(y)));
    }

    /// <summary>
    /// The largest text block, at most <paramref name="columns"/> × <paramref name="rows"/>, that this
    /// anchor places clear of IL-2's own text <paramref name="areas"/>. <see cref="RtssFit.Avoided"/>
    /// lists the areas the full-size block would have covered; <see cref="RtssFit.Overlapping"/> the
    /// ones no smaller block could avoid either (the requested size is then kept).
    /// </summary>
    public RtssFit Fit(int screenWidth, int screenHeight, double zoom, (int Width, int Height) cell, int columns, int rows, IReadOnlyList<Il2TextArea> areas)
    {
        if (Anchor == RtssAnchor.RtssDefault || areas.Count == 0)
            return new RtssFit(columns, rows, [], []);
        zoom = zoom > 0 ? zoom : 1;

        List<Il2TextArea> Hits(int c, int r)
        {
            var (x, y) = Position(screenWidth, screenHeight, zoom, r, c, cell)!.Value;
            var block = ScreenRect.FromSize(x * zoom / screenWidth, y * zoom / screenHeight,
                c * cell.Width * zoom / screenWidth, r * cell.Height * zoom / screenHeight);
            return areas.Where(a => block.Overlaps(a.Rect)).ToList();
        }

        var full = Hits(columns, rows);
        if (full.Count == 0)
            return new RtssFit(columns, rows, [], []);

        (int Columns, int Rows)? best = null;
        for (int c = columns; c >= Math.Min(columns, MinColumns); c--)
            for (int r = rows; r >= 1; r--)
                if (Hits(c, r).Count == 0)
                {
                    if (best is null || c * r > best.Value.Columns * best.Value.Rows)
                        best = (c, r);
                    break; // fewer rows only make it smaller
                }

        return best is var (bc, br) && br >= Math.Min(rows, MinRows)
            ? new RtssFit(bc, br, full, [])
            : new RtssFit(columns, rows, [], full);
    }

    /// <summary>Narrower or shorter blocks than this are not worth keeping clear; the text would be unreadable.</summary>
    public const int MinColumns = 24, MinRows = 3;
}

/// <summary>The text block size chosen by <see cref="RtssPlacement.Fit"/> and why.</summary>
public sealed record RtssFit(int Columns, int Rows, IReadOnlyList<Il2TextArea> Avoided, IReadOnlyList<Il2TextArea> Overlapping);
