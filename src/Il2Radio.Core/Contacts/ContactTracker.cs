using Il2Radio.Core.Acmi;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;

namespace Il2Radio.Core.Contacts;

public enum ContactStatus
{
    Spawned,
    Airborne,
    Landed,
    Destroyed,
    BailedOut,
    Despawned,
}

/// <summary>Where a position came from. Only <see cref="AcmiTrack"/> is a continuous live track.</summary>
public enum PositionSource
{
    /// <summary>Snapshot attached to a mission-log event (spawn, take-off, damage, landing...).</summary>
    LogEvent,

    /// <summary>Continuous position from a Tacview ACMI stream written by IL-2.</summary>
    AcmiTrack,
}

/// <summary>A position observation. <see cref="SourceTime"/> is in the producing source's own clock.</summary>
/// <param name="Heading">True heading in degrees (0 = north, clockwise) when the source reports it.</param>
/// <param name="Latitude">Absolute degrees from an ACMI fix. Mission-log fixes leave this empty; the two frames are not the same grid.</param>
/// <param name="Longitude">Absolute degrees, paired with <see cref="Latitude"/>.</param>
public sealed record PositionFix(WorldPoint Position, PositionSource Source, double SourceTime, string Detail, double? Heading = null, double? Latitude = null, double? Longitude = null);

public sealed class Contact(string key)
{
    public string Key { get; } = key;
    public long? LogId { get; internal set; }
    public string? AcmiId { get; internal set; }
    public string TypeName { get; internal set; } = "";
    public string PilotName { get; internal set; } = "";
    public int Country { get; internal set; } = -1;
    public int Coalition { get; internal set; } = -1;
    public bool IsPlayer { get; internal set; }
    public bool InPlayerFlight { get; internal set; }
    public int? FormationIndex { get; internal set; }
    public ContactStatus Status { get; internal set; } = ContactStatus.Spawned;
    public bool Damaged { get; internal set; }

    /// <summary>The despawn came from the Tacview track (object removed), not the mission log, so a reappearance can undo it.</summary>
    internal bool DespawnedByTrack { get; set; }
    public PositionFix? LastFix { get; internal set; }

    /// <summary>Callsign number from the mission plan (8 = "Oriole"); 0 when the plan has none for this pilot.</summary>
    public int Callsign { get; internal set; }

    /// <summary>
    /// Callsign word as his voice says it ("Lumme" for a German pilot's 9), else in English from the install's
    /// callsign table ("Canary"); null when neither knows it.
    /// </summary>
    public string? CallsignName { get; internal set; }

    /// <summary>Number within the callsign ("Oriole <b>two</b>") from the mission plan.</summary>
    public int? Callnum { get; internal set; }

    public bool IsOperational => Status is ContactStatus.Spawned or ContactStatus.Airborne or ContactStatus.Landed;

    public string DisplayName =>
        FormationIndex is int n && InPlayerFlight ? $"Flight #{n} {PilotName}".Trim()
        : PilotName.Length > 0 ? PilotName
        : TypeName;
}

