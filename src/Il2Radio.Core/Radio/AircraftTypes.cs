namespace Il2Radio.Core.Radio;

public enum AircraftClass
{
    Unknown,
    Fighter,
    Attack,
    Bomber,
    Recon,
    Transport,
}

/// <summary>
/// What a pilot could call an aircraft type on the radio. <see cref="Family"/> is the period nickname
/// ("Messerschmitt", "Yak"); <see cref="WingspanMetres"/> drives how far away it can be seen.
/// </summary>
public sealed record AircraftType(string Family, string Plural, AircraftClass Class, double WingspanMetres)
{
    public static readonly AircraftType Unknown = new("aircraft", "aircraft", AircraftClass.Unknown, 12);
}

/// <summary>
/// Maps IL-2 model names (from Tacview <c>Name=</c> or the mission log, e.g. "Bf 109 F-4", "Yak-1 ser.69")
/// to radio vocabulary. Matching is by prefix, so new variants of a known family need no entry.
/// Wingspans are approximate published figures.
/// </summary>
public static class AircraftTypes
{
    private static readonly (string Prefix, AircraftType Type)[] Table =
    [
        // Luftwaffe and Axis
        ("Bf 109", new("Messerschmitt", "Messerschmitts", AircraftClass.Fighter, 9.9)),
        ("Bf 110", new("Messerschmitt 110", "Messerschmitt 110s", AircraftClass.Fighter, 16.3)),
        ("Me 410", new("Messerschmitt 410", "Messerschmitt 410s", AircraftClass.Fighter, 16.4)),
        ("Me 262", new("Messerschmitt jet", "Messerschmitt jets", AircraftClass.Fighter, 12.5)),
        ("Fw 190", new("Focke-Wulf", "Focke-Wulfs", AircraftClass.Fighter, 10.5)),
        ("Ju 87", new("Stuka", "Stukas", AircraftClass.Attack, 13.8)),
        ("Hs 129", new("Henschel", "Henschels", AircraftClass.Attack, 14.2)),
        ("Ju 88", new("Junkers", "Junkers", AircraftClass.Bomber, 20.1)),
        ("He 111", new("Heinkel", "Heinkels", AircraftClass.Bomber, 22.6)),
        ("Ar 234", new("Arado jet", "Arado jets", AircraftClass.Bomber, 14.4)),
        ("Ju 52", new("Junkers transport", "Junkers transports", AircraftClass.Transport, 29.3)),
        ("MC.202", new("Macchi", "Macchis", AircraftClass.Fighter, 10.6)),
        ("IAR 80", new("IAR", "IARs", AircraftClass.Fighter, 10.5)),
        // Soviet
        ("Yak", new("Yak", "Yaks", AircraftClass.Fighter, 10.0)),
        ("LaGG", new("LaGG", "LaGGs", AircraftClass.Fighter, 9.8)),
        ("La-5", new("Lavochkin", "Lavochkins", AircraftClass.Fighter, 9.8)),
        ("MiG", new("MiG", "MiGs", AircraftClass.Fighter, 10.2)),
        ("I-16", new("Rata", "Ratas", AircraftClass.Fighter, 9.0)),
        ("I-153", new("Chaika", "Chaikas", AircraftClass.Fighter, 10.0)),
        ("Il-2", new("Sturmovik", "Sturmoviks", AircraftClass.Attack, 14.6)),
        ("Pe-2", new("Pe-2", "Pe-2s", AircraftClass.Bomber, 17.2)),
        ("U-2", new("U-2", "U-2s", AircraftClass.Recon, 11.4)),
        // Lend-lease and Western Allies
        ("P-40", new("Kittyhawk", "Kittyhawks", AircraftClass.Fighter, 11.4)),
        ("P-39", new("Airacobra", "Airacobras", AircraftClass.Fighter, 10.4)),
        ("P-47", new("Thunderbolt", "Thunderbolts", AircraftClass.Fighter, 12.4)),
        ("P-51", new("Mustang", "Mustangs", AircraftClass.Fighter, 11.3)),
        ("P-38", new("Lightning", "Lightnings", AircraftClass.Fighter, 15.8)),
        ("Spitfire", new("Spitfire", "Spitfires", AircraftClass.Fighter, 11.2)),
        ("Hurricane", new("Hurricane", "Hurricanes", AircraftClass.Fighter, 12.2)),
        ("Tempest", new("Tempest", "Tempests", AircraftClass.Fighter, 12.5)),
        ("Typhoon", new("Typhoon", "Typhoons", AircraftClass.Fighter, 12.7)),
        ("A-20", new("Boston", "Bostons", AircraftClass.Bomber, 18.7)),
        ("B-25", new("Mitchell", "Mitchells", AircraftClass.Bomber, 20.6)),
        ("B-26", new("Marauder", "Marauders", AircraftClass.Bomber, 21.6)),
        ("Mosquito", new("Mosquito", "Mosquitos", AircraftClass.Bomber, 16.5)),
        ("C-47", new("Dakota", "Dakotas", AircraftClass.Transport, 29.0)),
    ];

