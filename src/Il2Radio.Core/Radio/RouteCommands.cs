using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Radio;

/// <summary>
/// The player's place on his briefed route, and a steer home or to the target: what he would otherwise open IL-2's
/// map for. The answer is measured from the player's live fix to points in the mission file (waypoints, airfields),
/// which are in mission metres (<see cref="CoordinateFrame.Il2World"/>). Only IL-2's memory gives a live fix in that
/// frame (docs/MEMORY.md; an aircraft parked at Tusov read 0.8 km from the mission file's Tusov, at the field's height,
/// on 2026-10-28), so a leg, a course or a distance is given only from a memory fix. An ACMI fix is never measured
/// against the plan: the route call then names only the map square (through the same Stalingrad fit every square uses)
/// and the outcome is <see cref="ReplyOutcome.NoCourse"/>.
/// <para>
/// Who answers: the player's AI leader, the lowest living formation number below the player's, when he is live within
/// radio range and, under <see cref="RadioRealism.Pilot"/>, in formation with the player or able to see him this look
/// (the same gate as the other commands). He is navigating the flight and knows where the player is. Otherwise, when
/// the player leads, flies alone, or the leader cannot place him, it is the player's own map reading: a chat line
/// labeled "Route" that is not spoken, like the route corner call. A destroyed leader never answers; the next living
/// number leads.
/// </para>
/// </summary>
public static class RouteCommands
{
    /// <summary>The chat label of the player's own map reading.</summary>
    public const string OwnLabel = "Route";

    /// <summary>Within this of the waypoint ending his leg, the player is at it: the next leg is the one he is on.</summary>
    public const double AtWaypointMetres = UnaskedCalls.CornerAtMetres;

