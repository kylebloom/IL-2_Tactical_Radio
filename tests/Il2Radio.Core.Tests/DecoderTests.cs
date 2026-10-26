using System.Buffers.Binary;
using Il2Radio.Core.Acmi;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Telemetry;

namespace Il2Radio.Core.Tests;

public class BinaryMissionLogTests
{
    [Fact]
    public void Decodes_spawn_takeoff_and_kill_records()
    {
        var bytes = new MlgWriter()
            .ObjectSpawn(15, 864255, "Bf 109 F-4", 201, "\u0001Dieter Gehring", -1, (119_868f, 55f, 157_748f))
            .TakeOff(1560, 864255, (119_900f, 70f, 157_700f))
            .Kill(40327, 1, 864255, (120_000f, 1500f, 158_000f))
            .ToArray();

        var events = BinaryMissionLog.DecodeAll(bytes);

        var spawn = Assert.IsType<ObjectSpawnedEvent>(events[0]);
        Assert.Equal(864255, spawn.ObjectId);
        Assert.Equal("Bf 109 F-4", spawn.TypeName);
        Assert.Equal(201, spawn.Country);
        Assert.Equal("Dieter Gehring", spawn.Name); // control prefix stripped
        Assert.Equal(WorldPoint.FromIl2(119_868, 55, 157_748), spawn.Position);

        var takeOff = Assert.IsType<TakeOffEvent>(events[1]);
        Assert.Equal(31.2, takeOff.GameTime.TotalSeconds, 3); // 50 ticks per second

        var kill = Assert.IsType<KillEvent>(events[2]);
        Assert.Equal(864255, kill.TargetId);
        Assert.Equal(1500, kill.Position.Altitude);
    }

    [Fact]
    public void Leaves_partially_written_record_unconsumed()
    {
        var bytes = new MlgWriter().TakeOff(1, 7, (1, 2, 3)).TakeOff(2, 8, (1, 2, 3)).ToArray();
        var events = new List<MissionLogEvent>();

        int consumed = BinaryMissionLog.DecodeAvailable(bytes.AsSpan(0, bytes.Length - 5), events);

        Assert.Single(events);
        Assert.Equal(bytes.Length / 2, consumed);
    }

    [Fact]
    public void Rejects_corrupt_terminator()
    {
        var bytes = new MlgWriter().TakeOff(1, 7, (1, 2, 3)).TakeOff(2, 8, (1, 2, 3)).ToArray();
        bytes[bytes.Length / 2 - 1] = 0x00;

        Assert.Throws<FormatException>(() => BinaryMissionLog.DecodeAll(bytes));
    }

