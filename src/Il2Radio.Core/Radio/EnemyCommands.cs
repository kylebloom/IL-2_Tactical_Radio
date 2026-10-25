using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Radio;

/// <summary>
/// What the flight has seen of other aircraft across requests: a spotted aircraft stays "tracked" for a
/// while, and its identification only improves (or is corrected) instead of flickering between calls.
/// One instance per live session; it resets itself when the mission restarts.
/// </summary>
public sealed class SightingMemory
{
    internal sealed record Sighting(double LastSeen, IdentificationLevel Level, AircraftType? CalledType, bool Hedged);

    private readonly Dictionary<string, Sighting> _sightings = new(StringComparer.Ordinal);
    private int _missionStarts = -1;

    /// <summary>How long a contact stays tracked without being re-spotted, seconds of track time.</summary>
    public double RetainSeconds { get; init; } = 60;

    internal Sighting? Get(string key) => _sightings.GetValueOrDefault(key);

    internal void Set(string key, Sighting sighting) => _sightings[key] = sighting;

    internal void Sync(ContactTracker tracker)
    {
        if (tracker.MissionStarts == _missionStarts)
            return;
        _sightings.Clear();
        _missionStarts = tracker.MissionStarts;
    }
}

/// <summary>An aircraft the flight currently knows about, and how well.</summary>
internal sealed record SightedContact(LiveContact Target, AircraftType Type, IdentificationLevel Level, AircraftType? CalledType, bool Hedged, LiveContact Reporter);

/// <summary>
/// Commands 2 and 4: "Request enemy contacts" and "Identify contact". Observers are the player's own
/// flight (or, flying alone, any friendly within radio range). Only aircraft that exist in the live
/// track can be reported, and only once the visibility model says an observer has spotted them; what is
/// said about them is limited to what that observer could make out at that range.
/// </summary>
public static class EnemyCommands
{
    private const double SlotSeconds = 15;
    private const double ContactGroupRadiusMetres = 3_000;
    private const double ContactGroupAltitudeBandMetres = 1_000;
    private const int MaxReportedGroups = 4;

    /// <summary>Command 2: spotted aircraft that are hostile or not yet identified, nearest first.</summary>
    public static RadioResult EnemyContacts(ContactTracker tracker, RadioSettings? settings = null, SightingMemory? memory = null)
    {
        settings ??= new RadioSettings();
        var suppressed = new List<SuppressedContact>();
        if (Look(tracker, settings, memory ?? new SightingMemory(), suppressed) is not { } look)
            return new RadioResult(ReplyOutcome.PlayerPositionUnknown, [], suppressed);
        if (look.Observers.Count == 0)
            return new RadioResult(ReplyOutcome.NoReply, [], suppressed);

        var groups = Group(look.Sighted.Where(s => s.Level < IdentificationLevel.Side || !IsFriendly(look.Player, s.Target.Contact)), look)
            .OrderBy(g => g.Relative.RangeMetres)
            .Take(MaxReportedGroups)
            .ToList();

        if (groups.Count == 0)
        {
            var lead = Leader(look.Observers);
            return new RadioResult(ReplyOutcome.Position,
                [new RadioReply(lead.Contact, [], lead.Relative, lead.AgeSeconds, $"{RadioPhrases.Who(lead.Contact)}, no contacts.",
                    [.. SpeechToken.Identify(lead.Contact), new(SpeechWord.NoContacts)])],
                suppressed);
        }

        var replies = groups.Select(g =>
        {
            var reporter = g.Reporter;
            string text = $"{RadioPhrases.Who(reporter.Contact)}, {Describe(g, look.Player)}, at your {RadioPhrases.Clock(g.Relative.BearingDegrees, look.PlayerHeading)}, " +
                          $"{RadioPhrases.Range(g.Relative.RangeMetres)}, {HighLow(g.Relative.AltitudeDeltaMetres)}.";
            List<SpeechToken> tokens = [.. SpeechToken.Identify(reporter.Contact), new(SpeechWord.ContactCount, g.Members.Count)];
            if (g.Level >= IdentificationLevel.Class && g.Class != AircraftClass.Unknown)
                tokens.Add(new(SpeechWord.ContactClass, (int)g.Class * (g.Members.Count > 1 ? -1 : 1)));
            if (g.Level >= IdentificationLevel.Type && g.CalledType is not null)
                tokens.Add(new(SpeechWord.ContactType)); // spoken only by packs that have "contact.type"
            tokens.Add(new(SpeechWord.Clock, RadioPhrases.ClockHour(g.Relative.BearingDegrees, look.PlayerHeading)));
            tokens.Add(new(SpeechWord.Range, g.Relative.RangeMetres));
            tokens.Add(new(SpeechWord.Vertical, g.Relative.AltitudeDeltaMetres));
            return new RadioReply(reporter.Contact, g.Members.Select(m => m.Target.Contact).ToList(), g.Relative, g.Members.Max(m => m.Target.AgeSeconds), text, tokens);
        }).ToList();

        return new RadioResult(ReplyOutcome.Position, replies, suppressed);
    }

