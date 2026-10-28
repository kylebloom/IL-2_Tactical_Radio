using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Radio;

/// <summary>How much of the kneeboard is on screen. One key cycles hidden → small → large → hidden.</summary>
public enum KneeboardView { Hidden, Small, Large }

/// <summary>Whose mark it is.</summary>
public enum PlotSubject { Player, Mate, Contact }

/// <summary>A map square or its ninth, shaded whole (<see cref="Cell"/>), or a fuzzy patch round a point (<see cref="Patch"/>).</summary>
public enum PlotShape { Cell, Patch }

/// <summary>
/// One pencil mark on the kneeboard, made by something the radio said. <see cref="North"/>/<see cref="East"/> are mission
/// metres; for a <see cref="PlotShape.Cell"/> they are its south-west corner and <see cref="Size"/> its side, for a
/// <see cref="PlotShape.Patch"/> its centre and radius. It never moves, except the player's dead-reckoning mark.
/// </summary>
public sealed record PlotMark(PlotSubject Subject, string Key, string Label, PlotShape Shape, double North, double East, double Size, DateTime MadeUtc)
{
    /// <summary>Which way a contact was flying when it was called, true degrees; null when the call did not say.</summary>
    public double? Course { get; init; }

    /// <summary>The player's mark carried on from his last fix by his own heading and speed.</summary>
    public bool DeadReckoned { get; init; }

    /// <summary>A mate in formation with the player: drawn as a note beside the player's mark, not a mark of its own.</summary>
    public bool WithPlayer { get; init; }

    /// <summary>The call placed it where it was last seen.</summary>
    public bool LastSeen { get; init; }

    /// <summary>Side as far as the call said: negative enemy, positive friendly, 0 not made out.</summary>
    public int Side { get; init; }

    /// <summary>Who made it and when, for the footer ("12:38, Lumme 1").</summary>
    public string? Source { get; init; }

    /// <summary>The aircraft the call was about, so a newer call about any of them replaces this mark.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];

    /// <summary>Centre and a radius that covers the mark, whatever its shape.</summary>
    public (double North, double East, double Radius) Spread =>
        Shape == PlotShape.Cell ? (North + Size / 2, East + Size / 2, Size / Math.Sqrt(2)) : (North, East, Size);
}

/// <summary>A mark as it is drawn now: its age and how far it has faded.</summary>
public sealed record PlotEntry(PlotMark Mark, TimeSpan Age, double Alpha)
{
    /// <summary>"now", "1 min", "6 min".</summary>
    public string AgeText => Age.TotalSeconds < 60 ? "now" : $"{(int)Age.TotalMinutes} min";
}

/// <summary>Everything the overlay draws for one frame of the kneeboard.</summary>
public sealed record KneeboardPicture(KneeboardView View, KneeboardSheet Sheet, IReadOnlyList<PlotEntry> Marks, PlotEntry? Player, string? OffSheet,
    IReadOnlyList<string> WithPlayer, IReadOnlyList<string> Footer)
{
    /// <summary>A string that changes whenever the drawing would, so the overlay redraws only then.</summary>
    public string Signature =>
        $"{View}|{Player?.Mark.North:F0},{Player?.Mark.East:F0},{Player?.Mark.Size:F0},{Player?.AgeText},{Player?.Alpha:F2}|{OffSheet}|{string.Join(",", WithPlayer)}|" +
        string.Join(";", Marks.Select(m => $"{m.Mark.Key}@{m.Mark.North:F0},{m.Mark.East:F0},{m.Mark.Size:F0},{m.AgeText},{m.Alpha:F2}")) + "|" + string.Join("/", Footer);
}