    [Fact]
    public void Tail_returns_only_newly_appended_events()
    {
        string path = Path.GetTempFileName();
        try
        {
            var all = new MlgWriter().TakeOff(1, 7, (1, 2, 3)).TakeOff(2, 8, (1, 2, 3)).ToArray();
            using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            using var tail = new BinaryMissionLogTail(path);

            writer.Write(all, 0, all.Length / 2 + 3); // first record + start of second
            writer.Flush();
            Assert.Single(tail.ReadNew());

            writer.Write(all, all.Length / 2 + 3, all.Length - (all.Length / 2 + 3));
            writer.Flush();
            var second = Assert.Single(tail.ReadNew());
            Assert.Equal(8, Assert.IsType<TakeOffEvent>(second).PlaneId);
            Assert.Empty(tail.ReadNew());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class TextMissionLogTests
{
    [Theory]
    [InlineData("T:16960 AType:5 PID:109572 POS(23800.740, 116.003, 28128.986)", typeof(TakeOffEvent))]
    [InlineData("T:26383 AType:3 AID:107527 TID:106497 POS(25131.697,744.438,23284.689)", typeof(KillEvent))]
    [InlineData("T:58 AType:17 ID:107519 POS(39013.016,45.535,16807.107)", typeof(PositionEvent))]
    [InlineData("T:32497 AType:16 BOTID:108551 POS(23899.598,154.684,20580.168)", typeof(BotDeinitEvent))]
    [InlineData("T:525287 AType:7", typeof(MissionEndedEvent))]
    public void Parses_event_types(string line, Type expected) =>
        Assert.IsType(expected, TextMissionLog.ParseLine(line));

    [Fact]
    public void Parses_object_spawn_with_spaces_in_type()
    {
        var e = Assert.IsType<ObjectSpawnedEvent>(TextMissionLog.ParseLine(
            "T:15 AType:12 ID:137215 TYPE:LaGG-3 ser.29 COUNTRY:101 NAME:LaGG-3 ser.29 PID:-1 POS(19666.344,998.778,33864.637)"));
        Assert.Equal("LaGG-3 ser.29", e.TypeName);
        Assert.Equal(101, e.Country);
        Assert.Equal(-1, e.ParentId);
        Assert.Equal(998.778, e.Position!.Value.Altitude, 3);
    }

    [Fact]
    public void Parses_plane_spawn()
    {
        var e = Assert.IsType<PlaneSpawnedEvent>(TextMissionLog.ParseLine(
            "T:15 AType:10 PLID:276479 PID:277503 BUL:2000 SH:0 BOMB:0 RCT:0 (133119.406,998.935,185101.141) " +
            "IDS:6f3b5e69-38d7-4d83-868c-4e7b8129f41a LOGIN:60dc67e3-ffb2-4df3-a6e5-579e945b4018 NAME:=FB=Vaal " +
            "TYPE:Il-2 mod.1942 COUNTRY:101 FORM:0 FIELD:0 INAIR:0 PARENT:-1 ISPL:1 ISTSTART:1 PAYLOAD:0 FUEL:1.000 SKIN: WM:1"));
        Assert.Equal(276479, e.PlaneId);
        Assert.Equal("Il-2 mod.1942", e.TypeName);
        Assert.True(e.IsPlayer);
        Assert.Equal(WorldPoint.FromIl2(133119.406, 998.935, 185101.141), e.Position);
    }

    [Fact]
    public void Ignores_non_event_lines() => Assert.Null(TextMissionLog.ParseLine("garbage"));
}

public class TelemetryDecoderTests
{
    [Fact]
    public void Decodes_motion_packet()
    {
        var p = new byte[8 + 9 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(p, TelemetryDecoder.MotionPacketId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), 1234);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(8), (float)(Math.PI / 2));

        var m = Assert.IsType<MotionPacket>(TelemetryDecoder.Decode(p));
        Assert.Equal(1234u, m.Tick);
        Assert.Equal(90, m.HeadingDegrees, 3);
    }

    [Fact]
    public void Decodes_state_packet_with_indicators_and_vehicle_name()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(TelemetryDecoder.StatePacketId);
        w.Write((ushort)0); // size (patched below)
        w.Write(77u);       // tick
        w.Write((byte)1);   // one indicator
        w.Write(StatePacket.IndicatedAirspeedIndicator); w.Write((byte)1); w.Write(100f);
        w.Write((byte)1);   // one event
        w.Write(TelemetryEvent.VehicleNameType); w.Write((byte)11); w.Write((byte)10); w.Write("Bf 109 F-4"u8.ToArray());
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)bytes.Length);

        var s = Assert.IsType<StatePacket>(TelemetryDecoder.Decode(bytes));
        Assert.Equal(100f, s.IndicatedAirspeedMs);
        Assert.Equal("Bf 109 F-4", Assert.Single(s.Events).VehicleName);
    }

    [Fact]
    public void Returns_null_for_unknown_or_truncated_packets()
    {
        Assert.Null(TelemetryDecoder.Decode(new byte[] { 1, 2, 3 }));
        Assert.Null(TelemetryDecoder.Decode(new byte[12]));
        var truncated = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(truncated, TelemetryDecoder.MotionPacketId);
        Assert.Null(TelemetryDecoder.Decode(truncated));
    }
}

public class MissionFileTests
{
    private const string Sample = """
        # Mission File Version = 1.0;

        Options
        {
          Date = 6.9.1942;
          GuiMap = "stalingrad-autumn-1942";
          Countries
          {
            101 : 1;
            201 : 2;
          }
        }

        Plane
        {
          Name = "Hannes Guth,201033,0";
          Index = 10;
          XPos = 119869.0;
          YPos = 55.0;
          ZPos = 157778.0;
          Script = "LuaScripts\WorldObjects\Planes\bf109f4.txt";
          Country = 201;
          AILevel = 0;
          StartInAir = 2;
          Callsign = 1;
          Callnum = 3;
        }

        Plane
        {
          Name = "Felix Knierim,201033,0";
          Index = 11;
          XPos = 119848.0;
          YPos = 55.0;
          ZPos = 157764.0;
          Script = "LuaScripts\WorldObjects\Planes\bf109f4.txt";
          Country = 201;
          AILevel = 2;
          Callsign = 1;
          Callnum = 2;
        }

        Plane
        {
          Name = "Adil Gerasimov,101731,0";
          Index = 12;
          Script = "LuaScripts\WorldObjects\Planes\P40E1.txt";
          Country = 101;
          AILevel = 1;
          Callsign = 3;
        }
        """;

