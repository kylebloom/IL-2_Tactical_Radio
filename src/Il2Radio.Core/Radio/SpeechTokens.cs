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

    /// <summary>The group's heading, degrees. An eyeball report; a clip voice says the eight-point compass point.</summary>
    Heading,

    /// <summary>
    /// A course to fly, already rounded by <see cref="Geometry.Navigation.CourseDegrees"/> (5–360, north is 360).
    /// The chat says it in three figures. A clip voice says the eight-point compass point: the pilot clips have
    /// 1–20 and no zero, so they cannot say the figure.
    /// </summary>
    Course,

    /// <summary>The leader is about to turn onto the course that follows: Value negative left, positive right.</summary>
    Turn,

    /// <summary>"Target destroyed": an enemy aircraft the speaker saw go down.</summary>
    Destroyed,

    /// <summary>
    /// Where the speaker sits in formation with the player, relative to the player's nose, degrees (-180..180,
    /// negative left). Said with the actors' close-in words ("to the left", "to the right", "behind"); the chat
    /// keeps "in formation" / "with us".
    /// </summary>
    Beside,

    /// <summary>"Spotted", in front of a contact the flight has just gained. A headset word; the chat sentence is unchanged.</summary>
    Spotted,

    /// <summary>"You are in formation with us."</summary>
    InFormation,

    /// <summary>"You are with us."</summary>
    WithUs,

    /// <summary>Number of aircraft in a reported contact.</summary>
    ContactCount,

    /// <summary>Contact class as <see cref="AircraftClass"/>; negative when plural ("fighters").</summary>
    ContactClass,

    /// <summary>
    /// A named aircraft type: <see cref="SpeechToken.Name"/> is the <see cref="AircraftType.Family"/> called
    /// (null when no type was made out), Value the number of aircraft. The game's recordings have no type names,
    /// so a clip voice says the class word instead.
    /// </summary>
    ContactType,

    /// <summary>"No contacts."</summary>
    NoContacts,

    /// <summary>The contact's side, as far as the observer made out its markings: positive friendly, negative enemy.</summary>
    ContactSide,

    /// <summary>"Possible": the type call that follows is not certain.</summary>
    Hedge,

    /// <summary>
    /// Coarse range, because an eyeball does not count kilometres. 0 = "close" (under 2 km),
    /// 1 = "a few kilometres", 2 = "far". The recordings can say close and far; "a few kilometres"
    /// stays on the chat.
    /// </summary>
    RangeBand,

    /// <summary>The contact is on the speaker, not at a plotted position from the player.</summary>
    OnMe,

    /// <summary>The speaker cannot see the player, so he gives no position relative to him.</summary>
    CannotSee,

    /// <summary>
    /// The speaker's map square. <see cref="SpeechToken.Value"/> is <see cref="Geometry.GridSquare.Code"/>
    /// (row × 100 + column, so 1022 is row 10, column 22). <see cref="SpeechToken.Name"/> is the ninth
    /// ("1"–"9") when the text includes it.
    /// </summary>
    Grid,

    /// <summary>The place that follows is where the contact was last seen, not a look he still holds.</summary>
    LastSeen,

    /// <summary>
    /// A count the pilot could not separate into aircraft: 3 = "a few", 4 = "many".
    /// Used instead of <see cref="ContactCount"/> when the text says those words.
    /// </summary>
    ContactAmount,

    /// <summary>"Engaging": an AI leader leaving his course for the enemy the flight has seen.</summary>
    Engaging,

    /// <summary>"Follow me": the leader's order after he breaks off.</summary>
    FollowMe,

    /// <summary>"Rejoin formation": the flight tells the player, who has run ahead of it, to slow down and close up.</summary>
    Rejoin,
}

/// <summary>
/// One language-neutral element of a radio reply. Tokens carry exact values; each voice decides how
/// precisely it can say them (a clip voice may say "long range" for 18 km because the recordings
/// stop at 10 km; the chat keeps the English text). <see cref="Name"/> carries a word that is not a
/// number, such as the aircraft type called.
/// </summary>
public readonly record struct SpeechToken(SpeechWord Word, double Value = 0, string? Name = null)
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

    public override string ToString() =>
        (Value, Name) switch
        {
            (0, null) => Word.ToString(),
            (_, null) => $"{Word}({Value:0.#})",
            _ => $"{Word}({Value:0.#}, {Name})",
        };
}
