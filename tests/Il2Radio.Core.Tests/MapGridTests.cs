using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Tests;

/// <summary>
/// Stalingrad map squares, checked against the map on 2026-10-26: the four corners, and Karpovka,
/// Kalach, Basargino and Verbovka in the squares their mission-file positions occupy.
/// </summary>
public class MapGridTests
{
    [Theory]
    [InlineData(1500, 1500, "2301-1")]
    [InlineData(228000, 1500, "0101-7")]
    [InlineData(228000, 358000, "0136-9")]
    [InlineData(1500, 358000, "2336-3")]
    [InlineData(135863.578, 214954.438, "1022-5")] // Karpovka
    [InlineData(132558.703, 185433.203, "1019-2")] // Kalach
    [InlineData(127227.211, 228361.156, "1123-9")] // Basargino
    [InlineData(112622.930, 185624.969, "1219-2")] // Verbovka
    public void Mission_positions_fall_in_the_squares_printed_on_the_map(double north, double east, string square) =>
        Assert.Equal(square, MapGrid.TryLabel(north, east)!.Value.Text);

    [Fact]
    public void A_fix_within_a_kilometre_of_a_square_line_is_not_named()
    {
        Assert.Null(MapGrid.TryLabel(500, 5000));
        Assert.Null(MapGrid.TryLabel(230500, 5000));
        Assert.Null(MapGrid.TryLabel(5000, -100));
    }

    [Fact]
    public void A_fix_near_a_ninth_line_keeps_the_four_digits_only()
    {
        var square = MapGrid.TryLabel(5000, 3333);
        Assert.Equal("2301", square!.Value.Text);
        Assert.Null(square.Value.Ninth);
    }

    [Theory]
    [InlineData(48.727590782, 44.034126243, "1022-5")] // Karpovka
    [InlineData(48.694035322, 43.630242668, "1019-2")] // Kalach
    [InlineData(48.510741560, 43.641038765, "1219-2")] // Verbovka
    public void Acmi_lat_lon_of_an_airfield_lands_in_that_airfields_square(double latitude, double longitude, string square)
    {
        var (north, east) = MapGrid.ProjectStalingrad(latitude, longitude);
        Assert.Equal(square, MapGrid.TryLabel(north, east)!.Value.Text);
    }

    [Fact]
    public void Tusov_lies_in_1216_and_too_close_to_the_line_to_say()
    {
        var (north, east) = MapGrid.ProjectStalingrad(48.572257756, 43.258394687);
        Assert.Equal(12, MapGrid.StalingradNorthRows - (int)Math.Floor(north / MapGrid.SquareMetres));
        Assert.Equal(16, (int)Math.Floor(east / MapGrid.SquareMetres) + 1);
        Assert.Null(MapGrid.TryLabel(north, east));
    }

    [Fact]
    public void Waypoints_are_ordered_along_their_target_links()
    {
        var plan = MissionPlan.FromBlocks(MissionFileParser.Parse(new StringReader("""
            MCU_TR_Entity
            {
              Index = 21;
              MisObjID = 20;
            }
            MCU_Waypoint
            {
              Index = 31;
              Targets = [];
              Objects = [21];
              XPos = 1;
              YPos = 0;
              ZPos = 30;
            }
            MCU_Waypoint
            {
              Index = 30;
              Targets = [31];
              Objects = [21];
              XPos = 1;
              YPos = 0;
              ZPos = 10;
            }
            """)));

        var path = Assert.Single(plan.Routes[20]);
        Assert.Equal([10, 30], path.Select(p => p.East).ToArray());
    }
}
