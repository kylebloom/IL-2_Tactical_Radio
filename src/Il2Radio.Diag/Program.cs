using System.Diagnostics;
using System.IO.Compression;
using Il2Radio.Core;
using Il2Radio.Core.Acmi;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;
using Il2Radio.Windows;

Console.OutputEncoding = System.Text.Encoding.UTF8;
string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
string? gameArg = Option("--game");
var install = Il2Install.Locate(gameArg);

switch (command)
{
    case "check":
        return Check(args.Contains("--apply"));
    case "mission":
        return MissionCmd(Positional(1) ?? install?.GeneratedMission);
    case "mlg":
        return MlgCmd(Positional(1) ?? Newest(install?.FlightLogsDir, "*.mlg"));
    case "acmi":
        if (!RealismOk(out int acmiCode)) return acmiCode;
        return AcmiCmd(Positional(1) ?? Newest(install?.TracksDir, "*.acmi"));
    case "snapshot-test":
        return SnapshotTest(Positional(1) ?? Newest(install?.TracksDir, "*.acmi") ?? install?.StartupCfg);
    case "live":
        if (install is null) return Fail("IL-2 installation not found. Pass --game <path> or set IL2_PATH.");
        if (!RealismOk(out int liveCode)) return liveCode;
        if (Option("--positions") is { } positions && Il2Radio.Core.Settings.AppSettings.ParsePositions(positions) is null)
            return Fail($"--positions \"{positions}\": expected auto, memory or track");
        int port = int.TryParse(Option("--port"), out int p) ? p : ConfiguredPort(install) ?? 29373;
        TimeSpan? duration = int.TryParse(Option("--seconds"), out int secs) ? TimeSpan.FromSeconds(secs) : null;
        VoiceSelection? voices = args.Contains("--no-audio") ? null : Voices();
        using (var voice = voices is null ? null : new RadioVoice(Volume(), voices))
        using (var meter = voice is null || Option("--radio-wait") == "off" ? null : new Il2AudioMeter(RadioWaitRatio()))
        using (var chat = ChatSinks.Start(ChatOptions.Parse(args).WithGameScreen(install).WithIl2Areas(install)))
        {
            var session = new LiveSession(install, new LiveOptions
            {
                UdpPort = port,
                Duration = duration,
                Realism = ChosenRealism(),
                Positions = Il2Radio.Core.Settings.AppSettings.ParsePositions(Option("--positions") ?? "auto") ?? Il2Radio.Core.Settings.PositionsMode.Auto,
                UnaskedRoute = FlagOn("--unasked-route"),
                UnaskedContacts = FlagOn("--unasked-contacts"),
                UnaskedLosses = FlagOn("--unasked-losses"),
                Kneeboard = Il2Radio.Core.Osd.KneeboardPlacement.ParseView(Option("--kneeboard") ?? "small") ?? KneeboardView.Small,
            }, voice);
            session.Attach(meter, chat);
            return await session.RunAsync();
        }
    case "chat-test":
        return ChatSinks.Demo(ChatOptions.Parse(args).WithGameScreen(install).WithIl2Areas(install), int.TryParse(Option("--seconds"), out int demoSecs) ? demoSecs : 60, Option("--png"));
    case "kneeboard-test":
        return KneeboardTest.Run(Positional(1) ?? install?.GeneratedMission, Path.Combine(Environment.CurrentDirectory, "diag-output"),
            int.TryParse(Option("--screen-height"), out int kneeHeight) ? kneeHeight : 1080);
    case "audio-meter":
        return AudioMeter();
    case "say":
        return Say();
    case "voice-check":
        return VoiceCheck();
    case "mem-radio":
        return MemRadio();
    case "mem-compare":
        return MemCompare.Run(Positional(1), Positional(2) ?? Newest(install?.TracksDir, "*.acmi"));
    case "mem-info" or "mem-scan" or "mem-next" or "mem-near" or "mem-list" or "mem-dump" or "mem-ptrs" or "mem-roster" or "mem-refs" or "mem-watch":
        return MemTools.Run(command, args);
    default:
        Console.WriteLine("""
            Il2Radio.Diag — feasibility diagnostics for an IL-2 Great Battles tactical radio

              check [--apply]     Show which IL-2 data outputs are enabled; --apply enables them (backs up startup.cfg)
              mission [file]      Parse the Career mission (default data\Missions\_gen.Mission) — PLANNED data
              mlg [file]          Decode a binary mission log (default: newest in data\FlightLogs)
              snapshot-test [f]   Time reading a locked file (default: newest .acmi) via shadow copy — run as admin
              acmi [file] [--realism pilot|precise]
                                  Summarise a Tacview ACMI file and replay every radio command (default: newest in data\Tracks).
                                  --realism pilot (default): a pilot says only what he could know. precise: bearings and ranges from the track
              live [--port N] [--seconds S] [--volume V] [--voice V] [--no-audio] [--radio-wait on|off] [--radio-wait-ratio R]
                                  [--realism pilot|precise] [--unasked-route on|off] [--unasked-contacts on|off] [--unasked-losses on|off]
                                  [--positions auto|memory|track]  (auto: IL-2's memory, the track when memory can't be read)
                                  Run during a mission: telemetry + log tail + ACMI tail + contact table + radio
                                  --radio-wait (default on): hold replies while IL-2's own audio peaks R x above its floor (1.8)
                                  Chat text: --chat overlay|window|ingame|off  --chat-screen N  --chat-pos L,T,W,H  --chat-font PX
                                             (ingame: drawn in IL-2's own frames, works in exclusive fullscreen)
                                             --kneeboard-pos centre-right|top-right|...  --kneeboard small|large|hidden
                                             --keep-clear on|off   (default on: stay out of IL-2's message line and subtitles)
              chat-test [--seconds S] [--png file] [chat options]
                                  Loop a sample radio exchange in the chosen chat output (to check it shows over IL-2);
                                  --png renders the overlay's live, menu and radio-log views to images instead
              kneeboard-test [mission] [--screen-height H]
                                  Draw the kneeboard of a Career mission (default data\Missions\_gen.Mission) with sample replies,
                                  as diag-output\kneeboard-small.png and kneeboard-large.png, to check its look without IL-2
              audio-meter [--seconds S] [--radio-wait-ratio R] [--process NAME]
                                  Print IL-2's audio peak, floor and busy state, to tune --radio-wait-ratio while AI pilots talk
              say [--country C] [--voice V] [--volume V] [--range R] [--wav file]
                                  Sample replies in the voice for country C (201 German, 101 Soviet; default 201).
                                  R = 0..1 of radio range (more hiss further out); --wav saves instead of playing
              voice-check [--country C | --voice V] [--speaker N] [--play]
                                  List the clips a voice pack resolves to; --play plays each, with its key, to check by ear

            Memory (read-only, for finding IL-2's structures; see docs/MEMORY.md):
              mem-radio [--realism pilot|precise]
                                  Answer every radio command once from this mission's log and IL-2's memory (safe beside a flight)
            """ + "\n" + MemTools.Help + """


              --voice auto|off|<pack>  auto (default): recorded clips matching the speaker's nation; anything
                                  they can't say is left out (the chat has it). off: no voice
                                  <pack>: a voice.json path, its folder, or part of a pack name (e.g. ger)

              --game <path>       IL-2 install root (otherwise IL2_PATH or common Steam locations)
            """);
        return 0;
}

