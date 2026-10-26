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

    /// <summary>Cloud, haze and start time from the mission's Options block.</summary>
    public MissionWeather Weather { get; init; } = MissionWeather.Unknown;

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

    /// <summary>
    /// Waypoint polylines by planned-aircraft index, in mission-file metres. A chain follows each
    /// waypoint's <c>Targets</c> link; a waypoint that nothing points at starts a path. This is the
    /// briefed route, not a live position.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<IReadOnlyList<WorldPoint>>> Routes { get; init; }
        = new Dictionary<int, IReadOnlyList<IReadOnlyList<WorldPoint>>>();

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

        return new MissionPlan(options.GetString("GuiMap"), options.Get("Date") ?? "", options.Get("Time") ?? "", countries, aircraft)
        {
            Weather = MissionWeather.FromOptions(options),
            Routes = ParseRoutes(blocks),
        };
    }

    /// <summary>Waypoint chains keyed by the plane index their entity points at (<c>MisObjID</c>).</summary>
    private static Dictionary<int, IReadOnlyList<IReadOnlyList<WorldPoint>>> ParseRoutes(IReadOnlyList<MissionBlock> roots)
    {
        var all = Walk(roots).ToList();
        var planeOfEntity = new Dictionary<int, int>();
        foreach (var entity in all.Where(b => b.Type == "MCU_TR_Entity"))
        {
            int plane = entity.GetInt("MisObjID");
            if (plane >= 0)
                planeOfEntity[entity.GetInt("Index")] = plane;
        }

        var waypoints = new List<WaypointNode>();
        foreach (var block in all.Where(b => b.Type == "MCU_Waypoint"))
        {
            double x = block.GetDouble("XPos"), y = block.GetDouble("YPos"), z = block.GetDouble("ZPos");
            if (double.IsNaN(x) || double.IsNaN(z))
                continue;
            waypoints.Add(new WaypointNode(
                block.GetInt("Index"),
                IntList(block.Get("Targets")),
                IntList(block.Get("Objects")),
                WorldPoint.FromIl2(x, double.IsNaN(y) ? 0 : y, z)));
        }

        var byEntity = new Dictionary<int, List<WaypointNode>>();
        foreach (var waypoint in waypoints)
        {
            foreach (int entity in waypoint.Objects)
            {
                if (!byEntity.TryGetValue(entity, out var list))
                    byEntity[entity] = list = [];
                list.Add(waypoint);
            }
        }

        var routes = new Dictionary<int, IReadOnlyList<IReadOnlyList<WorldPoint>>>();
        foreach (var (entity, points) in byEntity)
        {
            if (!planeOfEntity.TryGetValue(entity, out int plane))
                continue;
            var paths = OrderPaths(points);
            if (paths.Count == 0)
                continue;
            if (!routes.TryGetValue(plane, out var existing))
                routes[plane] = paths;
            else
                routes[plane] = [.. existing, .. paths];
        }

        return routes;
    }

    private static List<IReadOnlyList<WorldPoint>> OrderPaths(List<WaypointNode> points)
    {
        var byIndex = points.GroupBy(p => p.Index).ToDictionary(g => g.Key, g => g.First());
        var incoming = new HashSet<int>();
        foreach (var point in byIndex.Values)
        {
            foreach (int target in point.Targets)
            {
                if (byIndex.ContainsKey(target))
                    incoming.Add(target);
            }
        }

        var heads = byIndex.Values.Where(p => !incoming.Contains(p.Index)).OrderBy(p => p.Index).ToList();
        if (heads.Count == 0 && byIndex.Count > 0)
            heads.Add(byIndex.Values.OrderBy(p => p.Index).First());

        var visited = new HashSet<int>();
        var paths = new List<IReadOnlyList<WorldPoint>>();
        foreach (var head in heads)
        {
            var line = new List<WorldPoint>();
            var point = head;
            while (visited.Add(point.Index))
            {
                line.Add(point.Position);
                WaypointNode? next = null;
                foreach (int target in point.Targets)
                {
                    if (byIndex.TryGetValue(target, out var candidate) && !visited.Contains(candidate.Index))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next is null)
                    break;
                point = next;
            }

            if (line.Count > 0)
                paths.Add(line);
        }

        foreach (var point in byIndex.Values.Where(p => !visited.Contains(p.Index)).OrderBy(p => p.Index))
            paths.Add([point.Position]);
        return paths;
    }

    private static IEnumerable<MissionBlock> Walk(IEnumerable<MissionBlock> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            foreach (var child in Walk(block.Children))
                yield return child;
        }
    }

    private static int[] IntList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return text.Trim().TrimStart('[').TrimEnd(']')
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : -1)
            .Where(value => value >= 0)
            .ToArray();
    }

    private sealed record WaypointNode(int Index, int[] Targets, int[] Objects, WorldPoint Position);

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

/// <summary>
/// Visibility-relevant conditions from a mission's Options block. <see cref="CloudCoverage"/> is derived
/// from the cloud preset name (<c>summer\00_Clear_05\sky.ini</c>: the leading number 00–04 runs from clear
/// to overcast). That naming is community knowledge of the editor presets, not verified for every preset.
/// </summary>
public sealed record MissionWeather(double CloudBaseMetres, double CloudThicknessMetres, double CloudCoverage, double Haze, TimeSpan StartTime)
{
    public static readonly MissionWeather Unknown = new(0, 0, 0, 0, TimeSpan.FromHours(12));

    public double CloudTopMetres => CloudBaseMetres + CloudThicknessMetres;

    public static MissionWeather FromOptions(MissionBlock options)
    {
        double Number(string key) => options.GetDouble(key) is var v && double.IsNaN(v) ? 0 : v;
        TimeSpan start = TimeSpan.TryParse(options.Get("Time") ?? "", System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : Unknown.StartTime;
        return new MissionWeather(Number("CloudLevel"), Number("CloudHeight"), CoverageOf(options.GetString("CloudConfig")), Math.Clamp(Number("Haze"), 0, 1), start);
    }

    /// <summary>0 (clear) to 1 (overcast) from a preset path such as <c>summer\02_Medium_03\sky.ini</c>.</summary>
    public static double CoverageOf(string cloudConfig)
    {
        string preset = cloudConfig.Replace('/', '\\').Split('\\').FirstOrDefault(p => p.Length >= 2 && char.IsDigit(p[0]) && char.IsDigit(p[1])) ?? "";
        if (preset.Length >= 2 && int.TryParse(preset[..2], out int index))
            return Math.Clamp(index / 4.0, 0, 1);
        string lower = cloudConfig.ToLowerInvariant();
        return lower.Contains("overcast") ? 1 : lower.Contains("heavy") ? 0.75 : lower.Contains("medium") ? 0.5 : lower.Contains("light") ? 0.25 : 0;
    }
}
