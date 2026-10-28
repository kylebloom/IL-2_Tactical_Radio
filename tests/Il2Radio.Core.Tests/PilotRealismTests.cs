using Il2Radio.Core.Acmi;
using Il2Radio.Core.Audio;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.Memory;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>
/// <see cref="RadioRealism.Pilot"/>: a speaker reports a position relative to the player only when he can see
/// the player or, for the player's own flight, is in formation. The live-track gate is the same one the
/// precise-mode tests cover; these tests check that pilot mode does not step around it.
/// </summary>
public class PilotRealismTests
{
    private const string PlayerName = "Gert Knoll";
    private readonly ManualTimeProvider _clock = new();
    private ContactTracker _tracker = null!;
    private AcmiStream _acmi = null!;
    private int _nextId = 1000;

    /// <summary>Detection effectively certain, so "can see" does not depend on the dice. Identification ranges stay normal.</summary>
    private static readonly RadioSettings See = new()
    {
        Realism = RadioRealism.Pilot,
        Visibility = new VisibilitySettings { DetectMetresPerSpanMetre = 100_000, MaxDetectFactor = 100, MisidentificationChance = 0 },
    };

    private static readonly RadioSettings Pilot = new() { Realism = RadioRealism.Pilot };

    /// <summary>Nothing is visible, so only a flight mate inside formation distance can still place the player.</summary>
    private static readonly RadioSettings Blind = new()
    {
        Realism = RadioRealism.Pilot,
        Visibility = new VisibilitySettings { DetectMetresPerSpanMetre = 0.01, MaxDetectFactor = 1 },
    };

    private const string Overcast = """
        CloudLevel = 1500;
        CloudHeight = 800;
        CloudConfig = "summer\04_Overcast_01\sky.ini";
        """;

    public PilotRealismTests() => Start("");

    private void Start(string extraOptions)
    {
        _tracker = new ContactTracker(_clock);
        _tracker.SetPlan(MissionPlan.FromBlocks(MissionFileParser.Parse(new StringReader($$"""
            Options
            {
              Time = 12:0:0;
              {{extraOptions}}
              Countries
              {
                101 : 1;
                201 : 2;
              }
            }
            Plane
            {
              Name = "{{PlayerName}},201033,0";
              Country = 201;
              AILevel = 0;
            }
            """))));
        _acmi = new AcmiStream();
        _acmi.Feed(Acmi.Header + "#0\n");
        _nextId = 1000;
        LogFlightMember(PlayerName, 4, isPlayer: true);
    }

    private void LogFlightMember(string pilot, int formationIndex, bool isPlayer = false)
    {
        long plane = _nextId++, bot = _nextId++;
        var origin = WorldPoint.FromIl2(0, 50, 0);
        _tracker.Apply(new ObjectSpawnedEvent(0, plane, "Bf 109 F-4", 201, pilot, -1, origin));
        _tracker.Apply(new ObjectSpawnedEvent(0, bot, "BotPilot_Bf109_GER41", 201, "BotPilot_Bf109_GER41", plane, origin));
        _tracker.Apply(new PlaneSpawnedEvent(0, plane, bot, origin, "Bf 109 F-4", 201, formationIndex, 1, 0, -1, isPlayer, ""));
    }

    private void Frame(double time, params string[] others) => Frame(time, withHeading: true, others);

