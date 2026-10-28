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
}
