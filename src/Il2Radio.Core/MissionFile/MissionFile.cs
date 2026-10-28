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

/// <summary>
/// One briefed waypoint: where it is and the name Career gave it. Plan, not a live position.
/// Career names its own flight's waypoints <c>WAYPOINT-TAKEOFF</c>, <c>CMD_Waypoint</c>, <c>CMD_AttackArea</c> (where the
/// flight is sent to attack or patrol; an <c>MCU_CMD_AttackArea</c> sits on the same spot) and <c>WAYPOINT_LAND</c>
/// (over the home field). VERIFIED in the <c>_gen.mission</c> IL-2 saved beside each Career track on this install,
/// 2026-10-25 to 2026-10-27 (sweeps, a patrol loop, an escort with only the landing waypoint).
/// </summary>
public sealed record PlannedWaypoint(WorldPoint Position, string Name)
{
    /// <summary>An attack or patrol area: Career's <c>CMD_AttackArea</c>, or an editor-style <c>…AT_AREA…</c> name.</summary>
    public bool IsAttack =>
        Name.Contains("AttackArea", StringComparison.OrdinalIgnoreCase) || Name.Contains("AT_AREA", StringComparison.OrdinalIgnoreCase);

    /// <summary>The landing waypoint over the home field (<c>WAYPOINT_LAND</c>).</summary>
    public bool IsLanding => Name.Contains("WAYPOINT_LAND", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An airfield in the mission (<c>Airfield</c> block). Career writes each field with its name, country and
/// position in mission metres; Tusov, country 201, sat at the same place in every Career mission checked
/// (VERIFIED, 2026-10-25 to 2026-10-28, within a kilometre between missions as the active runway changes).
/// </summary>
public sealed record PlannedAirfield(string Name, int Country, int Callsign, WorldPoint Position);

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
    /// waypoint's <c>Targets</c> link; a waypoint that nothing points at starts a path. A cycle, such
    /// as a patrol of attack areas that point at each other, is one path and repeats its first point
    /// so the leg that closes it is part of the route. This is the briefed route, not a live position.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<IReadOnlyList<WorldPoint>>> Routes { get; init; }
        = new Dictionary<int, IReadOnlyList<IReadOnlyList<WorldPoint>>>();

    /// <summary>The same chains as <see cref="Routes"/>, point for point, with each waypoint's name.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<IReadOnlyList<PlannedWaypoint>>> Waypoints { get; init; }
        = new Dictionary<int, IReadOnlyList<IReadOnlyList<PlannedWaypoint>>>();

    /// <summary>
    /// The briefed order of each flight's waypoints, by planned-aircraft index, as one sequence from take-off to
    /// landing. Career does not link its route only waypoint to waypoint, so <see cref="Waypoints"/> breaks a real
    /// sortie into pieces; this follows the links Career uses to move a flight on. VERIFIED on the Career
    /// <c>_gen.mission</c> files saved beside this install's tracks (22 distinct missions, 2026-10-20 to 2026-10-27; the
    /// walk takes every one of them to its landing waypoint):
    /// <list type="bullet">
    /// <item>a waypoint's own <c>Targets</c> name the next waypoint on a plain leg, and round a patrol loop;</item>
    /// <item>"go on after the patrol time" runs through <c>MCU_Timer</c> and <c>MCU_Counter</c> blocks (an attack area to
    /// its patrol loop, the loop to the way home);</item>
    /// <item><c>MCU_Deactivate</c> and <c>MCU_CheckZone</c> links point back or branch, and are not followed.</item>
    /// </list>
    /// The walk starts at <c>WAYPOINT-TAKEOFF</c> (or a waypoint nothing leads to), takes a direct link before a timed
    /// one, and visits each waypoint once, so a patrol loop is flown once round and its closing leg is not in the
    /// sequence. When the walk stops short of the landing waypoint, a chain of direct links that ends there is
    /// appended (the timed exit from a loop can be deeper than the walk looks). Attack areas Career leaves unlinked
    /// from this sequence (alternates for the same flight) stay out of it. Plan, not a live position.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<PlannedWaypoint>> BriefedRoutes { get; init; }
        = new Dictionary<int, IReadOnlyList<PlannedWaypoint>>();

    /// <summary>Every airfield in the mission, any country.</summary>
    public IReadOnlyList<PlannedAirfield> Airfields { get; init; } = [];

    /// <summary>
    /// The player's home field: his country's airfield nearest the flight's landing waypoint, or, without one,
    /// nearest where the player is briefed to start. Null when his country has no airfield in the mission.
    /// </summary>
    public PlannedAirfield? HomeField(IEnumerable<IReadOnlyList<PlannedWaypoint>> chains)
    {
        if (Player is not { } player)
            return null;
        var landing = chains.SelectMany(c => c).FirstOrDefault(w => w.IsLanding);
        var from = landing?.Position ?? player.StartPosition;
        return Airfields
            .Where(a => a.Country == player.Country)
            .MinBy(a => Math.Pow(a.Position.North - from.North, 2) + Math.Pow(a.Position.East - from.East, 2));
    }

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

        var waypoints = ParseRoutes(blocks, out var briefed);
        return new MissionPlan(options.GetString("GuiMap"), options.Get("Date") ?? "", options.Get("Time") ?? "", countries, aircraft)
        {
            Weather = MissionWeather.FromOptions(options),
            Waypoints = waypoints,
            BriefedRoutes = briefed,
            Routes = waypoints.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<IReadOnlyList<WorldPoint>>)kv.Value.Select(path => (IReadOnlyList<WorldPoint>)path.Select(w => w.Position).ToList()).ToList()),
            Airfields = Walk(blocks)
                .Where(b => b.Type == "Airfield" && !double.IsNaN(b.GetDouble("XPos")) && !double.IsNaN(b.GetDouble("ZPos")))
                .Select(b => new PlannedAirfield(b.GetString("Name"), b.GetInt("Country"), b.GetInt("Callsign", 0),
                    WorldPoint.FromIl2(b.GetDouble("XPos"), double.IsNaN(b.GetDouble("YPos")) ? 0 : b.GetDouble("YPos"), b.GetDouble("ZPos"))))
                .ToList(),
        };
    }

    /// <summary>Waypoint chains keyed by the plane index their entity points at (<c>MisObjID</c>).</summary>
    private static Dictionary<int, IReadOnlyList<IReadOnlyList<PlannedWaypoint>>> ParseRoutes(IReadOnlyList<MissionBlock> roots,
        out Dictionary<int, IReadOnlyList<PlannedWaypoint>> briefed)
    {
        var all = Walk(roots).ToList();
        var blockByIndex = new Dictionary<int, MissionBlock>();
        foreach (var block in all)
        {
            int index = block.GetInt("Index");
            if (index >= 0)
                blockByIndex.TryAdd(index, block);
        }
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
                WorldPoint.FromIl2(x, double.IsNaN(y) ? 0 : y, z),
                block.GetString("Name")));
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

        var routes = new Dictionary<int, IReadOnlyList<IReadOnlyList<PlannedWaypoint>>>();
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

        briefed = new Dictionary<int, IReadOnlyList<PlannedWaypoint>>();
        foreach (var (entity, points) in byEntity)
        {
            if (planeOfEntity.TryGetValue(entity, out int plane) && !briefed.ContainsKey(plane)
                && BriefedOrder(points, blockByIndex) is { Count: > 0 } order)
                briefed[plane] = order;
        }

        return routes;
    }

    /// <summary>How many timer and counter blocks a "go on" link may pass through between two waypoints.</summary>
    private const int TimedLinkDepth = 6;

    /// <summary>One flight's waypoints in briefed order; see <see cref="MissionPlan.BriefedRoutes"/>.</summary>
    private static List<PlannedWaypoint> BriefedOrder(List<WaypointNode> points, Dictionary<int, MissionBlock> blocks)
    {
        var byIndex = points.GroupBy(p => p.Index).ToDictionary(g => g.Key, g => g.First());
        int[] Direct(WaypointNode w) => w.Targets.Where(byIndex.ContainsKey).ToArray();
        List<int> Timed(WaypointNode w)
        {
            var found = new List<int>();
            var seen = new HashSet<int>();
            var frontier = w.Targets.Where(t => IsTimer(t)).ToList();
            for (int depth = 0; depth < TimedLinkDepth && frontier.Count > 0; depth++)
            {
                var next = new List<int>();
                foreach (int t in frontier.Where(seen.Add))
                {
                    foreach (int u in IntList(blocks[t].Get("Targets")))
                    {
                        if (byIndex.ContainsKey(u))
                            found.Add(u);
                        else if (IsTimer(u))
                            next.Add(u);
                    }
                }
                frontier = next;
            }

            return found;
        }
        bool IsTimer(int index) => blocks.TryGetValue(index, out var b) && b.Type is "MCU_Timer" or "MCU_Counter";

        var incoming = new HashSet<int>();
        foreach (var w in byIndex.Values)
        {
            incoming.UnionWith(Direct(w));
            incoming.UnionWith(Timed(w));
        }

        var ordered = byIndex.Values.OrderBy(w => w.Index).ToList();
        var head = ordered.FirstOrDefault(w => w.Name.Contains("TAKEOFF", StringComparison.OrdinalIgnoreCase))
                   ?? ordered.FirstOrDefault(w => !incoming.Contains(w.Index))
                   ?? ordered.FirstOrDefault();
        if (head is null)
            return [];

        var visited = new HashSet<int> { head.Index };
        var sequence = new List<WaypointNode> { head };
        for (var at = head; ;)
        {
            int next = Direct(at).Concat(Timed(at)).FirstOrDefault(t => !visited.Contains(t), -1);
            if (next < 0)
                break;
            at = byIndex[next];
            visited.Add(next);
            sequence.Add(at);
        }

        // A loop's timed exit can run deeper than the walk looks: the way home is then a direct chain to the landing waypoint.
        static bool Landing(WaypointNode w) => w.Name.Contains("WAYPOINT_LAND", StringComparison.OrdinalIgnoreCase);
        if (!Landing(sequence[^1]))
        {
            foreach (var start in ordered.Where(w => !visited.Contains(w.Index)))
            {
                var tail = new List<WaypointNode>();
                var seen = new HashSet<int>(visited);
                for (var w = start; w is not null && seen.Add(w.Index); w = Direct(w).Where(t => !seen.Contains(t)).Select(t => byIndex[t]).FirstOrDefault())
                    tail.Add(w);
                bool fed = ordered.Any(o => !visited.Contains(o.Index) && o != start && Direct(o).Contains(start.Index));
                if (!fed && tail.Count > 0 && Landing(tail[^1]))
                {
                    sequence.AddRange(tail);
                    break;
                }
            }
        }

        return sequence.Select(w => new PlannedWaypoint(w.Position, w.Name)).ToList();
    }

    private static List<IReadOnlyList<PlannedWaypoint>> OrderPaths(List<WaypointNode> points)
    {
        var byIndex = points.GroupBy(p => p.Index).ToDictionary(g => g.Key, g => g.First());
        var incoming = Incoming(byIndex.Values, byIndex.Keys.ToHashSet());

        var heads = byIndex.Values.Where(p => !incoming.Contains(p.Index)).OrderBy(p => p.Index).ToList();
        if (heads.Count == 0 && byIndex.Count > 0)
            heads.Add(byIndex.Values.OrderBy(p => p.Index).First());

        var visited = new HashSet<int>();
        var paths = new List<IReadOnlyList<PlannedWaypoint>>();
        foreach (var head in heads)
            AddChain(byIndex, visited, paths, head);

        while (true)
        {
            var remaining = byIndex.Values.Where(p => !visited.Contains(p.Index)).OrderBy(p => p.Index).ToList();
            if (remaining.Count == 0)
                break;
            var remainIds = remaining.Select(p => p.Index).ToHashSet();
            var start = remaining.FirstOrDefault(p => !Incoming(remaining, remainIds).Contains(p.Index)) ?? remaining[0];
            int before = visited.Count;
            AddChain(byIndex, visited, paths, start);
            if (visited.Count == before)
                break;
        }

        return paths;
    }

    private static HashSet<int> Incoming(IEnumerable<WaypointNode> points, HashSet<int> ids)
    {
        var incoming = new HashSet<int>();
        foreach (var point in points)
        {
            foreach (int target in point.Targets)
            {
                if (ids.Contains(target))
                    incoming.Add(target);
            }
        }

        return incoming;
    }

    /// <summary>
    /// One chain from <paramref name="start"/>. When the last point targets the first, the first
    /// point is repeated so a patrol loop keeps the leg that closes it.
    /// </summary>
    private static void AddChain(
        Dictionary<int, WaypointNode> byIndex,
        HashSet<int> visited,
        List<IReadOnlyList<PlannedWaypoint>> paths,
        WaypointNode start)
    {
        var line = new List<WaypointNode>();
        var point = start;
        while (visited.Add(point.Index))
        {
            line.Add(point);
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

        if (line.Count == 0)
            return;

        var positions = line.Select(p => new PlannedWaypoint(p.Position, p.Name)).ToList();
        if (line[^1].Targets.Contains(line[0].Index))
            positions.Add(positions[0]);
        paths.Add(positions);
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

    private sealed record WaypointNode(int Index, int[] Targets, int[] Objects, WorldPoint Position, string Name);

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
