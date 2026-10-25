using System.Text;
using System.Text.RegularExpressions;

namespace Il2Radio.Core.MissionFile;

/// <summary>
/// IL-2's callsign words, from <c>data\GUI\DefaultCallsigns.&lt;lang&gt;</c>: the names behind the numeric
/// <c>Callsign</c> of a mission's Plane block (8 → "Oriole"). Read from the user's install, never copied.
/// </summary>
public static partial class CallsignTable
{
    /// <summary>Parses lines like <c>callsign=8,  "Oriole",  101,201</c>; section headers and comments are ignored.</summary>
    public static IReadOnlyDictionary<int, string> Parse(TextReader reader)
    {
        var names = new Dictionary<int, string>();
        while (reader.ReadLine() is { } line)
        {
            var m = Entry().Match(line);
            if (m.Success && int.TryParse(m.Groups["id"].Value, out int id) && id > 0)
                names.TryAdd(id, m.Groups["name"].Value);
        }

        return names;
    }

    /// <summary>The table for a language ("eng", "ger", "rus"...) under an IL-2 data folder, or empty if it isn't there.</summary>
    public static IReadOnlyDictionary<int, string> Load(string dataDir, string language = "eng")
    {
        string path = Path.Combine(dataDir, "GUI", $"DefaultCallsigns.{language}");
        if (!File.Exists(path))
            return new Dictionary<int, string>();
        // The English table is UTF-8 with a BOM; StreamReader honours the BOM if a file has one.
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader);
    }

    [GeneratedRegex("""^\s*callsign\s*=\s*(?<id>\d+)\s*,\s*"(?<name>[^"]*)"\s*""")]
    private static partial Regex Entry();
}
