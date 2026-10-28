namespace Il2Radio.Core.Radio;

/// <summary>
/// What the player reads on the Help tab: what the radio is for, and when each call is the right one.
/// Written for any nation's pilot. The window fills in the key names from the current bindings.
/// <see cref="Call.Action"/> and <see cref="Utility.Action"/> are the <c>keys.*</c> names in settings.
/// </summary>
public static class RadioGuide
{
    public static IReadOnlyList<string> Purpose { get; } =
    [
        "IL-2 Tactical Radio is a companion for single-player in IL-2 Sturmovik: Great Battles. From the cockpit you call other aircraft, and pilots on your side answer with where friendlies are, where your own flight is, and what they can see.",
        "It is the same tool whichever air force you fly. A speaker uses that nation's pilot recordings from your IL-2 install — German, Russian or English — and the same words appear on screen.",
        "The program runs beside the game. It only reads: the mission log, the mission file, aircraft positions from the game's memory, and a Tacview track when one is recorded. It does not change the game, write to its memory, or inject anything into it.",
        "A reply is only what a pilot on your side could actually say. Positions come from a fresh track, not from the briefing. Destroyed aircraft do not answer. Other aircraft are mentioned only once someone could plausibly have seen them, and only as clearly as that look allows. If nobody can answer, the radio says why instead of guessing.",
    ];

    /// <summary>One radio call: which command, the settings key name, and when the player should use it.</summary>
    public readonly record struct Call(RadioCommand Command, string Action, string When);

    /// <summary>One entry per <see cref="RadioCommand"/>, in the order the player meets them.</summary>
    public static IReadOnlyList<Call> Calls { get; } =
    [
        new(RadioCommand.FriendlyPositions, "friendlyPositions",
            "You want every friendly who can hear you, not only the aircraft you took off with. Use it when the formation has split, when you are looking for other flights on the same task, or when you want a position check of friendlies within radio range. Aircraft flying together answer as one group, through the lowest formation number. A pilot who can see you gives a clock position. One who can hear you but cannot see you gives his own heading and altitude, and does not invent where you are."),
        new(RadioCommand.FlightPosition, "flightPosition",
            "You only want your own flight, the aircraft that launched with you. Use it when some of them are out of sight, or when you do not want every other friendly in range to answer as well. If you are already in formation, they say so."),
        new(RadioCommand.Rejoin, "rejoin",
            "You have lost your flight and need to get back to it. The main part of your flight answers. If those pilots can see you, they say where they are and add a heading and altitude. If they cannot see you, they still give heading and altitude rather than guessing a clock position. If you are already with them, they say so. Use this when you are separated. While you are still with the formation and only want a position, use Flight position."),
        new(RadioCommand.EnemyContacts, "enemyContacts",
            "You want the hostile or still-unknown aircraft your side can see, or to hear that the pilots who can answer have nothing in sight. This is not radar. Your own flight reports, and only what those pilots could plausibly see. If you are flying alone, any friendly in radio range reports instead. Use it when you suspect other aircraft, once a fight starts, or to ask whether anyone has a contact. A distant speck stays unidentified; side and type are named only from closer in. A contact that has just passed out of sight can still be reported for about a minute, as where it was last seen."),
        new(RadioCommand.IdentifyContact, "identifyContact",
            "You want the nearest aircraft anyone can see, called in more detail than the contacts picture: shape, side or type, as far as the look allows, and which way it is going when that can be made out. It may be hostile, friendly or still unknown. Use this when one contact is the one you care about. For the whole picture, use Enemy contacts."),
    ];

    /// <summary>A key that is not a radio call. <see cref="Action"/> matches the settings key names.</summary>
    public readonly record struct Utility(string Action, string Title, string When);

    public static IReadOnlyList<Utility> OtherKeys { get; } =
    [
        new("menu", "Radio menu",
            "Opens the radio menu in the game: every call, grouped as Flight, Enemy and Radio log. Choose with numpad 1 to 5; while the menu is open those keys pick from it instead of making their usual call. The menu key steps back a level and closes the menu at the top, and the quit key closes it too. It closes on its own after a few seconds without a key."),
        new("older", "Earlier messages",
            "Pages back through the radio log. Each line is stamped with the mission time, the same time as the watch in the cockpit."),
        new("newer", "Later messages",
            "Pages forward through the radio log. From the newest page, this returns to the live lines."),
        new("quit", "Quit",
            "Closes the radio and writes a session report beside the program. Closing this window does the same."),
    ];

    public static IReadOnlyList<string> Flying { get; } =
    [
        "Leave this window open while you fly. The keys work while IL-2 has focus, and the game still receives them, so choose keys the game does not use. Num Lock must be on for the numpad.",
        "Live positions are read from IL-2's memory a few seconds after you are in the air; the program only reads, and changes nothing in the game. No track recording is needed. If memory cannot be read, or Positions on the Settings tab is set to track, press LCtrl+R in the cockpit so IL-2 records a track, which is then read instead; that needs this program to run as administrator. With neither, pilots cannot give a current position, and the radio says so.",
        "On the Stalingrad map, a pilot who cannot see you may also name the map square he is in. On other maps he still gives heading and altitude. Your own flight names a square only while it is still on its briefed route.",
        "Realism, on the Settings tab, chooses how much a pilot claims. Pilot, the default, limits him to what he could know. Precise gives bearings and ranges taken from the track. The three unasked-call options let an AI leader name a new course once he starts a turn, say \"Follow me\" when he turns off his course (\"engaging\" when the flight has seen the enemy or he has turned onto one), tell you to slow down and rejoin when a mate sees you running ahead of the flight, and tell you with his heading and altitude when they have lost sight of you; let someone call when the contact picture changes, and let a flight mate who saw it report a loss, without you pressing a key. When you are leading, that course stays on screen and is not spoken. A loss is reported on screen, and only when the game's own pilots did not report it: their own kills, and a mate who calls that he is hit, are left to the game.",
        "Each line on screen starts with the time, then the speaker's callsign and name, so the message itself does not repeat his callsign. Your own calls are shown the same way.",
        "Each air force's recordings cover the common words, such as numbers, a clock position and a range. Words a recording set does not contain stay on screen, or are read by Windows. An optional extra voice can speak some of those missing words for German speakers. Soviet, British and American speakers are unchanged, and the radio works without that extra voice. It is on the Settings tab.",
    ];
}
