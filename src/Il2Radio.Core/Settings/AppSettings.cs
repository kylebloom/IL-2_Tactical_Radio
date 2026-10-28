using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Il2Radio.Core.Input;
using Il2Radio.Core.Osd;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Settings;

/// <summary>
/// The radio app's settings file (settings.json next to the program). Every value has a default, so
/// the file only needs what differs; a missing file is created with all of them for editing.
/// Unknown names are errors, so a misspelt setting is reported instead of silently ignored.
/// </summary>
public sealed record AppSettings
{
    /// <summary>IL-2 install root; null finds it (IL2_PATH, then the usual Steam folders).</summary>
    public string? Il2Path { get; init; }

    /// <summary>Radio volume, 0 to 1, independent of IL-2's own sound settings.</summary>
    public double Volume { get; init; } = 0.8;

    /// <summary>auto (the game's pilot voices by nation), off (chat only), or a voice pack's name or folder.</summary>
    public string Voice { get; init; } = "auto";

    /// <summary>
    /// pilot (the default): a friendly says only what that pilot could know. precise: bearings, ranges
    /// and counts from the live track. See <see cref="RadioRealism"/>.
    /// </summary>
    public string Realism { get; init; } = "pilot";

    public static readonly string[] RealismModes = ["pilot", "precise"];

    /// <summary>The radio's <see cref="RadioRealism"/> for this file. Call after <see cref="Validate"/>.</summary>
    public RadioRealism RealismMode =>
        string.Equals(Realism, "precise", StringComparison.OrdinalIgnoreCase) ? RadioRealism.Precise : RadioRealism.Pilot;

    /// <summary>
    /// Where live positions come from. auto (the default): IL-2's memory, read-only, so no track recording
    /// is needed; the Tacview track when memory can't be read. memory: memory only. track: the track only,
    /// as before 2026-10-27 (needs recording and administrator rights). See docs/MEMORY.md.
    /// </summary>
    public string Positions { get; init; } = "auto";

    public static readonly string[] PositionsModes = ["auto", "memory", "track"];

    /// <summary>The <see cref="Settings.PositionsMode"/> for this file. Call after <see cref="Validate"/>.</summary>
    public PositionsMode PositionsMode => ParsePositions(Positions) ?? PositionsMode.Auto;

    /// <summary>auto, memory or track (any case); null for anything else.</summary>
    public static PositionsMode? ParsePositions(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "auto" => Settings.PositionsMode.Auto,
        "memory" => Settings.PositionsMode.Memory,
        "track" => Settings.PositionsMode.Track,
        _ => null,
    };

    /// <summary>Hold replies while IL-2's own radio is talking (its audio peaks above the engine).</summary>
    public bool RadioWait { get; init; } = true;

    /// <summary>How far above its engine floor IL-2's audio must peak to count as talking.</summary>
    public double RadioWaitRatio { get; init; } = 1.8;

    /// <summary>Remind to start track recording (LCtrl+R) when none is running this long into a mission; 0 = never.</summary>
    public int RecordingReminderSeconds { get; init; } = 60;

    public ChatSettings Chat { get; init; } = new();

    public KeySettings Keys { get; init; } = new();

    /// <summary>
    /// The German phrase voice's settings, from before 2026-10-26. That voice is gone (only the game's
    /// recordings speak), so the value is ignored and never written; it is accepted so an older file still loads.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Piper { get; init; }

    /// <summary>Calls the flight makes without a numpad request. Each one can be turned off on its own.</summary>
    public UnaskedSettings Unasked { get; init; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Reads settings text; problems (bad JSON, unknown names, bad values) come back instead of throwing.</summary>
    public static (AppSettings Settings, IReadOnlyList<string> Problems) Parse(string json)
    {
        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return (new AppSettings(), [ex.Message]);
        }

        settings ??= new AppSettings();
        return (settings, settings.Validate());
    }