    /// <summary>Command 4: the nearest spotted aircraft (any side, except the player's own flight), identified as far as possible.</summary>
    public static RadioResult Identify(ContactTracker tracker, RadioSettings? settings = null, SightingMemory? memory = null)
    {
        settings ??= new RadioSettings();
        var suppressed = new List<SuppressedContact>();
        if (Look(tracker, settings, memory ?? new SightingMemory(), suppressed) is not { } look)
            return new RadioResult(ReplyOutcome.PlayerPositionUnknown, [], suppressed);
        if (look.Observers.Count == 0)
            return new RadioResult(ReplyOutcome.NoReply, [], suppressed);

        var nearest = Group(look.Sighted, look).OrderBy(g => g.Relative.RangeMetres).FirstOrDefault();
        if (nearest is null)
        {
            var lead = Leader(look.Observers);
            return new RadioResult(ReplyOutcome.Position,
                [new RadioReply(lead.Contact, [], lead.Relative, lead.AgeSeconds, $"{RadioPhrases.Who(lead.Contact)}, no contacts to identify.",
                    [.. SpeechToken.Identify(lead.Contact), new(SpeechWord.NoContacts)])],
                suppressed);
        }

        var g = nearest;
        string heading = g.Heading is double h ? $", heading {Navigation.CompassPoint(h)}" : "";
        string text = $"{RadioPhrases.Who(g.Reporter.Contact)}, contact at your {RadioPhrases.Clock(g.Relative.BearingDegrees, look.PlayerHeading)}, " +
                      $"{RadioPhrases.Range(g.Relative.RangeMetres)}: {Identification(g, look.Player)}{heading}.";
        List<SpeechToken> tokens = [.. SpeechToken.Identify(g.Reporter.Contact), new(SpeechWord.ContactCount, g.Members.Count)];
        if (g.Level >= IdentificationLevel.Class && g.Class != AircraftClass.Unknown)
            tokens.Add(new(SpeechWord.ContactClass, (int)g.Class * (g.Members.Count > 1 ? -1 : 1)));
        tokens.AddRange([new(SpeechWord.ContactType), new(SpeechWord.Clock, RadioPhrases.ClockHour(g.Relative.BearingDegrees, look.PlayerHeading)),
            new(SpeechWord.Range, g.Relative.RangeMetres), new(SpeechWord.Vertical, g.Relative.AltitudeDeltaMetres)]);
        var reply = new RadioReply(g.Reporter.Contact, g.Members.Select(m => m.Target.Contact).ToList(), g.Relative, g.Members.Max(m => m.Target.AgeSeconds), text, tokens);
        return new RadioResult(ReplyOutcome.Position, [reply], suppressed);
    }

    private sealed record LookResult(Contact Player, PositionFix PlayerFix, double PlayerHeading, IReadOnlyList<LiveContact> Observers, IReadOnlyList<SightedContact> Sighted);

    private sealed record SightedGroup(IReadOnlyList<SightedContact> Members, RelativePosition Relative, double? Heading, SightedContact Reporting)
    {
        public LiveContact Reporter => Reporting.Reporter;
        public IdentificationLevel Level => Members.Max(m => m.Level);
        public AircraftClass Class => Members.Where(m => m.Level >= IdentificationLevel.Class).Select(m => m.Type.Class).Distinct().Count() == 1
            ? Members.First(m => m.Level >= IdentificationLevel.Class).Type.Class
            : AircraftClass.Unknown;
        public AircraftType? CalledType => Members.Where(m => m.Level >= IdentificationLevel.Type).Select(m => m.CalledType).Distinct().Count() == 1
            ? Members.First(m => m.Level >= IdentificationLevel.Type).CalledType
            : null;
        /// <summary>Hedged only if every type-identified member is; one clear look settles the call for the group.</summary>
        public bool Hedged => Members.Where(m => m.Level >= IdentificationLevel.Type).All(m => m.Hedged);
    }

