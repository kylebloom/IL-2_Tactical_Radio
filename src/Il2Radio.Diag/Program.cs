using System.Diagnostics;
using System.IO.Compression;
using Il2Radio.Core;
using Il2Radio.Core.Acmi;
using Il2Radio.Core.Contacts;
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
        int port = int.TryParse(Option("--port"), out int p) ? p : ConfiguredPort(install) ?? 29373;
        TimeSpan? duration = int.TryParse(Option("--seconds"), out int secs) ? TimeSpan.FromSeconds(secs) : null;
        VoiceSelection? voices = args.Contains("--no-audio") ? null : Voices();
        using (voices?.Synth as IDisposable)
        using (var voice = voices is null ? null : new RadioVoice(Volume(), voices))
        using (var meter = voice is null || Option("--radio-wait") == "off" ? null : new Il2AudioMeter(RadioWaitRatio()))
        using (var chat = ChatSinks.Start(ChatOptions.Parse(args).WithGameScreen(install).WithIl2Areas(install)))
        {
            var session = new LiveSession(install, new LiveOptions
            {
                UdpPort = port,
                Duration = duration,
                Realism = ChosenRealism(),
                UnaskedRoute = FlagOn("--unasked-route"),
                UnaskedContacts = FlagOn("--unasked-contacts"),
            }, voice);
            session.Attach(meter, chat);
            return await session.RunAsync();
        }
    case "chat-test":
        return ChatSinks.Demo(ChatOptions.Parse(args).WithGameScreen(install).WithIl2Areas(install), int.TryParse(Option("--seconds"), out int demoSecs) ? demoSecs : 60, Option("--png"));
    case "audio-meter":
        return AudioMeter();
    case "say":
        return Say(Positional(1));
    case "voice-check":
        return VoiceCheck();
    case "piper-setup":
        return await PiperSetupCmd();
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
                                  [--realism pilot|precise] [--unasked-route on|off] [--unasked-contacts on|off]
                                  Run during a mission: telemetry + log tail + ACMI tail + contact table + radio
                                  --radio-wait (default on): hold replies while IL-2's own audio peaks R x above its floor (1.8)
                                  Chat text: --chat overlay|window|rtss|off  --chat-screen N  --chat-pos L,T,W,H  --chat-font PX
                                             --rtss-pos X,Y  --rtss-columns N   (rtss: RivaTuner OSD, works in exclusive fullscreen)
                                             --keep-clear on|off   (default on: stay out of IL-2's message line and subtitles)
              chat-test [--seconds S] [--png file] [chat options]
                                  Loop a sample radio exchange in the chosen chat output (to check it shows over IL-2);
                                  --png renders the overlay's live, menu and radio-log views to images instead
              audio-meter [--seconds S] [--radio-wait-ratio R] [--process NAME]
                                  Print IL-2's audio peak, floor and busy state, to tune --radio-wait-ratio while AI pilots talk
              say ["text"] [--country C] [--voice V] [--volume V] [--range R] [--wav file]
                                  No text: sample replies in the voice for country C (201 German, 101 Soviet; default 201).
                                  With text: speak it with TTS. R = 0..1 of radio range (more hiss further out).
                                  Separate several transmissions with |; --wav saves instead of playing
              voice-check [--country C | --voice V] [--speaker N] [--play]
                                  List the clips a voice pack resolves to; --play plays each, with its key, to check by ear
              piper-setup [--piper DIR] [--piper-voice NAME]
                                  Install the German phrase voice (Piper): shows what, from where and how big, and
                                  downloads only on Y (Yes / No / Later). DIR defaults to "piper" beside this program

              --piper DIR [--piper-voice NAME]  (say, live) fill the German recordings' gaps (type names,
                                  "nothing sighted") with an installed Piper voice. Never downloads

              --voice auto|tts|<pack>  auto (default): recorded clips matching the speaker's nation, else TTS.
                                  <pack>: a voice.json path, its folder, or part of a pack name (e.g. ger)

              --game <path>       IL-2 install root (otherwise IL2_PATH or common Steam locations)
            """);
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

    // An already installed German phrase voice (Diag never downloads; use piper-setup for that).
    if (Option("--piper") is { } piperDir)
    {
        string voiceName = Option("--piper-voice") ?? Il2Radio.Core.Audio.PiperCatalog.DefaultVoice;
        var piperVoice = Il2Radio.Core.Audio.PiperCatalog.Find(voiceName) ?? throw new InvalidOperationException($"unknown Piper voice {voiceName}");
        var piperInstall = Il2Radio.Core.Audio.PiperInstaller.Find(piperDir, piperVoice)
                           ?? throw new InvalidOperationException($"no complete Piper install with {voiceName} in {piperDir} (run piper-setup --piper {piperDir})");
        voices.Synth = new PiperSynthesizer(piperInstall, 0.9);
    }
    return voices;
}

// Asks before downloading Piper and a German voice into --piper <dir> (default: "piper" beside Diag), exactly as Il2Radio.exe does.
async Task<int> PiperSetupCmd()
{
    var settings = new Il2Radio.Core.Settings.PiperSettings
    {
        Enabled = true,
        Folder = Option("--piper"),
        Voice = Option("--piper-voice") ?? Il2Radio.Core.Audio.PiperCatalog.DefaultVoice,
    };
    if (settings.Validate().FirstOrDefault() is { } problem)
        return Fail(problem);
    var (result, synth) = await PiperConsole.StartAsync(settings, install?.Root);
    Console.WriteLine($"{result.Status}: {result.Message}");
    if (synth is null)
        return result.Status == Il2Radio.Core.Audio.PiperSetupStatus.Ready ? 0 : 1;
    using (synth)
    {
        var audio = synth.Synthesize("vermutlich Jaks");
        Console.WriteLine(audio is null ? $"Test phrase failed: {synth.LastError}" : $"Test phrase: {audio.Seconds:F2} s at {audio.SampleRate} Hz");
        return audio is null ? 1 : 0;
    }
}

int Country() => int.TryParse(Option("--country"), out int c) ? c : 201;

int Say(string? text)
{
    double range = double.TryParse(Option("--range"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double r) ? r : 0.3;
    VoiceSelection voices;
    try { voices = Voices(); } catch (InvalidOperationException ex) { return Fail(ex.Message); }
    using var phraseVoice = voices.Synth as IDisposable;

    // Free text has no tokens, so only TTS can say it; the samples carry tokens for clip voices too.
    var transmissions = text is null
        ? SampleTransmissions(range, Country())
        : text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select((line, i) => new Transmission(line, i + 1, range)).ToList();
    Console.WriteLine($"Voice: {voices.Describe()}");

    if (Option("--wav") is { } wavPath)
    {
        foreach (string spoken in RadioVoice.RenderToFile(transmissions, wavPath, Volume(), voices))
            Console.WriteLine($"  {spoken}");
        Console.WriteLine($"Wrote {wavPath}");
        return 0;
    }

    using var voice = new RadioVoice(Volume(), voices);
    voice.WaitUntilReady(TimeSpan.FromSeconds(10));
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
    // Enemy contacts: the type name and "no contacts" have no game recordings (a Piper voice says them, with --piper).
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
    using var phraseVoice = voices.Synth as IDisposable;
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

    // Identities first, then the track, so a glimpse in an earlier frame is remembered before the replay.
    var replayVoices = install is null ? null : VoiceSelection.Create("auto", install.Root);
    var tracker = new ContactTracker { CallsignWords = replayVoices is null ? null : replayVoices.CallsignWord };
    if (install is not null && File.Exists(install.GeneratedMission))
        tracker.SetPlan(MissionPlan.Load(install.GeneratedMission));
    List<string> chunks = install is null ? [] : LatestLogChunks(install.DataDir);
    if (chunks.Count > 0 && !SameMission(path, chunks[0]))
    {
        Console.WriteLine("\n(The mission-log chunks on disk belong to a different mission; replaying from the ACMI alone, so flight membership is unknown.)");
        chunks = [];
    }
    foreach (string chunk in chunks)
        foreach (var e in BinaryMissionLog.DecodeAll(File.ReadAllBytes(chunk)))
            tracker.Apply(e);
    var realism = ChosenRealism();
    var radio = new RadioSettings { Realism = realism };
    var sightings = new SightingMemory();
    acmi.FrameClosed = s =>
    {
        tracker.Apply(s);
        EnemyCommands.Notice(tracker, radio, sightings);
    };
    acmi.Feed(text);

    var aircraft = acmi.Objects.Values.Where(o => o.IsAircraft).ToList();
    Console.WriteLine($"ACMI: {path}\n{acmi.LinesParsed} lines, duration {acmi.CurrentTime:F0} s, {acmi.Objects.Count} objects, {aircraft.Count} aircraft. Reference {acmi.ReferenceLongitude}, {acmi.ReferenceLatitude}");
    Console.WriteLine($"  {"id",-8} {"name",-22} {"pilot",-24} {"coalition",-10} {"updates",7} {"seen",12}");
    foreach (var o in aircraft.OrderByDescending(o => o.PositionUpdates))
        Console.WriteLine($"  {o.Id,-8} {Trunc(o.Name, 22),-22} {Trunc(o.Pilot, 24),-24} {Trunc(o.Coalition, 10),-10} {o.PositionUpdates,7} {o.FirstSeenTime,5:F0}-{(o.RemovedTime ?? o.LastPositionTime),5:F0} s");
    Console.WriteLine("\nProperty keys seen: " + string.Join(", ", acmi.Objects.Values.SelectMany(o => o.Properties.Keys).Distinct().Order()));

    tracker.Apply(acmi);

    Console.WriteLine($"\nRadio checks replayed at end of recording (t={acmi.CurrentTime:F0} s, {chunks.Count} log chunks, author {acmi.Author}, realism {realism}); player: {tracker.Player?.DisplayName ?? "(not identified)"}");
    // How the German game pack would say each reply, with "quoted" parts for the Piper phrase voice (if installed).
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
                var clipsOnly = Il2Radio.Core.Audio.ClipPhraser.PhraseSegments(tokens, HasClip, null);
                var withPiper = Il2Radio.Core.Audio.ClipPhraser.PhraseSegments(tokens, HasClip, Il2Radio.Core.Audio.SynthWords.German);
                Console.WriteLine($"    voice (game clips):   {(clipsOnly is null ? "(TTS: clips can't say it)" : string.Join(" ", clipsOnly))}");
                Console.WriteLine($"    voice (+ Piper, de):  {(withPiper is null ? "(TTS)" : string.Join(" ", withPiper))}");
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

static List<string> LatestLogChunks(string dataDir)
{
    var regex = new System.Text.RegularExpressions.Regex(@"^missionReport\((?<stamp>[^)]+)\)\[(?<index>\d+)\]\.mlg$");
    return new DirectoryInfo(dataDir).EnumerateFiles("missionReport*.mlg")
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
