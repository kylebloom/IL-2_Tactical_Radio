using Il2Radio.Core.Acmi;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>Text lines for the on-screen radio log (overlay / second-screen window).</summary>
public class RadioChatTests
{
    private const string PlayerName = "Gert Knoll";
    private readonly ManualTimeProvider _clock = new();
    private readonly ContactTracker _tracker;
    private readonly AcmiStream _acmi = new();
    private int _nextId = 1000;

    public RadioChatTests()
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

    private void LogFlightMember(string pilot, int formationIndex, bool isPlayer = false)
    {
        long plane = _nextId++, bot = _nextId++;
        var origin = WorldPoint.FromIl2(0, 50, 0);
        _tracker.Apply(new ObjectSpawnedEvent(0, plane, "Bf 109 F-4", 201, pilot, -1, origin));
        _tracker.Apply(new ObjectSpawnedEvent(0, bot, "BotPilot_Bf109_GER41", 201, "BotPilot_Bf109_GER41", plane, origin));
        _tracker.Apply(new PlaneSpawnedEvent(0, plane, bot, origin, "Bf 109 F-4", 201, formationIndex, 1, 0, -1, isPlayer, ""));
    }

    private void Frame(double time, params string[] objects)
    {
        _acmi.Feed($"#{time}\n" + string.Concat(objects));
        _tracker.Apply(_acmi);
    }

    private static string Player() => Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis");

    private static string Mate(string id, string pilot, double north, double east, double alt = 3000) =>
        Acmi.Object(id, north, east, alt, pilot, "Axis", heading: 90);

    private IReadOnlyList<ChatLine> Exchange(RadioCommand command) =>
        RadioChat.Lines(command, RadioCommands.Execute(command, _tracker), _tracker.Player);

    [Fact]
    public void Request_comes_first_from_the_player_then_each_reply_verbatim_from_its_speaker()
    {
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Ernst Lignitz", 6);
        Frame(10, Player(), Mate("a", "Heinz Koelzig", 0, 17_000), Mate("c", "Ernst Lignitz", 5000, 0, alt: 1200));
        var result = RadioCommands.Execute(RadioCommand.FriendlyPositions, _tracker);

        var lines = RadioChat.Lines(RadioCommand.FriendlyPositions, result, _tracker.Player);

        Assert.Equal(3, lines.Count);
        Assert.Equal(new ChatLine(ChatLineKind.Request, PlayerName, "All friendlies, report positions.", 2, TimeSpan.Zero), lines[0]);
        Assert.All(lines.Skip(1), l => Assert.Equal(ChatLineKind.Reply, l.Kind));
        Assert.Equal(result.Replies.Select(r => r.Text), lines.Skip(1).Select(l => l.Text));
        Assert.Equal(["Ernst Lignitz", "Heinz Koelzig"], lines.Skip(1).Select(l => l.Speaker));
    }

    [Fact]
    public void Replies_are_spaced_by_estimated_airtime()
    {
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Ernst Lignitz", 6);
        Frame(10, Player(), Mate("a", "Heinz Koelzig", 0, 17_000), Mate("c", "Ernst Lignitz", 5000, 0, alt: 1200));

        var lines = Exchange(RadioCommand.FriendlyPositions);

        Assert.Equal(RadioChat.FirstReplyDelay, lines[1].Delay);
        Assert.Equal(lines[1].Delay + RadioChat.EstimatedAirtime(lines[1].Text), lines[2].Delay);
    }

    [Fact]
    public void Silence_without_a_player_position_is_explained_as_a_system_line()
    {
        LogFlightMember("Heinz Koelzig", 1); // logged but never tracked live

        var lines = Exchange(RadioCommand.FlightPosition);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Flight, report your position.", lines[0].Text);
        Assert.Equal(ChatLineKind.System, lines[1].Kind);
        Assert.Contains("no live position", lines[1].Text);
    }

    [Fact]
    public void Unknown_player_is_called_you()
    {
        var empty = new RadioResult(ReplyOutcome.NoReply, [], []);

        var lines = RadioChat.Lines(RadioCommand.Rejoin, empty, player: null);

        Assert.Equal("You", lines[0].Speaker);
        Assert.Equal("no reply", lines[1].Text);
    }
}

/// <summary>Immediate press feedback: the call is shown at once, the answers (or the reason for silence) later.</summary>
public class RadioChatFeedbackTests
{
    private static readonly DateTime T0 = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Request_line_is_due_immediately_and_names_the_caller()
    {
        var line = RadioChat.RequestLine(RadioCommand.FlightPosition, player: null);

        Assert.Equal(new ChatLine(ChatLineKind.Request, "You", "Flight, report your position.", -1, TimeSpan.Zero), line);
    }

    [Fact]
    public void Request_shows_at_once_and_stays_when_the_answers_arrive()
    {
        var timeline = new ChatTimeline();
        timeline.Post([RadioChat.RequestLine(RadioCommand.FriendlyPositions, null)], T0);

        Assert.Equal(["All friendlies, report positions."], timeline.Snapshot(T0).Select(v => v.Line.Text));

        // ~1.2 s later the snapshot is done; the answer batch does not repeat the call.
        var replies = RadioChat.Replies(new RadioResult(ReplyOutcome.NoReply, [], []), "position snapshot failed");
        timeline.Post(replies, T0.AddSeconds(1.2));

        Assert.Equal(["All friendlies, report positions.", "no reply: position snapshot failed"],
            timeline.Snapshot(T0.AddSeconds(2)).Select(v => v.Line.Text));
    }

    [Theory]
    [InlineData(ReplyOutcome.PlayerPositionUnknown, null, "no reply: no live position for your aircraft (is a track recording?)")]
    [InlineData(ReplyOutcome.PlayerPositionUnknown, "position snapshot needs administrator rights", "no reply: position snapshot needs administrator rights")]
    [InlineData(ReplyOutcome.NoReply, null, "no reply")]
    public void Silence_is_explained_with_the_failure_reason_when_known(ReplyOutcome outcome, string? problem, string expected)
    {
        var line = Assert.Single(RadioChat.Replies(new RadioResult(outcome, [], []), problem));

        Assert.Equal(ChatLineKind.System, line.Kind);
        Assert.Equal(expected, line.Text);
    }
}
