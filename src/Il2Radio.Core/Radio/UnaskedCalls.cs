using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Radio;

/// <summary>
/// Which unasked calls are on. Each one is its own setting; both default to on.
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
/// 5°, not an eight-point name. A heading taken from the track is the leader's nose after it has held
/// a new course gently. A break (faster than <see cref="BreakDegreesPerSecond"/>) is not a cruise call.
/// While any contact is still in sight, the route stays quiet and the picture call, if it changed, is the one line.
/// </remarks>
public sealed class UnaskedCalls
{
    /// <summary>A corner closer than this is imminent. About two kilometres at the speeds a cruise leg is flown.</summary>
    public const double CornerNearMetres = 3_500;

    /// <summary>Inside this the corner is already being flown; the call had its chance on the way in.</summary>
    public const double CornerAtMetres = 400;

    private const double AheadDegrees = 70;
    private const double BreakDegreesPerSecond = 10;
    private const double SettleSeconds = 5;
    private const double MinSampleSeconds = 0.4;

    private int _mission = -1;
    private readonly HashSet<string> _corners = [];
    private readonly HashSet<PictureMark> _picture = [];

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
            _haveHeading = false;
            _leaderKey = null;
            _pendingSpeak = false;
            _awaitingSettle = false;
            _announcedCourse = -1;
        }

        var contacts = EnemyCommands.EnemyContacts(tracker, settings, memory);
        if (contacts.Outcome == ReplyOutcome.PlayerPositionUnknown)
            return null;

        var groups = contacts.Replies.Where(r => r.Members.Count > 0).ToList();
        bool fighting = groups.Any(r => r.Tokens?.Any(t => t.Word == SpeechWord.LastSeen) != true);
        RadioReply? spoken = calls.Contacts ? Novel(groups) : null;

        var suppressed = new List<SuppressedContact>();
        if (RadioPicture.Build(tracker, settings, RadioPicture.IsFlightMate, suppressed) is not { } flight)
            return spoken;

        if (fighting || spoken is not null || !calls.Route)
        {
            UpdateHeading(flight, allowSpeech: false);
            return spoken;
        }

        return Route(flight, tracker.Plan);
    }

    private RadioReply? Novel(List<RadioReply> groups)
    {
        var marks = groups.Select(g => (Mark: PictureMark.Of(g), Reply: g)).ToList();
        RadioReply? novel = marks
            .Where(m => !_picture.Contains(m.Mark))
            .OrderBy(m => m.Reply.Relative.RangeMetres)
            .Select(m => m.Reply)
            .FirstOrDefault();
        _picture.Clear();
        foreach (var m in marks)
            _picture.Add(m.Mark);
        return novel;
    }

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
            return playerLeads ? RouteLine(flight, corner.OutboundDegrees) : HeadingLine(ai!, corner.OutboundDegrees);
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
        double travelled = 0;
        int vertex = best;
        while (vertex < path.Count - 1)
        {
            double step = Hypot(path[vertex].North - cursorNorth, path[vertex].East - cursorEast);
            if (travelled + step > CornerNearMetres)
                break;
            travelled += step;
            double outbound = Bearing(path[vertex].North, path[vertex].East, path[vertex + 1]);
            double toward = Bearing(north, east, path[vertex]);
            if (travelled > CornerAtMetres && !SameCourse(outbound, heading) && Delta(toward, heading) <= AheadDegrees)
            {
                corner = new($"{path[vertex].North:F0},{path[vertex].East:F0}", outbound);
                travel = travelled;
                return true;
            }

            cursorNorth = path[vertex].North;
            cursorEast = path[vertex].East;
            vertex++;
        }

        return false;
    }

    private static RadioReply HeadingLine(LiveContact speaker, double degrees)
    {
        int course = Navigation.CourseDegrees(degrees);
        return new(speaker.Contact, [speaker.Contact], speaker.Relative, speaker.AgeSeconds,
            $"{RadioPhrases.Who(speaker.Contact)}, heading {course:000}.",
            [.. SpeechToken.Identify(speaker.Contact), new SpeechToken(SpeechWord.Course, course)]);
    }

    private static RadioReply RouteLine(RadioPicture flight, double degrees)
    {
        int course = Navigation.CourseDegrees(degrees);
        var relative = Navigation.Relative(flight.PlayerFix.Position, flight.PlayerFix.Position) ?? new RelativePosition(0, 0, 0);
        return new RadioReply(flight.Player, [flight.Player], relative, 0,
            $"Route, heading {course:000}.",
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
