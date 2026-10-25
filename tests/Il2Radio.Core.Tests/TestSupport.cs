using System.Globalization;
using System.Text;

namespace Il2Radio.Core.Tests;

/// <summary>Builds binary .mlg records in the layout IL-2 writes.</summary>
internal sealed class MlgWriter
{
    private readonly MemoryStream _out = new();

    public byte[] ToArray() => _out.ToArray();

    public MlgWriter Record(uint tick, byte atype, Action<BinaryWriter> payload)
    {
        var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
            payload(w);
        using var o = new BinaryWriter(_out, Encoding.UTF8, leaveOpen: true);
        o.Write(tick);
        o.Write(atype);
        o.Write((ushort)body.Length);
        o.Write(body.ToArray());
        o.Write((byte)0x0A);
        return this;
    }

    public MlgWriter ObjectSpawn(uint tick, int id, string type, int country, string name, int parent, (float X, float Y, float Z) pos) =>
        Record(tick, 12, w =>
        {
            w.Write(id);
            Str(w, type);
            Str(w, country.ToString(CultureInfo.InvariantCulture));
            Str(w, name);
            w.Write(parent);
            Pos(w, pos);
        });

    public MlgWriter TakeOff(uint tick, int plane, (float X, float Y, float Z) pos) =>
        Record(tick, 5, w => { w.Write(plane); Pos(w, pos); });

    public MlgWriter Kill(uint tick, int attacker, int target, (float X, float Y, float Z) pos) =>
        Record(tick, 3, w => { w.Write(attacker); w.Write(target); Pos(w, pos); });

    public static void Str(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    public static void Pos(BinaryWriter w, (float X, float Y, float Z) p)
    {
        w.Write(p.X);
        w.Write(p.Y);
        w.Write(p.Z);
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Builds small Tacview ACMI streams with positions given in metres from the reference point.</summary>
internal static class Acmi
{
    public const double RefLat = 48.0, RefLon = 44.0;

    public static string Header =>
        $"FileType=text/acmi/tacview\nFileVersion=2.2\n0,ReferenceLongitude={RefLon},ReferenceLatitude={RefLat}\n";

    public static string Object(string id, double northM, double eastM, double altM, string pilot, string coalition, string name = "Bf 109 F-4", double? heading = null)
    {
        double lat = northM / 111_320.0;
        double lon = eastM / (111_320.0 * Math.Cos((RefLat + lat) * Math.PI / 180.0));
        string attitude = heading is double h ? string.Create(CultureInfo.InvariantCulture, $"|0|0|{h:F1}") : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{id},T={lon:F7}|{lat:F7}|{altM:F1}{attitude},Type=Air+FixedWing,Name={name},Pilot={pilot},Coalition={coalition}\n");
    }
}