    public static AircraftType Classify(string model)
    {
        string m = model.Trim();
        foreach (var (prefix, type) in Table)
            if (m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return type;
        return AircraftType.Unknown;
    }

    /// <summary>
    /// The type for a mission-file script name such as <c>he111h6</c> or <c>bf109f4</c>. The longest table prefix
    /// wins once spaces and punctuation are stripped, so <c>i153</c> is the Chaika and not the Rata.
    /// </summary>
    public static AircraftType ClassifyScript(string typeKey)
    {
        string key = LettersAndDigits(typeKey);
        if (key.Length == 0)
            return AircraftType.Unknown;
        AircraftType? best = null;
        int bestLength = 0;
        foreach (var (prefix, type) in Table)
        {
            string p = LettersAndDigits(prefix);
            if (p.Length > bestLength && key.StartsWith(p, StringComparison.Ordinal))
            {
                best = type;
                bestLength = p.Length;
            }
        }
        return best ?? AircraftType.Unknown;
    }

    private static string LettersAndDigits(string text) =>
        new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>The type with this <see cref="AircraftType.Family"/> (as carried by a speech token), or null.</summary>
    public static AircraftType? ByFamily(string family) =>
        family == AircraftType.Unknown.Family ? AircraftType.Unknown : Table.Select(e => e.Type).FirstOrDefault(t => t.Family == family);

    /// <summary>
    /// Silhouettes pilots confused in practice. Used for hedged ("looks like") misidentification at the
    /// edge of identification range; never applied to a confident identification.
    /// </summary>
    public static AircraftType? LookAlike(AircraftType type) => type.Family switch
    {
        "Messerschmitt" => Classify("Yak"),
        "Yak" => Classify("Bf 109"),
        "LaGG" => Classify("Bf 109"),
        "Focke-Wulf" => Classify("La-5"),
        "Lavochkin" => Classify("Fw 190"),
        "Sturmovik" => Classify("Hs 129"),
        "Henschel" => Classify("Il-2"),
        "Kittyhawk" => Classify("Bf 109"),
        "Junkers" => Classify("Pe-2"),
        "Pe-2" => Classify("Ju 88"),
        _ => null,
    };

    public static string ClassName(AircraftClass c, bool plural) => (c, plural) switch
    {
        (AircraftClass.Fighter, false) => "fighter",
        (AircraftClass.Fighter, true) => "fighters",
        (AircraftClass.Attack, false) => "ground-attack aircraft",
        (AircraftClass.Attack, true) => "ground-attack aircraft",
        (AircraftClass.Bomber, false) => "bomber",
        (AircraftClass.Bomber, true) => "bombers",
        (AircraftClass.Recon, false) => "reconnaissance aircraft",
        (AircraftClass.Recon, true) => "reconnaissance aircraft",
        (AircraftClass.Transport, false) => "transport",
        (AircraftClass.Transport, true) => "transports",
        _ => "aircraft",
    };
}
