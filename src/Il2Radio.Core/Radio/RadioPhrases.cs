using Il2Radio.Core.Contacts;

namespace Il2Radio.Core.Radio;

/// <summary>Spoken-style phrasing for radio replies. Metric units, as used by Luftwaffe and VVS pilots.</summary>
internal static class RadioPhrases
{
    /// <summary>
    /// How a speaker identifies himself: his career callsign ("Oriole two") when the mission plan and the
    /// install's callsign table give one, else "Number two" in the player's flight, else his name.
    /// </summary>
    public static string Who(Contact c) =>
        c.CallsignName is { } word && CallNumber(c) is int n ? $"{word} {Count(n)}"
        : c.FormationIndex is int f && c.InPlayerFlight ? $"Number {Digit(f)}"
        : c.DisplayName;

    /// <summary>The number spoken after the callsign: the plan's Callnum, else the formation number in the player's flight.</summary>
    public static int? CallNumber(Contact c) => c.Callnum ?? (c.InPlayerFlight ? c.FormationIndex : null);

    /// <summary>"zero-three-zero", rounded to the nearest 10°.</summary>
    public static string Bearing(double degrees)
    {
        int rounded = (int)(Math.Round(degrees / 10.0) * 10) % 360;
        return string.Join("-", rounded.ToString("000").Select(d => Digit(d - '0')));
    }

    public static string Range(double metres)
    {
        double km = metres / 1000.0;
        return km < 1.5 ? "under two kilometres" : $"approximately {Math.Round(km):F0} kilometres";
    }

    public static string Altitude(double metres) => $"altitude {Math.Round(metres / 100.0) * 100:F0} metres";

    /// <summary>Clock position of a bearing relative to the player's heading: "three o'clock".</summary>
    public static string Clock(double bearing, double playerHeading) => $"{Number(ClockHour(bearing, playerHeading))} o'clock";

    /// <summary>1–12; 12 is dead ahead.</summary>
    public static int ClockHour(double bearing, double playerHeading)
    {
        double relative = ((bearing - playerHeading) % 360 + 360) % 360;
        int hour = (int)Math.Round(relative / 30.0) % 12;
        return hour == 0 ? 12 : hour;
    }

    public static string Vertical(double altitudeDeltaMetres)
    {
        if (Math.Abs(altitudeDeltaMetres) < 300)
            return "level with you";
        double rounded = Math.Round(Math.Abs(altitudeDeltaMetres) / 100.0) * 100;
        return $"{rounded:F0} metres {(altitudeDeltaMetres > 0 ? "above" : "below")} you";
    }

    public static string Digit(int digit) => digit switch
    {
        0 => "zero", 1 => "one", 2 => "two", 3 => "three", 4 => "four",
        5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine",
        _ => digit.ToString(),
    };

    public static string Number(int n) => n switch
    {
        10 => "ten", 11 => "eleven", 12 => "twelve",
        _ => Digit(n),
    };

    /// <summary>"Number one" → "Number one", for sentence starts; group size as words.</summary>
    public static string Count(int n) => n <= 12 ? Number(n) : n.ToString();
}