// Answers every radio command once from this mission's log and IL-2's memory, without the numpad or the chat
// outputs, so it can run beside a flight: the check that memory positions reach the radio as they should.
int MemRadio()
{
    if (install is null)
        return Fail("IL-2 installation not found. Pass --game <path> or set IL2_PATH.");
    if (!RealismOk(out int realismCode))
        return realismCode;
    var tracker = new ContactTracker();
    if (File.Exists(install.GeneratedMission))
        tracker.SetPlan(MissionPlan.Load(install.GeneratedMission));
    var events = LatestLogChunks(install.DataDir).SelectMany(c => BinaryMissionLog.DecodeAll(File.ReadAllBytes(c))).ToList();
    if (events.Count == 0) // at mission end IL-2 moves the binary chunks to FlightLogs and leaves the text ones
        events = LatestLogChunks(install.DataDir, ".txt").SelectMany(File.ReadLines).Select(TextMissionLog.ParseLine).OfType<MissionLogEvent>().ToList();
    events = events.OrderBy(e => e.Tick).ToList();
    foreach (var e in events)
        tracker.Apply(e);
    string? missionFile = events.OfType<MissionStarted>().LastOrDefault()?.MissionFile;
    if (missionFile?.Contains("_gen", StringComparison.OrdinalIgnoreCase) != true)
        return Fail($"The newest mission log is not a mission generated here ({missionFile ?? "no AType:0"}); memory is read only for those.");
    if (tracker.Player?.LogId is not long playerId)
        return Fail("The mission log does not name the player yet.");

    using var memory = ProcessMemory.Open(ProcessMemory.Il2ProcessName);
    if (memory is null)
        return Fail("Il-2.exe is not running.");
    var clock = Stopwatch.StartNew();
    if (Il2Radio.Core.Memory.ObjectRoster.FindPlayer(memory, (int)playerId, out ulong cls) is null)
        return Fail($"Player {playerId} not found in memory.");
    var objects = Il2Radio.Core.Memory.ObjectRoster.FindAll(memory, cls);
    Console.WriteLine($"Found {objects.Count} objects in {clock.Elapsed.TotalSeconds:F1} s; {events.Count} log events; player {tracker.Player.DisplayName} ({playerId}).");

    // Two reads 1.5 s apart, so courses over ground (headings) exist.
    List<Il2Radio.Core.Memory.MemoryAircraft> ReadAll() =>
        Il2Radio.Core.Memory.MemoryAircraft.Of(objects.Select(o => Il2Radio.Core.Memory.ObjectRoster.Read(memory, o.Address, cls)).OfType<Il2Radio.Core.Memory.MemoryObject>());
    tracker.Apply(ReadAll(), 0);
    Thread.Sleep(1500);
    tracker.Apply(ReadAll(), 1.5);

    var radio = new RadioSettings { Realism = ChosenRealism() };
    var sightings = new SightingMemory();
    EnemyCommands.Notice(tracker, radio, sightings);

    var me = tracker.Player.LastFix;
    Console.WriteLine($"\n{"contact",-30} {"type",-16} {"side",4} {"status",-10} {"source",-7} {"heading",7}  relative to you");
    foreach (var c in tracker.Contacts.Where(c => c.LastFix?.Source == PositionSource.Memory).OrderByDescending(c => c.IsPlayer).ThenBy(c => c.Coalition).ThenBy(c => c.FormationIndex ?? 99))
    {
        var f = c.LastFix!;
        string rel = !c.IsPlayer && me is not null && Il2Radio.Core.Geometry.Navigation.Relative(me.Position, f.Position) is { } r
            ? $"brg {r.BearingDegrees:000} rng {r.RangeMetres / 1000:F1} km Δalt {r.AltitudeDeltaMetres:+0;-0} m"
            : c.IsPlayer ? $"{f.Position.North:F0} N {f.Position.East:F0} E {f.Position.Altitude:F0} m" : "";
        Console.WriteLine($"{Trunc((c.IsPlayer ? "YOU " : "") + c.DisplayName, 30),-30} {Trunc(c.TypeName, 16),-16} {c.Coalition,4} {c.Status,-10} {f.Source,-7} {(f.Heading is double h ? $"{h:000}" : "-"),7}  {rel}");
    }

    Console.WriteLine($"\nRadio, realism {radio.Realism}:");
    foreach (var command in Enum.GetValues<RadioCommand>())
    {
        var result = RadioCommands.Execute(command, tracker, radio, sightings);
        Console.WriteLine($"\n  {RadioCommands.Title(command)} → {result.Outcome}");
        foreach (var r in result.Replies)
            Console.WriteLine($"    RADIO: \"{r.Text}\"");
        foreach (var x in result.Suppressed.Take(6))
            Console.WriteLine($"    silent: {x.Contact.DisplayName} — {x.Reason}");
    }
    return 0;
}