    /// <summary>Loads the file, or creates it with the defaults when it doesn't exist.</summary>
    public static (AppSettings Settings, bool Created, IReadOnlyList<string> Problems) LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            File.WriteAllText(path, defaults.ToJson() + Environment.NewLine);
            return (defaults, true, []);
        }

        var (settings, problems) = Parse(File.ReadAllText(path));
        return (settings, false, problems);
    }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Volume is < 0 or > 1 || double.IsNaN(Volume))
            problems.Add($"volume {Volume.ToString(CultureInfo.InvariantCulture)}: expected 0 to 1");
        if (!(RadioWaitRatio > 1))
            problems.Add($"radioWaitRatio {RadioWaitRatio.ToString(CultureInfo.InvariantCulture)}: expected more than 1");
        if (RecordingReminderSeconds < 0)
            problems.Add($"recordingReminderSeconds {RecordingReminderSeconds}: expected 0 (off) or more");
        if (string.IsNullOrWhiteSpace(Voice))
            problems.Add("voice: expected auto, off or a voice pack's name");
        else if (Voice.Trim().Equals("tts", StringComparison.OrdinalIgnoreCase))
            problems.Add("voice \"tts\": Windows text-to-speech was removed; expected auto (the game's pilot voices), off or a voice pack's name");
        if (Realism is null || !RealismModes.Contains(Realism.ToLowerInvariant()))
            problems.Add($"realism \"{Realism}\": expected {string.Join(" or ", RealismModes)}");
        if (ParsePositions(Positions) is null)
            problems.Add($"positions \"{Positions}\": expected {string.Join(", ", PositionsModes)}");

        if (Chat is null)
            problems.Add("chat: missing");
        else
            problems.AddRange(Chat.Validate());

        if (Keys is null)
            problems.Add("keys: missing");
        else
            problems.AddRange(Keys.Validate());

        if (Unasked is null)
            problems.Add("unasked: missing");
        return problems;
    }
}

/// <summary>Where live positions come from; see <see cref="AppSettings.Positions"/>.</summary>
public enum PositionsMode { Auto, Memory, Track }

/// <summary>
/// Calls spoken without a numpad request. All are on until turned off. Turning all off also stops
/// the extra track snapshots those calls need while IL-2 is recording.
/// </summary>
public sealed record UnaskedSettings
{
    /// <summary>An AI leader calls the cruise course in degrees. A corner he has started turning for is "turning to heading 185"; a course he is already flying is "heading 185". When you lead, the chat shows "Route: Turning to heading 185."</summary>
    public bool Route { get; init; } = true;

    /// <summary>One call when the contact picture changes: a group gained, lost, or "on me".</summary>
    public bool Contacts { get; init; } = true;

    /// <summary>A flight mate who saw it calls one of the flight down, or an enemy going down, when IL-2's own pilots do not.</summary>
    public bool Losses { get; init; } = true;
}

/// <summary>Where the radio text goes and how it looks.</summary>
public sealed record ChatSettings
{
    public static readonly string[] Outputs = ["rtss", "ingame", "overlay", "window", "off"];

    /// <summary>
    /// rtss: RivaTuner Statistics Server's on-screen display, which works in exclusive fullscreen.
    /// ingame: our own OverlayKit host DLL, loaded into Il-2.exe, draws the text into the game's
    /// frames (fullscreen too, no RTSS; experimental).
    /// overlay: a click-through window over the game (windowed mode only). window: an ordinary
    /// window, e.g. on a second screen. off: no text.
    /// </summary>
    public string Output { get; init; } = "rtss";

    /// <summary>RTSS text position: left-middle, top-left, top-right, bottom-left, bottom-right, default (RTSS's own) or "X,Y".</summary>
    public string RtssPosition { get; init; } = "left-middle";

    /// <summary>Distance from the screen edge for the RTSS presets, in screen pixels.</summary>
    public int RtssMargin { get; init; } = 32;

    /// <summary>Where RTSS lines wrap (RTSS doesn't wrap itself).</summary>
    public int RtssColumns { get; init; } = 72;

    /// <summary>RTSS OSD zoom; null reads it from IL-2's RTSS profile.</summary>
    public int? RtssZoom { get; init; }

    /// <summary>RTSS character size [width, height] in zoomed pixels, for the right and bottom presets; null uses the estimate (9, 16).</summary>
    public int[]? RtssCell { get; init; }

    /// <summary>Screen number for the overlay and window outputs (0 = primary).</summary>
    public int Screen { get; init; }

