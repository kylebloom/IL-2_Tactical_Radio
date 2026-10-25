using Il2Radio.Core.Osd;

namespace Il2Radio.Core.Tests;

/// <summary>Keeping the radio text out of IL-2's own message line and subtitles.</summary>
public class Il2ScreenAreasTests
{
    [Fact]
    public void Default_overlay_area_is_narrowed_to_end_left_of_centre_subtitles()
    {
        var overlay = ScreenRect.FromSize(0.015, 0.40, 0.42, 0.30);

        var (rect, avoided) = Il2ScreenAreas.KeepClear(overlay, Il2ScreenAreas.All);

        Assert.Equal([Il2ScreenAreas.MiddleSubtitles], avoided);
        Assert.True(rect.Right < Il2ScreenAreas.MiddleSubtitles.Rect.Left);
        Assert.Equal(overlay.Top, rect.Top);
        Assert.Equal(overlay.Bottom, rect.Bottom);
    }

    [Fact]
    public void Area_reaching_into_the_message_line_ends_above_it()
    {
        var (rect, avoided) = Il2ScreenAreas.KeepClear(ScreenRect.FromSize(0.015, 0.5, 0.2, 0.4), [Il2ScreenAreas.MessageLine]);

        Assert.Equal([Il2ScreenAreas.MessageLine], avoided);
        Assert.True(rect.Bottom < Il2ScreenAreas.MessageLine.Rect.Top);
    }

    [Fact]
    public void Area_inside_an_il2_area_is_left_alone_rather_than_emptied()
    {
        var inside = ScreenRect.FromSize(0.05, 0.8, 0.1, 0.1);

        var (rect, avoided) = Il2ScreenAreas.KeepClear(inside, [Il2ScreenAreas.MessageLine]);

        Assert.Equal(inside, rect);
        Assert.Empty(avoided);
    }

    [Fact]
    public void Subtitle_rows_come_from_the_missions_vertical_alignments()
    {
        const string mission = """
            MCU_TR_Subtitle
            {
              SubtitleInfo
              {
                Duration = 5;
                HAlign = 1;
                VAlign = 0;
              }
            }
            """;

        var alignments = Il2ScreenAreas.SubtitleAlignments(mission);
        var areas = Il2ScreenAreas.ForSubtitleAlignments(alignments);

        Assert.Equal([0], alignments);
        Assert.Equal([Il2ScreenAreas.MessageLine, Il2ScreenAreas.BottomSubtitles], areas);
    }

    [Fact]
    public void Unknown_alignments_keep_every_area()
    {
        Assert.Equal(Il2ScreenAreas.All, Il2ScreenAreas.ForSubtitleAlignments([]));
        Assert.Equal(Il2ScreenAreas.All, Il2ScreenAreas.ForSubtitleAlignments([2]));
    }

    [Fact]
    public void Rtss_left_middle_block_is_fitted_between_subtitles_and_message_line()
    {
        // 2560x1440 at OSD zoom 2: 1280x720 zoomed pixels, 9x16 cells.
        var fit = new RtssPlacement().Fit(2560, 1440, 2, (9, 16), columns: 72, rows: 24, Il2ScreenAreas.All);

        Assert.Empty(fit.Overlapping);
        Assert.Contains(Il2ScreenAreas.MiddleSubtitles, fit.Avoided);
        Assert.Contains(Il2ScreenAreas.MessageLine, fit.Avoided);
        // Right edge before the subtitles at 30 %: (0.30 * 1280 - 16 margin) / 9 = 40.9 columns.
        Assert.Equal(40, fit.Columns);
        // Bottom above the message line at 72 %: (0.72 * 720 - 0.42 * 720) / 16 = 13.5 rows.
        Assert.Equal(13, fit.Rows);
    }

    [Fact]
    public void Rtss_block_that_cannot_avoid_an_area_keeps_its_size_and_reports_the_overlap()
    {
        var bottomLeft = new RtssPlacement(RtssAnchor.BottomLeft);

        var fit = bottomLeft.Fit(1920, 1080, 1, (9, 16), columns: 72, rows: 8, [Il2ScreenAreas.MessageLine]);

        Assert.Equal((72, 8), (fit.Columns, fit.Rows));
        Assert.Equal([Il2ScreenAreas.MessageLine], fit.Overlapping);
    }

    [Fact]
    public void Rtss_block_already_clear_is_unchanged()
    {
        var fit = new RtssPlacement(RtssAnchor.TopRight).Fit(1920, 1080, 1, (9, 16), columns: 40, rows: 8, Il2ScreenAreas.All);

        Assert.Equal((40, 8), (fit.Columns, fit.Rows));
        Assert.Empty(fit.Avoided);
    }
}
