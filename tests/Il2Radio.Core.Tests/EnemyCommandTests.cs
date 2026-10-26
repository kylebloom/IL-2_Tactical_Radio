using Il2Radio.Core.Acmi;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>
/// Commands 2 (enemy contacts) and 4 (identify). Spotting is probabilistic, so most tests use
/// <see cref="SureSpotting"/> (detection effectively certain at these ranges) and exercise the
/// deterministic identification ranges: fighter-sized (10 m span) type ≈ 1.8 km, side ≈ 3 km, class ≈ 4 km.
/// </summary>
public class EnemyCommandTests
{
    private const string PlayerName = "Gert Knoll";
    private readonly ManualTimeProvider _clock = new();
    private readonly AcmiStream _acmi = new();
    private ContactTracker _tracker = null!;
    private int _nextId = 1000;

    private static readonly RadioSettings SureSpotting = new()
    {
        Visibility = new VisibilitySettings { DetectMetresPerSpanMetre = 100_000, MaxDetectFactor = 100, MisidentificationChance = 0 },
    };

    public EnemyCommandTests() => Start("");

    /// <summary>Overcast deck from 1500 to 2300 m; one option per line, as in a real .Mission file.</summary>
    private const string Overcast = """
        CloudLevel = 1500;
        CloudHeight = 800;
        CloudConfig = "summer\04_Overcast_01\sky.ini";
        """;

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
        _acmi.Feed(Acmi.Header + "#0\n");
        Flight(PlayerName, 4, isPlayer: true);
        Flight("Heinz Koelzig", 1);
    }

    private void Flight(string pilot, int formationIndex, bool isPlayer = false)
    {
        long plane = _nextId++, bot = _nextId++;
        var origin = WorldPoint.FromIl2(0, 50, 0);
        _tracker.Apply(new ObjectSpawnedEvent(0, plane, "Bf 109 F-4", 201, pilot, -1, origin));
        _tracker.Apply(new ObjectSpawnedEvent(0, bot, "BotPilot_Bf109_GER41", 201, "BotPilot_Bf109_GER41", plane, origin));
        _tracker.Apply(new PlaneSpawnedEvent(0, plane, bot, origin, "Bf 109 F-4", 201, formationIndex, 1, 0, -1, isPlayer, ""));
    }

    /// <summary>Player at the origin heading north at 1000 m, wingman 100 m to his right.</summary>
    private void Frame(double time, params string[] others)
    {
        _acmi.Feed($"#{time}\n" + Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0)
                   + Acmi.Object("w", 0, 100, 1000, "Heinz Koelzig", "Axis", heading: 0) + string.Concat(others));
        _tracker.Apply(_acmi);
    }

    private static string Enemy(string id, double north, double east, double alt = 1000, string model = "Yak-1 ser.69", double heading = 90) =>
        Acmi.Object(id, north, east, alt, $"Pilot {id}", "Allies", model, heading);

    private string Contacts(RadioSettings? s = null, SightingMemory? memory = null) =>
        string.Join(" | ", EnemyCommands.EnemyContacts(_tracker, s ?? SureSpotting, memory).Replies.Select(r => r.Text));

    private string Identify(RadioSettings? s = null, SightingMemory? memory = null) =>
        Assert.Single(EnemyCommands.Identify(_tracker, s ?? SureSpotting, memory).Replies).Text;

    [Fact]
    public void Close_enemy_is_identified_by_type()
    {
        Frame(10, Enemy("e1", 0, 600));

        Assert.Equal("Number one, single Yak, at your three o'clock, under two kilometres, level.", Contacts());
    }

    [Fact]
    public void Identification_degrades_with_range()
    {
        Frame(10, Enemy("e1", 2600, 0));   // inside side range
        Assert.Equal("Number one, single enemy fighter, at your twelve o'clock, approximately 3 kilometres, level.", Contacts());

        Start("");
        Frame(10, Enemy("e1", 3600, 0));   // class range only
        Assert.Equal("Number one, single unidentified fighter, at your twelve o'clock, approximately 4 kilometres, level.", Contacts());

        Start("");
        Frame(10, Enemy("e1", 6000, 0));   // spotted, nothing more
        Assert.Equal("Number one, single unidentified aircraft, at your twelve o'clock, approximately 6 kilometres, level.", Contacts());
    }

    [Fact]
    public void Bombers_are_recognised_further_out_than_fighters()
    {
        Frame(10, Enemy("b1", 0, -4800, alt: 2000, model: "Pe-2 ser.87"));  // markings visible at ~4.9 km
        Assert.Equal("Number one, single enemy bomber, at your nine o'clock, approximately 5 kilometres, high.", Contacts());

        Start("");
        Frame(10, Enemy("b1", 0, -6000, alt: 3000, model: "Pe-2 ser.87"));  // ~6.3 km: shape, not markings
        Assert.Equal("Number one, single unidentified bomber, at your nine o'clock, approximately 6 kilometres, high.", Contacts());
    }

    [Fact]
    public void Nothing_beyond_visual_range_is_reported()
    {
        Frame(10, Enemy("e1", 0, 30_000));

        Assert.Equal("Number one, no contacts.", Contacts(new RadioSettings()));
    }

    [Fact]
    public void Overcast_between_observer_and_target_hides_it()
    {
        Start(Overcast);
        Frame(10, Enemy("e1", 600, 0, alt: 3000));

        Assert.Equal("Number one, no contacts.", Contacts());
    }

    [Fact]
    public void Partial_cloud_does_not_hide_an_aircraft_on_the_same_side_of_the_layer()
    {
        Start(Overcast);
        Frame(10, Enemy("e1", 600, 0, alt: 1100));

        Assert.StartsWith("Number one, single Yak", Contacts());
    }

    [Fact]
    public void Darkness_shortens_identification()
    {
        Start("Time = 23:30:0;");
        Frame(10, Enemy("e1", 600, 0));

        Assert.Equal("Number one, single unidentified fighter, at your twelve o'clock, under two kilometres, level.", Contacts());
    }

    [Fact]
    public void Identified_friendlies_are_not_reported_as_contacts_but_can_be_identified()
    {
        Frame(10, Acmi.Object("f1", 0, 800, 1000, "Other Unit", "Axis", "Bf 109 F-4", heading: 180));

        Assert.Equal("Number one, no contacts.", Contacts());
        Assert.Equal("Number one, contact at your three o'clock, under two kilometres: a Messerschmitt, friendly, heading south.", Identify());
    }

    [Fact]
    public void Unidentified_friendlies_are_reported_as_unidentified()
    {
        Frame(10, Acmi.Object("f1", 3600, 0, 1000, "Other Unit", "Axis", "Bf 109 F-4"));

        Assert.Contains("unidentified fighter", Contacts());
    }

    [Fact]
    public void Contacts_flying_together_are_counted_and_grouped()
    {
        Frame(10, Enemy("e1", 0, 600), Enemy("e2", 100, 650), Enemy("e3", -100, 700), Enemy("x", 0, -4600, alt: 4000, model: "Il-2 mod.1942"));

        string text = Contacts();

        Assert.Equal("Number one, three Yaks, at your three o'clock, under two kilometres, level. | " +
                     "Number one, single unidentified ground-attack aircraft, at your nine o'clock, approximately 5 kilometres, high.", text);
    }

    [Fact]
    public void One_clear_look_at_a_group_member_settles_the_call()
    {
        // e1 is close (confident); e2 trails at the edge of type range (would be hedged on its own).
        Frame(10, Enemy("e1", 0, 400), Enemy("e2", 0, 1700));

        Assert.StartsWith("Number one, pair of Yaks", Contacts());
    }

    [Fact]
    public void Identify_names_type_and_heading_like_the_user_story()
    {
        Frame(10, Enemy("e1", 300, 600, model: "Bf 109 G-2", heading: 92));

        Assert.Equal("Number one, contact at your two o'clock, under two kilometres: a Messerschmitt, enemy, heading east.", Identify());
    }

    [Fact]
    public void Identify_says_too_far_when_only_a_speck_is_visible()
    {
        Frame(10, Enemy("e1", 7000, 0));

        Assert.Equal("Number one, contact at your twelve o'clock, approximately 7 kilometres: too far to identify, heading east.", Identify());
    }

    [Fact]
    public void Identification_is_remembered_between_calls()
    {
        var memory = new SightingMemory();
        Frame(10, Enemy("e1", 0, 600));
        Assert.StartsWith("Number one, single Yak", Contacts(memory: memory));

        Frame(20, Enemy("e1", 3600, 0)); // now only at class range, but already identified
        Assert.StartsWith("Number one, single Yak", Contacts(memory: memory));
        Assert.StartsWith("Number one, single unidentified fighter", Contacts()); // a fresh memory would not know
    }

    [Fact]
    public void Edge_of_range_misidentification_is_hedged_and_corrected_up_close()
    {
        var memory = new SightingMemory();
        var settings = SureSpotting with { Visibility = SureSpotting.Visibility with { MisidentificationChance = 1 } };

        Frame(10, Enemy("e1", 1300, 0));
        Assert.StartsWith("Number one, single possible Messerschmitt", Contacts(settings, memory));

        Frame(20, Enemy("e1", 300, 0));
        Assert.StartsWith("Number one, single Yak", Contacts(settings, memory));
    }

    [Fact]
    public void A_glimpse_is_reported_as_last_seen_until_the_minute_is_up()
    {
        var memory = new SightingMemory();
        var settings = new RadioSettings { Realism = RadioRealism.Pilot };
        // The close pass is only in an earlier frame. Nothing asks the radio while it is happening.
        _acmi.FrameClosed = stream =>
        {
            _tracker.Apply(stream);
            EnemyCommands.Notice(_tracker, settings, memory);
        };

        Frame(10, Enemy("e1", 0, 600));
        Frame(25, Enemy("e1", 0, 30_000));

        Assert.Equal("Number one, single Yak, last seen at your three o'clock, close, level.", Contacts(settings, memory));
        Assert.Equal("Number one, contact last seen at your three o'clock, close: a Yak, enemy, heading east.", Identify(settings, memory));

        Frame(80, Enemy("e1", 0, 30_000));
        Assert.Equal("Number one, no contacts.", Contacts(settings, memory));
    }

    [Fact]
    public void Removed_aircraft_are_not_reported()
    {
        Frame(10, Enemy("e1", 0, 600));
        _acmi.Feed("#11\n-e1\n");
        Frame(12);

        Assert.Equal("Number one, no contacts.", Contacts());
    }

    [Fact]
    public void The_players_own_flight_is_never_a_contact()
    {
        Frame(10);

        Assert.Equal("Number one, no contacts.", Contacts());
        Assert.Equal("Number one, no contacts to identify.", Identify());
    }

    [Fact]
    public void No_live_player_position_means_no_reply()
    {
        Assert.Equal(ReplyOutcome.PlayerPositionUnknown, EnemyCommands.EnemyContacts(_tracker, SureSpotting).Outcome);
    }

    [Fact]
    public void Spotting_far_out_is_a_matter_of_chance_and_stable_within_a_slot()
    {
        // ~6 km from a fighter-sized target: well under certain with the default model.
        var outcomes = Enumerable.Range(0, 40).Select(i =>
        {
            Start("");
            Frame(i * 15 + 1, Enemy($"e{i}", 6500, 0));
            return Contacts(new RadioSettings()).Contains("unidentified");
        }).ToList();

        Assert.Contains(true, outcomes);
        Assert.Contains(false, outcomes);

        Start("");
        Frame(10, Enemy("same", 6500, 0));
        string first = Contacts(new RadioSettings());
        Assert.Equal(first, Contacts(new RadioSettings()));
    }

    [Fact]
    public void Type_level_calls_are_spoken_with_game_clips_using_the_class_word()
    {
        // Clips a German game pack has: no aircraft type names, no "no contacts".
        string[] pack = ["number.1", "pair", "group", "class.fighter", "class.fighters", "clock.3", "range.close", "range.1", "vertical.level", "vertical.above"];
        Frame(10, Enemy("e1", 0, 600), Enemy("e2", 50, 650));

        var contacts = Assert.Single(EnemyCommands.EnemyContacts(_tracker, SureSpotting).Replies);
        var identify = Assert.Single(EnemyCommands.Identify(_tracker, SureSpotting).Replies);

        Assert.Equal("Number one, pair of Yaks, at your three o'clock, under two kilometres, level.", contacts.Text);
        Assert.Equal(["number.1", "pair", "class.fighters", "clock.3", "range.close", "vertical.level"],
            Audio.ClipPhraser.Phrase(contacts.Tokens!, pack.Contains));
        Assert.Equal(["number.1", "pair", "class.fighters", "clock.3", "range.close", "vertical.level"],
            Audio.ClipPhraser.Phrase(identify.Tokens!, pack.Contains));
    }

    [Fact]
    public void Type_level_calls_carry_the_type_and_side_and_the_headset_says_the_class_word()
    {
        string[] pack = ["number.1", "pair", "group", "class.fighter", "class.fighters", "clock.3", "range.close", "range.1", "vertical.level", "vertical.above"];
        Frame(10, Enemy("e1", 0, 600), Enemy("e2", 50, 650));

        var contacts = Assert.Single(EnemyCommands.EnemyContacts(_tracker, SureSpotting).Replies);
        var identify = Assert.Single(EnemyCommands.Identify(_tracker, SureSpotting).Replies);

        Assert.Contains(new SpeechToken(SpeechWord.ContactType, 2, "Yak"), contacts.Tokens!);
        Assert.DoesNotContain(contacts.Tokens!, t => t.Word == SpeechWord.ContactSide); // "pair of Yaks": the text names no side
        Assert.Equal(["number.1", "pair", "class.fighters", "clock.3", "range.close", "vertical.level"],
            Audio.ClipPhraser.Phrase(contacts.Tokens!, pack.Contains));
        Assert.Contains("two Yaks, enemy", identify.Text);
        Assert.Equal(["number.1", "pair", "class.fighters", "clock.3", "range.close", "vertical.level"],
            Audio.ClipPhraser.Phrase(identify.Tokens!, pack.Contains)); // "Yaks" and "enemy" stay on the chat
    }

    [Fact]
    public void Side_level_calls_say_the_side_but_never_a_type()
    {
        Frame(10, Enemy("e1", 0, 2_600)); // fighter at 2.6 km: side (3 km) but not type (1.8 km)

        var contacts = Assert.Single(EnemyCommands.EnemyContacts(_tracker, SureSpotting).Replies);

        Assert.Contains("enemy fighter", contacts.Text);
        Assert.Contains(new SpeechToken(SpeechWord.ContactSide, -1), contacts.Tokens!);
        Assert.DoesNotContain(contacts.Tokens!, t => t.Word == SpeechWord.ContactType);
    }

    [Fact]
    public void No_contacts_is_the_acknowledgement_or_the_number_alone()
    {
        Frame(10);
        var reply = Assert.Single(EnemyCommands.EnemyContacts(_tracker, SureSpotting).Replies);

        string[] pack = ["number.1"];
        Assert.Equal(["number.1"], Audio.ClipPhraser.Phrase(reply.Tokens!, pack.Contains));
        string[] withAck = [.. pack, "ack"];
        Assert.Equal(["number.1", "ack"], Audio.ClipPhraser.Phrase(reply.Tokens!, withAck.Contains));
    }

    [Theory]
    [InlineData("Bf 109 F-4", "Messerschmitt", AircraftClass.Fighter)]
    [InlineData("Yak-1 ser.69", "Yak", AircraftClass.Fighter)]
    [InlineData("Il-2 mod.1942", "Sturmovik", AircraftClass.Attack)]
    [InlineData("He 111 H-6", "Heinkel", AircraftClass.Bomber)]
    [InlineData("I-16 type 24", "Rata", AircraftClass.Fighter)]
    [InlineData("Something new", "aircraft", AircraftClass.Unknown)]
    public void Aircraft_types_map_to_radio_vocabulary(string model, string family, AircraftClass cls)
    {
        var t = AircraftTypes.Classify(model);
        Assert.Equal((family, cls), (t.Family, t.Class));
    }

    [Theory]
    [InlineData(@"summer\00_Clear_05\sky.ini", 0.0)]
    [InlineData(@"summer\02_Medium_03\sky.ini", 0.5)]
    [InlineData(@"winter\04_Overcast_01\sky.ini", 1.0)]
    [InlineData("", 0.0)]
    public void Cloud_coverage_comes_from_the_preset_name(string config, double expected) =>
        Assert.Equal(expected, MissionWeather.CoverageOf(config));
}
