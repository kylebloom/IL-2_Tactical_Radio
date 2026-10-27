using Il2Radio.Core.Contacts;

namespace Il2Radio.Core.Radio;

/// <summary>
/// Commands 3 and 5: where is my own flight, and how do I rejoin it. Only the player's flight
/// (from mission-log AType 10) answers. The flight's "main group" is its largest cluster of aircraft
/// flying together; members flying apart from it report separately.
/// </summary>
public static class FlightCommands
{
    /// <summary>Command 3, "Request squadron/formation position": clock position, range and height of the flight.</summary>
    public static RadioResult FlightPosition(ContactTracker tracker, RadioSettings? settings = null)
    {
        settings ??= new RadioSettings();
        var suppressed = new List<SuppressedContact>();
        if (RadioPicture.Build(tracker, settings, RadioPicture.IsFlightMate, suppressed) is not { } picture)
            return new RadioResult(ReplyOutcome.PlayerPositionUnknown, [], suppressed);

        var groups = MainGroupFirst(picture, settings);
        var replies = new List<RadioReply>();
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var speaker = g.Speaker.Contact;
            string text;
            List<SpeechToken> tokens = [.. SpeechToken.Identify(speaker)];
            if (g.InFormation(settings))
            {
                text = $"{RadioPhrases.Who(speaker)}, you are in formation with us.";
                tokens.Add(new(SpeechWord.InFormation));
                tokens.Add(Beside(g.Speaker, picture.PlayerFix));
            }
            else if (settings.Realism == RadioRealism.Pilot
                     && !PilotReport.KnowsWhereYouAre(g, picture.Player, picture.PlayerFix, tracker, settings))
            {
                replies.Add(PilotReport.Unseen(g, separated: i > 0, PlanSquare.Report(g, tracker.Plan)));
                continue;
            }
            else if (settings.Realism == RadioRealism.Pilot)
            {
                var place = PilotReport.Place(g.Relative.BearingDegrees, picture.PlayerFix.Heading, mine: false, g.Relative.RangeMetres, g.Relative.AltitudeDeltaMetres);
                if (i > 0)
                {
                    string with = g.Members.Count > 1
                        ? $" with {RadioPhrases.Count(g.Members.Count - 1)} other{(g.Members.Count > 2 ? "s" : "")}"
                        : "";
                    text = $"{RadioPhrases.Who(speaker)}, separated{with}, {place.Text}.";
                    tokens.Add(new(SpeechWord.Separated, g.Members.Count - 1));
                }
                else if (g.Members.Count > 1)
                {
                    text = $"{RadioPhrases.Who(speaker)}, flight of {RadioPhrases.Count(g.Members.Count)}, {place.Text}.";
                    tokens.Add(new(SpeechWord.Group, g.Members.Count));
                }
                else
                {
                    text = $"{RadioPhrases.Who(speaker)}, I am {place.Text}.";
                }

                PilotReport.AddPlace(tokens, place);
            }
            else
            {
                string who = i == 0
                    ? $"{RadioPhrases.Who(speaker)}, {(g.Members.Count > 1 ? $"flight of {RadioPhrases.Count(g.Members.Count)}" : "I am")}"
                    : $"{RadioPhrases.Who(speaker)}, separated{(g.Members.Count > 1 ? $" with {RadioPhrases.Count(g.Members.Count - 1)} other{(g.Members.Count > 2 ? "s" : "")}" : "")},";
                string where = picture.PlayerFix.Heading is double heading
                    ? $"at your {RadioPhrases.Clock(g.Relative.BearingDegrees, heading)}"
                    : $"bearing {RadioPhrases.Bearing(g.Relative.BearingDegrees)}";
                text = $"{who} {where}, {RadioPhrases.Range(g.Relative.RangeMetres)}, {RadioPhrases.Vertical(g.Relative.AltitudeDeltaMetres)}.";

                if (i > 0)
                    tokens.Add(new(SpeechWord.Separated, g.Members.Count - 1));
                else if (g.Members.Count > 1)
                    tokens.Add(new(SpeechWord.Group, g.Members.Count));
                tokens.Add(picture.PlayerFix.Heading is double h
                    ? new(SpeechWord.Clock, RadioPhrases.ClockHour(g.Relative.BearingDegrees, h))
                    : new(SpeechWord.Bearing, g.Relative.BearingDegrees));
                tokens.Add(new(SpeechWord.Range, g.Relative.RangeMetres));
                tokens.Add(new(SpeechWord.Vertical, g.Relative.AltitudeDeltaMetres));
            }

            replies.Add(new RadioReply(speaker, g.Members.Select(m => m.Contact).ToList(), g.Relative, g.MaxAgeSeconds, text, tokens));
        }

