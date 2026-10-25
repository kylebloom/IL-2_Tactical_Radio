using Il2Radio.Core.Geometry;

namespace Il2Radio.Core.MissionLog;

/// <summary>
/// One mission-report event ("AType" record). The same event set is written by IL-2 as a binary
/// <c>.mlg</c> file (data\FlightLogs) and, when <c>mission_text_log = 1</c>, as text (data\logs\text).
/// Positions in these events are snapshots taken at the moment of the event, not continuous tracks.
/// </summary>
public abstract record MissionLogEvent(long Tick)
{
    /// <summary>Mission log ticks run at 50 per second of game time.</summary>
    public const double TicksPerSecond = 50.0;

    public TimeSpan GameTime => TimeSpan.FromSeconds(Tick / TicksPerSecond);
}

/// <summary>AType 0.</summary>
public sealed record MissionStarted(long Tick, DateTime? GameDateTime, string MissionFile) : MissionLogEvent(Tick);

/// <summary>AType 1. Projectile hit; no position.</summary>
public sealed record HitEvent(long Tick, string Ammo, long AttackerId, long TargetId) : MissionLogEvent(Tick);

/// <summary>AType 2. Position is the target's position at the time of damage.</summary>
public sealed record DamageEvent(long Tick, float Damage, long AttackerId, long TargetId, WorldPoint Position) : MissionLogEvent(Tick);

/// <summary>AType 3. Position is the target's position when destroyed.</summary>
public sealed record KillEvent(long Tick, long AttackerId, long TargetId, WorldPoint Position) : MissionLogEvent(Tick);

/// <summary>AType 4. A tracked sortie ended (landed and stopped, despawned, mission ended...).</summary>
public sealed record SortieEndedEvent(long Tick, long PlaneId, long BotId, WorldPoint Position) : MissionLogEvent(Tick);

/// <summary>AType 5.</summary>
public sealed record TakeOffEvent(long Tick, long PlaneId, WorldPoint Position) : MissionLogEvent(Tick);

/// <summary>AType 6.</summary>
public sealed record LandingEvent(long Tick, long PlaneId, WorldPoint Position) : MissionLogEvent(Tick);

/// <summary>AType 7.</summary>
public sealed record MissionEndedEvent(long Tick) : MissionLogEvent(Tick);

/// <summary>
/// AType 10. "Player plane spawn". In single-player this is written for every aircraft of the
/// player's flight, with <see cref="IsPlayer"/> set only for the human-controlled one.
/// </summary>
public sealed record PlaneSpawnedEvent(
    long Tick,
    long PlaneId,
    long BotId,
    WorldPoint Position,
    string TypeName,
    int Country,
    int FormationIndex,
    long AirfieldId,
    int InAir,
    long ParentId,
    bool IsPlayer,
    string Login) : MissionLogEvent(Tick);

/// <summary>AType 11. Group composition.</summary>
public sealed record GroupInitEvent(long Tick, long GroupId, IReadOnlyList<long> MemberIds, long LeaderId) : MissionLogEvent(Tick);

/// <summary>AType 12. Any game object spawned: aircraft, bot pilots, vehicles, bridges...</summary>
public sealed record ObjectSpawnedEvent(long Tick, long ObjectId, string TypeName, int Country, string Name, long ParentId, WorldPoint? Position)
    : MissionLogEvent(Tick);

/// <summary>AType 16. A bot (crew member) was removed.</summary>
public sealed record BotDeinitEvent(long Tick, long BotId, WorldPoint Position) : MissionLogEvent(Tick);

/// <summary>AType 17. Periodic position; documented by community parsers, never observed in single-player logs so far.</summary>
public sealed record PositionEvent(long Tick, long ObjectId, WorldPoint Position) : MissionLogEvent(Tick);

/// <summary>AType 18. Crew member bailed out of <see cref="ParentId"/>.</summary>
public sealed record BotEjectedEvent(long Tick, long BotId, long ParentId, WorldPoint Position) : MissionLogEvent(Tick);

/// <summary>Any AType this project does not interpret.</summary>
public sealed record UnhandledEvent(long Tick, int AType) : MissionLogEvent(Tick);
