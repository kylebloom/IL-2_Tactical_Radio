using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;

namespace Il2Radio.Core.Radio;

/// <summary>How closely a reply may follow the live track.</summary>
public enum RadioRealism
{
    /// <summary>Bearings, ranges, altitudes and counts as precisely as the track supports.</summary>
    Precise,

    /// <summary>
    /// A pilot gives a position relative to the player only when he can see the player or is in formation.
    /// Otherwise he reports his own heading and altitude. An enemy he cannot place relative to the player
    /// is described from his own cockpit, or as "on me" when he is in the fight.
    /// </summary>
    Pilot,
}

public sealed record RadioSettings
{
    /// <summary>
    /// How much of the live picture a pilot is allowed to say. The player's program and Diag default to
    /// <see cref="RadioRealism.Pilot"/>. Left unset here, replies stay <see cref="RadioRealism.Precise"/>,
    /// which is what the command tests lock in.
    /// </summary>
    public RadioRealism Realism { get; init; } = RadioRealism.Precise;

    /// <summary>Beyond this range a friendly cannot hear the request or answer it.</summary>
    public double RadioRangeMetres { get; init; } = 40_000;

    /// <summary>Fixes older than this are not "live" and are never reported as a current position.</summary>
    public double MaxFixAgeSeconds { get; init; } = 10;

    /// <summary>Aircraft closer than this (horizontally) to each other count as flying together.</summary>
    public double GroupRadiusMetres { get; init; } = 2_000;

    /// <summary>Aircraft further apart than this vertically are not flying together.</summary>
    public double GroupAltitudeBandMetres { get; init; } = 600;

    /// <summary>
    /// The player is "in formation" when this close to a member of the flight's main group. A kilometre
    /// is already a loose Rotte; at 1.5 km (before 2026-10-26) a mate still closing called the player
    /// in formation when he was plainly not.
    /// </summary>
    public double InFormationMetres { get; init; } = 1_000;

    /// <summary>
    /// ...and no further than this above or below him. In the 2026-10-27 flight the player, down with the Ju 52s,
    /// was told "you are in formation with us" by a leader 476 m away and 716 m above.
    /// </summary>
    public double InFormationHeightMetres { get; init; } = 300;

    /// <summary>A mate this close to the player, across and in height, is flying with him.</summary>
    public bool InFormation(RelativePosition relative) =>
        relative.RangeMetres <= InFormationMetres && Math.Abs(relative.AltitudeDeltaMetres) <= InFormationHeightMetres;

    /// <summary>Spotting and identification model used by the enemy-contact commands.</summary>
    public VisibilitySettings Visibility { get; init; } = new();
}

public enum ReplyOutcome
{
    /// <summary>At least one reply backed by fresh live fixes in the same frame as the player.</summary>
    Position,

    /// <summary>The integration cannot establish where the player is, so relative replies are impossible.</summary>
    PlayerPositionUnknown,

    /// <summary>No friendly could answer with information the integration can establish.</summary>
    NoReply,
}

/// <summary>
/// One radio transmission. <see cref="Contact"/> is the speaker; <see cref="Members"/> are the aircraft
/// the reply describes (the speaker included). <see cref="Relative"/> is from the player to the group centre.
/// <see cref="Text"/> is the English wording for display and TTS; <see cref="Tokens"/> says the same thing
/// language-neutrally, for voices built from recorded clips.
/// </summary>
public sealed record RadioReply(Contact Contact, IReadOnlyList<Contact> Members, RelativePosition Relative, double FixAgeSeconds, string Text, IReadOnlyList<SpeechToken>? Tokens = null)
{
    /// <summary>
    /// Chat name when the line is not this pilot keying. Null uses his own name. The route prompt uses
    /// "Route" so a wingman is not shown giving the leader an order.
    /// </summary>
    public string? SpeakerLabel { get; init; }
}

/// <summary>Contacts that were deliberately not reported, and why. Useful for diagnostics and tests.</summary>
public sealed record SuppressedContact(Contact Contact, string Reason);

public sealed record RadioResult(ReplyOutcome Outcome, IReadOnlyList<RadioReply> Replies, IReadOnlyList<SuppressedContact> Suppressed);

internal sealed record LiveContact(Contact Contact, PositionFix Fix, RelativePosition Relative, double AgeSeconds);

/// <summary>Aircraft flying together, with the member who speaks for them.</summary>
internal sealed record ContactGroup(IReadOnlyList<LiveContact> Members, WorldPoint Centre, RelativePosition Relative, double? Heading)
{
    public LiveContact Speaker =>
        Members.Where(m => m.Contact.InPlayerFlight && m.Contact.FormationIndex is not null)
            .OrderBy(m => m.Contact.FormationIndex).FirstOrDefault()
        ?? Members.OrderBy(m => m.Relative.RangeMetres).First();