// Prints the IL-2 audio meter twice a second, to tune --radio-wait-ratio while AI pilots talk.
int AudioMeter()
{
    using var meter = new Il2AudioMeter(RadioWaitRatio(), processName: Option("--process") ?? Il2AudioMeter.ProcessName);
    var end = DateTime.UtcNow.AddSeconds(int.TryParse(Option("--seconds"), out int s) ? s : 60);
    while (DateTime.UtcNow < end)
    {
        Thread.Sleep(500);
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.f}  {meter.Status}");
    }
    return 0;
}

double RadioWaitRatio() =>
    double.TryParse(Option("--radio-wait-ratio"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double r) && r > 1 ? r : 1.8;

double Volume() =>
    double.TryParse(Option("--volume"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0.8;

VoiceSelection Voices()
{
    var voices = VoiceSelection.Create(Option("--voice"), install?.Root);
    foreach (string problem in voices.Problems)
        Console.WriteLine($"voice pack skipped: {problem}");

    return voices;
}

int Country() => int.TryParse(Option("--country"), out int c) ? c : 201;

int Say()
{
    double range = double.TryParse(Option("--range"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double r) ? r : 0.3;
    VoiceSelection voices;
    try { voices = Voices(); } catch (InvalidOperationException ex) { return Fail(ex.Message); }

    var transmissions = SampleTransmissions(range, Country());
    Console.WriteLine($"Voice: {voices.Describe()}");

    if (Option("--wav") is { } wavPath)
    {
        foreach (string spoken in RadioVoice.RenderToFile(transmissions, wavPath, Volume(), voices))
            Console.WriteLine($"  {spoken}");
        Console.WriteLine($"Wrote {wavPath}");
        return 0;
    }

    using var voice = new RadioVoice(Volume(), voices);
    voice.Say(transmissions);

    // Wait for playback: roughly 80 ms per character plus squelch, generously bounded.
    var deadline = DateTime.UtcNow.AddSeconds(5 + transmissions.Sum(t => t.Text.Length) * 0.12);
    Thread.Sleep(500);
    while (DateTime.UtcNow < deadline && voice.LastError is null && !voice.Idle)
        Thread.Sleep(100);
    foreach (string spoken in voice.History)
        Console.WriteLine($"  {spoken}");
    Console.WriteLine(voice.LastError is { } error ? $"Audio error: {error}" : $"Spoke {voice.History.Count} transmission(s).");
    return voice.LastError is null ? 0 : 1;
}

// The README's example replies, as the radio commands phrase them (English text plus tokens), for a flight with callsign 8 "Oriole".
static List<Transmission> SampleTransmissions(double range, int country) =>
[
    new("Oriole one, five aircraft together, bearing one-one-zero, approximately 18 kilometres, altitude 2700 metres.", 1, range,
        [new(SpeechWord.Callsign, 8), new(SpeechWord.Speaker, 1), new(SpeechWord.Group, 5), new(SpeechWord.Bearing, 110), new(SpeechWord.Range, 18_000), new(SpeechWord.Vertical, 2_400), new(SpeechWord.Altitude, 2_700)], country),
    new("Oriole one, flight of five at your three o'clock, approximately 18 kilometres, 2400 metres above you.", 1, range,
        [new(SpeechWord.Callsign, 8), new(SpeechWord.Speaker, 1), new(SpeechWord.Group, 5), new(SpeechWord.Clock, 3), new(SpeechWord.Range, 18_000), new(SpeechWord.Vertical, 2_400)], country),
    new("Oriole three, separated, at your six o'clock, approximately 4 kilometres, level with you.", 3, range,
        [new(SpeechWord.Callsign, 8), new(SpeechWord.Speaker, 3), new(SpeechWord.Separated, 0), new(SpeechWord.Clock, 6), new(SpeechWord.Range, 4_000), new(SpeechWord.Vertical, 0)], country),
    new("Oriole one, steer one-one-zero, approximately 18 kilometres. We are at 2700 metres, heading zero-eight-zero.", 1, range,
        [new(SpeechWord.Callsign, 8), new(SpeechWord.Speaker, 1), new(SpeechWord.Steer, 110), new(SpeechWord.Range, 18_000), new(SpeechWord.Vertical, 2_400), new(SpeechWord.Altitude, 2_700), new(SpeechWord.Heading, 80)], country),
    // Enemy contacts: the type name has no game recording (the class word stands in), "no contacts" is the actors' "Roger".
    new("Oriole two, pair of Yaks, at your three o'clock, approximately 4 kilometres, high.", 2, range,
        [new(SpeechWord.Callsign, 8), new(SpeechWord.Speaker, 2), new(SpeechWord.ContactCount, 2), new(SpeechWord.ContactClass, -1), new(SpeechWord.ContactType, 2, "Yak"),
         new(SpeechWord.Clock, 3), new(SpeechWord.Range, 4_000), new(SpeechWord.Vertical, 800)], country),
    new("Oriole one, no contacts.", 1, range,
        [new(SpeechWord.Callsign, 8), new(SpeechWord.Speaker, 1), new(SpeechWord.NoContacts)], country),
];

int VoiceCheck()
{
    VoiceSelection voices;
    try { voices = Voices(); } catch (InvalidOperationException ex) { return Fail(ex.Message); }
    if (voices.For(Country()) is not { } clips)
        return Fail($"No voice pack for country {Country()} (searched {string.Join(", ", VoiceSelection.Folders)}). {(install is null ? "IL-2 install not found: pass --game." : "")}");

    var pack = clips.Pack;
    int speaker = pack.SpeakerIndex(int.TryParse(Option("--speaker"), out int s) ? s : 1);
    Console.WriteLine($"{pack.Name} — {pack.ManifestPath}\n  root {pack.Root}\n  speaker {speaker + 1} of {pack.Speakers.Count} ({pack.Speakers[speaker]}), language {pack.Language}, pre-processed {pack.PreProcessed}");
    int missing = 0;
    foreach (string key in pack.Keys)
    {
        string? path = pack.ClipPath(key, speaker);
        if (path is null)
        {
            missing++;
            Console.WriteLine($"  {key,-16} MISSING");
            continue;
        }

        var audio = Il2Radio.Core.Audio.ClipAudio.Load(path);
        Console.WriteLine($"  {key,-16} {audio.Seconds,5:F2} s  {Path.GetRelativePath(pack.Root, path)}{(pack.Word(key) is { } word ? $"  \"{word}\"" : "")}");
        if (args.Contains("--play"))
        {
            var pcm = Il2Radio.Core.Audio.ClipAudio.Join([audio], 16_000);
            RadioVoice.PlayNow(Il2Radio.Core.Audio.RadioEffect.ToWav(pcm, 16_000));
            Thread.Sleep(350);
        }
    }

    Console.WriteLine($"{pack.Keys.Count() - missing} clips found, {missing} missing.");
    return missing == 0 ? 0 : 1;
}

int Check(bool apply)
{
    if (install is null) return Fail("IL-2 installation not found. Pass --game <path> or set IL2_PATH.");
    Console.WriteLine($"IL-2 install: {install.Root}");
    var cfg = StartupConfig.Load(install.StartupCfg);

    foreach (var (section, key, value, why, _) in Il2Outputs.All)
    {
        string? current = cfg.Get(section, key);
        bool ok = current == value;
        Console.WriteLine($"  [{(ok ? "ok" : "--")}] {section}.{key} = {current ?? "(missing)"}{(ok ? "" : $"   → want {value}")}{(why.Length > 0 ? $"   ({why})" : "")}");
    }

    if (!apply)
    {
        Console.WriteLine("\nRun 'check --apply' with IL-2 closed to enable these (startup.cfg is backed up first).");
        return 0;
    }

    if (Process.GetProcessesByName("Il-2").Length > 0)
        return Fail("IL-2 is running; it rewrites startup.cfg on exit. Close the game and retry.");
    if (Il2Outputs.Enable(install, Il2Outputs.All) is not { } backup)
    {
        Console.WriteLine("\nNothing to change.");
        return 0;
    }

    Console.WriteLine($"\nUpdated startup.cfg. Backup: {backup}");
    return 0;
}

int SnapshotTest(string? path)
{
    if (path is null || !File.Exists(path)) return Fail("File not found.");
    if (!ShadowCopy.IsElevated()) return Fail("Shadow copies need administrator rights: open the terminal with 'Run as administrator' and retry.");
    Console.WriteLine($"Leftover snapshots removed: {ShadowCopy.CleanUpLeftovers()}");
    Console.WriteLine($"Reading {path} through a volume shadow copy, 3 times...");
    long size = 0;
    for (int i = 0; i < 3; i++)
    {
        var (bytes, timings) = ShadowCopy.ReadFrom(path, 0);
        Console.WriteLine($"  #{i + 1}: {bytes.Length / 1024} KB ({bytes.Length - size:+#;-#;0} bytes vs previous) — {timings}");
        size = bytes.Length;
    }
    return 0;
}

int MissionCmd(string? path)
{
    if (path is null || !File.Exists(path)) return Fail("Mission file not found.");
    var plan = MissionPlan.Load(path);
    Console.WriteLine($"Mission: {path}\nMap {plan.GuiMap}, {plan.Date} {plan.Time}; {plan.Aircraft.Count} planned aircraft.");
    Console.WriteLine("NOTE: everything below is PLANNED mission data, not a live observation.\n");
    var player = plan.Player;
    Console.WriteLine(player is null
        ? "No player aircraft (AILevel = 0) found."
        : $"Player: {player.PilotName} in {player.TypeKey}, country {player.Country} (coalition {player.Coalition}), callsign {player.Callsign}-{player.Callnum}, start {player.StartPosition}");
    Console.WriteLine("\nLikely flight-mates (same country + callsign):");
    foreach (var a in plan.LikelyFlightMates)
        Console.WriteLine($"  {a.Callnum,2} {a.PilotName,-24} {a.TypeKey,-12} start {a.StartPosition}");
    if (player is not null)
    {
        // What the route and steer calls measure against (memory fixes only; this is the plan, not a position).
        var chains = plan.Aircraft
            .Where(a => a.Country == player.Country && (player.Callsign > 0 ? a.Callsign == player.Callsign : a.IsPlayer))
            .SelectMany(a => plan.Waypoints.TryGetValue(a.Index, out var c) ? c : [])
            .ToList();
        Console.WriteLine($"\nBriefed waypoint chains of the player's flight ({chains.Count}):");
        foreach (var chain in chains)
        {
            Console.WriteLine($"  {chain.Count} waypoints{(chain.Count >= 3 && chain[0] == chain[^1] ? " (closed loop)" : "")}:");
            foreach (var w in chain)
            {
                string square = MapGrid.IsStalingrad(plan.GuiMap) && MapGrid.TryLabel(w.Position.North, w.Position.East) is { } g ? g.Text : "-";
                Console.WriteLine($"    {w.Name,-18} {(w.IsAttack ? "attack" : w.IsLanding ? "landing" : ""),-8} grid {square,-8} {w.Position}");
            }
        }
        var briefed = plan.Aircraft
            .Where(a => a.Country == player.Country && (player.Callsign > 0 ? a.Callsign == player.Callsign : a.IsPlayer))
            .Select(a => plan.BriefedRoutes.GetValueOrDefault(a.Index))
            .FirstOrDefault(r => r is { Count: > 0 }) ?? [];
        Console.WriteLine($"Briefed order (what the route and steer calls use), {briefed.Count} waypoints, {Math.Max(0, briefed.Count - 1)} legs:");
        for (int i = 0; i < briefed.Count; i++)
        {
            var w = briefed[i];
            string square = MapGrid.IsStalingrad(plan.GuiMap) && MapGrid.TryLabel(w.Position.North, w.Position.East) is { } g ? g.Text : "-";
            string leg = i == 0 ? "" : $"leg {i}: {Navigation.CourseDegrees(Navigation.Relative(briefed[i - 1].Position, w.Position)!.Value.BearingDegrees):000}, " +
                $"{Navigation.Relative(briefed[i - 1].Position, w.Position)!.Value.RangeMetres / 1000:F0} km";
            Console.WriteLine($"    {i + 1,2} {w.Name,-18} grid {square,-8} {leg}");
        }
        Console.WriteLine(plan.HomeField(chains) is { } home
            ? $"Home field: {home.Name} (country {home.Country}) {home.Position}"
            : "Home field: none of the player's country in this mission");
    }

    Console.WriteLine("\nPlanned aircraft by coalition and type:");
    foreach (var g in plan.Aircraft.GroupBy(a => (a.Coalition, a.TypeKey)).OrderBy(g => g.Key))
        Console.WriteLine($"  coalition {g.Key.Coalition}: {g.Count(),3} × {g.Key.TypeKey}");
    return 0;
}

int MlgCmd(string? path)
{
    if (path is null || !File.Exists(path)) return Fail("Mission log (.mlg) not found.");
    var events = BinaryMissionLog.DecodeAll(File.ReadAllBytes(path));
    Console.WriteLine($"Log: {path}\n{events.Count} events, last at {events.LastOrDefault()?.GameTime:hh\\:mm\\:ss} game time.");
    Console.WriteLine("Event counts: " + string.Join(", ", events.GroupBy(e => e.GetType().Name).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));

    var tracker = new ContactTracker();
    string? missionFile = install?.GeneratedMission;
    if (missionFile is not null && File.Exists(missionFile))
        tracker.SetPlan(MissionPlan.Load(missionFile)); // only correct if this log belongs to the current _gen mission

    var fixesPerPlane = new Dictionary<long, int>();
    foreach (var e in events)
    {
        tracker.Apply(e);
        long? id = e switch
        {
            TakeOffEvent t => t.PlaneId, LandingEvent l => l.PlaneId, DamageEvent d => d.TargetId,
            KillEvent k => k.TargetId, PositionEvent p => p.ObjectId, _ => null,
        };
        if (id is long v) fixesPerPlane[v] = fixesPerPlane.GetValueOrDefault(v) + 1;
    }

    Console.WriteLine($"\nAircraft roster ({tracker.Contacts.Count()}):");
    Console.WriteLine($"  {"name",-28} {"type",-20} {"coal",4} {"flt",3} {"status",-10} {"pos fixes",9}  last fix");
    foreach (var c in tracker.Contacts.OrderBy(c => c.Coalition).ThenBy(c => c.TypeName))
    {
        string last = c.LastFix is { } f ? $"{f.Detail} @ {TimeSpan.FromSeconds(f.SourceTime):hh\\:mm\\:ss}" : "-";
        Console.WriteLine($"  {Trunc(c.DisplayName + (c.IsPlayer ? " (YOU)" : ""), 28),-28} {Trunc(c.TypeName, 20),-20} {c.Coalition,4} {c.FormationIndex?.ToString() ?? "",3} {c.Status,-10} {fixesPerPlane.GetValueOrDefault(c.LogId ?? -1),9}  {last}");
    }

    int periodic = events.OfType<PositionEvent>().Count();
    Console.WriteLine($"""

        Evidence:
          AType 17 periodic position records: {periodic}{(periodic == 0 ? "  → this log has NO continuous positions" : "")}
          Positions only appear at discrete events (spawn, take-off, damage, kill, landing, bail-out).
        """);
    return 0;
}

int AcmiCmd(string? path)
{
    if (path is null || !File.Exists(path)) return Fail("No .acmi file found. Enable tacviewrecord (see 'check') and record a track in-mission.");
    var acmi = new AcmiStream();
    byte[] bytes;
    try
    {
        bytes = File.ReadAllBytes(path);
    }
    catch (IOException) when (ShadowCopy.IsElevated())
    {
        Console.WriteLine("ACMI is locked by IL-2 (track recording); reading it through a shadow-copy snapshot...");
        bytes = ShadowCopy.ReadFrom(path, 0).Bytes;
    }
    catch (IOException)
    {
        return Fail("The ACMI is locked by IL-2 while a track is recording. Stop recording (LCtrl+R), or run as administrator to read it by snapshot.");
    }

    string text;
    using (var stream = new MemoryStream(bytes))
    {
        Stream content = stream;
        ZipArchive? zip = null;
        if (stream.ReadByte() == 'P') // zip local-file signature "PK"
        {
            stream.Position = 0;
            zip = new ZipArchive(stream, ZipArchiveMode.Read);
            content = zip.Entries[0].Open();
        }
        else
        {
            stream.Position = 0;
        }

        using var reader = new StreamReader(content);
        text = reader.ReadToEnd() + "\n";
        zip?.Dispose();
    }

    var replayVoices = install is null ? null : VoiceSelection.Create("auto", install.Root);
    var tracker = new ContactTracker();
    // IL-2 saves the mission beside each track, in a folder named after it ("<track>\_gen.mission"). That is this
    // recording's own plan; data\Missions\_gen.Mission is whatever was generated last.
    string trackMission = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path), "_gen.mission");
    if (File.Exists(trackMission))
    {
        var plan = MissionPlan.Load(trackMission);
        if (install is not null)
            plan = plan with { CallsignNames = CallsignTable.Load(install.DataDir) };
        tracker.SetPlan(plan);
        Console.WriteLine($"Mission plan: {trackMission} (saved with the track)");
    }
    else if (install is not null && File.Exists(install.GeneratedMission))
        tracker.SetPlan(MissionPlan.Load(install.GeneratedMission));
    List<string> chunks = install is null ? [] : LatestLogChunks(install.DataDir);
    if (chunks.Count > 0 && !SameMission(path, chunks[0]))
    {
        Console.WriteLine("\n(The mission-log chunks on disk belong to a different mission; replaying from the ACMI alone, so flight membership is unknown.)");
        chunks = [];
    }
    var logEvents = new List<MissionLogEvent>();
    foreach (string chunk in chunks)
        logEvents.AddRange(BinaryMissionLog.DecodeAll(File.ReadAllBytes(chunk)));
    // IL-2 can leave only the text copies of an older mission's log.
    List<string> textChunks = chunks.Count > 0 || install is null ? [] : LatestLogChunks(install.DataDir, ".txt");
    if (textChunks.Count > 0 && SameMission(path, textChunks[0]))
    {
        foreach (string chunk in textChunks)
            foreach (string line in File.ReadLines(chunk))
                if (TextMissionLog.ParseLine(line) is { } e)
                    logEvents.Add(e);
        chunks = textChunks;
    }

    // Identities first, so the track's aircraft join the log's flight members. The rest go in at their
    // game time, alongside the track's frames, so a loss or despawn late in the mission does not
    // silence the flight from the start of the replay.
    bool IsIdentity(MissionLogEvent e) => e is ObjectSpawnedEvent or PlaneSpawnedEvent or MissionStarted;
    foreach (var e in logEvents.Where(IsIdentity).OrderBy(e => e.Tick))
        tracker.Apply(e);
    var pendingLog = new Queue<MissionLogEvent>(logEvents.Where(e => !IsIdentity(e)).OrderBy(e => e.Tick));
    void ApplyLogUpTo(double seconds)
    {
        while (pendingLog.TryPeek(out var e) && e.Tick / MissionLogEvent.TicksPerSecond <= seconds)
            tracker.Apply(pendingLog.Dequeue());
    }

    var realism = ChosenRealism();
    var radio = new RadioSettings { Realism = realism };
    var sightings = new SightingMemory();
    // The calls the flight makes unasked, looked at every 10 s of track time as the live session does.
    var unasked = new UnaskedCalls();
    var unaskedLines = new List<string>();
    double nextLook = 0;
    acmi.FrameClosed = s =>
    {
        ApplyLogUpTo(s.CurrentTime);
        tracker.Apply(s);
        EnemyCommands.Notice(tracker, radio, sightings);
        if (s.CurrentTime < nextLook)
            return;
        nextLook = s.CurrentTime + 10;
        if (unasked.Consider(tracker, radio, sightings) is { } call)
            unaskedLines.Add($"    {s.CurrentTime,5:F0} s  \"{RadioChat.Line(call).Speaker} {call.Text}\"");
    };
    acmi.Feed(text);
    ApplyLogUpTo(double.PositiveInfinity);

    var aircraft = acmi.Objects.Values.Where(o => o.IsAircraft).ToList();
    Console.WriteLine($"ACMI: {path}\n{acmi.LinesParsed} lines, duration {acmi.CurrentTime:F0} s, {acmi.Objects.Count} objects, {aircraft.Count} aircraft. Reference {acmi.ReferenceLongitude}, {acmi.ReferenceLatitude}");
    Console.WriteLine($"  {"id",-8} {"name",-22} {"pilot",-24} {"coalition",-10} {"updates",7} {"seen",12}");
    foreach (var o in aircraft.OrderByDescending(o => o.PositionUpdates))
        Console.WriteLine($"  {o.Id,-8} {Trunc(o.Name, 22),-22} {Trunc(o.Pilot, 24),-24} {Trunc(o.Coalition, 10),-10} {o.PositionUpdates,7} {o.FirstSeenTime,5:F0}-{(o.RemovedTime ?? o.LastPositionTime),5:F0} s");
    Console.WriteLine("\nProperty keys seen: " + string.Join(", ", acmi.Objects.Values.SelectMany(o => o.Properties.Keys).Distinct().Order()));

    tracker.Apply(acmi);

    Console.WriteLine($"\nUnasked calls during the replay ({unaskedLines.Count}):");
    unaskedLines.ForEach(Console.WriteLine);

    Console.WriteLine($"\nRadio checks replayed at end of recording (t={acmi.CurrentTime:F0} s, {chunks.Count} log chunks, author {acmi.Author}, realism {realism}); player: {tracker.Player?.DisplayName ?? "(not identified)"}");
    // How the German game pack would say each reply: the clip keys it plays. The rest is on the chat only.
    var germanPack = replayVoices?.For(201)?.Pack;
    foreach (var command in Enum.GetValues<RadioCommand>())
    {
        var result = RadioCommands.Execute(command, tracker, radio, sightings);
        Console.WriteLine($"\n  {RadioCommands.Title(command)} → {result.Outcome}");
        foreach (var r in result.Replies)
        {
            Console.WriteLine($"    RADIO: \"{r.Text}\"");
            if (germanPack is not null && r.Tokens is { } tokens)
            {
                int speaker = germanPack.SpeakerIndex(r.Contact.FormationIndex ?? 1);
                bool HasClip(string key) => germanPack.ClipPath(key, speaker) is not null;
                var clips = Il2Radio.Core.Audio.ClipPhraser.Phrase(tokens, HasClip);
                Console.WriteLine($"    voice (game clips):   {(clips is null ? "(chat only)" : string.Join(" ", clips))}");
            }
        }
        foreach (var x in result.Suppressed)
            Console.WriteLine($"    silent: {x.Contact.DisplayName} — {x.Reason}");
    }

    return 0;
}

/// <summary>
/// Track files are named "...yyyy-MM-dd_HH-mm-ss_NN.acmi" and log chunks "missionReport(yyyy-MM-dd_HH-mm-ss)[N]";
/// both stamps are taken at mission start, a few seconds apart.
/// </summary>
static bool SameMission(string acmiPath, string chunkPath)
{
    static DateTime? Stamp(string name) =>
        System.Text.RegularExpressions.Regex.Match(name, @"(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})") is { Success: true } m
        && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var t) ? t : null;
    return Stamp(Path.GetFileName(acmiPath)) is { } a && Stamp(Path.GetFileName(chunkPath)) is { } c && Math.Abs((a - c).TotalMinutes) <= 10;
}

