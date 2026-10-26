using System.Globalization;
using Il2Radio.Core.Acmi;
using Il2Radio.Core.Audio;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>
/// Calls the flight makes on its own: a cruise heading, and one line when the contact picture changes.
/// </summary>
public class UnaskedCallTests
{
    private const string PlayerName = "Gert Knoll";
    private const string LeaderName = "Werner Hoffmann";
    private readonly ManualTimeProvider _clock = new();
    private ContactTracker _tracker = null!;
    private AcmiStream _acmi = null!;
    private readonly UnaskedCalls _calls = new();
    private readonly SightingMemory _memory = new();
    private int _nextId = 1000;
    private long _leaderPlane;

    private static readonly RadioSettings See = new()
    {
        Realism = RadioRealism.Pilot,
        Visibility = new VisibilitySettings { DetectMetresPerSpanMetre = 100_000, MaxDetectFactor = 100, MisidentificationChance = 0 },
    };

    private const string Overcast = """
        CloudLevel = 1500;
        CloudHeight = 800;
        CloudConfig = "summer\04_Overcast_01\sky.ini";
        """;

    private const string GridHeader =
        "FileType=text/acmi/tacview\nFileVersion=2.2\n0,ReferenceLongitude=0,ReferenceLatitude=0\n";

    [Fact]
    public void An_AI_leader_calls_the_next_heading_once_as_the_corner_approaches()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1);
        Frame(10, 100_000, 205_000, 90);
        Assert.Null(Say());

        Frame(11, 100_000, 208_000, 90);
        var call = Say();

        Assert.Equal("Number one, turning to heading 180.", call!.Text);
        Assert.Null(call.SpeakerLabel);
        Assert.Contains(call.Tokens!, t => t.Word == SpeechWord.Course && t.Value == 180);
        Assert.DoesNotContain(call.Tokens!, t => t.Word == SpeechWord.Heading);
        // The headset: which way he turns and the compass point; the clips cannot say 180.
        Assert.Contains(new SpeechToken(SpeechWord.Turn, 1), call.Tokens!);
        Assert.Equal(["number.1", "turn.right", "compass.s"],
            ClipPhraser.Phrase(call.Tokens!, key => key is "number.1" or "turn.right" or "turn.left" or "compass.s"));
        Assert.Null(Say());
    }

    [Fact]
    public void An_AI_leader_calls_the_outbound_leg_of_a_patrol_corner()
    {
        PatrolLoop();
        Frame(10, 100_000, 208_000, 90);

        Assert.Equal("Number one, turning to heading 360.", Say()!.Text);
        Assert.Null(Say());
    }

    [Fact]
    public void A_patrol_corner_on_the_nose_is_called_when_the_leg_runs_the_other_way()
    {
        PatrolLoop();
        Frame(10, 100_500, 203_000, 270);

        Assert.Equal("Number one, turning to heading 090.", Say()!.Text);
    }

    [Fact]
    public void The_corner_that_closes_a_patrol_loop_is_called_on_the_way_in()
    {
        PatrolLoop();
        Frame(10, 102_000, 200_000, 230);

        Assert.Equal("Number one, turning to heading 090.", Say()!.Text);
    }

    [Fact]
    public void A_patrol_corner_off_the_nose_stays_quiet()
    {
        PatrolLoop();
        Frame(10, 100_500, 203_000, 220);

        Assert.Null(Say());
    }

    [Fact]
    public void A_turn_that_stays_inside_five_degrees_stays_quiet()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1, outboundDegrees: 92);
        Frame(10, 100_000, 208_000, 90);

        Assert.Null(Say());
    }

    [Fact]
    public void A_turn_inside_one_compass_point_is_still_called()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1, outboundDegrees: 100);
        Frame(10, 100_000, 208_000, 90);

        var call = Say();

        Assert.Equal("Number one, turning to heading 100.", call!.Text);
        Assert.Contains(call.Tokens!, t => t.Word == SpeechWord.Course && t.Value == 100);
    }

    [Fact]
    public void When_the_player_leads_the_corner_is_a_route_line()
    {
        RouteMission(playerFormation: 1, leaderFormation: 2);
        Frame(10, 100_000, 208_000, 90);

        var call = Say();

        Assert.Equal("Route, turning to heading 180.", call!.Text);
        Assert.Equal("Route", call.SpeakerLabel);
        Assert.Equal("[Route]", RadioChat.Line(call).Speaker);
        Assert.DoesNotContain(call.Tokens!, t => t.Word is SpeechWord.Callsign or SpeechWord.Speaker);
        Assert.True(call.Contact.IsPlayer); // the session keeps the player's own line off his headset
    }

    [Fact]
    public void A_destroyed_leader_does_not_make_the_call()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1);
        Frame(10, 100_000, 208_000, 90);
        _tracker.Apply(new KillEvent(1, 999, _leaderPlane, WorldPoint.FromIl2(0, 500, 0)));

        var call = Say();

        Assert.Equal("Route, turning to heading 180.", call!.Text);
        Assert.Equal(PlayerName, call.Contact.PilotName);
    }

    [Fact]
    public void The_players_own_heading_change_is_not_announced()
    {
        OpenFlight(playerFormation: 1, leaderFormation: 2);
        Track(0, playerHeading: 0, leaderHeading: 0);
        Track(6, playerHeading: 0, leaderHeading: 0);
        Track(20, playerHeading: 80, leaderHeading: 0);
        Track(26, playerHeading: 85, leaderHeading: 0);

        Assert.Null(Say());
    }

    [Fact]
    public void An_AI_leader_off_the_route_calls_a_settled_gentle_turn_and_not_a_break()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1);
        Track(0, leaderHeading: 0);
        Track(6, leaderHeading: 0);
        Assert.Null(Say());

        Track(8, leaderHeading: 180);
        Track(20, leaderHeading: 180);
        Assert.Null(Say());

        Track(40, leaderHeading: 270);
        Assert.Null(Say());
        Track(46, leaderHeading: 272);

        var settled = Say();
        Assert.Equal("Number one, heading 270.", settled!.Text);
        Assert.DoesNotContain("turning", settled.Text);
        Assert.Null(Say());
    }

    [Fact]
    public void A_leader_beyond_radio_range_is_not_heard()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1);
        Track(0, leaderNorth: 50_000, leaderHeading: 0);
        Track(10, leaderNorth: 50_000, leaderHeading: 0);
        Track(30, leaderNorth: 50_000, leaderHeading: 90);
        Track(40, leaderNorth: 50_000, leaderHeading: 90);

        Assert.Null(Say());
    }

    [Fact]
    public void A_stale_track_and_a_log_snapshot_do_not_produce_a_call()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1);
        Frame(10, 100_000, 208_000, 90);
        Assert.NotNull(Say());

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(Say());

        OpenFlight(playerFormation: 2, leaderFormation: 1);
        _tracker.Apply(new TakeOffEvent(50, _leaderPlane, WorldPoint.FromIl2(100_000, 1_000, 208_000)));
        Assert.Null(new UnaskedCalls().Consider(_tracker, See, new SightingMemory()));
    }

    [Fact]
    public void A_new_mission_can_call_the_same_corner_again()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1);
        Frame(10, 100_000, 208_000, 90);
        Assert.Equal("Number one, turning to heading 180.", Say()!.Text);

        _tracker.Apply(new MissionStarted(1, null, "next"));
        LogFlight(playerFormation: 2, leaderFormation: 1);
        _acmi = new AcmiStream();
        _acmi.Feed(GridHeader + "#0\n");
        Frame(10, 100_000, 208_000, 90);

        Assert.Equal("Number one, turning to heading 180.", Say()!.Text);
    }

    [Fact]
    public void Each_unasked_call_can_be_switched_off()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1);
        Frame(10, 100_000, 208_000, 90);
        var off = new UnaskedCallSettings { Route = false, Contacts = true };

        Assert.Null(new UnaskedCalls().Consider(_tracker, See, _memory, off));
        Assert.Equal("Number one, turning to heading 180.", new UnaskedCalls().Consider(_tracker, See, _memory, new UnaskedCallSettings { Route = true })!.Text);

        OpenFlight(playerFormation: 2, leaderFormation: 1, grid: false);
        SeeEnemy(10, 0, 600);
        Assert.Null(new UnaskedCalls().Consider(_tracker, See, new SightingMemory(), new UnaskedCallSettings { Contacts = false }));
        Assert.Contains("o'clock", new UnaskedCalls().Consider(_tracker, See, new SightingMemory(), new UnaskedCallSettings { Route = false, Contacts = true })!.Text);
    }

    [Fact]
    public void A_contact_is_called_once_and_a_clock_change_is_not_a_new_picture()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1, grid: false);
        SeeEnemy(10, 0, 600);
        var first = Say();
        Assert.Contains("o'clock", first!.Text);

        SeeEnemy(12, 2_000, 0);
        Assert.Null(Say());
    }

    [Fact]
    public void A_newly_gained_contact_is_spotted_and_a_change_to_the_same_aircraft_is_not()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1, grid: false, overcast: true);
        _acmi.Feed("#10\n" + Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0)
            + Acmi.Object("w", 8_000, 0, 3000, LeaderName, "Axis", heading: 0)
            + Acmi.Object("e", 8_000, 200, 3000, "Pilot e", "Allies", "Yak-1 ser.69", heading: 90));
        _tracker.Apply(_acmi);
        var gained = Say()!;
        Assert.Equal(SpeechWord.Spotted, gained.Tokens![1].Word); // after "number one"
        Assert.DoesNotContain("spotted", gained.Text, StringComparison.OrdinalIgnoreCase);

        _acmi.Feed("#30\n" + Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0)
            + Acmi.Object("w", 8_000, 0, 3000, LeaderName, "Axis", heading: 0)
            + Acmi.Object("e", 8_000, 5_000, 3000, "Pilot e", "Allies", "Yak-1 ser.69", heading: 90));
        _tracker.Apply(_acmi);
        var letGo = Say()!; // the same Yak, no longer on him
        Assert.DoesNotContain(letGo.Tokens!, t => t.Word == SpeechWord.Spotted);
    }

    [Fact]
    public void An_aircraft_flickering_in_and_out_of_sight_is_called_once_a_minute()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1, grid: false);
        SeeEnemy(10, 0, 600);
        Assert.NotNull(Say());

        LoseEnemy(20);
        Assert.Null(Say()); // gone from the track: nothing to call

        SeeEnemy(40, 0, 600);
        Assert.Null(Say()); // back in sight within the minute

        LoseEnemy(50);
        Assert.Null(Say());
        SeeEnemy(80, 0, 600);
        Assert.Contains("o'clock", Say()!.Text); // a minute on, it is a contact again
    }

    [Fact]
    public void A_second_picture_call_waits_for_the_pace_and_is_not_lost()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1, grid: false);
        SeeEnemy(10, 0, 600);
        Assert.NotNull(Say());

        SeeEnemy(15, 0, 600);
        _acmi.Feed(Acmi.Object("f", 0, -900, 1000, "Pilot f", "Allies", "Yak-1 ser.69", heading: 90));
        _tracker.Apply(_acmi);
        Assert.Null(Say()); // five seconds after the last call

        SeeEnemy(26, 0, 600);
        _acmi.Feed(Acmi.Object("f", 0, -900, 1000, "Pilot f", "Allies", "Yak-1 ser.69", heading: 90));
        _tracker.Apply(_acmi);
        Assert.NotNull(Say());
    }

    [Fact]
    public void On_me_is_a_picture_change_and_a_fight_holds_the_route_call()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1, grid: false, overcast: true);
        _acmi.Feed("#10\n" + Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0)
            + Acmi.Object("w", 8_000, 0, 3000, LeaderName, "Axis", heading: 0)
            + Acmi.Object("e", 8_000, 200, 3000, "Pilot e", "Allies", "Yak-1 ser.69", heading: 90));
        _tracker.Apply(_acmi);

        Assert.Contains("on me", Say()!.Text);

        _acmi.Feed("#30\n" + Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0)
            + Acmi.Object("w", 8_000, 0, 3000, LeaderName, "Axis", heading: 0)
            + Acmi.Object("e", 8_000, 5_000, 3000, "Pilot e", "Allies", "Yak-1 ser.69", heading: 90));
        _tracker.Apply(_acmi);

        var moved = Say(); // he let go: news, once the picture calls' pace allows it
        Assert.NotNull(moved);
        Assert.DoesNotContain("on me", moved!.Text);
        Assert.Contains("o'clock", moved.Text);
    }

    [Fact]
    public void A_contact_in_sight_holds_the_heading_call_until_the_picture_is_gone()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1);
        Frame(10, 100_000, 208_000, 90, Enemy(208_600, 100_000));

        var contact = Say();
        Assert.DoesNotContain("heading", contact!.Text);
        Assert.Null(Say());

        // Removal takes effect on the next frame. Sure spotting would still see him anywhere on the map.
        _acmi.Feed("#80\n-e\n#81\n");
        Frame(81, 100_000, 208_000, 90);

        Assert.Equal("Number one, turning to heading 180.", Say()!.Text);
    }

    private RadioReply? Say(UnaskedCallSettings? calls = null) =>
        _calls.Consider(_tracker, See, _memory, calls);

    private void RouteMission(int playerFormation, int leaderFormation, double outboundDegrees = 180)
    {
        double endNorth = 100_000 + 10_000 * Math.Cos(outboundDegrees * Math.PI / 180);
        double endEast = 210_000 + 10_000 * Math.Sin(outboundDegrees * Math.PI / 180);
        var mission = $$"""
            Options
            {
              GuiMap = "stalingrad-summer-1942";
              Time = 12:0:0;
              Countries
              {
                101 : 1;
                201 : 2;
              }
            }
            Plane
            {
              Name = "{{PlayerName}}";
              Index = 1;
              Country = 201;
              AILevel = 0;
              Callsign = 8;
              Callnum = 2;
            }
            Plane
            {
              Name = "{{LeaderName}}";
              Index = 10;
              Country = 201;
              Callsign = 8;
              Callnum = 1;
            }
            MCU_TR_Entity
            {
              Index = 21;
              MisObjID = 10;
            }
            MCU_Waypoint
            {
              Index = 30;
              Targets = [31];
              Objects = [21];
              XPos = 100000;
              YPos = 1000;
              ZPos = 200000;
            }
            MCU_Waypoint
            {
              Index = 31;
              Targets = [32];
              Objects = [21];
              XPos = 100000;
              YPos = 1000;
              ZPos = 210000;
            }
            MCU_Waypoint
            {
              Index = 32;
              Targets = [];
              Objects = [21];
              XPos = {{endNorth.ToString(CultureInfo.InvariantCulture)}};
              YPos = 1000;
              ZPos = {{endEast.ToString(CultureInfo.InvariantCulture)}};
            }
            """;
        _tracker = new ContactTracker(_clock);
        _tracker.SetPlan(MissionPlan.FromBlocks(MissionFileParser.Parse(new StringReader(mission))));
        LogFlight(playerFormation, leaderFormation);
        _acmi = new AcmiStream();
        _acmi.Feed(GridHeader + "#0\n");
    }

    /// <summary>
    /// A 10 km square, A east to B, B north to C, C west to D, D south back to A.
    /// A is 100000 N 200000 E. The repeated point is the corner whose outbound is east.
    /// </summary>
    private void PatrolLoop()
    {
        const string mission = """
            Options
            {
              GuiMap = "stalingrad-summer-1942";
              Time = 12:0:0;
              Countries
              {
                101 : 1;
                201 : 2;
              }
            }
            Plane
            {
              Name = "Gert Knoll";
              Index = 1;
              Country = 201;
              AILevel = 0;
              Callsign = 8;
              Callnum = 2;
            }
            Plane
            {
              Name = "Werner Hoffmann";
              Index = 10;
              Country = 201;
              Callsign = 8;
              Callnum = 1;
            }
            MCU_TR_Entity
            {
              Index = 21;
              MisObjID = 10;
            }
            MCU_Waypoint
            {
              Index = 40;
              Targets = [41];
              Objects = [21];
              XPos = 100000;
              YPos = 1000;
              ZPos = 200000;
            }
            MCU_Waypoint
            {
              Index = 41;
              Targets = [42];
              Objects = [21];
              XPos = 100000;
              YPos = 1000;
              ZPos = 210000;
            }
            MCU_Waypoint
            {
              Index = 42;
              Targets = [43];
              Objects = [21];
              XPos = 110000;
              YPos = 1000;
              ZPos = 210000;
            }
            MCU_Waypoint
            {
              Index = 43;
              Targets = [40];
              Objects = [21];
              XPos = 110000;
              YPos = 1000;
              ZPos = 200000;
            }
            """;
        _tracker = new ContactTracker(_clock);
        _tracker.SetPlan(MissionPlan.FromBlocks(MissionFileParser.Parse(new StringReader(mission))));
        LogFlight(playerFormation: 2, leaderFormation: 1);
        _acmi = new AcmiStream();
        _acmi.Feed(GridHeader + "#0\n");
    }

    private void OpenFlight(int playerFormation, int leaderFormation, bool grid = true, bool overcast = false)
    {
        string map = grid ? "stalingrad-summer-1942" : "stalingrad-summer-1942";
        string clouds = overcast ? Overcast : "";
        _tracker = new ContactTracker(_clock);
        _tracker.SetPlan(MissionPlan.FromBlocks(MissionFileParser.Parse(new StringReader($$"""
            Options
            {
              GuiMap = "{{map}}";
              Time = 12:0:0;
              {{clouds}}
              Countries
              {
                101 : 1;
                201 : 2;
              }
            }
            Plane
            {
              Name = "{{PlayerName}}";
              Index = 1;
              Country = 201;
              AILevel = 0;
            }
            Plane
            {
              Name = "{{LeaderName}}";
              Index = 10;
              Country = 201;
            }
            """))));
        LogFlight(playerFormation, leaderFormation);
        _acmi = new AcmiStream();
        _acmi.Feed((grid ? GridHeader : Acmi.Header) + "#0\n");
    }

    private void LogFlight(int playerFormation, int leaderFormation)
    {
        _leaderPlane = Flight(LeaderName, leaderFormation);
        Flight(PlayerName, playerFormation, isPlayer: true);
    }

    private long Flight(string pilot, int formation, bool isPlayer = false)
    {
        long plane = _nextId++;
        long bot = _nextId++;
        var origin = WorldPoint.FromIl2(0, 50, 0);
        _tracker.Apply(new ObjectSpawnedEvent(0, plane, "Bf 109 F-4", 201, pilot, -1, origin));
        _tracker.Apply(new ObjectSpawnedEvent(0, bot, "BotPilot_Bf109", 201, "BotPilot_Bf109", plane, origin));
        _tracker.Apply(new PlaneSpawnedEvent(0, plane, bot, origin, "Bf 109 F-4", 201, formation, 1, 0, -1, isPlayer, ""));
        return plane;
    }

    /// <summary>Both aircraft on a Stalingrad map point, noses on <paramref name="heading"/>.</summary>
    private void Frame(double time, double north, double east, double heading, string? extra = null)
    {
        _acmi.Feed($"#{time.ToString(CultureInfo.InvariantCulture)}\n"
            + Grid(PlayerName, "p", north, east, heading)
            + Grid(LeaderName, "w", north, east, heading)
            + extra);
        _tracker.Apply(_acmi);
    }

    private void Track(double time, double leaderHeading, double playerHeading = 0, double leaderNorth = 0)
    {
        _acmi.Feed($"#{time.ToString(CultureInfo.InvariantCulture)}\n"
            + Grid(PlayerName, "p", 100_000, 200_000, playerHeading)
            + Grid(LeaderName, "w", 100_000 + leaderNorth, 200_000, leaderHeading));
        _tracker.Apply(_acmi);
    }

    private void SeeEnemy(double time, double north, double east)
    {
        _acmi.Feed($"#{time.ToString(CultureInfo.InvariantCulture)}\n"
            + Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0)
            + Acmi.Object("w", 0, 100, 1000, LeaderName, "Axis", heading: 0)
            + Acmi.Object("e", north, east, 1000, "Pilot e", "Allies", "Yak-1 ser.69", heading: 90));
        _tracker.Apply(_acmi);
    }

    /// <summary>The enemy leaves the track (removal takes effect on the next frame) while the flight keeps flying.</summary>
    private void LoseEnemy(double time)
    {
        _acmi.Feed($"#{time.ToString(CultureInfo.InvariantCulture)}\n-e\n#{(time + 1).ToString(CultureInfo.InvariantCulture)}\n"
            + Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0)
            + Acmi.Object("w", 0, 100, 1000, LeaderName, "Axis", heading: 0));
        _tracker.Apply(_acmi);
    }

    private static string Enemy(double east, double north) =>
        Grid("Pilot e", "e", north, east, 90, "Allies", "Yak-1 ser.69");

    private static string Grid(string pilot, string id, double north, double east, double heading, string coalition = "Axis", string name = "Bf 109 F-4")
    {
        var (lat, lon) = MapGrid.StalingradLatLon(north, east);
        return string.Create(CultureInfo.InvariantCulture,
            $"{id},T={lon:F6}|{lat:F6}|1000|0|0|{heading:F1},Type=Air+FixedWing,Name={name},Pilot={pilot},Coalition={coalition}\n");
    }
}