    public double NearestRangeMetres => Members.Min(m => m.Relative.RangeMetres);

    /// <summary>One of them is in formation with the player (<see cref="RadioSettings.InFormation"/>).</summary>
    public bool InFormation(RadioSettings settings) => Members.Any(m => settings.InFormation(m.Relative));

    public double MaxAgeSeconds => Members.Max(m => m.AgeSeconds);
}

/// <summary>
/// What the radio can honestly know right now: the player's live position and every candidate
/// friendly with a fresh live fix within radio range. Everything else is listed as suppressed.
/// </summary>
internal sealed record RadioPicture(Contact Player, PositionFix PlayerFix, IReadOnlyList<LiveContact> Live, List<SuppressedContact> Suppressed)
{
    public static RadioPicture? Build(ContactTracker tracker, RadioSettings settings, Func<Contact, Contact, bool> isCandidate, List<SuppressedContact> suppressed)
    {
        var player = tracker.Player;
        if (player?.LastFix is not { } playerFix || !IsLive(tracker, playerFix, settings))
            return null;

        var live = new List<LiveContact>();
        foreach (var c in tracker.Contacts.Where(c => !c.IsPlayer && isCandidate(player, c)))
        {
            if (!c.IsOperational)
            {
                suppressed.Add(new(c, $"not operational ({c.Status})"));
                continue;
            }

            if (c.LastFix is not { } fix)
            {
                suppressed.Add(new(c, "no position observed"));
                continue;
            }

            double age = tracker.AgeSeconds(fix);
            if (!IsLive(tracker, fix, settings))
            {
                suppressed.Add(new(c, $"last fix is {fix.Source}/{fix.Detail}, {age:F0} s old — not live"));
                continue;
            }

            if (Navigation.Relative(playerFix.Position, fix.Position) is not { } rel)
            {
                suppressed.Add(new(c, "position in a different coordinate frame than the player"));
                continue;
            }

            if (rel.RangeMetres > settings.RadioRangeMetres)
            {
                suppressed.Add(new(c, $"out of radio range ({rel.RangeMetres / 1000:F0} km)"));
                continue;
            }

            live.Add(new LiveContact(c, fix, rel, age));
        }

        return new RadioPicture(player, playerFix, live, suppressed);
    }

    public static bool IsFriendly(Contact player, Contact c) => c.Coalition >= 0 && c.Coalition == player.Coalition;

    public static bool IsFlightMate(Contact player, Contact c) => c.InPlayerFlight && IsFriendly(player, c);

    /// <summary>Single-linkage grouping: aircraft within the group radius and altitude band of any member join it.</summary>
    public IReadOnlyList<ContactGroup> Groups(RadioSettings settings)
    {
        var remaining = Live.ToList();
        var groups = new List<ContactGroup>();
        while (remaining.Count > 0)
        {
            var members = new List<LiveContact> { remaining[0] };
            remaining.RemoveAt(0);
            for (int i = 0; i < members.Count; i++)
            {
                for (int j = remaining.Count - 1; j >= 0; j--)
                {
                    if (!Together(members[i].Fix.Position, remaining[j].Fix.Position, settings))
                        continue;
                    members.Add(remaining[j]);
                    remaining.RemoveAt(j);
                }
            }

            groups.Add(Summarise(members));
        }

        return groups;
    }

    private ContactGroup Summarise(List<LiveContact> members)
    {
        var p = members[0].Fix.Position;
        var centre = new WorldPoint(
            members.Average(m => m.Fix.Position.North),
            members.Average(m => m.Fix.Position.East),
            members.Average(m => m.Fix.Position.Altitude),
            p.Frame);
        return new ContactGroup(members, centre, Navigation.Relative(PlayerFix.Position, centre)!.Value, MeanHeading(members));
    }

    private static bool Together(WorldPoint a, WorldPoint b, RadioSettings settings) =>
        Navigation.Relative(a, b) is { } r
        && r.RangeMetres <= settings.GroupRadiusMetres
        && Math.Abs(r.AltitudeDeltaMetres) <= settings.GroupAltitudeBandMetres;

    private static double? MeanHeading(IEnumerable<LiveContact> members)
    {
        var headings = members.Select(m => m.Fix.Heading).OfType<double>().ToList();
        if (headings.Count == 0)
            return null;
        double x = headings.Sum(h => Math.Cos(h * Math.PI / 180)), y = headings.Sum(h => Math.Sin(h * Math.PI / 180));
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    private static bool IsLive(ContactTracker tracker, PositionFix fix, RadioSettings settings) =>
        fix.Source == PositionSource.AcmiTrack && tracker.AgeSeconds(fix) <= settings.MaxFixAgeSeconds;
}