    [Fact]
    public void Extracts_player_flight_and_coalitions()
    {
        var plan = MissionPlan.FromBlocks(MissionFileParser.Parse(new StringReader(Sample)));

        Assert.Equal("stalingrad-autumn-1942", plan.GuiMap);
        Assert.Equal(3, plan.Aircraft.Count);
        var player = Assert.IsType<PlannedAircraft>(plan.Player);
        Assert.Equal("Hannes Guth", player.PilotName);
        Assert.Equal("bf109f4", player.TypeKey);
        Assert.Equal(2, player.Coalition);
        Assert.False(player.StartInAir);
        Assert.Equal(WorldPoint.FromIl2(119869, 55, 157778), player.StartPosition);
        Assert.Equal("Felix Knierim", Assert.Single(plan.LikelyFlightMates).PilotName);
        Assert.Equal("p40e1", plan.Aircraft[2].TypeKey);
    }
}

public class AcmiStreamTests
{
    [Fact]
    public void Tracks_objects_across_frames_and_partial_lines()
    {
        var acmi = new AcmiStream();
        string text = Acmi.Header + "#0\n" + Acmi.Object("a1", 0, 0, 1000, "Hannes Guth", "Axis") + "#1.5\n" + Acmi.Object("a1", 100, 0, 1010, "Hannes Guth", "Axis");

        acmi.Feed(text[..^10]); // last line incomplete
        Assert.Equal(1, acmi.Objects["a1"].PositionUpdates);

        acmi.Feed(text[^10..]);
        var obj = acmi.Objects["a1"];
        Assert.Equal(2, obj.PositionUpdates);
        Assert.Equal(1.5, obj.LastPositionTime);
        Assert.Equal("Hannes Guth", obj.Pilot);
        Assert.True(obj.IsAircraft);
        var pos = acmi.PositionOf(obj)!.Value;
        Assert.Equal(100, pos.North, 0);
        Assert.Equal(CoordinateFrame.Acmi, pos.Frame);
    }

    [Fact]
    public void East_uses_one_scale_for_the_stream_so_absolute_degrees_give_true_distances()
    {
        // IL-2 writes absolute degrees with no reference point. The flight's leader and the player
        // from the 2026-10-26 Career track, 45.3 km apart by the great circle.
        var acmi = new AcmiStream();
        acmi.Feed("FileType=text/acmi/tacview\nFileVersion=2.1\n#0\n" +
                  "a,T=43.19650408|48.438842737|556,Type=Air+FixedWing,Name=Bf 109 F-4,Pilot=Viktor Richter,Coalition=Axis\n" +
                  "b,T=43.581345815|48.756216214|562,Type=Air+FixedWing,Name=Bf 109 F-4,Pilot=Gert Knoll,Coalition=Axis\n#1\n");

        var rel = Navigation.Relative(acmi.PositionOf(acmi.Objects["a"])!.Value, acmi.PositionOf(acmi.Objects["b"])!.Value)!.Value;

        Assert.InRange(rel.RangeMetres, 45_000, 45_600);
    }

    [Fact]
    public void Parses_il2_style_names_with_unescaped_commas_and_control_prefix()
    {
        // Lines as written by IL-2 (Career, Oct 2026).
        var acmi = new AcmiStream();
        acmi.Feed("FileType=text/acmi/tacview\nFileVersion=2.1\n0,Author=PilotNick\n#0.00\n" +
                  "107ff,T=43.659919044|48.538518733|402.07|0.0|3.7|51.5,AGL=326.66,Name=Il-2 mod.1942 - Boris Radishevskiy,101621,0,Type=Air+FixedWing,Coalition=Allies,Color=Red\n" +
                  "d6fff,T=43.642047048|48.591727424|2544.89|-0.1|2.7|81.6,AGL=2499.52,Name=Bf 109 F-4 - \u0001Heinz Koelzig,Type=Air+FixedWing,Coalition=Axis,Color=Blue\n");

        var il2 = acmi.Objects["107ff"];
        Assert.Equal("Boris Radishevskiy", il2.Pilot);
        Assert.Equal("Il-2 mod.1942", il2.Model);
        Assert.Equal("Allies", il2.Coalition);
        Assert.Equal("Air+FixedWing", il2.Type);
        Assert.Equal("Heinz Koelzig", acmi.Objects["d6fff"].Pilot);
        Assert.Equal("PilotNick", acmi.Author);
        Assert.Equal(48.538518733, il2.Latitude, 9);
    }