static List<string> LatestLogChunks(string dataDir, string extension = ".mlg")
{
    var regex = new System.Text.RegularExpressions.Regex(@"^missionReport\((?<stamp>[^)]+)\)\[(?<index>\d+)\]" + System.Text.RegularExpressions.Regex.Escape(extension) + "$");
    return new DirectoryInfo(dataDir).EnumerateFiles("missionReport*" + extension)
        .Select(f => (File: f, Match: regex.Match(f.Name)))
        .Where(x => x.Match.Success)
        .GroupBy(x => x.Match.Groups["stamp"].Value)
        .MaxBy(g => g.Key)?
        .OrderBy(x => int.Parse(x.Match.Groups["index"].Value))
        .Select(x => x.File.FullName)
        .ToList() ?? [];
}

static int? ConfiguredPort(Il2Install install) =>
    int.TryParse(StartupConfig.Load(install.StartupCfg).Get("telemetrydevice", "port"), out int p) ? p : null;

static string? Newest(string? dir, string pattern) =>
    dir is not null && Directory.Exists(dir)
        ? new DirectoryInfo(dir).EnumerateFiles(pattern, SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
        : null;

static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

bool RealismOk(out int code)
{
    code = 0;
    string? value = Option("--realism");
    if (value is null || value.Equals("pilot", StringComparison.OrdinalIgnoreCase) || value.Equals("precise", StringComparison.OrdinalIgnoreCase))
        return true;
    code = Fail($"--realism \"{value}\": expected pilot or precise");
    return false;
}

RadioRealism ChosenRealism() =>
    string.Equals(Option("--realism"), "precise", StringComparison.OrdinalIgnoreCase) ? RadioRealism.Precise : RadioRealism.Pilot;

bool FlagOn(string name) => !string.Equals(Option(name), "off", StringComparison.OrdinalIgnoreCase);

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string? Positional(int index)
{
    var positional = args.Where((a, i) => !a.StartsWith("--") && (i == 0 || !args[i - 1].StartsWith("--") || args[i - 1] == "--apply")).ToArray();
    return index < positional.Length ? positional[index] : null;
}
