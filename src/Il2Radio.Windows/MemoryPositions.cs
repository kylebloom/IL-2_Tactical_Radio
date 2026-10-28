using Il2Radio.Core.Memory;

namespace Il2Radio.Windows;

/// <summary>
/// Live positions read from IL-2's memory for one mission (docs/MEMORY.md): the aircraft class is learned from
/// the player's own object (by the id the mission log gives), every object of that class is found by one scan,
/// and a rescan every <see cref="RescanEvery"/> picks up aircraft that spawn later. Scans take seconds and run
/// in the background; <see cref="Read"/> only re-reads known objects (a few milliseconds). Read-only: the game
/// is opened with query and read rights, never written to.
/// </summary>
internal sealed class MemoryPositions(Action<string> note) : IDisposable
{
    /// <summary>After a failed attempt (the aircraft not yet in the scene, IL-2 not running), try again this often.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);

    /// <summary>Spawned aircraft appear at the next rescan (VERIFIED, 2026-10-27: every one within 30 s).</summary>
    public static readonly TimeSpan RescanEvery = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private ProcessMemory? _memory;
    private int? _playerId;
    private ulong _class;
    private Dictionary<ulong, MemoryObject> _objects = [];
    private Task? _scan;
    private DateTime _nextScanUtc = DateTime.MinValue;

    /// <summary>Objects are known and the last read found some: positions are live.</summary>
    public bool Live { get; private set; }

    /// <summary>One line for the monitor and the player's view.</summary>
    public string Status { get; private set; } = "not started";

    /// <summary>
    /// Reads every known aircraft now (one reading per id, the median of its parts), or null while none are
    /// known. Starts or restarts discovery for <paramref name="playerId"/> as needed; null stops using memory
    /// (no mission, or one the radio must not attach to).
    /// </summary>
    public IReadOnlyList<MemoryAircraft>? Read(int? playerId)
    {
        lock (_gate)
        {
            if (playerId != _playerId)
            {
                // A new mission (or none): its objects and even its class differ (docs/MEMORY.md).
                _playerId = playerId;
                _class = 0;
                _objects = [];
                _nextScanUtc = DateTime.MinValue;
                Live = false;
                Status = playerId is null ? "waiting for a mission" : "looking for your aircraft";
            }
            if (_playerId is not { } id)
                return null;

            if (_memory is { } m && SafeExited(m))
            {
                m.Dispose();
                _memory = null;
                _class = 0;
                _objects = [];
                Live = false;
                Status = "IL-2 closed";
            }

            if (DateTime.UtcNow >= _nextScanUtc && _scan is not { IsCompleted: false })
            {
                _nextScanUtc = DateTime.UtcNow + (_class == 0 ? RetryEvery : RescanEvery);
                _scan = Task.Run(() => Scan(id));
            }

            if (_memory is null || _class == 0 || _objects.Count == 0)
            {
                Live = false;
                return null;
            }

            var now = new List<MemoryObject>(_objects.Count);
            foreach (var (address, before) in _objects.ToList())
            {
                if (ObjectRoster.Read(_memory, address, _class) is { } o && o.Id == before.Id)
                    now.Add(o);
                else
                    _objects.Remove(address); // freed: a wreck at ground impact, or a despawn
            }
            Live = now.Count > 0;
            var aircraft = MemoryAircraft.Of(now);
            Status = Live ? $"live from IL-2's memory ({aircraft.Count} aircraft and objects)" : "no aircraft left in memory";
            return Live ? aircraft : null;
        }
    }

    /// <summary>Discovery (no class yet) or a rescan for spawns. Runs off the poll loop: a scan reads ~10 GB in a few seconds.</summary>
    private void Scan(int playerId)
    {
        try
        {
            ProcessMemory? memory;
            ulong cls;
            lock (_gate)
            {
                memory = _memory ??= ProcessMemory.Open(ProcessMemory.Il2ProcessName);
                cls = _class;
            }
            if (memory is null)
            {
                lock (_gate)
                    Status = "IL-2 is not running";
                return;
            }

            if (cls == 0)
            {
                memory.RefreshRegions();
                var started = DateTime.UtcNow;
                if (ObjectRoster.FindPlayer(memory, playerId, out cls) is null)
                {
                    lock (_gate)
                        Status = "your aircraft isn't in IL-2's memory yet (retrying)";
                    return;
                }
                note($"memory: found your aircraft in {(DateTime.UtcNow - started).TotalSeconds:F1} s");
            }
            else
            {
                memory.RefreshRegions(); // spawns allocate new memory
            }

            var found = ObjectRoster.FindAll(memory, cls);
            string? message = null;
            lock (_gate)
            {
                if (_playerId != playerId || !ReferenceEquals(_memory, memory))
                    return; // the mission changed while scanning
                bool first = _class == 0;
                _class = cls;
                int before = _objects.Select(o => o.Value.Id).Distinct().Count();
                foreach (var o in found)
                    _objects.TryAdd(o.Address, o);
                int after = _objects.Select(o => o.Value.Id).Distinct().Count();
                if (first)
                    message = $"memory: {after} aircraft and objects found; positions now come from IL-2's memory";
                else if (after > before)
                    message = $"memory: {after - before} new aircraft or objects found";
            }
            // Outside our lock: the session holds its own lock while it calls Read, so noting under ours could deadlock.
            if (message is not null)
                note(message);
        }
        catch (InvalidOperationException ex)
        {
            lock (_gate)
                Status = ex.Message;
        }
        catch (Exception ex)
        {
            note($"memory: scan failed: {ex.Message}");
        }
    }

    private static bool SafeExited(ProcessMemory memory)
    {
        try { return memory.Process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _memory?.Dispose();
            _memory = null;
        }
    }
}