        return new RadioResult(replies.Count > 0 ? ReplyOutcome.Position : ReplyOutcome.NoReply, replies, suppressed);
    }

    /// <summary>Command 5, "Separated — request rejoin": a steer to the flight's main group, its height and heading.</summary>
    public static RadioResult Rejoin(ContactTracker tracker, RadioSettings? settings = null)
    {
        settings ??= new RadioSettings();
        var suppressed = new List<SuppressedContact>();
        if (RadioPicture.Build(tracker, settings, RadioPicture.IsFlightMate, suppressed) is not { } picture)
            return new RadioResult(ReplyOutcome.PlayerPositionUnknown, [], suppressed);

        var groups = MainGroupFirst(picture, settings);
        if (groups.Count == 0)
            return new RadioResult(ReplyOutcome.NoReply, [], suppressed);

        var main = groups[0];
        var speaker = main.Speaker.Contact;
        bool withUs = main.InFormation(settings);
        bool pilot = settings.Realism == RadioRealism.Pilot;
        bool knows = !pilot || PilotReport.KnowsWhereYouAre(main, picture.Player, picture.PlayerFix, tracker, settings);
        string text;
        List<SpeechToken> tokens = [.. SpeechToken.Identify(speaker)];
        if (withUs)
        {
            text = $"{RadioPhrases.Who(speaker)}, you are with us.";
            tokens.Add(new(SpeechWord.WithUs));
            tokens.Add(Beside(main.Speaker, picture.PlayerFix));
        }
        else if (pilot && !knows)
        {
            var unseen = PilotReport.Unseen(main, separated: false, PlanSquare.Report(main, tracker.Plan));
            text = unseen.Text;
            tokens = [.. unseen.Tokens!];
        }
        else if (pilot)
        {
            var place = PilotReport.Place(main.Relative.BearingDegrees, picture.PlayerFix.Heading, mine: false, main.Relative.RangeMetres, main.Relative.AltitudeDeltaMetres);
            text = $"{RadioPhrases.Who(speaker)}, {place.Text}. {PilotReport.OwnStateSentence(main.Heading, main.Centre.Altitude)}";
            PilotReport.AddPlace(tokens, place);
            PilotReport.AddOwnState(tokens, main.Heading, main.Centre.Altitude);
        }
        else
        {
            text = $"{RadioPhrases.Who(speaker)}, steer {RadioPhrases.Bearing(main.Relative.BearingDegrees)}, {RadioPhrases.Range(main.Relative.RangeMetres)}. " +
                   $"We are at {Math.Round(main.Centre.Altitude / 100.0) * 100:F0} metres" +
                   (main.Heading is double h ? $", heading {RadioPhrases.Bearing(h)}." : ".");
            tokens.Add(new(SpeechWord.Steer, main.Relative.BearingDegrees));
            tokens.Add(new(SpeechWord.Range, main.Relative.RangeMetres));
            tokens.Add(new(SpeechWord.Vertical, main.Relative.AltitudeDeltaMetres));
            tokens.Add(new(SpeechWord.Altitude, main.Centre.Altitude));
            if (main.Heading is double heading)
                tokens.Add(new(SpeechWord.Heading, heading));
        }

        var reply = new RadioReply(speaker, main.Members.Select(m => m.Contact).ToList(), main.Relative, main.MaxAgeSeconds, text, tokens);
        return new RadioResult(ReplyOutcome.Position, [reply], suppressed);
    }

    /// <summary>Largest group first (ties: nearest), then the remaining groups by range.</summary>
    /// <summary>The speaker's side of the player's nose (-180..180, negative left), for the close-in words. North when the track has no heading.</summary>
    private static SpeechToken Beside(LiveContact speaker, PositionFix player) =>
        new(SpeechWord.Beside, ((speaker.Relative.BearingDegrees - (player.Heading ?? 0)) % 360 + 540) % 360 - 180);

    private static List<ContactGroup> MainGroupFirst(RadioPicture picture, RadioSettings settings)
    {
        var groups = picture.Groups(settings).ToList();
        var main = groups.OrderByDescending(g => g.Members.Count).ThenBy(g => g.Relative.RangeMetres).FirstOrDefault();
        if (main is null)
            return [];
        return [main, .. groups.Where(g => g != main).OrderBy(g => g.Relative.RangeMetres)];
    }
}
