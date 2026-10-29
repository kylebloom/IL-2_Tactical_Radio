using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.Memory;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Osd;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>
/// The kneeboard plot: a fixed sheet of the briefed area, with pencil marks made only by what the radio said. The
/// scenario is a Career-shaped sweep on the Stalingrad grid: east along 125 000 N, south to an attack area, home to Tusov.
/// </summary>
public class KneeboardTests
{
    private const string PlayerName = "Gert Knoll", LeaderName = "Richard Raub", MateName = "Enno Drecker";
    private const int Player = 1055743, Leader = 1001471, Mate = 1002495, Enemy = 2000127;
    private readonly ManualTimeProvider _clock = new();
    private readonly ContactTracker _tracker;
    private readonly Kneeboard _kneeboard = new();
    private static readonly DateTime T0 = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    public KneeboardTests()
    {
        _tracker = new ContactTracker(_clock);
        _tracker.SetPlan(Plan());
        LogPlane(Player, PlayerName, 2, isPlayer: true);
        LogPlane(Leader, LeaderName, 1);
        LogPlane(Mate, MateName, 3);
        var origin = WorldPoint.FromIl2(125_000, 3000, 190_000);
        _tracker.Apply(new ObjectSpawnedEvent(0, Enemy, "Hurricane Mk.II", 101, "Pilot", -1, origin));
        _tracker.Apply(new ObjectSpawnedEvent(0, Enemy + 1, "BotPilot", 101, "BotPilot", Enemy, origin));
        _kneeboard.Reset(_tracker.Plan);
    }

    private static MissionPlan Plan(string waypoints = Sweep) => MissionPlan.FromBlocks(MissionFileParser.Parse(new StringReader($$"""
        Options
        {
          GuiMap = "stalingrad-1942";
          Time = 12:0:0;
          Countries
          {
            101 : 1;
            201 : 2;
          }
        }
        Plane
        {
          Name = "{{LeaderName}}";
          Index = 10;
          LinkTrId = 21;
          Country = 201;
          Callsign = 8;
          Callnum = 1;
          XPos = 119849.602;
          YPos = 57;
          ZPos = 157794.172;
        }
        Plane
        {
          Name = "{{PlayerName}}";
          Index = 11;
          Country = 201;
          AILevel = 0;
          Callsign = 8;
          Callnum = 2;
          XPos = 119849.602;
          YPos = 57;
          ZPos = 157794.172;
        }
        MCU_TR_Entity
        {
          Index = 21;
          MisObjID = 10;
        }
        {{waypoints}}
        Airfield
        {
          Name = "Tusov";
          Index = 40;
          Country = 201;
          XPos = 119604.523;
          YPos = 55.385;
          ZPos = 157026.375;
          Chart
          {
          }
        }
        """)));

    private const string Sweep = """
        MCU_Waypoint
        {
          Index = 30;
          Name = "WAYPOINT-TAKEOFF";
          Targets = [31];
          Objects = [21];
          XPos = 125000;
          YPos = 1500;
          ZPos = 160000;
        }
        MCU_Waypoint
        {
          Index = 31;
          Name = "CMD_Waypoint";
          Targets = [32];
          Objects = [21];
          XPos = 125000;
          YPos = 3000;
          ZPos = 185000;
        }
        MCU_Waypoint
        {
          Index = 32;
          Name = "CMD_AttackArea";
          Targets = [33];
          Objects = [21];
          XPos = 105000;
          YPos = 3000;
          ZPos = 185000;
        }
        MCU_Waypoint
        {
          Index = 33;
          Name = "WAYPOINT_LAND";
          Targets = [];
          Objects = [21];
          XPos = 119859.883;
          YPos = 500;
          ZPos = 157809.234;
        }
        """;

    private void LogPlane(int id, string pilot, int formation, bool isPlayer = false)
    {
        var origin = WorldPoint.FromIl2(119848, 57, 157764);
        _tracker.Apply(new ObjectSpawnedEvent(0, id, "Bf 109 F-4", 201, pilot, -1, origin));
        _tracker.Apply(new ObjectSpawnedEvent(0, id + 1024, "BotPilot", 201, "BotPilot", id, origin));
        _tracker.Apply(new PlaneSpawnedEvent(0, id, id + 1024, origin, "Bf 109 F-4", 201, formation, 1, 0, -1, isPlayer, ""));
    }

    private static MemoryAircraft At(int id, double north, double east, double height = 3000) => new(id, north, height, east, 110);

    /// <summary>Two memory reads a second apart, flying east, so every aircraft has a course over ground.</summary>
    private void Read(double time, params MemoryAircraft[] aircraft)
    {
        _tracker.Apply(aircraft.Select(a => a with { East = a.East - 100 }).ToList(), time - 1);
        _tracker.Apply(aircraft, time);
    }

