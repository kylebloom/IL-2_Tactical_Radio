using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Il2Radio.Core.Memory;

public enum ScanType { I32, I64, F32, F64, Ascii, Utf16 }

/// <summary>
/// What a memory scan looks for: an exact value, a numeric range ("a..b"), or text.
/// A single float value matches to the precision it was written with ("1234" is ±0.5, "1234.5" ±0.05),
/// since positions in memory never equal a typed number exactly.
/// </summary>
public sealed class ScanPattern
{
    private readonly byte[]? _exact;
    private readonly double _min, _max;
    private readonly long _minInt, _maxInt;

    private ScanPattern(ScanType type, int size, byte[]? exact, double min, double max, long minInt, long maxInt, string text)
    {
        Type = type;
        Size = size;
        _exact = exact;
        _min = min;
        _max = max;
        _minInt = minInt;
        _maxInt = maxInt;
        Text = text;
    }

    public ScanType Type { get; }

    /// <summary>Bytes compared at each address.</summary>
    public int Size { get; }

    /// <summary>How the pattern was written, for listings.</summary>
    public string Text { get; }

    /// <summary>Natural alignment: values of this type are almost always stored on it.</summary>
    public int DefaultAlignment => Type switch
    {
        ScanType.I32 or ScanType.F32 => 4,
        ScanType.I64 or ScanType.F64 => 8,
        ScanType.Utf16 => 2,
        _ => 1,
    };

    public static int SizeOf(ScanType type) => type switch
    {
        ScanType.I32 or ScanType.F32 => 4,
        ScanType.I64 or ScanType.F64 => 8,
        _ => throw new ArgumentException("Text has no fixed size", nameof(type)),
    };

    public static ScanType ParseType(string type) => type.ToLowerInvariant() switch
    {
        "i32" or "int" or "int32" => ScanType.I32,
        "i64" or "long" or "int64" => ScanType.I64,
        "f32" or "float" or "single" => ScanType.F32,
        "f64" or "double" => ScanType.F64,
        "ascii" or "text" or "str" => ScanType.Ascii,
        "utf16" or "wide" or "unicode" => ScanType.Utf16,
        _ => throw new FormatException($"Unknown value type \"{type}\": expected i32, i64, f32, f64, ascii or utf16"),
    };

    public static ScanPattern Parse(ScanType type, string value)
    {
        switch (type)
        {
            case ScanType.Ascii:
                return TextPattern(type, Encoding.Latin1.GetBytes(value), value);
            case ScanType.Utf16:
                return TextPattern(type, Encoding.Unicode.GetBytes(value), value);
            case ScanType.I32 or ScanType.I64:
            {
                var (lo, hi) = SplitRange(value);
                long min = ParseInt(lo), max = hi is null ? min : ParseInt(hi);
                if (min > max) (min, max) = (max, min);
                return Integers(type, min, max, value);
            }
            default:
            {
                var (lo, hi) = SplitRange(value);
                double min, max;
                if (hi is null)
                {
                    double v = ParseDouble(lo);
                    int decimals = lo.Contains('.') ? lo.Length - lo.IndexOf('.') - 1 : 0;
                    double tolerance = 0.5 * Math.Pow(10, -decimals);
                    (min, max) = (v - tolerance, v + tolerance);
                }
                else
                {
                    (min, max) = (ParseDouble(lo), ParseDouble(hi));
                    if (min > max) (min, max) = (max, min);
                }
                return new ScanPattern(type, SizeOf(type), null, min, max, 0, 0, value);
            }
        }
    }

    public static ScanPattern Integers(ScanType type, long min, long max, string text)
    {
        int size = SizeOf(type);
        byte[]? exact = null;
        if (min == max)
        {
            exact = new byte[size];
            if (size == 4) BinaryPrimitives.WriteInt32LittleEndian(exact, (int)min);
            else BinaryPrimitives.WriteInt64LittleEndian(exact, min);
        }
        return new ScanPattern(type, size, exact, 0, 0, min, max, text);
    }

