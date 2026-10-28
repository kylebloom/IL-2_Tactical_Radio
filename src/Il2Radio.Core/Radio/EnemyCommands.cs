using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Radio;

/// <summary>
/// What the flight has seen of other aircraft across requests: a spot is kept for
/// <see cref="RetainSeconds"/> after the aircraft leaves sight, and its identification only improves
/// (or is corrected) on a look that still sees it.
/// One instance per live session; it resets itself when the mission restarts.
/// </summary>
public sealed class SightingMemory
{
    /// <param name="Course">
    /// Its direction of flight as made out on the last look that saw it, when that look was at
    /// <see cref="EnemyCommands.CourseLevel"/> or better; null when that look could not tell. Frozen once it is out of sight.
    /// </param>
    internal sealed record Sighting(double LastSeen, IdentificationLevel Level, AircraftType? CalledType, bool Hedged, WorldPoint Where, double? Heading, double? Course = null);

    private readonly Dictionary<string, Sighting> _sightings = new(StringComparer.Ordinal);
    private int _missionStarts = -1;

    /// <summary>How long a contact is still mentioned after the look that saw it, seconds of track time.</summary>
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

/// <summary>
/// An aircraft the flight currently knows about, and how well. <see cref="Held"/> means nobody can see it
/// on this look; <see cref="Where"/> is then the place it was last seen, and the reply must not use the
/// live position.
/// </summary>
internal sealed record SightedContact(LiveContact Target, AircraftType Type, IdentificationLevel Level, AircraftType? CalledType, bool Hedged, LiveContact Reporter, bool Held, WorldPoint Where, double? SeenHeading, double? SeenCourse = null)
{
    public WorldPoint Plot => Held ? Where : Target.Fix.Position;
}

/// <summary>
/// Commands 2 and 4: "Request enemy contacts" and "Identify contact". Observers are the player's own
/// flight (or, flying alone, any friendly within radio range). Only aircraft that exist in the live
/// track can be reported, and only once the visibility model says an observer has spotted them; what is
/// said about them is limited to what that observer could make out at that range. A spot is kept for
/// <see cref="SightingMemory.RetainSeconds"/> after the aircraft leaves sight, and is then reported as
/// where it was seen. In
/// <see cref="RadioRealism.Pilot"/> the position is the player's clock only when the spotter can see the
/// player or is in formation; a spotter in the fight who cannot says "on me", and otherwise gives his own clock.
/// </summary>
public static class EnemyCommands
{
    private const double SlotSeconds = 15;
    private const double ContactGroupRadiusMetres = 3_000;
    private const double ContactGroupAltitudeBandMetres = 1_000;
    private const int MaxReportedGroups = 4;

    /// <summary>
    /// A contact call says which way a group is flying only from a look at least this good. At class range (about
    /// 4 km for a fighter, 400 m per metre of span) the planform is made out, and with it which way the nose points;
    /// a speck at "sighted" range shows only that it is there (SPECULATIVE, the same game-design scale as the rest
    /// of the visibility model). The direction is the group's course over ground (memory: measured over
    /// <see cref="ContactTracker.CourseBaselineMetres"/>; ACMI: the track's yaw), on the eight-point compass.
    /// </summary>
    public const IdentificationLevel CourseLevel = IdentificationLevel.Class;

    /// <summary>
    /// The members' courses must agree this well (mean resultant length, 1 = all the same) for the group to have a
    /// direction of flight. About ±45° of spread; a furball going every way has none.
    /// </summary>
    private const double CourseAgreement = 0.7;

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

