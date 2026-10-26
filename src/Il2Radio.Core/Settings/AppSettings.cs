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

    /// <summary>auto (the game's pilot voices by nation, else Windows TTS), tts, or a voice pack's name or folder.</summary>
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

    /// <summary>Hold replies while IL-2's own radio is talking (its audio peaks above the engine).</summary>
    public bool RadioWait { get; init; } = true;

    /// <summary>How far above its engine floor IL-2's audio must peak to count as talking.</summary>
    public double RadioWaitRatio { get; init; } = 1.8;

    /// <summary>Remind to start track recording (LCtrl+R) when none is running this long into a mission; 0 = never.</summary>
    public int RecordingReminderSeconds { get; init; } = 60;

    public ChatSettings Chat { get; init; } = new();

    public KeySettings Keys { get; init; } = new();

    public PiperSettings Piper { get; init; } = new();

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

    /// <summary>
    /// The settings text with <c>piper.enabled</c> set and every other value kept, so the player's answer to the
    /// German-voice question is remembered without him editing JSON (JSON comments are dropped). Blank text
    /// starts from an empty object.
    /// </summary>
    public static string WithPiperEnabled(string json, bool enabled)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var root = string.IsNullOrWhiteSpace(json)
            ? new System.Text.Json.Nodes.JsonObject()
            : System.Text.Json.Nodes.JsonNode.Parse(json, documentOptions: options)!.AsObject();
        if (root["piper"] is not System.Text.Json.Nodes.JsonObject piper)
            root["piper"] = piper = new System.Text.Json.Nodes.JsonObject();
        piper["enabled"] = enabled;
        return root.ToJsonString(JsonOptions);
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
            problems.Add("voice: expected auto, tts or a voice pack's name");
        if (Realism is null || !RealismModes.Contains(Realism.ToLowerInvariant()))
            problems.Add($"realism \"{Realism}\": expected {string.Join(" or ", RealismModes)}");

        if (Chat is null)
            problems.Add("chat: missing");
        else
            problems.AddRange(Chat.Validate());

        if (Keys is null)
            problems.Add("keys: missing");
        else
            problems.AddRange(Keys.Validate());

        if (Piper is null)
            problems.Add("piper: missing");
        else
            problems.AddRange(Piper.Validate());
        return problems;
    }
}

/// <summary>
/// The optional German phrase voice (Piper, a local neural text-to-speech engine). It speaks only what the
/// game's German recordings can't: aircraft type names, the side, "possible", "nothing sighted", "with us".
/// Everything else keeps the game's own pilot voices. Nothing is downloaded without the player's yes.
/// </summary>
public sealed record PiperSettings
{
    /// <summary>
    /// null (the default): not answered yet, so the radio asks once at start: Yes / No / Later, with what,
    /// from where and how big. true: use it, and ask before downloading it again if it is missing. false: the
    /// game's recordings only, never asked. The radio saves the player's yes or no here itself.
    /// </summary>
    public bool? Enabled { get; init; }

    /// <summary>Which Piper voice: de_DE-thorsten-medium (default), de_DE-thorsten-high, de_DE-thorsten-low or de_DE-karlsson-low.</summary>
    public string Voice { get; init; } = Audio.PiperCatalog.DefaultVoice;

    /// <summary>Where Piper and the voice are stored; null = a "piper" folder beside the program. Never inside the IL-2 folder.</summary>
    public string? Folder { get; init; }

    /// <summary>Piper's speaking pace: below 1 is faster, above 1 slower. Radio calls are brisk.</summary>
    public double LengthScale { get; init; } = 0.9;

    public IEnumerable<string> Validate()
    {
        if (Audio.PiperCatalog.Find(Voice ?? "") is null)
            yield return $"piper.voice \"{Voice}\": expected one of {string.Join(", ", Audio.PiperCatalog.Voices.Select(v => v.Name))}";
        if (Folder is not null && string.IsNullOrWhiteSpace(Folder))
            yield return "piper.folder: expected null (beside the program) or a folder path";
        if (!(LengthScale is >= 0.5 and <= 2))
            yield return $"piper.lengthScale {LengthScale.ToString(CultureInfo.InvariantCulture)}: expected 0.5 to 2";
    }
}

/// <summary>Where the radio text goes and how it looks.</summary>
public sealed record ChatSettings
{
    public static readonly string[] Outputs = ["rtss", "overlay", "window", "off"];

    /// <summary>
    /// rtss: RivaTuner Statistics Server's on-screen display, which works in exclusive fullscreen.
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
