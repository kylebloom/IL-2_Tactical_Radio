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
public sealed record ChatLine(ChatLineKind Kind, string Speaker, string Text, int Coalition, TimeSpan Delay);

/// <summary>
/// Turns a radio exchange into text lines in the style of IL-2's chat ("Speaker: message"), for an
/// overlay or a second-screen window. Reply wording is the exact <see cref="RadioReply.Text"/>.
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
        RadioCommand.IdentifyContact => "Flight, identify nearest contact.",
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

    /// <summary>The player's call, shown the moment the key is pressed, before any answer is known.</summary>
    public static ChatLine RequestLine(RadioCommand command, Contact? player) =>
        new(ChatLineKind.Request, player?.PilotName is { Length: > 0 } name ? name : "You", RequestPhrase(command), player?.Coalition ?? -1, TimeSpan.Zero);

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
            var speaker = reply.Contact;
            lines.Add(new ChatLine(ChatLineKind.Reply, speaker.PilotName.Length > 0 ? speaker.PilotName : speaker.DisplayName,
                reply.Text, speaker.Coalition, at));
            at += EstimatedAirtime(reply.Text);
        }

        string? silence = result.Outcome switch
        {
            ReplyOutcome.Position => null,
            _ when problem is not null => $"no reply: {problem}",
            ReplyOutcome.PlayerPositionUnknown => "no reply: no live position for your aircraft",
            ReplyOutcome.NoReply => "no reply: nobody can answer",
            _ => "no reply",
        };
        if (silence is not null)
            lines.Add(new ChatLine(ChatLineKind.System, "", silence, -1, start + TimeSpan.FromSeconds(0.5)));

        return lines;
    }
}
