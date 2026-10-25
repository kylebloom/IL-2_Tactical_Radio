namespace Il2Radio.Core.Geometry;

/// <summary>
/// Identifies which coordinate system a position belongs to. Positions from different frames
/// must never be compared, because their origins are not known to coincide.
/// </summary>
public enum CoordinateFrame
{
    /// <summary>IL-2 mission/log coordinates: X = north (m), Z = east (m), Y = altitude ASL (m).</summary>
    Il2World,

    /// <summary>Local tangent plane derived from a Tacview ACMI stream (north/east metres from the ACMI reference).</summary>
    Acmi,
}

/// <summary>A horizontal position plus altitude, expressed as north/east/up metres in a specific frame.</summary>
public readonly record struct WorldPoint(double North, double East, double Altitude, CoordinateFrame Frame)
{
    /// <summary>Creates a point from IL-2 native coordinates as written in .Mission files and mission logs.</summary>
    public static WorldPoint FromIl2(double x, double y, double z) => new(x, z, y, CoordinateFrame.Il2World);

    public override string ToString() => $"N{North:F0} E{East:F0} alt {Altitude:F0} m [{Frame}]";
}

public readonly record struct RelativePosition(double BearingDegrees, double RangeMetres, double AltitudeDeltaMetres);

public static class Navigation
{
    /// <summary>
    /// Bearing (true, degrees clockwise from north), horizontal range and altitude difference from
    /// <paramref name="from"/> to <paramref name="to"/>. Returns null when the frames differ.
    /// </summary>
    public static RelativePosition? Relative(WorldPoint from, WorldPoint to)
    {
        if (from.Frame != to.Frame)
            return null;

        double dn = to.North - from.North;
        double de = to.East - from.East;
        double bearing = Math.Atan2(de, dn) * 180.0 / Math.PI;
        if (bearing < 0)
            bearing += 360.0;

        return new RelativePosition(bearing, Math.Sqrt(dn * dn + de * de), to.Altitude - from.Altitude);
    }

    /// <summary>Eight-point compass name for a bearing, e.g. 315 → "northwest".</summary>
    public static string CompassPoint(double bearingDegrees)
    {
        string[] names = ["north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"];
        int index = (int)Math.Round(((bearingDegrees % 360) + 360) % 360 / 45.0) % 8;
        return names[index];
    }
}
