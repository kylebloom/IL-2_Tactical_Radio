using System.Globalization;
using Il2Radio.Core.Contacts;

namespace Il2Radio.Core.Memory;

/// <summary>
/// The rows of <c>positions-*.csv</c>, written beside each evidence file: every aircraft at every memory read,
/// the ground truth a flight review checks replies against when no track was recorded. The first six columns
/// are mem-roster --csv's, so Diag mem-compare reads either file.
/// </summary>
public static class PositionLog
{
    public const string Header = "utc_ms,id,north,height,east,speed,heading,session_s,name,type,coalition,status";

    /// <param name="utcMs">Unix milliseconds of the read.</param>
    /// <param name="sessionSeconds">The tracker's time for the read (seconds since the radio started).</param>
    /// <param name="contact">The mission log's aircraft for this id; null while the log has not named it.</param>
    public static string Row(long utcMs, double sessionSeconds, MemoryAircraft aircraft, Contact? contact) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{utcMs},{aircraft.Id},{aircraft.North:F1},{aircraft.Height:F1},{aircraft.East:F1},{aircraft.Speed:F1}," +
            $"{(contact?.LastFix is { Source: PositionSource.Memory, Heading: { } heading } ? heading.ToString("F0", CultureInfo.InvariantCulture) : "")}," +
            $"{sessionSeconds:F1},{Quote(contact?.DisplayName)},{Quote(contact?.TypeName)},{contact?.Coalition.ToString(CultureInfo.InvariantCulture)},{contact?.Status}");

    private static string Quote(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.IndexOfAny([',', '"', '\n']) < 0 ? text : $"\"{text.Replace("\"", "\"\"")}\"";
}
