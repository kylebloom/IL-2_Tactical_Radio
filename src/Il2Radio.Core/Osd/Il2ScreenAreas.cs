namespace Il2Radio.Core.Osd;

/// <summary>A rectangle in fractions of the screen (0..1 from the top-left).</summary>
public readonly record struct ScreenRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Overlaps(ScreenRect other) =>
        Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;

    public static ScreenRect FromSize(double left, double top, double width, double height) => new(left, top, left + width, top + height);
}

/// <summary>An area where IL-2 draws its own text.</summary>
public sealed record Il2TextArea(string Name, ScreenRect Rect);

/// <summary>
/// Where IL-2 puts its own messages and subtitles, so the radio text can stay clear of them.
/// All positions are estimates in screen fractions:
/// - the message line ("Game is Paused", chat) sits bottom-left, about 3 % in from the left edge;
/// - mission subtitles (MCU_TR_Subtitle) are centred horizontally (HAlign = 1 in every Career
///   subtitle seen), either at the bottom (VAlign = 0) or in the middle (VAlign = 1) of the screen.
/// Their sizes depend on the text, so the areas are generous.
/// </summary>
public static class Il2ScreenAreas
{
    public static readonly Il2TextArea MessageLine = new("IL-2 message line", new(0.0, 0.72, 0.36, 1.0));
    public static readonly Il2TextArea MiddleSubtitles = new("IL-2 centre subtitles", new(0.30, 0.42, 0.70, 0.58));
    public static readonly Il2TextArea BottomSubtitles = new("IL-2 bottom subtitles", new(0.25, 0.80, 0.75, 0.96));

    public static IReadOnlyList<Il2TextArea> All { get; } = [MessageLine, MiddleSubtitles, BottomSubtitles];

    /// <summary>
    /// The areas a mission can use: the message line always, and the subtitle rows whose VAlign
    /// occurs in the mission's SubtitleInfo blocks. Unknown or missing VAlign values mean "assume all".
    /// </summary>
    public static IReadOnlyList<Il2TextArea> ForSubtitleAlignments(IEnumerable<int>? verticalAlignments)
    {
        var used = verticalAlignments?.Distinct().ToList();
        if (used is null || used.Count == 0 || used.Any(v => v is not (0 or 1)))
            return All;
        var areas = new List<Il2TextArea> { MessageLine };
        if (used.Contains(1))
            areas.Add(MiddleSubtitles);
        if (used.Contains(0))
            areas.Add(BottomSubtitles);
        return areas;
    }

    /// <summary>VAlign values of the SubtitleInfo blocks in a .Mission text file.</summary>
    public static IReadOnlyList<int> SubtitleAlignments(string missionText)
    {
        var values = new List<int>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(missionText, @"SubtitleInfo\s*\{[^}]*?VAlign\s*=\s*(\d+)"))
            values.Add(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        return values;
    }

    /// <summary>
    /// Shrinks <paramref name="rect"/> until it overlaps none of <paramref name="areas"/>. For each
    /// overlap it keeps the largest of the four parts left beside, above or below the area. Returns
    /// the result and the areas that forced a cut; an area that would leave nothing is ignored.
    /// </summary>
    public static (ScreenRect Rect, IReadOnlyList<Il2TextArea> Avoided) KeepClear(ScreenRect rect, IEnumerable<Il2TextArea> areas, double gap = 0.005)
    {
        var avoided = new List<Il2TextArea>();
        foreach (var area in areas)
        {
            if (!rect.Overlaps(area.Rect))
                continue;
            var a = area.Rect;
            ScreenRect[] parts =
            [
                rect with { Right = Math.Min(rect.Right, a.Left - gap) },
                rect with { Left = Math.Max(rect.Left, a.Right + gap) },
                rect with { Bottom = Math.Min(rect.Bottom, a.Top - gap) },
                rect with { Top = Math.Max(rect.Top, a.Bottom + gap) },
            ];
            var best = parts.Where(p => !p.IsEmpty).OrderByDescending(p => p.Area).FirstOrDefault();
            if (best.IsEmpty)
                continue;
            rect = best;
            avoided.Add(area);
        }
        return (rect, avoided);
    }
}
