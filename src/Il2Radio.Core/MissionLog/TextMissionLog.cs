using System.Globalization;
using System.Text.RegularExpressions;
using Il2Radio.Core.Geometry;

namespace Il2Radio.Core.MissionLog;

/// <summary>
/// Parser for text mission reports (<c>missionReport(...)[N].txt</c>), written when
/// <c>mission_text_log = 1</c> is set in startup.cfg. Field names follow the community-documented
/// "AType" format used by server statistics tools.
/// </summary>
public static partial class TextMissionLog
{
    public static MissionLogEvent? ParseLine(string line)
    {
        line = line.Trim();
        Match head = HeadRegex().Match(line);
        if (!head.Success)
            return null;

        long tick = long.Parse(head.Groups["tick"].Value, CultureInfo.InvariantCulture);
        int atype = int.Parse(head.Groups["atype"].Value, CultureInfo.InvariantCulture);
        var fields = ParseFields(line);

        return atype switch
        {
            0 => new MissionStarted(tick, ParseGameDate(fields), fields.GetValueOrDefault("MFile", "")),
            1 => new HitEvent(tick, fields["AMMO"], Id(fields, "AID"), Id(fields, "TID")),
            2 => new DamageEvent(tick, float.Parse(fields["DMG"], CultureInfo.InvariantCulture), Id(fields, "AID"), Id(fields, "TID"), Pos(line)!.Value),
            3 => new KillEvent(tick, Id(fields, "AID"), Id(fields, "TID"), Pos(line)!.Value),
            4 => new SortieEndedEvent(tick, Id(fields, "PLID"), Id(fields, "PID"), Pos(line)!.Value),
            5 => new TakeOffEvent(tick, Id(fields, "PID"), Pos(line)!.Value),
            6 => new LandingEvent(tick, Id(fields, "PID"), Pos(line)!.Value),
            7 => new MissionEndedEvent(tick),
            10 => new PlaneSpawnedEvent(tick, Id(fields, "PLID"), Id(fields, "PID"), Pos(line)!.Value,
                fields.GetValueOrDefault("TYPE", ""), (int)Id(fields, "COUNTRY"), (int)Id(fields, "FORM"), Id(fields, "FIELD"),
                (int)Id(fields, "INAIR"), Id(fields, "PARENT"), fields.GetValueOrDefault("ISPL") == "1", fields.GetValueOrDefault("LOGIN", "")),
            11 => new GroupInitEvent(tick, Id(fields, "GID"), BinaryMissionLog.ParseIdList(fields.GetValueOrDefault("IDS", "")), Id(fields, "LID")),
            12 => new ObjectSpawnedEvent(tick, Id(fields, "ID"), StripIndexSuffix(fields.GetValueOrDefault("TYPE", "")), (int)Id(fields, "COUNTRY"),
                fields.GetValueOrDefault("NAME", ""), Id(fields, "PID"), Pos(line)),
            16 => new BotDeinitEvent(tick, Id(fields, "BOTID"), Pos(line)!.Value),
            17 => new PositionEvent(tick, Id(fields, "ID"), Pos(line)!.Value),
            18 => new BotEjectedEvent(tick, Id(fields, "BOTID"), Id(fields, "PARENTID"), Pos(line)!.Value),
            _ => new UnhandledEvent(tick, atype),
        };
    }

    /// <summary>
    /// Splits "KEY:value KEY:value" pairs. Values may contain spaces (e.g. "TYPE:Bf 109 F-4"),
    /// so a value runs until the next " KEY:" token.
    /// </summary>
    private static Dictionary<string, string> ParseFields(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var matches = KeyRegex().Matches(line);
        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index + matches[i].Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;
            string value = line[start..end].Trim();
            int posIndex = value.IndexOf(" POS(", StringComparison.Ordinal);
            if (posIndex >= 0)
                value = value[..posIndex];
            result[matches[i].Groups["key"].Value] = value;
        }

        return result;
    }

    private static long Id(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && long.TryParse(value, CultureInfo.InvariantCulture, out long id) ? id : -1;

    private static WorldPoint? Pos(string line)
    {
        Match m = PosRegex().Match(line);
        if (!m.Success)
            return null;
        double P(string g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        return WorldPoint.FromIl2(P("x"), P("y"), P("z"));
    }

    private static DateTime? ParseGameDate(Dictionary<string, string> fields)
    {
        if (!fields.TryGetValue("GDate", out string? date) || !fields.TryGetValue("GTime", out string? time))
            return null;
        return DateTime.TryParseExact($"{date} {time}", "yyyy.M.d H:m:s", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : null;
    }

    private static string StripIndexSuffix(string type) => IndexSuffixRegex().Replace(type, "");

    [GeneratedRegex(@"^T:(?<tick>\d+) AType:(?<atype>\d+)")]
    private static partial Regex HeadRegex();

    [GeneratedRegex(@"(?:^|\s)(?<key>[A-Za-z]+):")]
    private static partial Regex KeyRegex();

    // POS(x,y,z) or a bare "(x,y,z)" as used by AType 4 and 10.
    [GeneratedRegex(@"\((?<x>-?[\d.]+),\s*(?<y>-?[\d.]+),\s*(?<z>-?[\d.]+)\)")]
    private static partial Regex PosRegex();

    [GeneratedRegex(@"(\[-?\d+,-?\d+\])+$")]
    private static partial Regex IndexSuffixRegex();
}