    /// <summary>Overlay and window area [left, top, width, height] as fractions of the screen.</summary>
    public double[] WindowArea { get; init; } = [0.015, 0.40, 0.42, 0.30];

    /// <summary>Overlay and window font size in pixels on a 1080-line screen.</summary>
    public double FontPixels { get; init; } = 16;

    /// <summary>Stay out of IL-2's own message line and subtitles.</summary>
    public bool KeepClear { get; init; } = true;

    /// <summary>The RTSS position as a placement. Call only on validated settings.</summary>
    public RtssPlacement Placement() => RtssPlacement.Parse(RtssPosition) with { Margin = RtssMargin };

    public IEnumerable<string> Validate()
    {
        if (!Outputs.Contains(Output?.ToLowerInvariant()))
            yield return $"chat.output \"{Output}\": expected {string.Join(", ", Outputs)}";
        string? position = null;
        try
        {
            RtssPlacement.Parse(RtssPosition);
        }
        catch (ArgumentException)
        {
            position = $"chat.rtssPosition \"{RtssPosition}\": expected left-middle, top-left, top-right, bottom-left, bottom-right, default or \"X,Y\"";
        }
        if (position is not null)
            yield return position;
        if (RtssMargin < 0)
            yield return $"chat.rtssMargin {RtssMargin}: expected 0 or more";
        if (RtssColumns < RtssPlacement.MinColumns)
            yield return $"chat.rtssColumns {RtssColumns}: expected at least {RtssPlacement.MinColumns}";
        if (RtssZoom is <= 0)
            yield return $"chat.rtssZoom {RtssZoom}: expected null (from RTSS) or more than 0";
        if (RtssCell is not null && (RtssCell.Length != 2 || RtssCell.Any(v => v <= 0)))
            yield return "chat.rtssCell: expected null or [width, height] in pixels";
        if (Screen < 0)
            yield return $"chat.screen {Screen}: expected 0 or more";
        if (WindowArea is not [var l, var t, var w, var h] || l < 0 || t < 0 || w <= 0 || h <= 0 || l + w > 1.0001 || t + h > 1.0001)
            yield return "chat.windowArea: expected [left, top, width, height] as fractions of the screen, inside it";
        if (!(FontPixels > 0))
            yield return $"chat.fontPixels {FontPixels.ToString(CultureInfo.InvariantCulture)}: expected more than 0";
    }
}

/// <summary>
/// Keys for each radio action, by name (see <see cref="VirtualKeys"/>). They are watched, never
/// consumed, so IL-2 still sees them: pick keys IL-2 doesn't use. Null or "" leaves an action unbound.
/// </summary>
public sealed record KeySettings
{
    public string? FriendlyPositions { get; init; } = "Num1";
    public string? FlightPosition { get; init; } = "Num2";
    public string? Rejoin { get; init; } = "Num3";
    public string? EnemyContacts { get; init; } = "Num4";
    public string? IdentifyContact { get; init; } = "Num5";
    public string? Menu { get; init; } = "Num0";
    public string? Older { get; init; } = "Num7";
    public string? Newer { get; init; } = "Num9";
    public string? Quit { get; init; } = "Num.";

    /// <summary>Each action's setting name and key name, in menu order.</summary>
    public IEnumerable<(string Action, string? Key)> All() =>
    [
        ("friendlyPositions", FriendlyPositions), ("flightPosition", FlightPosition), ("rejoin", Rejoin),
        ("enemyContacts", EnemyContacts), ("identifyContact", IdentifyContact), ("menu", Menu),
        ("older", Older), ("newer", Newer), ("quit", Quit),
    ];

    public IEnumerable<string> Validate()
    {
        var used = new Dictionary<int, string>();
        foreach (var (action, key) in All())
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (VirtualKeys.Parse(key) is not { } code)
            {
                yield return $"keys.{action} \"{key}\": unknown key; use one of {VirtualKeys.Help}";
                continue;
            }
            if (used.TryGetValue(code, out string? other))
                yield return $"keys.{action} \"{key}\": already used for keys.{other}";
            else
                used[code] = action;
        }
    }
}
