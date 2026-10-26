using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Radio;

/// <summary>
/// Which unasked calls are on. Each one is its own setting; all default to on.
/// A numpad call still answers either way.
/// </summary>
public sealed record UnaskedCallSettings
{
    /// <summary>
    /// On a cruise leg, one heading call per turn. An AI leader says it. When the player is the
    /// lowest living formation number, the chat shows a route line with no pilot's name.
    /// </summary>
    public bool Route { get; init; } = true;

    /// <summary>One call when the flight's contact picture changes: a group gained, lost, or "on me".</summary>
    public bool Contacts { get; init; } = true;

    /// <summary>A flight mate who saw it calls one of the flight down, or an enemy going down, unless the game's own pilots say it (<see cref="LossCalls"/>).</summary>
    public bool Losses { get; init; } = true;
}

/// <summary>
/// Calls the flight makes without being asked. One instance per session; it remembers what it has
/// already said and resets when the mission restarts.
/// </summary>
/// <remarks>
/// A corner is the briefed next leg, said once while the navigator's live fix is still on that chain
/// (the same 10 km gate as a map square) and within <see cref="CornerNearMetres"/>. That distance is
/// wider than the "about two kilometres" the call is aimed at so a snapshot every 10 s still lands
/// inside the window at cruise. It is plan data spoken as an order: the new course, to the nearest
/// 5°, not an eight-point name. A patrol whose points form a loop is one closed chain, so the corner
/// where it joins has a next leg. A corner behind the nearest leg is still said when the nose is
/// within <see cref="OnTheNoseDegrees"/> of it. A heading taken from the track is the leader's nose after it has held
/// a new course gently. A break (faster than <see cref="BreakDegreesPerSecond"/>) is not a cruise call.
/// While any contact is still in sight, the route stays quiet and the picture call, if it changed, is the one line.
/// A loss goes before either; a picture change it holds back is said on the next look.
/// </remarks>
public sealed class UnaskedCalls
{
    /// <summary>A corner closer than this is imminent. About two kilometres at the speeds a cruise leg is flown.</summary>
    public const double CornerNearMetres = 3_500;

    /// <summary>Inside this the corner is already being flown; the call had its chance on the way in.</summary>
    public const double CornerAtMetres = 400;

    private const double AheadDegrees = 70;

    /// <summary>A corner the nearest leg does not lead to is called only when the nose is this close to it.</summary>
    private const double OnTheNoseDegrees = 20;
    private const double BreakDegreesPerSecond = 10;
    private const double SettleSeconds = 5;
    private const double MinSampleSeconds = 0.4;

    private int _mission = -1;
    private readonly LossCalls _losses = new();
    private readonly HashSet<string> _corners = [];
    private readonly HashSet<PictureMark> _picture = [];
    private readonly Dictionary<string, (double Time, bool OnMe)> _saidAt = new(StringComparer.Ordinal);
    private double _lastPictureCall = double.NegativeInfinity;

    private string? _leaderKey;
    private bool _haveHeading;
    private double _lastHeading;
    private double _lastHeadingTime;
    private int _candidateCourse = -1;
    private double _candidateSince;
    private bool _candidateGentle;
    private int _announcedCourse = -1;
    private bool _awaitingSettle;
    private bool _pendingSpeak;

    /// <summary>The one line to say now, or null when nothing has changed that this session is allowed to say.</summary>
    public RadioReply? Consider(ContactTracker tracker, RadioSettings settings, SightingMemory memory, UnaskedCallSettings? calls = null)
    {
        calls ??= new UnaskedCallSettings();
        if (tracker.MissionStarts != _mission)
        {
            _mission = tracker.MissionStarts;
            _corners.Clear();
            _picture.Clear();
            _saidAt.Clear();
            _lastPictureCall = double.NegativeInfinity;
            _haveHeading = false;
            _leaderKey = null;
            _pendingSpeak = false;
            _awaitingSettle = false;
            _announcedCourse = -1;
            _losses.Reset();
        }

        _losses.Sample(tracker);
        var contacts = EnemyCommands.EnemyContacts(tracker, settings, memory);
        if (contacts.Outcome == ReplyOutcome.PlayerPositionUnknown)
            return null;

        var suppressed = new List<SuppressedContact>();
        var flight = RadioPicture.Build(tracker, settings, RadioPicture.IsFlightMate, suppressed);
        if (flight is not null && calls.Losses && _losses.Call(tracker, flight, settings) is { } loss)
        {
            UpdateHeading(flight, allowSpeech: false);
            return loss;
        }

        var groups = contacts.Replies.Where(r => r.Members.Count > 0).ToList();
        bool fighting = groups.Any(r => r.Tokens?.Any(t => t.Word == SpeechWord.LastSeen) != true);
        double now = tracker.Player?.LastFix?.SourceTime ?? 0;
        RadioReply? spoken = calls.Contacts ? Novel(groups, now) : null;

        if (flight is null)
            return spoken;

        if (fighting || spoken is not null || !calls.Route)
        {
            UpdateHeading(flight, allowSpeech: false);
            return spoken;
        }

        return Route(flight, tracker.Plan);
    }

