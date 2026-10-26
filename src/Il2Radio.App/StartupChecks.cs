using System.Diagnostics;
using Il2Radio.Core;
using Il2Radio.Core.Audio;
using Il2Radio.Core.Settings;
using Il2Radio.Windows;

namespace Il2Radio.App;

/// <summary>
/// The checks made before a flight, each kept as one [ok] or [!!] line with what to do about it.
/// A check that fails returns a short warning for the chat output as well, so it is seen in game.
/// The window shows <see cref="Results"/>; nothing is written to a console.
/// </summary>
internal static class StartupChecks
{
    /// <summary>Every result line so far, shown on the Status tab and kept in the live view.</summary>
    public static List<string> Results { get; } = [];

    /// <summary>Drops the previous session's lines before the checks run again.</summary>
    public static void Reset() => Results.Clear();

    public static void Ok(string text) => Results.Add($"  [ok] {text}");

    public static void Info(string text) => Results.Add($"  [..] {text}");

    public static void Warn(string text, params string[] details)
    {
        Results.Add($"  [!!] {text}");
        foreach (string detail in details)
            Results.Add($"       {detail}");
    }

    public static bool Il2Running => Process.GetProcessesByName("Il-2").Length > 0;

    /// <summary>Administrator rights, which the manifest asks for; without them positions can't be read while a track records.</summary>
    public static string? Elevation()
    {
        if (ShadowCopy.IsElevated())
        {
            Ok("Running as administrator (needed to read live positions)");
            return null;
        }

        Warn("Not running as administrator, so live positions can't be read while IL-2 records a track.",
            "Start Il2Radio.exe again and accept the administrator prompt.");
        return "Radio not running as administrator: nobody can report positions. Restart it and accept the prompt.";
    }

    /// <summary>
    /// The mission log and Tacview track outputs in startup.cfg. When they are off and IL-2 is closed,
    /// <paramref name="confirm"/> asks whether to switch them on (with a backup). IL-2 rewrites the file
    /// on exit, so not while it runs. True from <paramref name="confirm"/> is the only yes.
    /// </summary>
    public static string? Outputs(Il2Install install, Func<string, bool> confirm)
    {
        var cfg = StartupConfig.Load(install.StartupCfg);
        var missing = Il2Outputs.Missing(cfg, Il2Outputs.Required);
        if (missing.Count == 0)
        {
            Ok("IL-2 exports the mission log and Tacview tracks (startup.cfg)");
            return null;
        }

        string[] details = missing.Select(o => $"{o.Section}.{o.Key} is {cfg.Get(o.Section, o.Key) ?? "missing"}, needs {o.Value}: {o.Why}").ToArray();
        Warn($"IL-2 isn't set to export what the radio reads ({install.StartupCfg}):", details);
        const string chatWarning = "IL-2 isn't exporting tracks or the mission log, so the radio has little to report. Close IL-2 and restart the radio to set it up.";
        if (Il2Running)
        {
            Info("Close IL-2 and start the radio again to switch them on (IL-2 rewrites startup.cfg when it exits).");
            return chatWarning;
        }

        string question = "Switch on the mission log and Tacview tracks in startup.cfg? The file is backed up first."
            + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, details);
        if (!confirm(question))
        {
            Info("startup.cfg left unchanged.");
            return chatWarning;
        }

        if (Il2Running)
        {
            Info("IL-2 has started meanwhile; startup.cfg left unchanged. Close it and start the radio again.");
            return chatWarning;
        }

        string? backup = Il2Outputs.Enable(install, missing);
        Ok($"startup.cfg updated{(backup is null ? "" : $" (backup: {backup})")}");
        return null;
    }

    /// <summary>
    /// The optional German phrase voice (Piper). Not answered yet, or on and missing: <paramref name="ask"/> is shown
    /// the short consent text once (Yes / No / Later) and the download runs only on yes. Yes and no are passed to
    /// <paramref name="remember"/> to be saved in settings.json; later is not saved. Off: one line saying how to get it.
    /// Any outcome without the voice leaves the radio as it was: game recordings, class words, full text on screen.
    /// Never asked when nothing would use it (voice "tts", or no German recordings, e.g. IL-2's audio not found).
    /// </summary>
    public static async Task<PiperSynthesizer?> PhraseVoice(AppSettings settings, VoiceSelection voices, Il2Install install,
        Func<IReadOnlyList<string>, PiperAnswer>? ask, Action<PiperProgress>? progress, Action<string>? log,
        Action<bool>? remember, CancellationToken cancel = default)
    {
        bool germanRecordings = voices.Voices.Any(v => v.Pack.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase));
        if (settings.Piper.Enabled != false && (voices.Mode == "tts" || !germanRecordings))
        {
            if (settings.Piper.Enabled == true)
                Info("German phrase voice not used: it only fills gaps in the game's German pilot recordings, and none are in use");
            return null;
        }

        var (result, synth) = await PiperConsole.StartAsync(settings.Piper, install.Root, ask, progress, log, cancel);
        if (result.Remember is { } answer)
            remember?.Invoke(answer);
        bool? started = synth is null ? null : await StartsWithin(synth, TimeSpan.FromSeconds(10), cancel);
        switch (result.Status)
        {
            case PiperSetupStatus.Off:
                Info(result.Message);
                break;
            case PiperSetupStatus.NotInstalled:
                Info(result.Message);
                break;
            case PiperSetupStatus.Ready when started is false:
                Warn("The German phrase voice is installed but didn't start, so it is left out this time.",
                    "An antivirus program may be blocking piper.exe (see Troubleshooting in the README).",
                    $"Detail: {synth?.LastError ?? "no answer"}");
                break;
            case PiperSetupStatus.Ready:
                Ok($"{result.Message} (aircraft names and \"nothing sighted\" in German)");
                break;
            default:
                Warn(result.Message);
                break;
        }

        voices.Synth = synth;
        return synth;
    }

    /// <summary>
    /// Whether Piper answered its warm-up phrase: true, false (it failed: blocked, crashed), or null when it is
    /// still loading after <paramref name="wait"/> (a slow first model load; replies use it once it is up).
    /// </summary>
    private static async Task<bool?> StartsWithin(PiperSynthesizer synth, TimeSpan wait, CancellationToken cancel)
    {
        var warm = synth.WarmUp();
        try
        {
            return await warm.WaitAsync(wait, cancel);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Whether the chosen chat output can be seen: RTSS must run; the overlay is hidden by exclusive fullscreen.</summary>
    public static void ChatOutput(ChatSettings chat, Il2Install install)
    {
        switch (chat.Output.ToLowerInvariant())
        {
            case "rtss" when Process.GetProcessesByName("RTSS").Length == 0:
                Warn("RivaTuner Statistics Server isn't running, so the radio text can't show in IL-2.",
                    "Start it (it sits in the system tray); the text appears as soon as it is up.");
                break;
            case "rtss":
                Ok("RivaTuner Statistics Server is running (radio text in the game)");
                break;
            case "overlay" when StartupConfig.Load(install.StartupCfg).Get("graphics", "fullscreen") == "1":
                Warn("IL-2 runs in exclusive fullscreen, which hides the overlay window.",
                    "Choose RTSS for the radio text, or play IL-2 windowed.");
                break;
            case "off":
                Ok("Radio text is off");
                break;
            default:
                Ok($"Radio text in a {chat.Output} window");
                break;
        }
    }
}
