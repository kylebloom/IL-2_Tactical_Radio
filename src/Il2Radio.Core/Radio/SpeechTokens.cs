using Il2Radio.Core.Contacts;

namespace Il2Radio.Core.Radio;

/// <summary>What one part of a reply says. The quantity lives in <see cref="SpeechToken.Value"/>.</summary>
public enum SpeechWord
{
    /// <summary>The speaker's number: within his callsign, or in the player's flight; 0 when he has none.</summary>
    Speaker,

    /// <summary>The speaker's callsign number from the mission plan (8 = "Oriole"); only when the text uses it.</summary>
    Callsign,

    /// <summary>The speaker reports for a group of Value aircraft, himself included.</summary>
    Group,

    /// <summary>The speaker is separated from the main group, with Value others.</summary>
    Separated,

    /// <summary>Clock position relative to the player's heading, 1–12.</summary>
    Clock,

    /// <summary>True bearing from the player, degrees.</summary>
    Bearing,

    /// <summary>True course for the player to steer, degrees.</summary>
    Steer,

    /// <summary>Range from the player, metres.</summary>
    Range,

    /// <summary>Height relative to the player, metres (positive: above the player).</summary>
    Vertical,

    /// <summary>Altitude, metres.</summary>
    Altitude,

    /// <summary>The group's heading, degrees.</summary>
    Heading,

    /// <summary>"You are in formation with us."</summary>
    InFormation,

    /// <summary>"You are with us."</summary>
    WithUs,

    /// <summary>Number of aircraft in a reported contact.</summary>
    ContactCount,

    /// <summary>Contact class as <see cref="AircraftClass"/>; negative when plural ("fighters").</summary>
    ContactClass,

    /// <summary>A named aircraft type or identification verdict; recorded voices have no clips for it.</summary>
    ContactType,

    /// <summary>"No contacts."</summary>
    NoContacts,
}

/// <summary>
/// One language-neutral element of a radio reply. Tokens carry exact values; each voice decides how
/// precisely it can say them (Windows TTS speaks the English text; a clip voice may say "long range"
/// for 18 km because the recordings stop at 10 km).
/// </summary>
public readonly record struct SpeechToken(SpeechWord Word, double Value = 0)
{
    /// <summary>How the speaker identifies himself, matching <c>RadioPhrases.Who</c>: callsign (if used) and number.</summary>
    public static IEnumerable<SpeechToken> Identify(Contact c)
    {
        bool callsign = c.CallsignName is not null && RadioPhrases.CallNumber(c) is not null;
        if (callsign)
            yield return new(SpeechWord.Callsign, c.Callsign);
        int? number = callsign ? RadioPhrases.CallNumber(c) : c.InPlayerFlight ? c.FormationIndex : null;
        yield return new(SpeechWord.Speaker, number ?? 0);
    }

    public override string ToString() => Value == 0 ? Word.ToString() : $"{Word}({Value:0.#})";
}
