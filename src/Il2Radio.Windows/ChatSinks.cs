using Il2Radio.Core;
using Il2Radio.Core.Osd;
using Il2Radio.Core.Radio;
using Il2Radio.Core.Settings;

namespace Il2Radio.Windows;

public enum ChatMode
{
    Off,
    Overlay, // transparent, click-through window above the game (hidden by exclusive fullscreen)
    Window,  // ordinary window, e.g. on a second monitor
    Rtss,    // RivaTuner Statistics Server OSD: drawn inside the game's frames, works in exclusive fullscreen
}

/// <summary>Where the radio log goes and how it looks. Window position and size are fractions of the chosen screen.</summary>
public sealed record ChatOptions(
    ChatMode Mode = ChatMode.Overlay,
    int Screen = 0,
    float Left = 0.015f,
    float Top = 0.40f,   // bottom edge at 0.70, just above IL-2's own bottom-left message line (~0.73)
    float Width = 0.42f,
    float Height = 0.30f,
    float FontPixels = 16f,
    double HoldSeconds = 14,
    int MaxLines = 8,
    RtssPlacement? RtssPlacement = null,
    int RtssColumns = 72,
    int? RtssZoom = null,
    (int Width, int Height)? RtssScreen = null,
    (int Width, int Height)? RtssCell = null,
    bool KeepClear = true,
    IReadOnlyList<Il2TextArea>? Il2Areas = null,
    KeyBindings? Keys = null)
{
    /// <summary>The radio keys, for the menu and the log's paging hint.</summary>
    public KeyBindings KeyMap => Keys ?? KeyBindings.Default;

    /// <summary>IL-2's own text areas to keep clear of (none with --keep-clear off).</summary>
    public IReadOnlyList<Il2TextArea> Areas => KeepClear ? Il2Areas ?? Il2ScreenAreas.All : [];

    /// <summary>Rows of RTSS text to aim for before keeping clear of IL-2's areas: the line limit plus room for wrapping.</summary>
    public int RtssRows => MaxLines * 3;

    /// <summary>
    /// Estimated RTSS OSD character cell in zoomed pixels, used only to anchor text to the right or
    /// bottom edge. Not verified; calibrate with --rtss-cell W,H.
    /// </summary>
    public static readonly (int Width, int Height) DefaultRtssCell = (9, 16);

    /// <summary>
    /// Reads --chat overlay|window|rtss|off, --chat-screen N, --chat-pos L,T,W,H (fractions),
    /// --chat-font PX, --rtss-pos PRESET|X,Y, --rtss-margin PX, --rtss-columns N, --rtss-zoom Z,
    /// --rtss-screen WxH, --rtss-cell W,H, --keep-clear on|off.
    /// </summary>
    public static ChatOptions Parse(string[] args)
    {
        var options = new ChatOptions();
        string? Value(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        if (Value("--chat") is { } mode)
            options = options with { Mode = Enum.TryParse<ChatMode>(mode, ignoreCase: true, out var m) ? m : throw new ArgumentException($"--chat {mode}: expected overlay, window, rtss or off") };
        if (int.TryParse(Value("--chat-screen"), out int screen))
            options = options with { Screen = screen };
        if (float.TryParse(Value("--chat-font"), inv, out float font))
            options = options with { FontPixels = font };
        if (Value("--chat-pos")?.Split(',').Select(s => float.Parse(s, inv)).ToArray() is [var l, var t, var w, var h])
            options = options with { Left = l, Top = t, Width = w, Height = h };
        options = options with { RtssPlacement = RtssPlacement.Parse(Value("--rtss-pos"), Value("--rtss-margin")) };
        if (int.TryParse(Value("--rtss-columns"), out int columns))
            options = options with { RtssColumns = columns };
        if (int.TryParse(Value("--rtss-zoom"), out int zoom) && zoom > 0)
            options = options with { RtssZoom = zoom };
        if (Value("--rtss-screen")?.Split('x', 'X') is [var sw, var sh] && int.TryParse(sw, out int w2) && int.TryParse(sh, out int h2))
            options = options with { RtssScreen = (w2, h2) };
        if (Value("--rtss-cell")?.Split(',') is [var cw, var ch] && int.TryParse(cw, out int cellW) && int.TryParse(ch, out int cellH))
            options = options with { RtssCell = (cellW, cellH) };
        if (Value("--keep-clear") is { } keepClear)
            options = options with { KeepClear = keepClear.ToLowerInvariant() switch { "on" => true, "off" => false, _ => throw new ArgumentException($"--keep-clear {keepClear}: expected on or off") } };
        return options;
    }

    /// <summary>The options from the app's settings file. Call only on validated settings.</summary>
    public static ChatOptions From(ChatSettings chat, KeyBindings keys) => new()
    {
        Mode = Enum.Parse<ChatMode>(chat.Output, ignoreCase: true),
        Screen = chat.Screen,
        Left = (float)chat.WindowArea[0],
        Top = (float)chat.WindowArea[1],
        Width = (float)chat.WindowArea[2],
        Height = (float)chat.WindowArea[3],
        FontPixels = (float)chat.FontPixels,
        RtssPlacement = chat.Placement(),
        RtssColumns = chat.RtssColumns,
        RtssZoom = chat.RtssZoom,
        RtssCell = chat.RtssCell is [var w, var h] ? (w, h) : null,
        KeepClear = chat.KeepClear,
        Keys = keys,
    };

    /// <summary>Fills in the game's render size from startup.cfg unless --rtss-screen was given.</summary>
    public ChatOptions WithGameScreen(Il2Install? install)
    {
        if (RtssScreen is not null || install is null || !File.Exists(install.StartupCfg))
            return this;
        var cfg = StartupConfig.Load(install.StartupCfg);
        bool fullscreen = cfg.Get("graphics", "fullscreen") == "1";
        return int.TryParse(cfg.Get("graphics", fullscreen ? "full_width" : "win_width"), out int w)
               && int.TryParse(cfg.Get("graphics", fullscreen ? "full_height" : "win_height"), out int h)
            ? this with { RtssScreen = (w, h) }
            : this;
    }

    /// <summary>
    /// Narrows IL-2's text areas to those the planned Career mission uses: its subtitles' vertical
    /// alignments come from _gen.Mission. Without a mission every area is kept clear.
    /// </summary>
    public ChatOptions WithIl2Areas(Il2Install? install)
    {
        if (install is null || !File.Exists(install.GeneratedMission))
            return this;
        try
        {
            var alignments = Il2ScreenAreas.SubtitleAlignments(File.ReadAllText(install.GeneratedMission));
            return this with { Il2Areas = Il2ScreenAreas.ForSubtitleAlignments(alignments) };
        }
        catch (IOException)
        {
            return this;
        }
    }
}

/// <summary>An output for the radio log: the window overlay, a second-screen window or the RTSS OSD.</summary>
internal interface IChatSink : IDisposable
{
    /// <summary>Shows a new exchange. With <paramref name="syncToVoice"/>, replies appear as the voice starts them.</summary>
    void Post(IReadOnlyList<ChatLine> lines, bool syncToVoice = false);

    /// <summary>The voice started speaking this reply text.</summary>
    void Spoken(string text);

    /// <summary>Shows the command list instead of the live lines, or hides it again.</summary>
    void ToggleMenu(IReadOnlyList<ChatLine> menu);

    /// <summary>Pages through the radio log: positive is older, negative newer.</summary>
    void Scroll(int pages);

    /// <summary>The radio log stamps lines with this mission clock instead of the wall clock.</summary>
    void BindMissionClock(MissionClock clock);

    /// <summary>Keeps the stamps already shown, so the next mission does not rewrite them.</summary>
    void FreezeMissionTime();

    /// <summary>One-line state for the console (e.g. "RTSS not running"), or null.</summary>
    string? Status => null;
}

internal static class ChatSinks
{
    /// <summary>Opens the chosen chat output, or returns null when it is switched off or there is no desktop.</summary>
    public static IChatSink? Start(ChatOptions options) => options.Mode switch
    {
        ChatMode.Off => null,
        _ when !Environment.UserInteractive => null,
        ChatMode.Rtss => new RtssChat(options),
        _ => new ChatOverlay(options),
    };

    /// <summary>A timeline with the outputs' shared settings.</summary>
    public static ChatTimeline Timeline(ChatOptions options) =>
        new(options.HoldSeconds, maxLines: options.MaxLines) { HistoryHint = options.KeyMap.HistoryHint };

    /// <summary>Acts on the chat keys (menu and log paging); other keys are not the chat's.</summary>
    public static void HandleKey(IChatSink chat, RadioKey key, KeyBindings keys)
    {
        switch (key)
        {
            case RadioKey.Menu: chat.ToggleMenu(keys.Menu); break;
            case RadioKey.Older: chat.Scroll(+1); break;
            case RadioKey.Newer: chat.Scroll(-1); break;
        }
    }

    /// <summary>
    /// Repeats a sample exchange so the chat output can be checked over IL-2 without flying a mission.
    /// With <paramref name="png"/>, renders the overlay's views of it to image files instead.
    /// </summary>
    public static int Demo(ChatOptions options, int seconds, string? png = null)
    {
        ChatLine Line(ChatLineKind kind, string speaker, string text, double at, int coalition = 2) =>
            new(kind, speaker, text, coalition, TimeSpan.FromSeconds(at));
        string[] replies =
        [
            "Number one, five aircraft together, bearing one-one-zero, approximately 18 kilometres, altitude 2700 metres.",
            "Number six, bearing zero-zero-zero, approximately 5 kilometres, altitude 1200 metres.",
        ];
        IReadOnlyList<ChatLine> exchange =
        [
            Line(ChatLineKind.Request, "Gert Knoll", RadioChat.RequestPhrase(RadioCommand.FriendlyPositions), 0),
            Line(ChatLineKind.Reply, "Heinz Koelzig", replies[0], RadioChat.FirstReplyDelay.TotalSeconds),
            Line(ChatLineKind.Reply, "Ernst Lignitz", replies[1], (RadioChat.FirstReplyDelay + RadioChat.EstimatedAirtime(replies[0])).TotalSeconds),
            Line(ChatLineKind.Request, "Gert Knoll", RadioChat.RequestPhrase(RadioCommand.Rejoin), 13),
            Line(ChatLineKind.System, "", "no reply", 15, -1),
        ];

        if (png is not null)
        {
            foreach (string file in ChatOverlay.SaveFrames(options, exchange, png, options.RtssScreen ?? (1920, 1080)))
                Console.WriteLine($"wrote {file}");
            return 0;
        }

        using var chat = Start(options);
        if (chat is null)
            return 1;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); }; // so the RTSS slot is released

        var keys = options.KeyMap;
        Console.WriteLine($"Showing a sample radio exchange every 20 s for {seconds} s ({options.Mode}). {keys.Legend}; Ctrl+C also stops.");
        string? status = null;
        var next = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= next)
            {
                chat.Post(exchange);
                next = DateTime.UtcNow.AddSeconds(20);
            }
            if (chat.Status is { } s && s != status)
                Console.WriteLine(status = s);
            foreach (var key in keys.Pressed())
            {
                if (key == RadioKey.Quit)
                    stop.Cancel();
                HandleKey(chat, key, keys);
            }
            stop.Token.WaitHandle.WaitOne(50);
        }
        return 0;
    }
}
