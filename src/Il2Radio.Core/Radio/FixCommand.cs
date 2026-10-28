using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Radio;

/// <summary>
/// "Fix position": the player looks out, picks a feature he recognises, and finds himself on his map sheet. It is his own
/// look, not a call, so the reply is a chat line labelled "Fix" that is never spoken, and it puts his mark on the
/// kneeboard. The features are the mission's airfields (mission metres, <see cref="CoordinateFrame.Il2World"/>); the
/// larger Stalingrad landmarks (the Volga, the Don, the city and towns) are not in yet, so away from a field the answer
/// is "can't place myself". The look at the ground is gated like a look at an aircraft: haze, darkness, and cloud
/// between him and the field. The mark is as tight as the view: low and close over a field gives its ninth of the
/// square, high or far the whole square. Refused while an enemy is on him: there is no time to read a map.
/// Only a memory fix is in the mission's frame, so a track fix gives no answer (<see cref="ReplyOutcome.NoCourse"/>).
/// </summary>
public static class FixCommand
{
    /// <summary>The chat label of the player's own fix.</summary>
    public const string Label = "Fix";

    /// <summary>
    /// Furthest an airfield can be made out and matched to the sheet in clear daylight (slant range). A grass Career
    /// field is a pale patch with a few buildings; SPECULATIVE, on the same game-design scale as spotting.
    /// </summary>
    public const double AirfieldSightMetres = 12_000;

    /// <summary>Within this slant range and height above the field, he can tell which ninth of the square he is over.</summary>
    public const double NinthWithinMetres = 5_000;

    public const double NinthBelowMetres = 2_000;

    /// <summary>Less than this share of the look getting through the cloud, the ground is hidden.</summary>
    public const double MinCloudTransmission = 0.5;

    public static RadioResult FixPosition(ContactTracker tracker, RadioSettings? settings = null)
    {
        settings ??= new RadioSettings();
        var suppressed = new List<SuppressedContact>();
        if (RadioPicture.Build(tracker, settings, (_, _) => false, suppressed) is not { } picture)
            return new RadioResult(ReplyOutcome.PlayerPositionUnknown, [], suppressed);

        var fix = picture.PlayerFix;
        if (fix.Position.Frame != CoordinateFrame.Il2World)
        {
            suppressed.Add(new(picture.Player, "your live fix is from the track recording, not IL-2's memory; it is not on the briefing map"));
            return new RadioResult(ReplyOutcome.NoCourse, [], suppressed);
        }

        if (EnemyOnPlayer(tracker, picture, settings) is { } enemy)
        {
            suppressed.Add(new(enemy, "enemy on you: no time to read the map"));
            return Own(picture, "can't fix position, enemy on me.", []);
        }

        var plan = tracker.Plan;
        if (plan is null || Landmark(plan, fix) is not { } seen)
            return Own(picture, "can't place myself: nothing I recognise in sight.", []);

        bool ninth = seen.SlantMetres <= NinthWithinMetres && fix.Position.Altitude - seen.Field.Position.Altitude <= NinthBelowMetres;
        string by = $"by {seen.Field.Name} airfield";
        if (!MapGrid.IsStalingrad(plan.GuiMap) || MapGrid.Containing(fix.Position.North, fix.Position.East, ninth) is not { } grid)
            return Own(picture, $"{by}, but this map has no grid on the sheet.", []);
        return Own(picture, $"{by}, grid {grid.Text}.", [new(SpeechWord.Grid, grid.Code, grid.Ninth?.ToString())]);
    }

    /// <summary>The nearest airfield he can see from here, if any.</summary>
    private static (PlannedAirfield Field, double SlantMetres)? Landmark(MissionPlan plan, PositionFix fix)
    {
        var weather = plan.Weather;
        var visibility = new Visibility(weather);
        var timeOfDay = weather.StartTime + TimeSpan.FromSeconds(fix.SourceTime);
        double reach = AirfieldSightMetres * (1 - 0.6 * weather.Haze) * Visibility.DaylightFactor(timeOfDay);
        return plan.Airfields
            .Select(f => (Field: f, SlantMetres: Slant(fix.Position, f.Position)))
            .Where(f => f.SlantMetres <= reach && visibility.CloudTransmission(fix.Position.Altitude, f.Field.Position.Altitude) >= MinCloudTransmission)
            .OrderBy(f => f.SlantMetres)
            .Select(f => ((PlannedAirfield, double)?)f)
            .FirstOrDefault();
    }

    /// <summary>An enemy aircraft with a live fix close enough to be fighting him (<see cref="PilotReport.EngagedMetres"/>).</summary>
    private static Contact? EnemyOnPlayer(ContactTracker tracker, RadioPicture picture, RadioSettings settings) =>
        tracker.Contacts
            .Where(c => !c.IsPlayer && c.IsOperational && c.Coalition >= 0 && !RadioPicture.IsFriendly(picture.Player, c))
            .FirstOrDefault(c => c.LastFix is { IsTrack: true } f && tracker.AgeSeconds(f) <= settings.MaxFixAgeSeconds
                                 && Slant(picture.PlayerFix.Position, f.Position) <= PilotReport.EngagedMetres);

    private static double Slant(WorldPoint a, WorldPoint b) =>
        Navigation.Relative(a, b) is { } r ? Math.Sqrt(r.RangeMetres * r.RangeMetres + r.AltitudeDeltaMetres * r.AltitudeDeltaMetres) : double.PositiveInfinity;

    /// <summary>The player's own line: kept off the headset by the session, like the route reading.</summary>
    private static RadioResult Own(RadioPicture picture, string body, List<SpeechToken> tokens) =>
        new(ReplyOutcome.Position,
            [new RadioReply(picture.Player, [picture.Player], new RelativePosition(0, 0, 0), 0, $"{Label}, {body}", tokens) { SpeakerLabel = Label }],
            picture.Suppressed);
}
