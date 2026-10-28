using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Radio;

/// <summary>
/// The map square a pilot may name when he cannot see the player. The square is always his live
/// position. A member of the player's flight has been navigating on a waypoint chain, so he names
/// the square only while he is still within one 10 km square of that chain. Another friendly flight
/// usually has no chain to follow — Career briefs many of them with an attack area and a
/// return-to-base point — so hearing the call is enough: he names the square he is in. Another map,
/// or a fix within a kilometre of a grid line, and he says nothing about it.
/// </summary>
internal static class PlanSquare
{
    /// <summary>Still on the briefed route: one map square either side of the waypoint chain.</summary>
    public const double OnRouteMetres = MapGrid.SquareMetres;

    public static GridSquare? Report(ContactGroup group, MissionPlan? plan)
    {
        if (plan is null || !MapGrid.IsStalingrad(plan.GuiMap))
            return null;
        if (MapMetres(group.Speaker.Fix, plan) is not { } at)
            return null;
        var (north, east) = at;

        if (group.Speaker.Contact.InPlayerFlight
            && DistanceToRoute(north, east, Routes(plan, group.Speaker.Contact)) > OnRouteMetres)
            return null;
        return MapGrid.TryLabel(north, east);
    }

    /// <summary>
    /// Where a live fix lies in mission-file metres, the frame of the waypoints, airfields and map grid. A memory fix is
    /// already there, exactly, on any map. An ACMI fix is brought there only through the Stalingrad latitude fit (within
    /// about 360 m), good enough to name a 10 km square or to tell whether a flight is on its route. That fit is never
    /// used for a course or a distance to a briefed point: those are given from memory fixes alone (<see cref="RouteCommands"/>).
    /// </summary>
    internal static (double North, double East)? MapMetres(PositionFix fix, MissionPlan plan)
    {
        if (fix.Position.Frame == CoordinateFrame.Il2World)
            return (fix.Position.North, fix.Position.East);
        if (MapGrid.IsStalingrad(plan.GuiMap) && fix.Latitude is double latitude && fix.Longitude is double longitude)
            return MapGrid.ProjectStalingrad(latitude, longitude);
        return null;
    }

    /// <summary>Briefed waypoint chains for this pilot's flight (same callsign), in mission-file metres.</summary>
    internal static List<IReadOnlyList<WorldPoint>> Routes(MissionPlan plan, Contact speaker) =>
        Chains(plan, speaker).Select(path => (IReadOnlyList<WorldPoint>)path.Select(w => w.Position).ToList()).ToList();

    /// <summary>The same chains as <see cref="Routes"/>, with each waypoint's name.</summary>
    internal static List<IReadOnlyList<PlannedWaypoint>> Chains(MissionPlan plan, Contact speaker)
    {
        var paths = new List<IReadOnlyList<PlannedWaypoint>>();
        foreach (var aircraft in Flight(plan, speaker))
        {
            if (plan.Waypoints.TryGetValue(aircraft.Index, out var route))
                paths.AddRange(route);
        }

        return paths;
    }

    /// <summary>This pilot's flight's waypoints in briefed order (<see cref="MissionPlan.BriefedRoutes"/>); empty when the plan has none.</summary>
    internal static IReadOnlyList<PlannedWaypoint> Briefed(MissionPlan plan, Contact speaker) =>
        Flight(plan, speaker).Select(a => plan.BriefedRoutes.GetValueOrDefault(a.Index)).FirstOrDefault(r => r is { Count: > 0 }) ?? [];

    /// <summary>The planned aircraft of this pilot's flight (same country and callsign), or himself without a callsign.</summary>
    private static IEnumerable<PlannedAircraft> Flight(MissionPlan plan, Contact speaker)
    {
        var self = plan.AircraftOf(speaker.PilotName, speaker.Country);
        if (self is null)
            return [];
        return self.Callsign > 0
            ? plan.Aircraft.Where(a => a.Country == self.Country && a.Callsign == self.Callsign)
            : [self];
    }

    private static double DistanceToRoute(double north, double east, List<IReadOnlyList<WorldPoint>> paths)
    {
        if (paths.Count == 0)
            return double.PositiveInfinity;

        double best = double.PositiveInfinity;
        foreach (var path in paths)
            best = Math.Min(best, DistanceToPath(north, east, path));
        return best;
    }

    private static double DistanceToPath(double north, double east, IReadOnlyList<WorldPoint> path)
    {
        if (path.Count == 0)
            return double.PositiveInfinity;
        if (path.Count == 1)
            return Hypot(north - path[0].North, east - path[0].East);

        double best = double.PositiveInfinity;
        for (int i = 1; i < path.Count; i++)
            best = Math.Min(best, DistanceToSegment(north, east, path[i - 1], path[i]));
        return best;
    }

    private static double DistanceToSegment(double north, double east, WorldPoint a, WorldPoint b)
    {
        double dn = b.North - a.North, de = b.East - a.East;
        double lengthSquared = dn * dn + de * de;
        if (lengthSquared < 1)
            return Hypot(north - a.North, east - a.East);

        double t = Math.Clamp(((north - a.North) * dn + (east - a.East) * de) / lengthSquared, 0, 1);
        return Hypot(north - (a.North + t * dn), east - (a.East + t * de));
    }

    private static double Hypot(double north, double east) => Math.Sqrt(north * north + east * east);
}