    /// <summary>
    /// The picture call, if the picture changed in a way worth a transmission. In the 2026-10-26 fight a
    /// group drifting in and out of sight was called on nearly every 10 s look ("single aircraft", then
    /// "last seen", then the same aircraft again). The same aircraft are not called again within
    /// <see cref="RepeatSeconds"/> unless one of them has just come onto a pilot ("on me") or let go of
    /// him; a lost look alone is not news. Picture calls are at least <see cref="PictureGapSeconds"/>
    /// apart, except for a new "on me". A held-back change is looked at again on the next look.
    /// </summary>
    private RadioReply? Novel(List<RadioReply> groups, double now)
    {
        if (_saidAt.Count > 0 && now < _saidAt.Values.Max(s => s.Time))
            _saidAt.Clear(); // a new track file starts its clock again

        var marks = groups.Select(g => (Mark: PictureMark.Of(g), Reply: g)).ToList();
        var candidate = marks
            .Where(m => !_picture.Contains(m.Mark) && !RecentlySaid(m.Mark, now))
            .OrderByDescending(m => m.Mark.OnMe)
            .ThenBy(m => m.Reply.Relative.RangeMetres)
            .FirstOrDefault();
        bool threat = candidate.Reply is not null && candidate.Mark.OnMe && !(_saidAt.TryGetValue(candidate.Mark.Targets, out var was) && was.OnMe);
        if (candidate.Reply is not null && !threat && now - _lastPictureCall < PictureGapSeconds)
            return null; // too soon after the last one; the picture is kept as it was, so this is reconsidered

        _picture.Clear();
        foreach (var m in marks)
            _picture.Add(m.Mark);
        if (candidate.Reply is null)
            return null;
        _saidAt[candidate.Mark.Targets] = (now, candidate.Mark.OnMe);
        _lastPictureCall = now;
        return candidate.Reply;
    }

    private bool RecentlySaid(PictureMark mark, double now) =>
        _saidAt.TryGetValue(mark.Targets, out var said) && now - said.Time < RepeatSeconds && said.OnMe == mark.OnMe;

    /// <summary>The same aircraft, in the same relation to the speaker, are not called again for this long (track seconds).</summary>
    public const double RepeatSeconds = 60;

    /// <summary>Least time between two contact-picture calls, unless the second is a new "on me" (track seconds).</summary>
    public const double PictureGapSeconds = 15;

    private RadioReply? Route(RadioPicture flight, MissionPlan? plan)
    {
        var (ai, playerLeads) = Pick(flight);
        UpdateHeading(flight, allowSpeech: !playerLeads);

        Contact? navigator = playerLeads ? flight.Player : ai?.Contact;
        PositionFix? fix = playerLeads ? flight.PlayerFix : ai?.Fix;
        if (navigator is not null && fix is { Heading: double heading, Latitude: double latitude, Longitude: double longitude }
            && Imminent(plan, navigator, latitude, longitude, heading) is { } corner
            && _corners.Add(corner.Key))
        {
            _announcedCourse = Navigation.CourseDegrees(corner.OutboundDegrees);
            _pendingSpeak = false;
            _awaitingSettle = false;
            return playerLeads ? RouteLine(flight, corner.OutboundDegrees) : TurningLine(ai!, corner.OutboundDegrees);
        }

        if (!playerLeads && ai is not null && _pendingSpeak && ai.Fix.Heading is double nose)
        {
            _pendingSpeak = false;
            _announcedCourse = Navigation.CourseDegrees(nose);
            return HeadingLine(ai, nose);
        }

        return null;
    }

    /// <summary>Lowest living formation number. The player leads when he holds it, or when no AI mate is on the frequency.</summary>
    private static (LiveContact? Ai, bool PlayerLeads) Pick(RadioPicture flight)
    {
        var ai = flight.Live
            .Where(l => l.Contact.FormationIndex is not null)
            .OrderBy(l => l.Contact.FormationIndex)
            .FirstOrDefault();
        bool playerLeads = ai is null
            || (flight.Player.FormationIndex is int player && player <= ai.Contact.FormationIndex);
        return (ai, playerLeads);
    }

