using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Il2Radio.Core;
using Il2Radio.Core.Acmi;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;
using Il2Radio.Core.Telemetry;

namespace Il2Radio.Diag;

/// <summary>
/// Watches every data source IL-2 exposes during a mission and records evidence of what each one
/// actually delivers: player telemetry, mission-log growth, ACMI growth and AI position updates.
/// </summary>
internal sealed partial class LiveSession(Il2Install install, int udpPort, TimeSpan? duration = null, RadioVoice? voice = null)
{
    private readonly ContactTracker _tracker = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly object _gate = new();
    private readonly List<string> _notes = [];
    private readonly string _outputDir = Path.Combine(Environment.CurrentDirectory, "diag-output");

    // Telemetry
    private long _motionPackets, _statePackets, _otherPackets;
    private MotionPacket? _lastMotion;
    private StatePacket? _lastState;
    private string? _vehicleName;
    private DateTime? _lastPacketUtc;

    // Mission log
    private BinaryMissionLogTail? _mlg;
    private readonly Dictionary<string, BinaryMissionLogTail> _mlgTails = new(StringComparer.OrdinalIgnoreCase);
    private bool _usingChunks;
    private long _mlgEvents;
    private DateTime? _mlgLastGrowthUtc;
    private int _mlgGrowthCount;
    private readonly Dictionary<string, long> _textLogOffsets = new(StringComparer.OrdinalIgnoreCase);
    private long _textEvents;

    // Mission file
    private DateTime _missionStamp;

    // ACMI
    private string? _acmiPath;
    private long _acmiOffset;
    private int _requestInFlight;
    private Decoder? _acmiDecoder;
    private AcmiStream? _acmi;
    private DateTime? _acmiLastGrowthUtc;
    private bool _acmiLocked;
    private long _acmiSizeOnDisk;
    private int _acmiGrowthCount;

    private string _lastCommandOutput = $"Keys: {NumpadKeys.Legend}";

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(_outputDir);
        using var cts = duration is { } d ? new CancellationTokenSource(d) : new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (ShadowCopy.IsElevated())
        {
            int leftovers = ShadowCopy.CleanUpLeftovers();
            Note($"snapshots: running as administrator{(leftovers > 0 ? $"; removed {leftovers} leftover snapshot(s)" : "")}");
        }
        else
        {
            Note("snapshots: NOT running as administrator — a locked ACMI cannot be read during recording");
        }

        var udp = Task.Run(() => ListenUdpAsync(cts.Token));
        while (!cts.IsCancellationRequested)
        {
            Poll();
            Render();
            for (int i = 0; i < 20 && !cts.IsCancellationRequested; i++)
            {
                foreach (var key in NumpadKeys.Pressed())
                    HandleKey(key, cts);
                await Task.Delay(50);
            }
        }

