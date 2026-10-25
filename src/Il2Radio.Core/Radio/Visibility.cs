using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Radio;

/// <summary>How much an observer can tell about an aircraft, from least to most.</summary>
public enum IdentificationLevel
{
    /// <summary>Not seen at all.</summary>
    None,

    /// <summary>Seen, but only as "aircraft": side and type unknown.</summary>
    Sighted,

    /// <summary>Size and shape: fighter, bomber, ground-attack...</summary>
    Class,

    /// <summary>Markings and silhouette: friend or foe.</summary>
    Side,

    /// <summary>Type: "Messerschmitt", "Yak".</summary>
    Type,
}

public sealed record VisibilitySettings
{
    /// <summary>Range at which a look has a 50 % chance of spotting the aircraft, per metre of wingspan (fighter ≈ 4 km).</summary>
    public double DetectMetresPerSpanMetre { get; init; } = 400;

    /// <summary>Nothing is spotted beyond this multiple of the 50 % range (fighter ≈ 9 km, bomber ≈ 20 km).</summary>
    public double MaxDetectFactor { get; init; } = 2.2;

    /// <summary>Recognition ranges per metre of wingspan: fighter class ≈ 4 km, side ≈ 3 km, type ≈ 1.8 km.</summary>
    public double ClassMetresPerSpanMetre { get; init; } = 400;

    public double SideMetresPerSpanMetre { get; init; } = 300;

    public double TypeMetresPerSpanMetre { get; init; } = 180;

    /// <summary>An aircraft below the observer is seen against the ground, which shortens all ranges by this factor.</summary>
    public double AgainstGroundFactor { get; init; } = 0.75;

    /// <summary>Chance that a type call made near the edge of type range names a look-alike (always hedged).</summary>
    public double MisidentificationChance { get; init; } = 0.12;
}

/// <summary>
/// A simple, deterministic model of what WWII pilots could see. It is a game-design model, not a
/// physiological one: spotting probability falls with range relative to the target's size, and is
/// reduced by haze, darkness, the ground background and cloud between observer and target.
/// Identification needs progressively shorter ranges (class, then side, then type).
/// It only ever judges aircraft that exist in the live track; it never invents contacts.
/// </summary>
public sealed class Visibility(MissionWeather weather, VisibilitySettings? settings = null)
{
    private readonly VisibilitySettings _s = settings ?? new VisibilitySettings();

    public VisibilitySettings Settings => _s;

    /// <summary>Probability (0–1) that one look from <paramref name="observer"/> spots an aircraft at <paramref name="target"/>.</summary>
    public double DetectionProbability(WorldPoint observer, WorldPoint target, AircraftType type, TimeSpan timeOfDay)
    {
        if (Navigation.Relative(observer, target) is not { } rel)
            return 0;
        double range3d = Math.Sqrt(rel.RangeMetres * rel.RangeMetres + rel.AltitudeDeltaMetres * rel.AltitudeDeltaMetres);

        double r50 = _s.DetectMetresPerSpanMetre * type.WingspanMetres * ConditionFactor(rel, timeOfDay);
        if (r50 <= 0 || range3d > r50 * _s.MaxDetectFactor)
            return 0;

        double p = 1.0 / (1.0 + Math.Pow(range3d / r50, 4));
        return p * CloudTransmission(observer.Altitude, target.Altitude);
    }

    /// <summary>The most an observer can tell at this geometry, ignoring luck (used once the aircraft is spotted).</summary>
    public IdentificationLevel MaxIdentification(WorldPoint observer, WorldPoint target, AircraftType type, TimeSpan timeOfDay)
    {
        if (Navigation.Relative(observer, target) is not { } rel)
            return IdentificationLevel.None;
        double range3d = Math.Sqrt(rel.RangeMetres * rel.RangeMetres + rel.AltitudeDeltaMetres * rel.AltitudeDeltaMetres);
        double f = ConditionFactor(rel, timeOfDay) * type.WingspanMetres;
        return range3d <= _s.TypeMetresPerSpanMetre * f ? IdentificationLevel.Type
            : range3d <= _s.SideMetresPerSpanMetre * f ? IdentificationLevel.Side
            : range3d <= _s.ClassMetresPerSpanMetre * f ? IdentificationLevel.Class
            : IdentificationLevel.Sighted;
    }

    /// <summary>Where within type range the observer is: 0 at the limit, 1 right on top. Near the limit, look-alikes get confused.</summary>
    public double TypeConfidence(WorldPoint observer, WorldPoint target, AircraftType type, TimeSpan timeOfDay)
    {
        if (Navigation.Relative(observer, target) is not { } rel)
            return 0;
        double range3d = Math.Sqrt(rel.RangeMetres * rel.RangeMetres + rel.AltitudeDeltaMetres * rel.AltitudeDeltaMetres);
        double limit = _s.TypeMetresPerSpanMetre * type.WingspanMetres * ConditionFactor(rel, timeOfDay);
        return limit <= 0 ? 0 : Math.Clamp(1 - range3d / limit, 0, 1);
    }

    private double ConditionFactor(RelativePosition rel, TimeSpan timeOfDay)
    {
        double factor = 1 - 0.6 * weather.Haze;
        if (rel.AltitudeDeltaMetres < -300)
            factor *= _s.AgainstGroundFactor; // target below: seen against the ground
        return factor * DaylightFactor(timeOfDay);
    }

    /// <summary>Crude day/night curve (no sun position yet): full daylight 06:00–20:00, dusk and dawn either side.</summary>
    public static double DaylightFactor(TimeSpan timeOfDay)
    {
        double h = ((timeOfDay.TotalHours % 24) + 24) % 24;
        return h is >= 6 and < 20 ? 1.0
            : h is >= 5 and < 6 ? 0.6
            : h is >= 20 and < 21 ? 0.6
            : 0.2;
    }

    /// <summary>
    /// Share of a look that gets through the cloud layer. A line of sight from below the deck to above it
    /// (or either end inside it) passes through cloud; partial cover leaves gaps.
    /// </summary>
    public double CloudTransmission(double observerAltitude, double targetAltitude)
    {
        if (weather.CloudCoverage <= 0 || weather.CloudThicknessMetres <= 0)
            return 1;
        double low = Math.Min(observerAltitude, targetAltitude), high = Math.Max(observerAltitude, targetAltitude);
        bool crosses = high >= weather.CloudBaseMetres && low <= weather.CloudTopMetres;
        return crosses ? 1 - weather.CloudCoverage : 1;
    }

    /// <summary>
    /// Deterministic "dice" in [0, 1) for one observer looking at one target during one time slot, so
    /// asking twice in quick succession gives the same answer, while later looks re-roll.
    /// </summary>
    public static double Roll(string observerKey, string targetKey, long slot, int salt = 0)
    {
        unchecked
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in $"{observerKey}|{targetKey}|{slot}|{salt}")
            {
                h ^= c;
                h *= 1099511628211UL;
            }

            h ^= h >> 33;
            h *= 0xff51afd7ed558ccdUL;
            h ^= h >> 33;
            return (h >> 11) / (double)(1UL << 53);
        }
    }
}