/// <summary>
/// The kneeboard plot: the player's own sketch of the briefed area, with pencil marks for what the radio has already said.
/// Nothing on it is live. A mark is made by a reply at the moment it is said, from the words of that reply (a square, a
/// clock and a range band, "on me") and the player's own instruments (his compass for a clock position), never from the
/// tracker's positions; it stays put until a newer reply moves it, and fades as it ages. The player's own mark comes from
/// an ally who places him (his leader's route reply, a mate who has him in formation) or from his own fix by a landmark
/// (<see cref="FixCommand"/>), and is then carried on by dead reckoning, whose patch grows quickly enough that it never
/// works as a free GPS. Not thread-safe: the session drives it under its lock.
/// </summary>
public sealed class Kneeboard
{
    /// <summary>Full strength for this long, then fading to <see cref="FadedAlpha"/> at <see cref="FadedAfter"/>.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(1), FadedAfter = TimeSpan.FromMinutes(8);

    public const double FadedAlpha = 0.25;

    /// <summary>A contact called this long ago is rubbed out; a mate's report and the player's fix last longer.</summary>
    public static readonly TimeSpan ContactsKept = TimeSpan.FromMinutes(10), MatesKept = TimeSpan.FromMinutes(15), FixKept = TimeSpan.FromMinutes(30);

    /// <summary>A mate's or the player's mark younger than this can anchor another call made relative to it.</summary>
    public static readonly TimeSpan AnchorFor = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Dead reckoning grows its patch by this share of the distance flown since the fix, plus <see cref="DriftPerMinute"/>,
    /// standing for compass error, wind and a rough speed. At 400 km/h the patch grows about 6 km across in three minutes
    /// and 20 km in ten, on top of the fix's own size. SPECULATIVE; to be tuned in a flight.
    /// </summary>
    public const double DriftPerMetre = 0.10, DriftPerMinute = 300;

    /// <summary>Past this radius the dead reckoning is no use and is dropped.</summary>
    public const double LostBeyondMetres = 25_000;

    /// <summary>
    /// The deliberate error of one stretch of dead reckoning: a steady compass error up to this and a speed error up to
    /// <see cref="SpeedErrorShare"/>, seeded by the fix, so the patch's middle is not the truth either.
    /// </summary>
    public const double HeadingErrorDegrees = 6, SpeedErrorShare = 0.08;

    private const double FormationSpread = 1_000;

    private readonly Dictionary<string, PlotMark> _mates = new(StringComparer.Ordinal);
    private readonly List<PlotMark> _contacts = [];
    private readonly List<string> _steers = [];
    private PlotMark? _fix;
    private Reckoning? _reckoning;

    public KneeboardSheet? Sheet { get; private set; }

    /// <summary>A new sortie: a fresh sheet with no marks on it.</summary>
    public void Reset(MissionPlan? plan)
    {
        Sheet = plan is null ? null : KneeboardSheet.For(plan);
        _mates.Clear();
        _contacts.Clear();
        _steers.Clear();
        _fix = null;
        _reckoning = null;
    }

    /// <summary>
    /// Pencils in what one reply said. <paramref name="command"/> is the request it answers (null for an unasked call),
    /// <paramref name="playerHeading"/> the player's own compass when it was said (a clock position is relative to it),
    /// <paramref name="clock"/> the mission time to note against a fix ("12:38").
    /// </summary>
    public void Record(RadioCommand? command, RadioReply reply, double? playerHeading, DateTime utc, string? clock = null)
    {
        if (Sheet is null)
            return;
        var tokens = reply.Tokens ?? [];
        bool own = reply.Contact.IsPlayer;
        if (command is RadioCommand.RoutePosition or RadioCommand.SteerHome or RadioCommand.SteerTarget)
            Steer(reply, clock);

        if (command == RadioCommand.FixPosition && own)
        {
            // "By Tusov airfield, grid 1216-9": the footer notes what he fixed by.
            string by = RadioChat.Line(reply).Text.Split(", grid")[0];
            if (Square(tokens) is { } fix)
                Fix(fix.South, fix.West, fix.Size, PlotShape.Cell, utc, Source(clock, by.Length > 0 ? char.ToLowerInvariant(by[0]) + by[1..] : "own fix"));
            return;
        }
        if (own)
            return; // the player's own map reading ("Route") is not a fix: it would be reading the answer off the game

        if (tokens.Any(t => t.Word is SpeechWord.ContactCount or SpeechWord.ContactAmount or SpeechWord.ContactType))
        {
            Contact(reply, tokens, playerHeading, utc);
            return;
        }

        string who = Who(reply.Contact);
        if (command == RadioCommand.RoutePosition)
        {
            if (Square(tokens) is { } placed)
                Fix(placed.South, placed.West, placed.Size, PlotShape.Cell, utc, Source(clock, who));
            return;
        }
        if (command is RadioCommand.SteerHome or RadioCommand.SteerTarget)
            return;

        Mate(reply, tokens, playerHeading, utc, clock, who);
    }

