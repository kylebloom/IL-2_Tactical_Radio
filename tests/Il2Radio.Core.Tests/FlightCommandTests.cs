using Il2Radio.Core.Acmi;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>Reply grouping (command 1) and the flight commands 3 (flight position) and 5 (rejoin).</summary>
public class FlightCommandTests
{
    private const string PlayerName = "Gert Knoll";
    private readonly ManualTimeProvider _clock = new();
    private readonly ContactTracker _tracker;
    private readonly AcmiStream _acmi = new();
    private int _nextId = 1000;

    public FlightCommandTests()
    {
        _tracker = new ContactTracker(_clock);
        _tracker.SetPlan(MissionPlan.FromBlocks(MissionFileParser.Parse(new StringReader($$"""
            Options
            {
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
        _acmi.Feed(Acmi.Header + "#0\n");
        LogFlightMember(PlayerName, 4, isPlayer: true);
    }

    /// <summary>Spawns an aircraft of the player's flight the way the mission log does (AType 12 + BotPilot + AType 10).</summary>
    private long LogFlightMember(string pilot, int formationIndex, bool isPlayer = false)
    {
        long plane = _nextId++, bot = _nextId++;
        var origin = WorldPoint.FromIl2(0, 50, 0);
        _tracker.Apply(new ObjectSpawnedEvent(0, plane, "Bf 109 F-4", 201, pilot, -1, origin));
        _tracker.Apply(new ObjectSpawnedEvent(0, bot, "BotPilot_Bf109_GER41", 201, "BotPilot_Bf109_GER41", plane, origin));
        _tracker.Apply(new PlaneSpawnedEvent(0, plane, bot, origin, "Bf 109 F-4", 201, formationIndex, 1, 0, -1, isPlayer, ""));
        return plane;
    }

    private void Frame(double time, params string[] objects)
    {
        _acmi.Feed($"#{time}\n" + string.Concat(objects));
        _tracker.Apply(_acmi);
    }

    private static string Player(double heading = 0) => Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: heading);

    private static string Mate(string id, string pilot, double north, double east, double alt = 3000, double heading = 90) =>
        Acmi.Object(id, north, east, alt, pilot, "Axis", heading: heading);

    [Fact]
    public void Flight_flying_together_answers_as_one_group_through_its_leader()
    {
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Rudi Liebgott", 2);
        LogFlightMember("Ernst Lignitz", 6);
        Frame(10, Player(), Mate("a", "Rudi Liebgott", 0, 17_000), Mate("b", "Heinz Koelzig", 300, 17_200), Mate("c", "Ernst Lignitz", -200, 16_800));

        var reply = Assert.Single(FriendlyPositionsCommand.Execute(_tracker).Replies);

        Assert.Equal("Heinz Koelzig", reply.Contact.PilotName);
        Assert.Equal(3, reply.Members.Count);
        Assert.Equal("Number one, three aircraft together, bearing zero-nine-zero, approximately 17 kilometres, altitude 3000 metres.", reply.Text);
    }

    [Fact]
    public void Straggler_reports_separately_from_the_group()
    {
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Rudi Liebgott", 2);
        LogFlightMember("Ernst Lignitz", 6);
        Frame(10, Player(), Mate("a", "Heinz Koelzig", 0, 17_000), Mate("b", "Rudi Liebgott", 0, 17_500), Mate("c", "Ernst Lignitz", 5000, 0, alt: 1200));

        var replies = FriendlyPositionsCommand.Execute(_tracker).Replies;

        Assert.Equal(2, replies.Count);
        Assert.Equal("Number six, bearing zero-zero-zero, approximately 5 kilometres, altitude 1200 metres.", replies[0].Text);
        Assert.StartsWith("Number one, two aircraft together", replies[1].Text);
    }

    [Fact]
    public void Aircraft_at_very_different_altitudes_are_not_grouped()
    {
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Rudi Liebgott", 2);
        Frame(10, Player(), Mate("a", "Heinz Koelzig", 0, 10_000, alt: 3000), Mate("b", "Rudi Liebgott", 0, 10_200, alt: 1500));

        Assert.Equal(2, FriendlyPositionsCommand.Execute(_tracker).Replies.Count);
    }

    [Fact]
    public void Flight_position_uses_clock_code_relative_to_player_heading()
    {
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Rudi Liebgott", 2);
        Frame(10, Player(heading: 0), Mate("a", "Heinz Koelzig", 0, 17_000), Mate("b", "Rudi Liebgott", 200, 17_100));

        var reply = Assert.Single(FlightCommands.FlightPosition(_tracker).Replies);

        Assert.Equal("Number one, flight of two at your three o'clock, approximately 17 kilometres, 2000 metres above you.", reply.Text);
    }

    [Theory]
    [InlineData(90, "twelve o'clock")]
    [InlineData(180, "nine o'clock")]
    [InlineData(270, "six o'clock")]
    public void Clock_position_follows_player_heading(double heading, string expected)
    {
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, Player(heading), Mate("a", "Heinz Koelzig", 0, 17_000));

        Assert.Contains(expected, Assert.Single(FlightCommands.FlightPosition(_tracker).Replies).Text);
    }

    [Fact]
    public void Player_in_formation_is_told_so()
    {
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, Player(), Mate("a", "Heinz Koelzig", 100, 150, alt: 1020));

        Assert.Equal("Number one, you are in formation with us.", Assert.Single(FlightCommands.FlightPosition(_tracker).Replies).Text);
        Assert.Equal("Number one, you are with us.", Assert.Single(FlightCommands.Rejoin(_tracker).Replies).Text);
    }

    [Theory]
    [InlineData(900, true)]
    [InlineData(1_300, false)] // in formation up to 1.5 km before 2026-10-26
    public void In_formation_means_within_a_kilometre(double east, bool inFormation)
    {
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, Player(), Mate("a", "Heinz Koelzig", 0, east, alt: 1020));

        string text = Assert.Single(FlightCommands.FlightPosition(_tracker).Replies).Text;
        Assert.Equal(inFormation, text.Contains("in formation", StringComparison.Ordinal));
    }

    [Fact]
    public void Flight_position_reports_separated_members_after_the_main_group()
    {
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Rudi Liebgott", 2);
        LogFlightMember("Kunibert Schuberth", 3);
        Frame(10, Player(heading: 0),
            Mate("a", "Heinz Koelzig", 0, 17_000), Mate("b", "Rudi Liebgott", 100, 17_200),
            Mate("c", "Kunibert Schuberth", -4000, 0, alt: 1000));

        var replies = FlightCommands.FlightPosition(_tracker).Replies;

        Assert.Equal(2, replies.Count);
        Assert.StartsWith("Number one, flight of two at your three o'clock", replies[0].Text);
        Assert.Equal("Number three, separated, at your six o'clock, approximately 4 kilometres, level with you.", replies[1].Text);
    }

    [Fact]
    public void Rejoin_gives_steer_altitude_and_heading_of_main_group()
    {
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Rudi Liebgott", 2);
        Frame(10, Player(heading: 200), Mate("a", "Heinz Koelzig", -10_000, 12_000, heading: 80), Mate("b", "Rudi Liebgott", -10_100, 12_100, heading: 84));

        var reply = Assert.Single(FlightCommands.Rejoin(_tracker).Replies);

        Assert.Equal("Number one, steer one-three-zero, approximately 16 kilometres. We are at 3000 metres, heading zero-eight-zero.", reply.Text);
    }

    [Fact]
    public void Only_the_players_flight_answers_flight_commands()
    {
        // A friendly from another unit nearby, but nobody from the player's own flight is tracked.
        Frame(10, Player(), Acmi.Object("x", 0, 3000, 1000, "Other Unit Pilot", "Axis"));

        Assert.Equal(ReplyOutcome.NoReply, FlightCommands.Rejoin(_tracker).Outcome);
        Assert.Empty(FlightCommands.FlightPosition(_tracker).Replies);
        Assert.Single(FriendlyPositionsCommand.Execute(_tracker).Replies); // command 1 still hears them
    }

    [Fact]
    public void Destroyed_flight_mate_is_not_counted_in_the_group()
    {
        LogFlightMember("Heinz Koelzig", 1);
        long rudi = LogFlightMember("Rudi Liebgott", 2);
        _tracker.Apply(new KillEvent(100, 999, rudi, WorldPoint.FromIl2(0, 500, 0)));
        Frame(10, Player(), Mate("a", "Heinz Koelzig", 0, 17_000), Mate("b", "Rudi Liebgott", 100, 17_100));

        var reply = Assert.Single(FlightCommands.FlightPosition(_tracker).Replies);

        Assert.Single(reply.Members);
        Assert.StartsWith("Number one, I am at your", reply.Text);
    }

    /// <summary>Tokens with values rounded the way the English text rounds them, for readable assertions.</summary>
    private static string Spoken(RadioReply reply) => string.Join(" ", reply.Tokens!.Select(t => t.Word switch
    {
        SpeechWord.Range => $"Range:{Math.Round(t.Value / 1000)}km",
        SpeechWord.Bearing or SpeechWord.Steer or SpeechWord.Heading => $"{t.Word}:{Math.Round(t.Value / 10) * 10 % 360}",
        SpeechWord.Vertical or SpeechWord.Altitude => $"{t.Word}:{Math.Round(t.Value / 100) * 100}",
        _ => $"{t.Word}:{t.Value}",
    }));

    [Fact]
    public void Replies_carry_tokens_that_say_the_same_as_the_text()
    {
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Rudi Liebgott", 2);
        LogFlightMember("Kunibert Schuberth", 3);
        Frame(10, Player(heading: 0),
            Mate("a", "Heinz Koelzig", 0, 17_000), Mate("b", "Rudi Liebgott", 100, 17_200),
            Mate("c", "Kunibert Schuberth", -4000, 0, alt: 1000));

        var flight = FlightCommands.FlightPosition(_tracker).Replies;
        Assert.Equal("Speaker:1 Group:2 Clock:3 Range:17km Vertical:2000", Spoken(flight[0]));
        Assert.Equal("Speaker:3 Separated:0 Clock:6 Range:4km Vertical:0", Spoken(flight[1]));

        var friendly = FriendlyPositionsCommand.Execute(_tracker).Replies;
        Assert.Equal("Speaker:3 Bearing:180 Range:4km Vertical:0 Altitude:1000", Spoken(friendly[0]));
        Assert.Equal("Speaker:1 Group:2 Bearing:90 Range:17km Vertical:2000 Altitude:3000", Spoken(friendly[1]));
    }

    [Fact]
    public void Rejoin_tokens_carry_steer_range_altitude_and_heading()
    {
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Rudi Liebgott", 2);
        Frame(10, Player(heading: 200), Mate("a", "Heinz Koelzig", -10_000, 12_000, heading: 80), Mate("b", "Rudi Liebgott", -10_100, 12_100, heading: 84));

        var reply = Assert.Single(FlightCommands.Rejoin(_tracker).Replies);

        Assert.Equal("Speaker:1 Steer:130 Range:16km Vertical:2000 Altitude:3000 Heading:80", Spoken(reply));
    }

    [Fact]
    public void In_formation_tokens()
    {
        LogFlightMember("Heinz Koelzig", 1);
        Frame(10, Player(), Mate("a", "Heinz Koelzig", 100, 150, alt: 1020));

        Assert.Equal("Speaker:1 InFormation:0", Spoken(Assert.Single(FlightCommands.FlightPosition(_tracker).Replies)));
        Assert.Equal("Speaker:1 WithUs:0", Spoken(Assert.Single(FlightCommands.Rejoin(_tracker).Replies)));
    }

    [Fact]
    public void Speaker_outside_the_players_flight_has_no_number()
    {
        Frame(10, Player(), Acmi.Object("x", 0, 3000, 1000, "Other Unit Pilot", "Axis"));

        var reply = Assert.Single(FriendlyPositionsCommand.Execute(_tracker).Replies);

        Assert.StartsWith("Speaker:0 Bearing:90", Spoken(reply));
    }

    [Fact]
    public void Flight_commands_need_a_live_player_position()
    {
        LogFlightMember("Heinz Koelzig", 1);

        Assert.Equal(ReplyOutcome.PlayerPositionUnknown, FlightCommands.FlightPosition(_tracker).Outcome);
        Assert.Equal(ReplyOutcome.PlayerPositionUnknown, FlightCommands.Rejoin(_tracker).Outcome);
    }
}
