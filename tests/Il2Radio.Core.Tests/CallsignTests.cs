using Il2Radio.Core.Acmi;
using Il2Radio.Core.Audio;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>Career callsigns: the mission plan's Callsign/Callnum, the install's callsign words, and their use in replies.</summary>
public class CallsignTests
{
    private const string PlayerName = "Gert Knoll";
    private const string Plan = """
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
          Name = "Heinz Koelzig";
          Country = 201;
          AILevel = 3;
          Callsign = 8;
          Callnum = 1;
        }
        Plane
        {
          Name = "Rudi Liebgott";
          Country = 201;
          AILevel = 2;
          Callsign = 8;
          Callnum = 2;
        }
        Plane
        {
          Name = "Gert Knoll";
          Country = 201;
          AILevel = 0;
          Callsign = 8;
          Callnum = 4;
        }
        Plane
        {
          Name = "Bert Lange,201090,0";
          Country = 201;
          AILevel = 4;
          Callsign = 12;
          Callnum = 1;
        }
        """;

    private static readonly Dictionary<int, string> Names = new() { [8] = "Oriole", [12] = "Eagle" };

    private readonly ContactTracker _tracker = new(new ManualTimeProvider());
    private readonly AcmiStream _acmi = new();
    private int _nextId = 1000;

    // Career writes the player's flight-mates as "\u0001Name" (seen in a real _gen.Mission).
    private static MissionPlan LoadPlan(IReadOnlyDictionary<int, string> names) =>
        MissionPlan.FromBlocks(MissionFileParser.Parse(new StringReader(Plan.Replace("\"Rudi Liebgott\"", "\"\u0001Rudi Liebgott\"")))) with { CallsignNames = names };