    /// <summary>The player on leg 1 in grid 1118-5, his leader 300 m off his wing.</summary>
    private void OnLegOne(double height = 3000) => Read(10, At(Player, 125_500, 175_000, height), At(Leader, 125_500, 175_300, height));

    private Contact Who(int id) => _tracker.Contacts.Single(c => c.LogId == id);

    private void Record(RadioCommand? command, RadioResult result, DateTime utc)
    {
        foreach (var reply in result.Replies)
            _kneeboard.Record(command, reply, _tracker.Player?.LastFix?.Heading, utc, "12:00");
    }

    private KneeboardPicture Picture(DateTime utc) => _kneeboard.Picture(utc, KneeboardView.Large)!;

    [Fact]
    public void The_sheet_covers_the_briefed_route_and_home_field_with_a_square_of_margin_on_grid_lines()
    {
        var sheet = _kneeboard.Sheet!;

        Assert.Equal("Stalingrad", sheet.MapName);
        Assert.True(sheet.Labelled);
        Assert.Equal("Tusov", sheet.HomeField?.Name);
        Assert.Equal(4, sheet.Route.Count);
        Assert.Null(sheet.RouteOf);
        // Route and field span 105–125 km north and 157–185 km east; a square of margin, snapped out to the 10 km lines.
        Assert.True(sheet.South <= 95_000 && sheet.North >= 135_000);
        Assert.True(sheet.West <= 147_000 && sheet.East >= 195_000);
        Assert.All(new[] { sheet.South, sheet.North, sheet.West, sheet.East }, edge => Assert.Equal(0, edge % MapGrid.SquareMetres));
        Assert.InRange(sheet.WidthMetres / sheet.HeightMetres, KneeboardSheet.MinAspect, KneeboardSheet.MaxAspect + 0.5);
    }

    [Fact]
    public void An_escort_briefed_only_with_its_landing_point_still_gets_a_sheet_of_the_country_round_the_field()
    {
        var escort = new Kneeboard();
        escort.Reset(Plan("""
            MCU_Waypoint
            {
              Index = 33;
              Name = "WAYPOINT_LAND";
              Targets = [];
              Objects = [21];
              XPos = 119859.883;
              YPos = 500;
              ZPos = 157809.234;
            }
            """));

        Assert.True(escort.Sheet!.HeightMetres >= KneeboardSheet.MinSpanMetres);
        Assert.True(escort.Sheet.WidthMetres >= KneeboardSheet.MinSpanMetres);
        Assert.Null(escort.Sheet.RouteOf);
    }

    [Fact]
    public void An_escort_plots_the_covered_flights_route_and_names_it_as_theirs()
    {
        var escort = new Kneeboard();
        escort.Reset(Plan("""
            MCU_Waypoint
            {
              Index = 33;
              Name = "WAYPOINT_LAND";
              Targets = [];
              Objects = [21];
              XPos = 119859.883;
              YPos = 500;
              ZPos = 157809.234;
            }
            Plane
            {
              Name = "Karl Holle";
              Index = 50;
              LinkTrId = 51;
              Country = 201;
              Callsign = 11;
              Callnum = 1;
              Script = "LuaScripts\\WorldObjects\\Planes\\he111h6.txt";
              XPos = 120000;
              YPos = 1000;
              ZPos = 158000;
            }
            MCU_TR_Entity
            {
              Index = 51;
              MisObjID = 50;
            }
            MCU_Waypoint
            {
              Index = 60;
              Name = "FRIENDLY_B-WAYPOINT";
              Targets = [61];
              Objects = [51];
              XPos = 120000;
              YPos = 1000;
              ZPos = 158000;
            }
            MCU_Waypoint
            {
              Index = 61;
              Name = "CMD_AttackArea";
              Targets = [];
              Objects = [51];
              XPos = 190000;
              YPos = 3000;
              ZPos = 210000;
            }
            MCU_CMD_Cover
            {
              Index = 70;
              Name = "Cover";
              Targets = [51];
              Objects = [21];
            }
            """));

        var sheet = escort.Sheet!;
        Assert.Equal("bombers", sheet.RouteOf);
        Assert.Equal("BOMBERS' ROUTE", sheet.RouteHeading);
        Assert.Equal(["FRIENDLY_B-WAYPOINT", "CMD_AttackArea"], sheet.Route.Select(w => w.Name).ToArray());
        Assert.Null(sheet.OwnFrom);
        Assert.Equal("Tusov", sheet.HomeField?.Name);
        Assert.True(sheet.North >= 190_000);
        Assert.True(sheet.East >= 210_000);
    }

