using Il2Radio.Core.Acmi;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>
/// A flight mate who saw it calls a loss, unless IL-2's own pilots announce it. The scenario is the
/// 2026-10-26 Career flight: number six and a Hurricane collide (AID -1 on both) with the flight close by.
/// </summary>
public class LossCallTests
{
    private const string PlayerName = "Gert Knoll";
    private readonly ManualTimeProvider _clock = new();
    private readonly ContactTracker _tracker;
    private readonly AcmiStream _acmi = new();
    private readonly UnaskedCalls _calls = new();
    private readonly SightingMemory _memory = new();
    private readonly long _player, _leader, _six, _sixPilot, _hurricane, _hurricanePilot;
    private int _nextId = 1000;

    private static readonly RadioSettings Radio = new() { Realism = RadioRealism.Pilot };

    // Only the loss is under test here; the picture and route calls have their own tests.
    private static readonly UnaskedCallSettings LossesOnly = new() { Route = false, Contacts = false };

    public LossCallTests()
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
              Name = "{{PlayerName}}";
              Country = 201;
              AILevel = 0;
            }
            """))));
        (_leader, _) = Mate("Viktor Richter", 1);
        (_player, _) = Mate(PlayerName, 4, isPlayer: true);
        (_six, _sixPilot) = Mate("Enno Drecker", 6);
        (_hurricane, _hurricanePilot) = Spawn("Hurricane Mk.II", 101, "Timofey Borisenko,101651,0");
        _acmi.Feed(Acmi.Header + "#0\n");
    }

    [Fact]
    public void A_collision_the_game_does_not_announce_is_called_by_the_mate_who_saw_it()
    {
        Fly(10, leaderNorth: 2_000);
        Assert.Null(Say());

        Collide(12);
        Fly(20, leaderNorth: 2_000, wreckage: true);

        var call = Say();
        Assert.Equal("Number one, Number six is down. Hurricane down.", call!.Text);
        Assert.Equal("Viktor Richter", call.Contact.PilotName);
        Assert.Null(call.Tokens); // chat only: no recording says a mate is down
        Assert.Null(Say());
    }

    [Fact]
    public void An_enemy_seen_from_further_than_type_range_is_an_enemy_fighter()
    {
        Look(leaderNorth: -5_000);
        Collide(12);
        Fly(20, leaderNorth: -5_000, wreckage: true);

        Assert.Equal("Number one, Number six is down. Enemy fighter down.", Say()!.Text);
    }

    [Fact]
    public void Nobody_calls_a_loss_no_flight_mate_was_near()
    {
        Look(leaderNorth: -20_000);
        Collide(12);
        Fly(20, leaderNorth: -20_000, wreckage: true);

        Assert.Null(Say());
    }

    [Fact]
    public void An_AI_mate_reports_his_own_kill_in_the_game()
    {
        Look(leaderNorth: 2_000);
        _tracker.Apply(new KillEvent(600, _leader, _hurricane, Here));
        Fly(20, leaderNorth: 2_000);

        Assert.Null(Say());
    }

    [Fact]
    public void An_enemy_that_crashes_on_its_own_is_target_destroyed_on_the_headset()
    {
        Look(leaderNorth: 2_000);
        _tracker.Apply(new KillEvent(600, -1, _hurricane, Here));
        Fly(20, leaderNorth: 2_000, wreckage: true);

        var call = Say();
        Assert.Equal("Number six, Hurricane down.", call!.Text); // six, alive and beside it, saw it best
        Assert.Equal([new SpeechToken(SpeechWord.Speaker, 6), new SpeechToken(SpeechWord.Destroyed)], call.Tokens);
    }

    [Fact]
    public void The_player_knows_his_own_kill()
    {
        Look(leaderNorth: 2_000);
        _tracker.Apply(new KillEvent(600, _player, _hurricane, Here));
        Fly(20, leaderNorth: 2_000);

        Assert.Null(Say());
    }

    [Fact]
    public void A_mate_shot_down_alive_calls_it_himself()
    {
        Look(leaderNorth: 2_000);
        _tracker.Apply(new KillEvent(600, _hurricane, _six, Here));
        Fly(20, leaderNorth: 2_000, wreckage: true);

        Assert.Null(Say());
    }

    [Fact]
    public void A_mate_whose_pilot_is_killed_cannot_call_it_so_a_witness_does()
    {
        Look(leaderNorth: 2_000);
        _tracker.Apply(new KillEvent(600, _hurricane, _six, Here));
        _tracker.Apply(new KillEvent(600, _hurricane, _sixPilot, Here));
        Fly(20, leaderNorth: 2_000, wreckage: true);

        Assert.Equal("Number one, Number six is down.", Say()!.Text);
    }

    [Fact]
    public void A_bail_out_after_a_collision_is_said_as_bailed_out()
    {
        Look(leaderNorth: 2_000);
        _tracker.Apply(new KillEvent(600, -1, _six, Here));
        _tracker.Apply(new BotEjectedEvent(601, _sixPilot, _six, Here));
        Fly(20, leaderNorth: 2_000, wreckage: true);

        Assert.Equal("Number one, Number six bailed out.", Say()!.Text);
    }

    [Fact]
    public void A_loss_already_in_the_log_when_the_session_first_looks_is_not_called()
    {
        Fly(10, leaderNorth: 2_000);
        Collide(12);
        Fly(20, leaderNorth: 2_000, wreckage: true);

        Assert.Null(new UnaskedCalls().Consider(_tracker, Radio, _memory, LossesOnly));
    }

    [Fact]
    public void Turning_loss_calls_off_keeps_them_quiet()
    {
        Fly(10, leaderNorth: 2_000);
        Assert.Null(_calls.Consider(_tracker, Radio, _memory, LossesOnly with { Losses = false }));
        Collide(12);
        Fly(20, leaderNorth: 2_000, wreckage: true);

        Assert.Null(_calls.Consider(_tracker, Radio, _memory, LossesOnly with { Losses = false }));
    }

    [Fact]
    public void A_tracked_aircraft_named_later_by_the_log_stays_one_contact()
    {
        var tracker = new ContactTracker(_clock);
        var acmi = new AcmiStream();
        acmi.Feed(Acmi.Header + "#0\n" + Acmi.Object("h", 0, 0, 3000, "Mikhail Petrov", "Allies", "Hurricane Mk.II") + "#1\n");
        tracker.Apply(acmi);
        string key = Assert.Single(tracker.Contacts).Key;

        tracker.Apply(new ObjectSpawnedEvent(50, 7, "Hurricane Mk.II", 101, "Mikhail Petrov,101651,1", -1, Here));
        tracker.Apply(new ObjectSpawnedEvent(50, 8, "BotPilot_Hurricane", 101, "BotPilot_Hurricane", 7, Here));
        tracker.Apply(new KillEvent(100, -1, 7, Here));

        var hurricane = Assert.Single(tracker.Contacts);
        Assert.Equal(key, hurricane.Key);
        Assert.Equal(ContactStatus.Destroyed, hurricane.Status);
        Assert.Null(hurricane.Loss!.Attacker);
    }

    private static readonly WorldPoint Here = WorldPoint.FromIl2(0, 4500, 0);

    private RadioReply? Say() => _calls.Consider(_tracker, Radio, _memory, LossesOnly);

    /// <summary>The first look of the flight, before anything is lost, as the live session takes one every 10 s.</summary>
    private void Look(double leaderNorth)
    {
        Fly(10, leaderNorth);
        Assert.Null(Say());
    }

    private void Collide(long tick)
    {
        _tracker.Apply(new KillEvent(tick, -1, _hurricane, Here));
        _tracker.Apply(new KillEvent(tick, -1, _hurricanePilot, Here));
        _tracker.Apply(new KillEvent(tick, -1, _six, Here));
        _tracker.Apply(new KillEvent(tick, -1, _sixPilot, Here));
    }

    /// <summary>The player 13 km south of the fight, the leader near it, six and the Hurricane together at 13 km north.</summary>
    private void Fly(double time, double leaderNorth, bool wreckage = false)
    {
        double fight = 13_000;
        _acmi.Feed($"#{time}\n"
            + Acmi.Object("p", 0, 0, 4000, PlayerName, "Axis", heading: 0)
            + Acmi.Object("w", fight + leaderNorth, 0, 4500, "Viktor Richter", "Axis", heading: 0)
            + Acmi.Object("s", fight, 0, wreckage ? 2000 : 4500, "Enno Drecker", "Axis", heading: 0)
            + Acmi.Object("h", fight, 100, wreckage ? 2000 : 4500, "Timofey Borisenko", "Allies", "Hurricane Mk.II", heading: 180));
        _tracker.Apply(_acmi);
    }

    private (long Plane, long Pilot) Mate(string pilot, int formation, bool isPlayer = false)
    {
        var (plane, bot) = Spawn("Bf 109 F-4", 201, pilot);
        _tracker.Apply(new PlaneSpawnedEvent(0, plane, bot, WorldPoint.FromIl2(0, 50, 0), "Bf 109 F-4", 201, formation, 1, 0, -1, isPlayer, ""));
        return (plane, bot);
    }

    private (long Plane, long Pilot) Spawn(string type, int country, string name)
    {
        long plane = _nextId++, bot = _nextId++;
        var origin = WorldPoint.FromIl2(0, 50, 0);
        _tracker.Apply(new ObjectSpawnedEvent(0, plane, type, country, name, -1, origin));
        _tracker.Apply(new ObjectSpawnedEvent(0, bot, "BotPilot", country, "BotPilot", plane, origin));
        return (plane, bot);
    }
}