    /// <summary>
    /// Carries the player's mark on from his last fix by his own heading and speed (his compass and airspeed, read from
    /// memory), with this stretch's steady error. Called with each memory read, about once a second; null instruments
    /// leave the patch where it is, still growing. A gap is bridged for at most 5 s, so a pause does not fling it on.
    /// </summary>
    public void Advance(DateTime utc, double? heading, double? speedMetresPerSecond)
    {
        if (_reckoning is not { } r)
            return;
        if (heading is double h && speedMetresPerSecond is double v)
        {
            double seconds = Math.Clamp((utc - r.LastUtc).TotalSeconds, 0, 5);
            double step = v > 1 ? v * (1 + r.SpeedError) * seconds : 0;
            double course = (h + r.HeadingError) * Math.PI / 180;
            r = r with { North = r.North + step * Math.Cos(course), East = r.East + step * Math.Sin(course), Flown = r.Flown + step, LastUtc = utc };
        }
        _reckoning = RadiusOf(r, utc) > LostBeyondMetres ? null : r;
    }

    /// <summary>What to draw now, or null without a sheet.</summary>
    public KneeboardPicture? Picture(DateTime utc, KneeboardView view)
    {
        if (Sheet is not { } sheet)
            return null;
        Expire(utc);

        var marks = new List<PlotEntry>();
        if (_fix is { } fix)
            marks.Add(Entry(fix, utc));
        foreach (var mate in _mates.Values.Where(m => !m.WithPlayer && OnSheet(sheet, m)))
            marks.Add(Entry(mate, utc));
        foreach (var contact in _contacts.Where(c => OnSheet(sheet, c)))
            marks.Add(Entry(contact, utc));

        PlotEntry? player = Estimate(utc) is { } estimate ? Entry(estimate, utc) : null;
        string? off = player is { } p && !OnSheet(sheet, p.Mark) ? Edge(sheet, p.Mark) : null;
        var with = _mates.Values.Where(m => m.WithPlayer && utc - m.MadeUtc <= AnchorFor).Select(m => m.Label).Order(StringComparer.Ordinal).ToList();

        var footer = new List<string>();
        if (_fix is { Source: { } source })
            footer.Add($"Last fix {source}{(_reckoning is null ? "" : "; dead reckoning since")}");
        footer.AddRange(_steers.TakeLast(2));
        return new KneeboardPicture(view, sheet, marks, player, off, with, footer);
    }

    /// <summary>The player's best place now: the dead-reckoning patch, else the fix itself.</summary>
    private PlotMark? Estimate(DateTime utc)
    {
        if (_reckoning is { } r && RadiusOf(r, utc) <= LostBeyondMetres)
            return new PlotMark(PlotSubject.Player, "you", "DR", PlotShape.Patch, r.North, r.East, RadiusOf(r, utc), r.FixUtc) { DeadReckoned = true };
        return null;
    }

    /// <summary>Where the player is thought to be when a call is made relative to him: centre and radius, or null.</summary>
    private (double North, double East, double Radius)? PlayerAnchor(DateTime utc)
    {
        if (Estimate(utc) is { } dr)
            return dr.Spread;
        return _fix is { } fix && utc - fix.MadeUtc <= AnchorFor ? fix.Spread : null;
    }

    private void Fix(double north, double east, double size, PlotShape shape, DateTime utc, string source)
    {
        _fix = new PlotMark(PlotSubject.Player, "fix", "fix", shape, north, east, size, utc) { Source = source };
        var (n, e, radius) = _fix.Spread;
        int seed = (int)(utc.Ticks / TimeSpan.TicksPerSecond % int.MaxValue);
        _reckoning = new Reckoning(n, e, radius, 0, utc, utc,
            (Unit(seed, 1) * 2 - 1) * HeadingErrorDegrees, (Unit(seed, 2) * 2 - 1) * SpeedErrorShare);
    }

