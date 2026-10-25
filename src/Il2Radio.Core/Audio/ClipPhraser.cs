using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Audio;

/// <summary>
/// Turns a reply's <see cref="SpeechToken"/>s into a sequence of clip keys a voice pack can play.
/// Each token tries its most precise key first and falls back to coarser ones ("range.18", then
/// "range.far"). Tokens that carry the message (direction, range, "with us") are required: if a pack
/// cannot say them, the reply is not phrased at all and the caller falls back to text-to-speech.
/// Decorations (callsign, speaker number, group size, above/below, altitude, heading) are skipped when missing.
/// </summary>
/// <remarks>
/// Clip keys:
/// <c>callsign.&lt;IL-2 callsign number&gt;</c> (8 = "Oriole"), <c>number.1</c>–<c>number.20</c>,
/// <c>group</c>, <c>pair</c>, <c>separated</c>,
/// <c>clock.1</c>–<c>clock.12</c>, <c>compass.n|ne|e|se|s|sw|w|nw</c>, <c>steer</c>,
/// <c>range.close</c>, <c>range.1</c>, <c>range.2</c>, ... (kilometres), <c>range.far</c>,
/// <c>vertical.above|below|level</c>, <c>altitude</c> + <c>altitude.&lt;metres, nearest 100&gt;</c>,
/// <c>heading</c>, <c>information</c>, <c>withus</c>,
/// <c>class.fighter|fighters|attack|attacks|bomber|bombers|recon|recons|transport</c>,
/// <c>nocontacts</c> (no game clip; that reply uses text-to-speech) and the optional <c>contact.type</c>.
/// </remarks>
public static class ClipPhraser
{
    /// <summary>Below this a range is spoken as "close" (matches the English "under two kilometres").</summary>
    public const double CloseRangeMetres = 1_500;

    /// <summary>Within this height difference the English text says "level with you".</summary>
    public const double LevelBandMetres = 300;

    private static readonly string[] Compass = ["n", "ne", "e", "se", "s", "sw", "w", "nw"];

    /// <summary>The clip keys to play, in order, or null when a required token has no clip.</summary>
    public static IReadOnlyList<string>? Phrase(IReadOnlyList<SpeechToken> tokens, Func<string, bool> hasClip)
    {
        var keys = new List<string>();

        void Optional(params string?[] candidates)
        {
            if (candidates.FirstOrDefault(k => k is not null && hasClip(k)) is { } key)
                keys.Add(key);
        }

        bool Required(params string?[] candidates)
        {
            int before = keys.Count;
            Optional(candidates);
            return keys.Count > before;
        }

        foreach (var t in tokens)
        {
            bool ok = true;
            switch (t.Word)
            {
                case SpeechWord.Callsign when t.Value > 0:
                    Optional($"callsign.{(int)t.Value}");
                    break;
                case SpeechWord.Speaker when t.Value > 0:
                    Optional($"number.{(int)t.Value}");
                    break;
                case SpeechWord.Group:
                    Optional(t.Value == 2 ? "pair" : null, "group");
                    break;
                case SpeechWord.Separated:
                    Optional("separated");
                    break;
                case SpeechWord.Clock:
                    ok = Required($"clock.{(int)t.Value}");
                    break;
                case SpeechWord.Bearing:
                    ok = Required(CompassKey(t.Value));
                    break;
                case SpeechWord.Steer:
                    Optional("steer");
                    ok = Required(CompassKey(t.Value));
                    break;
                case SpeechWord.Range:
                    ok = Required(RangeKeys(t.Value));
                    break;
                case SpeechWord.Vertical:
                    Optional(Math.Abs(t.Value) < LevelBandMetres ? "vertical.level" : t.Value > 0 ? "vertical.above" : "vertical.below");
                    break;
                case SpeechWord.Altitude:
                    string altitude = $"altitude.{Math.Round(t.Value / 100.0) * 100:F0}";
                    if (hasClip(altitude))
                    {
                        Optional("altitude");
                        keys.Add(altitude);
                    }
                    break;
                case SpeechWord.Heading:
                    if (hasClip("heading") && hasClip(CompassKey(t.Value)))
                        keys.AddRange(["heading", CompassKey(t.Value)]);
                    break;
                case SpeechWord.InFormation:
                    ok = Required("information", "withus");
                    break;
                case SpeechWord.WithUs:
                    ok = Required("withus", "information");
                    break;
                case SpeechWord.ContactCount:
                    Optional(t.Value == 2 ? "pair" : t.Value > 2 ? "group" : null);
                    break;
                case SpeechWord.ContactClass:
                    Optional(ClassKey(t.Value), ClassKey(-t.Value));
                    break;
                case SpeechWord.ContactType:
                    // The game has no clips naming aircraft types: the class word ("fighters") stands in,
                    // and the type stays in the on-screen text. A custom pack can supply "contact.type".
                    Optional("contact.type");
                    break;
                case SpeechWord.NoContacts:
                    ok = Required("nocontacts");
                    break;
            }

            if (!ok)
                return null;
        }

        return keys.Count > 0 ? keys : null;
    }

    /// <summary>"class.fighter" / "class.fighters" (negative value = plural) for an <see cref="AircraftClass"/>.</summary>
    public static string? ClassKey(double value)
    {
        string? name = (AircraftClass)(int)Math.Abs(value) switch
        {
            AircraftClass.Fighter => "fighter",
            AircraftClass.Attack => "attack",
            AircraftClass.Bomber => "bomber",
            AircraftClass.Recon => "recon",
            AircraftClass.Transport => "transport",
            _ => null,
        };
        return name is null ? null : value < 0 && name != "transport" ? $"class.{name}s" : $"class.{name}";
    }

    /// <summary>Eight-point compass key for a true bearing: 110° → "compass.e".</summary>
    public static string CompassKey(double degrees)
    {
        int index = (int)Math.Round((((degrees % 360) + 360) % 360) / 45.0) % 8;
        return $"compass.{Compass[index]}";
    }

    /// <summary>Range candidates, most precise first: "range.close" under 1.5 km, else whole kilometres, then "range.far" beyond 10 km.</summary>
    public static string?[] RangeKeys(double metres)
    {
        if (metres < CloseRangeMetres)
            return ["range.close", "range.1"];
        int km = (int)Math.Round(metres / 1000.0);
        return [$"range.{km}", km > 10 ? "range.far" : null];
    }
}