    [Fact]
    public void An_escort_follows_the_bombers_home_and_then_draws_its_own_leg_back()
    {
        var escort = new Kneeboard();
        escort.Reset(Plan("""
            MCU_Waypoint
            {
              Index = 33;
              Name = "WAYPOINT_LAND";
              Targets = [];
              Objects = [21];
              XPos = 119859.883;
              YPos = 500;
              ZPos = 157809.234;
            }
            Plane
            {
              Name = "Karl Holle";
              Index = 50;
              LinkTrId = 51;
              Country = 201;
              Callsign = 11;
              Callnum = 1;
              Script = "LuaScripts\\WorldObjects\\Planes\\he111h6.txt";
              XPos = 120000;
              YPos = 1000;
              ZPos = 158000;
            }
            MCU_TR_Entity
            {
              Index = 51;
              MisObjID = 50;
            }
            MCU_Waypoint
            {
              Index = 60;
              Name = "FRIENDLY_B-WAYPOINT";
              Targets = [61];
              Objects = [51];
              XPos = 119916;
              YPos = 2000;
              ZPos = 157812;
            }
            MCU_Waypoint
            {
              Index = 61;
              Name = "FRIENDLY_BOMBERS_ACTION";
              Targets = [62];
              Objects = [51];
              XPos = 197524;
              YPos = 2000;
              ZPos = 215736;
            }
            MCU_Activate
            {
              Index = 62;
              Name = "Activate";
              Targets = [];
              Objects = [63];
            }
            MCU_TR_ComplexTrigger
            {
              Index = 63;
              Name = "Complex Trigger";
              Objects = [];
              OnEvents
              {
                OnEvent
                {
                  Type = 71;
                  TarId = 64;
                }
              }
            }
            MCU_Timer
            {
              Index = 64;
              Name = "5s_DELAY";
              Targets = [65];
              Objects = [];
            }
            MCU_Waypoint
            {
              Index = 65;
              Name = "FRIENDLY_B-WAYPOINT";
              Targets = [];
              Objects = [51];
              XPos = 123487;
              YPos = 2500;
              ZPos = 178855;
            }
            MCU_CMD_Cover
            {
              Index = 70;
              Name = "Cover";
              Targets = [51];
              Objects = [21];
            }
            """));

        var sheet = escort.Sheet!;
        Assert.Equal("bombers", sheet.RouteOf);
        Assert.Equal(3, sheet.OwnFrom);
        Assert.Equal(
            ["FRIENDLY_B-WAYPOINT", "FRIENDLY_BOMBERS_ACTION", "FRIENDLY_B-WAYPOINT", "WAYPOINT_LAND"],
            sheet.Route.Select(w => w.Name).ToArray());
        Assert.Equal(123_487, sheet.Route[2].Position.North, 0);
        Assert.Equal("Tusov", sheet.HomeField?.Name);
    }

    [Fact]
    public void A_sweep_keeps_its_own_route_when_the_flight_also_covers_someone()
    {
        var sweep = new Kneeboard();
        sweep.Reset(Plan(Sweep + """
            Plane
            {
              Name = "Karl Holle";
              Index = 50;
              LinkTrId = 51;
              Country = 201;
              Callsign = 11;
              Callnum = 1;
              Script = "LuaScripts\\WorldObjects\\Planes\\he111h6.txt";
              XPos = 120000;
              YPos = 1000;
              ZPos = 158000;
            }
            MCU_TR_Entity
            {
              Index = 51;
              MisObjID = 50;
            }
            MCU_Waypoint
            {
              Index = 60;
              Name = "FRIENDLY_B-WAYPOINT";
              Targets = [61];
              Objects = [51];
              XPos = 120000;
              YPos = 1000;
              ZPos = 158000;
            }
            MCU_Waypoint
            {
              Index = 61;
              Name = "CMD_AttackArea";
              Targets = [];
              Objects = [51];
              XPos = 190000;
              YPos = 3000;
              ZPos = 210000;
            }
            MCU_CMD_Cover
            {
              Index = 70;
              Name = "Cover";
              Targets = [51];
              Objects = [21];
            }
            """));

        Assert.Null(sweep.Sheet!.RouteOf);
        Assert.Equal(4, sweep.Sheet.Route.Count);
        Assert.Contains(sweep.Sheet.Route, w => w.Name == "CMD_AttackArea" && w.Position.North == 105_000);
    }