    private void Mate(RadioReply reply, IReadOnlyList<SpeechToken> tokens, double? playerHeading, DateTime utc, string? clock, string who)
    {
        string key = reply.Contact.Key;
        string label = Label(reply);
        bool together = tokens.Any(t => t.Word is SpeechWord.InFormation or SpeechWord.WithUs);
        var known = _mates.GetValueOrDefault(key) is { } before && utc - before.MadeUtc <= AnchorFor && !before.WithPlayer ? before : null;
        if (together)
        {
            if (PlayerAnchor(utc) is { } at)
                _mates[key] = new PlotMark(PlotSubject.Mate, key, label, PlotShape.Patch, at.North, at.East, at.Radius, utc) { WithPlayer = true };
            else if (known is not null)
            {
                // He has you with him, and you knew where he was: you are there too.
                var (n, e, radius) = known.Spread;
                Fix(n, e, radius + FormationSpread, PlotShape.Patch, utc, Source(clock, who));
            }
            return;
        }

        if (Square(tokens) is { } square)
        {
            _mates[key] = new PlotMark(PlotSubject.Mate, key, label, PlotShape.Cell, square.South, square.West, square.Size, utc);
            return;
        }

        if (Direction(tokens, playerHeading, reply.FromSpeaker) is not { } direction)
            return;
        if (PlayerAnchor(utc) is { } from)
            _mates[key] = Offset(PlotSubject.Mate, key, label, from, direction, utc);
        else if (known is not null)
        {
            // He placed himself off you, and you knew where he was: so you are the other way.
            var (n, e, radius) = known.Spread;
            var back = direction with { Bearing = (direction.Bearing + 180) % 360 };
            var you = Offset(PlotSubject.Player, "fix", "fix", (n, e, radius), back, utc);
            Fix(you.North, you.East, you.Size, PlotShape.Patch, utc, Source(clock, who));
        }
    }

    private void Contact(RadioReply reply, IReadOnlyList<SpeechToken> tokens, double? playerHeading, DateTime utc)
    {
        if (reply.Members.Count == 0)
            return;
        (double North, double East, double Radius)? anchor = reply.FromSpeaker ? SpeakerAnchor(reply.Contact, utc) : PlayerAnchor(utc);
        if (anchor is not { } from)
            return;

        var direction = tokens.Any(t => t.Word == SpeechWord.OnMe) ? null : Direction(tokens, playerHeading, reply.FromSpeaker);
        string key = "contact:" + string.Join(",", reply.Members.Select(m => m.Key).Order(StringComparer.Ordinal));
        var mark = direction is { } d
            ? Offset(PlotSubject.Contact, key, ContactLabel(tokens), from, d, utc)
            : new PlotMark(PlotSubject.Contact, key, ContactLabel(tokens), PlotShape.Patch, from.North, from.East,
                from.Radius + (tokens.Any(t => t.Word == SpeechWord.OnMe) || !reply.FromSpeaker ? PilotReport.EngagedMetres : Band(tokens).High), utc);
        var heading = tokens.FirstOrDefault(t => t.Word == SpeechWord.Heading);
        var side = tokens.FirstOrDefault(t => t.Word == SpeechWord.ContactSide);
        mark = mark with
        {
            Course = heading.Word == SpeechWord.Heading ? heading.Value : null,
            LastSeen = tokens.Any(t => t.Word == SpeechWord.LastSeen),
            Side = side.Word == SpeechWord.ContactSide ? Math.Sign(side.Value) : 0,
            Members = reply.Members.Select(m => m.Key).ToList(),
        };
        var members = mark.Members.ToHashSet(StringComparer.Ordinal);
        _contacts.RemoveAll(c => c.Members.Any(members.Contains));
        _contacts.Add(mark);
    }