    /// <summary>8-byte values that point at <paramref name="target"/> or up to <paramref name="before"/> bytes ahead of it (the start of a structure holding it).</summary>
    public static ScanPattern PointerTo(ulong target, ulong before) =>
        Integers(ScanType.I64, (long)(target - Math.Min(before, target)), (long)target, $"pointer to 0x{target:X} (-0x{before:X})");

    private static ScanPattern TextPattern(ScanType type, byte[] bytes, string text) =>
        bytes.Length == 0 ? throw new FormatException("Empty text") : new ScanPattern(type, bytes.Length, bytes, 0, 0, 0, 0, text);

    public bool Matches(ReadOnlySpan<byte> value)
    {
        if (value.Length < Size)
            return false;
        if (_exact is { } exact)
            return value[..Size].SequenceEqual(exact);
        return Type switch
        {
            ScanType.I32 => BinaryPrimitives.ReadInt32LittleEndian(value) is var i && i >= _minInt && i <= _maxInt,
            ScanType.I64 => BinaryPrimitives.ReadInt64LittleEndian(value) is var l && l >= _minInt && l <= _maxInt,
            ScanType.F32 => BinaryPrimitives.ReadSingleLittleEndian(value) is var f && f >= _min && f <= _max,
            ScanType.F64 => BinaryPrimitives.ReadDoubleLittleEndian(value) is var d && d >= _min && d <= _max,
            _ => false,
        };
    }

    /// <summary>
    /// Adds to <paramref name="found"/> the offsets in <paramref name="data"/>, below <paramref name="startLimit"/>
    /// and on <paramref name="alignment"/>, where the pattern matches.
    /// </summary>
    public void FindAll(ReadOnlySpan<byte> data, int startLimit, int alignment, List<int> found, int max)
    {
        startLimit = Math.Min(startLimit, data.Length - Size + 1);
        if (_exact is { } exact)
        {
            int pos = 0;
            while (pos < startLimit && found.Count < max)
            {
                int i = data[pos..].IndexOf(exact);
                if (i < 0)
                    break;
                int at = pos + i;
                if (at >= startLimit)
                    break;
                if (at % alignment == 0)
                    found.Add(at);
                pos = at + 1;
            }
            return;
        }

        for (int at = 0; at < startLimit && found.Count < max; at += alignment)
            if (Matches(data.Slice(at, Size)))
                found.Add(at);
    }

    /// <summary>The value as a number, for "increased"/"decreased"; null for text.</summary>
    public static double? Number(ScanType type, ReadOnlySpan<byte> value) => type switch
    {
        ScanType.I32 when value.Length >= 4 => BinaryPrimitives.ReadInt32LittleEndian(value),
        ScanType.I64 when value.Length >= 8 => BinaryPrimitives.ReadInt64LittleEndian(value),
        ScanType.F32 when value.Length >= 4 => BinaryPrimitives.ReadSingleLittleEndian(value),
        ScanType.F64 when value.Length >= 8 => BinaryPrimitives.ReadDoubleLittleEndian(value),
        _ => null,
    };

    public static string Format(ScanType type, ReadOnlySpan<byte> value) => type switch
    {
        ScanType.Ascii => "\"" + Encoding.Latin1.GetString(value) + "\"",
        ScanType.Utf16 => "\"" + Encoding.Unicode.GetString(value) + "\"",
        ScanType.I32 or ScanType.I64 => Number(type, value)?.ToString("0", CultureInfo.InvariantCulture) ?? "?",
        _ => Number(type, value)?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?",
    };

    private static (string Lo, string? Hi) SplitRange(string value)
    {
        int dots = value.IndexOf("..", StringComparison.Ordinal);
        return dots < 0 ? (value, null) : (value[..dots], value[(dots + 2)..]);
    }

    private static long ParseInt(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : throw new FormatException($"\"{s}\" is not a number");
}
