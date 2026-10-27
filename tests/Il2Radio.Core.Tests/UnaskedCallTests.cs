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

    /// <summary>Nobody spots anything: what the game's pilots react to and our model has not shown.</summary>
    private static readonly RadioSettings Blind = new()
    {
        Realism = RadioRealism.Pilot,
        Visibility = new VisibilitySettings { DetectMetresPerSpanMetre = 1, MaxDetectFactor = 1, MisidentificationChance = 0 },
    };

    /// <summary>A fighter is seen out to about 1.5 km, and certainly at 100 m.</summary>
    private static readonly RadioSettings ShortSight = new()
    {
        Realism = RadioRealism.Pilot,
        Visibility = new VisibilitySettings { DetectMetresPerSpanMetre = 100, MaxDetectFactor = 1.5, MisidentificationChance = 0 },
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
        Assert.Null(Say()); // the plan alone: he has not started the turn

        Frame(16, 100_000, 209_000, 110);
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
        Assert.Null(Say());
        Frame(16, 100_000, 209_000, 70);

        Assert.Equal("Number one, turning to heading 360.", Say()!.Text);
        Assert.Null(Say());
    }

    [Fact]
    public void A_patrol_corner_on_the_nose_is_called_when_the_leg_runs_the_other_way()
    {
        PatrolLoop();
        Frame(10, 100_500, 203_000, 270);
        Assert.Null(Say());
        Frame(16, 100_500, 202_000, 250);

        Assert.Equal("Number one, turning to heading 090.", Say()!.Text);
    }

    [Fact]
    public void The_corner_that_closes_a_patrol_loop_is_called_on_the_way_in()
    {
        PatrolLoop();
        Frame(10, 102_000, 200_000, 230);
        Assert.Null(Say());
        Frame(16, 101_000, 199_500, 210);

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
        Assert.Null(Say());
        Frame(16, 100_000, 209_000, 95); // half way round a small corner

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

        Track(8, leaderHeading: 170);
        Assert.Equal("Number one, turning right. Follow me.", Say()!.Text); // a break off his course, not a cruise call
        Track(20, leaderHeading: 180);
        Assert.Null(Say());

        Track(40, leaderHeading: 270);
        Assert.Null(Say());
        Track(46, leaderHeading: 272);
        Assert.Null(Say()); // not yet held long enough to be a course
        Track(56, leaderHeading: 271);

        var settled = Say();
        Assert.Equal("Number one, heading 270.", settled!.Text);
        Assert.DoesNotContain("turning", settled.Text);
        Assert.Null(Say());
    }

    // 2026-10-27: the escort leader weaved ±50° over the Ju 52s. Looks either side of the top of an S-turn
    // were taken for a new course ("heading 165"), and every swing was a "follow me".
    [Fact]
    public void A_weaving_leader_calls_neither_a_course_nor_a_turn_until_he_means_one()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1);
        double[] weave = [220, 220, 220, 190, 180, 230, 275, 230, 170, 210, 265, 215];
        for (int i = 0; i < weave.Length; i++)
        {
            Track(i * 11, leaderHeading: weave[i]);
            Assert.Null(Say());
        }

        Track(132, leaderHeading: 300);
        Assert.Null(Say()); // a swing like the others, until it holds
        Track(143, leaderHeading: 320);
        Assert.Equal("Number one, turning right. Follow me.", Say()!.Text);
        Track(154, leaderHeading: 321);
        Assert.Null(Say());
        Track(165, leaderHeading: 320);
        Assert.Equal("Number one, heading 320.", Say()!.Text);
    }

    [Fact]
    public void Two_looks_either_side_of_the_top_of_a_turn_are_not_a_course()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1);
        Track(0, leaderHeading: 220);
        Track(11, leaderHeading: 220);
        Track(22, leaderHeading: 200);
        Assert.Null(Say());
        Track(33, leaderHeading: 185);
        Assert.Null(Say());
        Track(44, leaderHeading: 186); // the same nose on the way back
        Assert.Null(Say());
        Track(55, leaderHeading: 215);

        Assert.DoesNotContain("heading", Say()?.Text ?? "");
    }

    // 2026-10-27: each look saw a different few of the nine Ju 52s, and the same formation was called
    // "many unidentified transports" twice 22 s apart, then "pair".
    [Fact]
    public void A_different_part_of_the_same_planned_flight_is_not_a_new_contact()
    {
        _tracker = new ContactTracker(_clock);
        var planes = string.Join("\n", Enumerable.Range(1, 4).Select(n => $$"""
            Plane
            {
              Name = "Pilot e{{n}}";
              Index = {{100 + n}};
              Country = 101;
              Callsign = 12;
              Callnum = {{n}};
            }
            """));
        _tracker.SetPlan(MissionPlan.FromBlocks(MissionFileParser.Parse(new StringReader($$"""
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
            }
            Plane
            {
              Name = "{{LeaderName}}";
              Index = 10;
              Country = 201;
            }
            {{planes}}
            """))));
        LogFlight(playerFormation: 2, leaderFormation: 1);
        _acmi = new AcmiStream();
        _acmi.Feed(Acmi.Header + "#0\n");

        // Out of sight is out of the track here: spotting is sure, so only what the track holds is seen.
        var present = new HashSet<int>();
        void Look(double time, params int[] seen)
        {
            string frame = $"#{time.ToString(CultureInfo.InvariantCulture)}\n"
                + string.Concat(present.Except(seen).Select(n => $"-e{n}\n"))
                + $"#{(time + 1).ToString(CultureInfo.InvariantCulture)}\n" // removal takes effect on the next frame
                + Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0)
                + Acmi.Object("w", 0, 100, 1000, LeaderName, "Axis", heading: 0)
                + string.Concat(seen.Select(n =>
                    Acmi.Object($"e{n}", 5_000 + 50 * n, 5_000, 1000, $"Pilot e{n}", "Allies", "Yak-1 ser.69", heading: 90)));
            present.Clear();
            present.UnionWith(seen);
            _acmi.Feed(frame);
            _tracker.Apply(_acmi);
        }

        Look(10, 1, 2, 3);
        Assert.NotNull(Say());
        Assert.Equal(12, _tracker.Contacts.Single(c => c.PilotName == "Pilot e1").Callsign);

        Look(32, 4); // another of them, the first three out of sight
        var e4 = Assert.Single(EnemyCommands.EnemyContacts(_tracker, See, _memory).Replies.SelectMany(r => r.Members));
        Assert.Equal(("Pilot e4", 12, 1), (e4.PilotName, e4.Callsign, e4.Coalition));
        Assert.Null(Say());
        Look(55, 1, 4);
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
        Assert.Null(Say());
        Frame(16, 100_000, 209_000, 110);
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
        Assert.Null(Say());
        Frame(16, 100_000, 209_000, 110);
        Assert.Equal("Number one, turning to heading 180.", Say()!.Text);

        _tracker.Apply(new MissionStarted(1, null, "next"));
        LogFlight(playerFormation: 2, leaderFormation: 1);
        _acmi = new AcmiStream();
        _acmi.Feed(GridHeader + "#0\n");
        Frame(10, 100_000, 208_000, 90);
        Assert.Null(Say());
        Frame(16, 100_000, 209_000, 110);

        Assert.Equal("Number one, turning to heading 180.", Say()!.Text);
    }

    [Fact]
    public void Each_unasked_call_can_be_switched_off()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1);
        var off = new UnaskedCallSettings { Route = false, Contacts = true };
        var on = new UnaskedCallSettings { Route = true };
        UnaskedCalls quiet = new(), talking = new();
        Frame(10, 100_000, 208_000, 90);
        Assert.Null(quiet.Consider(_tracker, See, _memory, off));
        Assert.Null(talking.Consider(_tracker, See, _memory, on));
        Frame(16, 100_000, 209_000, 110);

        Assert.Null(quiet.Consider(_tracker, See, _memory, off));
        Assert.Equal("Number one, turning to heading 180.", talking.Consider(_tracker, See, _memory, on)!.Text);

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
        Frame(20, 100_000, 208_500, 110, Enemy(208_600, 100_000));
        Assert.DoesNotContain("turning to heading", Say()?.Text ?? ""); // he has started it, but the contact holds the call

        _acmi.Feed("#30\n-e\n#31\n");
        Frame(31, 100_000, 209_000, 120);

        Assert.Equal("Number one, turning to heading 180.", Say()!.Text); // still coming round onto the leg
    }

    [Fact]
    public void A_corner_found_while_a_contact_was_in_sight_is_called_once_the_picture_is_gone()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1);
        Frame(10, 100_000, 208_000, 90, Enemy(208_600, 100_000));
        Assert.DoesNotContain("heading", Say()!.Text);

        _acmi.Feed("#20\n-e\n#21\n");
        Frame(21, 100_000, 208_200, 90);
        Assert.Null(Say());
        Frame(26, 100_000, 209_000, 110);

        Assert.Equal("Number one, turning to heading 180.", Say()!.Text);
    }

    // Reported in a flight (2026-10-26): "turning to heading 295" was the briefed leg, the leader turned
    // north-east for the enemy instead, and the player who flew 295 lost the flight.
    [Fact]
    public void A_briefed_corner_the_leader_turns_away_from_is_not_called_and_his_real_course_is()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1);
        Frame(11, 100_000, 208_000, 90);
        Assert.Null(Say());

        Frame(21, 100_500, 209_000, 60); // left, away from 180
        Assert.Null(Say());
        Frame(31, 101_500, 209_500, 12);
        Assert.Equal("Number one, turning left. Follow me.", Say()!.Text); // 78° off his course, the way he turned
        Frame(41, 103_000, 209_600, 4); // drifting a few degrees: holding the new course
        Assert.Null(Say());
        Frame(51, 104_500, 209_700, 5);

        var call = Say();
        Assert.Equal("Number one, heading 005.", call!.Text);
        Assert.Null(Say());
    }

    [Fact]
    public void A_briefed_corner_not_started_within_the_wait_is_dropped()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1);
        Frame(11, 100_000, 208_000, 90);
        Assert.Null(Say());
        Frame(41, 100_000, 212_000, 90);
        Assert.Null(Say());
        Frame(75, 100_000, 216_000, 90);
        Assert.Null(Say());

        Frame(80, 100_000, 217_000, 130);
        Assert.Null(Say());
        Frame(90, 99_500, 217_500, 175);
        Assert.Equal("Number one, turning right. Follow me.", Say()!.Text);
        Frame(96, 99_000, 217_500, 178);
        Assert.Null(Say());
        Frame(106, 98_000, 217_500, 179);

        Assert.Equal("Number one, heading 180.", Say()!.Text); // what he flies, not the old corner
    }

    // Reported in a flight (2026-10-26): the leader left the route for the enemy, and the player, ahead and out
    // of sight of the flight, heard nothing until the fight was already on.
    [Fact]
    public void A_leader_turning_off_his_course_for_an_enemy_in_the_picture_says_he_is_engaging_once()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1, grid: false);
        LeaderWithEnemy(0, leaderHeading: 0);
        Assert.Contains("o'clock", Say()!.Text); // the contact first

        LeaderWithEnemy(10, leaderHeading: 300);
        var call = Say()!;

        Assert.Equal("Number one, engaging, turning left. Follow me.", call.Text);
        Assert.Equal(["number.1", "engaging", "turn.left", "followme"],
            ClipPhraser.Phrase(call.Tokens!, key => key is "number.1" or "engaging" or "turn.left" or "turn.right" or "followme"));

        LeaderWithEnemy(20, leaderHeading: 200);
        Assert.DoesNotContain("engaging", Say()?.Text ?? ""); // once per break-off
    }

    // The game's pilots spot before our visibility model: in the 2026-10-26 flight the leader turned for the
    // Hurricanes before the flight was shown to see them. A turn that swings him onto a live enemy is engaging.
    [Fact]
    public void A_leader_who_turns_onto_an_enemy_the_flight_has_not_seen_is_engaging_and_the_enemy_is_remembered()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1, grid: false);
        LeaderAndEnemy(0, leaderHeading: 0, enemyNorth: 0, enemyEast: -10_000); // 10 km west, beyond sight
        Assert.Null(_calls.Consider(_tracker, Blind, _memory));
        Assert.DoesNotContain(EnemyCommands.EnemyContacts(_tracker, Blind, _memory).Replies, r => r.Members.Count > 0);

        LeaderAndEnemy(10, leaderHeading: 300, enemyNorth: 0, enemyEast: -10_000); // 60° left, onto it

        Assert.Equal("Number one, engaging, turning left. Follow me.", _calls.Consider(_tracker, Blind, _memory)!.Text);
        Assert.Contains(EnemyCommands.EnemyContacts(_tracker, Blind, _memory).Replies, r => r.Members.Count > 0); // he has seen it
    }

    // 2026-10-27: after Finch one was down, Finch two kept saying "follow me" from 5 km and beyond,
    // including while he taxied, and "engaging" for a fight that belonged to Finch three.
    [Fact]
    public void A_leader_who_has_left_every_mate_does_not_call_a_turn_or_a_heading()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1);
        Track(0, leaderHeading: 0, leaderNorth: 10_000);
        Track(6, leaderHeading: 0, leaderNorth: 10_000);
        Track(8, leaderHeading: 170, leaderNorth: 10_000);

        Assert.Null(Say());

        Track(20, leaderHeading: 180, leaderNorth: 10_000);
        Track(40, leaderHeading: 270, leaderNorth: 10_000);
        Track(46, leaderHeading: 272, leaderNorth: 10_000);
        Assert.Null(Say());
    }

    [Fact]
    public void A_leader_on_the_ground_does_not_call_a_turn()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1);
        Track(0, leaderHeading: 0, agl: 400);
        Track(6, leaderHeading: 0, agl: 400);
        Assert.Null(Say());

        Track(8, leaderHeading: 170, agl: 1);
        Assert.Null(Say());
        Track(46, leaderHeading: 180, agl: 1);
        Assert.Null(Say());
    }

    [Fact]
    public void A_logged_landing_stops_the_course_call()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1);
        Track(0, leaderHeading: 0);
        Track(6, leaderHeading: 0);
        _tracker.Apply(new LandingEvent(1, _leaderPlane, WorldPoint.FromIl2(0, 50, 0)));

        Track(12, leaderHeading: 170);
        Assert.Null(Say());
    }

    [Fact]
    public void A_turn_is_not_engaging_when_the_enemy_is_another_mates_fight()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1, grid: false);
        Flight("Kurt Meier", 3);
        void Look(double time, double leaderHeading)
        {
            _acmi.Feed($"#{time.ToString(CultureInfo.InvariantCulture)}\n"
                + Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0)
                + Acmi.Object("w", 0, 100, 1000, LeaderName, "Axis", heading: leaderHeading)
                + Acmi.Object("m", 0, 25_000, 1000, "Kurt Meier", "Axis", heading: 90)
                + Acmi.Object("e", 200, 25_000, 1000, "Pilot e", "Allies", "Yak-1 ser.69", heading: 90));
            _tracker.Apply(_acmi);
        }

        Look(0, 0);
        Say();
        Look(10, 90);

        var call = Say();
        Assert.Equal("Number one, turning right. Follow me.", call!.Text);
        Assert.DoesNotContain("engaging", call.Text);
    }

    [Fact]
    public void Lost_sight_is_not_called_when_the_player_is_far_from_the_leader()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1);
        Track(0, leaderHeading: 0, leaderNorth: -100);
        Assert.Null(_calls.Consider(_tracker, ShortSight, _memory));

        Track(10, leaderHeading: 0, leaderNorth: -12_000);
        Assert.Null(_calls.Consider(_tracker, ShortSight, _memory));
        Track(30, leaderHeading: 0, leaderNorth: -12_000);
        Assert.Null(_calls.Consider(_tracker, ShortSight, _memory));
    }

    [Fact]
    public void Lost_sight_is_not_called_by_a_leader_on_the_ground()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1);
        Track(0, leaderHeading: 0, leaderNorth: -100, agl: 400);
        Assert.Null(_calls.Consider(_tracker, ShortSight, _memory));

        Track(10, leaderHeading: 0, leaderNorth: -3_000, agl: 1);
        Assert.Null(_calls.Consider(_tracker, ShortSight, _memory));
        Track(30, leaderHeading: 0, leaderNorth: -3_000, agl: 1);
        Assert.Null(_calls.Consider(_tracker, ShortSight, _memory));
    }

    [Fact]
    public void A_leader_who_turns_away_from_the_only_enemy_is_not_engaging()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1, grid: false);
        LeaderAndEnemy(0, leaderHeading: 0, enemyNorth: 0, enemyEast: 10_000); // east
        _calls.Consider(_tracker, Blind, _memory);
        LeaderAndEnemy(10, leaderHeading: 300, enemyNorth: 0, enemyEast: 10_000); // left, away from it

        Assert.Equal("Number one, turning left. Follow me.", _calls.Consider(_tracker, Blind, _memory)!.Text);
    }

    [Fact]
    public void The_flight_says_once_when_it_loses_sight_of_a_player_out_in_front()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1);
        Track(0, leaderHeading: 0, leaderNorth: -100);
        Assert.Null(_calls.Consider(_tracker, ShortSight, _memory)); // with them, seen

        Track(10, leaderHeading: 0, leaderNorth: -3_000);
        Assert.Null(_calls.Consider(_tracker, ShortSight, _memory)); // one look unseen is not a loss
        Track(25, leaderHeading: 0, leaderNorth: -3_200);
        var lost = _calls.Consider(_tracker, ShortSight, _memory)!;
        Assert.Equal("Number one, we have lost sight of you. Heading north, altitude 1000 metres.", lost.Text);
        Assert.Contains(new SpeechToken(SpeechWord.CannotSee), lost.Tokens!);

        Track(35, leaderHeading: 0, leaderNorth: -3_400);
        Assert.Null(_calls.Consider(_tracker, ShortSight, _memory)); // once

        Track(40, leaderHeading: 0, leaderNorth: -100);
        _calls.Consider(_tracker, ShortSight, _memory); // seen again
        Track(50, leaderHeading: 0, leaderNorth: -3_000);
        _calls.Consider(_tracker, ShortSight, _memory);
        Track(75, leaderHeading: 0, leaderNorth: -3_000);
        Assert.StartsWith("Number one, we have lost sight of you.", _calls.Consider(_tracker, ShortSight, _memory)!.Text);
    }

    [Fact]
    public void A_briefed_corner_is_not_a_break_off()
    {
        RouteMission(playerFormation: 2, leaderFormation: 1);
        Frame(10, 100_000, 208_000, 90, Enemy(260_000, 100_000)); // an enemy far off, in sight
        Say();
        Frame(20, 100_000, 209_500, 150, Enemy(260_000, 100_000)); // 60° round toward the 180 leg

        Assert.DoesNotContain("Follow me", Say()?.Text ?? "");
    }

    [Fact]
    public void A_player_who_runs_ahead_of_the_flight_is_told_to_slow_down_and_rejoin()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1);
        Track(0, leaderHeading: 0, leaderNorth: -3_000);
        Assert.Null(Say());

        Track(10, leaderHeading: 0, leaderNorth: -3_000);
        var call = Say()!;
        Assert.Equal("Number one, you are a few kilometres ahead of us. Slow down and rejoin.", call.Text);
        Assert.Equal(["number.1", "rejoin"], ClipPhraser.Phrase(call.Tokens!, key => key is "number.1" or "rejoin"));

        Track(40, leaderHeading: 0, leaderNorth: -4_000);
        Assert.Null(Say()); // not every look

        Track(75, leaderHeading: 0, leaderNorth: -4_500);
        Assert.NotNull(Say()); // still ahead and not closing: again a minute on

        Track(140, leaderHeading: 0, leaderNorth: -3_500);
        Assert.Null(Say()); // closing: he is already doing it
    }

    [Fact]
    public void Ahead_is_only_said_to_a_wingman_flying_the_flights_way_in_front_of_it()
    {
        OpenFlight(playerFormation: 2, leaderFormation: 1);
        Track(0, leaderHeading: 0, playerHeading: 180, leaderNorth: -3_000);
        Track(10, leaderHeading: 0, playerHeading: 180, leaderNorth: -3_000);
        Assert.Null(Say()); // coming back to them
        Track(20, leaderHeading: 0, leaderNorth: 3_000);
        Track(30, leaderHeading: 0, leaderNorth: 3_000);
        Assert.Null(Say()); // behind them, not ahead

        OpenFlight(playerFormation: 1, leaderFormation: 2);
        Track(0, leaderHeading: 0, leaderNorth: -3_000);
        Track(10, leaderHeading: 0, leaderNorth: -3_000);
        Assert.Null(new UnaskedCalls().Consider(_tracker, See, _memory)); // he leads
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

    private void Track(double time, double leaderHeading, double playerHeading = 0, double leaderNorth = 0, double? agl = null)
    {
        _acmi.Feed($"#{time.ToString(CultureInfo.InvariantCulture)}\n"
            + Grid(PlayerName, "p", 100_000, 200_000, playerHeading, agl: agl)
            + Grid(LeaderName, "w", 100_000 + leaderNorth, 200_000, leaderHeading, agl: agl));
        _tracker.Apply(_acmi);
    }

    /// <summary>Player and leader side by side, the leader on <paramref name="leaderHeading"/>, a Yak 3 km east in sight.</summary>
    private void LeaderWithEnemy(double time, double leaderHeading)
    {
        _acmi.Feed($"#{time.ToString(CultureInfo.InvariantCulture)}\n"
            + Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0)
            + Acmi.Object("w", 0, 100, 1000, LeaderName, "Axis", heading: leaderHeading)
            + Acmi.Object("e", 0, 3_000, 1000, "Pilot e", "Allies", "Yak-1 ser.69", heading: 90));
        _tracker.Apply(_acmi);
    }

    private void LeaderAndEnemy(double time, double leaderHeading, double enemyNorth, double enemyEast)
    {
        _acmi.Feed($"#{time.ToString(CultureInfo.InvariantCulture)}\n"
            + Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0)
            + Acmi.Object("w", 0, 100, 1000, LeaderName, "Axis", heading: leaderHeading)
            + Acmi.Object("e", enemyNorth, enemyEast, 1000, "Pilot e", "Allies", "Yak-1 ser.69", heading: 90));
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

    private static string Grid(string pilot, string id, double north, double east, double heading, string coalition = "Axis", string name = "Bf 109 F-4", double? agl = null)
    {
        var (lat, lon) = MapGrid.StalingradLatLon(north, east);
        string height = agl is double a ? string.Create(CultureInfo.InvariantCulture, $",AGL={a:F1}") : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{id},T={lon:F6}|{lat:F6}|1000|0|0|{heading:F1}{height},Type=Air+FixedWing,Name={name},Pilot={pilot},Coalition={coalition}\n");
    }
}
