using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Radio;

/// <summary>
/// The fixed map sheet of the kneeboard plot: like a map folded to the briefed area, it covers the player's briefed route
/// and his home field, plus about one 10 km square of margin, and never pans. An escort with no route of its own takes
/// the route of the flight it was ordered to cover, labelled as theirs (<see cref="KneeboardSheet.RouteOf"/>). Bounds are mission metres
/// (<see cref="CoordinateFrame.Il2World"/>), snapped out to the 10 km grid lines so the squares fall whole on the sheet.
/// </summary>
/// <param name="Labelled">The map's squares can be named (Stalingrad only, <see cref="MapGrid"/>).</param>
/// <param name="RouteOf">Null when <see cref="Route"/> is the player's own. Otherwise the covered flight's class, as a pilot would note it ("bombers").</param>
/// <param name="OwnFrom">Index of the first point of the escort's own leg home, when <see cref="Route"/> begins with the covered flight's route. Null when the whole line is one party's.</param>
public sealed record KneeboardSheet(
    string MapName,
    double South, double West, double North, double East,
    IReadOnlyList<PlannedWaypoint> Route,
    PlannedAirfield? HomeField,
    bool Labelled,
    string? RouteOf = null,
    int? OwnFrom = null)
{
    /// <summary>Margin round the briefed area: one map square.</summary>
    public const double MarginMetres = MapGrid.SquareMetres;

    /// <summary>The sheet covers at least this each way (five squares), however short the briefed route.</summary>
    public const double MinSpanMetres = 5 * MapGrid.SquareMetres;

    /// <summary>The sheet is kept between these width-to-height ratios, so the panel is never a strip.</summary>
    public const double MinAspect = 1.0, MaxAspect = 1.5;

    public double WidthMetres => East - West;
    public double HeightMetres => North - South;

    public bool Contains(double north, double east) => north >= South && north <= North && east >= West && east <= East;

    /// <summary>"BRIEFED ROUTE", or "BOMBERS' ROUTE" when <see cref="RouteOf"/> names the covered flight.</summary>
    public string RouteHeading => RouteOf switch
    {
        null => "BRIEFED ROUTE",
        "their route" => "THEIR ROUTE",
        _ => $"{RouteOf.ToUpperInvariant()}' ROUTE",
    };

    /// <summary>The sheet for the player's sortie, or null when the plan has no player or no briefed route.</summary>
    public static KneeboardSheet? For(MissionPlan plan)
    {
        if (plan.Player is not { } player)
            return null;
        // Career gives the route to the flight's leader, so look across the player's flight (same country and callsign).
        var flight = plan.Aircraft.Where(a => a.Country == player.Country && (player.Callsign > 0 ? a.Callsign == player.Callsign : a == player)).ToList();
        var chains = flight.SelectMany(a => plan.Waypoints.GetValueOrDefault(a.Index) ?? []).ToList();
        var own = flight.Select(a => plan.BriefedRoutes.GetValueOrDefault(a.Index)).FirstOrDefault(r => r is { Count: > 0 })
            ?? chains.OrderByDescending(c => c.Count).FirstOrDefault() ?? [];
        // An escort is briefed with only its landing point. The route it was ordered to cover is the plan on the map.
        string? routeOf = null;
        int? ownFrom = null;
        var route = own;
        if (!HasOutbound(own) && Covered(plan, flight) is { } covered)
            (route, routeOf, ownFrom) = covered;
        if (route.Count == 0)
            return null;

        var home = plan.HomeField(chains.Count > 0 ? chains : [route]);
        var points = route.Select(w => w.Position).ToList();
        if (home is not null)
            points.Add(home.Position);

        double south = points.Min(p => p.North) - MarginMetres, north = points.Max(p => p.North) + MarginMetres;
        double west = points.Min(p => p.East) - MarginMetres, east = points.Max(p => p.East) + MarginMetres;
        // An escort is briefed with only its landing point: the sheet still shows the country round the field.
        if (north - south < MinSpanMetres)
            (south, north) = Widen(south, north, MinSpanMetres);
        if (east - west < MinSpanMetres)
            (west, east) = Widen(west, east, MinSpanMetres);
        // A long thin sortie still gets a sheet a pilot could fold: widen the short side about the middle.
        double width = east - west, height = north - south;
        if (width < height * MinAspect)
            (west, east) = Widen(west, east, height * MinAspect);
        else if (width > height * MaxAspect)
            (south, north) = Widen(south, north, width / MaxAspect);

        double s = MapGrid.SquareMetres;
        return new KneeboardSheet(NameOf(plan.GuiMap),
            Math.Floor(south / s) * s, Math.Floor(west / s) * s, Math.Ceiling(north / s) * s, Math.Ceiling(east / s) * s,
            route, home, MapGrid.IsStalingrad(plan.GuiMap), routeOf, ownFrom);
    }

    /// <summary>A route that leaves the field: two or more points, and one of them is not the landing waypoint.</summary>
    private static bool HasOutbound(IReadOnlyList<PlannedWaypoint> route) =>
        route.Count >= 2 && route.Any(w => !w.IsLanding);

    /// <summary>The covered flight's briefed route, and the word a pilot would write beside it ("bombers").</summary>
    private static (IReadOnlyList<PlannedWaypoint> Route, string Label, int? OwnFrom)? Covered(MissionPlan plan, List<PlannedAircraft> flight)
    {
        foreach (var aircraft in flight)
        {
            if (!plan.CoverOf.TryGetValue(aircraft.Index, out int coveredIndex))
                continue;
            if (plan.Aircraft.FirstOrDefault(a => a.Index == coveredIndex) is not { } covered)
                continue;
            var theirs = plan.Aircraft.Where(a => a.Country == covered.Country
                && (covered.Callsign > 0 ? a.Callsign == covered.Callsign : a.Index == covered.Index)).ToList();
            var outbound = theirs.Select(a => plan.BriefedRoutes.GetValueOrDefault(a.Index)).FirstOrDefault(r => r is { Count: >= 2 })
                ?? theirs.SelectMany(a => plan.Waypoints.GetValueOrDefault(a.Index) ?? []).OrderByDescending(c => c.Count).FirstOrDefault();
            if (outbound is not { Count: >= 2 })
                continue;
            var route = new List<PlannedWaypoint>(outbound);
            int? ownFrom = null;
            if (theirs.Select(a => plan.AfterObjective.GetValueOrDefault(a.Index)).FirstOrDefault(r => r is { Count: > 0 }) is { } homeward)
            {
                route.AddRange(homeward);
                // Once the bombers are back at their field, the escort's own landing is the leg on to its home.
                var landing = flight.SelectMany(a => plan.BriefedRoutes.GetValueOrDefault(a.Index) ?? []).FirstOrDefault(w => w.IsLanding);
                if (landing is not null && Apart(route[^1].Position, landing.Position))
                {
                    ownFrom = route.Count;
                    route.Add(landing);
                }
            }
            string label = theirs.Select(ClassNote).FirstOrDefault(n => n != "their route") ?? "their route";
            return (route, label, ownFrom);
        }
        return null;
    }

    /// <summary>Far enough apart to be its own corner, not the same field written twice.</summary>
    private static bool Apart(WorldPoint a, WorldPoint b) =>
        Math.Sqrt(Math.Pow(a.North - b.North, 2) + Math.Pow(a.East - b.East, 2)) > 2_000;

    /// <summary>The class word for a covered flight, from its mission-file script name. "their route" when the type is unknown.</summary>
    private static string ClassNote(PlannedAircraft aircraft) => AircraftTypes.ClassifyScript(aircraft.TypeKey).Class switch
    {
        AircraftClass.Bomber => "bombers",
        AircraftClass.Fighter => "fighters",
        AircraftClass.Attack => "attack",
        AircraftClass.Recon => "recon",
        AircraftClass.Transport => "transports",
        _ => "their route",
    };

    private static (double Low, double High) Widen(double low, double high, double size)
    {
        double middle = (low + high) / 2;
        return (middle - size / 2, middle + size / 2);
    }

    /// <summary>"stalingrad-1942" (Career's GuiMap) → "Stalingrad".</summary>
    private static string NameOf(string guiMap)
    {
        string name = guiMap.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p => p.All(char.IsLetter)) ?? "";
        return name.Length == 0 ? "Map" : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
    }
}
