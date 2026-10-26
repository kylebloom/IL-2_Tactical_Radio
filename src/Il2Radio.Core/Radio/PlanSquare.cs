using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Radio;

/// <summary>
/// The map square a pilot may name when he cannot see the player. The square is his live position.
/// The mission route only decides whether he has been navigating: within one 10 km square of his
/// flight's waypoint chain he still knows where he is, and the game's grid stands in for the
/// plan-square on his knee board. No route, another map, or a fix within a kilometre of a grid
/// line, and he says nothing about it.
/// </summary>
internal static class PlanSquare
{
    /// <summary>Still on the briefed route: one map square either side of the waypoint chain.</summary>
    public const double OnRouteMetres = MapGrid.SquareMetres;

    public static GridSquare? Report(ContactGroup group, MissionPlan? plan)
    {
        if (plan is null || !MapGrid.IsStalingrad(plan.GuiMap))
            return null;
        if (group.Speaker.Fix.Latitude is not double latitude || group.Speaker.Fix.Longitude is not double longitude)
            return null;

        var (north, east) = MapGrid.ProjectStalingrad(latitude, longitude);
        if (DistanceToRoute(north, east, RoutesOf(plan, group.Speaker.Contact)) > OnRouteMetres)
            return null;
        return MapGrid.TryLabel(north, east);
    }

    private static List<IReadOnlyList<WorldPoint>> RoutesOf(MissionPlan plan, Contact speaker)
    {
        var self = plan.AircraftOf(speaker.PilotName, speaker.Country);
        if (self is null)
            return [];

        IEnumerable<PlannedAircraft> flight = self.Callsign > 0
            ? plan.Aircraft.Where(a => a.Country == self.Country && a.Callsign == self.Callsign)
            : [self];

        var paths = new List<IReadOnlyList<WorldPoint>>();
        foreach (var aircraft in flight)
        {
            if (plan.Routes.TryGetValue(aircraft.Index, out var route))
                paths.AddRange(route);
        }

        return paths;
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
