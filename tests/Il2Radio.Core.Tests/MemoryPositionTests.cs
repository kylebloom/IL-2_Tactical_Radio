using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.Memory;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;
using Il2Radio.Core.Settings;

namespace Il2Radio.Core.Tests;

/// <summary>
/// Positions read from IL-2's memory (<see cref="PositionSource.Memory"/>): a live track in the mission's own
/// metres, for aircraft the mission log has named. The honesty edges are the same as the ACMI's: stale reads,
/// dead aircraft, and aircraft nobody has named.
/// </summary>
public class MemoryPositionTests
{
    private const string PlayerName = "Gert Knoll";
    private const int Player = 1055743, Raub = 1001471, Yak = 806911;
    private readonly ManualTimeProvider _clock = new();
    private readonly ContactTracker _tracker;

    public MemoryPositionTests()
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
        LogPlane(Player, PlayerName, 201, formation: 4, isPlayer: true);
        LogPlane(Raub, "Richard Raub", 201, formation: 1);
        LogPlane(Yak, "Foma Bayevsky,101273,1", 101, formation: null, type: "Yak-1 ser.69");
    }

    private void LogPlane(int id, string pilot, int country, int? formation, bool isPlayer = false, string type = "Bf 109 F-4")
    {
        var origin = WorldPoint.FromIl2(119848, 57, 157764);
        _tracker.Apply(new ObjectSpawnedEvent(0, id, type, country, pilot, -1, origin));
        _tracker.Apply(new ObjectSpawnedEvent(0, id + 1024, "BotPilot", country, "BotPilot", id, origin));
        if (formation is int n)
            _tracker.Apply(new PlaneSpawnedEvent(0, id, id + 1024, origin, type, country, n, 1, 0, -1, isPlayer, ""));
    }

    private void Read(double time, params MemoryAircraft[] aircraft) => _tracker.Apply(aircraft, time);

    private static MemoryAircraft At(int id, double north, double east, double height = 1000) => new(id, north, height, east, 100);

    private Contact Contact(int id) => _tracker.Contacts.Single(c => c.LogId == id);

    [Fact]
    public void Memory_positions_are_a_live_track_in_map_metres_and_answer_the_radio()
    {
        Read(10, At(Player, 120000, 157000), At(Raub, 120000, 160000, 2950));

        var fix = Contact(Raub).LastFix!;
        Assert.Equal(PositionSource.Memory, fix.Source);
        Assert.True(fix.IsTrack);
        Assert.Equal(CoordinateFrame.Il2World, fix.Position.Frame);
        Assert.Equal((120000.0, 160000.0, 2950.0), (fix.Position.North, fix.Position.East, fix.Position.Altitude));

        var result = FriendlyPositionsCommand.Execute(_tracker);
        Assert.Equal(ReplyOutcome.Position, result.Outcome);
        var reply = Assert.Single(result.Replies);
        Assert.Equal(90, reply.Relative.BearingDegrees, 0);
        Assert.Equal("Number one, bearing zero-nine-zero, approximately 3 kilometres, altitude 3000 metres.", reply.Text);
    }

    [Fact]
    public void Objects_the_mission_log_has_not_named_are_left_out()
    {
        int before = _tracker.Contacts.Count();

        Read(10, At(Player, 120000, 157000), At(1567743, 120226, 157036, 56)); // a parked object, or a spawn the next chunk will name

        Assert.Equal(before, _tracker.Contacts.Count());
        Assert.DoesNotContain(_tracker.Contacts, c => c.LogId == 1567743);
    }

    [Fact]
    public void Heading_is_the_course_over_ground_once_the_aircraft_has_moved()
    {
        Read(10, At(Raub, 120000, 160000));
        Assert.Null(Contact(Raub).LastFix!.Heading);

        Read(11, At(Raub, 120100, 160050));
        Assert.Equal(26.57, Contact(Raub).LastFix!.Heading!.Value, 2);

        // Under the baseline (parts jitter, a parked aircraft): the last course stands.
        Read(12, At(Raub, 120090, 160060));
        Assert.Equal(26.57, Contact(Raub).LastFix!.Heading!.Value, 2);
    }

    [Fact]
    public void An_aircraft_whose_objects_vanish_is_gone_and_comes_back_if_read_again()
    {
        Read(10, At(Player, 120000, 157000), At(Raub, 120000, 160000));
        Read(11, At(Player, 120000, 157100));

        Assert.Equal(ContactStatus.Despawned, Contact(Raub).Status);
        Assert.Equal(ReplyOutcome.NoReply, FriendlyPositionsCommand.Execute(_tracker).Outcome);

        Read(12, At(Player, 120000, 157200), At(Raub, 120000, 160200));
        Assert.True(Contact(Raub).IsOperational);
    }

    [Fact]
    public void An_aircraft_the_log_says_is_killed_never_answers_though_memory_still_moves_it()
    {
        // VERIFIED 2026-10-27: a shot-down Ju 52 kept a live position in memory for ~40 s while it fell.
        Read(10, At(Player, 120000, 157000), At(Raub, 120000, 160000));
        _tracker.Apply(new KillEvent(500, -1, Raub, WorldPoint.FromIl2(120000, 1000, 160000)));
        Read(11, At(Player, 120000, 157100), At(Raub, 120050, 160080, 700));

        Assert.Equal(ContactStatus.Destroyed, Contact(Raub).Status);
        Assert.Equal(PositionSource.Memory, Contact(Raub).LastFix!.Source);
        Assert.Equal(ReplyOutcome.NoReply, FriendlyPositionsCommand.Execute(_tracker).Outcome);
    }

    [Fact]
    public void Memory_fixes_go_stale_when_memory_stops_being_read()
    {
        Read(10, At(Player, 120000, 157000), At(Raub, 120000, 160000));
        Assert.Equal(ReplyOutcome.Position, FriendlyPositionsCommand.Execute(_tracker).Outcome);

        _clock.Advance(TimeSpan.FromSeconds(15)); // no read since: memory lost, or the radio stopped polling

        Assert.Equal(ReplyOutcome.PlayerPositionUnknown, FriendlyPositionsCommand.Execute(_tracker).Outcome);
    }

    [Fact]
    public void An_enemy_from_memory_is_reported_only_once_seen()
    {
        var blind = new RadioSettings { Visibility = new VisibilitySettings { DetectMetresPerSpanMetre = 0.01, MaxDetectFactor = 1 } };
        Read(10, At(Player, 120000, 157000), At(Raub, 120000, 157100), At(Yak, 121000, 157000, 1100));

        var result = EnemyCommands.EnemyContacts(_tracker, blind);

        Assert.Equal("Number one, no contacts.", Assert.Single(result.Replies).Text);
        Assert.Contains(result.Suppressed, s => s.Contact.LogId == Yak);
    }

    [Fact]
    public void An_aircraft_reading_is_the_median_of_its_parts()
    {
        var parts = new[]
        {
            new MemoryObject(1, Raub, 120000, 1000, 160000, 60),
            new MemoryObject(2, Raub, 120010, 1004, 160012, 61),
            new MemoryObject(3, Raub, 120004, 1002, 160006, 100), // one odd part (seen once on a take-off run)
            new MemoryObject(4, Yak, 130000, 900, 170000, 120),
        };

        var aircraft = MemoryAircraft.Of(parts);

        var raub = Assert.Single(aircraft, a => a.Id == Raub);
        Assert.Equal((120004.0, 1002.0, 160006.0, 61.0), (raub.North, raub.Height, raub.East, raub.Speed));
        Assert.Single(aircraft, a => a.Id == Yak);
    }

    [Fact]
    public void The_position_log_row_keeps_mem_roster_columns_first_and_adds_who_it_is()
    {
        Read(10, At(Raub, 120000, 160000));
        Read(11, At(Raub, 120100, 160050, 1234.56));

        string row = PositionLog.Row(1_761_600_000_123, 11, At(Raub, 120100, 160050, 1234.56), Contact(Raub));
        string unnamed = PositionLog.Row(1_761_600_000_123, 11, At(1567743, 120226, 157036, 56), null);

        var f = row.Split(',');
        Assert.Equal(PositionLog.Header.Split(',').Length, f.Length);
        Assert.Equal(["1761600000123", Raub.ToString(), "120100.0", "1234.6", "160050.0", "100.0", "27", "11.0"], f[..8]);
        Assert.Contains("Raub", f[8]);
        Assert.Equal(("Bf 109 F-4", "2", "Spawned"), (f[9], f[10], f[11]));
        Assert.Equal("1761600000123,1567743,120226.0,56.0,157036.0,100.0,,11.0,,,,", unnamed);
    }

    [Theory]
    [InlineData("auto", PositionsMode.Auto)]
    [InlineData("Memory", PositionsMode.Memory)]
    [InlineData(" track ", PositionsMode.Track)]
    public void The_positions_setting_reads_each_mode(string value, PositionsMode mode)
    {
        var (settings, problems) = AppSettings.Parse($$"""{ "positions": "{{value}}" }""");

        Assert.Empty(problems);
        Assert.Equal(mode, settings.PositionsMode);
    }

    [Fact]
    public void The_positions_setting_defaults_to_auto_and_rejects_anything_else()
    {
        Assert.Equal(PositionsMode.Auto, new AppSettings().PositionsMode);
        var (_, problems) = AppSettings.Parse("""{ "positions": "ram" }""");
        Assert.Contains(problems, p => p.StartsWith("positions \"ram\"", StringComparison.Ordinal));
    }
}
