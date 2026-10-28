using System.Buffers.Binary;

namespace Il2Radio.Core.Memory;

/// <summary>One of IL-2's scene objects as read from memory: an aircraft's airframe, a part of it, a bot, a turret...</summary>
/// <param name="Address">Where the object starts (its class pointer).</param>
/// <param name="Id">The mission-log object id it belongs to (AType:10/12), shared by an aircraft's parts.</param>
/// <param name="North">Mission-log x, metres.</param>
/// <param name="Height">Above sea level, metres.</param>
/// <param name="East">Mission-log z, metres.</param>
/// <param name="Speed">Metres per second.</param>
public sealed record MemoryObject(ulong Address, int Id, double North, double Height, double East, float Speed);

/// <summary>One aircraft at one read: the median of its objects, which are its parts and lie within its own size of each other.</summary>
public sealed record MemoryAircraft(int Id, double North, double Height, double East, double Speed)
{
    /// <summary>
    /// One reading per id. The median rather than the mean, so one odd part cannot pull the aircraft (a single
    /// part once read 360 km/h on the ground while the aircraft was on its take-off run; docs/MEMORY.md).
    /// </summary>
    public static List<MemoryAircraft> Of(IEnumerable<MemoryObject> objects) =>
        objects.GroupBy(o => o.Id)
            .Select(g => new MemoryAircraft(g.Key,
                Median(g.Select(o => o.North)), Median(g.Select(o => o.Height)), Median(g.Select(o => o.East)), Median(g.Select(o => (double)o.Speed))))
            .ToList();

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}

/// <summary>
/// Finds IL-2's scene objects once per mission and re-reads them cheaply (docs/MEMORY.md, "find the nodes once").
/// Only data layouts are assumed, never code addresses: the class pointer is learned from the player's own
/// object, whose id the mission log gives. Layout found on Il-2.exe 1.0.0.1 (2026-10-27).
/// </summary>
public static class ObjectRoster
{
    /// <summary>From an object's start: its id, then a flags field (2 for every aircraft node in the first mission; 1, 257 and others on parked objects), then <see cref="NodeMarker"/>.</summary>
    public const int IdOffset = 0x2E88;
    public const int SpeedOffset = 0x2EB4;
    public const int PositionOffset = 0x2FF8;
    public const int NodeKind = 2;
    public const uint NodeMarker = 0x84222325;

    /// <summary>Bytes read per object: up to the end of the position.</summary>
    private const int ReadLength = PositionOffset + 24;

    /// <summary>
    /// The player's own object: the player's id followed (after the flags) by <see cref="NodeMarker"/>, in an object
    /// that starts with a pointer into a module (its class) and has a plausible position. Null until the aircraft is
    /// in the scene. The class differed between the first two missions tried (parts in the first, one object per
    /// aircraft in the second), which is why it is learned here rather than fixed.
    /// </summary>
    public static MemoryObject? FindPlayer(IProcessMemory memory, int playerId, out ulong classPointer)
    {
        classPointer = 0;
        var pattern = ScanPattern.Integers(ScanType.I32, playerId, playerId, $"id {playerId}");
        var book = new AddressBook(memory);
        foreach (var hit in MemoryScanner.Scan(memory, pattern, new ScanOptions { WritableOnly = true, Alignment = 8 }).Hits)
        {
            if (hit.Address < IdOffset)
                continue;
            ulong start = hit.Address - IdOffset;
            if (Read(memory, start) is not { } found || found.Id != playerId)
                continue;
            ulong cls = ReadPointer(memory, start);
            if (book.RegionOf(cls)?.Module is null)
                continue;
            classPointer = cls;
            return found;
        }
        return null;
    }

    /// <summary>Every object of the player's class, by a scan for the class pointer (a few seconds; once per mission).</summary>
    public static List<MemoryObject> FindAll(IProcessMemory memory, ulong classPointer)
    {
        var pattern = ScanPattern.Integers(ScanType.I64, (long)classPointer, (long)classPointer, "class");
        var objects = new List<MemoryObject>();
        foreach (var hit in MemoryScanner.Scan(memory, pattern, new ScanOptions { WritableOnly = true }).Hits)
            if (Read(memory, hit.Address, classPointer) is { } found)
                objects.Add(found);
        return objects;
    }

    /// <summary>
    /// The object at <paramref name="start"/> now, or null when it no longer is one (freed, reused, or not of
    /// <paramref name="classPointer"/> when given): its id block and marker must be intact and its position plausible.
    /// </summary>
    public static MemoryObject? Read(IProcessMemory memory, ulong start, ulong? classPointer = null)
    {
        var data = new byte[ReadLength];
        if (memory.Read(start, data) != data.Length)
            return null;
        if (classPointer is { } cls && BinaryPrimitives.ReadUInt64LittleEndian(data) != cls)
            return null;
        var span = data.AsSpan();
        int id = BinaryPrimitives.ReadInt32LittleEndian(span[IdOffset..]);
        if (id <= 0 || BinaryPrimitives.ReadUInt32LittleEndian(span[(IdOffset + 8)..]) != NodeMarker)
            return null;
        double north = BinaryPrimitives.ReadDoubleLittleEndian(span[PositionOffset..]);
        double height = BinaryPrimitives.ReadDoubleLittleEndian(span[(PositionOffset + 8)..]);
        double east = BinaryPrimitives.ReadDoubleLittleEndian(span[(PositionOffset + 16)..]);
        float speed = BinaryPrimitives.ReadSingleLittleEndian(span[SpeedOffset..]);
        if (!Plausible(north, height, east) || !float.IsFinite(speed) || speed is < 0 or > 400)
            return null;
        return new MemoryObject(start, id, north, height, east, speed);
    }

    /// <summary>Inside a Great Battles map (they are a few hundred km across) and between the sea bed and 20 km.</summary>
    public static bool Plausible(double north, double height, double east) =>
        north is > 1000 and < 1_000_000 && east is > 1000 and < 1_000_000 && height is > -100 and < 20_000;

    private static ulong ReadPointer(IProcessMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        return memory.Read(address, buffer) == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(buffer) : 0;
    }
}
