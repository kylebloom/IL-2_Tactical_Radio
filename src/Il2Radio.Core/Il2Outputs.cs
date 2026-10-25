namespace Il2Radio.Core;

/// <summary>One startup.cfg value this project wants, and what it is for.</summary>
/// <param name="Required">The radio can't work without it; the others feed the diagnostic monitor only.</param>
public sealed record Il2Output(string Section, string Key, string Value, string Why, bool Required);

/// <summary>The data outputs IL-2 must have switched on in startup.cfg, and switching them on.</summary>
public static class Il2Outputs
{
    public static IReadOnlyList<Il2Output> All { get; } =
    [
        new("telemetrydevice", "enable", "true", "player state/events over UDP", false),
        new("telemetrydevice", "addr", "\"127.0.0.1\"", "", false),
        new("telemetrydevice", "port", "29373", "", false),
        new("telemetrydevice", "decimation", "2", "", false),
        new("motiondevice", "enable", "true", "player attitude over UDP", false),
        new("motiondevice", "addr", "\"127.0.0.1\"", "", false),
        new("motiondevice", "port", "29373", "", false),
        new("motiondevice", "decimation", "2", "", false),
        new("system", "mission_text_log", "1", "mission log: who is in your flight, take-offs, losses", true),
        new("track_record", "tacviewrecord", "1", "Tacview track while recording: the only live aircraft positions", true),
    ];

    public static IEnumerable<Il2Output> Required => All.Where(o => o.Required);

    /// <summary>The outputs whose value in <paramref name="cfg"/> differs from the wanted one.</summary>
    public static IReadOnlyList<Il2Output> Missing(StartupConfig cfg, IEnumerable<Il2Output> outputs) =>
        outputs.Where(o => cfg.Get(o.Section, o.Key) != o.Value).ToList();

    /// <summary>
    /// Sets the outputs in the install's startup.cfg after backing it up. IL-2 rewrites the file on
    /// exit, so the caller must make sure the game is closed. Returns the backup's path, or null when
    /// nothing needed changing.
    /// </summary>
    public static string? Enable(Il2Install install, IEnumerable<Il2Output> outputs)
    {
        var cfg = StartupConfig.Load(install.StartupCfg);
        bool changed = false;
        foreach (var o in outputs)
            changed |= cfg.Set(o.Section, o.Key, o.Value);
        if (!changed)
            return null;

        string backup = $"{install.StartupCfg}.radio-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(install.StartupCfg, backup);
        cfg.Save(install.StartupCfg);
        return backup;
    }
}