    /// <summary>Everything the observers can see right now, merged with what they saw recently.</summary>
    private static LookResult? Look(ContactTracker tracker, RadioSettings settings, SightingMemory memory, List<SuppressedContact> suppressed)
    {
        memory.Sync(tracker);
        if (RadioPicture.Build(tracker, settings, RadioPicture.IsFriendly, suppressed) is not { } friendly)
            return null;

        var flight = friendly.Live.Where(l => l.Contact.InPlayerFlight).ToList();
        IReadOnlyList<LiveContact> observers = flight.Count > 0 ? flight : friendly.Live;
        var observerKeys = observers.Select(o => o.Contact.Key).ToHashSet();

        // Candidate targets: every other live aircraft, friend or foe, except the player and the observers' own flight.
        var others = new List<SuppressedContact>();
        var candidates = RadioPicture.Build(tracker, settings with { RadioRangeMetres = double.MaxValue },
                (p, c) => !c.InPlayerFlight && !observerKeys.Contains(c.Key), others)!.Live;

        var visibility = new Visibility(tracker.Plan?.Weather ?? MissionWeather.Unknown, settings.Visibility);
        var timeOfDay = (tracker.Plan?.Weather ?? MissionWeather.Unknown).StartTime + TimeSpan.FromSeconds(friendly.PlayerFix.SourceTime);
        double now = friendly.PlayerFix.SourceTime;
        long slot = (long)Math.Floor(now / SlotSeconds);
        var sighted = new List<SightedContact>();

        foreach (var target in candidates)
        {
            var type = AircraftTypes.Classify(target.Contact.TypeName);
            LiveContact? spotter = null;
            var level = IdentificationLevel.None;
            foreach (var o in observers)
            {
                double p = visibility.DetectionProbability(o.Fix.Position, target.Fix.Position, type, timeOfDay);
                if (p <= 0 || Visibility.Roll(o.Contact.Key, target.Contact.Key, slot) >= p)
                    continue;
                var l = visibility.MaxIdentification(o.Fix.Position, target.Fix.Position, type, timeOfDay);
                if (spotter is null || l > level || (l == level && Distance(o, target) < Distance(spotter, target)))
                    (spotter, level) = (o, l);
            }

            var previous = memory.Get(target.Contact.Key);
            bool remembered = previous is not null && now - previous.LastSeen <= memory.RetainSeconds
                              && observers.Any(o => visibility.DetectionProbability(o.Fix.Position, target.Fix.Position, type, timeOfDay) > 0.02);
            if (spotter is null && !remembered)
            {
                suppressed.Add(new(target.Contact, "not spotted by any observer"));
                continue;
            }

            spotter ??= observers.MinBy(o => Distance(o, target))!;
            var known = (IdentificationLevel)Math.Max((int)level, (int)(previous?.Level ?? IdentificationLevel.None));

            // The type actually called: correct when close, possibly a hedged look-alike near the limit.
            AircraftType? called = previous?.CalledType;
            bool hedged = previous?.Hedged ?? false;
            if (known >= IdentificationLevel.Type)
            {
                double confidence = visibility.TypeConfidence(spotter.Fix.Position, target.Fix.Position, type, timeOfDay);
                if (called is null)
                {
                    var lookAlike = AircraftTypes.LookAlike(type);
                    bool confused = lookAlike is not null && confidence < 0.4
                                    && Visibility.Roll(spotter.Contact.Key, target.Contact.Key, 0, salt: 7) < visibility.Settings.MisidentificationChance;
                    (called, hedged) = confused ? (lookAlike, true) : (type, confidence < 0.4);
                }
                else if (confidence > 0.6)
                {
                    (called, hedged) = (type, false); // a closer look settles it
                }
            }

            memory.Set(target.Contact.Key, new SightingMemory.Sighting(spotter is not null && level > IdentificationLevel.None ? now : previous?.LastSeen ?? now, known, called, hedged));
            sighted.Add(new SightedContact(target, type, known, called, hedged, spotter));
        }

        return new LookResult(friendly.Player, friendly.PlayerFix, friendly.PlayerFix.Heading ?? 0, observers, sighted);
    }