        try { await udp; } catch (OperationCanceledException) { }
        string summary = WriteSummary();
        Console.WriteLine($"\nEvidence summary written to {summary}");
        return 0;
    }

    private void HandleKey(RadioKey key, CancellationTokenSource cts)
    {
        if (key == RadioKey.Quit)
            cts.Cancel();
        else if (key is RadioKey.Menu or RadioKey.Older or RadioKey.Newer)
            ChatKey?.Invoke(key);
        else if (key switch
                 {
                     RadioKey.FriendlyPositions => RadioCommand.FriendlyPositions,
                     RadioKey.FlightPosition => RadioCommand.FlightPosition,
                     RadioKey.Rejoin => RadioCommand.Rejoin,
                     _ => (RadioCommand?)null,
                 } is { } command)
            _ = RequestAsync(command);
    }

    /// <summary>Raised for the chat's own keys: the command menu and paging the radio log.</summary>
    public event Action<RadioKey>? ChatKey;

    /// <summary>Raised as soon as a radio key is accepted, before positions are refreshed: immediate press feedback.</summary>
    public event Action<RadioCommand, Contact?>? Keyed;

    /// <summary>
    /// Raised for every answered radio request (with the player contact, if known, and why positions
    /// could not be refreshed, if they couldn't), e.g. for the chat output.
    /// </summary>
    public event Action<RadioCommand, RadioResult, Contact?, string?>? Transmitted;

    /// <summary>State of the chat output (e.g. "RTSS: not running"), shown in the monitor header.</summary>
    public Func<string?>? ChatStatus { get; set; }

    /// <summary>State of the IL-2 audio meter that holds replies while IL-2 transmits, shown in the monitor header.</summary>
    public Func<string?>? AudioStatus { get; set; }

    private string Describe(RadioCommand command, string? problem = null)
    {
        var settings = new RadioSettings();
        var result = RadioCommands.Execute(command, _tracker, settings);
        Transmitted?.Invoke(command, result, _tracker.Player, problem);
        voice?.Say(result.Replies.Select(r => new Transmission(
            r.Text,
            r.Contact.FormationIndex ?? Math.Abs(r.Contact.Key.GetHashCode()),
            r.Relative.RangeMetres / settings.RadioRangeMetres,
            r.Tokens,
            r.Contact.Country > 0 ? r.Contact.Country : _tracker.Plan?.Player?.Country ?? -1)));
        var sb = new StringBuilder($"[{DateTime.Now:HH:mm:ss}] {RadioCommands.Title(command)} → {result.Outcome}\n");
        foreach (var r in result.Replies)
            sb.AppendLine($"  RADIO: \"{r.Text}\" (fix age {r.FixAgeSeconds:F1} s)");
        if (result.Outcome == ReplyOutcome.PlayerPositionUnknown)
            sb.AppendLine("  (no live player position — no relative report can be made)");
        foreach (var s in result.Suppressed.Take(8))
            sb.AppendLine($"  silent: {s.Contact.DisplayName} — {s.Reason}");
        Note(sb.ToString().TrimEnd());
        return sb.ToString();
    }

    private async Task ListenUdpAsync(CancellationToken token)
    {
        UdpClient client;
        try
        {
            client = new UdpClient(new IPEndPoint(IPAddress.Loopback, udpPort));
        }
        catch (SocketException ex)
        {
            Note($"UDP: cannot bind 127.0.0.1:{udpPort} ({ex.SocketErrorCode}). Another telemetry tool may own the port.");
            return;
        }

        using (client)
        await using (var capture = File.Create(Path.Combine(_outputDir, $"udp-capture-{_startedUtc:yyyyMMdd-HHmmss}.bin")))
        {
            Note($"UDP: listening on 127.0.0.1:{udpPort}");
            while (!token.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(token);
                // capture format: [int64 unix-ms][int32 length][bytes] — replayable in tests
                capture.Write(BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                capture.Write(BitConverter.GetBytes(result.Buffer.Length));
                capture.Write(result.Buffer);

                var packet = TelemetryDecoder.Decode(result.Buffer);
                lock (_gate)
                {
                    _lastPacketUtc = DateTime.UtcNow;
                    switch (packet)
                    {
                        case MotionPacket m: _motionPackets++; _lastMotion = m; break;
                        case StatePacket s:
                            _statePackets++;
                            _lastState = s;
                            foreach (var e in s.Events)
                                if (e.VehicleName is { } name) _vehicleName = name;
                            break;
                        default: _otherPackets++; break;
                    }
                }
            }
        }
    }

    private void Poll()
    {
        lock (_gate)
        {
            try { PollMission(); } catch (Exception ex) { Note($"mission: {ex.Message}"); }
            try { PollBinaryLog(); } catch (Exception ex) { Note($"mlg: {ex.Message}"); }
            try { PollTextLogs(); } catch (Exception ex) { Note($"text log: {ex.Message}"); }
            try { PollAcmi(); } catch (Exception ex) { Note($"acmi: {ex.Message}"); }
        }
    }

    private void PollMission()
    {
        if (!File.Exists(install.GeneratedMission))
            return;
        var stamp = File.GetLastWriteTimeUtc(install.GeneratedMission);
        if (stamp == _missionStamp)
            return;
        _missionStamp = stamp;
        _tracker.SetPlan(MissionPlan.Load(install.GeneratedMission));
        Note($"mission: loaded _gen.Mission ({stamp.ToLocalTime():HH:mm:ss}); player {_tracker.Plan?.Player?.PilotName} in {_tracker.Plan?.Player?.TypeKey}");
    }

    /// <summary>
    /// With mission_text_log = 1, IL-2 writes the mission report in rotating chunks directly in
    /// data\ (missionReport(...)[0].mlg/.txt, [1], ...) while the mission runs; the complete .mlg in
    /// data\FlightLogs appears at the end. Follow the binary chunks; fall back to FlightLogs.
    /// </summary>
    private void PollBinaryLog()
    {
        var chunks = LogChunks(install.DataDir, "*.mlg");
        if (chunks.Count > 0)
        {
            _usingChunks = true;
        }
        else if (!_usingChunks && NewestSince(install.FlightLogsDir, "*.mlg") is { } whole)
        {
            chunks = [whole]; // a whole-mission log; only appears once the mission ends
        }

        foreach (string path in chunks)
        {
            if (!_mlgTails.TryGetValue(path, out var tail))
            {
                _mlgTails[path] = tail = new BinaryMissionLogTail(path);
                _mlg = tail;
                Note($"mlg: reading {Path.GetFileName(path)}");
            }

            var events = tail.ReadNew();
            if (events.Count == 0)
                continue;
            _mlgEvents += events.Count;
            _mlgLastGrowthUtc = DateTime.UtcNow;
            _mlgGrowthCount++;
            foreach (var e in events)
                _tracker.Apply(e);
        }
    }

    private void PollTextLogs()
    {
        // Counted for evidence only: the binary chunks carry the same events.
        foreach (string path in LogChunks(install.DataDir, "*.txt"))
        {
            long offset = _textLogOffsets.GetValueOrDefault(path);
            var info = new FileInfo(path);
            if (info.Length <= offset)
                continue;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Position = offset;
            using var reader = new StreamReader(fs);
            string text = reader.ReadToEnd();
            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0)
                continue;
            _textLogOffsets[path] = offset + Encoding.UTF8.GetByteCount(text.AsSpan(0, lastNewline + 1));
            _textEvents += text[..lastNewline].Split('\n').Count(line => TextMissionLog.ParseLine(line) is not null);
        }
    }

    /// <summary>
    /// All missionReport(date)[N] chunks of the most recent mission, in chunk order, provided that
    /// mission wrote something since this session started (so a session joined mid-mission still
    /// reads chunk [0] with the spawns).
    /// </summary>
    private List<string> LogChunks(string dir, string extensionPattern)
    {
        if (!Directory.Exists(dir))
            return [];

        var latest = new DirectoryInfo(dir).EnumerateFiles("missionReport*" + extensionPattern[1..], SearchOption.TopDirectoryOnly)
            .Select(f => (File: f, Match: ChunkName().Match(f.Name)))
            .Where(x => x.Match.Success)
            .GroupBy(x => x.Match.Groups["stamp"].Value)
            .MaxBy(g => g.Max(x => x.File.LastWriteTimeUtc));

        if (latest is null || latest.Max(x => x.File.LastWriteTimeUtc) < _startedUtc.AddMinutes(-2))
            return [];

        return latest.OrderBy(x => int.Parse(x.Match.Groups["index"].Value)).Select(x => x.File.FullName).ToList();
    }

    [GeneratedRegex(@"^missionReport\((?<stamp>[^)]+)\)\[(?<index>\d+)\]\.")]
    private static partial Regex ChunkName();

    private void PollAcmi()
    {
        var newest = NewestSince(install.TracksDir, "*.acmi");
        if (newest is not null && newest != _acmiPath)
        {
            _acmiPath = newest;
            _acmiLocked = false;
            _acmiOffset = 0;
            _acmiDecoder = new UTF8Encoding(false).GetDecoder();
            _acmi = new AcmiStream();
            Note($"acmi: found {Path.GetFileName(newest)}");
        }

        if (_acmiPath is null)
            return;

        _acmiSizeOnDisk = new FileInfo(_acmiPath).Length;
        if (_acmiSizeOnDisk <= _acmiOffset)
            return;

        byte[] bytes;
        try
        {
            using var fs = new FileStream(_acmiPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Position = _acmiOffset;
            bytes = new byte[fs.Length - _acmiOffset];
            fs.ReadExactly(bytes);
            if (_acmiLocked)
                Note("acmi: file unlocked (recording stopped?) — reading it directly");
            _acmiLocked = false;
        }
        catch (IOException)
        {
            // IL-2 opens the ACMI without read sharing while recording. It is read on demand
            // through a shadow-copy snapshot when a radio request is made.
            if (!_acmiLocked)
                Note("acmi: locked by IL-2 while recording — positions will be read by snapshot when you press Num1.");
            _acmiLocked = true;
            return;
        }

        FeedAcmi(bytes);
    }

    /// <summary>Appends newly read ACMI bytes (from the file or a snapshot) and updates contacts. Caller holds _gate.</summary>
    private void FeedAcmi(byte[] bytes)
    {
        if (_acmi is null || _acmiDecoder is null || bytes.Length == 0)
            return;

        if (_acmiOffset == 0 && bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
        {
            Note("acmi: file is zip-compressed (.zip.acmi); cannot be read incrementally.");
            _acmiPath = null;
            return;
        }

        var chars = new char[_acmiDecoder.GetCharCount(bytes, 0, bytes.Length)];
        _acmiDecoder.GetChars(bytes, 0, bytes.Length, chars, 0);
        _acmi.Feed(new string(chars).TrimStart('﻿'));
        _acmiOffset += bytes.Length;
        _acmiLastGrowthUtc = DateTime.UtcNow;
        _acmiGrowthCount++;
        _tracker.Apply(_acmi);
    }

    /// <summary>Radio request: refresh positions (by snapshot if the ACMI is locked), then answer.</summary>
    private async Task RequestAsync(RadioCommand command)
    {
        if (Interlocked.Exchange(ref _requestInFlight, 1) == 1)
            return;
        try
        {
            Contact? player;
            lock (_gate)
                player = _tracker.Player;
            Keyed?.Invoke(command, player);

            string? path;
            long offset;
            bool locked;
            lock (_gate)
            {
                path = _acmiPath;
                offset = _acmiOffset;
                locked = _acmiLocked;
                _lastCommandOutput = $"[{DateTime.Now:HH:mm:ss}] {RadioCommands.Title(command)} — stand by...";
            }

            string refresh = "";
            string? problem = path is null ? "no Tacview track is recording (LCtrl+R)" : null;
            if (path is not null && locked)
            {
                if (!ShadowCopy.IsElevated())
                {
                    refresh = "  (ACMI is locked and snapshots need administrator rights — restart the terminal as administrator)\n";
                    problem = "position snapshot needs administrator rights";
                }
                else
                {
                    try
                    {
                        var (bytes, timings) = await Task.Run(() => ShadowCopy.ReadFrom(path, offset));
                        lock (_gate)
                        {
                            if (_acmiPath == path && _acmiOffset == offset)
                                FeedAcmi(bytes);
                        }

                        refresh = $"  snapshot: {bytes.Length / 1024} KB new ACMI data; {timings}\n";
                        Note($"acmi: {refresh.Trim()}");
                    }
                    catch (Exception ex)
                    {
                        refresh = $"  snapshot failed: {ex.Message}\n";
                        Note($"acmi: {refresh.Trim()}");
                        problem = "position snapshot failed";
                    }
                }
            }

            lock (_gate)
                _lastCommandOutput = refresh + Describe(command, problem);
        }
        finally
        {
            Interlocked.Exchange(ref _requestInFlight, 0);
        }
    }

    private string? NewestSince(string dir, string pattern) =>
        Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles(pattern, SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc >= _startedUtc.AddMinutes(-2))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
            : null;

    private void Render()
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            sb.AppendLine($"IL-2 radio feasibility monitor — {DateTime.Now:HH:mm:ss}   ({NumpadKeys.Legend})");
            if (ChatStatus?.Invoke() is { } chatStatus)
                sb.AppendLine(chatStatus);
            if (AudioStatus?.Invoke() is { } audioStatus)
                sb.AppendLine(audioStatus);
            sb.AppendLine(new string('─', 110));
            sb.AppendLine($"UDP telemetry :{udpPort}  motion {_motionPackets}  state {_statePackets}  other {_otherPackets}  last {Ago(_lastPacketUtc, now)}  vehicle {_vehicleName ?? "?"}");
            if (_lastMotion is { } m)
                sb.AppendLine($"   player heading {m.HeadingDegrees:000}°  pitch {m.Pitch * 57.3:F0}°  roll {m.Roll * 57.3:F0}°   (no world position in telemetry)");
            if (_lastState is { } s)
                sb.AppendLine($"   IAS {(s.IndicatedAirspeedMs * 3.6):F0} km/h  AGL {s.AboveGroundLevelM:F0} m  paused {s.Paused}");
            sb.AppendLine($"Binary log    {(_mlg is null ? "(no mission log yet)" : Path.GetFileName(_mlg.Path))}  events {_mlgEvents}  growth {_mlgGrowthCount}×  last {Ago(_mlgLastGrowthUtc, now)}");
            sb.AppendLine($"Text log      events {_textEvents}  files {_textLogOffsets.Count}");
            sb.AppendLine($"ACMI          {(_acmiPath is null ? "(no new .acmi yet — enable tacviewrecord and record a track)" : Path.GetFileName(_acmiPath))}{(_acmiLocked ? $"  LOCKED by IL-2 ({_acmiSizeOnDisk / 1024} KB; read {_acmiOffset / 1024} KB, refreshed on Num1)" : "")}  growth {_acmiGrowthCount}×  last {Ago(_acmiLastGrowthUtc, now)}  t={_acmi?.CurrentTime:F0}s");
            sb.AppendLine(new string('─', 110));

            var player = _tracker.Player;
            sb.AppendLine($"{"contact",-30} {"type",-18} {"coal",4} {"status",-10} {"source/detail",-22} {"age s",7}  relative to player");
            foreach (var c in _tracker.Contacts.OrderByDescending(c => c.IsPlayer).ThenBy(c => c.Coalition).ThenBy(c => c.FormationIndex ?? 99).Take(28))
            {
                string src = c.LastFix is { } f ? $"{f.Source}/{f.Detail}" : "-";
                string age = c.LastFix is { } f2 ? _tracker.AgeSeconds(f2).ToString("F0") : "-";
                string rel = "";
                // Only live tracks give a current relative position; event snapshots are history.
                if (!c.IsPlayer && player?.LastFix is { Source: PositionSource.AcmiTrack } pf && c.LastFix is { Source: PositionSource.AcmiTrack } cf
                    && Navigation.Relative(pf.Position, cf.Position) is { } r)
                    rel = $"brg {r.BearingDegrees:000} rng {r.RangeMetres / 1000:F1} km Δalt {r.AltitudeDeltaMetres:+0;-0} m";
                else if (c.LastFix is { } snap)
                    rel = $"(no live position; last seen at {snap.Detail})";
                string name = (c.IsPlayer ? "YOU " : c.InPlayerFlight ? $"#{c.FormationIndex} " : "") + c.DisplayName;
                sb.AppendLine($"{Trunc(name, 30),-30} {Trunc(c.TypeName, 18),-18} {c.Coalition,4} {c.Status,-10} {Trunc(src, 22),-22} {age,7}  {rel}");
            }

            sb.AppendLine(new string('─', 110));
            sb.AppendLine(_lastCommandOutput.TrimEnd());
            sb.AppendLine(new string('─', 110));
            foreach (string note in _notes.TakeLast(8))
                sb.AppendLine(note);
        }

        if (!Console.IsOutputRedirected)
            Console.Clear();
        Console.Write(sb.ToString());
    }

    private string WriteSummary()
    {
        string path = Path.Combine(_outputDir, $"evidence-{_startedUtc:yyyyMMdd-HHmmss}.md");
        var sb = new StringBuilder();
        lock (_gate)
        {
            sb.AppendLine($"# Live session evidence ({_startedUtc:u} → {DateTime.UtcNow:u})\n");
            sb.AppendLine($"- UDP telemetry on port {udpPort}: motion packets {_motionPackets}, state packets {_statePackets}, vehicle `{_vehicleName ?? "?"}`");
            sb.AppendLine($"- Binary mission log: {(_mlg?.Path ?? "none")} — {_mlgEvents} events, file grew during session {_mlgGrowthCount} times (last {(_mlgLastGrowthUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "never")})");
            sb.AppendLine($"- Text mission log: {_textEvents} events across {_textLogOffsets.Count} files");
            sb.AppendLine($"- ACMI: {(_acmiPath ?? "none")} — grew {_acmiGrowthCount} times (last {(_acmiLastGrowthUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "never")}), stream time {_acmi?.CurrentTime:F0} s");
            if (_acmi is not null)
            {
                var aircraft = _acmi.Objects.Values.Where(o => o.IsAircraft).ToList();
                sb.AppendLine($"  - aircraft objects: {aircraft.Count}; with ≥10 position updates: {aircraft.Count(o => o.PositionUpdates >= 10)}");
                foreach (var o in aircraft.OrderByDescending(o => o.PositionUpdates).Take(40))
                    sb.AppendLine($"  - `{o.Id}` {o.Name} / {o.Pilot} / {o.Coalition}: {o.PositionUpdates} updates, {o.FirstSeenTime:F0}–{o.LastPositionTime:F0} s{(o.Removed ? " (removed)" : "")}");
            }

            if (voice?.Held is var (heldCount, heldTotal) && heldCount > 0)
                sb.AppendLine($"- Replies held for IL-2's own radio: {heldCount}, {heldTotal.TotalSeconds:F1} s in total (max {RadioVoice.MaxHold.TotalSeconds:F0} s each)");

            sb.AppendLine("\n## Contacts at end of session\n");
            foreach (var c in _tracker.Contacts)
                sb.AppendLine($"- {(c.IsPlayer ? "**player** " : "")}{c.DisplayName} ({c.TypeName}) coalition {c.Coalition}, {c.Status}, last fix {c.LastFix?.Source}/{c.LastFix?.Detail}");
            sb.AppendLine("\n## Notes\n");
            foreach (string n in _notes)
                sb.AppendLine($"    {n.Replace("\n", "\n    ")}");
        }

        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private void Note(string text)
    {
        lock (_gate) _notes.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
    }

    private static string Ago(DateTime? t, DateTime now) => t is null ? "never" : $"{(now - t.Value).TotalSeconds:F0}s ago";

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
