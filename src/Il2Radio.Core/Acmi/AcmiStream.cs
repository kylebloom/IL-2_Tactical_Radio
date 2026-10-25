using System.Globalization;
using System.Text;
using Il2Radio.Core.Geometry;

namespace Il2Radio.Core.Acmi;

/// <summary>State of one object in a Tacview ACMI stream.</summary>
public sealed class AcmiObject(string id)
{
    public string Id { get; } = id;
    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);

    // T= components: longitude/latitude offsets (deg, relative to the reference point), altitude (m),
    // roll, pitch, yaw (deg), flat-world U/V (m) and heading (deg). NaN = never reported.
    public double Longitude = double.NaN, Latitude = double.NaN, Altitude = double.NaN;
    public double Roll = double.NaN, Pitch = double.NaN, Yaw = double.NaN;
    public double U = double.NaN, V = double.NaN, Heading = double.NaN;

    public double FirstSeenTime { get; internal set; } = double.NaN;
    public double LastPositionTime { get; internal set; } = double.NaN;
    public int PositionUpdates { get; internal set; }
    public bool Removed { get; internal set; }
    public double? RemovedTime { get; internal set; }

    public string Name => Properties.GetValueOrDefault("Name", "");
    public string Type => Properties.GetValueOrDefault("Type", "");
    public string Coalition => Properties.GetValueOrDefault("Coalition", "");

    /// <summary>
    /// Pilot name. IL-2 writes aircraft names as "Bf 109 F-4 - Heinz Koelzig" (AI may carry a
    /// ",rosterId,n" suffix; the human pilot appears under the account nickname). A Pilot= property wins if present.
    /// </summary>
    public string Pilot =>
        Properties.TryGetValue("Pilot", out string? pilot) ? pilot
        : Name.IndexOf(" - ", StringComparison.Ordinal) is int i and >= 0 ? StripControl(Name[(i + 3)..].Split(',')[0]).Trim()
        : "";

    // Names of the player's flight-mates carry a leading 0x01 byte, as in the mission log.
    private static string StripControl(string s) => new(s.Where(c => !char.IsControl(c)).ToArray());

    /// <summary>Vehicle model, e.g. "Bf 109 F-4", without the pilot part of <see cref="Name"/>.</summary>
    public string Model => Name.IndexOf(" - ", StringComparison.Ordinal) is int i and >= 0 ? Name[..i] : Name;
    public bool IsAircraft => Type.Contains("Air", StringComparison.Ordinal) && !Type.Contains("Parachutist", StringComparison.Ordinal);
    /// <summary>
    /// True heading in degrees 0-360. IL-2 writes 6-component transforms, where yaw is the heading
    /// (checked against ground track in a Career recording: within ~4°, i.e. wind drift).
    /// </summary>
    public double? HeadingDegrees =>
        !double.IsNaN(Heading) ? Normalize(Heading) : !double.IsNaN(Yaw) ? Normalize(Yaw) : null;

    private static double Normalize(double degrees) => (degrees % 360 + 360) % 360;

    public bool HasPosition => !double.IsNaN(Longitude) && !double.IsNaN(Latitude);
}

/// <summary>
/// Incremental parser for the Tacview text ACMI format (v2.x). Feed it text as it becomes available;
/// incomplete trailing lines are buffered until their newline arrives.
/// </summary>
/// <remarks>Format reference: https://www.tacview.net/documentation/acmi/</remarks>
public sealed class AcmiStream
{
    private const double MetresPerDegree = 111_320.0;
    private readonly StringBuilder _pending = new();
    private readonly Dictionary<string, AcmiObject> _objects = new(StringComparer.Ordinal);

    public double ReferenceLongitude { get; private set; }
    public double ReferenceLatitude { get; private set; }
    public string? ReferenceTime { get; private set; }

    /// <summary>Recording author; for IL-2 this is the player's account nickname, which also names the player's aircraft.</summary>
    public string? Author { get; private set; }

    /// <summary>Current stream time in seconds relative to ReferenceTime (last "#t" frame marker).</summary>
    public double CurrentTime { get; private set; }

    public IReadOnlyDictionary<string, AcmiObject> Objects => _objects;
    public long LinesParsed { get; private set; }

    public void Feed(string text)
    {
        _pending.Append(text);
        string buffer = _pending.ToString();
        int lastNewline = buffer.LastIndexOf('\n');
        if (lastNewline < 0)
            return;

        _pending.Clear();
        _pending.Append(buffer, lastNewline + 1, buffer.Length - lastNewline - 1);

        string? continued = null;
        foreach (string rawLine in buffer[..lastNewline].Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.EndsWith('\\') && !line.EndsWith("\\\\", StringComparison.Ordinal))
            {
                continued = (continued ?? "") + line[..^1] + "\n";
                continue;
            }

            if (continued is not null)
            {
                line = continued + line;
                continued = null;
            }

            ParseLine(line);
        }

