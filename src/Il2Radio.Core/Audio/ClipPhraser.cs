using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Audio;

/// <summary>
/// Turns a reply's <see cref="SpeechToken"/>s into a sequence of clip keys a voice pack can play.
/// Each token tries its most precise key first and falls back to coarser ones ("range.18", then
/// "range.far"). Only recordings in the pack are ever played: a part the pack cannot say is left out
/// of the audio and stays on the chat, which always carries the whole sentence. Nothing is required,
/// so a gap never silences the rest of the transmission and never hands it to another voice.
/// </summary>
/// <remarks>
/// Clip keys:
/// <c>callsign.&lt;IL-2 callsign number&gt;</c> (8 = "Oriole"), <c>number.1</c>–<c>number.20</c>,
/// <c>group</c>, <c>pair</c>, <c>separated</c>,
/// <c>clock.1</c>–<c>clock.12</c>, <c>compass.n|ne|e|se|s|sw|w|nw</c>, <c>steer</c>,
/// <c>range.close</c>, <c>range.1</c>, <c>range.2</c>, ... (kilometres), <c>range.far</c>, <c>range.few</c>,
/// <c>vertical.above|below|level</c>, <c>altitude</c> + <c>altitude.&lt;metres, nearest 100&gt;</c>,
/// <c>heading</c>, <c>information</c>, <c>withus</c>,
/// <c>class.fighter|fighters|attack|attacks|bomber|bombers|recon|recons|transport</c>,
/// <c>nocontacts</c>, the optional <c>contact.type</c>, <c>side.enemy|friendly</c>, <c>hedge</c>,
/// <c>few</c>, <c>many</c>, <c>onme</c>, <c>cannotsee</c>, <c>lastseen</c>, <c>grid</c>,
/// <c>turn.left|right</c>, <c>destroyed</c>, <c>ack</c>, <c>beside.left|right|behind</c>, <c>spotted</c>,
/// <c>engaging</c>, <c>followme</c> and <c>rejoin</c>.
/// <para>
/// Placeholders: where a whole message has no recording of its own, a nearby game phrase stands in
/// rather than leave the speaker saying only his callsign. "No contacts", "with us" and "in formation"
/// fall back to the acknowledgement (<c>ack</c>, the actors' "Roger"), except that a mate in formation beside or
/// behind the player says where he is ("to the left"); "a few" and "many" to
/// <c>group</c>. A course is said as the eight-point compass clip, after "turning left/right" when the
/// leader is about to turn. The three-figure course stays on the chat: the clips have 1–20 and no zero.
/// A contact's direction of flight (<see cref="SpeechWord.Heading"/>) is the compass clip too.
/// </para>
/// <para>
/// Chat only, with no recording to say them: <see cref="SpeechWord.Leg"/>, <see cref="SpeechWord.OffRoute"/>,
/// <see cref="SpeechWord.Waypoint"/> and <see cref="SpeechWord.Destination"/> (a place name such as "Tusov"); a route
/// or steer reply says the speaker, the compass point of the course and the range.
/// </para>
/// </remarks>
public static class ClipPhraser
{
    /// <summary>Below this a range is spoken as "close" (matches the English "under two kilometres").</summary>
    public const double CloseRangeMetres = 1_500;

    /// <summary>Within this height difference the English text says "level with you".</summary>
    public const double LevelBandMetres = 300;

    private static readonly string[] Compass = ["n", "ne", "e", "se", "s", "sw", "w", "nw"];

