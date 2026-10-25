namespace Il2Radio.Core.Input;

/// <summary>
/// Key names used in the settings file ("Num1", "Num.", "F9", "K") and their Windows virtual-key
/// codes. Names are case-insensitive; each key has one display label, the first name listed for it.
/// </summary>
public static class VirtualKeys
{
    private static readonly List<(string Name, int Code)> Names = Build();

    private static List<(string, int)> Build()
    {
        var names = new List<(string, int)>();
        for (int i = 0; i <= 9; i++)
            names.Add(($"Num{i}", 0x60 + i));
        names.AddRange([("Num*", 0x6A), ("Num+", 0x6B), ("Num-", 0x6D), ("Num.", 0x6E), ("Num/", 0x6F)]);
        for (int i = 1; i <= 24; i++)
            names.Add(($"F{i}", 0x70 + i - 1));
        for (char c = 'A'; c <= 'Z'; c++)
            names.Add((c.ToString(), c));
        for (char c = '0'; c <= '9'; c++)
            names.Add((c.ToString(), c));
        names.AddRange(
        [
            ("Insert", 0x2D), ("Delete", 0x2E), ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
            ("Up", 0x26), ("Down", 0x28), ("Left", 0x25), ("Right", 0x27), ("Pause", 0x13), ("ScrollLock", 0x91),
        ]);

        // Aliases, after the labels: Windows Forms' names and spelled-out numpad keys.
        for (int i = 0; i <= 9; i++)
            names.Add(($"NumPad{i}", 0x60 + i));
        names.AddRange(
        [
            ("Multiply", 0x6A), ("Add", 0x6B), ("Subtract", 0x6D), ("Decimal", 0x6E), ("NumDel", 0x6E), ("Divide", 0x6F),
            ("Ins", 0x2D), ("Del", 0x2E), ("PgUp", 0x21), ("PgDn", 0x22),
        ]);
        return names;
    }

    /// <summary>The virtual-key code for a key name, or null if the name is unknown.</summary>
    public static int? Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string trimmed = name.Trim();
        foreach (var (n, code) in Names)
            if (n.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                return code;
        return null;
    }

    /// <summary>How a key is shown in menus and hints, e.g. "Num1".</summary>
    public static string Label(int code) =>
        Names.FirstOrDefault(n => n.Code == code).Name ?? $"key 0x{code:X2}";

    /// <summary>Numpad digits and the decimal key only work as such with Num Lock on; * + - / work either way.</summary>
    public static bool NeedsNumLock(int code) => code is >= 0x60 and <= 0x69 or 0x6E;

    /// <summary>Every accepted name, for error messages.</summary>
    public const string Help = "Num0–Num9, Num. Num+ Num- Num* Num/, F1–F24, A–Z, 0–9, Insert, Delete, Home, End, PageUp, PageDown, Up, Down, Left, Right, Pause, ScrollLock";
}
