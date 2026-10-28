using System.Globalization;
using System.Text;

namespace Il2Radio.Core.Memory;

/// <summary>An address that matched, with the value it held when last read.</summary>
public sealed record ScanHit(ulong Address, byte[] Value);

public sealed class ScanOptions
{
    /// <summary>Null: the pattern's natural alignment.</summary>
    public int? Alignment { get; init; }

    /// <summary>Only regions of this module's image (e.g. "Il-2.exe"), for static addresses.</summary>
    public string? Module { get; init; }

    public bool WritableOnly { get; init; }

    public int MaxHits { get; init; } = 2_000_000;

    public bool Includes(MemoryRegion region) =>
        (!WritableOnly || region.Writable)
        && (Module is null || string.Equals(region.Module, Module, StringComparison.OrdinalIgnoreCase));
}

public enum RefineKind { Changed, Unchanged, Increased, Decreased, Matches }

/// <summary>How a follow-up scan narrows the saved results.</summary>
public sealed record RefineFilter(RefineKind Kind, ScanPattern? Pattern = null)
{
    /// <summary>"changed", "unchanged", "increased", "decreased", or a value / range in the saved scan's type.</summary>
    public static RefineFilter Parse(string text, ScanType type) => text.ToLowerInvariant() switch
    {
        "changed" => new(RefineKind.Changed),
        "unchanged" or "same" => new(RefineKind.Unchanged),
        "increased" or "up" => new(RefineKind.Increased),
        "decreased" or "down" => new(RefineKind.Decreased),
        _ => new(RefineKind.Matches, ScanPattern.Parse(type, text)),
    };
}

public sealed record ScanResult(List<ScanHit> Hits, int RegionsScanned, ulong BytesScanned, bool Truncated);

public static class MemoryScanner
{
    public const int ChunkSize = 4 << 20;

    /// <summary>Searches every included region for <paramref name="pattern"/>.</summary>
    public static ScanResult Scan(IProcessMemory memory, ScanPattern pattern, ScanOptions? options = null)
    {
        options ??= new ScanOptions();
        int alignment = options.Alignment ?? pattern.DefaultAlignment;
        var hits = new List<ScanHit>();
        var found = new List<int>();
        var buffer = new byte[ChunkSize + pattern.Size];
        int regions = 0;
        ulong bytes = 0;

        foreach (var region in memory.Regions)
        {
            if (!options.Includes(region))
                continue;
            regions++;
            for (ulong offset = 0; offset < region.Size; offset += ChunkSize)
            {
                ulong start = region.Base + offset;
                ulong left = region.Size - offset;
                // Read a little past the chunk so a value straddling the boundary is still seen whole.
                int want = (int)Math.Min((ulong)(ChunkSize + pattern.Size - 1), left);
                int read = memory.Read(start, buffer.AsSpan(0, want));
                if (read < pattern.Size)
                    continue;
                bytes += (ulong)read;

                found.Clear();
                pattern.FindAll(buffer.AsSpan(0, read), (int)Math.Min((ulong)ChunkSize, left), alignment, found, options.MaxHits - hits.Count);
                foreach (int at in found)
                    hits.Add(new ScanHit(start + (ulong)at, buffer.AsSpan(at, pattern.Size).ToArray()));
                if (hits.Count >= options.MaxHits)
                    return new ScanResult(hits, regions, bytes, Truncated: true);
            }
        }
        return new ScanResult(hits, regions, bytes, Truncated: false);
    }

    /// <summary>
    /// Re-reads each saved hit and keeps those passing <paramref name="filter"/>, with their new values.
    /// Hits that can no longer be read (memory freed) are dropped.
    /// </summary>
    public static List<ScanHit> Refine(IProcessMemory memory, IReadOnlyList<ScanHit> hits, ScanType type, RefineFilter filter)
    {
        var kept = new List<ScanHit>();
        foreach (var (hit, now) in ReadAgain(memory, hits))
            if (Passes(type, filter, hit.Value, now))
                kept.Add(hit with { Value = now });
        return kept;
    }

    /// <summary>The hits with their current values (unreadable ones dropped), reading nearby hits together.</summary>
    public static IEnumerable<(ScanHit Hit, byte[] Now)> ReadAgain(IProcessMemory memory, IReadOnlyList<ScanHit> hits)
    {
        const int Window = 64 * 1024;
        var ordered = hits.OrderBy(h => h.Address).ToList();
        var buffer = new byte[Window + 64];
        int i = 0;
        while (i < ordered.Count)
        {
            // Group hits that fit in one window and read them with one call.
            ulong start = ordered[i].Address;
            int j = i;
            ulong end = start;
            while (j < ordered.Count && ordered[j].Address + (ulong)ordered[j].Value.Length - start <= (ulong)buffer.Length)
            {
                end = Math.Max(end, ordered[j].Address + (ulong)ordered[j].Value.Length);
                j++;
            }

            int length = (int)(end - start);
            int read = memory.Read(start, buffer.AsSpan(0, length));
            for (int k = i; k < j; k++)
            {
                var hit = ordered[k];
                int at = (int)(hit.Address - start);
                byte[] now;
                if (read == length)
                    now = buffer.AsSpan(at, hit.Value.Length).ToArray();
                else
                {
                    // Part of the window went away: fall back to reading this one alone.
                    now = new byte[hit.Value.Length];
                    if (memory.Read(hit.Address, now) != now.Length)
                        continue;
                }
                yield return (hit, now);
            }
            i = j;
        }
    }