/// <summary>
/// Combines mission-log events, ACMI tracks and the mission plan into a list of aircraft contacts.
/// It never invents positions: a contact's position is only ever the last real observation, and
/// every observation keeps its source and age.
/// </summary>
public sealed class ContactTracker(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, Contact> _contacts = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _logIdToKey = new();
    private readonly Dictionary<long, long> _botToPlane = new();
    private readonly HashSet<long> _pilotBots = [];
    private readonly Dictionary<long, ObjectSpawnedEvent> _unconfirmedObjects = new();
    private readonly Dictionary<PositionSource, (double Time, DateTimeOffset ReceivedAt)> _clocks = new();

    public MissionPlan? Plan { get; private set; }
    public IEnumerable<Contact> Contacts => _contacts.Values;
    public Contact? Player => _contacts.Values.FirstOrDefault(c => c.IsPlayer);
    public int MissionStarts { get; private set; }

    public void SetPlan(MissionPlan plan)
    {
        Plan = plan;
        foreach (var c in _contacts.Values)
            ResolveCallsign(c);
    }

    /// <summary>
    /// The callsign word a speaker's voice says, by (IL-2 country, callsign number), or null to keep the
    /// install's English word. The game's German recordings don't say the English table's words (callsign 9
    /// is "Lumme", not "Canary"), so the on-screen text takes the voice's word and both name the same callsign.
    /// </summary>
    public Func<int, int, string?>? CallsignWords
    {
        get => _callsignWords;
        set
        {
            _callsignWords = value;
            foreach (var c in _contacts.Values)
                ResolveCallsign(c);
        }
    }

    private Func<int, int, string?>? _callsignWords;

    /// <summary>
    /// Age of a fix in seconds: how far the source clock has advanced since the fix, plus how long
    /// ago (wall clock) the source last produced anything. A source that stops updating ages its fixes.
    /// </summary>
    public double AgeSeconds(PositionFix fix)
    {
        if (!_clocks.TryGetValue(fix.Source, out var clock))
            return double.PositiveInfinity;
        double sinceUpdate = (_time.GetUtcNow() - clock.ReceivedAt).TotalSeconds;
        return Math.Max(0, clock.Time - fix.SourceTime) + Math.Max(0, sinceUpdate);
    }

    public void Apply(MissionLogEvent e)
    {
        double t = e.GameTime.TotalSeconds;
        AdvanceClock(PositionSource.LogEvent, t);

        switch (e)
        {
            case MissionStarted:
                Reset();
                MissionStarts++;
                break;

            case ObjectSpawnedEvent spawn when spawn.TypeName.StartsWith("BotPilot", StringComparison.Ordinal):
                _botToPlane[spawn.ObjectId] = spawn.ParentId;
                _pilotBots.Add(spawn.ObjectId);
                if (_unconfirmedObjects.Remove(spawn.ParentId, out var plane))
                    ConfirmAircraft(plane);
                break;

            case ObjectSpawnedEvent spawn when spawn.TypeName.StartsWith("Bot", StringComparison.Ordinal):
                _botToPlane[spawn.ObjectId] = spawn.ParentId; // gunners etc.
                break;

            case ObjectSpawnedEvent spawn:
                // Only treated as an aircraft once a BotPilot child spawns (vehicles, bridges, etc. have none).
                _unconfirmedObjects[spawn.ObjectId] = spawn;
                break;

            case PlaneSpawnedEvent ps:
            {
                var c = GetOrCreateByLogId(ps.PlaneId);
                c.TypeName = ps.TypeName;
                SetCountry(c, ps.Country);
                c.InPlayerFlight = true;
                c.FormationIndex = ps.FormationIndex;
                c.IsPlayer |= ps.IsPlayer;
                _botToPlane[ps.BotId] = ps.PlaneId;
                c.LastFix = new PositionFix(ps.Position, PositionSource.LogEvent, t, "spawn");
                break;
            }

            case TakeOffEvent to when FindOrConfirm(to.PlaneId) is { } c:
                c.Status = ContactStatus.Airborne;
                c.LastFix = new PositionFix(to.Position, PositionSource.LogEvent, t, "take-off");
                break;

            case LandingEvent land when FindOrConfirm(land.PlaneId) is { } c:
                if (c.IsOperational)
                    c.Status = ContactStatus.Landed;
                c.LastFix = new PositionFix(land.Position, PositionSource.LogEvent, t, "landing");
                break;

            case DamageEvent dmg when FindPlaneOrCrew(dmg.TargetId) is { } c:
                c.Damaged = true;
                c.LastFix = new PositionFix(dmg.Position, PositionSource.LogEvent, t, "damaged");
                break;

            case KillEvent kill when (Find(kill.TargetId) ?? (_pilotBots.Contains(kill.TargetId) ? FindPlaneOrCrew(kill.TargetId) : null)) is { } c:
                c.Status = ContactStatus.Destroyed; // aircraft destroyed or its pilot killed: no longer answers
                c.LastFix = new PositionFix(kill.Position, PositionSource.LogEvent, t, "destroyed");
                break;

            case KillEvent kill when FindPlaneOrCrew(kill.TargetId) is { } c:
                c.Damaged = true; // a gunner or other crew member was killed
                break;

            case BotEjectedEvent ej when FindPlaneOrCrew(ej.ParentId) is { } c:
                if (c.Status != ContactStatus.Destroyed)
                    c.Status = ContactStatus.BailedOut;
                break;

            case SortieEndedEvent end when Find(end.PlaneId) is { } c:
                if (c.IsOperational)
                    c.Status = ContactStatus.Despawned;
                break;

            case BotDeinitEvent deinit when FindPlaneOrCrew(deinit.BotId) is { } c:
                if (c.IsOperational)
                    c.Status = ContactStatus.Despawned;
                break;

            case PositionEvent pos when Find(pos.ObjectId) is { } c:
                c.LastFix = new PositionFix(pos.Position, PositionSource.LogEvent, t, "position report");
                break;
        }
    }

    /// <summary>Synchronises aircraft from an ACMI stream (positions are in the ACMI frame).</summary>
    public void Apply(AcmiStream acmi)
    {
        AdvanceClock(PositionSource.AcmiTrack, acmi.CurrentTime);
        string? playerName = Plan?.Player?.PilotName;

        foreach (var obj in acmi.Objects.Values.Where(o => o.IsAircraft))
        {
            var c = _contacts.Values.FirstOrDefault(x => x.AcmiId == obj.Id)
                    ?? _contacts.Values.FirstOrDefault(x => x.AcmiId is null && obj.Pilot.Length > 0 && x.PilotName == obj.Pilot)
                    ?? GetOrCreate($"acmi:{obj.Id}");
            c.AcmiId = obj.Id;
            if (c.TypeName.Length == 0) c.TypeName = obj.Model;
            if (c.PilotName.Length == 0) c.PilotName = obj.Pilot;
            if (c.Callsign == 0) ResolveCallsign(c);
            if (c.Coalition < 0) c.Coalition = AcmiCoalition(obj.Coalition);
            if (obj.Removed && c.IsOperational)
            {
                c.Status = ContactStatus.Despawned;
                c.DespawnedByTrack = true;
            }
            else if (!obj.Removed && c.Status == ContactStatus.Despawned && c.DespawnedByTrack)
            {
                // Seen again, e.g. in the next recording segment (_01.acmi) after the previous one closed.
                c.Status = ContactStatus.Airborne;
                c.DespawnedByTrack = false;
            }
            if (acmi.PositionOf(obj) is { } p)
                c.LastFix = new PositionFix(p, PositionSource.AcmiTrack, obj.LastPositionTime, "track", obj.HeadingDegrees,
                    acmi.ReferenceLatitude + obj.Latitude, acmi.ReferenceLongitude + obj.Longitude);

            if ((playerName is not null && obj.Pilot == playerName) || (acmi.Author is { Length: > 0 } author && obj.Pilot == author))
            {
                c.IsPlayer = true;
                MergeIntoLoggedPlayer(c);
            }
        }
    }

    /// <summary>
    /// The player's ACMI object is named after the account nickname, not the career pilot name, so it
    /// cannot be matched by name; fold it into the player contact identified from the mission log.
    /// </summary>
    private void MergeIntoLoggedPlayer(Contact acmiPlayer)
    {
        var logged = _contacts.Values.FirstOrDefault(x => x.IsPlayer && x != acmiPlayer && x.AcmiId is null);
        if (logged is null)
            return;
        logged.AcmiId = acmiPlayer.AcmiId;
        logged.LastFix = acmiPlayer.LastFix;
        _contacts.Remove(acmiPlayer.Key);
    }

    private void Reset()
    {
        _contacts.Clear();
        _logIdToKey.Clear();
        _botToPlane.Clear();
        _pilotBots.Clear();
        _unconfirmedObjects.Clear();
    }

    private void ConfirmAircraft(ObjectSpawnedEvent spawn)
    {
        var c = GetOrCreateByLogId(spawn.ObjectId);
        if (c.TypeName.Length == 0) c.TypeName = spawn.TypeName;
        if (spawn.Name != spawn.TypeName)
            c.PilotName = PilotNameOf(spawn.Name);
        SetCountry(c, spawn.Country);
        if (Plan?.Player is { } p && p.PilotName.Length > 0 && p.PilotName == c.PilotName)
            c.IsPlayer = true;
        if (spawn.Position is { } pos && c.LastFix is null)
            c.LastFix = new PositionFix(pos, PositionSource.LogEvent, spawn.GameTime.TotalSeconds, "spawn");
        ResolveCallsign(c);
    }

    /// <summary>
    /// Takes the pilot's callsign from the mission plan, matched by pilot name and country. The word is the
    /// one his voice says (<see cref="CallsignWords"/>, for the same country the voice is chosen by), else the install's.
    /// </summary>
    private void ResolveCallsign(Contact c)
    {
        if (Plan?.AircraftOf(c.PilotName, c.Country) is not { Callsign: > 0 } planned)
            return;
        c.Callsign = planned.Callsign;
        int country = c.Country > 0 ? c.Country : planned.Country;
        c.CallsignName = CallsignWords?.Invoke(country, planned.Callsign) ?? Plan.CallsignName(planned);
        c.Callnum = planned.Callnum > 0 ? planned.Callnum : null;
    }

    private void SetCountry(Contact c, int country)
    {
        c.Country = country;
        c.Coalition = Plan?.CoalitionOf(country) is int k and >= 0 ? k : country is > 0 and < 900 ? country / 100 : -1;
    }

    // IL-2 coalitions: 1 = Allies, 2 = Axis. The ACMI coalition naming used by IL-2 is unverified.
    private static int AcmiCoalition(string name) => name switch
    {
        "Allies" => 1,
        "Axis" or "Enemies" => 2,
        _ => -1,
    };

    /// <summary>
    /// A new ACMI segment (<c>_01.acmi</c> and on) starts its <c>#t</c> clock again at 0. Anything
    /// further back than this is that restart, not a late frame of the same file.
    /// </summary>
    private const double SegmentRestartSeconds = 1;

    private void AdvanceClock(PositionSource source, double time)
    {
        var now = _time.GetUtcNow();
        if (_clocks.TryGetValue(source, out var existing) && time < existing.Time - SegmentRestartSeconds)
        {
            // Keeping the previous file's clock would make every fix in the new file older than
            // MaxFixAgeSeconds, so a live picture is reported as "no live player position".
            if (source == PositionSource.AcmiTrack)
                DropAcmiFixes();
            _clocks[source] = (time, now);
            return;
        }

        double latest = _clocks.TryGetValue(source, out var clock) ? Math.Max(clock.Time, time) : time;
        _clocks[source] = (latest, now);
    }

    /// <summary>Positions from the previous segment are not where anyone is in the new file.</summary>
    private void DropAcmiFixes()
    {
        foreach (var c in _contacts.Values)
        {
            if (c.LastFix?.Source == PositionSource.AcmiTrack)
                c.LastFix = null;
        }
    }

    /// <summary>Career pilot names in logs look like "Hannes Guth,201033,0"; the suffix is a roster id.</summary>
    internal static string PilotNameOf(string logName)
    {
        var parts = logName.Split(',');
        return parts.Length == 3 && parts[1].All(char.IsDigit) && parts[2].All(char.IsDigit) ? parts[0] : logName;
    }

    private Contact? FindOrConfirm(long logId)
    {
        if (_unconfirmedObjects.Remove(logId, out var spawn))
            ConfirmAircraft(spawn); // only aircraft take off and land
        return Find(logId);
    }

    private Contact? Find(long logId) => _logIdToKey.TryGetValue(logId, out string? key) ? _contacts[key] : null;

    private Contact? FindPlaneOrCrew(long id) =>
        Find(id) ?? (_botToPlane.TryGetValue(id, out long plane) ? Find(plane) : null);

    private Contact GetOrCreateByLogId(long logId)
    {
        if (Find(logId) is { } existing)
            return existing;
        var c = GetOrCreate($"log:{logId}");
        c.LogId = logId;
        _logIdToKey[logId] = c.Key;
        return c;
    }

    private Contact GetOrCreate(string key)
    {
        if (!_contacts.TryGetValue(key, out var c))
            _contacts[key] = c = new Contact(key);
        return c;
    }
}