    [Fact]
    public void The_leaders_route_reply_shades_the_ninth_he_named_and_starts_dead_reckoning()
    {
        OnLegOne();
        var result = RouteCommands.RoutePosition(_tracker);
        Assert.Contains("grid 1118-5", Assert.Single(result.Replies).Text);

        Record(RadioCommand.RoutePosition, result, T0);
        var picture = Picture(T0);

        var fix = Assert.Single(picture.Marks, m => m.Mark.Subject == PlotSubject.Player).Mark;
        Assert.Equal(PlotShape.Cell, fix.Shape);
        Assert.Equal((123_333, 173_333, 3_333), ((int)fix.North, (int)fix.East, (int)fix.Size));
        Assert.Matches("^12:00, .*1$", fix.Source); // when, and who placed him: the leader
        Assert.True(picture.Player!.Mark.DeadReckoned);
        Assert.StartsWith("Last fix 12:00", picture.Footer[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_players_own_map_reading_is_not_a_fix()
    {
        // Alone, with no leader to ask, the route call is his own reading of the map: reading the game, not a fix.
        Read(10, At(Player, 125_500, 175_000));
        var result = RouteCommands.RoutePosition(_tracker);
        Assert.Equal(RouteCommands.OwnLabel, Assert.Single(result.Replies).SpeakerLabel);

        Record(RadioCommand.RoutePosition, result, T0);
        var picture = Picture(T0);

        Assert.DoesNotContain(picture.Marks, m => m.Mark.Subject == PlotSubject.Player);
        Assert.Null(picture.Player);
        Assert.Contains(picture.Footer, f => f.Contains("1118-5", StringComparison.Ordinal)); // the steer line is still noted
    }

    [Fact]
    public void A_noted_course_and_speed_are_rounded_the_way_he_would_write_them()
    {
        Assert.Equal(90, Kneeboard.NotedCourse(94));
        Assert.Equal(100, Kneeboard.NotedCourse(96));
        Assert.Equal(0, Kneeboard.NotedCourse(355));
        Assert.Equal(100, Kneeboard.NotedSpeed(100), 3); // 360 km/h already on a step
        Assert.Equal(380 / 3.6, Kneeboard.NotedSpeed(105), 3); // 378 km/h, written as 380
        var (north, east) = Kneeboard.WindError(T0);
        Assert.InRange(Math.Sqrt(north * north + east * east) * 3.6, Kneeboard.WindErrorMinKmh - 0.01, Kneeboard.WindErrorMaxKmh + 0.01);
    }

    [Fact]
    public void Dead_reckoning_holds_the_noted_course_and_speed_and_walks_off_on_a_wrong_wind()
    {
        OnLegOne();
        Record(RadioCommand.RoutePosition, RouteCommands.RoutePosition(_tracker), T0);
        var start = Picture(T0).Player!.Mark;
        var (windNorth, windEast) = Kneeboard.WindError(T0);

        for (int s = 1; s <= 60; s++)
            _kneeboard.Advance(T0.AddSeconds(s), 90, 100); // a minute due east at 100 m/s, which is 360 km/h
        var dr = Picture(T0.AddSeconds(60)).Player!.Mark;

        // The pencil flies the noted 090 at 100 m/s. The wind is the only reason it is not the truth.
        Assert.InRange(dr.East, start.East + 6_000 + windEast * 60 - 1, start.East + 6_000 + windEast * 60 + 1);
        Assert.InRange(dr.North, start.North + windNorth * 60 - 1, start.North + windNorth * 60 + 1);
        Assert.True(Math.Abs(windNorth) > 0.1 || Math.Abs(windEast) > 0.1, "the wind must move the pencil off the track");
        Assert.Empty(Picture(T0.AddSeconds(60)).Pencil);
        Assert.InRange(dr.Size - start.Size, Kneeboard.DriftPerMetre * 6_000 + Kneeboard.DriftPerMinute - 1, Kneeboard.DriftPerMetre * 6_000 + Kneeboard.DriftPerMinute + 1);
    }

    [Fact]
    public void Dead_reckoning_does_not_follow_a_change_of_speed_or_a_few_degrees_of_course()
    {
        OnLegOne();
        Record(RadioCommand.RoutePosition, RouteCommands.RoutePosition(_tracker), T0);
        var start = Picture(T0).Player!.Mark;
        var (windNorth, windEast) = Kneeboard.WindError(T0);

        for (int s = 1; s <= 10; s++)
            _kneeboard.Advance(T0.AddSeconds(s), 96, 100); // 096 is written 100; 100 m/s is the note
        for (int s = 11; s <= 60; s++)
            _kneeboard.Advance(T0.AddSeconds(s), 96, 150); // slowing or speeding is not replotted until the next leg
        var dr = Picture(T0.AddSeconds(60)).Player!.Mark;

        double course = Kneeboard.NotedCourse(96) * Math.PI / 180;
        double north = (Kneeboard.NotedSpeed(100) * Math.Cos(course) + windNorth) * 60;
        double east = (Kneeboard.NotedSpeed(100) * Math.Sin(course) + windEast) * 60;
        Assert.InRange(dr.North, start.North + north - 1, start.North + north + 1);
        Assert.InRange(dr.East, start.East + east - 1, start.East + east + 1);
    }

    [Fact]
    public void A_turn_is_plotted_late_from_the_pencil_once_the_new_course_settles()
    {
        OnLegOne();
        Record(RadioCommand.RoutePosition, RouteCommands.RoutePosition(_tracker), T0);
        var start = Picture(T0).Player!.Mark;
        var (windNorth, windEast) = Kneeboard.WindError(T0);

        for (int s = 1; s <= 30; s++)
            _kneeboard.Advance(T0.AddSeconds(s), 90, 100);
        for (int s = 31; s <= 50; s++)
            _kneeboard.Advance(T0.AddSeconds(s), 0, 100); // onto north; the pencil keeps 090 until the course has held
        var picture = Picture(T0.AddSeconds(50));
        var dr = picture.Player!.Mark;

        // Eight more seconds on the old leg, then twelve on 360. The corner is where he noted the turn.
        const int eastSeconds = 38, northSeconds = 12;
        Assert.InRange(dr.East, start.East + 100 * eastSeconds + windEast * 50 - 1, start.East + 100 * eastSeconds + windEast * 50 + 1);
        Assert.InRange(dr.North, start.North + 100 * northSeconds + windNorth * 50 - 1, start.North + 100 * northSeconds + windNorth * 50 + 1);
        var corner = Assert.Single(picture.Pencil);
        Assert.InRange(corner.East, start.East + 100 * eastSeconds + windEast * eastSeconds - 1, start.East + 100 * eastSeconds + windEast * eastSeconds + 1);
        Assert.InRange(corner.North, start.North + windNorth * eastSeconds - 1, start.North + windNorth * eastSeconds + 1);
    }

    [Fact]
    public void A_brief_weave_is_not_a_new_leg()
    {
        OnLegOne();
        Record(RadioCommand.RoutePosition, RouteCommands.RoutePosition(_tracker), T0);
        var start = Picture(T0).Player!.Mark;
        var (windNorth, windEast) = Kneeboard.WindError(T0);

        for (int s = 1; s <= 20; s++)
            _kneeboard.Advance(T0.AddSeconds(s), 90, 100);
        for (int s = 21; s <= 25; s++)
            _kneeboard.Advance(T0.AddSeconds(s), 180, 100); // five seconds off, short of the time a new course must hold
        for (int s = 26; s <= 35; s++)
            _kneeboard.Advance(T0.AddSeconds(s), 90, 100);
        var dr = Picture(T0.AddSeconds(35)).Player!.Mark;

        Assert.InRange(dr.East, start.East + 100 * 35 + windEast * 35 - 1, start.East + 100 * 35 + windEast * 35 + 1);
        Assert.InRange(dr.North, start.North + windNorth * 35 - 1, start.North + windNorth * 35 + 1);
        Assert.Empty(Picture(T0.AddSeconds(35)).Pencil);
    }

    [Fact]
    public void Heading_that_will_not_settle_freezes_the_pencil_and_grows_the_patch_faster()
    {
        OnLegOne();
        Record(RadioCommand.RoutePosition, RouteCommands.RoutePosition(_tracker), T0);

        PlotMark? held = null;
        for (int s = 1; s <= 40; s++)
        {
            double heading = s <= 10 ? 90 : s % 2 == 0 ? 0 : 180;
            _kneeboard.Advance(T0.AddSeconds(s), heading, 100);
            if (s == 30)
                held = Picture(T0.AddSeconds(s)).Player!.Mark; // still plotting; the fight has not yet frozen the pencil
        }
        var frozen = Picture(T0.AddSeconds(40)).Player!.Mark;
        Assert.NotNull(held);

        Assert.InRange(frozen.East, held.East - 1, held.East + 1);
        Assert.InRange(frozen.North, held.North - 1, held.North + 1);
        double extra = Kneeboard.DriftPerMinute * 10 / 60.0 + Kneeboard.ManeuverDriftPerMinute * 10 / 60.0;
        Assert.InRange(frozen.Size - held.Size, extra - 1, extra + 1);
        Assert.Empty(Picture(T0.AddSeconds(40)).Pencil);
    }

    [Fact]
    public void Dead_reckoning_is_dropped_once_its_patch_is_no_use()
    {
        OnLegOne();
        Record(RadioCommand.RoutePosition, RouteCommands.RoutePosition(_tracker), T0);
        for (int s = 1; s <= 10 * 60; s++)
            _kneeboard.Advance(T0.AddSeconds(s), 90, 110);
        Assert.NotNull(Picture(T0.AddMinutes(10)).Player); // ten minutes on, it is wide but still of use

        var later = T0.AddMinutes(30);
        for (int s = 10 * 60 + 1; s <= 30 * 60; s++)
            _kneeboard.Advance(T0.AddSeconds(s), 90, 110);
        Assert.Null(Picture(later).Player);
        Assert.Contains(Picture(later).Marks, m => m.Mark.Subject == PlotSubject.Player); // the fix itself stays, faded
    }

    [Fact]
    public void A_contact_called_on_the_players_clock_is_marked_off_his_place_that_way()
    {
        OnLegOne(); // flying east
        Record(RadioCommand.RoutePosition, RouteCommands.RoutePosition(_tracker), T0);
        var you = Picture(T0).Player!.Mark;
        // "A pair of Hurricanes, at your twelve o'clock, a few kilometres, heading west."
        var reply = new RadioReply(Who(Leader), [Who(Enemy)], new RelativePosition(90, 5_000, 0), 0, "Number one, ...",
            [new(SpeechWord.Speaker, 1), new(SpeechWord.ContactCount, 2), new(SpeechWord.ContactType, 2, "Hurricane"),
             new(SpeechWord.Clock, 12), new(SpeechWord.RangeBand, 1), new(SpeechWord.Vertical, 0), new(SpeechWord.Heading, 270)]);
        _kneeboard.Record(RadioCommand.EnemyContacts, reply, 90, T0.AddSeconds(5));

        var contact = Assert.Single(Picture(T0.AddSeconds(5)).Marks, m => m.Mark.Subject == PlotSubject.Contact).Mark;
        Assert.Equal("2 Hurricanes", contact.Label);
        Assert.Equal(270, contact.Course);
        Assert.InRange(contact.East - you.East, 4_500, 5_500); // twelve o'clock on an easterly heading: east, 2–8 km
        Assert.InRange(Math.Abs(contact.North - you.North), 0, 200);
        Assert.True(contact.Size > you.Size + 3_000, "the patch is as wide as \"a few kilometres\" and his own uncertainty");
    }

    [Fact]
    public void A_contact_is_not_drawn_while_the_player_has_no_place_on_the_sheet()
    {
        OnLegOne();
        var reply = new RadioReply(Who(Leader), [Who(Enemy)], new RelativePosition(90, 5_000, 0), 0, "Number one, ...",
            [new(SpeechWord.ContactCount, 1), new(SpeechWord.Clock, 12), new(SpeechWord.RangeBand, 1)]);
        _kneeboard.Record(RadioCommand.EnemyContacts, reply, 90, T0);

        Assert.Empty(Picture(T0).Marks);
    }

    [Fact]
    public void A_contact_on_a_mate_is_marked_at_his_last_report_and_a_newer_call_replaces_it()
    {
        // The mate cannot see the player and names his square: that is where he is marked.
        var square = new RadioReply(Who(Mate), [Who(Mate)], new RelativePosition(0, 9_000, 0), 0, "Number three, I cannot see you. ...",
            [new(SpeechWord.Speaker, 3), new(SpeechWord.CannotSee), new(SpeechWord.Grid, 1120, "5")]);
        _kneeboard.Record(RadioCommand.FlightPosition, square, 90, T0);
        var onMe = new RadioReply(Who(Mate), [Who(Enemy)], new RelativePosition(0, 9_000, 0), 0, "Number three, enemy fighter, on me.",
            [new(SpeechWord.ContactCount, 1), new(SpeechWord.ContactClass, 1), new(SpeechWord.ContactSide, -1), new(SpeechWord.OnMe)]) { FromSpeaker = true };
        _kneeboard.Record(RadioCommand.EnemyContacts, onMe, 90, T0.AddSeconds(10));
        _kneeboard.Record(null, onMe, 90, T0.AddSeconds(40));

        var picture = Picture(T0.AddSeconds(40));
        var mate = Assert.Single(picture.Marks, m => m.Mark.Subject == PlotSubject.Mate).Mark;
        var contact = Assert.Single(picture.Marks, m => m.Mark.Subject == PlotSubject.Contact);
        Assert.Equal("3", mate.Label);
        Assert.Equal(mate.Spread.North, contact.Mark.North, 1);
        Assert.Equal(mate.Spread.East, contact.Mark.East, 1);
        Assert.Equal(-1, contact.Mark.Side);
        Assert.Equal("fighter", contact.Mark.Label);
        Assert.Equal("now", contact.AgeText);
    }

    [Fact]
    public void A_mate_in_formation_is_noted_with_the_player_and_marks_fade_then_go()
    {
        OnLegOne();
        Record(RadioCommand.RoutePosition, RouteCommands.RoutePosition(_tracker), T0);
        var formation = new RadioReply(Who(Leader), [Who(Leader)], new RelativePosition(90, 300, 0), 0, "Number one, you are in formation with us.",
            [new(SpeechWord.Speaker, 1), new(SpeechWord.InFormation), new(SpeechWord.Beside, 90)]);
        _kneeboard.Record(RadioCommand.FlightPosition, formation, 90, T0);

        Assert.Equal(["1"], Picture(T0.AddSeconds(30)).WithPlayer);
        var fix = Picture(T0.AddMinutes(5)).Marks.Single(m => m.Mark.Subject == PlotSubject.Player);
        Assert.InRange(fix.Alpha, Kneeboard.FadedAlpha, 0.99);
        Assert.Equal("5 min", fix.AgeText);
        Assert.Empty(Picture(T0.AddMinutes(10)).WithPlayer);
        Assert.Empty(Picture(T0.AddMinutes(31)).Marks);
    }

    [Fact]
    public void A_mate_who_has_the_player_with_him_places_him_where_the_mate_last_reported()
    {
        var square = new RadioReply(Who(Leader), [Who(Leader)], new RelativePosition(0, 9_000, 0), 0, "Number one, I cannot see you. ...",
            [new(SpeechWord.Speaker, 1), new(SpeechWord.CannotSee), new(SpeechWord.Grid, 1120, "5")]);
        _kneeboard.Record(RadioCommand.FlightPosition, square, 90, T0);
        var withUs = new RadioReply(Who(Leader), [Who(Leader)], new RelativePosition(90, 300, 0), 0, "Number one, you are with us.",
            [new(SpeechWord.Speaker, 1), new(SpeechWord.WithUs)]);
        _kneeboard.Record(RadioCommand.Rejoin, withUs, 90, T0.AddSeconds(30));

        var you = Picture(T0.AddSeconds(30)).Marks.Single(m => m.Mark.Subject == PlotSubject.Player).Mark;
        var (south, west, size) = MapGrid.Bounds(new GridSquare(11, 20, 5));
        Assert.Equal(south + size / 2, you.North, 1);
        Assert.Equal(west + size / 2, you.East, 1);
    }

    [Fact]
    public void A_place_off_the_sheet_is_named_by_its_edge_and_mates_off_it_are_not_drawn()
    {
        var far = new RadioReply(Who(Mate), [Who(Mate)], new RelativePosition(0, 9_000, 0), 0, "Number three, I cannot see you. ...",
            [new(SpeechWord.Speaker, 3), new(SpeechWord.CannotSee), new(SpeechWord.Grid, 0136, "9")]);
        _kneeboard.Record(RadioCommand.FlightPosition, far, 90, T0);
        Assert.Empty(Picture(T0).Marks);

        var formationFar = new RadioReply(Who(Mate), [Who(Mate)], new RelativePosition(90, 300, 0), 0, "Number three, you are with us.",
            [new(SpeechWord.Speaker, 3), new(SpeechWord.WithUs)]);
        _kneeboard.Record(RadioCommand.Rejoin, formationFar, 90, T0.AddSeconds(5));
        Assert.Equal("north-east", Picture(T0.AddSeconds(5)).OffSheet);
    }

    [Fact]
    public void A_new_sortie_clears_the_sheet()
    {
        OnLegOne();
        Record(RadioCommand.RoutePosition, RouteCommands.RoutePosition(_tracker), T0);
        _kneeboard.Reset(_tracker.Plan);

        var picture = Picture(T0);
        Assert.Empty(picture.Marks);
        Assert.Null(picture.Player);
        Assert.Empty(picture.Footer);
    }

    [Fact]
    public void A_landmark_fix_low_over_a_field_gives_its_ninth_and_high_up_the_whole_square()
    {
        Read(10, At(Player, 119_800, 157_500, height: 800)); // over Tusov
        var low = Assert.Single(FixCommand.FixPosition(_tracker).Replies);
        Assert.Equal("Fix, by Tusov airfield, grid 1216-9.", low.Text);
        Assert.Equal(FixCommand.Label, low.SpeakerLabel);

        _kneeboard.Record(RadioCommand.FixPosition, low, 90, T0, "12:00");
        var fix = Picture(T0).Marks.Single(m => m.Mark.Subject == PlotSubject.Player).Mark;
        Assert.Equal(MapGrid.SquareMetres / 3, fix.Size, 1);
        Assert.Equal("12:00, by Tusov airfield", fix.Source);

        Read(20, At(Player, 119_800, 157_500, height: 4_000));
        Assert.Equal("Fix, by Tusov airfield, grid 1216.", Assert.Single(FixCommand.FixPosition(_tracker).Replies).Text);
    }

    [Fact]
    public void Away_from_any_field_or_with_an_enemy_on_him_there_is_no_fix()
    {
        Read(10, At(Player, 125_500, 175_000));
        Assert.Equal("Fix, can't place myself: nothing I recognise in sight.", Assert.Single(FixCommand.FixPosition(_tracker).Replies).Text);

        Read(20, At(Player, 119_800, 157_500, height: 800), At(Enemy, 119_800, 158_500, height: 900));
        var refused = FixCommand.FixPosition(_tracker);
        Assert.Equal("Fix, can't fix position, enemy on me.", Assert.Single(refused.Replies).Text);
        Assert.Contains(refused.Suppressed, s => s.Contact.LogId == Enemy);
    }

    [Fact]
    public void A_fix_from_the_track_recording_is_not_on_the_briefing_map()
    {
        var acmi = new Il2Radio.Core.Acmi.AcmiStream();
        acmi.Feed(Acmi.Header + "#10\n" + Acmi.Object("p", 0, 0, 800, PlayerName, "Axis", heading: 90));
        acmi.Feed("#11\n");
        _tracker.Apply(acmi);

        Assert.Equal(ReplyOutcome.NoCourse, FixCommand.FixPosition(_tracker).Outcome);
    }

    [Fact]
    public void A_fix_is_a_look_not_a_call_and_is_in_the_menu_with_the_views()
    {
        Assert.False(RadioCommands.IsTransmission(RadioCommand.FixPosition));
        Assert.True(RadioCommands.IsTransmission(RadioCommand.RoutePosition));

        var menu = new RadioMenu(RadioMenu.Default);
        menu.MenuKey(T0);
        menu.Choose(5, T0);
        Assert.Equal("Radio › Kneeboard", menu.View!.Title);
        Assert.Equal(RadioCommand.FixPosition, menu.Choose(1, T0).Command);
        menu.MenuKey(T0);
        menu.Choose(5, T0);
        Assert.Equal(RadioMenuAction.KneeboardLarge, menu.Choose(3, T0).Action);
    }

    [Theory]
    [InlineData("top-left", 22, 22)]
    [InlineData("top-right", 1920 - 22 - 400, 22)]
    [InlineData("bottom-right", 1920 - 22 - 400, 1080 - 22 - 300)]
    [InlineData("centre-right", 1920 - 22 - 400, 390)]
    [InlineData("centre-left", 22, 390)]
    public void A_preset_pins_its_corner_a_margin_in(string preset, int x, int y) =>
        Assert.Equal((x, y), KneeboardPlacement.Parse(preset)!.Place((1920, 1080), (400, 300)));

    [Fact]
    public void A_custom_place_scales_with_the_resolution_and_stays_on_screen()
    {
        var custom = KneeboardPlacement.Parse("custom", [960, 540], [1920, 1080])!;

        Assert.Equal((1280, 720), custom.Place((2560, 1440), (100, 100)));
        Assert.Equal((1920 - 1100, 0), new KneeboardPlacement(KneeboardAnchor.Custom, 5000, -40).Place((1920, 1080), (1100, 600)));
    }

    [Fact]
    public void The_view_cycles_hidden_small_large()
    {
        Assert.Equal(KneeboardView.Small, KneeboardPlacement.Next(KneeboardView.Hidden));
        Assert.Equal(KneeboardView.Large, KneeboardPlacement.Next(KneeboardView.Small));
        Assert.Equal(KneeboardView.Hidden, KneeboardPlacement.Next(KneeboardView.Large));
        Assert.Null(KneeboardPlacement.ParseView("2"));
    }

    [Fact]
    public void A_course_stays_centred_on_the_left_of_its_leg_when_the_paper_there_is_free()
    {
        var east = SheetNotes.Course(0, 0, 200, 0, 40, 10, 3, []);
        Assert.InRange(east.OriginX, 99.9f, 100.1f);
        Assert.InRange(east.OriginY, -0.1f, 0.1f);
        Assert.InRange(east.AngleDegrees, -0.1f, 0.1f);
        Assert.InRange(east.LocalX, -20.1f, -19.9f);
        Assert.InRange(east.LocalY, -13.1f, -12.9f);

        // A leg flown west is turned upright, and the words stay on the left of that way, which is below the line.
        var west = SheetNotes.Course(200, 0, 0, 0, 40, 10, 3, []);
        Assert.InRange(west.AngleDegrees, -0.1f, 0.1f);
        Assert.InRange(west.LocalY, 2.9f, 3.1f);
    }

    [Fact]
    public void A_short_leg_home_does_not_write_its_course_through_the_field_name()
    {
        // The escort's last leg: about 21 km, mostly west, with the words longer than the clear paper at Tusov.
        var ring = SheetNotes.Ink.Upright(-8, -8, 16, 16);
        var name = SheetNotes.Ink.Upright(11, -11, 56, 18);
        var place = SheetNotes.Course(84, -14, 0, 0, 120, 16, 3, [ring, name]);

        Assert.False(place.Box.Hits(ring, 6));
        Assert.False(place.Box.Hits(name, 6));
    }

    [Fact]
    public void The_field_name_moves_when_its_usual_place_is_already_written_on()
    {
        var taken = SheetNotes.Ink.Upright(11, -11, 56, 18);
        var name = SheetNotes.Beside(11, -11, 56, 18, 0, 0, 8, 6, [taken]);

        Assert.False(name.Box.Hits(taken, 6));
        Assert.True(MathF.Abs(name.X - 11) > 1 || MathF.Abs(name.Y + 11) > 1);
    }
}
