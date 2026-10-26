using Il2Radio.Core.Acmi;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>
/// Scenario tests for radio command 1. The rule under test throughout: a reply is only produced
/// from a fresh live track, never from planned data or stale event snapshots.
/// </summary>
public class FriendlyPositionsTests
{
    private const string PlayerName = "Hannes Guth";
    private readonly ManualTimeProvider _clock = new();
    private readonly ContactTracker _tracker;
    private readonly AcmiStream _acmi = new();

    public FriendlyPositionsTests()
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
    }

    private void Frame(double time, params string[] objects)
    {
        _acmi.Feed($"#{time}\n" + string.Concat(objects));
        _tracker.Apply(_acmi);
    }

    private static string Player(double n = 0, double e = 0, double alt = 1000) => Acmi.Object("p", n, e, alt, PlayerName, "Axis");

    [Fact]
    public void Friendly_nearby_reports_bearing_range_and_altitude()
    {
        Frame(10, Player(), Acmi.Object("f1", 0, 3000, 2950, "Felix Knierim", "Axis"));

        var result = FriendlyPositionsCommand.Execute(_tracker);

        Assert.Equal(ReplyOutcome.Position, result.Outcome);
        var reply = Assert.Single(result.Replies);
        Assert.Equal(90, reply.Relative.BearingDegrees, 0);
        Assert.InRange(reply.Relative.RangeMetres, 2990, 3010);
        Assert.Equal("Felix Knierim, bearing zero-nine-zero, approximately 3 kilometres, altitude 3000 metres.", reply.Text);
    }

    [Fact]
    public void Enemy_aircraft_are_never_reported_as_friendlies()
    {
        Frame(10, Player(), Acmi.Object("e1", 2000, 0, 1500, "Adil Gerasimov", "Allies", "P-40E-1"));

        var result = FriendlyPositionsCommand.Execute(_tracker);

        Assert.Equal(ReplyOutcome.NoReply, result.Outcome);
        Assert.Empty(result.Replies);
    }

    [Fact]
    public void No_friendly_within_radio_range_gives_no_reply()
    {
        Frame(10, Player(), Acmi.Object("f1", 60_000, 0, 1000, "Felix Knierim", "Axis"));

        var result = FriendlyPositionsCommand.Execute(_tracker, new RadioSettings { RadioRangeMetres = 40_000 });

        Assert.Equal(ReplyOutcome.NoReply, result.Outcome);
        Assert.Contains("out of radio range", Assert.Single(result.Suppressed).Reason);
        var silence = Assert.Single(RadioChat.Replies(result));
        Assert.Equal("no reply: nobody can answer", silence.Text);
        Assert.DoesNotContain("is a track recording", silence.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Restarted_track_segment_still_reports_aircraft_that_are_in_it()
    {
        // The previous file's #t ran well past the live-fix limit. IL-2's next segment starts at #0.
        Frame(10, Player(),
            Acmi.Object("f1", 0, 3000, 1000, "Felix Knierim", "Axis"),
            Acmi.Object("gone", 0, 5000, 1000, "Old Wingman", "Axis"));
        Frame(40, Player(),
            Acmi.Object("f1", 0, 3000, 1000, "Felix Knierim", "Axis"),
            Acmi.Object("gone", 0, 5000, 1000, "Old Wingman", "Axis"));

        var next = new AcmiStream();
        next.Feed(Acmi.Header + "#0\n" + Player() + Acmi.Object("f1", 0, 4000, 1000, "Felix Knierim", "Axis")
            + "#2\n" + Player() + Acmi.Object("f1", 0, 4000, 1000, "Felix Knierim", "Axis"));
        _tracker.Apply(next);

        var result = FriendlyPositionsCommand.Execute(_tracker);

        Assert.Equal(ReplyOutcome.Position, result.Outcome);
        Assert.Equal("Felix Knierim", Assert.Single(result.Replies).Contact.PilotName);
        Assert.DoesNotContain(RadioChat.Lines(RadioCommand.FriendlyPositions, result, _tracker.Player),
            l => l.Text.Contains("is a track recording", StringComparison.Ordinal));
    }

    [Fact]
    public void Separated_player_hears_only_flight_members_in_range_sorted_by_distance()
    {
        Frame(10, Player(),
            Acmi.Object("f1", 0, 8000, 4000, "Far Wingman", "Axis"),
            Acmi.Object("f2", 2000, 0, 500, "Near Wingman", "Axis"),
            Acmi.Object("f3", 90_000, 0, 1000, "Lost Wingman", "Axis"));

        var result = FriendlyPositionsCommand.Execute(_tracker);

        Assert.Equal(["Near Wingman", "Far Wingman"], result.Replies.Select(r => r.Contact.PilotName));
        Assert.Equal([500.0, 4000.0], result.Replies.Select(r => r.Contact.LastFix!.Position.Altitude));
    }

    [Fact]
    public void Destroyed_friendly_does_not_answer_even_if_its_wreck_is_still_tracked()
    {
        var log = new MlgWriter()
            .ObjectSpawn(10, 500, "Bf 109 F-4", 201, "Felix Knierim,201033,0", -1, (0, 50, 0))
            .ObjectSpawn(10, 501, "BotPilot_Bf109_GER41", 201, "BotPilot_Bf109_GER41", 500, (0, 50, 0))
            .Kill(400, 900, 500, (100, 900, 100))
            .ToArray();
        foreach (var e in BinaryMissionLog.DecodeAll(log))
            _tracker.Apply(e);

        Frame(10, Player(), Acmi.Object("f1", 0, 2000, 900, "Felix Knierim", "Axis"));

        var result = FriendlyPositionsCommand.Execute(_tracker);

        Assert.Empty(result.Replies);
        Assert.Contains("Destroyed", Assert.Single(result.Suppressed).Reason);
    }

    [Fact]
    public void Stale_tracks_are_not_reported_as_live()
    {
        Frame(10, Player(), Acmi.Object("f1", 0, 3000, 1000, "Felix Knierim", "Axis"));
        _clock.Advance(TimeSpan.FromSeconds(30)); // ACMI stream stopped updating

        var result = FriendlyPositionsCommand.Execute(_tracker);

        Assert.Equal(ReplyOutcome.PlayerPositionUnknown, result.Outcome);
        Assert.Empty(result.Replies);
    }

    [Fact]
    public void Friendly_that_stopped_updating_while_player_continues_is_suppressed()
    {
        Frame(10, Player(), Acmi.Object("f1", 0, 3000, 1000, "Felix Knierim", "Axis"));
        Frame(40, Player(n: 500)); // only the player keeps moving

        var result = FriendlyPositionsCommand.Execute(_tracker);

        Assert.Empty(result.Replies);
        Assert.Contains("not live", Assert.Single(result.Suppressed).Reason);
    }

    [Fact]
    public void Missing_telemetry_means_no_relative_reports()
    {
        var tracker = new ContactTracker(_clock); // nothing received at all

        var result = FriendlyPositionsCommand.Execute(tracker);

        Assert.Equal(ReplyOutcome.PlayerPositionUnknown, result.Outcome);
        Assert.Empty(result.Replies);
        var silence = Assert.Single(RadioChat.Replies(result));
        Assert.Equal("no reply: no live position for your aircraft", silence.Text);
        Assert.DoesNotContain("is a track recording", silence.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_event_snapshots_are_never_presented_as_current_positions()
    {
        // Only mission-log data: take-off positions minutes ago, as in real single-player logs.
        var log = new MlgWriter()
            .ObjectSpawn(0, 100, "Bf 109 F-4", 201, $"{PlayerName},201033,0", -1, (0, 50, 0))
            .ObjectSpawn(0, 101, "BotPilot_Bf109_GER41", 201, "BotPilot_Bf109_GER41", 100, (0, 50, 0))
            .ObjectSpawn(0, 200, "Bf 109 F-4", 201, "Felix Knierim,201033,0", -1, (10, 50, 10))
            .ObjectSpawn(0, 201, "BotPilot_Bf109_GER41", 201, "BotPilot_Bf109_GER41", 200, (10, 50, 10))
            .TakeOff(100, 100, (0, 60, 0))
            .TakeOff(110, 200, (10, 60, 10))
            .ToArray();
        var tracker = new ContactTracker(_clock);
        tracker.SetPlan(_tracker.Plan!);
        foreach (var e in BinaryMissionLog.DecodeAll(log))
            tracker.Apply(e);

        var result = FriendlyPositionsCommand.Execute(tracker);

        Assert.True(tracker.Player is not null, "player identified from the log");
        Assert.Equal(ReplyOutcome.PlayerPositionUnknown, result.Outcome);
        Assert.Empty(result.Replies);
    }

    [Fact]
    public void Planned_mission_data_alone_produces_no_position_claims()
    {
        // _tracker has a mission plan but no live observations of any kind.
        var result = FriendlyPositionsCommand.Execute(new ContactTracker(_clock));
        Assert.Empty(result.Replies);
        Assert.DoesNotContain(_tracker.Contacts, c => !c.IsPlayer);
    }

    [Fact]
    public void Mission_restart_clears_contacts()
    {
        var log = new MlgWriter()
            .ObjectSpawn(0, 200, "Bf 109 F-4", 201, "Felix Knierim,201033,0", -1, (10, 50, 10))
            .ObjectSpawn(0, 201, "BotPilot_Bf109_GER41", 201, "BotPilot_Bf109_GER41", 200, (10, 50, 10))
            .ToArray();
        foreach (var e in BinaryMissionLog.DecodeAll(log))
            _tracker.Apply(e);
        Assert.Single(_tracker.Contacts);

        _tracker.Apply(new MissionStarted(0, null, "Missions\\_gen.msnbin"));

        Assert.Empty(_tracker.Contacts);
        Assert.Equal(1, _tracker.MissionStarts);
    }

    [Fact]
    public void Actual_track_position_is_used_not_the_planned_waypoint()
    {
        // The plan says the wingman starts next to the player; the live track says 5 km north.
        Frame(10, Player(), Acmi.Object("f1", 5000, 0, 1200, "Felix Knierim", "Axis"));

        var reply = Assert.Single(FriendlyPositionsCommand.Execute(_tracker).Replies);

        Assert.Equal(0, reply.Relative.BearingDegrees, 0);
        Assert.InRange(reply.Relative.RangeMetres, 4990, 5010);
    }

    [Fact]
    public void Positions_in_different_frames_are_not_mixed()
    {
        // Wingman known only from a log event (IL-2 frame), player from ACMI.
        var log = new MlgWriter()
            .ObjectSpawn(0, 200, "Bf 109 F-4", 201, "Felix Knierim,201033,0", -1, (10, 50, 10))
            .ObjectSpawn(0, 201, "BotPilot_Bf109_GER41", 201, "BotPilot_Bf109_GER41", 200, (10, 50, 10))
            .ToArray();
        foreach (var e in BinaryMissionLog.DecodeAll(log))
            _tracker.Apply(e);
        Frame(10, Player());

        var result = FriendlyPositionsCommand.Execute(_tracker);

        Assert.Empty(result.Replies);
        Assert.Contains("not live", Assert.Single(result.Suppressed).Reason);
    }
}