    /// <summary>A speaker's own place, for "on me" and "my two o'clock": his last mark, or the player's when he flies with him.</summary>
    private (double North, double East, double Radius)? SpeakerAnchor(Contact speaker, DateTime utc)
    {
        if (_mates.GetValueOrDefault(speaker.Key) is not { } mark || utc - mark.MadeUtc > AnchorFor)
            return null;
        if (mark.WithPlayer)
            return PlayerAnchor(utc);
        return mark.Spread;
    }

    /// <summary>A direction and range as said, ready to be laid off an anchor.</summary>
    private readonly record struct Said(double Bearing, double Spread, double Range, double RangeSpread, bool Ring);

    private static Said? Direction(IReadOnlyList<SpeechToken> tokens, double? playerHeading, bool fromSpeaker)
    {
        var band = Band(tokens);
        foreach (var t in tokens)
        {
            switch (t.Word)
            {
                case SpeechWord.Clock when !fromSpeaker && playerHeading is double heading:
                    return new Said((heading + t.Value % 12 * 30 + 360) % 360, 15, band.Middle, band.Half, false);
                case SpeechWord.Clock:
                    return new Said(0, 180, 0, band.High, true); // his nose, or the player's compass, is not known: anywhere round him
                case SpeechWord.Bearing or SpeechWord.Steer:
                    return new Said(t.Value, tokens.Any(x => x.Word == SpeechWord.RangeBand) ? 22.5 : 5, band.Middle, band.Half, false);
            }
        }
        return null;
    }

    /// <summary>The range as said: an exact figure (<see cref="RadioRealism.Precise"/>) or close / a few kilometres / far.</summary>
    private static (double Middle, double Half, double High) Band(IReadOnlyList<SpeechToken> tokens)
    {
        if (tokens.FirstOrDefault(t => t.Word == SpeechWord.Range) is { Word: SpeechWord.Range } exact)
        {
            double half = Math.Max(300, exact.Value * 0.1);
            return (exact.Value, half, exact.Value + half);
        }
        return tokens.FirstOrDefault(t => t.Word == SpeechWord.RangeBand).Value switch
        {
            0 => (PilotReport.CloseMetres / 2, PilotReport.CloseMetres / 2, PilotReport.CloseMetres),
            1 => ((PilotReport.CloseMetres + PilotReport.FarBeyondMetres) / 2, (PilotReport.FarBeyondMetres - PilotReport.CloseMetres) / 2, PilotReport.FarBeyondMetres),
            _ => (12_000, 4_000, 16_000),
        };
    }

    private static PlotMark Offset(PlotSubject subject, string key, string label, (double North, double East, double Radius) from, Said said, DateTime utc)
    {
        if (said.Ring)
            return new PlotMark(subject, key, label, PlotShape.Patch, from.North, from.East, from.Radius + said.Range + said.RangeSpread, utc);
        double b = said.Bearing * Math.PI / 180;
        double radius = from.Radius + said.RangeSpread + said.Range * Math.Sin(said.Spread * Math.PI / 180);
        return new PlotMark(subject, key, label, PlotShape.Patch, from.North + said.Range * Math.Cos(b), from.East + said.Range * Math.Sin(b), radius, utc);
    }

    private static (double South, double West, double Size)? Square(IReadOnlyList<SpeechToken> tokens) =>
        tokens.FirstOrDefault(t => t.Word == SpeechWord.Grid) is { Word: SpeechWord.Grid } grid
            ? MapGrid.Bounds(MapGrid.FromCode((int)grid.Value, grid.Name))
            : null;

    private void Steer(RadioReply reply, string? clock)
    {
        string text = RadioChat.Line(reply).Text.TrimEnd('.');
        _steers.Add(clock is null ? text : $"{clock} {text}");
        if (_steers.Count > 4)
            _steers.RemoveAt(0);
    }

    private void Expire(DateTime utc)
    {
        _contacts.RemoveAll(c => utc - c.MadeUtc > ContactsKept);
        foreach (var key in _mates.Where(m => utc - m.Value.MadeUtc > MatesKept).Select(m => m.Key).ToList())
            _mates.Remove(key);
        if (_fix is { } fix && utc - fix.MadeUtc > FixKept)
            _fix = null;
    }

