using System.Globalization;
using Il2Radio.Core.Geometry;

namespace Il2Radio.Core.MissionFile;

/// <summary>A block in a text .Mission file, e.g. <c>Plane { Name = "..."; ... }</c>.</summary>
public sealed class MissionBlock(string type)
{
    public string Type { get; } = type;
    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);
    public List<string> RawLines { get; } = [];
    public List<MissionBlock> Children { get; } = [];

    public string? Get(string key) => Properties.GetValueOrDefault(key);

    public string GetString(string key) => Get(key)?.Trim('"') ?? "";

    public int GetInt(string key, int fallback = -1) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    public double GetDouble(string key) =>
        double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
}

/// <summary>
/// Minimal parser for IL-2 text mission files (<c>*.Mission</c>). Career Mode writes the mission it
/// is about to fly to <c>data\Missions\_gen.Mission</c> alongside the binary <c>_gen.msnbin</c>.
/// </summary>
public static class MissionFileParser
{
    public static List<MissionBlock> Parse(TextReader reader)
    {
        var roots = new List<MissionBlock>();
        var stack = new Stack<MissionBlock>();
        string? pendingType = null;

        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line == "{")
            {
                var block = new MissionBlock(pendingType ?? "");
                pendingType = null;
                if (stack.Count > 0)
                    stack.Peek().Children.Add(block);
                else
                    roots.Add(block);
                stack.Push(block);
                continue;
            }

            if (line == "}")
            {
                if (stack.Count > 0)
                    stack.Pop();
                continue;
            }

            if (!line.EndsWith(';'))
            {
                pendingType = line; // block type name; the "{" follows on the next line
                continue;
            }

            if (stack.Count == 0)
                continue;

            string body = line[..^1];
            int eq = body.IndexOf('=');
            if (eq > 0)
                stack.Peek().Properties[body[..eq].Trim()] = body[(eq + 1)..].Trim();
            else
                stack.Peek().RawLines.Add(body.Trim());
        }

        return roots;
    }

    public static List<MissionBlock> ParseFile(string path)
    {
        using var reader = new StreamReader(path);
        return Parse(reader);
    }
}

/// <summary>An aircraft as planned in the mission file. This is intent, not a live observation.</summary>
public sealed record PlannedAircraft(
    int Index,
    string PilotName,
    string TypeKey,
    int Country,
    int Coalition,
    bool IsPlayer,
    int Callsign,
    int Callnum,
    int NumberInFormation,
    bool StartInAir,
    WorldPoint StartPosition);

public sealed record MissionPlan(
    string GuiMap,
    string Date,
    string Time,
    IReadOnlyDictionary<int, int> CountryToCoalition,
    IReadOnlyList<PlannedAircraft> Aircraft)
{
    public PlannedAircraft? Player => Aircraft.FirstOrDefault(a => a.IsPlayer);

    /// <summary>Callsign words by <see cref="PlannedAircraft.Callsign"/> number (English), empty when unknown.</summary>
    public IReadOnlyDictionary<int, string> CallsignNames { get; init; } = new Dictionary<int, string>();

    /// <summary>The planned aircraft flown by this pilot, if the plan has one (country -1 matches any).</summary>
    public PlannedAircraft? AircraftOf(string pilotName, int country) =>
        pilotName.Length == 0 ? null
        : Aircraft.FirstOrDefault(a => a.PilotName == pilotName && (country <= 0 || a.Country == country));

    /// <summary>"Oriole", or null when the aircraft has no callsign or its word is unknown.</summary>
    public string? CallsignName(PlannedAircraft aircraft) =>
        aircraft.Callsign > 0 && CallsignNames.TryGetValue(aircraft.Callsign, out var name) && name.Length > 0 ? name : null;

    /// <summary>
    /// Aircraft that appear to share the player's flight. Heuristic: same country and callsign as the
    /// player. Unverified against the career generator; treat as planned information only.
    /// </summary>
    public IEnumerable<PlannedAircraft> LikelyFlightMates =>
        Player is { } p
            ? Aircraft.Where(a => !a.IsPlayer && a.Country == p.Country && a.Callsign == p.Callsign && p.Callsign != 0)
            : [];

    public int CoalitionOf(int country) => CountryToCoalition.GetValueOrDefault(country, -1);

    public static MissionPlan FromBlocks(IReadOnlyList<MissionBlock> blocks)
    {
        var options = blocks.FirstOrDefault(b => b.Type == "Options") ?? new MissionBlock("Options");
        var countries = new Dictionary<int, int>();
        foreach (var line in options.Children.FirstOrDefault(c => c.Type == "Countries")?.RawLines ?? [])
        {
            var parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int country) && int.TryParse(parts[1], out int coalition))
                countries[country] = coalition;
        }

        var aircraft = blocks
            .Where(b => b.Type == "Plane")
            .Select(b =>
            {
                int country = b.GetInt("Country");
                return new PlannedAircraft(
                    b.GetInt("Index"),
                    // Career marks the player's flight-mates with a 0x01 before the name (VERIFIED, _gen.Mission 2026-10-25).
                    string.Concat(b.GetString("Name").Split(',')[0].Where(ch => !char.IsControl(ch))),
                    Path.GetFileNameWithoutExtension(b.GetString("Script").Replace('\\', '/')).ToLowerInvariant(),
                    country,
                    countries.GetValueOrDefault(country, -1),
                    b.GetInt("AILevel") == 0,
                    b.GetInt("Callsign", 0),
                    b.GetInt("Callnum", 0),
                    b.GetInt("NumberInFormation", 0),
                    b.GetInt("StartInAir", 0) == 0, // 0 = air start, 1 = runway, 2 = parking

                    WorldPoint.FromIl2(b.GetDouble("XPos"), b.GetDouble("YPos"), b.GetDouble("ZPos")));
            })
            .ToList();

        return new MissionPlan(options.GetString("GuiMap"), options.Get("Date") ?? "", options.Get("Time") ?? "", countries, aircraft);
    }

    /// <summary>
    /// Loads a mission file. For a mission inside an IL-2 install (<c>data\Missions\…</c>), callsign words
    /// come from the install's <c>data\GUI\DefaultCallsigns.eng</c>.
    /// </summary>
    public static MissionPlan Load(string path)
    {
        var plan = FromBlocks(MissionFileParser.ParseFile(path));
        string? dataDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path)));
        return dataDir is null ? plan : plan with { CallsignNames = CallsignTable.Load(dataDir) };
    }
}
