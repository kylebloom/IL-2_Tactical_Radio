using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;

namespace Il2Radio.Core.Radio;

/// <summary>
/// What a pilot can honestly say about a position he was not given. The numbers are a game-design
/// choice (SPECULATIVE as 1942 procedure): close is under 2 km, a few kilometres up to 8 km, otherwise
/// far; own altitude is to the nearest 500 m; a heading is an eight-point compass point. Nothing here
/// invents a bearing. Whether he can see the player uses the same visibility model as spotting.
/// </summary>
internal static class PilotReport
{
    /// <summary>Under this, the call is "close".</summary>
    public const double CloseMetres = 2_000;

    /// <summary>Beyond this, the call is "far". Up to this, "a few kilometres".</summary>
    public const double FarBeyondMetres = 8_000;

    /// <summary>An enemy this close to the speaker is his fight: he says "on me" when he cannot place it on the player.</summary>
    public const double EngagedMetres = 2_000;

    /// <summary>Same band as "level" everywhere else (300 m).</summary>
    public const double LevelMetres = 300;

    public static string Range(double metres) =>
        metres < CloseMetres ? "close" : metres <= FarBeyondMetres ? "a few kilometres" : "far";

    /// <summary>0 close, 1 a few kilometres, 2 far. Matches <see cref="SpeechWord.RangeBand"/>.</summary>
    public static double RangeBand(double metres) =>
        metres < CloseMetres ? 0 : metres <= FarBeyondMetres ? 1 : 2;

    public static string Height(double altitudeDeltaMetres) =>
        Math.Abs(altitudeDeltaMetres) < LevelMetres ? "level with you"
        : altitudeDeltaMetres > 0 ? "above you" : "below you";

    public static double RoundedAltitude(double metres) => Math.Round(metres / 500.0) * 500.0;

    public static string OwnStateSentence(double? heading, double altitude)
    {
        string alt = $"altitude {RoundedAltitude(altitude):F0} metres";
        return heading is double h
            ? $"Heading {Navigation.CompassPoint(h)}, {alt}."
            : $"{char.ToUpperInvariant(alt[0])}{alt[1..]}.";
    }

    /// <summary>Clock when the nose in question is known, otherwise an eight-point "to your east".</summary>
    public static DirectionPhrase Direction(double bearingDegrees, double? noseDegrees, bool mine)
    {
        string whose = mine ? "my" : "your";
        if (noseDegrees is double nose)
        {
            int hour = RadioPhrases.ClockHour(bearingDegrees, nose);
            return new($"at {whose} {RadioPhrases.Clock(bearingDegrees, nose)}", new(SpeechWord.Clock, hour));
        }

        return new($"to {whose} {Navigation.CompassPoint(bearingDegrees)}", new(SpeechWord.Bearing, bearingDegrees));
    }

    /// <summary>"at your three o'clock, a few kilometres, above you", plus the tokens that say it.</summary>
    public static PlacePhrase Place(double bearingDegrees, double? noseDegrees, bool mine, double rangeMetres, double altitudeDeltaMetres)
    {
        var direction = Direction(bearingDegrees, noseDegrees, mine);
        return new(
            $"{direction.Text}, {Range(rangeMetres)}, {Height(altitudeDeltaMetres)}",
            direction.Token,
            new(SpeechWord.RangeBand, RangeBand(rangeMetres)),
            new(SpeechWord.Vertical, altitudeDeltaMetres));
    }

    public static void AddPlace(List<SpeechToken> tokens, PlacePhrase place)
    {
        tokens.Add(place.Direction);
        tokens.Add(place.Range);
        tokens.Add(place.Height);
    }

    public static void AddOwnState(List<SpeechToken> tokens, double? heading, double altitude)
    {
        if (heading is double h)
            tokens.Add(new(SpeechWord.Heading, h));
        tokens.Add(new(SpeechWord.Altitude, RoundedAltitude(altitude)));
    }

    /// <summary>
    /// He can put himself relative to the player: a flight mate is in formation, or the visibility
    /// model says someone in the group has the player in sight this look. The look is the same 15 s dice as spotting.
    /// </summary>
    public static bool KnowsWhereYouAre(ContactGroup group, Contact player, PositionFix playerFix, ContactTracker tracker, RadioSettings settings) =>
        group.Members.Any(m =>
            (m.Contact.InPlayerFlight && m.Relative.RangeMetres <= settings.InFormationMetres)
            || CanSeePlayer(m, player, playerFix, tracker, settings));

    public static bool CanSeePlayer(LiveContact observer, Contact player, PositionFix playerFix, ContactTracker tracker, RadioSettings settings)
    {
        var weather = tracker.Plan?.Weather ?? MissionWeather.Unknown;
        var visibility = new Visibility(weather, settings.Visibility);
        var timeOfDay = weather.StartTime + TimeSpan.FromSeconds(playerFix.SourceTime);
        double p = visibility.DetectionProbability(observer.Fix.Position, playerFix.Position, AircraftTypes.Classify(player.TypeName), timeOfDay);
        if (p <= 0)
            return false;
        long slot = (long)Math.Floor(playerFix.SourceTime / 15.0);
        return Visibility.Roll(observer.Contact.Key, player.Key, slot, salt: 3) < p;
    }

    /// <summary>Own heading and altitude, and an admission that he cannot see the player. No clock and no steer.</summary>
    public static RadioReply Unseen(ContactGroup group, bool separated)
    {
        var speaker = group.Speaker.Contact;
        int count = group.Members.Count;
        string who = RadioPhrases.Who(speaker);
        string own = OwnStateSentence(group.Heading, group.Centre.Altitude);
        string text;
        List<SpeechToken> tokens = [.. SpeechToken.Identify(speaker)];
        if (separated)
        {
            string with = count > 1 ? $" with {RadioPhrases.Count(count - 1)} other{(count > 2 ? "s" : "")}" : "";
            text = $"{who}, separated{with}. I cannot see you. {own}";
            tokens.Add(new(SpeechWord.Separated, Math.Max(0, count - 1)));
        }
        else if (count > 1)
        {
            text = $"{who}, flight of {RadioPhrases.Count(count)}. I cannot see you. {own}";
            tokens.Add(new(SpeechWord.Group, count));
        }
        else
        {
            text = $"{who}, I cannot see you. {own}";
        }

        tokens.Add(new(SpeechWord.CannotSee));
        AddOwnState(tokens, group.Heading, group.Centre.Altitude);
        return new RadioReply(speaker, group.Members.Select(m => m.Contact).ToList(), group.Relative, group.MaxAgeSeconds, text, tokens);
    }
}

internal readonly record struct DirectionPhrase(string Text, SpeechToken Token);

internal readonly record struct PlacePhrase(string Text, SpeechToken Direction, SpeechToken Range, SpeechToken Height);