    private void Start(IReadOnlyDictionary<int, string> names)
    {
        _tracker.SetPlan(LoadPlan(names));
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

    private void Frame(params string[] objects)
    {
        _acmi.Feed("#10\n" + string.Concat(objects));
        _tracker.Apply(_acmi);
    }

    private static string Player() => Acmi.Object("p", 0, 0, 1000, PlayerName, "Axis", heading: 0);

    private static string Mate(string id, string pilot, double north, double east) => Acmi.Object(id, north, east, 3000, pilot, "Axis", heading: 90);

    [Fact]
    public void Flight_mates_answer_with_their_career_callsign()
    {
        Start(Names);
        LogFlightMember("Heinz Koelzig", 1);
        LogFlightMember("Rudi Liebgott", 2);
        Frame(Player(), Mate("a", "Heinz Koelzig", 0, 17_000), Mate("b", "Rudi Liebgott", 200, 17_100));

        var position = Assert.Single(FlightCommands.FlightPosition(_tracker).Replies);
        Assert.Equal("Oriole one, flight of two at your three o'clock, approximately 17 kilometres, 2000 metres above you.", position.Text);
        Assert.Equal([new(SpeechWord.Callsign, 8), new SpeechToken(SpeechWord.Speaker, 1)], position.Tokens!.Take(2));

        Assert.StartsWith("Oriole one, steer zero-nine-zero", Assert.Single(FlightCommands.Rejoin(_tracker).Replies).Text);
        var rudi = _tracker.Contacts.Single(c => c.PilotName == "Rudi Liebgott"); // planned as "\u0001Rudi Liebgott"
        Assert.Equal("Oriole", rudi.CallsignName);
        Assert.Equal(2, rudi.Callnum);
        Assert.StartsWith("Oriole one, two aircraft together", Assert.Single(FriendlyPositionsCommand.Execute(_tracker).Replies).Text);
    }

    [Fact]
    public void Pilot_of_another_flight_answers_with_his_callsign_when_seen_only_in_the_acmi()
    {
        Start(Names);
        Frame(Player(), Acmi.Object("x", 0, 3000, 1000, "Bert Lange", "Axis"));

        var reply = Assert.Single(FriendlyPositionsCommand.Execute(_tracker).Replies);

        Assert.Equal("Eagle one, bearing zero-nine-zero, approximately 3 kilometres, altitude 1000 metres.", reply.Text);
        Assert.Equal(12, reply.Contact.Callsign);
        Assert.Equal(1, reply.Contact.Callnum);
    }

    [Fact]
    public void Unknown_callsign_word_falls_back_to_the_formation_number()
    {
        Start(new Dictionary<int, string>()); // no DefaultCallsigns table found
        LogFlightMember("Heinz Koelzig", 1);
        Frame(Player(), Mate("a", "Heinz Koelzig", 0, 17_000));

        var reply = Assert.Single(FlightCommands.FlightPosition(_tracker).Replies);

        Assert.StartsWith("Number one, I am at your three o'clock", reply.Text);
        Assert.Equal(8, reply.Contact.Callsign);
        Assert.DoesNotContain(reply.Tokens!, t => t.Word == SpeechWord.Callsign);
        Assert.Equal(new SpeechToken(SpeechWord.Speaker, 1), reply.Tokens![0]);
    }

    [Fact]
    public void Pilot_missing_from_the_plan_keeps_number_or_name()
    {
        Start(Names);
        LogFlightMember("Ernst Lignitz", 6);
        Frame(Player(), Mate("a", "Ernst Lignitz", 0, 17_000), Acmi.Object("y", 0, -3000, 1000, "Felix Knierim", "Axis"));

        var replies = FriendlyPositionsCommand.Execute(_tracker).Replies;

        Assert.StartsWith("Felix Knierim, bearing two-seven-zero", replies[0].Text);
        Assert.StartsWith("Number six, bearing zero-nine-zero", replies[1].Text);
    }

    [Fact]
    public void Callsigns_are_resolved_when_the_plan_arrives_after_the_contacts()
    {
        _acmi.Feed(Acmi.Header + "#0\n");
        LogFlightMember(PlayerName, 4, isPlayer: true);
        LogFlightMember("Heinz Koelzig", 1);

        _tracker.SetPlan(LoadPlan(Names));
        Frame(Player(), Mate("a", "Heinz Koelzig", 0, 17_000));

        Assert.StartsWith("Oriole one,", Assert.Single(FlightCommands.FlightPosition(_tracker).Replies).Text);
    }

    [Fact]
    public void Callsign_table_is_parsed_from_the_game_format()
    {
        var names = CallsignTable.Parse(new StringReader("""
            // Callsigns
            [ObjectType=0]
            	callsign=0,	"None"
            [end]

            [ObjectType=1]		//Planes
            	callsign=1,		"Stork",		101,201
            	callsign=8,		"Oriole",		101,201
            	callsign=46,	"Acorn",		102	// British
            [end]
            """));

        Assert.Equal(new Dictionary<int, string> { [1] = "Stork", [8] = "Oriole", [46] = "Acorn" }, names);
    }

    [Fact]
    public void Mission_in_an_install_takes_callsign_words_from_the_install()
    {
        string root = Directory.CreateTempSubdirectory("il2radio-install").FullName;
        try
        {
            string data = Path.Combine(root, "data");
            Directory.CreateDirectory(Path.Combine(data, "Missions"));
            Directory.CreateDirectory(Path.Combine(data, "GUI"));
            File.WriteAllText(Path.Combine(data, "Missions", "_gen.Mission"), Plan);
            File.WriteAllText(Path.Combine(data, "GUI", "DefaultCallsigns.eng"), "﻿[ObjectType=1]\n\tcallsign=8,\t\"Oriole\",\t101,201\n");

            var plan = MissionPlan.Load(Path.Combine(data, "Missions", "_gen.Mission"));

            Assert.Equal("Oriole", plan.CallsignName(plan.Player!));
            Assert.Null(plan.CallsignName(plan.AircraftOf("Bert Lange", 201)!)); // 12 is not in this table
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Clip_voice_says_the_callsign_word_before_the_number_when_it_has_one()
    {
        HashSet<string> clips = ["callsign.8", "number.1", "clock.3", "range.5"];
        SpeechToken[] tokens = [new(SpeechWord.Callsign, 8), new(SpeechWord.Speaker, 1), new(SpeechWord.Clock, 3), new(SpeechWord.Range, 5_000)];

        Assert.Equal(["callsign.8", "number.1", "clock.3", "range.5"], ClipPhraser.Phrase(tokens, clips.Contains));
        Assert.Equal(["number.1", "clock.3", "range.5"], ClipPhraser.Phrase(tokens, clips.Except(["callsign.8"]).Contains));
    }
}