    private void UpdateHeading(RadioPicture flight, bool allowSpeech)
    {
        var (ai, playerLeads) = Pick(flight);
        if (playerLeads || ai?.Fix.Heading is not double heading)
        {
            _pendingSpeak = false;
            return;
        }

        double now = ai.Fix.SourceTime;
        int course = Navigation.CourseDegrees(heading);
        if (!_haveHeading || _leaderKey != ai.Contact.Key)
        {
            _haveHeading = true;
            _leaderKey = ai.Contact.Key;
            _lastHeading = heading;
            _lastHeadingTime = now;
            _candidateCourse = course;
            _candidateSince = now;
            _candidateGentle = true;
            _announcedCourse = course;
            _pendingSpeak = false;
            _awaitingSettle = false;
            return;
        }

        double dt = now - _lastHeadingTime;
        if (dt < MinSampleSeconds)
            return;

        double rate = Delta(heading, _lastHeading) / dt;
        _lastHeading = heading;
        _lastHeadingTime = now;

        if (course != _candidateCourse)
        {
            _candidateCourse = course;
            _candidateSince = now;
            _candidateGentle = rate <= BreakDegreesPerSecond;
            _pendingSpeak = false;
            _awaitingSettle = true;
            return;
        }

        if (rate > BreakDegreesPerSecond)
            _candidateGentle = false;
        if (!_candidateGentle || course == _announcedCourse)
            _awaitingSettle = false;
        else if (allowSpeech && _awaitingSettle && now - _candidateSince >= SettleSeconds)
        {
            _pendingSpeak = true;
            _awaitingSettle = false;
        }

        // A fight absorbs the course he is holding, so the roll-out is not announced afterwards.
        if (!allowSpeech)
        {
            _pendingSpeak = false;
            _awaitingSettle = false;
            _announcedCourse = course;
        }
    }

    private readonly record struct Corner(string Key, double OutboundDegrees);

    private static Corner? Imminent(MissionPlan? plan, Contact navigator, double latitude, double longitude, double heading)
    {
        if (plan is null || !MapGrid.IsStalingrad(plan.GuiMap))
            return null;

        var (north, east) = MapGrid.ProjectStalingrad(latitude, longitude);
        Corner? best = null;
        double bestTravel = double.PositiveInfinity;
        foreach (var path in PlanSquare.Routes(plan, navigator))
        {
            if (TryCorner(path, north, east, heading, out var corner, out double travel) && travel < bestTravel)
            {
                best = corner;
                bestTravel = travel;
            }
        }

        return best;
    }

    private static bool TryCorner(IReadOnlyList<WorldPoint> path, double north, double east, double heading, out Corner corner, out double travel)
    {
        corner = default;
        travel = 0;
        if (path.Count < 2)
            return false;

        int best = -1;
        double bestDist = double.PositiveInfinity;
        double bestT = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double t = SegmentT(north, east, path[i - 1], path[i], out double dist);
            if (dist < bestDist)
            {
                best = i;
                bestDist = dist;
                bestT = t;
            }
        }

        if (best < 0 || bestDist > PlanSquare.OnRouteMetres)
            return false;

        var end = path[best];
        var start = path[best - 1];
        double cursorNorth = start.North + bestT * (end.North - start.North);
        double cursorEast = start.East + bestT * (end.East - start.East);
        bool closed = Closed(path);
        double travelled = 0;
        int vertex = best;
        int steps = 0;
        while (steps++ < path.Count)
        {
            bool join = closed && vertex == path.Count - 1;
            if (!join && vertex >= path.Count - 1)
                break;

            int next = join ? 1 : vertex + 1;
            double step = Hypot(path[vertex].North - cursorNorth, path[vertex].East - cursorEast);
            if (travelled + step > CornerNearMetres)
                break;
            travelled += step;
            double outbound = Bearing(path[vertex].North, path[vertex].East, path[next]);
            double toward = Bearing(north, east, path[vertex]);
            if (travelled > CornerAtMetres && !SameCourse(outbound, heading) && Delta(toward, heading) <= AheadDegrees)
            {
                corner = new($"{path[vertex].North:F0},{path[vertex].East:F0}", outbound);
                travel = travelled;
                return true;
            }

            cursorNorth = path[vertex].North;
            cursorEast = path[vertex].East;
            vertex = join ? 1 : vertex + 1;
        }