    /// <summary>"Where am I on the route": map square, leg of the briefed chain, and the next waypoint as a course and distance.</summary>
    public static RadioResult RoutePosition(ContactTracker tracker, RadioSettings? settings = null)
    {
        settings ??= new RadioSettings();
        var suppressed = new List<SuppressedContact>();
        if (RadioPicture.Build(tracker, settings, RadioPicture.IsFlightMate, suppressed) is not { } picture)
            return new RadioResult(ReplyOutcome.PlayerPositionUnknown, [], suppressed);

        var navigator = Navigator(picture, tracker, settings, suppressed);
        var plan = tracker.Plan;
        var fix = picture.PlayerFix;
        var parts = new List<string>();
        var tokens = new List<SpeechToken>();
        if (plan is not null && MapGrid.IsStalingrad(plan.GuiMap) && PlanSquare.MapMetres(fix, plan) is { } at
            && MapGrid.TryLabel(at.North, at.East) is { } grid)
        {
            parts.Add($"grid {grid.Text}");
            tokens.Add(new(SpeechWord.Grid, grid.Code, grid.Ninth?.ToString()));
        }

        if (!InMissionFrame(fix))
        {
            suppressed.Add(new(picture.Player, FrameReason));
            RadioReply[] squareOnly = parts.Count > 0 ? [Reply(navigator, picture, string.Join(", ", parts) + ".", tokens)] : [];
            return new RadioResult(ReplyOutcome.NoCourse, squareOnly, suppressed);
        }

        if (Locate(plan is null ? [] : Route(plan, picture.Player), fix.Position) is not { } place)
        {
            parts.Add("no briefed route");
            return new RadioResult(ReplyOutcome.Position, [Reply(navigator, picture, string.Join(", ", parts) + ".", tokens)], suppressed);
        }

        int legs = place.Path.Count - 1;
        if (legs > 0)
        {
            string leg = $"leg {RadioPhrases.Count(place.Leg)} of {RadioPhrases.Count(legs)}";
            if (place.OffMetres > PlanSquare.OnRouteMetres)
            {
                parts.Add($"{Distance(place.OffMetres)} off the route, nearest {leg}");
                tokens.Add(new(SpeechWord.OffRoute, place.OffMetres));
            }
            else
            {
                parts.Add(leg);
            }
            tokens.Add(new(SpeechWord.Leg, place.Leg, legs.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var next = place.Path[place.Next];
        string? kind = Kind(next);
        var to = Navigation.Relative(fix.Position, next.Position)!.Value;
        tokens.Add(new(SpeechWord.Waypoint, 0, kind));
        string head = parts.Count > 0 ? string.Join(", ", parts) + ". " : "";
        string body = $"{head}{(head.Length > 0 ? "Next" : "next")} waypoint{(kind is null ? "" : $", {kind}")}, {CourseAndDistance(to, tokens)}.";
        return new RadioResult(ReplyOutcome.Position, [Reply(navigator, picture, body, tokens)], suppressed);
    }

    /// <summary>A course and distance from the player's live position to his home field (his country's field by the landing waypoint).</summary>
    public static RadioResult SteerHome(ContactTracker tracker, RadioSettings? settings = null) =>
        Steer(tracker, settings, (plan, player, _) => plan.HomeField(PlanSquare.Chains(plan, player)) is { } home ? (home.Name, home.Position) : null,
            "no home field in the briefing");

    /// <summary>
    /// A course and distance to the briefed target: the next attack-area waypoint ahead on the player's route, or, past
    /// the last one, the nearest. An escort or a free hunt with no attack area has none, and says so.
    /// </summary>
    public static RadioResult SteerTarget(ContactTracker tracker, RadioSettings? settings = null) =>
        Steer(tracker, settings, (plan, player, at) => AttackAhead(Route(plan, player), at) is { } target ? ("target area", target) : null,
            "no target area in the briefing");

    /// <summary>
    /// The route the player's flight was briefed to fly, as one sequence (<see cref="MissionPlan.BriefedRoutes"/>); a plan
    /// without one (an editor mission whose waypoints link only directly) falls back to its chains.
    /// </summary>
    private static List<IReadOnlyList<PlannedWaypoint>> Route(MissionPlan plan, Contact player) =>
        PlanSquare.Briefed(plan, player) is { Count: > 0 } briefed ? [briefed] : PlanSquare.Chains(plan, player);

    private static RadioResult Steer(ContactTracker tracker, RadioSettings? settings,
        Func<MissionPlan, Contact, WorldPoint, (string Name, WorldPoint Where)?> destination, string none)
    {
        settings ??= new RadioSettings();
        var suppressed = new List<SuppressedContact>();
        if (RadioPicture.Build(tracker, settings, RadioPicture.IsFlightMate, suppressed) is not { } picture)
            return new RadioResult(ReplyOutcome.PlayerPositionUnknown, [], suppressed);

        var navigator = Navigator(picture, tracker, settings, suppressed);
        var fix = picture.PlayerFix;
        if (!InMissionFrame(fix))
        {
            suppressed.Add(new(picture.Player, FrameReason));
            return new RadioResult(ReplyOutcome.NoCourse, [], suppressed);
        }

        var plan = tracker.Plan;
        if (plan is null || destination(plan, picture.Player, fix.Position) is not { } place
            || Navigation.Relative(fix.Position, place.Where) is not { } to)
            return new RadioResult(ReplyOutcome.Position, [Reply(navigator, picture, none + ".", [])], suppressed);

        List<SpeechToken> tokens = [new(SpeechWord.Destination, 0, place.Name)];
        string body = $"{place.Name}, {CourseAndDistance(to, tokens)}.";
        return new RadioResult(ReplyOutcome.Position, [Reply(navigator, picture, body, tokens)], suppressed);
    }

    private const string FrameReason =
        "your live fix is from the track recording, not IL-2's memory; no leg, course or distance is measured from it to the briefing";

    /// <summary>Waypoints and airfields are mission metres; only a memory fix is in that frame.</summary>
    private static bool InMissionFrame(PositionFix fix) => fix.Position.Frame == CoordinateFrame.Il2World;

    /// <summary>
    /// The AI leader who answers, or null for the player's own map reading. Under <see cref="RadioRealism.Pilot"/> he
    /// must be in formation with the player or see him this look; a leader who cannot is listed as suppressed.
    /// </summary>
    private static LiveContact? Navigator(RadioPicture picture, ContactTracker tracker, RadioSettings settings, List<SuppressedContact> suppressed)
    {
        var leader = picture.Live.Where(l => l.Contact.FormationIndex is not null).MinBy(l => l.Contact.FormationIndex);
        if (leader is null || (picture.Player.FormationIndex is int own && own <= leader.Contact.FormationIndex))
            return null;
        if (settings.Realism == RadioRealism.Precise
            || settings.InFormation(leader.Relative)
            || PilotReport.CanSeePlayer(leader, picture.Player, picture.PlayerFix, tracker, settings))
            return leader;
        suppressed.Add(new(leader.Contact, "leader cannot see you and is not in formation; your own map reading answers"));
        return null;
    }

    private static RadioReply Reply(LiveContact? navigator, RadioPicture picture, string body, List<SpeechToken> tokens)
    {
        if (navigator is { } n)
            return new RadioReply(n.Contact, [n.Contact], n.Relative, n.AgeSeconds, $"{RadioPhrases.Who(n.Contact)}, {body}",
                [.. SpeechToken.Identify(n.Contact), .. tokens]);
        // The player's own reading: kept off the headset by the session (the speaker is the player), like the route corner call.
        return new RadioReply(picture.Player, [picture.Player], new RelativePosition(0, 0, 0), 0, $"{OwnLabel}, {body}", tokens)
        { SpeakerLabel = OwnLabel };
    }

    /// <summary>"heading 095, 18 kilometres", and the <see cref="SpeechWord.Course"/> and <see cref="SpeechWord.Range"/> tokens that say it.</summary>
    private static string CourseAndDistance(RelativePosition to, List<SpeechToken> tokens)
    {
        int course = Navigation.CourseDegrees(to.BearingDegrees);
        tokens.Add(new(SpeechWord.Course, course));
        tokens.Add(new(SpeechWord.Range, to.RangeMetres));
        return $"heading {course:000}, {Distance(to.RangeMetres)}";
    }

    /// <summary>Whole kilometres, as a pilot reads them off a map; "under two kilometres" closer in, like the other replies.</summary>
    internal static string Distance(double metres) =>
        metres < 1_500 ? "under two kilometres" : $"{Math.Round(metres / 1000.0, MidpointRounding.AwayFromZero):F0} kilometres";

    private static string? Kind(PlannedWaypoint waypoint) =>
        waypoint.IsAttack ? "attack area" : waypoint.IsLanding ? "landing" : null;

    /// <summary>
    /// The player's place on his chains: the nearest leg (a later leg wins a tie, so a waypoint just flown over counts as
    /// passed), and the waypoint ending it, or the one after when he is already within <see cref="AtWaypointMetres"/> of
    /// it. <see cref="RoutePlace.Leg"/> counts from 1; a chain of one waypoint has no legs.
    /// </summary>
    private static RoutePlace? Locate(List<IReadOnlyList<PlannedWaypoint>> chains, WorldPoint at)
    {
        RoutePlace? best = null;
        foreach (var path in chains)
        {
            if (path.Count == 1 && (best is null || Hypot(at, path[0].Position) < best.OffMetres))
                best = new RoutePlace(path, 0, 0, Hypot(at, path[0].Position));
            RoutePlace? inPath = null;
            for (int i = 1; i < path.Count; i++)
            {
                double d = SegmentDistance(at, path[i - 1].Position, path[i].Position);
                if (inPath is null || d <= inPath.OffMetres + 1)
                    inPath = new RoutePlace(path, i, i, d);
            }
            if (inPath is not null && (best is null || inPath.OffMetres < best.OffMetres))
                best = inPath;
        }

        if (best is not { Leg: > 0 } place || Hypot(at, place.Path[place.Next].Position) > AtWaypointMetres)
            return best;
        if (place.Next < place.Path.Count - 1)
            return place with { Leg = place.Leg + 1, Next = place.Next + 1 };
        return Closed(place.Path) ? place with { Leg = 1, Next = 1 } : place;
    }

    /// <summary>The next attack-area waypoint ahead on the player's chain (round a closed patrol), else the nearest one anywhere.</summary>
    private static WorldPoint? AttackAhead(List<IReadOnlyList<PlannedWaypoint>> chains, WorldPoint at)
    {
        if (Locate(chains, at) is { } place)
        {
            var path = place.Path;
            bool closed = Closed(path);
            int unique = closed ? path.Count - 1 : path.Count; // a closed path repeats its first point at the end
            int start = closed ? place.Next % unique : place.Next;
            for (int step = 0; step < unique; step++)
            {
                int i = closed ? (start + step) % unique : start + step;
                if (i >= unique)
                    break;
                if (path[i].IsAttack)
                    return path[i].Position;
            }
        }

        return chains.SelectMany(c => c).Where(w => w.IsAttack).MinBy(w => Hypot(at, w.Position))?.Position;
    }

    private sealed record RoutePlace(IReadOnlyList<PlannedWaypoint> Path, int Leg, int Next, double OffMetres);

    private static bool Closed(IReadOnlyList<PlannedWaypoint> path) =>
        path.Count >= 3 && Hypot(path[0].Position, path[^1].Position) < 1;

    private static double SegmentDistance(WorldPoint p, WorldPoint a, WorldPoint b)
    {
        double dn = b.North - a.North, de = b.East - a.East;
        double lengthSquared = dn * dn + de * de;
        double t = lengthSquared < 1 ? 0 : Math.Clamp(((p.North - a.North) * dn + (p.East - a.East) * de) / lengthSquared, 0, 1);
        return Math.Sqrt(Math.Pow(p.North - (a.North + t * dn), 2) + Math.Pow(p.East - (a.East + t * de), 2));
    }

    private static double Hypot(WorldPoint a, WorldPoint b) => Math.Sqrt(Math.Pow(a.North - b.North, 2) + Math.Pow(a.East - b.East, 2));
}
