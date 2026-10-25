using System.Diagnostics;
using Il2Radio.Core;
using Il2Radio.Core.Settings;
using Il2Radio.Windows;

namespace Il2Radio.App;

/// <summary>
/// The checks made before a flight, each printed as one [ok] or [!!] line with what to do about it.
/// A check that fails returns a short warning for the chat output as well, so it is seen in game.
/// </summary>
internal static class StartupChecks
{
    /// <summary>Every result line so far, kept on screen once the live view takes over the console.</summary>
    public static List<string> Results { get; } = [];

    public static void Ok(string text) => Print($"  [ok] {text}");

    public static void Info(string text) => Print($"  [..] {text}");

    private static void Print(string line)
    {
        Results.Add(line);
        Console.WriteLine(line);
    }

    public static void Warn(string text, params string[] details)
    {
        Results.Add($"  [!!] {text}");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  [!!] {text}");
        Console.ResetColor();
        foreach (string detail in details)
            Console.WriteLine($"       {detail}");
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
    /// offers to switch them on (with a backup); IL-2 rewrites the file on exit, so not while it runs.
    /// </summary>
    public static string? Outputs(Il2Install install)
    {
        var cfg = StartupConfig.Load(install.StartupCfg);
        var missing = Il2Outputs.Missing(cfg, Il2Outputs.Required);
        if (missing.Count == 0)
        {
            Ok("IL-2 exports the mission log and Tacview tracks (startup.cfg)");
            return null;
        }

        Warn($"IL-2 isn't set to export what the radio reads ({install.StartupCfg}):",
            missing.Select(o => $"{o.Section}.{o.Key} is {cfg.Get(o.Section, o.Key) ?? "missing"}, needs {o.Value}: {o.Why}").ToArray());
        const string chatWarning = "IL-2 isn't exporting tracks or the mission log, so the radio has little to report. Close IL-2 and restart the radio to set it up.";
        if (Il2Running)
        {
            Console.WriteLine("       Close IL-2 and start the radio again to switch them on (IL-2 rewrites startup.cfg when it exits).");
            return chatWarning;
        }

        Console.Write("       Switch them on now? startup.cfg is backed up first. Type y and Enter for yes: ");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
        {
            Console.WriteLine("       Left unchanged.");
            return chatWarning;
        }

        if (Il2Running)
        {
            Console.WriteLine("       IL-2 has started meanwhile; left unchanged. Close it and start the radio again.");
            return chatWarning;
        }

        string? backup = Il2Outputs.Enable(install, missing);
        Ok($"startup.cfg updated{(backup is null ? "" : $" (backup: {backup})")}");
        return null;
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
                    "Set \"chat\": { \"output\": \"rtss\" } in settings.json, or play IL-2 windowed.");
                break;
            case "off":
                Ok("Radio text is off (chat.output)");
                break;
            default:
                Ok($"Radio text in a {chat.Output} window");
                break;
        }
    }
}
