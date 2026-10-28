using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Il2Radio.Core;
using Il2Radio.Core.Acmi;
using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.Memory;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;
using Il2Radio.Core.Settings;
using Il2Radio.Core.Telemetry;

namespace Il2Radio.Windows;

/// <summary>How a <see cref="LiveSession"/> runs: the developer monitor (Diag) or the player's app.</summary>
internal sealed record LiveOptions
{
    /// <summary>UDP telemetry port to listen on and capture; null leaves telemetry alone (nothing but the monitor uses it).</summary>
    public int? UdpPort { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>Where the evidence summary (and the UDP capture) is written at the end.</summary>
    public string OutputDir { get; init; } = Path.Combine(Environment.CurrentDirectory, "diag-output");

    /// <summary>
    /// The player's view: data-source state and the radio's own replies, without telemetry, the
    /// contact table or the reasons contacts stayed silent, which would show aircraft nobody reported.
    /// </summary>
    public bool Compact { get; init; }

    public string Title { get; init; } = "IL-2 radio feasibility monitor";

    public KeyBindings Keys { get; init; } = KeyBindings.Default;

    /// <summary>Remind to start track recording when none runs this long into a mission; zero switches it off.</summary>
    public TimeSpan RecordingReminder { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How much a pilot is allowed to say. The player's program and Diag default to pilot.</summary>
    public RadioRealism Realism { get; init; } = RadioRealism.Pilot;

    /// <summary>Where live positions come from: IL-2's memory, the Tacview track, or memory with the track as fallback.</summary>
    public PositionsMode Positions { get; init; } = PositionsMode.Auto;

    /// <summary>An AI leader calls a cruise heading, or the chat shows a route line when the player leads.</summary>
    public bool UnaskedRoute { get; init; } = true;

    /// <summary>One call when the flight's contact picture changes.</summary>
    public bool UnaskedContacts { get; init; } = true;

    /// <summary>A flight mate who saw it calls a loss the game's own pilots do not.</summary>
    public bool UnaskedLosses { get; init; } = true;

    /// <summary>Any unasked call is on, so the track is read without a numpad press.</summary>
    public bool AnyUnasked => UnaskedRoute || UnaskedContacts || UnaskedLosses;

    /// <summary>The app's start-up check lines, kept in the compact view (otherwise its alerts are listed).</summary>
    public IReadOnlyList<string> StartupResults { get; init; } = [];

    /// <summary>
    /// When set, the player's compact view is delivered here and the console is left alone.
    /// Diag leaves this null and keeps the scrolling monitor.
    /// </summary>
    public Action<CompactStatus>? Status { get; init; }
}

/// <summary>
/// The player's view of a flight. It carries the mission, the track and what the radio itself said,
/// and never names aircraft the radio did not report.
/// </summary>
internal sealed record CompactStatus(
    string Mission,
    string Track,
    string You,
    string? Chat,
    string? Audio,
    string Exchange,
    IReadOnlyList<string> Notices,
    IReadOnlyList<string> Notes);

/// <summary>
/// Watches every data source IL-2 exposes during a mission and records evidence of what each one
/// actually delivers: player telemetry, mission-log growth, ACMI growth and AI position updates.
/// </summary>
internal sealed partial class LiveSession(Il2Install install, LiveOptions options, RadioVoice? voice = null)
{
    /// <summary>What the reminder says, in the console and the chat output.</summary>
    public const string RecordingReminderText = "No track is recording, so your flight can't see where anyone is. Press LCtrl+R to record.";

    /// <summary>Log chunks arrive about every 33 s while a mission runs; none for this long means it ended or is paused.</summary>
    private static readonly TimeSpan MissionQuietAfter = TimeSpan.FromSeconds(90);

    private readonly ContactTracker _tracker = new();
    private readonly SightingMemory _sightings = new(); // what the flight has spotted, kept between calls
    private readonly UnaskedCalls _unasked = new();
    private DateTime _unaskedAttemptUtc = DateTime.MinValue;
    private int _snapshotInFlight;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly object _gate = new();
    private readonly List<(string Text, bool MonitorOnly)> _notes = []; // MonitorOnly: kept out of the compact view (radio calls with their silences, which name unreported aircraft)
    private readonly string _outputDir = options.OutputDir;
    private readonly int? _udpPort = options.UdpPort;
    private readonly TrackRecordingWatch _recordingWatch = new(options.RecordingReminder);
    private readonly MissionClock _missionClock = new();
    private IChatSink? _chat;
    private RadioMenuInput? _menu; // only with a chat output: the menu has nowhere to show otherwise
    private DateTime? _gameOrigin; // in-game date and time at tick 0, from the mission log
    private TimeSpan _latestLogTime = TimeSpan.MinValue;

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

    // Running mission, from its log chunks
    private string? _chunkStamp;
    private DateTime? _missionStartUtc;
    private DateTime? _chunksLastWriteUtc;
    private bool _recording; // a track for this mission is being written (IL-2 holds the ACMI locked)

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

    // IL-2's memory (docs/MEMORY.md); null when positions come from the track only
    private MemoryPositions? _memoryPositions;
    private bool _memoryLive;
    private string? _missionFile; // MFile of the mission log's AType:0: memory is read only for missions generated here
    private StreamWriter? _positionLog; // positions-*.csv beside the evidence: the review's ground truth without a track
    private string? _positionLogPath;
    private long _positionRows;

    /// <summary>The track's positions are not applied: memory supplies them, or the player chose memory only. The two frames never mix.</summary>
    private bool TrackPositionsOff => _memoryLive || options.Positions == PositionsMode.Memory;

    private string _lastCommandOutput = $"Keys: {options.Keys.Legend}";
    private string _lastExchange = $"Keys: {options.Keys.Legend}"; // the compact view's: replies only

    /// <summary>Runs until the quit key, Ctrl+C, the duration or <paramref name="stop"/>.</summary>
    public async Task<int> RunAsync(CancellationToken stop = default)
    {
        Directory.CreateDirectory(_outputDir);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (options.Duration is { } d)
            cts.CancelAfter(d);
        if (options.Status is null)
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

        if (options.Positions != PositionsMode.Track)
            _memoryPositions = new MemoryPositions(text => Note(text));
        using var memoryPositions = _memoryPositions;

        var udp = _udpPort is int port ? Task.Run(() => ListenUdpAsync(port, cts.Token)) : Task.CompletedTask;
        while (!cts.IsCancellationRequested)
        {
            Poll();
            if (options.AnyUnasked)
                _ = PollUnaskedAsync();
            Render();
            for (int i = 0; i < 20 && !cts.IsCancellationRequested; i++)
            {
                var pressed = options.Keys.Pressed().ToList();
                var digits = options.Keys.PressedDigits().ToList(); // polled every tick so a press is never stale
                if (_menu is not null)
                {
                    (pressed, var chosen) = _menu.Handle(pressed, digits, DateTime.UtcNow);
                    if (chosen.Command is { } command)
                        _ = RequestAsync(command);
                    else if (chosen.Action != RadioMenuAction.None)
                        ChatKey?.Invoke(chosen.Action == RadioMenuAction.LogOlder ? RadioKey.Older : RadioKey.Newer);
                }
                foreach (var key in pressed)
                    HandleKey(key, cts);
                await Task.Delay(50);
            }
        }

        try { await udp; } catch (OperationCanceledException) { }
        lock (_gate)
        {
            _positionLog?.Dispose();
            _positionLog = null;
        }
        string summary = WriteSummary();
        if (options.Status is null)
            Console.WriteLine($"\nEvidence summary written to {summary}");
        return 0;
    }

    private void HandleKey(RadioKey key, CancellationTokenSource cts)
    {
        if (key == RadioKey.Quit)
            cts.Cancel();
        else if (key is RadioKey.Older or RadioKey.Newer)
            ChatKey?.Invoke(key);
        else if (key switch
                 {
                     RadioKey.FriendlyPositions => RadioCommand.FriendlyPositions,
                     RadioKey.FlightPosition => RadioCommand.FlightPosition,
                     RadioKey.Rejoin => RadioCommand.Rejoin,
                     RadioKey.EnemyContacts => RadioCommand.EnemyContacts,
                     RadioKey.IdentifyContact => RadioCommand.IdentifyContact,
                     _ => (RadioCommand?)null,
                 } is { } command)
            _ = RequestAsync(command);
    }

    /// <summary>Raised for the chat's own keys: paging the radio log (by key or from the menu).</summary>
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

    /// <summary>Raised with a message for the player, such as the track-recording reminder, e.g. for the chat output.</summary>
    public event Action<string>? Alert;

    private readonly List<string> _missionAlerts = [];
    private DateTime? _alertedMission;

    /// <summary>
    /// Raises <see cref="Alert"/> now and again whenever a mission starts, for problems that last the
    /// whole session (e.g. IL-2 not exporting tracks): text shown before the flight has faded by then.
    /// </summary>
    public void AlertEachMission(string text)
    {
        lock (_gate)
            _missionAlerts.Add(text);
        Note($"warning: {text}", monitorOnly: true); // the compact view lists these on their own
        Alert?.Invoke(text);
    }

    /// <summary>
    /// Connects the outputs: the key click and the voice's hold for IL-2's own radio, and the chat
    /// output (calls, replies in step with the voice, alerts, and the chat's menu and paging keys).
    /// </summary>
    public void Attach(Il2AudioMeter? meter, IChatSink? chat)
    {
        if (voice is not null)
            Keyed += (_, _) => voice.PlayKeyClick();
        if (voice is not null && meter is not null)
        {
            // Don't talk over IL-2's own AI radio: replies wait while its audio peaks above the engine.
            voice.ChannelBusy = () => meter.Busy;
            AudioStatus = () => meter.Status;
        }

        _chat = chat;
        chat?.BindMissionClock(_missionClock);
        if (chat is null)
            return;
        // The call shows the moment the key is pressed; the answers follow once positions are refreshed.
        Keyed += (radio, player) => chat.Post([RadioChat.RequestLine(radio, player)]);
        Transmitted += (_, result, _, problem) => chat.Post(RadioChat.Replies(result, problem), syncToVoice: voice is not null);
        if (voice is not null)
            voice.Speaking += (transmission, _) => chat.Spoken(transmission.Text);
        ChatStatus = () => chat.Status;
        ChatKey += key => ChatSinks.HandleKey(chat, key);
        _menu = new RadioMenuInput(chat, options.Keys);
        Alert += text => chat.Post([new ChatLine(ChatLineKind.System, "", text, -1, TimeSpan.Zero)]);
    }

    private string Describe(RadioCommand command, string? problem = null)
    {
        var settings = Radio();
        var result = RadioCommands.Execute(command, _tracker, settings, _sightings);
        Transmitted?.Invoke(command, result, _tracker.Player, problem);
        voice?.Say(result.Replies.Select(TransmissionOf));
        var sb = new StringBuilder($"[{DateTime.Now:HH:mm:ss}] {RadioCommands.Title(command)} → {result.Outcome}\n");
        foreach (var r in result.Replies)
            sb.AppendLine($"  RADIO: \"{r.Text}\" (fix age {r.FixAgeSeconds:F1} s)");
        if (result.Outcome == ReplyOutcome.PlayerPositionUnknown)
            sb.AppendLine("  (no live player position — no relative report can be made)");
        string replies = sb.ToString();
        foreach (var s in result.Suppressed.Take(8))
            sb.AppendLine($"  silent: {s.Contact.DisplayName} — {s.Reason}");
        Note(sb.ToString().TrimEnd(), monitorOnly: true);
        lock (_gate)
            _lastExchange = problem is null ? replies : replies + $"  ({problem})\n";
        return sb.ToString();
    }

    private async Task ListenUdpAsync(int udpPort, CancellationToken token)
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
        bool remind;
        var alerts = new List<string>();
        RadioReply? unasked = null;
        lock (_gate)
        {
            try { PollMission(); } catch (Exception ex) { Note($"mission: {ex.Message}"); }
            try { PollBinaryLog(); } catch (Exception ex) { Note($"mlg: {ex.Message}"); }
            try { PollTextLogs(); } catch (Exception ex) { Note($"text log: {ex.Message}"); }
            try { PollAcmi(); } catch (Exception ex) { Note($"acmi: {ex.Message}"); }
            try { PollMemory(); } catch (Exception ex) { Note($"memory: {ex.Message}"); }
            try { PollRecording(); } catch (Exception ex) { Note($"recording: {ex.Message}"); }
            if ((_memoryLive || !_acmiLocked) && options.AnyUnasked && Volatile.Read(ref _requestInFlight) == 0)
                unasked = _unasked.Consider(_tracker, Radio(), _sightings, CallSettings());
            var now = DateTime.UtcNow;
            var mission = RunningMissionStartUtc(now);
            remind = _recordingWatch.Update(now, mission, _recording || _memoryLive); // memory needs no recording

            // A mission found in the first seconds was already running at start-up, when the alerts were shown.
            if (mission is { } m && m != _alertedMission)
            {
                _alertedMission = m;
                if (now - _startedUtc > TimeSpan.FromSeconds(30))
                    alerts.AddRange(_missionAlerts);
            }
        }

        if (remind)
        {
            Note($"reminder: {RecordingReminderText}");
            alerts.Add(RecordingReminderText);
        }
        foreach (string alert in alerts)
            Alert?.Invoke(alert);
        if (unasked is not null)
            DeliverUnasked(unasked);
    }

    /// <summary>
    /// While IL-2 holds the track, read the new bytes about every 10 s so a cruise turn or a new
    /// contact can be said without a numpad press. A shadow copy costs about a second, so this stays
    /// off unless an unasked call is switched on. A numpad call already in flight keeps the channel.
    /// </summary>
    private async Task PollUnaskedAsync()
    {
        try
        {
            if (DateTime.UtcNow - _unaskedAttemptUtc < TimeSpan.FromSeconds(10))
                return;
            lock (_gate)
            {
                if (TrackPositionsOff)
                    return; // memory is read every poll; no snapshot needed
            }
            if (Volatile.Read(ref _requestInFlight) == 1 || Volatile.Read(ref _snapshotInFlight) == 1)
                return;

            string? path;
            long offset;
            bool locked;
            long size;
            lock (_gate)
            {
                path = _acmiPath;
                offset = _acmiOffset;
                locked = _acmiLocked;
                size = _acmiSizeOnDisk;
            }

            if (!locked || path is null || size <= offset || !ShadowCopy.IsElevated())
                return;
            if (Interlocked.CompareExchange(ref _snapshotInFlight, 1, 0) != 0)
                return;

            _unaskedAttemptUtc = DateTime.UtcNow;
            try
            {
                if (Volatile.Read(ref _requestInFlight) == 1)
                    return;
                var (bytes, timings) = await Task.Run(() => ShadowCopy.ReadFrom(path, offset));
                RadioReply? reply = null;
                lock (_gate)
                {
                    if (_acmiPath == path && _acmiOffset == offset)
                        FeedAcmi(bytes);
                    if (Volatile.Read(ref _requestInFlight) == 0)
                        reply = _unasked.Consider(_tracker, Radio(), _sightings, CallSettings());
                }

                if (reply is not null)
                    DeliverUnasked(reply);
                Note($"acmi: unasked snapshot {bytes.Length / 1024} KB; {timings}");
            }
            finally
            {
                Interlocked.Exchange(ref _snapshotInFlight, 0);
            }
        }
        catch (Exception ex)
        {
            Note($"acmi: unasked snapshot failed: {ex.Message}");
        }
    }

    private RadioSettings Radio() => new() { Realism = options.Realism };

    private UnaskedCallSettings CallSettings() => new() { Route = options.UnaskedRoute, Contacts = options.UnaskedContacts, Losses = options.UnaskedLosses };

    private void DeliverUnasked(RadioReply reply)
    {
        // The route prompt is the player's own line: chat only, never in his headset. Everything else takes
        // its turn on the voice queue, which shows a line no clip can say when its turn comes.
        bool speak = voice is not null && !reply.Contact.IsPlayer;
        if (speak)
            voice!.Say([TransmissionOf(reply)], interrupt: false);
        _chat?.Post([RadioChat.Line(reply)], syncToVoice: speak);
        lock (_gate)
            _lastExchange = $"[{DateTime.Now:HH:mm:ss}] {reply.Text}\n";
        Note($"unasked: \"{reply.Text}\"", monitorOnly: true);
    }

    private Transmission TransmissionOf(RadioReply reply)
    {
        var settings = Radio();
        return new Transmission(
            reply.Text,
            reply.Contact.FormationIndex ?? Math.Abs(reply.Contact.Key.GetHashCode()),
            reply.Relative.RangeMetres / settings.RadioRangeMetres,
            reply.Tokens,
            reply.Contact.Country > 0 ? reply.Contact.Country : _tracker.Plan?.Player?.Country ?? -1);
    }

    /// <summary>Start of the mission whose log chunks are still arriving, or null (no mission, ended or paused). Caller holds _gate.</summary>
    private DateTime? RunningMissionStartUtc(DateTime nowUtc) =>
        _chunksLastWriteUtc is { } last && nowUtc - last < MissionQuietAfter ? _missionStartUtc : null;

    /// <summary>
    /// Whether this mission's track is recording: IL-2 holds the newest ACMI without read sharing
    /// exactly while it writes it, so a failed shared open means recording. Only an ACMI created
    /// after the mission started counts. Caller holds _gate.
    /// </summary>
    private void PollRecording()
    {
        _recording = false;
        bool thisMission = _acmiPath is not null && File.Exists(_acmiPath)
            && !(_missionStartUtc is { } start && File.GetCreationTimeUtc(_acmiPath) < start.AddMinutes(-2));
        if (thisMission)
        {
            try
            {
                using var fs = new FileStream(_acmiPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException)
            {
                _recording = true; // IL-2 holds it without read sharing exactly while writing it
            }
        }

        // The locked file's size is visible without reading it. Growth means the sim is writing;
        // a few seconds without growth is a pause, and the clock stops with the watch. Once IL-2
        // lets go of the file, a stall is just "not recording", and the clock runs on.
        if (_recording)
            _missionClock.ObserveRecording(_acmiSizeOnDisk, DateTime.UtcNow);
        else
            _missionClock.StopWatching(DateTime.UtcNow);
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
        else
        {
            _chunksLastWriteUtc = null; // the chunks were deleted: the next mission is loading
        }

        if (_usingChunks && chunks.Count > 0)
            TrackMission(chunks);

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
            var previousLogTime = _latestLogTime;
            bool sawStart = false;
            foreach (var e in events)
            {
                if (e is MissionStarted started)
                {
                    sawStart = true;
                    _gameOrigin = started.GameDateTime;
                    _missionFile = started.MissionFile;
                    _latestLogTime = TimeSpan.MinValue;
                }
                if (e.GameTime > _latestLogTime)
                    _latestLogTime = e.GameTime;
                _tracker.Apply(e);
            }
            // The log's GTime is the cockpit clock at tick 0. A chunk's newest tick is up to ~33 s
            // behind the watch; the track sample below replaces it once a frame has been read.
            if (sawStart)
            {
                _chat?.FreezeMissionTime();
                _missionClock.Reset(DateTime.UtcNow);
            }
            if ((sawStart || _latestLogTime > previousLogTime) && _gameOrigin is { } origin)
            {
                var sample = origin.Add(_latestLogTime);
                var now = DateTime.UtcNow;
                if (!_missionClock.Authoritative)
                    _missionClock.Sync(sample, now, authoritative: false);
                else if (_missionClock.At(now) is { } current && sample > current.AddSeconds(2))
                    _missionClock.Sync(sample, now, authoritative: true); // the log is ahead of a stalled track; never pull the watch backwards
            }
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

    /// <summary>
    /// Notes which mission the chunks belong to and when they last grew. Their names carry the
    /// mission's local start time ("missionReport(2026-10-25_19-01-02)[3].mlg"); if that doesn't parse,
    /// the moment the chunks were first seen stands in. Caller holds _gate.
    /// </summary>
    private void TrackMission(List<string> chunks)
    {
        string stamp = ChunkName().Match(Path.GetFileName(chunks[0])).Groups["stamp"].Value;
        if (stamp != _chunkStamp)
        {
            _chunkStamp = stamp;
            _missionStartUtc = DateTime.TryParseExact(stamp, "yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var local)
                ? local.ToUniversalTime()
                : DateTime.UtcNow;
            Note($"mission: started {_missionStartUtc.Value.ToLocalTime():HH:mm:ss}");
        }

        _chunksLastWriteUtc = chunks.Max(File.GetLastWriteTimeUtc);
    }

    private void PollAcmi()
    {
        var newest = NewestSince(install.TracksDir, "*.acmi");
        if (newest is not null && newest != _acmiPath)
        {
            bool segment = _acmiPath is not null;
            _acmiPath = newest;
            _acmiLocked = false;
            _acmiOffset = 0;
            _acmiDecoder = new UTF8Encoding(false).GetDecoder();
            _acmi = new AcmiStream { FrameClosed = NoticeFrame };
            if (segment)
                _missionClock.NewRecording(DateTime.UtcNow);
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
            // IL-2 opens the ACMI without read sharing while recording. It is read through a
            // shadow-copy snapshot: on a radio request, and about every 10 s while an unasked call is on.
            if (!_acmiLocked)
                Note("acmi: locked by IL-2 while recording — positions will be read by snapshot on each radio call.");
            _acmiLocked = true;
            return;
        }

        FeedAcmi(bytes);
    }

    /// <summary>
    /// Reads every aircraft's position from IL-2's memory, when memory is a source (docs/MEMORY.md), and records
    /// what the flight could see, as each track frame does. Only for a mission IL-2 generated on this machine
    /// (Career and quick missions: "_gen" in the log's mission file), never a server's: the memory rule is
    /// single-player only. Caller holds _gate.
    /// </summary>
    private void PollMemory()
    {
        if (_memoryPositions is null)
            return;
        bool local = _missionFile?.Contains("_gen", StringComparison.OrdinalIgnoreCase) == true;
        int? playerId = local && _tracker.Player?.LogId is long id ? (int)id : null;
        var aircraft = _memoryPositions.Read(playerId);
        bool live = aircraft is not null;
        if (aircraft is not null)
        {
            double time = (DateTime.UtcNow - _startedUtc).TotalSeconds;
            _tracker.Apply(aircraft, time);
            EnemyCommands.Notice(_tracker, Radio(), _sightings);
            LogPositions(aircraft, time);
        }
        if (live != _memoryLive)
        {
            Note(live ? "positions: live from IL-2's memory (no track recording needed)"
                : options.Positions == PositionsMode.Memory ? $"positions: IL-2's memory not readable ({_memoryPositions.Status})"
                : $"positions: IL-2's memory not readable ({_memoryPositions.Status}); using the track recording");
            _memoryLive = live;
        }
    }

    /// <summary>
    /// Appends every aircraft of one memory read to positions-*.csv (<see cref="PositionLog"/>), named or not,
    /// flushed each read so a crash keeps the flight. A write failure stops the log, never the radio. Caller holds _gate.
    /// </summary>
    private void LogPositions(IReadOnlyList<MemoryAircraft> aircraft, double time)
    {
        if (_positionLogPath is not null && _positionLog is null)
            return; // failed earlier
        try
        {
            if (_positionLog is null)
            {
                _positionLogPath = Path.Combine(_outputDir, $"positions-{_startedUtc:yyyyMMdd-HHmmss}.csv");
                _positionLog = new StreamWriter(_positionLogPath, append: true);
                if (_positionLog.BaseStream.Length == 0)
                    _positionLog.WriteLine(PositionLog.Header);
            }
            long utcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var byId = new Dictionary<long, Contact>();
            foreach (var c in _tracker.Contacts)
                if (c.LogId is long id)
                    byId.TryAdd(id, c);
            foreach (var a in aircraft)
                _positionLog.WriteLine(PositionLog.Row(utcMs, time, a, byId.GetValueOrDefault(a.Id)));
            _positionLog.Flush();
            _positionRows += aircraft.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Note($"positions log stopped: {ex.Message}");
            _positionLog?.Dispose();
            _positionLog = null;
        }
    }

    /// <summary>
    /// A track frame has closed. Record what the flight could see there, so a glimpse between radio calls
    /// is still mentioned for the next minute. Caller holds _gate, from inside <see cref="FeedAcmi"/>.
    /// </summary>
    private void NoticeFrame(AcmiStream stream)
    {
        if (TrackPositionsOff)
            return;
        _tracker.Apply(stream);
        EnemyCommands.Notice(_tracker, new RadioSettings { Realism = options.Realism }, _sightings);
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
        if (!TrackPositionsOff)
            _tracker.Apply(_acmi);
        // ReferenceTime is the in-game clock at #0, and CurrentTime is seconds of simulation
        // since then. That is the cockpit watch at the last frame in this read.
        if (MissionClock.TryParseReference(_acmi.ReferenceTime, out var reference))
            _missionClock.Sync(reference.AddSeconds(_acmi.CurrentTime), DateTime.UtcNow, authoritative: true);
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

            // From memory: a fresh read takes milliseconds, so answer at once without touching the track.
            lock (_gate)
            {
                if (_memoryPositions is not null && TrackPositionsOff)
                    PollMemory();
                if (TrackPositionsOff)
                {
                    _lastCommandOutput = Describe(command, _memoryLive ? null : $"positions from IL-2's memory not available: {_memoryPositions?.Status}");
                    return;
                }
            }

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
                // An unasked call may already be copying the tail. Wait for it, then read whatever it did not.
                var waitStart = DateTime.UtcNow;
                while (Volatile.Read(ref _snapshotInFlight) == 1 && DateTime.UtcNow - waitStart < TimeSpan.FromSeconds(4))
                    await Task.Delay(40);

                lock (_gate)
                {
                    if (_acmiPath == path)
                        offset = _acmiOffset;
                    locked = _acmiLocked;
                }

                long length = offset;
                try { length = new FileInfo(path).Length; } catch (IOException) { }

                if (locked && length > offset && !ShadowCopy.IsElevated())
                {
                    refresh = "  (ACMI is locked and snapshots need administrator rights — restart the terminal as administrator)\n";
                    problem = "position snapshot needs administrator rights";
                }
                else if (locked && length > offset)
                {
                    if (Interlocked.CompareExchange(ref _snapshotInFlight, 1, 0) != 0)
                    {
                        refresh = "  snapshot failed: another snapshot is still running\n";
                        problem = "position snapshot failed";
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
                        finally
                        {
                            Interlocked.Exchange(ref _snapshotInFlight, 0);
                        }
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
        if (options.Status is { } report)
        {
            CompactStatus snapshot;
            lock (_gate)
                snapshot = Snapshot(DateTime.UtcNow);
            try
            {
                report(snapshot);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // The window went away between polls.
            }
            return;
        }

        var sb = new StringBuilder();
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            sb.AppendLine($"{options.Title} — {DateTime.Now:HH:mm:ss}   ({options.Keys.Legend})");
            if (ChatStatus?.Invoke() is { } chatStatus)
                sb.AppendLine(chatStatus);
            if (AudioStatus?.Invoke() is { } audioStatus)
                sb.AppendLine(audioStatus);
            sb.AppendLine(new string('─', 110));
            if (options.Compact)
                RenderCompact(sb, now);
            else
                RenderMonitor(sb, now);
        }

        if (!Console.IsOutputRedirected)
            Console.Clear();
        Console.Write(sb.ToString());
    }

    /// <summary>The player's view, for the window. Caller holds _gate.</summary>
    private CompactStatus Snapshot(DateTime now)
    {
        var missionStart = RunningMissionStartUtc(now);
        string mission = missionStart is { } start
            ? $"running since {start.ToLocalTime():HH:mm:ss}"
            : _chunkStamp is null ? "waiting for a mission to start" : "none running (ended, paused or loading)";
        string track = _memoryLive
            ? "positions live from IL-2's memory; no recording needed"
            : options.Positions == PositionsMode.Memory
                ? $"waiting for IL-2's memory: {_memoryPositions?.Status}"
            : _recording
            ? $"recording ({Path.GetFileName(_acmiPath)}, {_acmiSizeOnDisk / 1024} KB): positions are live"
            : missionStart is not null
                ? "NOT recording: press LCtrl+R in the cockpit, or nobody can report positions"
                : "not recording (press LCtrl+R once in the cockpit)";
        var player = _tracker.Player;
        int flight = _tracker.Contacts.Count(c => c.InPlayerFlight && !c.IsPlayer);
        string you = player is null
            ? "not identified yet (the mission log names you once you spawn)"
            : $"{player.DisplayName} in {player.TypeName}{(flight > 0 ? $", {flight} others in your flight" : "")}";
        IReadOnlyList<string> notices = options.StartupResults.Count > 0
            ? options.StartupResults
            : _missionAlerts.Select(alert => $"Warning     {alert}").ToArray();
        var notes = _notes.Where(n => !n.MonitorOnly).TakeLast(8).Select(n => n.Text).ToArray();
        return new CompactStatus(mission, track, you, ChatStatus?.Invoke(), AudioStatus?.Invoke(), _lastExchange.TrimEnd(), notices, notes);
    }

    /// <summary>The player's view. Shows nothing about other aircraft beyond what the radio itself said. Caller holds _gate.</summary>
    private void RenderCompact(StringBuilder sb, DateTime now)
    {
        var status = Snapshot(now);
        sb.AppendLine($"Mission     {status.Mission}");
        sb.AppendLine($"Track       {status.Track}");
        sb.AppendLine($"You         {status.You}");
        if (options.StartupResults.Count > 0)
            sb.AppendLine("At start-up:");
        foreach (string line in status.Notices)
            sb.AppendLine(line);
        sb.AppendLine(new string('─', 110));
        sb.AppendLine(status.Exchange);
        sb.AppendLine(new string('─', 110));
        foreach (string note in status.Notes)
            sb.AppendLine(note);
    }

    /// <summary>The developer monitor: every source, every contact, every reason for silence. Caller holds _gate.</summary>
    private void RenderMonitor(StringBuilder sb, DateTime now)
    {
        sb.AppendLine(_udpPort is int udpPort
            ? $"UDP telemetry :{udpPort}  motion {_motionPackets}  state {_statePackets}  other {_otherPackets}  last {Ago(_lastPacketUtc, now)}  vehicle {_vehicleName ?? "?"}"
            : "UDP telemetry (not listening)");
        if (_lastMotion is { } m)
            sb.AppendLine($"   player heading {m.HeadingDegrees:000}°  pitch {m.Pitch * 57.3:F0}°  roll {m.Roll * 57.3:F0}°   (no world position in telemetry)");
        if (_lastState is { } s)
            sb.AppendLine($"   IAS {(s.IndicatedAirspeedMs * 3.6):F0} km/h  AGL {s.AboveGroundLevelM:F0} m  paused {s.Paused}");
        sb.AppendLine($"Binary log    {(_mlg is null ? "(no mission log yet)" : Path.GetFileName(_mlg.Path))}  events {_mlgEvents}  growth {_mlgGrowthCount}×  last {Ago(_mlgLastGrowthUtc, now)}");
        sb.AppendLine($"Text log      events {_textEvents}  files {_textLogOffsets.Count}");
        sb.AppendLine($"ACMI          {(_acmiPath is null ? "(no new .acmi yet — enable tacviewrecord and record a track)" : Path.GetFileName(_acmiPath))}{(_acmiLocked ? $"  LOCKED by IL-2 ({_acmiSizeOnDisk / 1024} KB; read {_acmiOffset / 1024} KB, refreshed on each call)" : "")}  growth {_acmiGrowthCount}×  last {Ago(_acmiLastGrowthUtc, now)}  t={_acmi?.CurrentTime:F0}s{(TrackPositionsOff ? "  (positions not used: memory)" : "")}");
        sb.AppendLine($"Memory        {(_memoryPositions is null ? "off (positions: track)" : _memoryPositions.Status)}");
        sb.AppendLine(new string('─', 110));

        var player = _tracker.Player;
        sb.AppendLine($"{"contact",-30} {"type",-18} {"coal",4} {"status",-10} {"source/detail",-22} {"age s",7}  relative to player");
        foreach (var c in _tracker.Contacts.OrderByDescending(c => c.IsPlayer).ThenBy(c => c.Coalition).ThenBy(c => c.FormationIndex ?? 99).Take(28))
        {
            string src = c.LastFix is { } f ? $"{f.Source}/{f.Detail}" : "-";
            string age = c.LastFix is { } f2 ? _tracker.AgeSeconds(f2).ToString("F0") : "-";
            string rel = "";
            // Only live tracks give a current relative position; event snapshots are history.
            if (!c.IsPlayer && player?.LastFix is { IsTrack: true } pf && c.LastFix is { IsTrack: true } cf
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
        foreach (var note in _notes.TakeLast(8))
            sb.AppendLine(note.Text);
    }

    private string WriteSummary()
    {
        string path = Path.Combine(_outputDir, $"evidence-{_startedUtc:yyyyMMdd-HHmmss}.md");
        var sb = new StringBuilder();
        lock (_gate)
        {
            sb.AppendLine($"# Live session evidence ({_startedUtc:u} → {DateTime.UtcNow:u})\n");
            sb.AppendLine($"- Program version: {AppVersion.Text}");
            sb.AppendLine(_udpPort is int udpPort
                ? $"- UDP telemetry on port {udpPort}: motion packets {_motionPackets}, state packets {_statePackets}, vehicle `{_vehicleName ?? "?"}`"
                : "- UDP telemetry: not listened to");
            sb.AppendLine($"- Binary mission log: {(_mlg?.Path ?? "none")} — {_mlgEvents} events, file grew during session {_mlgGrowthCount} times (last {(_mlgLastGrowthUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "never")})");
            sb.AppendLine($"- Text mission log: {_textEvents} events across {_textLogOffsets.Count} files");
            sb.AppendLine($"- Positions: {options.Positions}; memory {(_memoryPositions is null ? "off" : _memoryPositions.Status)}");
            sb.AppendLine($"- Memory position log: {(_positionLogPath is null ? "none (memory never live)" : $"{_positionLogPath} — {_positionRows} rows")}");
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
            foreach (var n in _notes)
                sb.AppendLine($"    {n.Text.Replace("\n", "\n    ")}");
        }

        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private void Note(string text, bool monitorOnly = false)
    {
        lock (_gate) _notes.Add(($"[{DateTime.Now:HH:mm:ss}] {text}", monitorOnly));
    }

    private static string Ago(DateTime? t, DateTime now) => t is null ? "never" : $"{(now - t.Value).TotalSeconds:F0}s ago";

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