    [Fact]
    public void Empty_transform_fields_keep_previous_values_and_removal_is_tracked()
    {
        var acmi = new AcmiStream();
        acmi.Feed(Acmi.Header + "#0\nb2,T=0.01|0.02|500,Type=Air+FixedWing\n#2\nb2,T=||600\n#3\n-b2\n#4\n");

        var obj = acmi.Objects["b2"];
        Assert.Equal(0.01, obj.Longitude);
        Assert.Equal(600, obj.Altitude);
        Assert.True(obj.Removed);
        Assert.Equal(3, obj.RemovedTime);
    }

    [Fact]
    public void Final_frame_removals_of_a_closed_recording_are_not_applied()
    {
        // IL-2 ends a recording by removing every object in the last frame.
        var acmi = new AcmiStream();
        acmi.Feed(Acmi.Header + "#0\na,T=0|0|500,Type=Air+FixedWing\nb,T=0|0|600,Type=Air+FixedWing\n#168.84\n-a\n-b\n");

        Assert.False(acmi.Objects["a"].Removed);
        Assert.False(acmi.Objects["b"].Removed);
        Assert.Equal(2, acmi.PendingRemovals);
    }
}

public class ContactTrackerAcmiTests
{
    [Fact]
    public void Aircraft_removed_by_one_recording_segment_come_back_in_the_next()
    {
        var tracker = new ContactTracker();
        var first = new AcmiStream();
        first.Feed(Acmi.Header + "#0\n" + Acmi.Object("a", 0, 0, 500, "Pilot A", "Axis") + "#1\n-a\n#2\n");
        tracker.Apply(first);
        Assert.Equal(Contacts.ContactStatus.Despawned, Assert.Single(tracker.Contacts).Status);

        var second = new AcmiStream(); // next file, e.g. _01.acmi
        second.Feed(Acmi.Header + "#0\n" + Acmi.Object("a", 100, 0, 500, "Pilot A", "Axis"));
        tracker.Apply(second);

        Assert.Equal(Contacts.ContactStatus.Airborne, Assert.Single(tracker.Contacts).Status);
    }
}

public class NavigationTests
{
    [Theory]
    [InlineData(1000, 0, 0)]
    [InlineData(0, 1000, 90)]
    [InlineData(-1000, 0, 180)]
    [InlineData(0, -1000, 270)]
    [InlineData(1000, 1000, 45)]
    public void Bearing_is_clockwise_from_north(double north, double east, double expected)
    {
        var from = new WorldPoint(0, 0, 0, CoordinateFrame.Il2World);
        var to = new WorldPoint(north, east, 0, CoordinateFrame.Il2World);
        Assert.Equal(expected, Navigation.Relative(from, to)!.Value.BearingDegrees, 6);
    }

    [Fact]
    public void Refuses_to_compare_different_frames() =>
        Assert.Null(Navigation.Relative(new WorldPoint(0, 0, 0, CoordinateFrame.Il2World), new WorldPoint(1, 1, 0, CoordinateFrame.Acmi)));

    [Fact]
    public void Il2_coordinates_map_x_to_north_and_z_to_east()
    {
        var p = WorldPoint.FromIl2(100, 2000, 300);
        Assert.Equal((100.0, 300.0, 2000.0), (p.North, p.East, p.Altitude));
    }

    [Theory]
    [InlineData(315, "northwest")]
    [InlineData(10, "north")]
    [InlineData(100, "east")]
    public void Compass_points(double bearing, string expected) => Assert.Equal(expected, Navigation.CompassPoint(bearing));

    [Theory]
    [InlineData(0, 360)]
    [InlineData(360, 360)]
    [InlineData(2, 360)]
    [InlineData(2.5, 5)]
    [InlineData(3, 5)]
    [InlineData(92, 90)]
    [InlineData(100, 100)]
    [InlineData(180, 180)]
    [InlineData(182.5, 185)]
    [InlineData(357, 355)]
    [InlineData(358, 360)]
    [InlineData(-90, 270)]
    public void A_radio_course_is_the_nearest_five_degrees_and_north_is_360(double bearing, int expected) =>
        Assert.Equal(expected, Navigation.CourseDegrees(bearing));
}