    private static PlotEntry Entry(PlotMark mark, DateTime utc)
    {
        var age = utc - mark.MadeUtc;
        double t = Math.Clamp((age - FreshFor) / (FadedAfter - FreshFor), 0, 1);
        return new PlotEntry(mark, age, 1 - t * (1 - FadedAlpha));
    }

    private static bool OnSheet(KneeboardSheet sheet, PlotMark mark)
    {
        var (n, e, _) = mark.Spread;
        return sheet.Contains(n, e);
    }

    /// <summary>The sheet's edge nearest a place off it: "north", "south-east".</summary>
    private static string Edge(KneeboardSheet sheet, PlotMark mark)
    {
        var (n, e, _) = mark.Spread;
        string ns = n > sheet.North ? "north" : n < sheet.South ? "south" : "";
        string ew = e > sheet.East ? "east" : e < sheet.West ? "west" : "";
        return ns.Length > 0 && ew.Length > 0 ? $"{ns}-{ew}" : ns + ew;
    }

    private static double RadiusOf(Reckoning r, DateTime utc) =>
        r.FixRadius + DriftPerMetre * r.Flown + DriftPerMinute * (utc - r.FixUtc).TotalMinutes;

    private static string Source(string? clock, string who) => clock is null ? who : $"{clock}, {who}";

    /// <summary>"Lumme 1" for a speaker with a callsign, "Number 4" in the player's flight without one, else his name.</summary>
    private static string Who(Contact c) =>
        RadioPhrases.CallNumber(c) is int n ? c.CallsignName is { } word ? $"{word} {n}" : $"Number {n}" : c.PilotName;

    /// <summary>A mate's mark: his number in the player's flight ("4"), else his callsign; "+2" for the others with him.</summary>
    private static string Label(RadioReply reply)
    {
        var c = reply.Contact;
        string who = c.InPlayerFlight && RadioPhrases.CallNumber(c) is int n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : Who(c);
        int others = reply.Members.Count - 1;
        return others > 0 ? $"{who} +{others}" : who;
    }

    /// <summary>"2 Hurricanes", "many bombers", "fighter?", "a few aircraft": what the call named, no more.</summary>
    private static string ContactLabel(IReadOnlyList<SpeechToken> tokens)
    {
        var count = tokens.FirstOrDefault(t => t.Word == SpeechWord.ContactCount);
        var amount = tokens.FirstOrDefault(t => t.Word == SpeechWord.ContactAmount);
        var type = tokens.FirstOrDefault(t => t.Word == SpeechWord.ContactType && t.Name is not null);
        var cls = tokens.FirstOrDefault(t => t.Word == SpeechWord.ContactClass);
        int n = count.Word == SpeechWord.ContactCount ? (int)count.Value : type.Word == SpeechWord.ContactType ? (int)type.Value : 0;
        bool plural = amount.Word == SpeechWord.ContactAmount || n > 1;
        string what = type.Name is { } family
            ? plural ? AircraftTypes.ByFamily(family)?.Plural ?? family + "s" : family
            : cls.Word == SpeechWord.ContactClass ? AircraftTypes.ClassName((AircraftClass)(int)Math.Abs(cls.Value), plural)
            : plural ? "aircraft" : "aircraft";
        string how = amount.Word == SpeechWord.ContactAmount ? amount.Value >= 4 ? "many " : "a few "
            : n > 1 ? $"{n} " : "";
        string hedge = tokens.Any(t => t.Word == SpeechWord.Hedge) ? "?" : "";
        return how + what + hedge;
    }

    /// <summary>A deterministic number in [0, 1) for one seed and slot.</summary>
    private static double Unit(int seed, int slot)
    {
        unchecked
        {
            uint h = (uint)seed * 2654435761u ^ (uint)slot * 40503u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (h & 0xFFFFFF) / (double)0x1000000;
        }
    }

    private sealed record Reckoning(double North, double East, double FixRadius, double Flown, DateTime FixUtc, DateTime LastUtc, double HeadingError, double SpeedError);
}
