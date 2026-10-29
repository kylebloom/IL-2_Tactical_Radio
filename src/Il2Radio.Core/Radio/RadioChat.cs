using Il2Radio.Core.Contacts;

namespace Il2Radio.Core.Radio;

public enum ChatLineKind
{
    Request, // the player's outgoing call
    Reply,   // a friendly answering
    System,  // the tool explaining a silence; not radio traffic
    Info,    // the tool's command menu; not radio traffic, but meant to be read
}

/// <summary>
/// One line of the on-screen radio log. <see cref="Delay"/> is measured from the moment the request
/// is keyed, so replies can appear roughly as they are spoken rather than all at once.
/// </summary>
public sealed record ChatLine(ChatLineKind Kind, string Speaker, string Text, int Coalition, TimeSpan Delay)
{
    /// <summary>The transmission as the voice says it, when the chat shows it shorter. The voice is matched on this.</summary>
    public string? Spoken { get; init; }
}

/// <summary>
/// Turns a radio exchange into text lines in the style of IL-2's chat ("Speaker: message"), for an
/// overlay or a second-screen window. The speaker is his callsign and name, each in brackets
/// ("[Lumme 1] [Viktor Richter]"); the chat then leaves out the callsign he says at the start of
/// <see cref="RadioReply.Text"/>. The chat output puts the bracketed time in front.
/// </summary>
public static class RadioChat
{
    /// <summary>Roughly when the first reply starts after keying: squelch plus a moment to answer.</summary>
    public static readonly TimeSpan FirstReplyDelay = TimeSpan.FromSeconds(1.2);

    /// <summary>What the player says on the radio for each command.</summary>
    public static string RequestPhrase(RadioCommand command) => command switch
    {
        RadioCommand.FriendlyPositions => "All friendlies, report positions.",
        RadioCommand.FlightPosition => "Flight, report your position.",
        RadioCommand.Rejoin => "Flight, I am separated. Request rejoin.",
        RadioCommand.EnemyContacts => "Flight, report contacts.",
        RadioCommand.IdentifyContact => "Flight, identify nearest enemy.",
        RadioCommand.RoutePosition => "Flight, where are we on the route?",
        RadioCommand.SteerHome => "Flight, request steer home.",
        RadioCommand.SteerTarget => "Flight, request steer to the target.",
        RadioCommand.FixPosition => "Checking the ground against the map.",
        _ => RadioCommands.Title(command) + ".",
    };

    /// <summary>
    /// Speech-length estimate for spacing replies: the TTS voice runs at roughly 14 characters a
    /// second, plus squelch and tail. Approximate only; the voice does not report when it starts a line.
    /// </summary>
    public static TimeSpan EstimatedAirtime(string text) => TimeSpan.FromSeconds(0.9 + text.Length / 14.0);

    /// <summary>The whole exchange as one batch: the player's call, then the replies after <see cref="FirstReplyDelay"/>.</summary>
    public static IReadOnlyList<ChatLine> Lines(RadioCommand command, RadioResult result, Contact? player, string? problem = null) =>
        [RequestLine(command, player), .. Replies(result, problem, FirstReplyDelay)];

    /// <summary>One reply as a chat line. <see cref="RadioReply.SpeakerLabel"/> overrides the callsign and name.</summary>
    public static ChatLine Line(RadioReply reply, TimeSpan delay = default)
    {
        string speaker = reply.SpeakerLabel is { } label ? Bracket(label) : Speaker(reply.Contact);
        string text = WithoutOwnCall(reply.Text, reply.SpeakerLabel ?? RadioPhrases.Who(reply.Contact));
        return new ChatLine(ChatLineKind.Reply, speaker, text, reply.Contact.Coalition, delay) { Spoken = text == reply.Text ? null : reply.Text };
    }

    /// <summary>The player's call, shown the moment the key is pressed, before any answer is known.</summary>
    public static ChatLine RequestLine(RadioCommand command, Contact? player) =>
        new(ChatLineKind.Request, player is null ? Bracket("You") : Speaker(player), RequestPhrase(command), player?.Coalition ?? -1, TimeSpan.Zero);

    /// <summary>
    /// "[Lumme 1] [Viktor Richter]": the callsign with its number in digits, then the name, each in
    /// brackets. Without a callsign, "[Number 1]" in the player's flight; otherwise the name alone.
    /// </summary>
    public static string Speaker(Contact c)
    {
        string name = c.PilotName.Length > 0 ? c.PilotName : c.DisplayName;
        string? call = RadioPhrases.CallNumber(c) is int n
            ? c.CallsignName is { } word ? $"{word} {n}" : c.InPlayerFlight ? $"Number {n}" : null
            : null;
        return call is null ? Bracket(name) : $"{Bracket(call)} {Bracket(name)}";
    }

    private static string Bracket(string part) => $"[{part}]";

    /// <summary>"Lumme one, flight of five, ..." shows as "Flight of five, ...": the speaker column already names him.</summary>
    private static string WithoutOwnCall(string text, string who)
    {
        string prefix = who + ", ";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || text.Length == prefix.Length)
            return text;
        string rest = text[prefix.Length..];
        return char.ToUpperInvariant(rest[0]) + rest[1..];
    }

    /// <summary>
    /// The answers to a call already on screen, starting <paramref name="start"/> from now. A silence
    /// is explained in one system line, using <paramref name="problem"/> (e.g. "position snapshot
    /// failed") when the request could not refresh positions.
    /// </summary>
    public static IReadOnlyList<ChatLine> Replies(RadioResult result, string? problem = null, TimeSpan start = default)
    {
        var lines = new List<ChatLine>();
        var at = start;
        foreach (var reply in result.Replies)
        {
            lines.Add(Line(reply, at));
            at += EstimatedAirtime(reply.Text);
        }

        string? silence = result.Outcome switch
        {
            ReplyOutcome.Position => null,
            _ when problem is not null => $"no reply: {problem}",
            ReplyOutcome.PlayerPositionUnknown => "no reply: no live position for your aircraft",
            ReplyOutcome.NoReply => "no reply: nobody can answer",
            ReplyOutcome.NoCourse => "no steer: only IL-2's memory puts you on the briefing map; the track recording is another frame",
            _ => "no reply",
        };
        if (silence is not null)
            lines.Add(new ChatLine(ChatLineKind.System, "", silence, -1, start + TimeSpan.FromSeconds(0.5)));

        return lines;
    }
}