    private static IEnumerable<SightedGroup> Group(IEnumerable<SightedContact> sighted, LookResult look)
    {
        var remaining = sighted.ToList();
        while (remaining.Count > 0)
        {
            var members = new List<SightedContact> { remaining[0] };
            remaining.RemoveAt(0);
            for (int i = 0; i < members.Count; i++)
            {
                for (int j = remaining.Count - 1; j >= 0; j--)
                {
                    if (Navigation.Relative(members[i].Target.Fix.Position, remaining[j].Target.Fix.Position) is not { } r
                        || r.RangeMetres > ContactGroupRadiusMetres || Math.Abs(r.AltitudeDeltaMetres) > ContactGroupAltitudeBandMetres
                        || SameSideKnown(members[i], remaining[j], look.Player) is false)
                        continue;
                    members.Add(remaining[j]);
                    remaining.RemoveAt(j);
                }
            }

            var p = members[0].Target.Fix.Position;
            var centre = new WorldPoint(members.Average(m => m.Target.Fix.Position.North), members.Average(m => m.Target.Fix.Position.East),
                members.Average(m => m.Target.Fix.Position.Altitude), p.Frame);
            var headings = members.Select(m => m.Target.Fix.Heading).OfType<double>().ToList();
            double? heading = headings.Count == 0 ? null
                : (Math.Atan2(headings.Sum(x => Math.Sin(x * Math.PI / 180)), headings.Sum(x => Math.Cos(x * Math.PI / 180))) * 180 / Math.PI + 360) % 360;
            var reporting = members.MinBy(m => Distance(m.Reporter, m.Target))!;
            yield return new SightedGroup(members, Navigation.Relative(look.PlayerFix.Position, centre)!.Value, heading, reporting);
        }
    }

    /// <summary>False only when both sides are known and differ; aircraft of known opposing sides never merge.</summary>
    private static bool? SameSideKnown(SightedContact a, SightedContact b, Contact player) =>
        a.Level >= IdentificationLevel.Side && b.Level >= IdentificationLevel.Side
            ? IsFriendly(player, a.Target.Contact) == IsFriendly(player, b.Target.Contact)
            : null;

    private static string Describe(SightedGroup g, Contact player)
    {
        int n = g.Members.Count;
        bool plural = n > 1;
        string count = n switch
        {
            1 => "single",
            2 => "pair of",
            3 => "three",
            4 => "four",
            < 10 => "group of",
            _ => "large formation of",
        };
        bool enemy = g.Level >= IdentificationLevel.Side && !g.Members.All(m => m.Level >= IdentificationLevel.Side && IsFriendly(player, m.Target.Contact));

        if (g.Level >= IdentificationLevel.Type && g.CalledType is { } t)
            return $"{count} {(g.Hedged ? "possible " : "")}{(plural ? t.Plural : t.Family)}";
        if (g.Level >= IdentificationLevel.Side && g.Class != AircraftClass.Unknown)
            return $"{count} {(enemy ? "enemy" : "friendly")} {AircraftTypes.ClassName(g.Class, plural)}";
        if (g.Level >= IdentificationLevel.Class && g.Class != AircraftClass.Unknown)
            return $"{count} unidentified {AircraftTypes.ClassName(g.Class, plural)}";
        return $"{count} unidentified aircraft";
    }

    private static string Identification(SightedGroup g, Contact player)
    {
        bool plural = g.Members.Count > 1;
        bool friendly = g.Members.All(m => IsFriendly(player, m.Target.Contact));
        string side = friendly ? "friendly" : "enemy";

        if (g.Level >= IdentificationLevel.Type && g.CalledType is { } t)
        {
            string name = plural ? $"{RadioPhrases.Count(g.Members.Count)} {t.Plural}" : $"a {t.Family}";
            return g.Hedged ? $"looks like {name}, can't be sure" : $"{name}, {side}";
        }

        if (g.Level >= IdentificationLevel.Side)
            return $"{side} {AircraftTypes.ClassName(g.Class, plural)}, type not yet identified";
        if (g.Level >= IdentificationLevel.Class && g.Class != AircraftClass.Unknown)
            return $"{AircraftTypes.ClassName(g.Class, plural)}, too far to see markings";
        return "too far to identify";
    }

    private static string HighLow(double altitudeDeltaMetres) =>
        altitudeDeltaMetres > 300 ? "high" : altitudeDeltaMetres < -300 ? "low" : "level";

    private static bool IsFriendly(Contact player, Contact c) => RadioPicture.IsFriendly(player, c);

    private static LiveContact Leader(IReadOnlyList<LiveContact> observers) =>
        observers.Where(o => o.Contact.FormationIndex is not null).MinBy(o => o.Contact.FormationIndex) ?? observers.MinBy(o => o.Relative.RangeMetres)!;

    private static double Distance(LiveContact a, LiveContact b) =>
        Navigation.Relative(a.Fix.Position, b.Fix.Position)?.RangeMetres ?? double.MaxValue;
}