    /// <summary>The clip keys to play, in order, or null when the pack can say none of the reply.</summary>
    public static IReadOnlyList<string>? Phrase(IReadOnlyList<SpeechToken> tokens, Func<string, bool> hasClip)
    {
        var keys = new List<string>();

        bool Say(params string?[] candidates)
        {
            if (candidates.FirstOrDefault(k => k is not null && hasClip(k)) is not { } key)
                return false;
            keys.Add(key);
            return true;
        }

        // The class word stands in for the type unless the pack has a recording for the type itself.
        bool typeSpoken = tokens.Any(t => t.Word == SpeechWord.ContactType) && hasClip("contact.type");

        // A mate in formation says where he sits ("to the left") rather than the acknowledgement.
        bool besideSpoken = tokens.Any(t => t.Word == SpeechWord.Beside && BesideKey(t.Value) is { } k && hasClip(k));

        foreach (var t in tokens)
        {
            switch (t.Word)
            {
                case SpeechWord.Callsign when t.Value > 0:
                    Say($"callsign.{(int)t.Value}");
                    break;
                case SpeechWord.Speaker when t.Value > 0:
                    Say($"number.{(int)t.Value}");
                    break;
                case SpeechWord.Group:
                    Say(t.Value == 2 ? "pair" : null, "group");
                    break;
                case SpeechWord.Separated:
                    Say("separated");
                    break;
                case SpeechWord.Clock:
                    Say($"clock.{(int)t.Value}");
                    break;
                case SpeechWord.Bearing:
                    Say(CompassKey(t.Value));
                    break;
                case SpeechWord.Steer:
                    Say("steer");
                    Say(CompassKey(t.Value));
                    break;
                case SpeechWord.Range:
                    Say(RangeKeys(t.Value));
                    break;
                case SpeechWord.Vertical:
                    Say(Math.Abs(t.Value) < LevelBandMetres ? "vertical.level" : t.Value > 0 ? "vertical.above" : "vertical.below");
                    break;
                case SpeechWord.Altitude:
                    string altitude = $"altitude.{Math.Round(t.Value / 100.0) * 100:F0}";
                    if (hasClip(altitude))
                    {
                        Say("altitude");
                        keys.Add(altitude);
                    }
                    break;
                case SpeechWord.Heading:
                case SpeechWord.Course:
                    // An eight-point direction is a game phrase; "heading" in front only when the pack has it.
                    if (hasClip(CompassKey(t.Value)))
                    {
                        Say("heading");
                        keys.Add(CompassKey(t.Value));
                    }
                    break;
                case SpeechWord.Turn:
                    Say(t.Value < 0 ? "turn.left" : "turn.right");
                    break;
                case SpeechWord.InFormation:
                    Say("information", "withus", besideSpoken ? null : "ack");
                    break;
                case SpeechWord.WithUs:
                    Say("withus", "information", besideSpoken ? null : "ack");
                    break;
                case SpeechWord.Beside:
                    Say(BesideKey(t.Value));
                    break;
                case SpeechWord.Spotted:
                    Say("spotted");
                    break;
                case SpeechWord.NoContacts:
                    Say("nocontacts", "ack");
                    break;
                case SpeechWord.ContactCount:
                    Say(t.Value == 2 ? "pair" : t.Value > 2 ? "group" : null);
                    break;
                case SpeechWord.ContactSide:
                    Say(t.Value < 0 ? "side.enemy" : "side.friendly");
                    break;
                case SpeechWord.ContactClass when !typeSpoken:
                    Say(ClassKey(t.Value), ClassKey(-t.Value));
                    break;
                case SpeechWord.Hedge:
                    Say("hedge");
                    break;
                case SpeechWord.ContactType:
                    // The game has no clips naming aircraft types: the class word ("fighters") stands in and
                    // the type stays in the on-screen text. A custom pack can supply "contact.type".
                    Say("contact.type");
                    break;
                case SpeechWord.RangeBand:
                    // "A few kilometres" has no recording, and a kilometre figure would claim more than an eyeball.
                    Say(t.Value <= 0 ? "range.close" : t.Value >= 2 ? "range.far" : "range.few");
                    break;
                case SpeechWord.OnMe:
                    Say("onme");
                    break;
                case SpeechWord.CannotSee:
                    Say("cannotsee");
                    break;
                case SpeechWord.Grid:
                    Say("grid");
                    break;
                case SpeechWord.LastSeen:
                    Say("lastseen");
                    break;
                case SpeechWord.ContactAmount:
                    Say(t.Value >= 4 ? "many" : t.Value >= 3 ? "few" : t.Value == 2 ? "pair" : null, t.Value >= 3 ? "group" : null);
                    break;
                case SpeechWord.Destroyed:
                    Say("destroyed");
                    break;
                case SpeechWord.Engaging:
                    Say("engaging");
                    break;
                case SpeechWord.FollowMe:
                    Say("followme");
                    break;
                case SpeechWord.Rejoin:
                    Say("rejoin");
                    break;
            }
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

    /// <summary>
    /// The close-in word for a mate at this angle off the player's nose: "beside.left" from 30° to 120° left,
    /// "beside.right" the same on the right, "beside.behind" beyond 120°. Ahead has no recording (null).
    /// </summary>
    public static string? BesideKey(double relativeDegrees) =>
        Math.Abs(relativeDegrees) >= 120 ? "beside.behind"
        : relativeDegrees <= -30 ? "beside.left"
        : relativeDegrees >= 30 ? "beside.right"
        : null;

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
