using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;

namespace Il2Radio.Core.Radio;

/// <summary>
/// A flight mate who saw it calls a loss: one of the flight down, or an enemy going down near him.
/// IL-2's own pilots already cover some losses, and then this stays quiet (UNVERIFIED in a flight):
/// a kill by an AI flight mate (the game's "target destroyed", <c>rep_trgdestr</c>), the player's own
/// kill, and a flight mate shot down by an enemy whose pilot lives to call "I'm hit" or "going down"
/// (<c>rep_imhit</c>, <c>rep_falling</c>). The German recordings have no clip for someone else's
/// loss, so a collision, a crash or a killed pilot goes unsaid in the game.
/// </summary>
/// <remarks>
/// The kill reaches the app with the next mission-log chunk, up to about 30 s late, so the witness
/// is chosen from where everyone was over the last <see cref="MemorySeconds"/>: a living flight mate,
/// on the frequency now, who came within <see cref="WitnessMetres"/> of the lost aircraft. He names
/// an enemy's type inside type-identification range, and otherwise says "enemy fighter". The call
/// is chat text: the recordings cannot say it. Losses already in the log when the session first looks
/// are never called.
/// </remarks>
internal sealed class LossCalls
{
    /// <summary>A flight mate this close to the lost aircraft could have seen it go down.</summary>
    public const double WitnessMetres = 8_000;

    /// <summary>Positions are kept this long (track time), which covers a late log chunk.</summary>
    public const double MemorySeconds = 60;

    private const double SampleEverySeconds = 2;

    private readonly List<(double Time, Dictionary<string, WorldPoint> Positions)> _samples = [];
    private readonly HashSet<string> _handled = [];
    private bool _seeded;

    public void Reset()
    {
        _samples.Clear();
        _handled.Clear();
        _seeded = false;
    }

    /// <summary>Remembers where every tracked aircraft is now. Call on every look, before <see cref="Call"/>.</summary>
    public void Sample(ContactTracker tracker)
    {
        if (!_seeded)
        {
            _seeded = true;
            foreach (var c in tracker.Contacts.Where(c => c.Loss is not null))
                _handled.Add(c.Key);
        }

        var tracked = tracker.Contacts
            .Where(c => c.Loss is null && c.LastFix is { IsTrack: true })
            .ToList();
        if (tracked.Count == 0)
            return;

        double now = tracked.Max(c => c.LastFix!.SourceTime);
        if (_samples.Count > 0 && now < _samples[^1].Time)
            _samples.Clear(); // a new track file starts its clock again
        if (_samples.Count > 0 && now - _samples[^1].Time < SampleEverySeconds)
            return;

        _samples.Add((now, tracked.ToDictionary(c => c.Key, c => c.LastFix!.Position)));
        _samples.RemoveAll(s => now - s.Time > MemorySeconds);
    }

    /// <summary>The losses one witness saw, in one line, or null. Losses nobody in the flight saw are dropped.</summary>
    public RadioReply? Call(ContactTracker tracker, RadioPicture flight, RadioSettings settings)
    {
        var fresh = tracker.Contacts
            .Where(c => c.Loss is not null && !c.IsPlayer && !_handled.Contains(c.Key))
            .Where(c => IsMate(flight.Player, c) || IsEnemy(flight.Player, c))
            .ToList();
        if (fresh.Count == 0)
            return null;

        var lost = new List<Contact>();
        foreach (var c in fresh)
        {
            _handled.Add(c.Key);
            if (!GameSaysIt(c, flight.Player))
                lost.Add(c);
        }

        // The mate who saw the most of them says them all in one transmission (a collision is two
        // losses); losses only someone else saw go out on the next look.
        var seenBy = flight.Live
            .Where(l => l.Contact.Loss is null)
            .Select(mate => (Mate: mate, Seen: lost.Select(c => (Lost: c, Metres: Closest(c, mate)))
                .Where(s => s.Metres <= WitnessMetres).ToList()))
            .Where(w => w.Seen.Count > 0)
            .OrderByDescending(w => w.Seen.Count)
            .ThenBy(w => w.Seen.Sum(s => s.Metres))
            .ToList();
        if (seenBy.Count == 0)
            return null;

        var (speaker, said) = seenBy[0];
        foreach (var other in lost.Where(c => said.All(s => s.Lost != c) && seenBy.Any(w => w.Seen.Any(s => s.Lost == c))))
            _handled.Remove(other.Key);

        string parts = string.Join(" ", said
            .OrderBy(s => IsMate(flight.Player, s.Lost) ? 0 : 1)
            .Select(s => Sentence(s.Lost, s.Metres, flight.Player, settings)));
        // Enemies only: the headset says the actors' "target destroyed". A mate's loss has no recording
        // (the pilots' own "I'm going down" is first person), so that line stays on the chat alone.
        IReadOnlyList<SpeechToken>? tokens = said.All(s => IsEnemy(flight.Player, s.Lost))
            ? [.. SpeechToken.Identify(speaker.Contact), new SpeechToken(SpeechWord.Destroyed)]
            : null;
        return new RadioReply(speaker.Contact, [speaker.Contact], speaker.Relative, speaker.AgeSeconds,
            $"{RadioPhrases.Who(speaker.Contact)}, {parts}", tokens);
    }

    /// <summary>Whether one of IL-2's own pilots announces this loss. Inferred from the log, not heard.</summary>
    internal static bool GameSaysIt(Contact lost, Contact player)
    {
        var attacker = lost.Loss!.Attacker;
        if (IsEnemy(player, lost))
            return attacker is not null && (attacker.IsPlayer || IsMate(player, attacker));
        return attacker is not null && IsEnemy(player, attacker) && !lost.Loss.PilotKilled;
    }

    /// <summary>How close the mate came to the lost aircraft in the remembered positions; infinity when never seen together.</summary>
    private double Closest(Contact lost, LiveContact mate)
    {
        double best = double.PositiveInfinity;
        foreach (var (_, positions) in _samples)
        {
            if (positions.TryGetValue(lost.Key, out var a) && positions.TryGetValue(mate.Contact.Key, out var b)
                && Navigation.Relative(a, b) is { } rel)
                best = Math.Min(best, rel.RangeMetres);
        }

        return best;
    }

    private static string Sentence(Contact lost, double metres, Contact player, RadioSettings settings)
    {
        if (IsMate(player, lost))
            return lost.Loss!.BailedOut && !lost.Loss.PilotKilled ? $"{RadioPhrases.Who(lost)} bailed out." : $"{RadioPhrases.Who(lost)} is down.";

        var type = AircraftTypes.Classify(lost.TypeName);
        bool named = type != AircraftType.Unknown && metres <= settings.Visibility.TypeMetresPerSpanMetre * type.WingspanMetres;
        string what = named ? type.Family
            : type.Class == AircraftClass.Unknown ? "enemy aircraft"
            : $"enemy {AircraftTypes.ClassName(type.Class, plural: false)}";
        return $"{char.ToUpperInvariant(what[0])}{what[1..]} down.";
    }

    private static bool IsMate(Contact player, Contact c) => RadioPicture.IsFlightMate(player, c) && !c.IsPlayer;

    private static bool IsEnemy(Contact player, Contact c) => c.Coalition >= 0 && player.Coalition >= 0 && c.Coalition != player.Coalition;
}
