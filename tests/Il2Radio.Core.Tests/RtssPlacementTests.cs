using System.Buffers.Binary;
using System.Text;
using Il2Radio.Core.Osd;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>Where the radio text goes in the RTSS OSD: presets, margin, zoom and the &lt;P=x,y&gt; tag.</summary>
public class RtssPlacementTests
{
    private static readonly (int, int) Cell = (9, 16);

    [Theory]
    [InlineData(null, RtssAnchor.LeftMiddle)]
    [InlineData("left-middle", RtssAnchor.LeftMiddle)]
    [InlineData("top-left", RtssAnchor.TopLeft)]
    [InlineData("TOP_RIGHT", RtssAnchor.TopRight)]
    [InlineData("bottom-left", RtssAnchor.BottomLeft)]
    [InlineData("bottom-right", RtssAnchor.BottomRight)]
    [InlineData("default", RtssAnchor.RtssDefault)]
    public void Presets_parse(string? text, RtssAnchor expected) => Assert.Equal(expected, RtssPlacement.Parse(text).Anchor);

    [Fact]
    public void Explicit_coordinates_and_margin_parse()
    {
        var placement = RtssPlacement.Parse("120,400", "50");

        Assert.Equal(new RtssPlacement(RtssAnchor.Pixel, 50, 120, 400), placement);
        Assert.Equal((120, 400), placement.Position(1920, 1080, 2, 3, 40, Cell));
    }

    [Fact]
    public void Unknown_preset_is_rejected() => Assert.Throws<ArgumentException>(() => RtssPlacement.Parse("centre"));

    [Fact]
    public void Default_is_left_side_mid_height_with_padding_independent_of_text_size()
    {
        var placement = new RtssPlacement();

        Assert.Equal((32, 454), placement.Position(1920, 1080, 1, 3, 60, Cell));   // 0.42 × 1080 = 453.6
        Assert.Equal((32, 454), placement.Position(1920, 1080, 1, 8, 72, Cell));
    }

    [Fact]
    public void Corners_keep_the_margin_from_both_edges()
    {
        var size = (Lines: 3, Columns: 40); // 360 × 48 zoomed pixels with a 9 × 16 cell

        Assert.Equal((24, 24), (RtssPlacement.Parse("top-left", "24").Position(1920, 1080, 1, size.Lines, size.Columns, Cell)));
        Assert.Equal((1920 - 24 - 360, 24), RtssPlacement.Parse("top-right", "24").Position(1920, 1080, 1, size.Lines, size.Columns, Cell));
        Assert.Equal((24, 1080 - 24 - 48), RtssPlacement.Parse("bottom-left", "24").Position(1920, 1080, 1, size.Lines, size.Columns, Cell));
        Assert.Equal((1920 - 24 - 360, 1080 - 24 - 48), RtssPlacement.Parse("bottom-right", "24").Position(1920, 1080, 1, size.Lines, size.Columns, Cell));
    }

    [Fact]
    public void Coordinates_are_in_zoomed_pixels()
    {
        // Zoom 2: a 1920 × 1080 frame is 960 × 540 zoomed pixels and a 32 px margin is 16.
        Assert.Equal((16, 540 - 16 - 2 * 16), RtssPlacement.Parse("bottom-left").Position(1920, 1080, 2, 2, 10, Cell));
        Assert.Equal((16, 227), new RtssPlacement().Position(1920, 1080, 2, 2, 10, Cell)); // 0.42 × 540 = 226.8
    }

    [Fact]
    public void Text_too_big_for_the_corner_is_clamped_on_screen()
    {
        Assert.Equal((0, 0), RtssPlacement.Parse("bottom-right").Position(320, 200, 1, 40, 200, Cell));
    }

    [Fact]
    public void Rtss_default_emits_no_position()
    {
        Assert.Null(RtssPlacement.Parse("default").Position(1920, 1080, 1, 3, 40, Cell));
    }

    [Fact]
    public void Visible_size_ignores_tags()
    {
        Assert.Equal((2, 13), RtssOsd.VisibleSize("<C=FFFFFF>Gert: <C>hello<C>\n<C=AAAAAA>Heinz: hello!<C>"));
        Assert.Equal((0, 0), RtssOsd.VisibleSize(""));
    }

    [Fact]
    public void Osd_zoom_is_read_from_the_hooked_il2_application_entry()
    {
        const int appOffset = 64, appEntry = 400, apps = 3;
        var memory = new byte[appOffset + appEntry * apps];
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(offset), value);
        Put(RtssOsd.SignatureOffset, RtssOsd.Signature);
        Put(RtssOsd.VersionOffset, 0x00020015);
        Put(RtssOsd.AppEntrySizeOffset, appEntry);
        Put(RtssOsd.AppArrOffsetOffset, appOffset);
        Put(RtssOsd.AppArrSizeOffset, apps);
        void App(int index, uint pid, string path, uint zoom)
        {
            int at = appOffset + index * appEntry;
            Put(at + RtssOsd.AppProcessIdOffset, pid);
            Encoding.Latin1.GetBytes(path).CopyTo(memory, at + RtssOsd.AppNameOffset);
            Put(at + RtssOsd.AppOsdPixelOffset, zoom);
        }
        App(0, 100, @"C:\Games\other.exe", 3);
        App(1, 0, @"C:\stale\Il-2.exe", 4);  // free entry: ignored
        App(2, 200, @"C:\Steam\IL-2 Sturmovik Battle of Stalingrad\bin\game\Il-2.exe", 2);

        Assert.Equal(2, RtssOsd.AppOsdZoom(memory, "Il-2.exe"));
        Assert.Null(RtssOsd.AppOsdZoom(memory, "missing.exe"));
    }
}