        // The flight can be inside a patrol box, pointing at a corner the nearest leg has already
        // passed. That corner is the turn they are about to make.
        double bestNose = double.PositiveInfinity;
        int last = closed ? path.Count - 1 : path.Count - 2;
        for (int v = 1; v <= last; v++)
        {
            int next = closed && v == path.Count - 1 ? 1 : v + 1;
            if (next >= path.Count)
                continue;
            double dist = Hypot(path[v].North - north, path[v].East - east);
            if (dist <= CornerAtMetres || dist >= CornerNearMetres || dist >= bestNose)
                continue;
            double outbound = Bearing(path[v].North, path[v].East, path[next]);
            double toward = Bearing(north, east, path[v]);
            if (SameCourse(outbound, heading) || Delta(toward, heading) > OnTheNoseDegrees)
                continue;
            bestNose = dist;
            corner = new($"{path[v].North:F0},{path[v].East:F0}", outbound);
            travel = dist;
        }

        return bestNose < double.PositiveInfinity;
    }

    private static bool Closed(IReadOnlyList<WorldPoint> path) =>
        path.Count >= 3 && Hypot(path[0].North - path[^1].North, path[0].East - path[^1].East) < 1;

    /// <summary>The nose he is already flying. The course figure stays on the chat.</summary>
    private static RadioReply HeadingLine(LiveContact speaker, double degrees) =>
        CourseLine(speaker, degrees, "heading");

    /// <summary>
    /// The briefed next leg, said while he is still on the old course. The headset says which way he
    /// turns ("turning left") and the compass point, because the clips cannot say the figure.
    /// </summary>
    private static RadioReply TurningLine(LiveContact speaker, double degrees) =>
        CourseLine(speaker, degrees, "turning to heading",
            speaker.Fix.Heading is double nose && !SameCourse(nose, degrees) ? new SpeechToken(SpeechWord.Turn, TurnSide(nose, degrees)) : null);

    private static RadioReply CourseLine(LiveContact speaker, double degrees, string verb, SpeechToken? turn = null)
    {
        int course = Navigation.CourseDegrees(degrees);
        return new(speaker.Contact, [speaker.Contact], speaker.Relative, speaker.AgeSeconds,
            $"{RadioPhrases.Who(speaker.Contact)}, {verb} {course:000}.",
            [.. SpeechToken.Identify(speaker.Contact), .. turn is { } t ? [t] : Array.Empty<SpeechToken>(), new SpeechToken(SpeechWord.Course, course)]);
    }

    /// <summary>-1 for a left turn from <paramref name="from"/> to <paramref name="to"/>, +1 for a right one (the shorter way round).</summary>
    private static int TurnSide(double from, double to) => ((to - from) % 360 + 540) % 360 - 180 < 0 ? -1 : 1;

    private static RadioReply RouteLine(RadioPicture flight, double degrees)
    {
        int course = Navigation.CourseDegrees(degrees);
        var relative = Navigation.Relative(flight.PlayerFix.Position, flight.PlayerFix.Position) ?? new RelativePosition(0, 0, 0);
        return new RadioReply(flight.Player, [flight.Player], relative, 0,
            $"Route, turning to heading {course:000}.",
            [new SpeechToken(SpeechWord.Course, course)])
        { SpeakerLabel = "Route" };
    }

    private readonly record struct PictureMark(string Targets, bool OnMe, bool Held)
    {
        public static PictureMark Of(RadioReply reply) => new(
            string.Join("|", reply.Members.Select(m => m.Key).OrderBy(k => k, StringComparer.Ordinal)),
            reply.Tokens?.Any(t => t.Word == SpeechWord.OnMe) == true,
            reply.Tokens?.Any(t => t.Word == SpeechWord.LastSeen) == true);
    }

    private static bool SameCourse(double a, double b) => Navigation.CourseDegrees(a) == Navigation.CourseDegrees(b);

    private static double Bearing(double north, double east, WorldPoint to) =>
        Bearing(north, east, to.North, to.East);

    private static double Bearing(double north, double east, double toNorth, double toEast)
    {
        double degrees = Math.Atan2(toEast - east, toNorth - north) * 180.0 / Math.PI;
        return (degrees + 360.0) % 360.0;
    }

    private static double Delta(double a, double b)
    {
        double d = ((a - b) % 360 + 360) % 360;
        return d > 180 ? 360 - d : d;
    }

    private static double SegmentT(double north, double east, WorldPoint a, WorldPoint b, out double distance)
    {
        double dn = b.North - a.North, de = b.East - a.East;
        double lengthSquared = dn * dn + de * de;
        double t = lengthSquared < 1 ? 0 : Math.Clamp(((north - a.North) * dn + (east - a.East) * de) / lengthSquared, 0, 1);
        distance = Hypot(north - (a.North + t * dn), east - (a.East + t * de));
        return t;
    }

    private static double Hypot(double north, double east) => Math.Sqrt(north * north + east * east);
}