    /// <summary>
    /// For each hit, the values matching <paramref name="pattern"/> within <paramref name="within"/> bytes either side
    /// (offsets relative to the hit, on the pattern's alignment, the hit's own bytes excluded). Only hits with at
    /// least <paramref name="minimum"/> such values are returned: e.g. an object id with its position nearby.
    /// </summary>
    public static List<(ScanHit Hit, List<(long Offset, byte[] Value)> Nearby)> Near(
        IProcessMemory memory, IReadOnlyList<ScanHit> hits, ScanPattern pattern, int within, int minimum = 1, int? alignment = null)
    {
        int align = alignment ?? pattern.DefaultAlignment;
        var book = new AddressBook(memory);
        var kept = new List<(ScanHit, List<(long, byte[])>)>();
        foreach (var hit in hits)
        {
            if (book.RegionOf(hit.Address) is not { } region)
                continue;
            // Stay inside the hit's region, so one unreadable neighbour does not hide the rest.
            ulong start = Math.Max(region.Base, hit.Address - Math.Min((ulong)within, hit.Address));
            ulong end = Math.Min(region.End, hit.Address + (ulong)within + (ulong)pattern.Size);
            start += (ulong)((align - (long)(start % (ulong)align)) % align);
            if (end <= start)
                continue;
            var data = new byte[end - start];
            if (memory.Read(start, data) != data.Length)
                continue;

            var nearby = new List<(long, byte[])>();
            for (int at = 0; at + pattern.Size <= data.Length; at += align)
            {
                ulong address = start + (ulong)at;
                if (address + (ulong)pattern.Size > hit.Address && address < hit.Address + (ulong)hit.Value.Length)
                    continue;
                if (pattern.Matches(data.AsSpan(at, pattern.Size)))
                    nearby.Add(((long)address - (long)hit.Address, data.AsSpan(at, pattern.Size).ToArray()));
            }
            if (nearby.Count >= minimum)
                kept.Add((hit, nearby));
        }
        return kept;
    }

    private static bool Passes(ScanType type, RefineFilter filter, byte[] before, byte[] now)
    {
        switch (filter.Kind)
        {
            case RefineKind.Changed: return !before.AsSpan().SequenceEqual(now);
            case RefineKind.Unchanged: return before.AsSpan().SequenceEqual(now);
            case RefineKind.Matches: return filter.Pattern!.Matches(now);
        }
        if (ScanPattern.Number(type, before) is not { } a || ScanPattern.Number(type, now) is not { } b)
            return false;
        return filter.Kind == RefineKind.Increased ? b > a : b < a;
    }
}

/// <summary>A saved scan, so mem-next can narrow it in a later Diag run. Tied to one run of the game.</summary>
public sealed record ScanState(int ProcessId, DateTime ProcessStartUtc, ScanType Type, string Description, List<ScanHit> Hits)
{
    private const uint Magic = 0x4E414353; // "SCAN"

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new BinaryWriter(File.Create(path), Encoding.UTF8);
        writer.Write(Magic);
        writer.Write(ProcessId);
        writer.Write(ProcessStartUtc.Ticks);
        writer.Write((int)Type);
        writer.Write(Description);
        writer.Write(Hits.Count);
        foreach (var hit in Hits)
        {
            writer.Write(hit.Address);
            writer.Write((ushort)hit.Value.Length);
            writer.Write(hit.Value);
        }
    }

    public static ScanState? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        if (reader.ReadUInt32() != Magic)
            return null;
        int pid = reader.ReadInt32();
        var start = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        var type = (ScanType)reader.ReadInt32();
        string description = reader.ReadString();
        int count = reader.ReadInt32();
        var hits = new List<ScanHit>(count);
        for (int i = 0; i < count; i++)
        {
            ulong address = reader.ReadUInt64();
            hits.Add(new ScanHit(address, reader.ReadBytes(reader.ReadUInt16())));
        }
        return new ScanState(pid, start, type, description, hits);
    }
}

/// <summary>Shows a block of memory every way a field could be stored, eight bytes per row.</summary>
public static class MemoryDump
{
    public static IEnumerable<string> Format(ulong address, ReadOnlySpan<byte> data, AddressBook book)
    {
        var lines = new List<string>();
        var ci = CultureInfo.InvariantCulture;
        for (int at = 0; at + 8 <= data.Length; at += 8)
        {
            var row = data.Slice(at, 8);
            int i0 = BitConverter.ToInt32(row), i1 = BitConverter.ToInt32(row[4..]);
            float f0 = BitConverter.ToSingle(row), f1 = BitConverter.ToSingle(row[4..]);
            double d = BitConverter.ToDouble(row);
            ulong u = BitConverter.ToUInt64(row);

            var text = new StringBuilder();
            foreach (byte b in row)
                text.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');

            string pointer = u != 0 && book.IsMapped(u) ? "→ " + book.Name(u) : "";
            lines.Add(string.Create(ci,
                $"+{at,4:X3}  {address + (ulong)at:X12}  {Convert.ToHexString(row)}  i32 {i0,11} {i1,11}  f32 {Plain(f0),11} {Plain(f1),11}  f64 {Plain(d),14}  {text}  {pointer}"));
        }
        return lines;
    }

    /// <summary>A float as a short number, or "·" when it is clearly not a float (huge, tiny, NaN).</summary>
    private static string Plain(double v) =>
        v == 0 ? "0"
        : double.IsFinite(v) && Math.Abs(v) is >= 1e-4 and < 1e9 ? v.ToString("0.####", CultureInfo.InvariantCulture)
        : "·";
}
