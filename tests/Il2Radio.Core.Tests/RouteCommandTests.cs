using Il2Radio.Core.Acmi;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.Memory;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>
/// "Where am I on the route", steer home and steer to target. The briefing is in mission metres, so a leg, a course
/// or a distance comes only from a memory fix (the same frame); an ACMI fix never gets one. The mission below has the
/// shape of a real Career sortie (names as IL-2 writes them in <c>_gen.mission</c>): take-off, a waypoint, an attack
/// area, then the landing waypoint over Tusov.
/// </summary>
public class RouteCommandTests
{
    private const string PlayerName = "Gert Knoll", LeaderName = "Richard Raub", MateName = "Enno Drecker";
    private const int Player = 1055743, Leader = 1001471, Mate = 1002495;
    private readonly ManualTimeProvider _clock = new();
    private ContactTracker _tracker = null!;

    private static readonly RadioSettings Pilot = new() { Realism = RadioRealism.Pilot };

    // Leg 1: east along 125000 N from 160000 E to 185000 E. Leg 2: south to the attack area at 105000 N.
    // Leg 3: north-west to the landing waypoint over Tusov.
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

    /// <summary>An escort: Career gives the flight only its landing waypoint (and a cover order, not parsed).</summary>
    private const string Escort = """
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

    /// <summary>
    /// The way Career really links a sortie (the 2026-10-26 and 2026-10-27 <c>_gen.mission</c> files): take-off to a
    /// waypoint to an attack area by direct links, a timer on to a patrol loop of attack areas, a counter-timer-counter
    /// exit from the loop to the way home, deactivate links pointing back, and an alternate attack area linked only
    /// onward. The direct-link chains alone are four separate pieces.
    /// </summary>
    private const string CareerLinks = """
        MCU_Waypoint
        {
          Index = 50;
          Name = "WAYPOINT-TAKEOFF";
          Targets = [51];
          Objects = [21];
          XPos = 120000;
          YPos = 500;
          ZPos = 158000;
        }
        MCU_Waypoint
        {
          Index = 51;
          Name = "CMD_Waypoint";
          Targets = [52,70];
          Objects = [21];
          XPos = 135000;
          YPos = 3000;
          ZPos = 165000;
        }
        MCU_Deactivate
        {
          Index = 70;
          Targets = [50];
          Objects = [];
        }
        MCU_Waypoint
        {
          Index = 52;
          Name = "CMD_AttackArea";
          Targets = [71,72];
          Objects = [21];
          XPos = 135000;
          YPos = 3000;
          ZPos = 185000;
        }
        MCU_Timer
        {
          Index = 71;
          Targets = [53];
          Objects = [];
        }
        MCU_Deactivate
        {
          Index = 72;
          Targets = [51];
          Objects = [];
        }
        MCU_Waypoint
        {
          Index = 53;
          Name = "CMD_AttackArea";
          Targets = [54,73];
          Objects = [21];
          XPos = 145000;
          YPos = 3000;
          ZPos = 195000;
        }
        MCU_Waypoint
        {
          Index = 54;
          Name = "CMD_AttackArea";
          Targets = [55];
          Objects = [21];
          XPos = 145000;
          YPos = 3000;
          ZPos = 205000;
        }
        MCU_Waypoint
        {
          Index = 55;
          Name = "CMD_AttackArea";
          Targets = [53];
          Objects = [21];
          XPos = 135000;
          YPos = 3000;
          ZPos = 205000;
        }
        MCU_Counter
        {
          Index = 73;
          Targets = [74];
          Objects = [];
        }
        MCU_Timer
        {
          Index = 74;
          Targets = [75];
          Objects = [];
        }
        MCU_Counter
        {
          Index = 75;
          Targets = [56];
          Objects = [];
        }
        MCU_Waypoint
        {
          Index = 56;
          Name = "CMD_Waypoint";
          Targets = [57];
          Objects = [21];
          XPos = 125000;
          YPos = 1000;
          ZPos = 190000;
        }
        MCU_Waypoint
        {
          Index = 57;
          Name = "WAYPOINT_LAND";
          Targets = [];
          Objects = [21];
          XPos = 119859.883;
          YPos = 500;
          ZPos = 157809.234;
        }
        MCU_Waypoint
        {
          Index = 58;
          Name = "CMD_AttackArea";
          Targets = [76];
          Objects = [21];
          XPos = 160000;
          YPos = 3000;
          ZPos = 190000;
        }
        MCU_Timer
        {
          Index = 76;
          Targets = [57];
          Objects = [];
        }
        """;

    public RouteCommandTests() => Open(Sweep);

    private void Open(string waypoints, int playerFormation = 2, int leaderFormation = 1, string map = "stalingrad-summer-1942")
    {
        _tracker = new ContactTracker(_clock);
        _tracker.SetPlan(MissionPlan.FromBlocks(MissionFileParser.Parse(new StringReader($$"""
            Options
            {
              GuiMap = "{{map}}";
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
              StartInAir = 1;
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
              StartInAir = 1;
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
              Callsign = 30;
              XPos = 119604.523;
              YPos = 55.385;
              ZPos = 157026.375;
              Chart
              {
              }
            }
            Airfield
            {
              Name = "Pitomnik";
              Index = 41;
              Country = 201;
              XPos = 135349.672;
              YPos = 140;
              ZPos = 234219.172;
            }
            Airfield
            {
              Name = "Soviet strip";
              Index = 42;
              Country = 101;
              XPos = 119900;
              YPos = 60;
              ZPos = 158000;
            }
            """))));
        LogPlane(Player, PlayerName, playerFormation, isPlayer: true);
        LogPlane(Leader, LeaderName, leaderFormation);
        LogPlane(Mate, MateName, 3);
    }

    private void LogPlane(int id, string pilot, int formation, bool isPlayer = false)
    {
        var origin = WorldPoint.FromIl2(119848, 57, 157764);
        _tracker.Apply(new ObjectSpawnedEvent(0, id, "Bf 109 F-4", 201, pilot, -1, origin));
        _tracker.Apply(new ObjectSpawnedEvent(0, id + 1024, "BotPilot", 201, "BotPilot", id, origin));
        _tracker.Apply(new PlaneSpawnedEvent(0, id, id + 1024, origin, "Bf 109 F-4", 201, formation, 1, 0, -1, isPlayer, ""));
    }

    private void Read(double time, params MemoryAircraft[] aircraft) => _tracker.Apply(aircraft, time);

    private static MemoryAircraft At(int id, double north, double east, double height = 3000) => new(id, north, height, east, 120);

    /// <summary>The player on leg 1, 10 km short of its end, with his leader 300 m off his wing.</summary>
    private void OnLegOne(double leaderNorth = 125_500, double leaderEast = 175_300) =>
        Read(10, At(Player, 125_500, 175_000), At(Leader, leaderNorth, leaderEast));

    private static RadioReply Only(RadioResult result)
    {
        Assert.Equal(ReplyOutcome.Position, result.Outcome);
        return Assert.Single(result.Replies);
    }

    [Fact]
    public void The_leader_flying_with_the_player_gives_square_leg_and_next_waypoint()
    {
        OnLegOne();

        var reply = Only(RouteCommands.RoutePosition(_tracker, Pilot));

        Assert.Equal("Number one, grid 1118-5, leg one of three. Next waypoint, heading 095, 10 kilometres.", reply.Text);
        Assert.Equal(Leader, reply.Contact.LogId);
        Assert.Null(reply.SpeakerLabel);
        Assert.Equal(
            [SpeechWord.Speaker, SpeechWord.Grid, SpeechWord.Leg, SpeechWord.Waypoint, SpeechWord.Course, SpeechWord.Range],
            reply.Tokens!.Select(t => t.Word));
        Assert.Equal(95, reply.Tokens!.Single(t => t.Word == SpeechWord.Course).Value);
        // The headset: his number, then the compass point of the course. Grid and leg have no recording.
        Assert.Equal(["number.1", "compass.e", "range.10"],
            Audio.ClipPhraser.Phrase(reply.Tokens!, new[] { "number.1", "compass.e", "range.10", "range.far" }.Contains));
    }

    [Fact]
    public void When_the_player_leads_it_is_his_own_map_reading_on_the_chat_only()
    {
        Open(Sweep, playerFormation: 1, leaderFormation: 2);
        OnLegOne();

        var reply = Only(RouteCommands.RoutePosition(_tracker, Pilot));

        Assert.Equal("Route, grid 1118-5, leg one of three. Next waypoint, heading 095, 10 kilometres.", reply.Text);
        Assert.Equal("Route", reply.SpeakerLabel);
        Assert.True(reply.Contact.IsPlayer); // the session never puts the player's own lines on the headset
        var line = RadioChat.Line(reply);
        Assert.Equal("[Route]", line.Speaker);
        Assert.Equal("Grid 1118-5, leg one of three. Next waypoint, heading 095, 10 kilometres.", line.Text);
    }

    [Fact]
    public void A_leader_who_cannot_see_the_player_does_not_answer_under_pilot_realism()
    {
        OnLegOne(leaderNorth: 140_000, leaderEast: 175_000); // 14.5 km away: beyond any look at a fighter

        var pilot = RouteCommands.RoutePosition(_tracker, Pilot);
        var reply = Only(pilot);
        Assert.Equal("Route", reply.SpeakerLabel);
        Assert.Contains(pilot.Suppressed, s => s.Contact.LogId == Leader && s.Reason.Contains("cannot see you"));

        // Precise: he is on the frequency, so he answers, as in every other precise reply.
        Assert.StartsWith("Number one, grid 1118-5", Only(RouteCommands.RoutePosition(_tracker, new RadioSettings())).Text);
    }

    [Fact]
    public void A_destroyed_leader_never_answers_and_the_next_living_number_leads()
    {
        Read(10, At(Player, 125_500, 175_000), At(Leader, 125_500, 175_300), At(Mate, 125_500, 174_700));
        _tracker.Apply(new KillEvent(500, -1, Leader, WorldPoint.FromIl2(125_500, 3000, 175_300)));
        Read(11, At(Player, 125_500, 175_000), At(Leader, 125_400, 175_300, 2500), At(Mate, 125_500, 174_700));

        foreach (var result in new[] { RouteCommands.RoutePosition(_tracker, Pilot), RouteCommands.SteerHome(_tracker, Pilot), RouteCommands.SteerTarget(_tracker, Pilot) })
        {
            var reply = Only(result);
            Assert.NotEqual(Leader, reply.Contact.LogId);
            Assert.Equal("Route", reply.SpeakerLabel); // the player (number two) now leads number three
            Assert.Contains(result.Suppressed, s => s.Contact.LogId == Leader && s.Reason.Contains("Destroyed"));
        }
    }

    [Fact]
    public void Off_the_route_he_says_how_far_and_the_nearest_leg()
    {
        Read(10, At(Player, 145_000, 175_000), At(Leader, 145_000, 175_300));

        Assert.Equal("Number one, grid 0918-5, 20 kilometres off the route, nearest leg one of three. Next waypoint, heading 155, 22 kilometres.",
            Only(RouteCommands.RoutePosition(_tracker, Pilot)).Text);
    }

    [Fact]
    public void At_a_waypoint_the_next_leg_is_the_one_he_is_on()
    {
        Read(10, At(Player, 125_000, 184_800), At(Leader, 125_100, 184_600)); // 200 m short of the corner

        Assert.Equal("Number one, grid 1119-5, leg two of three. Next waypoint, attack area, heading 180, 20 kilometres.",
            Only(RouteCommands.RoutePosition(_tracker, Pilot)).Text);
    }

    [Fact]
    public void Within_a_kilometre_of_a_grid_line_the_square_is_left_out()
    {
        Read(10, At(Player, 130_300, 175_000), At(Leader, 130_300, 175_300)); // 300 m north of the 130 km line

        Assert.Equal("Number one, leg one of three. Next waypoint, heading 120, 11 kilometres.",
            Only(RouteCommands.RoutePosition(_tracker, Pilot)).Text);
    }

    [Fact]
    public void Steer_home_is_to_his_own_sides_field_by_the_landing_waypoint()
    {
        OnLegOne();

        var reply = Only(RouteCommands.SteerHome(_tracker, Pilot));

        // Tusov, not Pitomnik (farther from the landing waypoint) nor the Soviet strip beside it.
        Assert.Equal("Number one, Tusov, heading 250, 19 kilometres.", reply.Text);
        Assert.Equal([SpeechWord.Speaker, SpeechWord.Destination, SpeechWord.Course, SpeechWord.Range], reply.Tokens!.Select(t => t.Word));
        Assert.Equal("Tusov", reply.Tokens![1].Name);
    }

    [Fact]
    public void Steer_to_target_is_the_next_attack_area_ahead()
    {
        OnLegOne();

        Assert.Equal("Number one, target area, heading 155, 23 kilometres.", Only(RouteCommands.SteerTarget(_tracker, Pilot)).Text);
    }

    [Fact]
    public void An_escort_has_no_target_area_and_says_so_but_still_has_a_home()
    {
        Open(Escort);
        OnLegOne();

        Assert.Equal("Number one, no target area in the briefing.", Only(RouteCommands.SteerTarget(_tracker, Pilot)).Text);
        Assert.Equal("Number one, Tusov, heading 250, 19 kilometres.", Only(RouteCommands.SteerHome(_tracker, Pilot)).Text);
        Assert.Equal("Number one, grid 1118-5. Next waypoint, landing, heading 250, 18 kilometres.", Only(RouteCommands.RoutePosition(_tracker, Pilot)).Text);
    }

    [Fact]
    public void No_steer_is_measured_from_a_track_recording_fix()
    {
        // The same flight, positions from the ACMI only: another frame than the briefing.
        var acmi = new AcmiStream();
        acmi.Feed(Acmi.Header + "#10\n" + Acmi.Object("p", 0, 0, 3000, PlayerName, "Axis", heading: 90)
                  + Acmi.Object("l", 0, 300, 3000, LeaderName, "Axis", heading: 90));
        acmi.Feed("#11\n");
        _tracker.Apply(acmi);
        Assert.Equal(CoordinateFrame.Acmi, _tracker.Player!.LastFix!.Position.Frame);

        foreach (var result in new[] { RouteCommands.SteerHome(_tracker, Pilot), RouteCommands.SteerTarget(_tracker, Pilot) })
        {
            Assert.Equal(ReplyOutcome.NoCourse, result.Outcome);
            Assert.Empty(result.Replies);
            Assert.Contains(result.Suppressed, s => s.Contact.IsPlayer && s.Reason.Contains("track recording"));
            Assert.Equal("no steer: only IL-2's memory puts you on the briefing map; the track recording is another frame",
                Assert.Single(RadioChat.Replies(result)).Text);
        }

        // The route call can still name the square (the same fit every square uses), never a leg or a course.
        var route = RouteCommands.RoutePosition(_tracker, Pilot);
        Assert.Equal(ReplyOutcome.NoCourse, route.Outcome);
        Assert.All(route.Replies, r =>
        {
            Assert.Matches(@"^Number one, grid \d{4}(-\d)?\.$", r.Text);
            Assert.DoesNotContain(r.Tokens!, t => t.Word is SpeechWord.Course or SpeechWord.Range or SpeechWord.Leg);
        });
    }

    [Fact]
    public void A_stale_memory_fix_gives_no_answer()
    {
        OnLegOne();
        _clock.Advance(TimeSpan.FromSeconds(15)); // memory stopped being read

        Assert.Equal(ReplyOutcome.PlayerPositionUnknown, RouteCommands.RoutePosition(_tracker, Pilot).Outcome);
        Assert.Equal(ReplyOutcome.PlayerPositionUnknown, RouteCommands.SteerHome(_tracker, Pilot).Outcome);
        Assert.Equal(ReplyOutcome.PlayerPositionUnknown, RouteCommands.SteerTarget(_tracker, Pilot).Outcome);
    }

    [Fact]
    public void Off_the_stalingrad_map_memory_still_gives_leg_and_steer_but_no_square()
    {
        Open(Sweep, map: "moscow-winter-1941");
        OnLegOne();

        Assert.Equal("Number one, leg one of three. Next waypoint, heading 095, 10 kilometres.", Only(RouteCommands.RoutePosition(_tracker, Pilot)).Text);
    }

    [Fact]
    public void The_commands_are_in_the_flight_menu_and_dispatch()
    {
        OnLegOne();

        Assert.StartsWith("Number one, Tusov", Only(RadioCommands.Execute(RadioCommand.SteerHome, _tracker, Pilot)).Text);
        Assert.Equal("Flight, request steer home.", RadioChat.RequestPhrase(RadioCommand.SteerHome));
        var flight = RadioMenu.Default[0];
        Assert.Equal([RadioCommand.FlightPosition, RadioCommand.Rejoin, RadioCommand.RoutePosition, RadioCommand.SteerHome, RadioCommand.SteerTarget],
            flight.Children!.Select(c => c.Command!.Value));
    }

    [Fact]
    public void The_route_corner_call_works_from_memory_positions()
    {
        // Before 2026-10-28 the corner was found only from an ACMI latitude, so memory positions never gave one.
        Open(Sweep, playerFormation: 1, leaderFormation: 2);
        Read(10, At(Player, 125_000, 182_000), At(Leader, 125_000, 181_700));
        Read(11, At(Player, 125_000, 182_500), At(Leader, 125_000, 182_200)); // flying east, 2.5 km short of the corner

        var call = new UnaskedCalls().Consider(_tracker, Pilot, new SightingMemory());

        Assert.Equal("Route, turning to heading 180.", call?.Text);
    }

    [Fact]
    public void A_career_sortie_is_read_in_briefed_order_through_its_timers_not_its_back_links()
    {
        Open(CareerLinks);
        var plan = _tracker.Plan!;

        Assert.True(plan.Waypoints[10].Count > 1); // the direct links alone break it into pieces
        Assert.Equal(
            [(120000.0, 158000.0), (135000.0, 165000.0), (135000.0, 185000.0), (145000.0, 195000.0), (145000.0, 205000.0), (135000.0, 205000.0),
             (125000.0, 190000.0), (119859.883, 157809.234)],
            plan.BriefedRoutes[10].Select(w => (w.Position.North, w.Position.East)));
        Assert.DoesNotContain(plan.BriefedRoutes[10], w => w.Position.North == 160000); // the alternate attack area
    }

    [Fact]
    public void On_a_career_patrol_loop_the_leg_counts_along_the_briefed_order()
    {
        Open(CareerLinks);
        Read(10, At(Player, 145_400, 197_500), At(Leader, 145_400, 197_800));

        Assert.Equal("Number one, grid 0920-6, leg four of seven. Next waypoint, attack area, heading 095, 8 kilometres.",
            Only(RouteCommands.RoutePosition(_tracker, Pilot)).Text);
        Assert.Equal("Number one, target area, heading 095, 8 kilometres.", Only(RouteCommands.SteerTarget(_tracker, Pilot)).Text);
    }

    [Fact]
    public void Named_waypoints_and_airfields_are_read_from_the_mission()
    {
        var plan = _tracker.Plan!;
        var chain = Assert.Single(plan.Waypoints[10]);
        Assert.Equal(["WAYPOINT-TAKEOFF", "CMD_Waypoint", "CMD_AttackArea", "WAYPOINT_LAND"], chain.Select(w => w.Name));
        Assert.Equal([false, false, true, false], chain.Select(w => w.IsAttack));
        Assert.True(chain[^1].IsLanding);
        Assert.Equal(chain.Select(w => w.Position), Assert.Single(plan.Routes[10]));
        Assert.Equal(["Tusov", "Pitomnik", "Soviet strip"], plan.Airfields.Select(a => a.Name));
        Assert.Equal(CoordinateFrame.Il2World, plan.Airfields[0].Position.Frame);
    }
}
