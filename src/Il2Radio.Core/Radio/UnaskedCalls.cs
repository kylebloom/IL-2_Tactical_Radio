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
/// A corner is the briefed next leg, found while the navigator's live fix is still on that chain
/// (the same 10 km gate as a map square) and within <see cref="CornerNearMetres"/>. That distance is
/// wider than the "about two kilometres" the call is aimed at so a snapshot every 10 s still lands
/// inside the window at cruise. The new course is said to the nearest 5°, not an eight-point name.
/// A patrol whose points form a loop is one closed chain, so the corner
/// where it joins has a next leg. A corner behind the nearest leg is still found when the nose is
/// within <see cref="OnTheNoseDegrees"/> of it. When the player leads, the corner is a route line at once.
/// An AI leader says it only once his live nose has swung at least <see cref="TurnInDegrees"/> toward
/// the new leg: the plan is not his intent. In the 2026-10-26 flight the leader was called "turning to
/// heading 295" and turned north-east instead, and the player who followed the call lost the flight.
/// A corner he turns away from, or does not start within <see cref="CornerWaitSeconds"/>, is dropped
/// unsaid, and the course he does settle on is called instead. A heading taken from the track is the
/// leader's nose after it has held within <see cref="SettleDegrees"/> of a new course gently, for a
/// course at least that far from the last one called.
/// A break (faster than <see cref="BreakDegreesPerSecond"/>) is not a cruise call.
/// While any contact is still in sight, the route stays quiet and the picture call, if it changed, is the one line.
/// A loss goes before either; a picture change it holds back is said on the next look.
/// <para>
/// Break-off: when the AI leader's nose swings <see cref="BreakOffDegrees"/> off the course he was holding,
/// not toward a briefed corner, he says once "turning left. Follow me.", with "engaging" when an enemy in
/// the picture is within <see cref="EngageInferMetres"/> of him, or his turn points him at one
/// (see <see cref="BreakOff"/>). A contact that only another mate has, or a last-seen one, is not his fight.
/// It goes first, so the turn is not absorbed into the fight unsaid. He says it again only after he has called a cruise
/// course. He says it, and a cruise heading, only while he is airborne and someone in the flight is within
/// <see cref="WithFlightMetres"/>. A turn while he is alone or on the ground is followed in silence, so it is
/// not announced when a mate comes back into range. In the 2026-10-26 flight the leader left the route for the
/// enemy and the player, ahead and out of sight, heard nothing. In the 2026-10-27 flight the new leader said
/// "follow me" from 5 km and beyond, and again while taxiing.
/// </para>
/// <para>
/// Lost sight: when nobody in the flight has seen the player for <see cref="LostSightSeconds"/> while he is out
/// in front (and no farther than <see cref="LostSightMaxMetres"/>), having been seen in the last two minutes,
/// the leader says once "we have lost sight of you" with his own heading and altitude. Not from the ground,
/// and not when another airborne mate is on the frequency and the leader is not with him.
/// </para>
/// <para>
/// Running ahead: when the player, flying the flight's way, has been at least <see cref="AheadMetres"/> in
/// front of the AI leader's nose for <see cref="AheadHoldSeconds"/> while the leader held his course, a mate
/// who can see the player (the leader first) says "you are a few kilometres ahead of us. Slow down and
/// rejoin." Again after <see cref="AheadRepeatSeconds"/> while the gap is not closing. In the same flight the
/// player climbed past the flight at 150 km/h more and flew its headings 5 km in front of it, not knowing.
/// It goes after the route calls, which are part of the same picture.
/// </para>
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
    /// <summary>An AI leader's nose must have swung this far toward a briefed leg before he calls the turn.</summary>
    public const double TurnInDegrees = 10;

    /// <summary>A briefed corner the AI leader has not started turning for within this long (track seconds) is dropped.</summary>
    public const double CornerWaitSeconds = 60;

    /// <summary>A nose within this of the new course is holding it; a gentle drift on a leg is not a new course.</summary>
    public const double SettleDegrees = 10;

    /// <summary>The leader's nose this far off the course he was holding, with an enemy in the picture, is a break-off.</summary>
    public const double BreakOffDegrees = 45;

    /// <summary>An enemy the leader turns onto within this range is taken as what he broke off for. About as far as the game's pilots react. An enemy already in the picture counts as his fight inside the same range, and not beyond it.</summary>
    public const double EngageInferMetres = 15_000;

    /// <summary>
    /// He is leading the flight while another living mate (the player counts) is at least this close.
    /// Beyond it a turn is his own affair. The 2026-10-27 calls started at about 5 km.
    /// </summary>
    public const double WithFlightMetres = 4_000;

    /// <summary>
    /// At or under this height above the ground he is on the surface (taxi, rollout, a wheels-down figure of about
    /// 1.4 m in IL-2's ACMI) and is not giving formation orders. A sample with no AGL is treated as airborne.
    /// </summary>
    public const double OnGroundMetres = 15;

    /// <summary>His turn must have brought that enemy at least this much nearer his nose.</summary>
    private const double EngageInferTurnDegrees = 20;

    /// <summary>An enemy he has turned past by more than this is not what he turned for.</summary>
    private const double EngageInferNoseDegrees = 30;

    /// <summary>The player is ahead of the flight at least this far in front of the AI leader.</summary>
    public const double AheadMetres = 1_500;

    /// <summary>"In front" is within this of the leader's nose, and the player's nose within <see cref="AheadSameWayDegrees"/> of it.</summary>
    private const double AheadConeDegrees = 45;
    private const double AheadSameWayDegrees = 45;

    /// <summary>The leader's nose may wander this much while the player is ahead; more is a turn, and the look starts again.</summary>
    private const double AheadSteadyDegrees = 20;

    /// <summary>The player has been ahead this long (track seconds) before the flight says so.</summary>
    public const double AheadHoldSeconds = 8;

    /// <summary>The call is not repeated sooner than this, and only while the gap is not closing.</summary>
    public const double AheadRepeatSeconds = 60;

    /// <summary>A gap this much smaller than at the last call is closing: the player is already correcting.</summary>
    private const double AheadClosingMetres = 300;

    /// <summary>
    /// Nobody in the flight has seen the player for this long (track seconds) while he is out in front: "we have lost
    /// sight of you". Longer than one spotting dice slot (15 s), so one unlucky look is not a loss.
    /// </summary>
    public const double LostSightSeconds = 20;

    /// <summary>The flight must have seen him within this long for "lost sight" to make sense; after that it has nothing to lose.</summary>
    private const double LostSightMemorySeconds = 120;

    /// <summary>"Out in front" for the lost-sight call: within this of the leader's nose. Wider than the ahead call, since he may be turning.</summary>
    private const double LostSightConeDegrees = 60;

    /// <summary>Beyond this he has not lost the player out in front of the flight; the player is simply elsewhere. The same edge as "far".</summary>
    private const double LostSightMaxMetres = PilotReport.FarBeyondMetres;

    private const double BreakDegreesPerSecond = 10;

    /// <summary>
    /// A new course is held this long (track seconds) before it is called. With a look about every 11 s that is three
    /// looks, not two: in the 2026-10-27 escort the leader weaved ±50° over the Ju 52s, two looks either side of the
    /// top of an S-turn showed the same nose, and "heading 165" was called 11 s before "turning right. Follow me."
    /// </summary>
    public const double SettleSeconds = 15;

    /// <summary>A leader who has reversed his turn within this long (track seconds) is weaving.</summary>
    public const double WeaveMemorySeconds = 60;

    /// <summary>A weaving leader's swing off his course is a break-off only once it has held this long (track seconds) without coming back.</summary>
    public const double BreakOffHoldSeconds = 8;

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
    private PendingCorner? _pendingCorner;
    private bool _brokeOff;
    private double _turnSinceCourse;
    private int _lastStepSide;
    private double _lastReversalAt = double.NegativeInfinity;
    private BreakHeld? _breakHeld;

    private double? _aheadSince;
    private double _aheadLeaderNose;
    private double _aheadCalledAt = double.NegativeInfinity;
    private double _aheadCalledRange;
    private double _playerSeenAt = double.NegativeInfinity;
    private bool _lostSightCalled;

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
            _pendingCorner = null;
            _brokeOff = false;
            _turnSinceCourse = 0;
            ForgetWeave();
            _aheadSince = null;
            _aheadCalledAt = double.NegativeInfinity;
            _playerSeenAt = double.NegativeInfinity;
            _lostSightCalled = false;
            _losses.Reset();
        }

        _losses.Sample(tracker);
        var contacts = EnemyCommands.EnemyContacts(tracker, settings, memory);
        if (contacts.Outcome == ReplyOutcome.PlayerPositionUnknown)
            return null;

        var suppressed = new List<SuppressedContact>();
        var flight = RadioPicture.Build(tracker, settings, RadioPicture.IsFlightMate, suppressed);
        var groups = contacts.Replies.Where(r => r.Members.Count > 0).ToList();
        // The briefed corner ahead of an AI leader is found on every look while he is still leading, so his turn
        // onto the next leg is not taken for a break-off while a contact is in sight. Its call is still made only
        // on a quiet look. Finding it while he is alone would use the corner up unheard.
        bool leading = flight is not null && Pick(flight).Ai is { } leader && CanLead(flight, leader);
        if (leading)
            FindCorner(flight!, tracker.Plan);
        if (leading && calls.Route && BreakOff(flight!, groups, tracker, settings, memory) is { } breakOff)
        {
            UpdateHeading(flight!, allowSpeech: false);
            return breakOff;
        }

        if (flight is not null && calls.Losses && _losses.Call(tracker, flight, settings) is { } loss)
        {
            UpdateHeading(flight, allowSpeech: false);
            return loss;
        }

        bool fighting = groups.Any(r => r.Tokens?.Any(t => t.Word == SpeechWord.LastSeen) != true);
        double now = tracker.Player?.LastFix?.SourceTime ?? 0;
        RadioReply? spoken = calls.Contacts ? Novel(groups, now) : null;

        if (flight is null)
            return spoken;

        if (fighting || spoken is not null || !calls.Route)
        {
            UpdateHeading(flight, allowSpeech: false);
            // A contact far off is no reason to leave the player out in front; one on a pilot is the fight.
            if (spoken is null && calls.Route && !groups.Any(g => g.Tokens?.Any(t => t.Word == SpeechWord.OnMe) == true))
                return Ahead(flight, tracker, settings);
            _aheadSince = null;
            return spoken;
        }

        return Route(flight, tracker.Plan) ?? Ahead(flight, tracker, settings);
    }

    /// <summary>
    /// "Engaging, turning left. Follow me.": the AI leader has turned <see cref="BreakOffDegrees"/> off the
    /// course he held, not toward a briefed corner, and he is still airborne with someone in the flight
    /// (<see cref="CanLead"/>). "Engaging" when an enemy in the picture, not a last-seen one, is within
    /// <see cref="EngageInferMetres"/> of him, or when his turn points him at a live enemy aircraft
    /// (<see cref="TurnedToward"/>): the game's pilots spot before our visibility model does, and a leader
    /// who swings onto an enemy has seen it. That enemy is then remembered as seen by him, so a contact
    /// request agrees with what he said. A contact only a distant mate has does not make it his fight.
    /// With neither, he says only "turning left. Follow me.", and from a weaving leader only once the swing holds
    /// (<see cref="Meant"/>).
    /// </summary>
    private RadioReply? BreakOff(RadioPicture flight, IReadOnlyList<RadioReply> groups, ContactTracker tracker, RadioSettings settings, SightingMemory memory)
    {
        var (ai, playerLeads) = Pick(flight);
        if (playerLeads || ai is null || !CanLead(flight, ai) || _brokeOff || ai.Fix.Heading is not double nose
            || !_haveHeading || _leaderKey != ai.Contact.Key || _announcedCourse < 0)
            return null;
        double turned = TurnSoFar(nose);
        if (Math.Abs(turned) < BreakOffDegrees)
        {
            _breakHeld = null;
            return null;
        }
        if (_pendingCorner is { } corner && Delta(nose, corner.OutboundDegrees) < Delta(corner.NoseWhenFound, corner.OutboundDegrees))
            return null; // the briefed turn, which the corner call says

        int side = turned < 0 ? -1 : 1;
        bool engaging = EnemyNearLeader(ai, groups);
        var target = engaging ? null : TurnedToward(ai, nose - turned, nose, side, tracker, settings);
        if (!engaging && target is null && !Meant(ai.Fix.SourceTime, side, Math.Abs(turned)))
            return null;

        _brokeOff = true;
        _breakHeld = null;
        _pendingCorner = null;
        if (target is not null)
        {
            memory.Sync(tracker);
            memory.Set(target.Contact.Key, new SightingMemory.Sighting(
                target.Fix.SourceTime, IdentificationLevel.Side, null, false, target.Fix.Position, target.Fix.Heading));
            engaging = true;
        }

        var who = ai.Contact;
        string turning = $"turning {(side < 0 ? "left" : "right")}";
        List<SpeechToken> tokens = [.. SpeechToken.Identify(who)];
        if (engaging)
            tokens.Add(new(SpeechWord.Engaging));
        tokens.Add(new(SpeechWord.Turn, side));
        tokens.Add(new(SpeechWord.FollowMe));
        return new RadioReply(who, [who], ai.Relative, ai.AgeSeconds,
            $"{RadioPhrases.Who(who)}, {(engaging ? "engaging, " : "")}{turning}. Follow me.", tokens);
    }

    /// <summary>
    /// An enemy the flight is reporting now, not a last-seen one, within <see cref="EngageInferMetres"/> of the leader.
    /// A mate's fight on the other side of the map is not this.
    /// </summary>
    private static bool EnemyNearLeader(LiveContact leader, IReadOnlyList<RadioReply> groups) =>
        groups.Any(g => g.Tokens?.Any(t => t.Word == SpeechWord.LastSeen) != true
            && g.Members.Any(c => c.LastFix is { } fix
                && Navigation.Relative(leader.Fix.Position, fix.Position) is { RangeMetres: <= EngageInferMetres }));

    /// <summary>
    /// A swing off his course with no enemy to explain it is a turn he means. A steady leader means it at once.
    /// One who has reversed his turn within <see cref="WeaveMemorySeconds"/> is weaving, as an escort does over its
    /// bombers, and means it only when the swing has held <see cref="BreakOffHoldSeconds"/> on that side without
    /// coming back more than <see cref="SettleDegrees"/>. In the 2026-10-27 escort every weave was a "follow me".
    /// </summary>
    private bool Meant(double now, int side, double off)
    {
        if (now < _lastReversalAt)
            ForgetWeave(); // a new track file starts its clock again
        if (now - _lastReversalAt > WeaveMemorySeconds)
            return true;

        if (_breakHeld is not { } held || held.Side != side || now < held.Since || off < held.Peak - SettleDegrees)
        {
            _breakHeld = new(now, side, off);
            return false;
        }

        _breakHeld = held with { Peak = Math.Max(held.Peak, off) };
        return now - held.Since >= BreakOffHoldSeconds;
    }

    /// <summary>A weaving leader's swing off his course, waiting to show it holds.</summary>
    private sealed record BreakHeld(double Since, int Side, double Peak);

    private void ForgetWeave()
    {
        _lastStepSide = 0;
        _lastReversalAt = double.NegativeInfinity;
        _breakHeld = null;
    }

    /// <summary>Airborne, and at least one other living mate is within <see cref="WithFlightMetres"/>. The player counts.</summary>
    private static bool CanLead(RadioPicture flight, LiveContact leader) =>
        Airborne(leader) && (Within(leader.Fix.Position, flight.PlayerFix.Position)
            || flight.Live.Any(l => l.Contact.Key != leader.Contact.Key && Airborne(l) && Within(leader.Fix.Position, l.Fix.Position)));

    /// <summary>
    /// A logged landing, or ACMI height above the ground at or under <see cref="OnGroundMetres"/>.
    /// No AGL on the sample means the track did not say, and he is taken as flying.
    /// </summary>
    private static bool Airborne(LiveContact mate) =>
        mate.Contact.Status != ContactStatus.Landed
        && (mate.Fix.AboveGroundMetres is not double agl || agl > OnGroundMetres);

    private static bool Within(WorldPoint from, WorldPoint to) =>
        Navigation.Relative(from, to) is { RangeMetres: <= WithFlightMetres };

    /// <summary>
    /// The live enemy aircraft the leader's turn has swung him onto: within <see cref="EngageInferMetres"/>, on the
    /// side he turned to, at least <see cref="EngageInferTurnDegrees"/> nearer his nose than it was off the course
    /// he held, and not passed by more than <see cref="EngageInferNoseDegrees"/> (he may still be coming round to it).
    /// The one nearest his nose; null when no enemy explains the turn.
    /// </summary>
    private static LiveContact? TurnedToward(LiveContact leader, double heldNose, double nose, int side, ContactTracker tracker, RadioSettings settings)
    {
        var enemies = RadioPicture.Build(tracker, settings with { RadioRangeMetres = double.MaxValue },
            (player, c) => c.Coalition >= 0 && player.Coalition >= 0 && c.Coalition != player.Coalition, [])?.Live ?? [];
        return enemies
            .Select(e => (Enemy: e, Off: Navigation.Relative(leader.Fix.Position, e.Fix.Position)))
            .Where(x => x.Off is { RangeMetres: <= EngageInferMetres })
            .Select(x => (x.Enemy, Bearing: x.Off!.Value.BearingDegrees))
            .Where(x => Delta(x.Bearing, heldNose) - Delta(x.Bearing, nose) >= EngageInferTurnDegrees
                && (Signed(x.Bearing - heldNose) < 0 ? -1 : 1) == side
                && Signed(x.Bearing - nose) * side >= -EngageInferNoseDegrees) // still round the turn, or just passed
            .OrderBy(x => Delta(x.Bearing, nose))
            .Select(x => x.Enemy)
            .FirstOrDefault();
    }

    /// <summary>
    /// "You are a few kilometres ahead of us. Slow down and rejoin.": the player has run out in front of the AI
    /// leader, flying the flight's way, while the leader held his course. Said by a mate who can see him.
    /// </summary>
    private RadioReply? Ahead(RadioPicture flight, ContactTracker tracker, RadioSettings settings)
    {
        var (ai, playerLeads) = Pick(flight);
        if (playerLeads || ai?.Fix.Heading is not double leaderNose || flight.PlayerFix.Heading is not double playerNose
            || Navigation.Relative(ai.Fix.Position, flight.PlayerFix.Position) is not { } fromLeader)
        {
            _aheadSince = null;
            return null;
        }

        double now = flight.PlayerFix.SourceTime;
        if (now < _aheadCalledAt || now < _playerSeenAt || (_aheadSince is double since && now < since))
        {
            _aheadSince = null; // a new track file starts its clock again
            _aheadCalledAt = double.NegativeInfinity;
            _playerSeenAt = double.NegativeInfinity;
        }

        // Pilot realism: only a mate who has the player in sight knows where he is.
        bool CanSee(LiveContact l) => settings.Realism != RadioRealism.Pilot || PilotReport.CanSeePlayer(l, flight.Player, flight.PlayerFix, tracker, settings);
        if (flight.Live.Any(CanSee))
        {
            _playerSeenAt = now;
            _lostSightCalled = false;
        }
        else if (!_lostSightCalled && Airborne(ai) && WithHisFlight(flight, ai)
            && fromLeader.RangeMetres >= AheadMetres && fromLeader.RangeMetres <= LostSightMaxMetres
            && Delta(fromLeader.BearingDegrees, leaderNose) <= LostSightConeDegrees
            && now - _playerSeenAt >= LostSightSeconds && now - _playerSeenAt <= LostSightMemorySeconds)
        {
            _lostSightCalled = true;
            return LostSight(ai);
        }

        bool ahead = fromLeader.RangeMetres >= AheadMetres
            && Delta(fromLeader.BearingDegrees, leaderNose) <= AheadConeDegrees
            && Delta(playerNose, leaderNose) <= AheadSameWayDegrees
            && !flight.Live.Any(l => settings.InFormation(l.Relative));
        if (!ahead)
        {
            _aheadSince = null;
            _aheadCalledAt = double.NegativeInfinity;
            return null;
        }

        if (_aheadSince is null || Delta(leaderNose, _aheadLeaderNose) > AheadSteadyDegrees)
        {
            _aheadSince = now;
            _aheadLeaderNose = leaderNose;
            return null;
        }

        if (now - _aheadSince < AheadHoldSeconds || now - _aheadCalledAt < AheadRepeatSeconds
            || (_aheadCalledAt > double.NegativeInfinity && fromLeader.RangeMetres < _aheadCalledRange - AheadClosingMetres))
            return null;

        var speaker = flight.Live
            .Where(l => l.Contact.FormationIndex is not null)
            .OrderBy(l => l.Contact.FormationIndex)
            .FirstOrDefault(CanSee);
        if (speaker is null)
            return null;

        _aheadCalledAt = now;
        _aheadCalledRange = fromLeader.RangeMetres;
        string where = fromLeader.RangeMetres < PilotReport.CloseMetres ? "ahead of us" : $"{PilotReport.Range(fromLeader.RangeMetres)} ahead of us";
        List<SpeechToken> tokens = [.. SpeechToken.Identify(speaker.Contact)];
        if (fromLeader.RangeMetres >= PilotReport.CloseMetres)
            tokens.Add(new(SpeechWord.RangeBand, PilotReport.RangeBand(fromLeader.RangeMetres)));
        tokens.Add(new(SpeechWord.Rejoin));
        return new RadioReply(speaker.Contact, [speaker.Contact], speaker.Relative, speaker.AgeSeconds,
            $"{RadioPhrases.Who(speaker.Contact)}, you are {where}. Slow down and rejoin.", tokens);
    }

    /// <summary>
    /// Another airborne mate is on the frequency, and the leader is with him. A two-ship has no such mate:
    /// the leader is the flight. Used so a pilot who has left cannot say the flight has lost the player.
    /// </summary>
    private static bool WithHisFlight(RadioPicture flight, LiveContact leader)
    {
        var others = flight.Live.Where(l => l.Contact.Key != leader.Contact.Key && l.Contact.FormationIndex is not null && Airborne(l)).ToList();
        return others.Count == 0 || others.Any(l => Within(leader.Fix.Position, l.Fix.Position));
    }

    /// <summary>
    /// "We have lost sight of you. Heading south, altitude 3500 metres.": the leader's own heading and altitude,
    /// the most a pilot who cannot see the player can give him to find the flight again.
    /// </summary>
    private static RadioReply LostSight(LiveContact leader)
    {
        var who = leader.Contact;
        double altitude = leader.Fix.Position.Altitude;
        List<SpeechToken> tokens = [.. SpeechToken.Identify(who), new SpeechToken(SpeechWord.CannotSee)];
        PilotReport.AddOwnState(tokens, leader.Fix.Heading, altitude);
        return new RadioReply(who, [who], leader.Relative, leader.AgeSeconds,
            $"{RadioPhrases.Who(who)}, we have lost sight of you. {PilotReport.OwnStateSentence(leader.Fix.Heading, altitude)}", tokens);
    }

    /// <summary>
    /// The picture call, if the picture changed in a way worth a transmission. In the 2026-10-26 fight a
    /// group drifting in and out of sight was called on nearly every 10 s look ("single aircraft", then
    /// "last seen", then the same aircraft again). The same aircraft are not called again within
    /// <see cref="RepeatSeconds"/> unless one of them has just come onto a pilot ("on me") or let go of
    /// him; a lost look alone is not news. Picture calls are at least <see cref="PictureGapSeconds"/>
    /// apart, except for a new "on me". A held-back change is looked at again on the next look.
    /// "The same aircraft" is the same planned flight where the plan names it (<see cref="PictureMark"/>).
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

        // Gained: in sight now, and neither in the last picture nor called in the last minute in any form.
        bool gained = candidate.Reply is not null && !candidate.Mark.Held
            && _picture.All(p => p.Targets != candidate.Mark.Targets)
            && !(_saidAt.TryGetValue(candidate.Mark.Targets, out var before) && now - before.Time < RepeatSeconds);

        _picture.Clear();
        foreach (var m in marks)
            _picture.Add(m.Mark);
        if (candidate.Reply is null)
            return null;
        _saidAt[candidate.Mark.Targets] = (now, candidate.Mark.OnMe);
        _lastPictureCall = now;
        return gained ? WithSpotted(candidate.Reply) : candidate.Reply;
    }

    /// <summary>The actors' "spotted" after the speaker's callsign and number. The chat sentence is unchanged.</summary>
    private static RadioReply WithSpotted(RadioReply reply)
    {
        if (reply.Tokens is not { } tokens)
            return reply;
        int after = tokens.TakeWhile(t => t.Word is SpeechWord.Callsign or SpeechWord.Speaker).Count();
        return reply with { Tokens = [.. tokens.Take(after), new SpeechToken(SpeechWord.Spotted), .. tokens.Skip(after)] };
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

        if (playerLeads || ai is null)
        {
            _pendingCorner = null;
            return flight.PlayerFix is { Heading: double heading }
                && Imminent(plan, flight.Player, flight.PlayerFix, heading) is { } corner
                && _corners.Add(corner.Key)
                ? RouteLine(flight, corner.OutboundDegrees)
                : null;
        }

        if (!CanLead(flight, ai))
            return null;

        if (TurningIn(ai) is { } turning)
            return turning;

        if (_pendingSpeak && ai.Fix.Heading is double nose)
        {
            _pendingSpeak = false;
            _brokeOff = false;
            Announce(Navigation.CourseDegrees(nose));
            return HeadingLine(ai, nose);
        }

        return null;
    }

    /// <summary>Notes the briefed corner coming up ahead of an AI leader; <see cref="TurningIn"/> says it once he starts the turn.</summary>
    private void FindCorner(RadioPicture flight, MissionPlan? plan)
    {
        var (ai, playerLeads) = Pick(flight);
        if (!playerLeads && ai?.Fix is { Heading: double heading } fix
            && Imminent(plan, ai.Contact, fix, heading) is { } corner
            && _corners.Add(corner.Key))
            _pendingCorner = new(corner.OutboundDegrees, heading, fix.SourceTime);
    }

    /// <summary>The briefed corner, found ahead of the AI leader while he was still on his old nose.</summary>
    private sealed record PendingCorner(double OutboundDegrees, double NoseWhenFound, double FoundAt);

    /// <summary>
    /// The corner call, once the leader's live nose has come <see cref="TurnInDegrees"/> (or half the turn) closer to the
    /// briefed leg; "heading" when he is already on it. A nose that has swung away from it, or a corner
    /// still not started after <see cref="CornerWaitSeconds"/>, drops it unsaid.
    /// </summary>
    private RadioReply? TurningIn(LiveContact ai)
    {
        if (_pendingCorner is not { } pending || ai.Fix.Heading is not double nose)
            return null;

        double before = Delta(pending.NoseWhenFound, pending.OutboundDegrees);
        double now = Delta(nose, pending.OutboundDegrees);
        // A small corner (100 from 090) is started once he is half way round to it.
        if (before - now >= Math.Min(TurnInDegrees, before / 2))
        {
            _pendingCorner = null;
            _brokeOff = false;
            Announce(Navigation.CourseDegrees(pending.OutboundDegrees));
            _pendingSpeak = false;
            _awaitingSettle = false;
            return SameCourse(nose, pending.OutboundDegrees) ? HeadingLine(ai, pending.OutboundDegrees) : TurningLine(ai, pending.OutboundDegrees);
        }

        if (now - before >= TurnInDegrees || ai.Fix.SourceTime - pending.FoundAt > CornerWaitSeconds || ai.Fix.SourceTime < pending.FoundAt)
            _pendingCorner = null;
        return null;
    }

    /// <summary>The course he is now taken to hold; his turning is counted again from here.</summary>
    private void Announce(int course)
    {
        _announcedCourse = course;
        _turnSinceCourse = 0;
    }

    /// <summary>
    /// How far, and which way, the leader has turned since the course he held: negative left. Counted sample by
    /// sample, so a turn the long way round (190 left through 095 to 360) is a left turn of 190°, not 170° right.
    /// </summary>
    private double TurnSoFar(double nose) => _turnSinceCourse + Signed(nose - _lastHeading);

    /// <summary>A heading change folded into -180..180.</summary>
    private static double Signed(double degrees) => ((degrees % 360) + 540) % 360 - 180;

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
            _brokeOff = false;
            _lastHeading = heading;
            _lastHeadingTime = now;
            _candidateCourse = course;
            _candidateSince = now;
            _candidateGentle = true;
            Announce(course);
            _pendingSpeak = false;
            _awaitingSettle = false;
            ForgetWeave();
            return;
        }

        double dt = now - _lastHeadingTime;
        if (dt < MinSampleSeconds)
            return;

        // Alone, or on the ground, his nose is still followed. The turn is not stored up to be announced
        // when a mate comes back within range, and a taxi turn is not a break-off.
        if (!CanLead(flight, ai))
        {
            _lastHeading = heading;
            _lastHeadingTime = now;
            _turnSinceCourse = 0;
            Announce(course);
            _candidateCourse = course;
            _candidateSince = now;
            _candidateGentle = true;
            _pendingSpeak = false;
            _awaitingSettle = false;
            _brokeOff = false;
            _pendingCorner = null;
            ForgetWeave();
            return;
        }

        double rate = Delta(heading, _lastHeading) / dt;
        double step = Signed(heading - _lastHeading);
        _turnSinceCourse += step;
        if (Math.Abs(step) >= SettleDegrees)
        {
            int stepSide = step < 0 ? -1 : 1;
            if (_lastStepSide != 0 && stepSide != _lastStepSide)
                _lastReversalAt = now;
            _lastStepSide = stepSide;
        }
        _lastHeading = heading;
        _lastHeadingTime = now;

        // In the 2026-10-26 flight the leader rolled out on 360 and drifted to 344 over 30 s; on exact 5° courses
        // that never settled, so his new course was never called.
        if (Delta(course, _candidateCourse) >= SettleDegrees)
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
        if (!_candidateGentle || Delta(course, _announcedCourse) < SettleDegrees)
            _awaitingSettle = false;
        else if (allowSpeech && _awaitingSettle && now - _candidateSince >= SettleSeconds)
        {
            _pendingSpeak = true;
            _awaitingSettle = false;
        }

        // A fight absorbs the course he is holding, so the roll-out is not announced afterwards. His turning is
        // still counted from the course he last held, so a slow turn off it while a contact is in sight is a break-off.
        if (!allowSpeech)
        {
            _pendingSpeak = false;
            _awaitingSettle = false;
            _announcedCourse = course;
        }
    }

    private readonly record struct Corner(string Key, double OutboundDegrees);

    /// <summary>
    /// The briefed corner ahead of this navigator's live fix. A memory fix is already in mission metres; an ACMI fix
    /// goes through the Stalingrad fit (<see cref="PlanSquare.MapMetres"/>). Before 2026-10-28 only the ACMI's
    /// latitude was used, so with memory positions (no latitude) no corner was ever found.
    /// </summary>
    private static Corner? Imminent(MissionPlan? plan, Contact navigator, PositionFix fix, double heading)
    {
        if (plan is null || PlanSquare.MapMetres(fix, plan) is not { } at)
            return null;

        var (north, east) = at;
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

    /// <summary>
    /// What a picture call was about. An aircraft the plan gives a callsign is marked by its flight, so a different
    /// part of the same formation is the same aircraft. Spotting is diced per aircraft: in the 2026-10-27 flight each
    /// look saw a different few of the nine Ju 52s, and "many unidentified transports" was called again 22 s later,
    /// then "pair of unidentified transports" for the same nine.
    /// </summary>
    private readonly record struct PictureMark(string Targets, bool OnMe, bool Held)
    {
        public static PictureMark Of(RadioReply reply) => new(
            string.Join("|", reply.Members.Select(m => m.Callsign > 0 ? $"flight:{m.Coalition}:{m.Callsign}" : m.Key)
                .Distinct().OrderBy(k => k, StringComparer.Ordinal)),
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