        var replies = settings.Realism == RadioRealism.Pilot
            ? groups.Select(g => PilotEnemyReply(g, look, tracker, settings)).ToList()
            : groups.Select(g =>
        {
            var reporter = g.Reporter;
            string seen = g.Held ? "last seen " : "";
            string text = $"{RadioPhrases.Who(reporter.Contact)}, {Describe(g, look.Player)}, {seen}at your {RadioPhrases.Clock(g.Relative.BearingDegrees, look.PlayerHeading)}, " +
                          $"{RadioPhrases.Range(g.Relative.RangeMetres)}, {HighLow(g.Relative.AltitudeDeltaMetres)}";
            List<SpeechToken> tokens = [.. SpeechToken.Identify(reporter.Contact), new(SpeechWord.ContactCount, g.Members.Count)];
            if (g.Held)
                tokens.Add(new(SpeechWord.LastSeen));
            bool typed = g.Level >= IdentificationLevel.Type && g.CalledType is not null;
            // Mirrors Describe: the side is said only for a class-level call ("enemy fighters"), never with a type.
            if (!typed && g.Level >= IdentificationLevel.Side && g.Class != AircraftClass.Unknown)
                tokens.Add(new(SpeechWord.ContactSide, IsEnemyCall(g, look.Player) ? -1 : 1));
            if (g.Level >= IdentificationLevel.Class && g.Class != AircraftClass.Unknown)
                tokens.Add(new(SpeechWord.ContactClass, (int)g.Class * (g.Members.Count > 1 ? -1 : 1)));
            if (typed)
            {
                if (g.Hedged)
                    tokens.Add(new(SpeechWord.Hedge));
                tokens.Add(new(SpeechWord.ContactType, g.Members.Count, g.CalledType!.Family));
            }
            tokens.Add(new(SpeechWord.Clock, RadioPhrases.ClockHour(g.Relative.BearingDegrees, look.PlayerHeading)));
            tokens.Add(new(SpeechWord.Range, g.Relative.RangeMetres));
            tokens.Add(new(SpeechWord.Vertical, g.Relative.AltitudeDeltaMetres));
            text += FlyingTo(g, tokens) + ".";
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
        if (settings.Realism == RadioRealism.Pilot)
            return new RadioResult(ReplyOutcome.Position, [PilotIdentifyReply(g, look, tracker, settings)], suppressed);

        string heading = g.Heading is double h ? $", heading {Navigation.CompassPoint(h)}" : "";
        string seen = g.Held ? "last seen " : "";
        string text = $"{RadioPhrases.Who(g.Reporter.Contact)}, contact {seen}at your {RadioPhrases.Clock(g.Relative.BearingDegrees, look.PlayerHeading)}, " +
                      $"{RadioPhrases.Range(g.Relative.RangeMetres)}: {Identification(g, look.Player)}{heading}.";
        List<SpeechToken> tokens = [.. SpeechToken.Identify(g.Reporter.Contact), new(SpeechWord.ContactCount, g.Members.Count)];
        if (g.Held)
            tokens.Add(new(SpeechWord.LastSeen));
        if (g.Level >= IdentificationLevel.Class && g.Class != AircraftClass.Unknown)
            tokens.Add(new(SpeechWord.ContactClass, (int)g.Class * (g.Members.Count > 1 ? -1 : 1)));
        // Mirrors Identification: a hedged type carries no side; a sure type or a side-level look does.
        int side = g.Members.All(m => IsFriendly(look.Player, m.Target.Contact)) ? 1 : -1;
        if (g.Level >= IdentificationLevel.Type && g.CalledType is { } called)
        {
            if (g.Hedged)
                tokens.Add(new(SpeechWord.Hedge));
            tokens.Add(new(SpeechWord.ContactType, g.Members.Count, called.Family));
            if (!g.Hedged)
                tokens.Add(new(SpeechWord.ContactSide, side));
        }
        else
        {
            tokens.Add(new(SpeechWord.ContactType, g.Members.Count)); // no type made out: nothing to name
            if (g.Level >= IdentificationLevel.Side)
                tokens.Add(new(SpeechWord.ContactSide, side));
        }
        tokens.AddRange([new(SpeechWord.Clock, RadioPhrases.ClockHour(g.Relative.BearingDegrees, look.PlayerHeading)),
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

        /// <summary>Every member is being reported from memory: nobody has them in sight on this look.</summary>
        public bool Held => Members.All(m => m.Held);

        /// <summary>
        /// Which way the group is flying, from the members whose look showed it (<see cref="SightedContact.SeenCourse"/>),
        /// or null when none did or their courses disagree by more than <see cref="CourseAgreement"/> allows.
        /// </summary>
        public double? Course
        {
            get
            {
                var courses = Members.Select(m => m.SeenCourse).OfType<double>().ToList();
                if (courses.Count == 0)
                    return null;
                double x = courses.Sum(c => Math.Cos(c * Math.PI / 180)), y = courses.Sum(c => Math.Sin(c * Math.PI / 180));
                if (Math.Sqrt(x * x + y * y) / courses.Count < CourseAgreement)
                    return null;
                return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
            }
        }
    }

    /// <summary>", heading west" and its <see cref="SpeechWord.Heading"/> token, when the group has a direction of flight; else nothing.</summary>
    private static string FlyingTo(SightedGroup g, List<SpeechToken> tokens)
    {
        if (g.Course is not double course)
            return "";
        tokens.Add(new(SpeechWord.Heading, course));
        return $", heading {Navigation.CompassPoint(course)}";
    }

    /// <summary>
    /// One look at the current picture, recorded into <paramref name="memory"/> and not spoken. The live
    /// session calls this as each track frame closes, so a glimpse between radio calls is not discarded.
    /// </summary>
    public static void Notice(ContactTracker tracker, RadioSettings? settings, SightingMemory memory) =>
        Look(tracker, settings ?? new RadioSettings(), memory, []);

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
            bool remembered = previous is not null && now - previous.LastSeen <= memory.RetainSeconds;
            if (spotter is null && !remembered)
            {
                suppressed.Add(new(target.Contact, "not spotted by any observer"));
                continue;
            }

            bool held = spotter is null;
            spotter ??= observers.MinBy(o => Distance(o, target))!;
            var known = (IdentificationLevel)Math.Max((int)level, (int)(previous?.Level ?? IdentificationLevel.None));

            // The type actually called: correct when close, possibly a hedged look-alike near the limit.
            // A held contact keeps the call from the look that saw it; the aircraft's present position is not a new look.
            AircraftType? called = previous?.CalledType;
            bool hedged = previous?.Hedged ?? false;
            if (!held && known >= IdentificationLevel.Type)
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

            var where = held ? previous!.Where : target.Fix.Position;
            double? heading = held ? previous!.Heading : target.Fix.Heading;
            // Direction of flight: from this look only when it is good enough to see the shape; a held contact keeps
            // the one it had when last seen, never the live track's.
            double? course = held ? previous!.Course : level >= CourseLevel ? target.Fix.Heading : null;
            memory.Set(target.Contact.Key, new SightingMemory.Sighting(
                level > IdentificationLevel.None ? now : previous?.LastSeen ?? now, known, called, hedged, where, heading, course));
            sighted.Add(new SightedContact(target, type, known, called, hedged, spotter, held, where, heading, course));
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
                    if (Navigation.Relative(members[i].Plot, remaining[j].Plot) is not { } r
                        || r.RangeMetres > ContactGroupRadiusMetres || Math.Abs(r.AltitudeDeltaMetres) > ContactGroupAltitudeBandMetres
                        || SameSideKnown(members[i], remaining[j], look.Player) is false)
                        continue;
                    members.Add(remaining[j]);
                    remaining.RemoveAt(j);
                }
            }

            var p = members[0].Plot;
            var centre = new WorldPoint(members.Average(m => m.Plot.North), members.Average(m => m.Plot.East),
                members.Average(m => m.Plot.Altitude), p.Frame);
            var headings = members.Select(m => m.Held ? m.SeenHeading : m.Target.Fix.Heading).OfType<double>().ToList();
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

    /// <summary>A pair or a flight of four, once their shapes can be separated. Farther out, or a bigger formation, is "a few" or "many".</summary>
    private static bool ExactCount(SightedGroup g) =>
        g.Members.Count == 1 || (g.Members.Count <= 4 && g.Level >= IdentificationLevel.Class);

    private static string Strength(SightedGroup g, bool coarse)
    {
        int n = g.Members.Count;
        if (coarse && !ExactCount(g))
            return n >= 7 ? "many" : "a few";
        return n switch
        {
            1 => "single",
            2 => "pair of",
            3 => "three",
            4 => "four",
            < 10 => "group of",
            _ => "large formation of",
        };
    }

    private static string Describe(SightedGroup g, Contact player, bool coarse = false)
    {
        int n = g.Members.Count;
        bool plural = n > 1;
        string count = Strength(g, coarse);
        bool enemy = IsEnemyCall(g, player);

        if (g.Level >= IdentificationLevel.Type && g.CalledType is { } t)
            return $"{count} {(g.Hedged ? "possible " : "")}{(plural ? t.Plural : t.Family)}";
        if (g.Level >= IdentificationLevel.Side && g.Class != AircraftClass.Unknown)
            return $"{count} {(enemy ? "enemy" : "friendly")} {AircraftTypes.ClassName(g.Class, plural)}";
        if (g.Level >= IdentificationLevel.Class && g.Class != AircraftClass.Unknown)
            return $"{count} unidentified {AircraftTypes.ClassName(g.Class, plural)}";
        return $"{count} unidentified aircraft";
    }

    /// <summary>A side-level group is called "enemy" unless every member's markings were seen and are friendly.</summary>
    private static bool IsEnemyCall(SightedGroup g, Contact player) =>
        g.Level >= IdentificationLevel.Side && !g.Members.All(m => m.Level >= IdentificationLevel.Side && IsFriendly(player, m.Target.Contact));

    private static string Identification(SightedGroup g, Contact player, bool coarse = false)
    {
        bool plural = g.Members.Count > 1;
        bool friendly = g.Members.All(m => IsFriendly(player, m.Target.Contact));
        string side = friendly ? "friendly" : "enemy";

        if (g.Level >= IdentificationLevel.Type && g.CalledType is { } t)
        {
            string name = coarse && !ExactCount(g)
                ? g.Members.Count >= 7 ? $"many {t.Plural}" : $"a few {t.Plural}"
                : plural ? $"{RadioPhrases.Count(g.Members.Count)} {t.Plural}" : $"a {t.Family}";
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

    private static RadioReply PilotEnemyReply(SightedGroup g, LookResult look, ContactTracker tracker, RadioSettings settings)
    {
        var reporter = g.Reporter;
        bool fuzzy = !ExactCount(g);
        string what = Describe(g, look.Player, coarse: true);
        string who = RadioPhrases.Who(reporter.Contact);
        List<SpeechToken> tokens = [.. SpeechToken.Identify(reporter.Contact)];
        AddSightingTokens(tokens, g, look.Player, includeCount: true, fuzzy: fuzzy);

        string text;
        bool fromSpeaker = false;
        string seen = g.Held ? "last seen " : "";
        if (g.Held)
            tokens.Add(new(SpeechWord.LastSeen));
        if (WithPlayer(reporter, look, tracker, settings))
        {
            text = ContactFrom(who, what, tokens, g.Relative, look.PlayerFix.Heading, mine: false, seen, g);
        }
        else
        {
            fromSpeaker = true;
            var fromHim = RelativeTo(reporter, g.Members);
            if (Engaged(g) || fromHim is null)
            {
                string band = fromHim is { } r && Math.Abs(r.AltitudeDeltaMetres) >= PilotReport.LevelMetres ? $", {HighLow(r.AltitudeDeltaMetres)}" : "";
                bool mates = KnownFriendly(g, look.Player);
                text = $"{who}, {what}, {seen}{(mates ? "with me" : "on me")}{band}.";
                if (!mates)
                    tokens.Add(new(SpeechWord.OnMe));
                if (fromHim is { } height && Math.Abs(height.AltitudeDeltaMetres) >= PilotReport.LevelMetres)
                    tokens.Add(new(SpeechWord.Vertical, height.AltitudeDeltaMetres));
            }
            else
            {
                text = ContactFrom(who, what, tokens, fromHim.Value, reporter.Fix.Heading, mine: true, seen, g);
            }
        }

        return new RadioReply(reporter.Contact, g.Members.Select(m => m.Target.Contact).ToList(), g.Relative, g.Members.Max(m => m.Target.AgeSeconds), text, tokens)
        { FromSpeaker = fromSpeaker };
    }

    private static RadioReply PilotIdentifyReply(SightedGroup g, LookResult look, ContactTracker tracker, RadioSettings settings)
    {
        var reporter = g.Reporter;
        bool fuzzy = !ExactCount(g);
        bool countInText = !fuzzy || g.Level >= IdentificationLevel.Type;
        string who = RadioPhrases.Who(reporter.Contact);
        string id = Identification(g, look.Player, coarse: true);
        string heading = g.Level >= IdentificationLevel.Type && g.Heading is double h ? $", heading {Navigation.CompassPoint(h)}" : "";
        List<SpeechToken> tokens = [.. SpeechToken.Identify(reporter.Contact)];
        AddSightingTokens(tokens, g, look.Player, includeCount: countInText, fuzzy: fuzzy);
        // A sure type call says the side ("a Yak, enemy"); a hedge does not. Class-level calls already got it above.
        if (g.Level >= IdentificationLevel.Type && g.CalledType is not null && !g.Hedged)
            tokens.Add(new(SpeechWord.ContactSide, g.Members.All(m => IsFriendly(look.Player, m.Target.Contact)) ? 1 : -1));

        string seen = g.Held ? "last seen " : "";
        if (g.Held)
            tokens.Add(new(SpeechWord.LastSeen));
        string text;
        bool fromSpeaker = false;
        if (WithPlayer(reporter, look, tracker, settings))
        {
            var where = PilotReport.Direction(g.Relative.BearingDegrees, look.PlayerFix.Heading, mine: false);
            text = $"{who}, contact {seen}{where.Text}, {PilotReport.Range(g.Relative.RangeMetres)}: {id}{heading}.";
            tokens.Add(where.Token);
            tokens.Add(new(SpeechWord.RangeBand, PilotReport.RangeBand(g.Relative.RangeMetres)));
        }
        else
        {
            fromSpeaker = true;
            var fromHim = RelativeTo(reporter, g.Members);
            if (Engaged(g) || fromHim is null)
            {
                bool mates = KnownFriendly(g, look.Player);
                text = $"{who}, {seen}{(mates ? "with me" : "on me")}: {id}{heading}.";
                if (!mates)
                    tokens.Add(new(SpeechWord.OnMe));
            }
            else
            {
                var where = PilotReport.Direction(fromHim.Value.BearingDegrees, reporter.Fix.Heading, mine: true);
                text = $"{who}, contact {seen}{where.Text}, {PilotReport.Range(fromHim.Value.RangeMetres)}: {id}{heading}.";
                tokens.Add(where.Token);
                tokens.Add(new(SpeechWord.RangeBand, PilotReport.RangeBand(fromHim.Value.RangeMetres)));
            }
        }

        return new RadioReply(reporter.Contact, g.Members.Select(m => m.Target.Contact).ToList(), g.Relative, g.Members.Max(m => m.Target.AgeSeconds), text, tokens)
        { FromSpeaker = fromSpeaker };
    }

    /// <summary>The class, side and type tokens, in the same order as a precise call. A fuzzy count is "a few" or "many".</summary>
    private static void AddSightingTokens(List<SpeechToken> tokens, SightedGroup g, Contact player, bool includeCount, bool fuzzy)
    {
        if (includeCount)
            tokens.Add(fuzzy
                ? new(SpeechWord.ContactAmount, g.Members.Count >= 7 ? 4 : 3)
                : new(SpeechWord.ContactCount, g.Members.Count));
        bool typed = g.Level >= IdentificationLevel.Type && g.CalledType is not null;
        if (!typed && g.Level >= IdentificationLevel.Side && g.Class != AircraftClass.Unknown)
            tokens.Add(new(SpeechWord.ContactSide, IsEnemyCall(g, player) ? -1 : 1));
        if (g.Level >= IdentificationLevel.Class && g.Class != AircraftClass.Unknown)
            tokens.Add(new(SpeechWord.ContactClass, (int)g.Class * (g.Members.Count > 1 ? -1 : 1)));
        if (typed)
        {
            if (g.Hedged)
                tokens.Add(new(SpeechWord.Hedge));
            tokens.Add(new(SpeechWord.ContactType, g.Members.Count, g.CalledType!.Family));
        }
    }

    /// <summary>
    /// Clock, coarse range and high/low, then which way they are flying when the look showed it. Contact calls say
    /// "high", not "above you". A group "on me" or "with me" gets no direction: it is fighting or flying with him.
    /// </summary>
    private static string ContactFrom(string who, string what, List<SpeechToken> tokens, RelativePosition relative, double? noseDegrees, bool mine, string seen, SightedGroup g)
    {
        var where = PilotReport.Direction(relative.BearingDegrees, noseDegrees, mine);
        tokens.Add(where.Token);
        tokens.Add(new(SpeechWord.RangeBand, PilotReport.RangeBand(relative.RangeMetres)));
        tokens.Add(new(SpeechWord.Vertical, relative.AltitudeDeltaMetres));
        return $"{who}, {what}, {seen}{where.Text}, {PilotReport.Range(relative.RangeMetres)}, {HighLow(relative.AltitudeDeltaMetres)}{FlyingTo(g, tokens)}.";
    }

    private static bool WithPlayer(LiveContact reporter, LookResult look, ContactTracker tracker, RadioSettings settings) =>
        settings.InFormation(reporter.Relative)
        || PilotReport.CanSeePlayer(reporter, look.Player, look.PlayerFix, tracker, settings);

    /// <summary>
    /// He has made out their markings and they are all on his side. "On me" means an enemy is on him; friendly
    /// aircraft that close are "with me" (2026-10-26 flight: "on me: friendly fighters" for a pair of 109s).
    /// </summary>
    private static bool KnownFriendly(SightedGroup g, Contact player) =>
        g.Level >= IdentificationLevel.Side && g.Members.All(m => IsFriendly(player, m.Target.Contact));

    private static bool Engaged(SightedGroup g) =>
        g.Members.Any(m => (Navigation.Relative(g.Reporter.Fix.Position, m.Plot)?.RangeMetres ?? double.MaxValue) <= PilotReport.EngagedMetres);

    private static RelativePosition? RelativeTo(LiveContact origin, IReadOnlyList<SightedContact> members)
    {
        var p = members[0].Plot;
        var centre = new WorldPoint(
            members.Average(m => m.Plot.North),
            members.Average(m => m.Plot.East),
            members.Average(m => m.Plot.Altitude),
            p.Frame);
        return Navigation.Relative(origin.Fix.Position, centre);
    }
}