        if (continued is not null)
            _pending.Insert(0, continued.Replace("\n", "\\\n"));
    }

    /// <summary>Local north/east/altitude position in the ACMI frame.</summary>
    public WorldPoint? PositionOf(AcmiObject obj)
    {
        if (!obj.HasPosition)
            return null;
        double latitude = ReferenceLatitude + obj.Latitude;
        double north = obj.Latitude * MetresPerDegree;
        double east = obj.Longitude * MetresPerDegree * Math.Cos(latitude * Math.PI / 180.0);
        return new WorldPoint(north, east, double.IsNaN(obj.Altitude) ? 0 : obj.Altitude, CoordinateFrame.Acmi);
    }

    private void ParseLine(string line)
    {
        if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            return;
        LinesParsed++;

        if (line[0] == '#')
        {
            if (double.TryParse(line.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                CurrentTime = t;
            return;
        }

        if (line[0] == '-')
        {
            if (_objects.TryGetValue(line[1..].Trim(), out var removed))
            {
                removed.Removed = true;
                removed.RemovedTime = CurrentTime;
            }
            return;
        }

        if (line.StartsWith("FileType=", StringComparison.Ordinal) || line.StartsWith("FileVersion=", StringComparison.Ordinal))
            return;

        var fields = SplitUnescaped(line);
        if (fields.Count == 0)
            return;

        string id = fields[0];
        if (id == "0")
        {
            foreach (var (key, value) in fields.Skip(1).Select(SplitProperty))
            {
                switch (key)
                {
                    case "ReferenceLongitude": ReferenceLongitude = ParseDouble(value); break;
                    case "ReferenceLatitude": ReferenceLatitude = ParseDouble(value); break;
                    case "ReferenceTime": ReferenceTime = value; break;
                    case "Author": Author = value; break;
                }
            }
            return;
        }

        if (!_objects.TryGetValue(id, out var obj))
        {
            obj = new AcmiObject(id) { FirstSeenTime = CurrentTime };
            _objects[id] = obj;
        }
        else if (obj.Removed)
        {
            obj.Removed = false; // id reused
            obj.RemovedTime = null;
        }

        foreach (var (key, value) in fields.Skip(1).Select(SplitProperty))
        {
            if (key == "T")
                ApplyTransform(obj, value);
            else
                obj.Properties[key] = value;
        }
    }

    private void ApplyTransform(AcmiObject obj, string value)
    {
        string[] parts = value.Split('|');
        static void Set(ref double target, string s)
        {
            if (s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                target = v;
        }

        if (parts.Length >= 3)
        {
            Set(ref obj.Longitude, parts[0]);
            Set(ref obj.Latitude, parts[1]);
            Set(ref obj.Altitude, parts[2]);
        }

        switch (parts.Length)
        {
            case 5:
                Set(ref obj.U, parts[3]);
                Set(ref obj.V, parts[4]);
                break;
            case 6:
                Set(ref obj.Roll, parts[3]);
                Set(ref obj.Pitch, parts[4]);
                Set(ref obj.Yaw, parts[5]);
                break;
            case 9:
                Set(ref obj.Roll, parts[3]);
                Set(ref obj.Pitch, parts[4]);
                Set(ref obj.Yaw, parts[5]);
                Set(ref obj.U, parts[6]);
                Set(ref obj.V, parts[7]);
                Set(ref obj.Heading, parts[8]);
                break;
        }

        obj.LastPositionTime = CurrentTime;
        obj.PositionUpdates++;
    }

    private static List<string> SplitUnescaped(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                current.Append(line[++i]);
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());

        // IL-2 does not escape commas inside values ("Name=Il-2 mod.1942 - Boris Radishevskiy,101621,0"),
        // so a field without '=' after the object id is a continuation of the previous value.
        for (int i = fields.Count - 1; i >= 2; i--)
        {
            if (fields[i].Contains('='))
                continue;
            fields[i - 1] += "," + fields[i];
            fields.RemoveAt(i);
        }

        return fields;
    }

    private static (string Key, string Value) SplitProperty(string field)
    {
        int eq = field.IndexOf('=');
        return eq < 0 ? (field, "") : (field[..eq], field[(eq + 1)..]);
    }

    private static double ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
}
