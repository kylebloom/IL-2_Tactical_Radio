using Il2Radio.Core.Contacts;

namespace Il2Radio.Core.Radio;

/// <summary>
/// Radio command 1, "Request positions of nearby friendly aircraft". Friendlies flying together
/// answer as one group through their senior member; stragglers answer for themselves. A friendly
/// answers only when the integration has a fresh live track of it; planned waypoints and stale
/// event snapshots are never presented as current positions. In <see cref="RadioRealism.Pilot"/>,
/// another unit answers only when someone in it can see the player; the player's own flight that
/// cannot see him reports its heading and altitude instead of a vector.
/// </summary>
public static class FriendlyPositionsCommand
{
    public static RadioResult Execute(ContactTracker tracker, RadioSettings? settings = null)
    {
        settings ??= new RadioSettings();
        var suppressed = new List<SuppressedContact>();
        if (RadioPicture.Build(tracker, settings, RadioPicture.IsFriendly, suppressed) is not { } picture)
            return new RadioResult(ReplyOutcome.PlayerPositionUnknown, [], suppressed);

        if (settings.Realism == RadioRealism.Pilot)
            return Pilot(tracker, settings, picture, suppressed);

        var replies = picture.Groups(settings)
            .OrderBy(g => g.Relative.RangeMetres)
            .Select(g =>
            {
                var speaker = g.Speaker.Contact;
                string size = g.Members.Count > 1 ? $"{RadioPhrases.Count(g.Members.Count)} aircraft together, " : "";
                string text = $"{RadioPhrases.Who(speaker)}, {size}bearing {RadioPhrases.Bearing(g.Relative.BearingDegrees)}, " +
                              $"{RadioPhrases.Range(g.Relative.RangeMetres)}, {RadioPhrases.Altitude(g.Centre.Altitude)}.";
                List<SpeechToken> tokens = [.. SpeechToken.Identify(speaker)];
                if (g.Members.Count > 1)
                    tokens.Add(new(SpeechWord.Group, g.Members.Count));
                tokens.Add(new(SpeechWord.Bearing, g.Relative.BearingDegrees));
                tokens.Add(new(SpeechWord.Range, g.Relative.RangeMetres));
                tokens.Add(new(SpeechWord.Vertical, g.Relative.AltitudeDeltaMetres));
                tokens.Add(new(SpeechWord.Altitude, g.Centre.Altitude));
                return new RadioReply(speaker, g.Members.Select(m => m.Contact).ToList(), g.Relative, g.MaxAgeSeconds, text, tokens);
            })
            .ToList();

        return new RadioResult(replies.Count > 0 ? ReplyOutcome.Position : ReplyOutcome.NoReply, replies, suppressed);
    }

    private static RadioResult Pilot(ContactTracker tracker, RadioSettings settings, RadioPicture picture, List<SuppressedContact> suppressed)
    {
        var replies = new List<RadioReply>();
        foreach (var g in picture.Groups(settings).OrderBy(g => g.Relative.RangeMetres))
        {
            bool ownFlight = g.Members.Any(m => m.Contact.InPlayerFlight);
            bool knows = PilotReport.KnowsWhereYouAre(g, picture.Player, picture.PlayerFix, tracker, settings);
            if (!knows && !ownFlight)
            {
                foreach (var m in g.Members)
                    suppressed.Add(new(m.Contact, "cannot see the player"));
                continue;
            }

            if (!knows)
            {
                replies.Add(PilotReport.Unseen(g, separated: false));
                continue;
            }

            var speaker = g.Speaker.Contact;
            var place = PilotReport.Place(g.Relative.BearingDegrees, picture.PlayerFix.Heading, mine: false, g.Relative.RangeMetres, g.Relative.AltitudeDeltaMetres);
            string size = g.Members.Count > 1 ? $"{RadioPhrases.Count(g.Members.Count)} aircraft together, " : "";
            string text = $"{RadioPhrases.Who(speaker)}, {size}{place.Text}.";
            List<SpeechToken> tokens = [.. SpeechToken.Identify(speaker)];
            if (g.Members.Count > 1)
                tokens.Add(new(SpeechWord.Group, g.Members.Count));
            PilotReport.AddPlace(tokens, place);
            replies.Add(new RadioReply(speaker, g.Members.Select(m => m.Contact).ToList(), g.Relative, g.MaxAgeSeconds, text, tokens));
        }

        return new RadioResult(replies.Count > 0 ? ReplyOutcome.Position : ReplyOutcome.NoReply, replies, suppressed);
    }
}
