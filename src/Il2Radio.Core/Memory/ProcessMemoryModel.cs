using System.Globalization;

namespace Il2Radio.Core.Memory;

/// <summary>
/// Read-only view of another process's memory, for reverse-engineering IL-2's structures (Diag mem-*).
/// There is deliberately no write: the project rule allows reading the game's memory, never changing it.
/// </summary>
public interface IProcessMemory
{
    /// <summary>Committed, readable regions in address order.</summary>
    IReadOnlyList<MemoryRegion> Regions { get; }

    IReadOnlyList<MemoryModule> Modules { get; }

    /// <summary>Copies memory at <paramref name="address"/> into <paramref name="buffer"/>; returns the bytes read (0 when unreadable).</summary>
    int Read(ulong address, Span<byte> buffer);
}

/// <param name="Module">The module whose image this region belongs to, or null for heap, stack and mapped data.</param>
public sealed record MemoryRegion(ulong Base, ulong Size, bool Writable, bool Executable, string? Module)
{
    public ulong End => Base + Size;
    public bool Contains(ulong address) => address >= Base && address < End;
}

public sealed record MemoryModule(string Name, ulong Base, ulong Size)
{
    public bool Contains(ulong address) => address >= Base && address < Base + Size;
}

/// <summary>Names addresses as "module+offset" and tells pointers from other numbers.</summary>
public sealed class AddressBook(IProcessMemory memory)
{
    public string Name(ulong address) =>
        memory.Modules.FirstOrDefault(m => m.Contains(address)) is { } m
            ? $"{m.Name}+0x{address - m.Base:X}"
            : $"0x{address:X}";

    /// <summary>True when <paramref name="value"/> points into a readable region, so it is probably a pointer.</summary>
    public bool IsMapped(ulong value) => RegionOf(value) is not null;

    public MemoryRegion? RegionOf(ulong address)
    {
        var regions = memory.Regions;
        int lo = 0, hi = regions.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (address < regions[mid].Base) hi = mid - 1;
            else if (address >= regions[mid].End) lo = mid + 1;
            else return regions[mid];
        }
        return null;
    }
}

/// <summary>
/// Address expressions in Cheat Engine style: numbers are hex ("1A2B" or "0x1A2B"), a module name stands for
/// its base ("Il-2.exe+3C0"), "[x]" reads the 8-byte pointer at x, "rN" (or "@N") is result N of the saved scan,
/// and terms join with + and -. Example: "[[Il-2.exe+1F2A30]+18]+40".
/// </summary>
public static class AddressExpression
{
    public static ulong Evaluate(string text, IProcessMemory memory, Func<int, ulong?>? result = null)
    {
        string s = text.Replace(" ", "");
        int pos = 0;
        ulong value = Expr(s, ref pos, memory, result);
        if (pos != s.Length)
            throw new FormatException($"Unexpected \"{s[pos..]}\" in address \"{text}\"");
        return value;
    }

    private static ulong Expr(string s, ref int pos, IProcessMemory memory, Func<int, ulong?>? result)
    {
        ulong value = Term(s, ref pos, memory, result);
        while (pos < s.Length && s[pos] is '+' or '-')
        {
            bool minus = s[pos++] == '-';
            ulong term = Term(s, ref pos, memory, result);
            value = minus ? value - term : value + term;
        }
        return value;
    }

    private static ulong Term(string s, ref int pos, IProcessMemory memory, Func<int, ulong?>? result)
    {
        if (pos >= s.Length)
            throw new FormatException($"Address \"{s}\" ends too early");

        if (s[pos] == '[')
        {
            pos++;
            ulong at = Expr(s, ref pos, memory, result);
            if (pos >= s.Length || s[pos] != ']')
                throw new FormatException($"Missing ] in address \"{s}\"");
            pos++;
            Span<byte> buffer = stackalloc byte[8];
            if (memory.Read(at, buffer) != 8)
                throw new FormatException($"Cannot read the pointer at 0x{at:X}");
            return BitConverter.ToUInt64(buffer);
        }

        // "rN" (or "@N", which PowerShell would read as splatting) is a saved scan result.
        if (s[pos] == '@' || (s[pos] is 'r' or 'R' && pos + 1 < s.Length && char.IsAsciiDigit(s[pos + 1])))
        {
            int start = ++pos;
            while (pos < s.Length && char.IsAsciiDigit(s[pos])) pos++;
            if (!int.TryParse(s[start..pos], out int index))
                throw new FormatException($"Expected a result number after r in \"{s}\"");
            return result?.Invoke(index) ?? throw new FormatException($"No saved scan result r{index}");
        }

        // Module names can hold '-' and '.', so try them (longest first) before reading a number.
        foreach (var module in memory.Modules.OrderByDescending(m => m.Name.Length))
        {
            if (string.Compare(s, pos, module.Name, 0, module.Name.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                pos += module.Name.Length;
                return module.Base;
            }
        }

        if (string.Compare(s, pos, "0x", 0, 2, StringComparison.OrdinalIgnoreCase) == 0)
            pos += 2;
        int digits = pos;
        while (pos < s.Length && char.IsAsciiHexDigit(s[pos])) pos++;
        if (pos == digits || !ulong.TryParse(s[digits..pos], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong number))
            throw new FormatException($"Expected a hex number or module name at \"{s[digits..]}\"");
        return number;
    }
}