    private void Frame(double time, bool withHeading, params string[] others)
    {
        string player = withHeading
            ? Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0)
            : Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis");
        _acmi.Feed($"#{time}\n" + player + string.Concat(others));
        _tracker.Apply(_acmi);
    }

    private static string Mate(string id, string pilot, double north, double east, double alt = 1000, double? heading = 90) =>
        Acmi.Object(id, north, east, alt, pilot, "Axis", heading: heading);

    private static string Bogey(string id, double north, double east, double alt = 1000, string model = "Yak-1 ser.69", double heading = 90) =>
        Acmi.Object(id, north, east, alt, $"Pilot {id}", "Allies", model, heading);

    [Fact]
    public void A_friendly_who_can_see_the_player_gives_a_coarse_clock_position()
    {
        Frame(10, Acmi.Object("f", 0, 4_000, 1000, "Felix Knierim", "Axis"));

        var pilot = Assert.Single(FriendlyPositionsCommand.Execute(_tracker, See).Replies);
        var precise = Assert.Single(FriendlyPositionsCommand.Execute(_tracker).Replies);

        Assert.Equal("Felix Knierim, at your three o'clock, a few kilometres, level with you.", pilot.Text);
        Assert.Contains(pilot.Tokens!, t => t.Word == SpeechWord.RangeBand && t.Value == 1);
        Assert.DoesNotContain(pilot.Tokens!, t => t.Word is SpeechWord.Range or SpeechWord.Bearing or SpeechWord.Altitude);
        Assert.Equal("Felix Knierim, bearing zero-nine-zero, approximately 4 kilometres, altitude 1000 metres.", precise.Text);
    }

    [Fact]
    public void Close_and_far_use_those_words()
    {
        Frame(10, Acmi.Object("f", 0, 800, 1000, "Felix Knierim", "Axis"));
        Assert.Equal("Felix Knierim, at your three o'clock, close, level with you.",
            Assert.Single(FriendlyPositionsCommand.Execute(_tracker, See).Replies).Text);

        Start("");
        Frame(10, Acmi.Object("f", 0, 12_000, 1000, "Felix Knierim", "Axis"));
        var far = Assert.Single(FriendlyPositionsCommand.Execute(_tracker, See).Replies);
        Assert.Equal("Felix Knierim, at your three o'clock, far, level with you.", far.Text);
        Assert.Contains(far.Tokens!, t => t.Word == SpeechWord.RangeBand && t.Value == 2);
    }

    [Fact]
    public void Without_a_player_heading_the_call_is_an_eight_point_compass()
    {
        Frame(10, withHeading: false, Acmi.Object("f", 0, 4_000, 1000, "Felix Knierim", "Axis"));

        var reply = Assert.Single(FriendlyPositionsCommand.Execute(_tracker, See).Replies);

        Assert.Equal("Felix Knierim, to your east, a few kilometres, level with you.", reply.Text);
        Assert.Contains(reply.Tokens!, t => t.Word == SpeechWord.Bearing);
    }

    [Fact]
    public void Another_unit_that_cannot_see_the_player_still_answers_the_call()
    {
        Frame(10, Acmi.Object("f", 0, 20_000, 1000, "Felix Knierim", "Axis", heading: 90));

        var reply = Assert.Single(FriendlyPositionsCommand.Execute(_tracker, Pilot).Replies);

        Assert.Equal("Felix Knierim, I cannot see you. Heading east, altitude 1000 metres.", reply.Text);
        Assert.Contains(reply.Tokens!, t => t.Word == SpeechWord.CannotSee);
        Assert.DoesNotContain(reply.Tokens!, t => t.Word is SpeechWord.Clock or SpeechWord.Bearing or SpeechWord.Range or SpeechWord.RangeBand);
    }

    [Fact]
    public void A_stranger_inside_formation_distance_still_has_to_see_the_player()
    {
        Frame(10, Acmi.Object("f", 0, 400, 1000, "Felix Knierim", "Axis"));

        var reply = Assert.Single(FriendlyPositionsCommand.Execute(_tracker, Blind).Replies);

        Assert.Equal("Felix Knierim, I cannot see you. Altitude 1000 metres.", reply.Text);
        Assert.DoesNotContain(reply.Text, "o'clock", StringComparison.Ordinal);
    }

    [Fact]
    public void The_players_flight_that_cannot_see_him_reports_its_own_heading_and_altitude()
    {
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, Mate("a", "Heinz Koelzig", 0, 20_000, alt: 3000, heading: 90));

        var friendly = Assert.Single(FriendlyPositionsCommand.Execute(_tracker, Pilot).Replies);
        var position = Assert.Single(FlightCommands.FlightPosition(_tracker, Pilot).Replies);
        var rejoin = Assert.Single(FlightCommands.Rejoin(_tracker, Pilot).Replies);

        Assert.Equal("Number one, I cannot see you. Heading east, altitude 3000 metres.", friendly.Text);
        Assert.Equal(friendly.Text, position.Text);
        Assert.Equal(friendly.Text, rejoin.Text);
        Assert.Contains(rejoin.Tokens!, t => t.Word == SpeechWord.CannotSee);
        Assert.Contains(rejoin.Tokens!, t => t.Word == SpeechWord.Heading && t.Value == 90);
        Assert.DoesNotContain(rejoin.Tokens!, t => t.Word is SpeechWord.Steer or SpeechWord.Clock or SpeechWord.Range or SpeechWord.RangeBand);
        Assert.DoesNotContain("steer", rejoin.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flight_mate_in_formation_answers_even_when_the_visibility_model_is_blind()
    {
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, Mate("a", "Heinz Koelzig", 0, 400, alt: 1000, heading: 0));

        Assert.Equal("Number one, at your three o'clock, close, level with you.",
            Assert.Single(FriendlyPositionsCommand.Execute(_tracker, Blind).Replies).Text);
        Assert.Equal("Number one, you are in formation with us.",
            Assert.Single(FlightCommands.FlightPosition(_tracker, Blind).Replies).Text);
        Assert.Equal("Number one, you are with us.",
            Assert.Single(FlightCommands.Rejoin(_tracker, Blind).Replies).Text);
    }

    [Fact]
    public void A_flight_that_can_see_the_player_gives_clock_range_and_on_rejoin_its_own_heading()
    {
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, Mate("a", "Heinz Koelzig", 0, 4_000, alt: 3000, heading: 90));

        Assert.Equal("Number one, I am at your three o'clock, a few kilometres, above you.",
            Assert.Single(FlightCommands.FlightPosition(_tracker, See).Replies).Text);
        var rejoin = Assert.Single(FlightCommands.Rejoin(_tracker, See).Replies);
        Assert.Equal("Number one, at your three o'clock, a few kilometres, above you. Heading east, altitude 3000 metres.", rejoin.Text);
        Assert.Contains(rejoin.Tokens!, t => t.Word == SpeechWord.RangeBand && t.Value == 1);
        Assert.DoesNotContain(rejoin.Tokens!, t => t.Word == SpeechWord.Steer);
    }

    [Fact]
    public void The_main_group_and_a_straggler_who_can_see_the_player_each_answer()
    {
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Rudi Liebgott", 2);
        LogFlightMember("Ernst Lignitz", 3);
        Frame(10,
            Mate("a", "Heinz Koelzig", 0, 4_000, alt: 3000),
            Mate("b", "Rudi Liebgott", 200, 4_200, alt: 3000),
            Mate("c", "Ernst Lignitz", 0, -4_000, alt: 3000));

        var replies = FlightCommands.FlightPosition(_tracker, See).Replies;

        Assert.Equal("Number one, flight of two, at your three o'clock, a few kilometres, above you.", replies[0].Text);
        Assert.Equal("Number three, separated, at your nine o'clock, a few kilometres, above you.", replies[1].Text);
    }

    [Fact]
    public void A_wingman_with_the_player_calls_a_coarse_contact_in_the_players_clock()
    {
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, Mate("w", "Heinz Koelzig", 0, 100, alt: 1000, heading: 0), Bogey("e1", 0, 600));

        Assert.Equal("Number one, single Yak, at your three o'clock, close, level.",
            Assert.Single(EnemyCommands.EnemyContacts(_tracker, See).Replies).Text);
        Assert.Equal("Number one, contact at your three o'clock, close: a Yak, enemy, heading east.",
            Assert.Single(EnemyCommands.Identify(_tracker, See).Replies).Text);
    }

    [Fact]
    public void Four_aircraft_close_enough_to_separate_are_counted_and_a_larger_formation_is_many()
    {
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, [Mate("w", "Heinz Koelzig", 0, 100, alt: 1000, heading: 0), .. Four(600)]);

        Assert.Equal("Number one, four Yaks, at your three o'clock, close, level.", Contacts());

        Start("");
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, [Mate("w", "Heinz Koelzig", 0, 100, alt: 1000, heading: 0), .. Eight(600)]);

        var many = Assert.Single(EnemyCommands.EnemyContacts(_tracker, See).Replies);
        Assert.Equal("Number one, many Yaks, at your three o'clock, close, level.", many.Text);
        Assert.Contains(many.Tokens!, t => t.Word == SpeechWord.ContactAmount && t.Value == 4);
    }

    [Fact]
    public void Aircraft_too_far_to_separate_are_a_few()
    {
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, [Mate("w", "Heinz Koelzig", 0, 100, alt: 1000, heading: 0), .. SixFar()]);

        var reply = Assert.Single(EnemyCommands.EnemyContacts(_tracker, See).Replies);

        Assert.Equal("Number one, a few unidentified aircraft, at your twelve o'clock, a few kilometres, level.", reply.Text);
        Assert.Contains(reply.Tokens!, t => t.Word == SpeechWord.ContactAmount && t.Value == 3);
    }

    [Fact]
    public void A_pilot_fighting_away_from_the_player_says_the_enemy_is_on_him()
    {
        Start(Overcast);
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Ernst Lignitz", 2);
        Frame(10,
            Mate("w", "Heinz Koelzig", 0, 100, alt: 1000, heading: 0),
            Mate("d", "Ernst Lignitz", 8_000, 0, alt: 3000, heading: 0),
            Bogey("e1", 8_000, 200, alt: 3000, heading: 90));

        Assert.Equal("Number two, single Yak, on me.", Contacts(See));
        var identify = Assert.Single(EnemyCommands.Identify(_tracker, See).Replies);
        Assert.Equal("Number two, on me: a Yak, enemy, heading east.", identify.Text);
        Assert.Contains(identify.Tokens!, t => t.Word == SpeechWord.OnMe);
        Assert.DoesNotContain(identify.Tokens!, t => t.Word is SpeechWord.Clock or SpeechWord.Range or SpeechWord.RangeBand);
    }

    [Fact]
    public void Friendly_aircraft_beside_a_pilot_are_with_him_not_on_him()
    {
        Start(Overcast);
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Ernst Lignitz", 2);
        Frame(10,
            Mate("w", "Heinz Koelzig", 0, 100, alt: 1000, heading: 0),
            Mate("d", "Ernst Lignitz", 8_000, 0, alt: 3000, heading: 0),
            Acmi.Object("g", 8_000, 200, 3000, "Josef Weinbergmair", "Axis", "Bf 109 G-2", heading: 90));

        var identify = Assert.Single(EnemyCommands.Identify(_tracker, See).Replies);
        Assert.StartsWith("Number two, with me: ", identify.Text);
        Assert.DoesNotContain(identify.Tokens!, t => t.Word == SpeechWord.OnMe);
    }

    [Fact]
    public void A_pilot_who_cannot_see_the_player_gives_his_own_clock_for_a_distant_contact()
    {
        Start(Overcast);
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Ernst Lignitz", 2);
        Frame(10,
            Mate("w", "Heinz Koelzig", 0, 100, alt: 1000, heading: 0),
            Mate("d", "Ernst Lignitz", 8_000, 0, alt: 3000, heading: 0),
            Bogey("e1", 8_000, 5_000, alt: 3000));

        Assert.Equal("Number two, single unidentified aircraft, at my three o'clock, a few kilometres, level.", Contacts(See));
        Assert.Equal("Number two, contact at my three o'clock, a few kilometres: too far to identify.",
            Assert.Single(EnemyCommands.Identify(_tracker, See).Replies).Text);
    }

    [Fact]
    public void Pilot_mode_still_refuses_what_the_track_does_not_support()
    {
        Assert.Equal(ReplyOutcome.PlayerPositionUnknown, FriendlyPositionsCommand.Execute(new ContactTracker(_clock), Pilot).Outcome);

        Frame(10, Acmi.Object("f", 0, 60_000, 1000, "Felix Knierim", "Axis"));
        Assert.Contains("out of radio range", Assert.Single(FriendlyPositionsCommand.Execute(_tracker, Pilot).Suppressed).Reason);

        Start("");
        var log = new MlgWriter()
            .ObjectSpawn(10, 500, "Bf 109 F-4", 201, "Felix Knierim,201033,0", -1, (0, 50, 0))
            .ObjectSpawn(10, 501, "BotPilot_Bf109_GER41", 201, "BotPilot_Bf109_GER41", 500, (0, 50, 0))
            .Kill(400, 900, 500, (100, 900, 100))
            .ToArray();
        foreach (var e in BinaryMissionLog.DecodeAll(log))
            _tracker.Apply(e);
        Frame(10, Acmi.Object("f", 0, 2_000, 1000, "Felix Knierim", "Axis"));
        var destroyed = FriendlyPositionsCommand.Execute(_tracker, See);
        Assert.Empty(destroyed.Replies);
        Assert.Contains("Destroyed", Assert.Single(destroyed.Suppressed).Reason);

        Start("");
        Frame(10, Acmi.Object("e", 2_000, 0, 1500, "Adil Gerasimov", "Allies", "P-40E-1"));
        Assert.Empty(FriendlyPositionsCommand.Execute(_tracker, See).Replies);

        Start("");
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, Mate("w", "Heinz Koelzig", 0, 100, heading: 0), Bogey("e1", 0, 30_000));
        Assert.Equal("Number one, no contacts.", Contacts(Pilot));
    }

    [Fact]
    public void Stale_tracks_are_not_a_pilot_report()
    {
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, Mate("a", "Heinz Koelzig", 0, 2_000, alt: 1000, heading: 0));
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(ReplyOutcome.PlayerPositionUnknown, FlightCommands.Rejoin(_tracker, Pilot).Outcome);
    }

    [Fact]
    public void The_game_clips_say_close_and_far_and_leave_a_few_kilometres_to_the_phrase_voice()
    {
        string[] clips =
        [
            "number.1", "number.2", "clock.3", "clock.12", "range.close", "range.far", "range.4",
            "vertical.level", "vertical.above", "compass.e", "heading", "altitude", "altitude.3000",
            "class.fighter", "class.fighters",
        ];
        bool Has(string key) => clips.Contains(key);

        Frame(10, Acmi.Object("f", 0, 800, 1000, "Felix Knierim", "Axis"));
        var close = Assert.Single(FriendlyPositionsCommand.Execute(_tracker, See).Replies);
        Assert.Equal(["clock.3", "range.close", "vertical.level"], ClipPhraser.Phrase(close.Tokens!, Has));
        Assert.Equal(["clock.3", "vertical.level"], ClipPhraser.Phrase(close.Tokens!, key => Has(key) && key != "range.close")); // a missing clip drops only that part

        Start("");
        Frame(10, Acmi.Object("f", 0, 4_000, 1000, "Felix Knierim", "Axis"));
        var few = Assert.Single(FriendlyPositionsCommand.Execute(_tracker, See).Replies);
        Assert.Equal(["clock.3", "vertical.level"], ClipPhraser.Phrase(few.Tokens!, Has)); // "a few kilometres" is chat only

        Start("");
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, Mate("a", "Heinz Koelzig", 0, 20_000, alt: 3000, heading: 90));
        var unseen = Assert.Single(FlightCommands.Rejoin(_tracker, Pilot).Replies);
        Assert.Equal(
            ["number.1", "heading", "compass.e", "altitude", "altitude.3000"],
            ClipPhraser.Phrase(unseen.Tokens!, Has));

        Start(Overcast);
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Ernst Lignitz", 2);
        Frame(10,
            Mate("w", "Heinz Koelzig", 0, 100, alt: 1000, heading: 0),
            Mate("d", "Ernst Lignitz", 8_000, 0, alt: 3000, heading: 0),
            Bogey("e1", 8_000, 200, alt: 3000));
        var onMe = Assert.Single(EnemyCommands.EnemyContacts(_tracker, See).Replies);
        Assert.Equal(["number.2", "class.fighter"], ClipPhraser.Phrase(onMe.Tokens!, Has)); // "Yak" and "on me" stay on the chat
    }

    [Fact]
    public void A_pilot_on_his_route_who_cannot_see_the_player_names_his_map_square()
    {
        StartMission(RouteMission(offRoute: false));
        LogFlightMember("Felix Knierim", 2);
        var (latitude, longitude) = MapGrid.StalingradLatLon(135864, 215000);
        FrameAt(10, latitude, longitude, shiftNorth: -15_000);

        var reply = Assert.Single(FriendlyPositionsCommand.Execute(_tracker, Pilot).Replies);

        Assert.Equal("Number two, I cannot see you. Heading east, altitude 1000 metres. Grid 1022-5.", reply.Text);
        Assert.Contains(reply.Tokens!, t => t.Word == SpeechWord.Grid && t.Value == 1022 && t.Name == "5");
        Assert.Null(ClipPhraser.Phrase(reply.Tokens!, _ => false)); // no clips: chat only
    }

    [Fact]
    public void From_memory_the_square_comes_from_the_map_metres_themselves()
    {
        StartMission(RouteMission(offRoute: false));
        LogFlightMember("Felix Knierim", 2); // log ids: the player 1000, Felix 1002
        // IL-2's memory gives the mission's own metres, so no latitude fit stands between him and his square.
        // Two reads give his course over ground: east.
        _tracker.Apply([new MemoryAircraft(1000, 120864, 1000, 214900, 100), new MemoryAircraft(1002, 135864, 1000, 214900, 100)], 10);
        _tracker.Apply([new MemoryAircraft(1000, 120964, 1000, 214900, 100), new MemoryAircraft(1002, 135864, 1000, 215000, 100)], 11);

        var reply = Assert.Single(FriendlyPositionsCommand.Execute(_tracker, Pilot).Replies);

        Assert.Equal("Number two, I cannot see you. Heading east, altitude 1000 metres. Grid 1022-5.", reply.Text);
    }

    [Fact]
    public void An_allied_flight_far_from_its_waypoint_still_names_its_square()
    {
        StartMission(RouteMission(offRoute: true));
        var (latitude, longitude) = MapGrid.StalingradLatLon(135864, 215000);
        FrameAt(10, latitude, longitude, shiftNorth: -15_000);

        var reply = Assert.Single(FriendlyPositionsCommand.Execute(_tracker, Pilot).Replies);

        Assert.Equal("Felix Knierim, I cannot see you. Heading east, altitude 1000 metres. Grid 1022-5.", reply.Text);
        Assert.Contains(reply.Tokens!, t => t.Word == SpeechWord.Grid && t.Value == 1022 && t.Name == "5");
    }

    [Fact]
    public void The_players_flight_off_its_route_or_on_another_map_does_not_name_a_square()
    {
        StartMission(RouteMission(offRoute: true));
        LogFlightMember("Felix Knierim", 2);
        var (latitude, longitude) = MapGrid.StalingradLatLon(135864, 215000);
        FrameAt(10, latitude, longitude, shiftNorth: -15_000);
        Assert.Equal("Number two, I cannot see you. Heading east, altitude 1000 metres.",
            Assert.Single(FriendlyPositionsCommand.Execute(_tracker, Pilot).Replies).Text);

        StartMission(RouteMission(offRoute: false).Replace("stalingrad-summer-1942", "moscow-summer", StringComparison.Ordinal));
        FrameAt(10, latitude, longitude, shiftNorth: -15_000);
        Assert.DoesNotContain("Grid", Assert.Single(FriendlyPositionsCommand.Execute(_tracker, Pilot).Replies).Text, StringComparison.Ordinal);
    }

    private void StartMission(string mission)
    {
        _tracker = new ContactTracker(_clock);
        _tracker.SetPlan(MissionPlan.FromBlocks(MissionFileParser.Parse(new StringReader(mission))));
        _acmi = new AcmiStream();
        _acmi.Feed(Acmi.Header + "#0\n");
        _nextId = 1000;
        LogFlightMember(PlayerName, 4, isPlayer: true);
    }

    /// <summary>
    /// Places Felix at an absolute lat/lon and the player <paramref name="shiftNorth"/> metres of Il2 north away,
    /// so the two stay inside radio range without sharing a square.
    /// </summary>
    private void FrameAt(double time, double latitude, double longitude, double shiftNorth)
    {
        string felix = AtLatLon("f", "Felix Knierim", latitude, longitude);
        var playerIl2 = MapGrid.ProjectStalingrad(latitude, longitude);
        var (playerLat, playerLon) = MapGrid.StalingradLatLon(playerIl2.North + shiftNorth, playerIl2.East);
        string player = AtLatLon("p", PlayerName, playerLat, playerLon, heading: 0);
        _acmi.Feed($"#{time}\n" + player + felix);
        _tracker.Apply(_acmi);
    }

    private static string AtLatLon(string id, string pilot, double latitude, double longitude, double heading = 90)
    {
        double northM = (latitude - Acmi.RefLat) * 111_320.0;
        double eastM = (longitude - Acmi.RefLon) * 111_320.0 * Math.Cos(latitude * Math.PI / 180.0);
        return Acmi.Object(id, northM, eastM, 1000, pilot, "Axis", heading: heading);
    }

    private static string RouteMission(bool offRoute) => $$"""
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
          Name = "{{PlayerName}},201033,0";
          Index = 1;
          Country = 201;
          AILevel = 0;
        }
        Plane
        {
          Name = "Felix Knierim";
          Index = 10;
          Country = 201;
          Callsign = 3;
          Callnum = 2;
        }
        Plane
        {
          Name = "Werner Hoffmann";
          Index = 20;
          Country = 201;
          Callsign = 3;
          Callnum = 1;
        }
        MCU_TR_Entity
        {
          Index = 21;
          MisObjID = 20;
        }
        MCU_Waypoint
        {
          Index = 30;
          Targets = [31];
          Objects = [21];
          XPos = 135864;
          YPos = 60;
          ZPos = {{(offRoute ? 100000 : 200000)}};
        }
        MCU_Waypoint
        {
          Index = 31;
          Targets = [];
          Objects = [21];
          XPos = 135864;
          YPos = 60;
          ZPos = {{(offRoute ? 130000 : 230000)}};
        }
        """;

    private string Contacts(RadioSettings? settings = null) =>
        Assert.Single(EnemyCommands.EnemyContacts(_tracker, settings ?? See).Replies).Text;

    private static string[] Four(double east) =>
        [Bogey("a", 0, east), Bogey("b", 80, east + 40), Bogey("c", -80, east + 80), Bogey("d", 40, east - 40)];

    private static string[] Eight(double east) =>
        [.. Four(east), Bogey("e", 120, east + 20), Bogey("f", -120, east + 60), Bogey("g", 20, east + 100), Bogey("h", -40, east - 80)];

    private static string[] SixFar() =>
        Enumerable.Range(0, 6).Select(i => Bogey($"n{i}", 6_000, (i - 2.5) * 80)).ToArray();
}
